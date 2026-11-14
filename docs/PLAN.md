# Project Goal: NightCode Assay (Companion Exploratory Testing Tool for NightCodeForge)

Build a lightweight Windows desktop application in **C# / .NET 10 (Avalonia)** designed as a companion to **NightCodeForge** for manual exploratory software testing (covering both Web and Desktop applications). As a user or maintainer tests delivered changes from the autonomous forge, **NightCode Assay** captures screenshots, tester observations, and rich technical context on the fly, uses a Vision-Language Model (VLM) via a simple `OllamaSharp` or `OpenAI` connector to describe visual UI states/anomalies, and hands off a clean, structured Markdown report directly back into NightCodeForge (via Clipboard, Local Session Files, or 1-Click Ideation Chat creation).

---

## 1. Zero-Duplication Configuration & NightCodeForge Companion Integration

To avoid duplicating LLM/VLM settings and repository paths while keeping `NightCodeAssay` simple and standalone-capable:

1. **OllamaRouter-Style Shared API Key Authentication (`DefaultKeyReader`):**
   - `NightCodeForge` generates and persists a 32-byte base64url default API key in `<ContentRoot>/config/default-key.dat` wrapped at rest via `IKeyProtector` (`ProtectedData` DPAPI `CurrentUser` on Windows, `0600` file permissions on Unix), following the exact same pattern as `OllamaRouter`.
   - `NightCodeAssay` includes a lightweight `DefaultKeyReader` that automatically resolves and unwraps `default-key.dat` using the following precedence:
     1. Explicit `NightCodeForge:ApiKey` in Assay's `appsettings.json` (for remote/Tailscale instances).
     2. Explicit `NightCodeForge:ConfigDirectory` in Assay's `appsettings.json` $\rightarrow$ `<ConfigDirectory>/default-key.dat`.
     3. `NIGHTCODEFORGE_HOME` environment variable $\rightarrow$ `<NIGHTCODEFORGE_HOME>/config/default-key.dat`.
     4. Standard Windows installation path (`%LOCALAPPDATA%\Programs\NightCodeForge\config\default-key.dat`) or sibling development repository (`..\NightCodeForge\src\NightCodeForge.Web\config\default-key.dat`).
   - All requests from Assay to NightCodeForge pass `Authorization: Bearer <key>`.

2. **NightCodeForge Companion HTTP API (`/api/companion/*`):**
   - `GET /api/companion/config`: Returns NightCodeForge's active LLM configuration (`providerType`, `endpoint`, `defaultModelId`, `architectModelId`, and `openAiCompatible` endpoint/API key).
   - `GET /api/companion/workspaces`: Returns registered NightCodeForge workspaces (`id`, `name`, `emoji`, `repositoryPath`, `defaultBaseBranch`) so the tester can pick the workspace under test from a dropdown in Assay.
   - `POST /api/companion/workspaces/{workspaceId}/ideation`: Creates a new Ideation conversation in NightCodeForge pre-seeded with the compiled exploratory `report.md` (and local session asset paths), returning `{ conversationId, url }` so Assay can open the exact Ideation session in the user's browser with one click.

3. **Optional Local Overrides & Standalone Fallback (`appsettings.json`):**
   - Assay stores only minimal preferences in its own `appsettings.json` (`NightCodeForgeUrl` defaulting to `http://localhost:5270`, `Hotkey`, `CdpPort`, `LogFilePaths`, and optional LLM overrides: `ProviderTypeOverride`, `EndpointOverride`, `VisionModelIdOverride`).
   - If `NightCodeForge` is unreachable or not running, Assay falls back cleanly to its local `appsettings.json` overrides (or `http://localhost:11434` Ollama defaults) so testing is never blocked.

---

## 2. User Workflow & Ergonomics (Zero-Friction)

1. **System Tray & Session Control Window (Zero-VRAM Rendering):**
   - Configure Avalonia with software rendering (`Win32RenderingMode.Software`) so `NightCodeAssay` consumes **0 MB of GPU VRAM** and never competes with local LLM/VLM inference.
   - Runs cleanly in the Windows System Tray with a compact control window featuring:
     - **Target Workspace Selector:** Dropdown populated from NightCodeForge (`/api/companion/workspaces`) or `<Unlinked / Standalone>`.
     - **Session Controls:** Start / Stop Session, live captured entry counter, and status indicator for VLM / CDP connectivity.
     - **Handoff & Export Actions:**
       - 📂 **Open Session Folder / Open `report.md`** (`./sessions/YYYY-MM-DD_HH-mm/`).
       - 📋 **Copy Report to Clipboard** (formatted Markdown ready to paste into NightCodeForge Review Room *"Request Changes"*, *Ideation*, or *Triage*).
       - 💡 **Send to NightCodeForge Ideation** (creates an Ideation chat for the selected workspace via `POST /api/companion/workspaces/{id}/ideation` and launches the browser).
   - Starting a session automatically creates `./sessions/YYYY-MM-DD_HH-mm/` containing an `assets/` subfolder and an initialized `report.md` pre-populated with system & workspace metadata (OS version, screen resolution, workspace name/branch, start timestamp).

