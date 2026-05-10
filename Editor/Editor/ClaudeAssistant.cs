// ============================================================
//  Claude Assistant for Unity — AUTO-FIX Edition
//  Place BOTH files in: Assets/Editor/ClaudeAssistant/
//    - ClaudeAssistant.cs
//    - ClaudeAssistant.asmdef
//  Open: Window > Claude Assistant  or  Ctrl+Shift+A
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.Networking;

namespace ClaudeAssistant
{
    [InitializeOnLoad]
    public static class ClaudeAutoOpen
    {
        static ClaudeAutoOpen()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorPrefs.GetBool("ClaudeAssistant_AutoOpen", true))
                    ClaudeAssistantWindow.ShowWindow();
            };
        }
    }

    public class ClaudeAssistantWindow : EditorWindow
    {
        private const string MODEL         = "claude-sonnet-4-6";
        private const string API_URL       = "https://api.anthropic.com/v1/messages";
        private const string PREF_KEY      = "ClaudeAssistant_ApiKey";
        private const string PREF_AUTOOPEN = "ClaudeAssistant_AutoOpen";
        private const string PREF_AUTOERR  = "ClaudeAssistant_AutoErrors";
        private const string HISTORY_FILE  = "ClaudeAssistantHistory.json";
        private const int    MAX_HISTORY   = 60;
        private const int    MAX_API_MSGS  = 20;
        private const double BATCH_DELAY   = 1.5;

        private const string SYSTEM_PROMPT =
            "You are Claude, a friendly Unity assistant built into the Unity Editor. " +
            "You help people learning Unity and getting stuck — especially with errors, scripts, and understanding how things work. " +
            "You also have deep knowledge of VRChat world and avatar creation, including VRCSDK3, UdonSharp, VRChat Expression Menus, " +
            "PhysBones, Constraints, AudioLink, and Quest optimization. " +
            "Your top priority is fixing compile errors and runtime errors clearly and completely. " +
            "When fixing code: always return the FULL corrected script, explain what was wrong in 1-2 sentences, and say what to do next. " +
            "When explaining concepts: use plain language, avoid jargon unless you define it, and give short practical examples. " +
            "Always wrap C# or UdonSharp code in ```csharp code blocks. " +
            "Be encouraging — learning Unity and VRChat is hard and the user is doing great. Keep responses concise and actionable.";

        private const string FIX_PROMPT =
            "You are a Unity C# expert. I will give you a C# file and a compile error. " +
            "Return ONLY the complete fixed file with no explanation, no markdown, no code fences. " +
            "Just the raw C# code that compiles correctly.";

        private string  _apiKey      = "";
        private bool    _autoOpen    = true;
        private bool    _autoErrors  = true;
        private bool    _showSettings= false;
        private bool    _isLoading   = false;
        private bool    _isFixing    = false;
        private string  _userInput   = "";
        private string  _status      = "";
        private Vector2 _scroll;

        private List<ChatMessage>  _messages      = new List<ChatMessage>();
        private List<string>       _pendingErrors = new List<string>();
        private List<CompileError> _compileErrors = new List<CompileError>();

        private double _errorBatchTime  = 0;
        private double _lastRepaintTime = 0;

        private GUIStyle _sUser, _sClaude, _sError, _sCode, _sInput, _sQuick, _sFix;
        private bool _stylesReady;

        // ── Data types ────────────────────────────────────────
        [Serializable]
        private class ChatMessage
        {
            public string role;
            public string content;
            public bool   isError;
        }
        [Serializable]
        private class SaveData
        {
            public List<ChatMessage> msgs;
        }
        private class CompileError
        {
            public string file;
            public int    line;
            public string message;
        }

        [MenuItem("Window/Claude Assistant")]
        public static void ShowWindow()
        {
            ClaudeAssistantWindow w = GetWindow<ClaudeAssistantWindow>();
            w.titleContent = new GUIContent("Claude");
            w.minSize = new Vector2(340, 480);
        }

        private void OnEnable()
        {
            _apiKey     = EditorPrefs.GetString(PREF_KEY,     "");
            _autoOpen   = EditorPrefs.GetBool(PREF_AUTOOPEN,  true);
            _autoErrors = EditorPrefs.GetBool(PREF_AUTOERR,   true);

            LoadHistory();

            Application.logMessageReceived         += OnLog;
            CompilationPipeline.compilationStarted += OnCompileStart;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyFinished;
            EditorApplication.update               += Tick;

            if (_messages.Count == 0)
                AddBot("Hey! I'm Claude — your Unity & VRChat helper 👋\n\n" +
                       "Stuck on an error? Hit the red AUTO-FIX button and I'll sort it out.\n\n" +
                       "I can also:\n" +
                       "* Explain errors in plain English\n" +
                       "* Write or fix C# and UdonSharp scripts\n" +
                       "* Help with VRChat worlds, avatars, PhysBones, AudioLink\n" +
                       "* Explain any Unity concept step by step\n\n" +
                       "What are you working on?");
        }

        private void OnDisable()
        {
            Application.logMessageReceived         -= OnLog;
            CompilationPipeline.compilationStarted -= OnCompileStart;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyFinished;
            EditorApplication.update               -= Tick;
            SaveHistory();
        }

        // ── Compile error capture ─────────────────────────────
        private void OnAssemblyFinished(string assemblyPath, CompilerMessage[] messages)
        {
            _compileErrors.Clear();
            foreach (CompilerMessage msg in messages)
            {
                if (msg.type == CompilerMessageType.Error)
                {
                    CompileError e = new CompileError();
                    e.file    = msg.file;
                    e.line    = msg.line;
                    e.message = msg.message;
                    _compileErrors.Add(e);
                }
            }
            if (_compileErrors.Count > 0)
                Repaint();
        }

        private void Tick()
        {
            if (_pendingErrors.Count > 0 && EditorApplication.timeSinceStartup > _errorBatchTime)
                FlushErrors();

            if ((_isLoading || _isFixing) && EditorApplication.timeSinceStartup - _lastRepaintTime > 0.12)
            {
                _lastRepaintTime = EditorApplication.timeSinceStartup;
                Repaint();
            }
        }

        private void OnLog(string msg, string stack, LogType type)
        {
            if (!_autoErrors || _isLoading) return;
            if (type != LogType.Error && type != LogType.Exception) return;
            _pendingErrors.Add("[" + type + "] " + msg + "\n" + stack);
            _errorBatchTime = EditorApplication.timeSinceStartup + BATCH_DELAY;
        }

        private void OnCompileStart(object obj)
        {
            _status = "Compiling...";
            Repaint();
        }

        private void FlushErrors()
        {
            string combined = string.Join("\n\n", _pendingErrors.ToArray());
            _pendingErrors.Clear();
            string text = "Unity detected errors:\n```\n" + combined + "\n```\nHow do I fix them?";
            AddUser(text, true);
            SendToClaude(text);
        }

        // ── AUTO-FIX ──────────────────────────────────────────
        private async void AutoFixErrors()
        {
            if (_compileErrors.Count == 0)
            {
                AddBot("No compile errors detected right now! Try clicking Fix Errors or check your Console tab.");
                return;
            }
            if (string.IsNullOrEmpty(_apiKey))
            {
                AddBot("Add your API key in Settings first!");
                return;
            }

            _isFixing = true;
            int fixedCount = 0;

            // Group errors by file
            Dictionary<string, List<CompileError>> byFile = new Dictionary<string, List<CompileError>>();
            foreach (CompileError e in _compileErrors)
            {
                if (string.IsNullOrEmpty(e.file)) continue;
                if (!byFile.ContainsKey(e.file))
                    byFile[e.file] = new List<CompileError>();
                byFile[e.file].Add(e);
            }

            foreach (string filePath in byFile.Keys)
            {
                // Skip files outside Assets (don't touch packages)
                if (!filePath.Contains("Assets")) continue;

                string fullPath = Path.GetFullPath(filePath);
                if (!File.Exists(fullPath)) continue;

                string code = File.ReadAllText(fullPath);
                List<CompileError> errors = byFile[filePath];

                StringBuilder errorList = new StringBuilder();
                foreach (CompileError e in errors)
                    errorList.AppendLine("Line " + e.line + ": " + e.message);

                string prompt = "FILE: " + filePath + "\n\n" +
                                "ERRORS:\n" + errorList.ToString() + "\n\n" +
                                "CODE:\n" + code;

                AddBot("Fixing: " + Path.GetFileName(filePath) + "...");

                string fixed_code = await CallClaudeForFix(prompt);

                if (!string.IsNullOrEmpty(fixed_code) && fixed_code != "ERROR")
                {
                    File.WriteAllText(fullPath, fixed_code);
                    fixedCount++;
                    AddBot("Fixed " + Path.GetFileName(filePath) + "! Recompiling...");
                }
                else
                {
                    AddBot("Could not auto-fix " + Path.GetFileName(filePath) + ". Try asking me manually what the error means.");
                }
            }

            _isFixing = false;

            if (fixedCount > 0)
            {
                AssetDatabase.Refresh();
                AddBot("Done! Fixed " + fixedCount + " file(s). Waiting for Unity to recompile...");
            }
            else
            {
                AddBot("Could not auto-fix these errors. Copy the red errors from your Console and paste them here and I'll help manually!");
            }
        }

        private async System.Threading.Tasks.Task<string> CallClaudeForFix(string prompt)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"model\":\"").Append(MODEL).Append("\"");
            sb.Append(",\"max_tokens\":4096");
            sb.Append(",\"system\":\"").Append(Esc(FIX_PROMPT)).Append("\"");
            sb.Append(",\"messages\":[{\"role\":\"user\",\"content\":\"").Append(Esc(prompt)).Append("\"}]}");

            byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
            UnityWebRequest req = new UnityWebRequest(API_URL, "POST");
            req.uploadHandler   = new UploadHandlerRaw(bytes);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type",      "application/json");
            req.SetRequestHeader("x-api-key",         _apiKey);
            req.SetRequestHeader("anthropic-version", "2023-06-01");

            UnityWebRequestAsyncOperation op = req.SendWebRequest();
            while (!op.isDone)
                await System.Threading.Tasks.Task.Yield();

            string result = "ERROR";
            if (req.result == UnityWebRequest.Result.Success)
            {
                result = ParseText(req.downloadHandler.text);
                // Strip any accidental code fences
                result = Regex.Replace(result, @"^```(?:csharp|cs)?\s*", "", RegexOptions.Multiline);
                result = Regex.Replace(result, @"```\s*$", "", RegexOptions.Multiline);
                result = result.Trim();
            }
            req.Dispose();
            return result;
        }

        // ── History ───────────────────────────────────────────
        private string HistoryPath
        {
            get { return Path.Combine(Application.dataPath, "..", "Library", HISTORY_FILE); }
        }

        private void SaveHistory()
        {
            try
            {
                SaveData data = new SaveData();
                data.msgs = _messages;
                File.WriteAllText(HistoryPath, JsonUtility.ToJson(data, true));
            }
            catch { }
        }

        private void LoadHistory()
        {
            try
            {
                if (!File.Exists(HistoryPath)) return;
                SaveData d = JsonUtility.FromJson<SaveData>(File.ReadAllText(HistoryPath));
                if (d != null && d.msgs != null)
                {
                    _messages = d.msgs;
                    if (_messages.Count > MAX_HISTORY)
                        _messages.RemoveRange(0, _messages.Count - MAX_HISTORY);
                }
            }
            catch { }
        }

        // ── GUI ───────────────────────────────────────────────
        private void OnGUI()
        {
            BuildStyles();
            DrawToolbar();
            if (_showSettings) { DrawSettings(); return; }
            DrawContextStrip();
            DrawAutoFixBanner();
            DrawChatArea();
            DrawInputArea();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Claude Assistant", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();

            if (!string.IsNullOrEmpty(_status))
                GUILayout.Label(_status, EditorStyles.miniLabel);

            Color prev = GUI.color;
            GUI.color = _autoErrors ? new Color(1f, 0.45f, 0.45f) : Color.gray;
            if (GUILayout.Button(_autoErrors ? "Auto-Err ON" : "Auto-Err OFF",
                    EditorStyles.toolbarButton, GUILayout.Width(88)))
            {
                _autoErrors = !_autoErrors;
                EditorPrefs.SetBool(PREF_AUTOERR, _autoErrors);
            }
            GUI.color = prev;

            if (GUILayout.Button("Settings", EditorStyles.toolbarButton, GUILayout.Width(55)))
                _showSettings = !_showSettings;

            if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(40)))
                if (EditorUtility.DisplayDialog("Clear Chat", "Clear all history?", "Yes", "No"))
                { _messages.Clear(); SaveHistory(); }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawAutoFixBanner()
        {
            if (_compileErrors.Count == 0 && !_isFixing) return;

            Color prev = GUI.color;

            if (_isFixing)
            {
                GUI.color = new Color(1f, 0.8f, 0.2f);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                int dots = (int)(EditorApplication.timeSinceStartup % 3) + 1;
                GUILayout.Label("Fixing errors" + new string('.', dots), EditorStyles.boldLabel);
                EditorGUILayout.EndVertical();
            }
            else
            {
                GUI.color = new Color(1f, 0.3f, 0.3f);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                GUI.color = prev;
                GUILayout.Label(_compileErrors.Count + " compile error(s) detected!", EditorStyles.boldLabel);
                for (int i = 0; i < Math.Min(_compileErrors.Count, 3); i++)
                {
                    CompileError e = _compileErrors[i];
                    GUILayout.Label(Path.GetFileName(e.file) + " line " + e.line + ": " + e.message,
                        EditorStyles.miniLabel);
                }
                if (_compileErrors.Count > 3)
                    GUILayout.Label("...and " + (_compileErrors.Count - 3) + " more", EditorStyles.miniLabel);

                EditorGUI.BeginDisabledGroup(string.IsNullOrEmpty(_apiKey));
                if (GUILayout.Button("AUTO-FIX WITH CLAUDE", _sFix, GUILayout.Height(32)))
                    AutoFixErrors();
                EditorGUI.EndDisabledGroup();

                if (string.IsNullOrEmpty(_apiKey))
                    GUILayout.Label("Add API key in Settings to enable auto-fix", EditorStyles.centeredGreyMiniLabel);

                EditorGUILayout.EndVertical();
            }

            GUI.color = prev;
        }

        private void DrawContextStrip()
        {
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            string sel   = Selection.activeGameObject != null ? Selection.activeGameObject.name : "nothing";

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUILayout.Label("Scene: " + scene, EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            GUILayout.Label("Selected: " + sel, EditorStyles.miniLabel);

            if (Selection.activeGameObject != null)
            {
                if (GUILayout.Button("Ask about selected", EditorStyles.miniButton, GUILayout.Width(115)))
                {
                    GameObject go = Selection.activeGameObject;
                    Component[] comps = go.GetComponents<Component>();
                    string[] names = new string[comps.Length];
                    for (int i = 0; i < comps.Length; i++)
                        names[i] = comps[i].GetType().Name;
                    _userInput = "I have a GameObject '" + go.name + "' with: " +
                                 string.Join(", ", names) + ". Help me with it.";
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawChatArea()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

            for (int i = 0; i < _messages.Count; i++)
                DrawBubble(_messages[i]);

            if (_isLoading)
            {
                int dots = (int)(EditorApplication.timeSinceStartup % 3) + 1;
                GUILayout.Label("  Claude is thinking" + new string('.', dots), EditorStyles.miniLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawBubble(ChatMessage m)
        {
            bool isUser = m.role == "user";
            GUILayout.BeginHorizontal();
            if (isUser)
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label(m.content, m.isError ? _sError : _sUser,
                    GUILayout.MaxWidth(position.width - 50));
            }
            else
            {
                GUILayout.Space(4);
                RenderBot(m.content);
                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
        }

        private void RenderBot(string content)
        {
            string[] parts = Regex.Split(content,
                @"```(?:csharp|cs|c#|udon|udonsharp)?(.*?)```",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            GUILayout.BeginVertical(GUILayout.MaxWidth(position.width - 55));
            bool isCode = false;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (string.IsNullOrWhiteSpace(part)) { isCode = !isCode; continue; }
                if (isCode)
                {
                    string trimmed = part.Trim();
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    GUILayout.Label(trimmed, _sCode);
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Save Script", GUILayout.Width(90), GUILayout.Height(20)))
                        SaveScript(trimmed);
                    if (GUILayout.Button("Copy", GUILayout.Width(55), GUILayout.Height(20)))
                    { EditorGUIUtility.systemCopyBuffer = trimmed; _status = "Copied!"; }
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.EndVertical();
                }
                else
                    GUILayout.Label(part.Trim(), _sClaude);

                isCode = !isCode;
            }
            GUILayout.EndVertical();
        }

        private void DrawInputArea()
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Fix Errors",   _sQuick)) { _userInput = "Fix the errors in my Unity console."; Submit(); }
            if (GUILayout.Button("Explain Error", _sQuick)) { _userInput = "Explain what this error means and how to fix it: "; GUI.FocusControl("CI"); }
            if (GUILayout.Button("New Script",    _sQuick)) { _userInput = "Write a C# MonoBehaviour script that "; GUI.FocusControl("CI"); }
            if (GUILayout.Button("VRChat Help",   _sQuick)) { _userInput = "Help me with VRChat: "; GUI.FocusControl("CI"); }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (string.IsNullOrEmpty(_apiKey))
                EditorGUILayout.HelpBox("Enter your API key in Settings to start!", MessageType.Warning);

            GUI.SetNextControlName("CI");
            _userInput = EditorGUILayout.TextArea(_userInput, _sInput,
                GUILayout.MinHeight(54), GUILayout.MaxHeight(90));

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Ctrl+Enter to send", EditorStyles.centeredGreyMiniLabel);
            GUILayout.FlexibleSpace();
            EditorGUI.BeginDisabledGroup(_isLoading || string.IsNullOrEmpty(_apiKey) || string.IsNullOrWhiteSpace(_userInput));
            if (GUILayout.Button("Send", GUILayout.Width(70), GUILayout.Height(26)) || IsCtrlEnter())
                Submit();
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private bool IsCtrlEnter()
        {
            Event e = Event.current;
            return e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.Return
                   && (e.control || e.command) && GUI.GetNameOfFocusedControl() == "CI";
        }

        private void DrawSettings()
        {
            EditorGUILayout.Space(8);
            GUILayout.Label("Settings", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            GUILayout.Label("Anthropic API Key", EditorStyles.boldLabel);
            string k = EditorGUILayout.PasswordField(_apiKey);
            if (k != _apiKey) { _apiKey = k; EditorPrefs.SetString(PREF_KEY, k); }

            if (GUILayout.Button("Get API key at console.anthropic.com", EditorStyles.linkLabel))
                Application.OpenURL("https://console.anthropic.com");

            EditorGUILayout.Space(6);
            bool ao = EditorGUILayout.Toggle("Open on Unity startup", _autoOpen);
            if (ao != _autoOpen) { _autoOpen = ao; EditorPrefs.SetBool(PREF_AUTOOPEN, ao); }

            bool ae = EditorGUILayout.Toggle("Auto-send errors to Claude", _autoErrors);
            if (ae != _autoErrors) { _autoErrors = ae; EditorPrefs.SetBool(PREF_AUTOERR, ae); }

            EditorGUILayout.Space(4);
            GUILayout.Label("Shortcut: Ctrl + Shift + A", EditorStyles.miniLabel);
            GUILayout.Label("Send:     Ctrl + Enter",      EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(8);
            if (GUILayout.Button("Back to Chat")) _showSettings = false;
        }

        private void Submit()
        {
            if (string.IsNullOrWhiteSpace(_userInput)) return;
            string txt = _userInput.Trim();
            _userInput = "";
            GUI.FocusControl(null);
            AddUser(txt);
            SendToClaude(txt);
        }

        private void AddUser(string text, bool isError = false)
        {
            ChatMessage m = new ChatMessage();
            m.role = "user"; m.content = text; m.isError = isError;
            _messages.Add(m);
            _scroll = new Vector2(0, float.MaxValue);
            Repaint();
        }

        private void AddBot(string text)
        {
            ChatMessage m = new ChatMessage();
            m.role = "assistant"; m.content = text;
            _messages.Add(m);
            _scroll = new Vector2(0, float.MaxValue);
            SaveHistory();
            Repaint();
        }

        private async void SendToClaude(string userText)
        {
            if (string.IsNullOrEmpty(_apiKey))
            { AddBot("Add your API key in Settings first!"); return; }

            _isLoading = true; _status = "Thinking..."; Repaint();

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"model\":\"").Append(MODEL).Append("\"");
            sb.Append(",\"max_tokens\":2048");
            sb.Append(",\"system\":\"").Append(Esc(SYSTEM_PROMPT)).Append("\"");
            sb.Append(",\"messages\":[");

            int start = Math.Max(0, _messages.Count - MAX_API_MSGS);
            for (int i = start; i < _messages.Count; i++)
            {
                if (i > start) sb.Append(",");
                ChatMessage m = _messages[i];
                string extra = "";
                if (i == _messages.Count - 1 && m.role == "user")
                {
                    extra = " [Scene: " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                    if (Selection.activeGameObject != null)
                        extra += ", Selected: " + Selection.activeGameObject.name;
                    extra += "]";
                }
                sb.Append("{\"role\":\"").Append(m.role).Append("\",\"content\":\"")
                  .Append(Esc(m.content + extra)).Append("\"}");
            }
            sb.Append("]}");

            byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
            UnityWebRequest req = new UnityWebRequest(API_URL, "POST");
            req.uploadHandler   = new UploadHandlerRaw(bytes);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type",      "application/json");
            req.SetRequestHeader("x-api-key",         _apiKey);
            req.SetRequestHeader("anthropic-version", "2023-06-01");

            UnityWebRequestAsyncOperation op = req.SendWebRequest();
            while (!op.isDone)
                await System.Threading.Tasks.Task.Yield();

            _isLoading = false; _status = "";

            string result = req.result != UnityWebRequest.Result.Success
                ? "Error: " + req.error + "\n" + req.downloadHandler.text
                : ParseText(req.downloadHandler.text);

            req.Dispose();
            AddBot(result);
        }

        private static string Esc(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        private static string ParseText(string json)
        {
            Match m = Regex.Match(json, "\"text\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : "Could not parse response.";
        }

        private void SaveScript(string code)
        {
            Match m    = Regex.Match(code, @"\bclass\s+(\w+)");
            string name = m.Success ? m.Groups[1].Value : "ClaudeScript";
            string path = EditorUtility.SaveFilePanelInProject("Save Script", name, "cs", "Save script");
            if (string.IsNullOrEmpty(path)) return;
            File.WriteAllText(path, code);
            AssetDatabase.Refresh();
            MonoScript asset = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            if (asset != null) { EditorGUIUtility.PingObject(asset); Selection.activeObject = asset; }
            _status = "Saved!";
        }

        private void BuildStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;

            _sUser = new GUIStyle(EditorStyles.wordWrappedLabel);
            _sUser.normal.background = Tex(new Color(0.2f, 0.44f, 0.9f, 0.88f));
            _sUser.normal.textColor  = Color.white;
            _sUser.padding  = new RectOffset(10, 10, 7, 7);
            _sUser.margin   = new RectOffset(28, 6, 2, 2);
            _sUser.wordWrap = true;
            _sUser.fontSize = 12;

            _sClaude = new GUIStyle(EditorStyles.wordWrappedLabel);
            _sClaude.normal.background = Tex(new Color(0.14f, 0.14f, 0.16f, 0.55f));
            _sClaude.normal.textColor  = new Color(0.93f, 0.93f, 0.93f);
            _sClaude.padding  = new RectOffset(10, 10, 7, 7);
            _sClaude.margin   = new RectOffset(6, 28, 2, 2);
            _sClaude.wordWrap = true;
            _sClaude.fontSize = 12;

            _sError = new GUIStyle(_sUser);
            _sError.normal.background = Tex(new Color(0.78f, 0.1f, 0.1f, 0.88f));
            _sError.normal.textColor  = Color.white;

            _sCode = new GUIStyle(EditorStyles.wordWrappedLabel);
            _sCode.fontSize = 11;
            _sCode.wordWrap = true;
            _sCode.normal.textColor = new Color(0.5f, 1f, 0.5f);
            _sCode.padding = new RectOffset(4, 4, 4, 4);

            _sInput = new GUIStyle(EditorStyles.textArea);
            _sInput.wordWrap = true;
            _sInput.fontSize = 12;

            _sQuick = new GUIStyle(EditorStyles.miniButton);
            _sQuick.fontSize = 10;

            _sFix = new GUIStyle(EditorStyles.miniButton);
            _sFix.fontSize    = 13;
            _sFix.fontStyle   = FontStyle.Bold;
            _sFix.normal.background  = Tex(new Color(0.85f, 0.2f, 0.2f, 1f));
            _sFix.normal.textColor   = Color.white;
            _sFix.hover.background   = Tex(new Color(1f, 0.3f, 0.3f, 1f));
            _sFix.hover.textColor    = Color.white;
        }

        private static Texture2D Tex(Color c)
        {
            Texture2D t = new Texture2D(2, 2);
            Color[] p = new Color[] { c, c, c, c };
            t.SetPixels(p);
            t.Apply();
            return t;
        }
    }
}
