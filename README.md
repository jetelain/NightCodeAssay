# NightCode Assay

**NightCode Assay** is a lightweight, zero-VRAM Windows desktop application (.NET 10 / Avalonia UI with software rendering) designed as an exploratory software testing companion for **NightCodeForge** — and fully usable in **Standalone Mode** whenever NightCodeForge is offline or unavailable.

During manual exploratory testing of Web or Desktop applications, NightCode Assay captures screenshots via a global hotkey (`Ctrl+Alt+S`), gathers desktop/browser/log context on the fly, optionally enriches findings with a Vision-Language Model (VLM) via `OllamaSharp` or `OpenAI` (`IChatClient`), and writes a structured `report.md` ready for handoff into **NightCodeForge Ideation**, **Triage**, or **Review Room**.

---

## Key Features

- **Zero-VRAM Software Rendering:** Configures Avalonia with `Win32RenderingMode.Software` (`X11RenderingMode.Software` on Linux) so Assay uses **0 MB of GPU VRAM** and never competes with local VLM/LLM inference.
- **Standalone & Companion Modes:**
  - **Standalone Mode (`<Unlinked / Standalone>`):** Works immediately out of the box without NightCodeForge running. Sessions, annotated screenshots (`assets/capture_001.png`), and `report.md` are saved locally under `./sessions/YYYY-MM-DD_HH-mm/`.
  - **NightCodeForge Companion Mode:** Automatically discovers NightCodeForge's DPAPI-protected `default-key.dat` (`DefaultKeyReader`), connects to `/api/companion/config` and `/api/companion/workspaces`, and enables 1-click **Send to Ideation** (`POST /api/companion/workspaces/{id}/ideation`).
- **On-the-Fly Capture Overlay (`Ctrl+Alt+S`):**
  - Captures the desktop or active window **before** showing any overlay UI.
  - Drag the header bar or modal border to move the overlay out of the way and inspect content underneath.
  - Scroll the mouse wheel (or use `+` / `-` / `Ctrl++` / `Ctrl+-` / `Ctrl+0`) to zoom in/out up to 500%, right/middle-drag to pan, and left-drag directly on the screenshot to draw a red highlight rectangle.
  - Choose a category (`[Bug]`, `[UI/UX]`, `[Enhancement]`, `[Question]`), type your observation, and press `Enter` to submit (`Shift+Enter` for newline, `Esc` to cancel).
  - Non-blocking `Channel<CaptureEntry>` pipeline saves PNGs, runs VLM visual analysis (with automatic retries and a 300s default per-attempt timeout), and appends entries to `report.md` in the background.
- **Hybrid Technical Context Collection (`IContextCollector`):**
  - **Windows Desktop (Win32):** Foreground window title, process name, executable path, window bounds, and recent `Application` Event Log errors/warnings (last 30 seconds).
  - **Web / Browser (CDP & Firefox WebDriver BiDi):** Connects via `ClientWebSocket` either to Chromium CDP (`http://localhost:9222/json` for Edge / Chrome) or to Firefox WebDriver BiDi (`ws://127.0.0.1:9222/session`), keeping a 50-event ring buffer of console errors, uncaught exceptions, failed network requests, and HTTP errors (`status >= 400`). Includes 1-click **Start Edge**, **Start Chrome**, and **Start Firefox** debug-port launchers in the Diagnostics card and System Tray menu.
  - **Local Log Tailer:** Reads configured `LogFilePaths` globs and workspace `<RepositoryPath>/**/logs/*.log` files with `FileShare.ReadWrite`, extracting lines from the last 20 seconds.
- **Graceful VLM Degradation:** Supports `Ollama`, `OllamaRouter` (via standard `OllamaApiClient` without router headers), and `OpenAiCompatible` endpoints. If no VLM is running or a request times out, `[VLM analysis unavailable]` is recorded without ever losing the screenshot, note, or technical context.

---

## Project Structure

