// ============================================================
//  Claude MCP Server for Unity
//  Place in: Assets/Editor/ClaudeMCP/ClaudeMCPServer.cs
//  Starts a local HTTP server so Claude can control Unity
// ============================================================

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ClaudeMCP
{
    [InitializeOnLoad]
    public static class ClaudeMCPServer
    {
        private const int PORT = 23457;
        private static HttpListener _listener;
        private static Thread _thread;
        private static bool _running = false;
        private static ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
        private static ConcurrentQueue<string> _pendingResults  = new ConcurrentQueue<string>();
        private static List<CompilerMessage> _lastCompileErrors = new List<CompilerMessage>();
        private static List<string> _consoleLogs = new List<string>();
        private const int MAX_LOGS = 100;

        static ClaudeMCPServer()
        {
            EditorApplication.delayCall += Start;
            EditorApplication.update    += ProcessMainThreadQueue;
            Application.logMessageReceived += CaptureLog;
            CompilationPipeline.assemblyCompilationFinished += OnCompiled;
        }

        private static void CaptureLog(string msg, string stack, LogType type)
        {
            lock (_consoleLogs)
            {
                _consoleLogs.Add("[" + type + "] " + msg);
                if (_consoleLogs.Count > MAX_LOGS)
                    _consoleLogs.RemoveAt(0);
            }
        }

        private static void OnCompiled(string path, CompilerMessage[] msgs)
        {
            lock (_lastCompileErrors)
            {
                _lastCompileErrors.Clear();
                foreach (var m in msgs)
                    if (m.type == CompilerMessageType.Error)
                        _lastCompileErrors.Add(m);
            }
        }

        private static void ProcessMainThreadQueue()
        {
            Action action;
            int processed = 0;
            while (processed < 5 && _mainThreadQueue.TryDequeue(out action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogError("[Claude MCP] Main thread error: " + e.Message); }
                processed++;
            }
        }

        [MenuItem("Window/Claude MCP/Start Server")]
        public static void Start()
        {
            if (_running) return;
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://localhost:" + PORT + "/");
                _listener.Start();
                _running = true;
                _thread  = new Thread(ListenLoop) { IsBackground = true };
                _thread.Start();
                Debug.Log("[Claude MCP] Server running on port " + PORT);
            }
            catch (Exception e)
            {
                Debug.LogError("[Claude MCP] Failed to start: " + e.Message);
            }
        }

        [MenuItem("Window/Claude MCP/Stop Server")]
        public static void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            Debug.Log("[Claude MCP] Server stopped.");
        }

        private static void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    HttpListenerContext ctx = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
                }
                catch { if (_running) Thread.Sleep(100); }
            }
        }

        private static void HandleRequest(HttpListenerContext ctx)
        {
            string path   = ctx.Request.Url.AbsolutePath;
            string method = ctx.Request.HttpMethod;
            string body   = "";

            if (method == "POST" || method == "PUT")
            {
                using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                    body = reader.ReadToEnd();
            }

            string response = "{}";
            int statusCode  = 200;

            try
            {
                if (path == "/status")
                    response = HandleStatus();
                else if (path == "/scene" && method == "GET")
                    response = HandleGetScene();
                else if (path == "/files" && method == "GET")
                    response = HandleListFiles(ctx.Request.QueryString["path"] ?? "Assets");
                else if (path == "/file" && method == "GET")
                    response = HandleReadFile(ctx.Request.QueryString["path"]);
                else if (path == "/file" && method == "POST")
                    response = HandleWriteFile(body);
                else if (path == "/file" && method == "DELETE")
                    response = HandleDeleteFile(ctx.Request.QueryString["path"]);
                else if (path == "/errors" && method == "GET")
                    response = HandleGetErrors();
                else if (path == "/logs" && method == "GET")
                    response = HandleGetLogs();
                else if (path == "/refresh" && method == "POST")
                    response = HandleRefresh();
                else if (path == "/selected" && method == "GET")
                    response = HandleGetSelected();
                else if (path == "/select" && method == "POST")
                    response = HandleSelect(body);
                else if (path == "/create-script" && method == "POST")
                    response = HandleCreateScript(body);
                else if (path == "/menu" && method == "POST")
                    response = HandleMenuItem(body);
                else if (path == "/packages" && method == "GET")
                    response = HandleGetPackages();
                else
                { response = "{\"error\":\"Unknown endpoint\"}"; statusCode = 404; }
            }
            catch (Exception e)
            {
                response   = "{\"error\":\"" + EscJson(e.Message) + "\"}";
                statusCode = 500;
            }

            byte[] buf = Encoding.UTF8.GetBytes(response);
            ctx.Response.StatusCode  = statusCode;
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            ctx.Response.ContentLength64 = buf.Length;
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
            ctx.Response.OutputStream.Close();
        }

        // ── Handlers ──────────────────────────────────────────

        private static string HandleStatus()
        {
            string sceneName = "";
            string unityVer  = Application.unityVersion;
            bool   hasErrors = false;

            RunOnMainThread(() => {
                sceneName = SceneManager.GetActiveScene().name;
                hasErrors = _lastCompileErrors.Count > 0;
            });

            return "{\"status\":\"ok\",\"scene\":\"" + EscJson(sceneName) +
                   "\",\"unity_version\":\"" + unityVer +
                   "\",\"has_errors\":" + (hasErrors ? "true" : "false") +
                   ",\"port\":" + PORT + "}";
        }

        private static string HandleGetScene()
        {
            string result = "{}";
            RunOnMainThread(() => {
                Scene scene = SceneManager.GetActiveScene();
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"name\":\"").Append(EscJson(scene.name)).Append("\"");
                sb.Append(",\"path\":\"").Append(EscJson(scene.path)).Append("\"");
                sb.Append(",\"object_count\":").Append(scene.rootCount);
                sb.Append(",\"objects\":[");

                GameObject[] roots = scene.GetRootGameObjects();
                for (int i = 0; i < roots.Length; i++)
                {
                    if (i > 0) sb.Append(",");
                    AppendGameObject(sb, roots[i], 0, 3);
                }
                sb.Append("]}");
                result = sb.ToString();
            });
            return result;
        }

        private static void AppendGameObject(StringBuilder sb, GameObject go, int depth, int maxDepth)
        {
            sb.Append("{\"name\":\"").Append(EscJson(go.name)).Append("\"");
            sb.Append(",\"active\":").Append(go.activeSelf ? "true" : "false");
            sb.Append(",\"tag\":\"").Append(EscJson(go.tag)).Append("\"");
            sb.Append(",\"layer\":").Append(go.layer);

            // Components
            Component[] comps = go.GetComponents<Component>();
            sb.Append(",\"components\":[");
            for (int i = 0; i < comps.Length; i++)
            {
                if (comps[i] == null) continue;
                if (i > 0) sb.Append(",");
                sb.Append("\"").Append(EscJson(comps[i].GetType().Name)).Append("\"");
            }
            sb.Append("]");

            // Children
            if (depth < maxDepth && go.transform.childCount > 0)
            {
                sb.Append(",\"children\":[");
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    if (i > 0) sb.Append(",");
                    AppendGameObject(sb, go.transform.GetChild(i).gameObject, depth + 1, maxDepth);
                }
                sb.Append("]");
            }
            else if (go.transform.childCount > 0)
                sb.Append(",\"child_count\":").Append(go.transform.childCount);

            sb.Append("}");
        }

        private static string HandleListFiles(string relativePath)
        {
            string fullPath = Path.Combine(Application.dataPath, "..",
                relativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
            fullPath = Path.GetFullPath(fullPath);

            if (!Directory.Exists(fullPath))
                return "{\"error\":\"Path not found\"}";

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"path\":\"").Append(EscJson(relativePath)).Append("\"");
            sb.Append(",\"files\":[");

            string[] files = Directory.GetFiles(fullPath);
            string[] dirs  = Directory.GetDirectories(fullPath);
            bool first = true;

            foreach (string d in dirs)
            {
                if (!first) sb.Append(","); first = false;
                string name = Path.GetFileName(d);
                sb.Append("{\"name\":\"").Append(EscJson(name))
                  .Append("\",\"type\":\"folder\",\"path\":\"")
                  .Append(EscJson(relativePath + "/" + name)).Append("\"}");
            }
            foreach (string f in files)
            {
                if (f.EndsWith(".meta")) continue;
                if (!first) sb.Append(","); first = false;
                string name = Path.GetFileName(f);
                long size = new FileInfo(f).Length;
                sb.Append("{\"name\":\"").Append(EscJson(name))
                  .Append("\",\"type\":\"file\",\"size\":").Append(size)
                  .Append(",\"path\":\"").Append(EscJson(relativePath + "/" + name)).Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string HandleReadFile(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "{\"error\":\"No path provided\"}";

            string fullPath = ToFullPath(path);
            if (!File.Exists(fullPath))
                return "{\"error\":\"File not found: " + EscJson(path) + "\"}";

            string content = File.ReadAllText(fullPath);
            return "{\"path\":\"" + EscJson(path) + "\",\"content\":\"" + EscJson(content) + "\"}";
        }

        private static string HandleWriteFile(string body)
        {
            // Expect JSON: {"path":"Assets/...", "content":"..."}
            string path    = ExtractJson(body, "path");
            string content = ExtractJson(body, "content");

            if (string.IsNullOrEmpty(path))
                return "{\"error\":\"No path provided\"}";

            string fullPath = ToFullPath(path);
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(fullPath, content);

            string result = "{\"ok\":true}";
            RunOnMainThread(() => {
                AssetDatabase.ImportAsset(path);
            });
            return result;
        }

        private static string HandleDeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "{\"error\":\"No path provided\"}";
            string fullPath = ToFullPath(path);
            if (!File.Exists(fullPath))
                return "{\"error\":\"File not found\"}";
            File.Delete(fullPath);
            if (File.Exists(fullPath + ".meta"))
                File.Delete(fullPath + ".meta");
            RunOnMainThread(() => AssetDatabase.Refresh());
            return "{\"ok\":true,\"deleted\":\"" + EscJson(path) + "\"}";
        }

        private static string HandleGetErrors()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"errors\":[");
            lock (_lastCompileErrors)
            {
                for (int i = 0; i < _lastCompileErrors.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    CompilerMessage m = _lastCompileErrors[i];
                    sb.Append("{\"file\":\"").Append(EscJson(m.file))
                      .Append("\",\"line\":").Append(m.line)
                      .Append(",\"message\":\"").Append(EscJson(m.message)).Append("\"}");
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string HandleGetLogs()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"logs\":[");
            lock (_consoleLogs)
            {
                for (int i = 0; i < _consoleLogs.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("\"").Append(EscJson(_consoleLogs[i])).Append("\"");
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string HandleRefresh()
        {
            RunOnMainThread(() => AssetDatabase.Refresh());
            return "{\"ok\":true}";
        }

        private static string HandleGetSelected()
        {
            string result = "{\"selected\":null}";
            RunOnMainThread(() => {
                GameObject go = Selection.activeGameObject;
                if (go == null) { result = "{\"selected\":null}"; return; }

                StringBuilder sb = new StringBuilder();
                sb.Append("{\"selected\":{");
                sb.Append("\"name\":\"").Append(EscJson(go.name)).Append("\"");
                sb.Append(",\"active\":").Append(go.activeSelf ? "true" : "false");
                sb.Append(",\"tag\":\"").Append(EscJson(go.tag)).Append("\"");
                sb.Append(",\"position\":{\"x\":").Append(go.transform.position.x)
                  .Append(",\"y\":").Append(go.transform.position.y)
                  .Append(",\"z\":").Append(go.transform.position.z).Append("}");
                sb.Append(",\"components\":[");
                Component[] comps = go.GetComponents<Component>();
                for (int i = 0; i < comps.Length; i++)
                {
                    if (comps[i] == null) continue;
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"type\":\"").Append(EscJson(comps[i].GetType().Name)).Append("\"}");
                }
                sb.Append("]}}");
                result = sb.ToString();
            });
            return result;
        }

        private static string HandleSelect(string body)
        {
            string name = ExtractJson(body, "name");
            string result = "{\"ok\":false,\"error\":\"Not found\"}";
            RunOnMainThread(() => {
                GameObject go = GameObject.Find(name);
                if (go != null)
                {
                    Selection.activeGameObject = go;
                    result = "{\"ok\":true,\"selected\":\"" + EscJson(go.name) + "\"}";
                }
            });
            return result;
        }

        private static string HandleCreateScript(string body)
        {
            string path    = ExtractJson(body, "path");
            string content = ExtractJson(body, "content");
            string name    = ExtractJson(body, "name");

            if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(name))
                path = "Assets/Scripts/" + name + ".cs";

            if (string.IsNullOrEmpty(path))
                return "{\"error\":\"No path or name provided\"}";

            string fullPath = ToFullPath(path);
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (string.IsNullOrEmpty(content))
            {
                string className = Path.GetFileNameWithoutExtension(path);
                content = "using UnityEngine;\n\npublic class " + className + " : MonoBehaviour\n{\n    void Start()\n    {\n    }\n\n    void Update()\n    {\n    }\n}\n";
            }

            File.WriteAllText(fullPath, content);
            RunOnMainThread(() => AssetDatabase.Refresh());
            return "{\"ok\":true,\"path\":\"" + EscJson(path) + "\"}";
        }

        private static string HandleMenuItem(string body)
        {
            string item = ExtractJson(body, "item");
            if (string.IsNullOrEmpty(item))
                return "{\"error\":\"No menu item provided\"}";
            bool ok = false;
            RunOnMainThread(() => ok = EditorApplication.ExecuteMenuItem(item));
            return "{\"ok\":" + (ok ? "true" : "false") + "}";
        }

        private static string HandleGetPackages()
        {
            // List package folders
            string pkgPath = Path.Combine(Application.dataPath, "..", "Packages");
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"packages\":[");
            if (Directory.Exists(pkgPath))
            {
                string[] dirs = Directory.GetDirectories(pkgPath);
                for (int i = 0; i < dirs.Length; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("\"").Append(EscJson(Path.GetFileName(dirs[i]))).Append("\"");
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ── Helpers ───────────────────────────────────────────
        private static void RunOnMainThread(Action action)
        {
            bool done = false;
            _mainThreadQueue.Enqueue(() => { action(); done = true; });
            double start = EditorApplication.timeSinceStartup;
            while (!done && EditorApplication.timeSinceStartup - start < 5.0)
                Thread.Sleep(10);
        }

        private static string ToFullPath(string assetPath)
        {
            if (assetPath.StartsWith("Assets/") || assetPath.StartsWith("Assets\\"))
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
            return Path.GetFullPath(assetPath);
        }

        private static string EscJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        private static string ExtractJson(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return "";
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (m.Success) return Regex.Unescape(m.Groups[1].Value);
            return "";
        }
    }
}
