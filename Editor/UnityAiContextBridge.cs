using UnityEditor;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Text;

// Copyright (c) 2026 Jamius Siam
// Licensed under the MIT License. See LICENSE.md for details.
namespace Editor
{
    public class UnityAiContextBridge : EditorWindow, IHasCustomMenu
    {
        private const int MaxItems = 100;
        private const string ShowLogsEditorPrefsKey = "Artemis.UnityContextBridge.ShowLogs";
        private const string BridgeInfoFolderName = BridgeSource;
        private const string BridgeSource = "unity-ai-context-bridge";
        private const string ServerHost = "127.0.0.1";
        private const int MinDynamicPort = 49152;
        private const int MaxDynamicPort = 65535;
        private const int MaxServerStartAttempts = 20;
        private const double HeartbeatIntervalSeconds = 10.0;
        private static readonly object ItemsLock = new object();
        private static readonly List<ContextItem> Items = new List<ContextItem>();
        private static readonly System.Random PortRandom = new System.Random();
        private static HttpListener _listener;
        private static Thread _serverThread;
        private static volatile bool _running;
        private static bool _showLogs = true;
        private static bool _refreshQueued;
        private static int _serverPort;
        private static string _authToken;
        private static string _bridgeInfoPath;
        private static double _nextHeartbeatTime;
        private Vector2 _scrollPosition;

        [System.Serializable]
        private class ContextItem
        {
            public string name;
            public string type;

            public string hierarchyPath;
            public string scenePath;

            public string assetPath;
            
            public int instanceId;
            public string globalObjectId;
        }

        [MenuItem("Tools/Unity AI Context Bridge")]
        public static void Open()
        {
            GetWindow<UnityAiContextBridge>("AI Context");
        }

        [MenuItem("Tools/Unity AI Context Bridge Settings/Show Logs")]
        private static void ToggleShowLogsMenuItem()
        {
            ToggleShowLogs();
        }

        [MenuItem("Tools/Unity AI Context Bridge Settings/Show Logs", true)]
        private static bool ValidateToggleShowLogsMenuItem()
        {
            _showLogs = EditorPrefs.GetBool(ShowLogsEditorPrefsKey, true);
            Menu.SetChecked("Tools/Unity AI Context Bridge Settings/Show Logs", _showLogs);
            return true;
        }

        private void OnEnable()
        {
            _showLogs = EditorPrefs.GetBool(ShowLogsEditorPrefsKey, true);
            SubscribeEditorEvents();
            StartServer();
            RefreshContextItems();
        }

        private void OnDisable()
        {
            UnsubscribeEditorEvents();
            StopServer();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Unity AI Context Bridge", EditorStyles.boldLabel);

            var dropArea = GUILayoutUtility.GetRect(0, 80, GUILayout.ExpandWidth(true));
            GUI.Box(dropArea, "Drag Unity assets, prefabs, or hierarchy objects here");

            HandleDragAndDrop(dropArea);
            DrawContextItems();
        }

        public void AddItemsToMenu(GenericMenu menu)
        {
            menu.AddItem(new GUIContent("Settings/Show Logs"), _showLogs, ToggleShowLogs);
        }

        private static void ToggleShowLogs()
        {
            _showLogs = !_showLogs;
            EditorPrefs.SetBool(ShowLogsEditorPrefsKey, _showLogs);

            if (_showLogs)
                Debug.Log("Unity AI Context Bridge logs enabled.");
        }

        private void HandleDragAndDrop(Rect dropArea)
        {
            Event evt = Event.current;

            if (!dropArea.Contains(evt.mousePosition))
                return;

            if (evt.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                evt.Use();
            }

            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();

                foreach (var obj in DragAndDrop.objectReferences)
                {
                    AddContextItem(obj);
                    Log($"Dropped: {obj.name}");
                }

                Repaint();
                evt.Use();
            }
        }