- `src/NightCodeAssay.Core`: Configuration (`AssayOptions`), DPAPI/Plain `DefaultKeyReader`, domain models, `IReportWriter` (`ReportWriter`), `IVisionAnalyzer` (`ChatClientVisionAnalyzer`), and `CapturePipelineService`.
- `src/NightCodeAssay.Infrastructure`: Win32 screen capture & SkiaSharp annotation (`Win32ScreenCaptureService`), Win32 `RegisterHotKey` listener (`GlobalHotkeyService`), Win32 foreground/EventLog collector, CDP WebSocket ring-buffer listener, workspace log tailer, and `NightCodeForgeCompanionClient`.
- `src/NightCodeAssay.App`: Avalonia desktop application with System Tray icon, Session Control Window, and borderless topmost Quick Capture floating modal.
- `tests/NightCodeAssay.Tests`: xUnit tests covering key resolution, Markdown report generation, log tailing, VLM fallback, companion client serialization, CDP parsing, and standalone end-to-end capture pipeline.

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows 10/11 (recommended for Win32 screen capture, global hotkeys, DPAPI key unwrapping, and Event Log collection)
- *(Optional)* [Ollama](https://ollama.com/) with a vision model (e.g., `ollama pull llama3.2-vision` or `qwen2.5vl`) or an OpenAI-compatible VLM endpoint

### Build, Test & Run

```powershell
# Build the entire solution
dotnet build NightCodeAssay.slnx

# Run the unit and standalone pipeline tests
dotnet test NightCodeAssay.slnx

# Launch the desktop application (defaults to Standalone Mode if NightCodeForge is offline)
dotnet run --project src/NightCodeAssay.App
```

---

## Configuration (`src/NightCodeAssay.App/appsettings.json`)

```json
{
  "NightCodeForgeUrl": "http://localhost:5270",
  "NightCodeForge": {
    "Url": "",
    "ApiKey": "",
    "ConfigDirectory": ""
  },
  "Hotkey": "Ctrl+Alt+S",
  "CaptureActiveWindowOnly": false,
  "CdpPort": 9222,
  "SessionsDirectory": "./sessions",
  "LogFilePaths": [],
  "ProviderTypeOverride": "Ollama",
  "EndpointOverride": "http://localhost:11434",
  "VisionModelIdOverride": "",
  "OpenAiApiKeyOverride": "",
  "VlmTimeoutSeconds": 300,
  "VlmMaxRetries": 2
}
```

- **Local Overrides vs. Companion Config:** By default, `appsettings.json` points `ProviderTypeOverride` to `Ollama` and `EndpointOverride` to `http://localhost:11434` so **Standalone Mode** works immediately. To inherit the LLM provider and endpoint directly from NightCodeForge (`/api/companion/config`), clear `ProviderTypeOverride` and `EndpointOverride` to `""`.
- **Companion Key Precedence (`DefaultKeyReader`):**
  1. Explicit `NightCodeForge:ApiKey` in `appsettings.json` (useful for remote/Tailscale instances)
  2. Explicit `NightCodeForge:ConfigDirectory` -> `<ConfigDirectory>/default-key.dat`
  3. `NIGHTCODEFORGE_HOME` environment variable -> `<NIGHTCODEFORGE_HOME>/config/default-key.dat`
  4. Standard Windows installation paths (`%LOCALAPPDATA%\Programs\NightCodeForge\config\default-key.dat`, `%LOCALAPPDATA%\NightCodeForge\config\default-key.dat`) or sibling repo (`..\NightCodeForge\src\NightCodeForge.Web\config\default-key.dat`)
- **Environment Variable Overrides:** Any setting can also be overridden with the `NIGHTCODEASSAY_` prefix (for example `NIGHTCODEASSAY_VisionModelIdOverride=qwen2.5vl:7b`).

---

## Capturing Web Console & Network Errors (Edge, Chrome, and Firefox)

To let NightCode Assay read the active tab URL, uncaught JavaScript exceptions, `console.error` output, and failed HTTP requests (`>= 400`), use the **Start Edge**, **Start Chrome**, or **Start Firefox** buttons in the Session Control window (or System Tray menu). Each button launches the browser with remote debugging enabled on `CdpPort` (default `9222`) using an isolated profile directory (`%LOCALAPPDATA%\NightCodeAssay\browser-profiles\`) so debugging still works alongside your regular browser windows.

You can also launch a browser manually from a terminal:

```powershell
# Google Chrome (CDP)
chrome.exe --remote-debugging-port=9222 --remote-allow-origins=* --user-data-dir="%LOCALAPPDATA%\NightCodeAssay\browser-profiles\chrome"

# Microsoft Edge (CDP)
msedge.exe --remote-debugging-port=9222 --remote-allow-origins=* --user-data-dir="%LOCALAPPDATA%\NightCodeAssay\browser-profiles\edge"

# Mozilla Firefox (WebDriver BiDi)
firefox.exe --no-remote --profile "%LOCALAPPDATA%\NightCodeAssay\browser-profiles\firefox" --remote-debugging-port 9222
```

When an exploratory session starts (or when you click **Refresh**), Assay automatically connects via Chromium CDP (`http://localhost:9222/json`) or Firefox WebDriver BiDi (`ws://127.0.0.1:9222/session`) and maintains a rolling buffer of the last 50 browser errors.

---

## Report Output & NightCodeForge Handoff

Each session creates `./sessions/YYYY-MM-DD_HH-mm/` with:
- `assets/capture_001.png`, `assets/capture_002.png`, ...
- `report.md` formatted for both human readers and LLM agents.

From the Control Window or System Tray menu, you can:
1. **Open Session Folder / Open `report.md`:** Inspect artifacts directly on disk.
2. **Copy Report to Clipboard:** Replaces relative `./assets/...` links with absolute local file paths so you can paste findings directly into NightCodeForge's **Review Room (*Request Changes*)**, **Triage**, or **Ideation** chat.
3. **Send to Ideation:** When NightCodeForge `/api/companion/*` is available and a linked workspace is selected, creates a pre-seeded Ideation conversation (`POST /api/companion/workspaces/{workspaceId}/ideation`) and opens `/w/{workspaceId}/ideation?conversationId={id}` in your browser.