2. **On-the-Fly Capture via Global Hotkey (`Ctrl+Alt+S`):**
   - Upon pressing the global hotkey (registered via Win32 `RegisterHotKey`), the app **immediately captures** the screen (or active window, configurable) **before** rendering any overlay UI.
   - Concurrently, it gathers technical context in the background (Desktop + Web + Local Logs).
   - It then displays a sleek, borderless floating modal (`Topmost = true`, centered or near the cursor) featuring:
     - A thumbnail preview of the captured screenshot (with basic mouse-drag support to draw a red highlight rectangle on the image, saving both the annotated image or compositing the highlight).
     - An auto-focused text box for the tester's observation (`Enter` to submit, `Escape` to cancel, `Shift+Enter` for a newline).
     - A quick category/severity selector: `[Bug]`, `[UI/UX]`, `[Enhancement]`, `[Question]`.

3. **Non-Blocking Asynchronous Pipeline:**
   - Pressing `Enter` immediately closes the floating modal so the tester's flow is never interrupted.
   - A background worker queue (`Channel<CaptureEntry>`) processes each entry asynchronously: saving the PNG file, querying the VLM for visual description, and appending the formatted entry to `report.md` using thread-safe file locking.

---

## 3. Automatic Technical Context Collection (Hybrid Desktop & Web)

Implement a modular `IContextCollector` service combining three context sources:

1. **Windows Desktop Context (Win32):**
   - Retrieve the foreground window handle (`GetForegroundWindow`), window title (`GetWindowText`), process name (`Process.GetProcessById`), executable path, and window bounds/dimensions.
   - Query recent entries (last 30 seconds) from the Windows Event Log (`System.Diagnostics.EventLog` — `Application` log) filtered by process name or recent error/warning events.

2. **Web Context via Chrome DevTools Protocol (CDP):**
   - Automatically detect if the active foreground process is a Chromium-based browser (`chrome.exe`, `msedge.exe`, `brave.exe`) or poll a configured local debugging port (default: `http://localhost:9222/json`).
   - Connect via lightweight `ClientWebSocket` (avoid heavy dependencies like Playwright) to capture the active tab URL, page title, recent console errors (`Runtime.exceptionThrown`, `Log.entryAdded` with `error` level), and failed network requests (`Network.responseReceived` with HTTP status >= 400).
   - *Implementation note:* Run a lightweight background CDP listener as soon as a test session starts to maintain a circular ring buffer of the last 50 console/network error events so they are immediately available when the hotkey is triggered.

3. **Workspace-Aware Local Log File Tailer:**
   - Allow configuring explicit local log file paths/globs in `appsettings.json`.
   - When a NightCodeForge workspace is selected, automatically discover and tail recent `.log` files in `<RepositoryPath>/logs/` or `<RepositoryPath>/**/logs/*.log` modified during the session.
   - Extract lines appended within the 20 seconds preceding the capture timestamp (reading with `FileShare.ReadWrite`).

---

## 4. Simple VLM Enrichment (`OllamaSharp` & `OpenAI` via `IChatClient`)

Keep the inference layer strictly simple — **no `OllamaRouter` headers, routing targets, or fleet logic**:

1. **Unified `Microsoft.Extensions.AI.IChatClient` Abstraction:**
   - **Ollama / OllamaRouter Provider (`OllamaSharp`):** When NightCodeForge's `ProviderType` is `Ollama` or `OllamaRouter`, instantiate `OllamaApiClient(httpClient, modelId)` directly against the configured `Endpoint` (e.g., `http://localhost:11434`). Both standalone Ollama and OllamaRouter expose the standard Ollama API, so plain `OllamaSharp` works transparently.
   - **OpenAI-Compatible Provider (`Microsoft.Extensions.AI.OpenAI`):** When `ProviderType` is `OpenAiCompatible`, instantiate `new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions).GetChatClient(modelId).AsIChatClient()`.