        private void DrawContextItems()
        {
            var snapshot = GetItemsSnapshot();
            var hasItems = snapshot.Length > 0;

            EditorGUILayout.Space(8);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Selected Context", EditorStyles.boldLabel);

                using (new EditorGUI.DisabledScope(!hasItems))
                {
                    if (GUILayout.Button("Clear List", GUILayout.Width(90)))
                    {
                        ClearContextItems();
                        Repaint();
                    }
                }
            }

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            var removedItem = DrawContextList("Hierarchy", snapshot, false);
            EditorGUILayout.Space(6);
            removedItem |= DrawContextList("Assets", snapshot, true);
            EditorGUILayout.EndScrollView();

            if (removedItem)
                Repaint();
        }

        private static bool DrawContextList(string title, ContextItem[] snapshot, bool showAssets)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

            var visibleCount = 0;
            var removedItem = false;

            foreach (var item in snapshot)
            {
                var isAsset = !string.IsNullOrEmpty(item.assetPath);

                if (isAsset != showAssets)
                    continue;

                visibleCount++;

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(item.name);

                    if (GUILayout.Button("X", GUILayout.Width(24)))
                    {
                        RemoveContextItem(item);
                        removedItem = true;
                    }
                }

                var detail = isAsset ? item.assetPath : item.hierarchyPath;
                if (!string.IsNullOrEmpty(detail))
                    EditorGUILayout.LabelField(detail, EditorStyles.miniLabel);
            }

            if (visibleCount == 0)
                EditorGUILayout.LabelField("None", EditorStyles.miniLabel);

            return removedItem;
        }

        private static ContextItem[] GetItemsSnapshot()
        {
            lock (ItemsLock)
            {
                return Items.ToArray();
            }
        }

        private static void ClearContextItems()
        {
            lock (ItemsLock)
            {
                Items.Clear();
            }
        }

        private static void RemoveContextItem(ContextItem item)
        {
            lock (ItemsLock)
            {
                Items.RemoveAll(existing => IsSameContextItem(existing, item));
            }
        }

        private static void AddContextItem(UnityEngine.Object obj)
        {
            if (obj == null)
                return;

            var item = CreateContextItem(obj);

            lock (ItemsLock)
            {
                Items.RemoveAll(existing => IsSameContextItem(existing, item));
                Items.Insert(0, item);

                if (Items.Count > MaxItems)
                    Items.RemoveRange(MaxItems, Items.Count - MaxItems);
            }
        }

        private static void SubscribeEditorEvents()
        {
            EditorApplication.hierarchyChanged -= QueueContextItemRefresh;
            EditorApplication.hierarchyChanged += QueueContextItemRefresh;

            EditorApplication.projectChanged -= QueueContextItemRefresh;
            EditorApplication.projectChanged += QueueContextItemRefresh;
        }

        private static void UnsubscribeEditorEvents()
        {
            EditorApplication.hierarchyChanged -= QueueContextItemRefresh;
            EditorApplication.projectChanged -= QueueContextItemRefresh;
        }

        private static void QueueContextItemRefresh()
        {
            if (_refreshQueued)
                return;

            _refreshQueued = true;
            EditorApplication.delayCall += RefreshContextItemsOnDelay;
        }

        private static void RefreshContextItemsOnDelay()
        {
            _refreshQueued = false;

            if (RefreshContextItems())
                RepaintOpenWindows();
        }

        private static void RepaintOpenWindows()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<UnityAiContextBridge>())
                window.Repaint();
        }

        private static bool RefreshContextItems()
        {
            var changed = false;

            lock (ItemsLock)
            {
                for (int i = Items.Count - 1; i >= 0; i--)
                {
                    var item = Items[i];
                    var obj = ResolveContextItemObject(item);

                    if (obj == null)
                    {
                        Items.RemoveAt(i);
                        changed = true;
                        Log($"Removed missing context item: {item.name}");
                        continue;
                    }

                    var refreshedItem = CreateContextItem(obj);
                    if (!HasContextItemChanged(item, refreshedItem))
                        continue;

                    Items[i] = refreshedItem;
                    changed = true;
                    Log($"Updated context item: {refreshedItem.name}");
                }
            }

            return changed;
        }

        private static UnityEngine.Object ResolveContextItemObject(ContextItem item)
        {
            if (!string.IsNullOrEmpty(item.globalObjectId) &&
                GlobalObjectId.TryParse(item.globalObjectId, out var globalObjectId))
            {
                var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalObjectId);
                if (obj != null)
                    return obj;
            }

            return item.instanceId == 0 ? null : EditorUtility.InstanceIDToObject(item.instanceId);
        }

        private static bool HasContextItemChanged(ContextItem existing, ContextItem refreshed)
        {
            return existing.name != refreshed.name ||
                   existing.type != refreshed.type ||
                   existing.hierarchyPath != refreshed.hierarchyPath ||
                   existing.scenePath != refreshed.scenePath ||
                   existing.assetPath != refreshed.assetPath ||
                   existing.instanceId != refreshed.instanceId ||
                   existing.globalObjectId != refreshed.globalObjectId;
        }

        private static bool IsSameContextItem(ContextItem first, ContextItem second)
        {
            if (!string.IsNullOrEmpty(first.globalObjectId) && !string.IsNullOrEmpty(second.globalObjectId))
                return first.globalObjectId == second.globalObjectId;

            return first.instanceId == second.instanceId;
        }

        private static ContextItem CreateContextItem(UnityEngine.Object obj)
        {
            var gameObject = obj as GameObject;
            var component = obj as Component;

            if (component != null)
                gameObject = component.gameObject;

            var assetPath = AssetDatabase.GetAssetPath(obj);
            var scenePath = string.Empty;
            var hierarchyPath = string.Empty;

            if (gameObject != null && string.IsNullOrEmpty(assetPath))
            {
                scenePath = gameObject.scene.IsValid() ? gameObject.scene.path : string.Empty;
                hierarchyPath = GetHierarchyPath(gameObject.transform);
            }

            return new ContextItem
            {
                name = obj.name,
                type = obj.GetType().FullName,
                globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString(),
                assetPath = assetPath,
                hierarchyPath = hierarchyPath,
                scenePath = scenePath,
                instanceId = obj.GetInstanceID()
            };
        }

        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null)
                return string.Empty;

            var path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }

            return path;
        }

        private static void StartServer()
        {
            if (_running)
                return;

            _authToken = GenerateAuthToken();

            for (int attempt = 0; attempt < MaxServerStartAttempts; attempt++)
            {
                var port = GetRandomPort();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://{ServerHost}:{port}/");

                try
                {
                    listener.Start();

                    _listener = listener;
                    _serverPort = port;
                    _running = true;

                    _serverThread = new Thread(ServerLoop);
                    _serverThread.IsBackground = true;
                    _serverThread.Start();

                    RegisterHeartbeat();
                    WriteBridgeInfoFile();

                    Debug.Log($"Unity AI Context Bridge server started on http://{ServerHost}:{_serverPort}");
                    return;
                }
                catch (HttpListenerException)
                {
                    listener.Close();
                }
                catch (Exception exception)
                {
                    listener.Close();
                    _authToken = null;
                    Debug.LogError($"Failed to start Unity AI Context Bridge server: {exception.Message}");
                    return;
                }
            }

            _authToken = null;
            Debug.LogError($"Failed to start Unity AI Context Bridge server after {MaxServerStartAttempts} port attempts.");
        }

        private static void StopServer()
        {
            _running = false;
            UnregisterHeartbeat();

            try
            {
                _listener?.Stop();
                _listener?.Close();

                Debug.Log("Unity AI Context Bridge server stopped.");
            }
            catch
            {
                Debug.LogWarning("Failed to cleanly stop Unity AI Context Bridge server.");
            }

            _listener = null;
            _serverThread = null;
            _serverPort = 0;
            _authToken = null;
            DeleteBridgeInfoFile();
        }

        private static string GenerateAuthToken()
        {
            var bytes = new byte[32];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToBase64String(bytes);
        }

        private static int GetRandomPort()
        {
            lock (PortRandom)
            {
                return PortRandom.Next(MinDynamicPort, MaxDynamicPort + 1);
            }
        }

        private static void RegisterHeartbeat()
        {
            EditorApplication.update -= WriteBridgeInfoHeartbeat;
            EditorApplication.update += WriteBridgeInfoHeartbeat;
            _nextHeartbeatTime = 0.0;
        }

        private static void UnregisterHeartbeat()
        {
            EditorApplication.update -= WriteBridgeInfoHeartbeat;
            _nextHeartbeatTime = 0.0;
        }

        private static void WriteBridgeInfoHeartbeat()
        {
            if (!_running)
                return;

            var currentTime = EditorApplication.timeSinceStartup;
            if (currentTime < _nextHeartbeatTime)
                return;

            WriteBridgeInfoFile();
            _nextHeartbeatTime = currentTime + HeartbeatIntervalSeconds;
        }

        private static void WriteBridgeInfoFile()
        {
            if (_serverPort <= 0)
                return;

            try
            {
                var bridgeInfoPath = GetBridgeInfoPath();
                Directory.CreateDirectory(Path.GetDirectoryName(bridgeInfoPath));

                var temporaryPath = bridgeInfoPath + ".tmp";
                File.WriteAllText(temporaryPath, BuildBridgeInfoJson(), new UTF8Encoding(false));

                if (File.Exists(bridgeInfoPath))
                    File.Replace(temporaryPath, bridgeInfoPath, null);
                else
                    File.Move(temporaryPath, bridgeInfoPath);
            }
            catch (Exception exception)
            {
                Log($"Failed to write Unity AI Context Bridge discovery file: {exception.Message}");
            }
        }

        private static string GetBridgeInfoPath()
        {
            if (!string.IsNullOrEmpty(_bridgeInfoPath))
                return _bridgeInfoPath;

            var projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            
            var bridgeInfoDirectory = Path.Combine(projectPath, "Temp", BridgeInfoFolderName);
            _bridgeInfoPath = Path.Combine(bridgeInfoDirectory, "bridge.json");
            return _bridgeInfoPath;
        }

        private static void DeleteBridgeInfoFile()
        {
            try
            {
                var bridgeInfoPath = GetBridgeInfoPath();
                if (File.Exists(bridgeInfoPath))
                    File.Delete(bridgeInfoPath);

                var temporaryPath = bridgeInfoPath + ".tmp";
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (Exception exception)
            {
                Log($"Failed to delete Unity AI Context Bridge discovery file: {exception.Message}");
            }
        }

        private static string BuildBridgeInfoJson()
        {
            var builder = new StringBuilder();
            var projectPath = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            var url = $"http://{ServerHost}:{_serverPort}/";

            builder.AppendLine("{");
            AppendJsonProperty(builder, "schemaVersion", "1", false, 2);
            AppendJsonProperty(builder, "source", BridgeSource, true, 2);
            AppendJsonProperty(builder, "host", ServerHost, true, 2);
            AppendJsonProperty(builder, "port", _serverPort.ToString(), false, 2);
            AppendJsonProperty(builder, "url", url, true, 2);
            AppendJsonProperty(builder, "projectName", Application.productName, true, 2);
            AppendJsonProperty(builder, "editorProcessId", GetEditorProcessId().ToString(), false, 2);
            AppendJsonProperty(builder, "lastHeartbeat", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), true, 2);
            AppendJsonProperty(builder, "authToken", _authToken, true, 2);
            AppendJsonProperty(builder, "projectPath", projectPath, true, 2, false);
            builder.AppendLine("}");

            return builder.ToString();
        }

        private static int GetEditorProcessId()
        {
            return System.Diagnostics.Process.GetCurrentProcess().Id;
        }

        private static void ServerLoop()
        {
            while (_running && _listener != null)
            {
                try
                {
                    var context = _listener.GetContext();
                    var response = context.Response;

                    if (!IsAuthorized(context.Request))
                    {
                        WriteUnauthorized(response);
                        continue;
                    }

                    string json = BuildJson();

                    byte[] buffer = Encoding.UTF8.GetBytes(json);

                    response.ContentType = "application/json";
                    response.ContentLength64 = buffer.Length;
                    response.OutputStream.Write(buffer, 0, buffer.Length);
                    response.OutputStream.Close();
                }
                catch
                {
                    // Listener stopped or request failed
                }
            }
        }

        private static bool IsAuthorized(HttpListenerRequest request)
        {
            var authorization = request.Headers["Authorization"];
            var expectedAuthorization = $"Bearer {_authToken}";
            return !string.IsNullOrEmpty(_authToken)
                   && string.Equals(authorization, expectedAuthorization, StringComparison.Ordinal);
        }

        private static void WriteUnauthorized(HttpListenerResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            response.Headers["WWW-Authenticate"] = "Bearer";
            response.ContentLength64 = 0;
            response.OutputStream.Close();
        }

        private static string BuildJson()
        {
            ContextItem[] snapshot;

            lock (ItemsLock)
            {
                snapshot = Items.ToArray();
            }

            var builder = new StringBuilder();
            builder.AppendLine("{");
            builder.AppendLine("  \"source\": \"unity-ai-context-bridge\",");
            builder.AppendLine("  \"items\": [");

            for (int i = 0; i < snapshot.Length; i++)
            {
                var item = snapshot[i];

                builder.AppendLine("    {");
                AppendJsonProperty(builder, "name", item.name, true, 6);
                AppendJsonProperty(builder, "type", item.type, true, 6);
                
                AppendJsonProperty(builder, "hierarchyPath", item.hierarchyPath, true, 6);
                AppendJsonProperty(builder, "scenePath", item.scenePath, true, 6);
                
                AppendJsonProperty(builder, "assetPath", item.assetPath, true, 6);
                
                AppendJsonProperty(builder, "instanceId", item.instanceId.ToString(), false, 6);
                AppendJsonProperty(builder, "globalObjectId", item.globalObjectId, true, 6, false);
                builder.Append(i == snapshot.Length - 1 ? "    }" : "    },");
                builder.AppendLine();
            }

            builder.AppendLine("  ]");
            builder.AppendLine("}");

            return builder.ToString();
        }

        private static void AppendJsonProperty(StringBuilder builder, string name, string value, bool quoteValue,
            int indent, bool trailingComma = true)
        {
            builder.Append(' ', indent);
            builder.Append('"');
            builder.Append(name);
            builder.Append("\": ");

            if (quoteValue)
            {
                builder.Append('"');
                builder.Append(EscapeJson(value));
                builder.Append('"');
            }
            else
            {
                builder.Append(value);
            }

            if (trailingComma)
                builder.Append(',');

            builder.AppendLine();
        }

        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var builder = new StringBuilder(value.Length);

            foreach (char character in value)
            {
                switch (character)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (character < ' ')
                            builder.AppendFormat("\\u{0:x4}", (int)character);
                        else
                            builder.Append(character);
                        break;
                }
            }

            return builder.ToString();
        }

        private static void Log(string message)
        {
            if (_showLogs)
                Debug.Log(message);
        }

        private class ContextItemAssetPostprocessor : AssetPostprocessor
        {
            private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
                string[] movedAssets, string[] movedFromAssetPaths)
            {
                QueueContextItemRefresh();
            }
        }
    }
}
