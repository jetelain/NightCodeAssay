# Plan: Deep Technical Context & Target Executable Discovery

## Objective

Enrich `NightCodeAssay`'s exploratory test reports (`report.md`) with actionable technical context about the **actual application under test**—whether it is a native/managed desktop application or a `localhost`-hosted web application—so developers and **NightCodeForge** agents (Triage, Ideation, Review Room) can immediately identify the binary, exact build/commit version, active configuration (redacted of secrets), and relevant application logs without manual hunting.

---

## 1. Target Executable Resolution

### 1.1 Non-Web / Desktop Applications (`WindowsDesktopContextCollector`)

Currently, `WindowsDesktopContextCollector` obtains the foreground `HWND` and queries `process.MainModule?.FileName`, which has several limitations (`DesktopContext.ExecutablePath` is not rendered in `ReportWriter`, `MainModule` fails on elevated processes, `dotnet run` resolves to `dotnet.exe`, and clicking the Assay UI button can briefly capture Assay's own window).

**Planned Enhancements:**

1. **Z-Order Foreground Fallback (Ignore Assay's Own Process):**
   - When `GetForegroundWindow()` belongs to `Environment.ProcessId` (or an invisible/cloaked shell window), walk top-level visible windows in Z-order via `GetWindow(hwnd, GW_HWNDNEXT)` + `IsWindowVisible` to find the actual target window underneath Assay.
2. **NativeAOT-Safe Image Path Resolution (`QueryFullProcessImageNameW`):**
   - Open the process with `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, ...)` (`0x1000`) and call `QueryFullProcessImageNameW` (`kernel32.dll`) as the primary mechanism before falling back to `Process.MainModule?.FileName`.
3. **Unwrapping Host & Wrapper Processes:**
   - **`dotnet.exe` Host Unwrapping:** When the resolved image is `dotnet.exe` (e.g., `dotnet run` or `dotnet exec MyApp.dll`), inspect the process command line and working directory (via Win32 `NtQueryInformationProcess` / PEB command-line read on Windows, or `/proc/<pid>/cmdline` and `/proc/<pid>/cwd` on Linux) to extract:
     - The target `.dll` or `.exe` path (e.g., `bin/Debug/net10.0/MyApp.dll`), or
     - The `--project` / working directory `.csproj` and its corresponding build output assembly.
   - **`msedgewebview2.exe` Child Window Unwrapping:** If the foreground window is an embedded WebView2 process, resolve its parent PID (`ParentProcessId`) to attribute the capture to the host desktop application.

---

### 1.2 Web Applications on `localhost` (`LocalPortProcessResolver`)

When testing a web application via Edge, Chrome, or Firefox, `DesktopContext` points to the browser executable (`msedge.exe`, `chrome.exe`, `firefox.exe`). If the web app is hosted locally, we can map the target TCP port(s) directly to the backend server process—mirroring `OllamaRouter`'s `LocalClientIdentityResolver`, but looking up the **listening server** (`LISTEN`) instead of the client connection.

**Planned Enhancements:**

1. **Extracting Candidate Loopback Ports:**
   - Parse `WebContext.ActiveUrl` (from CDP or Firefox WebDriver BiDi). If the host is loopback (`localhost`, `127.0.0.1`, `[::1]`, or `*.localhost`), extract its port (explicit port, or `80` / `443` default).
   - **Frontend Dev Server + Backend API Correlation:** Modern web apps often run a SPA dev server on one port (e.g., `http://localhost:5173` $\rightarrow$ `node.exe`) while making API calls to a backend on another port (e.g., `http://localhost:5000/api/...` $\rightarrow$ `.NET` Kestrel process).
   - Extend `CdpContextCollector` to record loopback `host:port` origins observed in recent network requests and HTTP errors (`Network.responseReceived`, `Network.loadingFailed`, `network.responseCompleted`, `network.fetchError`), alongside `ActiveUrl`.
2. **Platform Port-to-PID Resolution (Zero Process Spawning, NativeAOT-Safe):**
   - **Windows (`iphlpapi.dll` — `GetExtendedTcpTable`):**
     - Query `TCP_TABLE_OWNER_PID_LISTENER (3)` for both `AF_INET` (`2`) and `AF_INET6` (`23`) (since Kestrel / ASP.NET Core frequently binds dual-stack IPv6 `[::]:port`).
     - Match rows with `dwState == MIB_TCP_STATE_LISTEN (2)` and `ntohs(dwLocalPort) == targetPort` to retrieve `dwOwningPid`.
   - **Linux (`/proc/net/tcp` & `/proc/net/tcp6`):**
     - Scan for rows with state `st == "0A"` (`TCP_LISTEN`) and local port matching `targetPort`, extract the socket `inode`, and scan `/proc/<pid>/fd` (with a bounded 200 ms time budget, identical to `OllamaRouter`).
3. **Prioritizing Application Binaries & Handling System Proxies:**
   - If multiple loopback ports are active (e.g., `node.exe` on `5173` and `MyBackend.Api.exe` on `5000`), resolve all of them and prioritize managed/.NET or workspace-matching executables over generic dev proxies.
   - Detect known kernel/proxy listeners:
     - `PID == 4` (`System` / `http.sys` used by IIS / IIS Express): report as `System (http.sys / IIS)` and optionally inspect `iisexpress.exe` processes.
     - `wslrelay.exe` / `com.docker.backend.exe`: report that the port is forwarded to WSL2 or a Docker container.

---

## 2. Target Executable Metadata & Binary Profile

Once a target process (`PID`) and its primary executable/entry assembly path (`ExecutablePath` / `EntryAssemblyPath`) are identified (from Desktop, Web `localhost` port, or both), collect a `TargetExecutableInfo` snapshot:

| Property | Source | Purpose in Report |
| :--- | :--- | :--- |
| **`ProcessId` & `ProcessName`** | `Process.GetProcessById(pid)` | Identifies the exact OS process instance. |
| **`ExecutablePath`** | `QueryFullProcessImageNameW` | Full path to the `.exe` binary. |
| **`EntryAssemblyPath`** | Unwrapped `.dll`/`.exe` when hosted by `dotnet.exe` | Points to the actual compiled application binary (`bin/Debug/net10.0/MyApp.dll`) instead of `C:\Program Files\dotnet\dotnet.exe`. |
| **`WorkingDirectory`** | Process PEB / `/proc/<pid>/cwd` or `Path.GetDirectoryName(EntryAssemblyPath)` | Base directory for relative config and log paths. |
| **`FileLastWriteUtc`** | `File.GetLastWriteTimeUtc(EntryAssemblyPath ?? ExecutablePath)` | **Build timestamp** — immediately reveals if the tester is running a stale build vs. newly compiled code. |
| **`ProductVersion` & `FileVersion`** | `FileVersionInfo.GetVersionInfo(...)` | **Exact version & commit SHA** — .NET 8/9/10 SDK builds automatically embed `+<git-commit-sha>` in `ProductVersion` (`InformationalVersion`), e.g., `1.2.0+a1b2c3d4`. |
| **`BuildConfiguration`** | Path heuristic (`\bin\Debug\`, `\bin\Release\`, `\publish\`) | Indicates Debug vs. Release vs. Published build. |
| **`InferredRepositoryPath` & `InferredBranch`** | Walk up parent directories from `EntryAssemblyPath` / `WorkingDirectory` (up to 6 levels) looking for `.git/HEAD` | Automatically discovers the Git repository and active branch **even in Standalone Mode (`<Unlinked / Standalone>`)**. |
| **`ProcessStartedAt` & `WorkingSetBytes`** | `process.StartTime`, `process.WorkingSet64` | Process uptime and memory footprint at capture time. |
| **`ResolutionSource`** | `ForegroundWindow` \| `LocalhostPort(5270)` \| `CdpNetworkPort(5000)` | Explains *how* Assay located the executable. |

---

## 3. Convention-Based Discovery of Logs & Configuration

When `TargetExecutableInfo` is available, Assay can discover logs and configuration files by convention—even when no NightCodeForge workspace is selected or when the binary runs outside `<RepositoryPath>`.

### 3.1 Convention-Based Log Discovery (`WorkspaceLogTailer`)

Extend `WorkspaceLogTailer.DiscoverLogFiles(...)` to accept `TargetExecutableInfo?` and probe candidate directories in priority order:

1. **Explicitly Configured Globs:** `AssayOptions.LogFilePaths` (existing behavior).
2. **Executable & Entry Assembly Directories:**
   - `<EntryAssemblyDir>/logs/*.log`, `<EntryAssemblyDir>/log/*.log`, `<EntryAssemblyDir>/*.log`
   - `<WorkingDirectory>/logs/*.log`, `<WorkingDirectory>/App_Data/logs/*.log`
3. **Inferred Project & Repository Roots:**
   - If `<EntryAssemblyDir>` is inside `bin/{Debug|Release}/...`, walk up to the project root (directory containing `*.csproj`) and inferred repository root (`.git`), scanning `<ProjectRoot>/logs/` and `<InferredRepoRoot>/**/logs/` (up to depth 4).
4. **Standard OS Application Data Conventions (using `<AppName>` from `EntryAssembly` / `ProcessName`):**
   - `%LOCALAPPDATA%\<AppName>\logs\*.log`
   - `%LOCALAPPDATA%\Programs\<AppName>\logs\*.log` *(convention used by NightCodeForge and OllamaRouter)*
   - `%APPDATA%\<AppName>\logs\*.log`
5. **Supported Extensions & Filtering:**
   - Support `*.log`, `*.clef`, and `*.jsonl` (Serilog compact/JSON logs), plus `*.txt` inside dedicated `logs/` folders.
   - Maintain per-file read offsets and the 20-second lookback window (`FileShare.ReadWrite | FileShare.Delete`), and pass extracted log lines through `SensitiveDataRedactor`.

### 3.2 Convention-Based Configuration Discovery (`TargetConfigCollector`)

Create a new `TargetConfigCollector` that locates active configuration files associated with the resolved target executable:

1. **Candidate Search Locations (in order):**
   - `<WorkingDirectory>` (when distinct from `<EntryAssemblyDir>`, e.g., `dotnet run` project folder)
   - `<EntryAssemblyDir>`
   - `<ProjectRoot>` (if `<EntryAssemblyDir>` is under `bin/...`)
   - `%LOCALAPPDATA%\<AppName>` and `%LOCALAPPDATA%\<AppName>\config`
   - `%LOCALAPPDATA%\Programs\<AppName>\config`
2. **Candidate File Conventions:**
   - `appsettings.json` and `appsettings.*.json` (e.g., `appsettings.Development.json`, `appsettings.Local.json`, `appsettings.Production.json`)
   - `.env` (redacted key-value summary)
3. **Size & Context Budgeting:**
   - Avoid bloating `report.md` if an application has huge configuration files or if multiple captures are taken against the same process in one session:
     - Cap inline configuration snippets in `report.md` (e.g., max 80 lines per JSON file / 60 lines per `.env` file).
     - Emit the redacted configuration inline once per unique target binary on the first capture that observes it, and reference that capture on subsequent captures unless the configuration changes.

---

## 4. Secret & Credential Redaction (`SensitiveDataRedactor`)

All discovered configuration files and tail log lines must pass through a deterministic, zero-allocation-friendly `SensitiveDataRedactor` before being written to `report.md` or sent to any external endpoint:

1. **JSON Property Key Matching (`appsettings*.json`, `*.json`):**
   - Parse with `JsonNode` / `Utf8JsonReader` and recursively inspect property names.
   - Redact values to `"[REDACTED]"` whenever the property name matches (case-insensitive token/substring):
     - `key` (e.g., `ApiKey`, `DefaultKey`, `SigningKey`, `EncryptionKey`, `PrivateKey`, `AccessKey` — while keeping non-secret UI keys like `Hotkey` or `KeyBinding` on a safe allowlist!)
     - `secret`, `password`, `passwd`, `pwd`, `token`, `jwt`, `bearer`, `credential`, `authorization`, `pat`, `webhook`, `connectionstring` (handled via rule #2), `pin`, `certificate`, `clientsecret`.
2. **Smart Connection String Sanitization:**
   - For properties inside `"ConnectionStrings"` or string values matching key-value connection string syntax (`Server=...;...` or `Data Source=...;...`):
     - Redact only sensitive segments (`Password=...`, `Pwd=...`, `AccountKey=...`, `SharedAccessKey=...`, `Secret=...`, `Token=...` $\rightarrow$ `Password=[REDACTED]`) while preserving `Server`, `Host`, `Database`, `Data Source`, and `Provider` so Triage agents can still see which database topology is in use.
3. **Regex / Pattern Redaction (Applied to All Config Strings, `.env`, and Log Lines):**
   - **URLs with userinfo:** `https://user:pass@host` $\rightarrow$ `https://[REDACTED]@host`
   - **Auth headers:** `Authorization:\s*(Bearer|Basic)\s+\S+` $\rightarrow$ `Authorization: $1 [REDACTED]`
   - **Known API key / token prefixes:**
     - OpenAI / Anthropic: `sk-[A-Za-z0-9_-]{16,}`
     - GitHub PATs: `(gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,})`
     - GitLab / Slack / Google / AWS: `glpat-[A-Za-z0-9_-]{16,}`, `xox[baprs]-[A-Za-z0-9-]{10,}`, `AIza[0-9A-Za-z_-]{30,}`, `AKIA[0-9A-Z]{16}`
     - JWTs: `eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}`

---

## 5. Additional High-Value Context Improvements

1. **Include HTTP Method on CDP Network Errors:**
   - Correlate `Network.requestWillBeSent` (`requestId` $\rightarrow$ `method`) with `Network.responseReceived` / `Network.loadingFailed` in `CdpContextCollector` so failed requests are logged as `[HTTP 500 Internal Server Error] POST http://localhost:5270/api/items` instead of omitting the HTTP verb.
2. **Fallback Git Repository & Branch Header Update:**
   - If a session starts in `<Unlinked / Standalone>` mode (where `SessionMetadata.RepositoryPath` is initially empty), and a capture resolves a target executable with an `InferredRepositoryPath` and `InferredBranch`, display the inferred repository and branch in the capture's technical context (and optionally use it when staging a NightCodeForge Triage handoff).

---

## 6. Proposed `report.md` Format

```markdown
## #1 [18:05:12] [Bug] Clicking "Save" returns an error banner
- **Tester Observation:** Clicking "Save" returns an error banner
- **Screenshot:** ![Capture 1](./assets/capture_001.png)
- **Visual Analysis (VLM):** ...
- **Technical Context:**
  - **Target App / Window:** `msedge` — "Settings - MyWebApp" (`C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe`)
  - **Web URL (if applicable):** `http://localhost:5270/settings`
  - **Resolved Target Executable (`localhost:5270`):**
    - **Process:** `MyWebApp.Server` (PID `24180`)
    - **Executable / Assembly:** `C:\repos\MyWebApp\src\MyWebApp.Server\bin\Debug\net10.0\MyWebApp.Server.exe`
    - **Version / Build:** `1.2.0+9c4e1a2b` | Built: `2026-11-14 17:42:10 UTC` (`Debug`) | Uptime: `14m 22s` | RAM: `142 MB`
    - **Repository / Branch:** `C:\repos\MyWebApp` (`feat/settings-redesign`)
  - **Target Configuration (`appsettings.Development.json`, redacted):**
    ```json
    {
      "ConnectionStrings": {
        "Default": "Data Source=app.db;Password=[REDACTED]"
      },
      "FeatureFlags": {
        "EnableNewSettingsUi": true
      },
      "ExternalApi": {
        "Endpoint": "https://api.example.com",
        "ApiKey": "[REDACTED]"
      }
    }
    ```
  - **Console / Network Errors (CDP):**
    ```text
    [HTTP 500 Internal Server Error] POST http://localhost:5270/api/settings
    ```
  - **Application / Windows Logs:**
    ```text
    [app-20261114.log] [18:05:11.842 ERR] System.InvalidOperationException: Cannot update read-only setting...
    ```
```

*(Note: For non-web desktop tests, **Resolved Target Executable** directly describes the foreground desktop application rather than a `localhost` port listener, avoiding duplicate lines.)*

---

## 7. Resolved Design Decisions

1. **Configuration Deduplication Strategy (Option A):**
   - Emit the redacted configuration block inline on the *first* capture in a session that encounters a given target binary (`PrimaryBinaryPath`), and on subsequent captures emit `Same as Capture #<Index> (no config file changes detected)` unless any discovered config file's `LastWriteTimeUtc` or redacted content changed.
2. **`dotnet.exe` Command-Line Inspection on Windows (Windows 11+):**
   - Use `NtQueryInformationProcess` (`ntdll.dll`) with `ProcessCommandLineInformation` (`INFOCLASS 60`) and `ProcessBasicInformation` (`INFOCLASS 0`) via `PROCESS_QUERY_LIMITED_INFORMATION` (`0x1000`).
3. **Configuration File Scope:**
   - Collect only `appsettings.json`, `appsettings.*.json`, and `.env`.