2. **Model Resolution:**
   - Use `VisionModelIdOverride` from Assay's `appsettings.json` if specified; otherwise use NightCodeForge's `DefaultModelId` (or `ArchitectModelId`).
3. **Multimodal Prompting & Graceful Degradation:**
   - Send a single `ChatMessage(ChatRole.User, [new DataContent(pngBytes, "image/png"), new TextContent(prompt)])` with a strict system prompt instructing the model to provide a factual, technical description (visible UI components, displayed values, enabled/disabled states, visible error messages, layout anomalies, and any red highlight box drawn by the user) correlated with the tester's note, without speculating on source code.
   - Handle timeouts, non-vision models, or offline endpoints gracefully (write `[VLM analysis unavailable]` in the report without ever losing the tester's note, screenshot, or technical context).

---

## 5. Strict Report Output Format (`report.md`)

Structure `report.md` specifically so NightCodeForge's Ideation Architect, Triage Agent, or Review Room can parse it reliably and generate an actionable task list:

```markdown
# Exploratory Test Report — {Date}
- **Workspace:** {WorkspaceName} (`{RepositoryPath}`)
- **Branch:** `{ActiveBranch}`
- **OS:** {OSVersion}
- **Session ID:** {SessionId}

---

## #{Index} [{Timestamp}] [{Category}] {ShortSummary}
- **Tester Observation:** {UserNote}
- **Screenshot:** ![Capture {Index}]({RelativeOrAbsolutePngPath})
- **Visual Analysis (VLM):** {VlmDescription}
- **Technical Context:**
  - **Target App / Window:** `{ProcessName}` — "{WindowTitle}"
  - **Web URL (if applicable):** `{ActiveUrl}`
  - **Console / Network Errors (CDP):**
    ```text
    {CdpLogs_or_None}
    ```
  - **Application / Windows Logs:**
    ```text
    {FileOrEventViewerLogs_or_None}
    ```
```

*(Note: When copying to clipboard or sending to NightCodeForge Ideation, image paths and log snippets are formatted so agents and markdown viewers can reference the exact local artifacts on disk.)*

---

## 6. Architecture & Deliverables

* **Tech Stack:** C# / .NET 10, Avalonia UI 11+ (`Win32RenderingMode.Software`), `CommunityToolkit.Mvvm`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.AI`, `OllamaSharp`, `Microsoft.Extensions.AI.OpenAI`.
* **Solution Structure:**
  - `src/NightCodeAssay.Core`: Domain models, interfaces (`IScreenCaptureService`, `IContextCollector`, `IVisionAnalyzer`, `IReportWriter`, `INightCodeForgeClient`), `DefaultKeyReader` & `IKeyProtector` (DPAPI/Plain), `Channel<CaptureEntry>` background processor, and `IChatClient` VLM analyzer.
  - `src/NightCodeAssay.Infrastructure`: Win32 screen/window capture & global hotkey (`RegisterHotKey`), Win32 foreground window & Windows Event Log collector, CDP WebSocket ring-buffer listener, workspace-aware log file tailer, and NightCodeForge HTTP companion client.
  - `src/NightCodeAssay.App`: Avalonia desktop application (System Tray icon, Session Control Window, and Topmost Quick-Capture Floating Modal with red-rectangle annotation canvas).
  - `tests/NightCodeAssay.Tests`: Unit tests for `DefaultKeyReader`, report formatting, log tailing, VLM fallback handling, and NightCodeForge client integration.
* **Companion Specification in `NightCodeForge`:**
  - Specified in [STEP_12_37_COMPANION_API_KEY_STORE_AND_ASSAY_HANDOFF_ENDPOINTS.md](../../NightCodeForge/docs/features/STEP_12_37_COMPANION_API_KEY_STORE_AND_ASSAY_HANDOFF_ENDPOINTS.md): DPAPI-backed `default-key.dat` store (`IApiKeyStore`) and the `/api/companion/*` endpoints (`config`, `workspaces`, and `ideation` session creation with `?conversationId={id}` deep-link support).
* **Deliverables:**
  1. Scaffold the complete .NET 10 solution in `NightCodeAssay` and implement all classes end-to-end (no stubs or `// TODO` placeholders).
  2. Verify the solution compiles cleanly (`dotnet build`) and all unit tests pass (`dotnet test`).
  3. Write a comprehensive `README.md` (in English) explaining the companion workflow with NightCodeForge, how to launch Chrome/Edge with `--remote-debugging-port=9222`, local overrides, and how reports flow into NightCodeForge Ideation and Review Room.
