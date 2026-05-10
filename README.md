# Claude Unity Assistant

> AI-powered error fixing and help, built right into the Unity Editor.

Stuck on a red compile error? One click and Claude fixes it automatically. Ask questions in plain English. Works with standard Unity **and** VRChat world/avatar projects.

![Unity Editor with Claude panel](https://img.shields.io/badge/Unity-2021.3%2B-blue) ![License MIT](https://img.shields.io/badge/license-MIT-green)

---

## Features

- 🔴 **Auto-Fix Errors** — detects compile errors and fixes the file automatically
- 💬 **In-editor chat** — ask anything without leaving Unity
- 🎮 **VRChat support** — UdonSharp, PhysBones, AudioLink, VRCSDK3, Quest optimization
- ✏️ **Script writing** — describe what you want, get working C# code
- 📖 **Plain English explanations** — no more googling cryptic error messages
- 💾 **Persistent history** — your chat is saved between Unity sessions

---

## Install

### Option A · Unity Package Manager (recommended)

1. In Unity: **Window → Package Manager**
2. Click **+** → **Add package from git URL**
3. Paste:
```
https://github.com/YOUR_USERNAME/claude-unity-assistant.git#upm
```

### Option B · Drag and drop

Download this repo → drag the `Editor` folder into `Assets/Editor/ClaudeAssistant/` in your project.

### Option C · Windows installer

Download and run `Install.bat` from the [Releases](https://github.com/YOUR_USERNAME/claude-unity-assistant/releases) page.

---

## First-time setup

1. Open Unity → **Window → Claude Assistant**
2. Click **Settings** (top right of the panel)
3. Paste your Anthropic API key — get one free at [console.anthropic.com](https://console.anthropic.com)
4. Done!

---

## Usage

| Button | What it does |
|---|---|
| **AUTO-FIX** (red banner) | Automatically fixes detected compile errors |
| **Fix Errors** | Sends current console errors to Claude |
| **Explain Error** | Paste an error, get a plain English explanation |
| **New Script** | Describe what you want, get working C# |
| **VRChat Help** | UdonSharp, PhysBones, AudioLink, worlds & avatars |

**Shortcuts:** `Ctrl+Shift+A` to open · `Ctrl+Enter` to send

---

## Requirements

- Unity 2021.3 LTS or newer
- Anthropic API key (free tier is fine) — [console.anthropic.com](https://console.anthropic.com)

---

## License

MIT — free to use, modify, and distribute.
