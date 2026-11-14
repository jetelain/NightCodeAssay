using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using NightCodeAssay.Core.Models;
using NightCodeAssay.Core.Services;

namespace NightCodeAssay.Infrastructure.Context;

/// <summary>
/// Inspects a target local process by PID to resolve its executable path, unwrap host processes
/// (<c>dotnet.exe</c>, <c>msedgewebview2.exe</c>), extract build/version metadata, infer Git
/// repository context, and collect redacted convention-based configuration files.
/// </summary>
public sealed class TargetProcessInspector
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessBasicInformationClass = 0;
    private const int ProcessCommandLineInformationClass = 60;

    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    private const int StatusBufferOverflow = unchecked((int)0x80000005);

    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "chromium", "firefox", "brave", "vivaldi", "opera"
    };

    private static readonly HashSet<string> SystemOrShellProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost",
        "ApplicationFrameHost", "TextInputHost", "dwm", "csrss", "svchost", "idle",
        "System", "LockApp", "ShellHost"
    };

    private static readonly HashSet<string> ProxyOrRelayProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "wslrelay", "com.docker.backend", "vmmem", "vmmemWSL"
    };

    private static readonly HashSet<string> DotnetToolingAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet", "csc", "vbc", "msbuild", "vstest.console", "testhost", "testhost.net"
    };

    private static readonly HashSet<string> DotnetValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--depsfile", "--runtimeconfig", "--additional-deps", "--additionalprobingpath",
        "--fx-version", "--roll-forward", "--arch", "--os"
    };

    private readonly TargetConfigCollector _configCollector;
    private readonly ILogger<TargetProcessInspector> _logger;

    public TargetProcessInspector(
        TargetConfigCollector configCollector,
        ILogger<TargetProcessInspector> logger)
    {
        _configCollector = configCollector ?? throw new ArgumentNullException(nameof(configCollector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public static bool IsBrowserProcessName(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) &&
        BrowserProcessNames.Contains(StripExeExtension(processName));

    public static bool IsSystemOrShellProcessName(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) &&
        SystemOrShellProcessNames.Contains(StripExeExtension(processName));

    public static bool IsProxyOrRelayProcessName(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) &&
        ProxyOrRelayProcessNames.Contains(StripExeExtension(processName));

    public TargetExecutableInfo? InspectProcess(
        int pid,
        string resolutionSource,
        string? fallbackProcessName = null,
        string? fallbackExecutablePath = null)
    {
        if (pid <= 0)
        {
            return null;
        }

        if (pid == 4)
        {
            return new TargetExecutableInfo(
                ProcessId: 4,
                ProcessName: "System (http.sys / IIS)",
                ExecutablePath: string.Empty,
                EntryAssemblyPath: string.Empty,
                WorkingDirectory: string.Empty,
                FileLastWriteUtc: null,
                ProductVersion: null,
                FileVersion: null,
                BuildConfiguration: null,
                InferredRepositoryPath: null,
                InferredBranch: null,
                ProcessStartedAt: null,
                WorkingSetBytes: null,
                ResolutionSource: resolutionSource,
                ConfigurationFiles: []);
        }

        try
        {
            var effectivePid = UnwrapWebViewHostPidIfNeeded(pid);
            using var process = TryGetProcessById(effectivePid);

            var processName = process?.ProcessName;
            if (string.IsNullOrWhiteSpace(processName))
            {
                processName = !string.IsNullOrWhiteSpace(fallbackProcessName) ? fallbackProcessName : "unknown";
            }

            var executablePath = TryGetProcessImagePath(effectivePid);
            if (string.IsNullOrWhiteSpace(executablePath) && process is not null)
            {
                try
                {
                    executablePath = process.MainModule?.FileName ?? string.Empty;
                }
                catch
                {
                    executablePath = string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(executablePath))
            {
                executablePath = fallbackExecutablePath ?? string.Empty;
            }

            var processWorkingDir = TryGetProcessWorkingDirectory(effectivePid);
            var commandLine = TryGetProcessCommandLine(effectivePid);

            var entryAssemblyPath = string.Empty;
            var workingDirectory = processWorkingDir ?? string.Empty;

            if (IsDotnetHost(executablePath, processName))
            {
                var (resolvedEntry, resolvedWorkDir) = ResolveDotnetTarget(commandLine, processWorkingDir);
                if (!string.IsNullOrWhiteSpace(resolvedEntry))
                {
                    entryAssemblyPath = resolvedEntry;
                    var resolvedAppName = Path.GetFileNameWithoutExtension(resolvedEntry);
                    if (!string.IsNullOrWhiteSpace(resolvedAppName))
                    {
                        processName = resolvedAppName;
                    }
                }

                if (!string.IsNullOrWhiteSpace(resolvedWorkDir))
                {
                    workingDirectory = resolvedWorkDir;
                }
            }

            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                workingDirectory = SafeGetDirectoryName(!string.IsNullOrWhiteSpace(entryAssemblyPath)
                    ? entryAssemblyPath
                    : executablePath) ?? string.Empty;
            }

            var versionBinaryPath = ResolveVersionInspectionPath(executablePath, entryAssemblyPath);
            var (fileLastWriteUtc, productVersion, fileVersion) = ExtractBinaryVersionMetadata(
                versionBinaryPath,
                executablePath);

            var primaryPath = !string.IsNullOrWhiteSpace(entryAssemblyPath) ? entryAssemblyPath : executablePath;
            var buildConfig = InferBuildConfiguration(primaryPath);
            var (inferredRepoPath, inferredBranch) = InferGitRepository(primaryPath, workingDirectory);

            DateTimeOffset? processStartedAt = null;
            long? workingSetBytes = null;
            if (process is not null)
            {
                try
                {
                    processStartedAt = new DateTimeOffset(process.StartTime);
                }
                catch
                {
                    // Protected/system processes may deny StartTime access.
                }

                try
                {
                    workingSetBytes = process.WorkingSet64;
                }
                catch
                {
                    // Ignore inaccessible memory metrics.
                }
            }

            IReadOnlyList<TargetConfigFileSnapshot> configFiles = [];
            if (!IsBrowserProcessName(processName) &&
                !IsSystemOrShellProcessName(processName) &&
                !IsProxyOrRelayProcessName(processName))
            {
                configFiles = _configCollector.CollectConfigurations(
                    executablePath,
                    entryAssemblyPath,
                    workingDirectory,
                    inferredRepoPath);
            }

            return new TargetExecutableInfo(
                ProcessId: effectivePid,
                ProcessName: processName,
                ExecutablePath: executablePath,
                EntryAssemblyPath: entryAssemblyPath,
                WorkingDirectory: workingDirectory,
                FileLastWriteUtc: fileLastWriteUtc,
                ProductVersion: productVersion,
                FileVersion: fileVersion,
                BuildConfiguration: buildConfig,
                InferredRepositoryPath: inferredRepoPath,
                InferredBranch: inferredBranch,
                ProcessStartedAt: processStartedAt,
                WorkingSetBytes: workingSetBytes,
                ResolutionSource: resolutionSource,
                ConfigurationFiles: configFiles);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to inspect process {Pid}.", pid);
            return null;
        }
    }

    public static string? TryGetProcessImagePath(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return WindowsGetProcessImagePath(pid);
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                return new FileInfo($"/proc/{pid}/exe").LinkTarget;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    internal static (string? EntryAssemblyPath, string? WorkingDirectory) ResolveDotnetTarget(
        string? commandLine,
        string? processWorkingDir,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return (null, processWorkingDir);
        }

        fileExists ??= File.Exists;
        directoryExists ??= Directory.Exists;

        var args = SplitCommandLineArgs(commandLine);
        if (args.Count == 0)
        {
            return (null, processWorkingDir);
        }

        var index = 0;
        var firstFileName = SafeGetFileNameWithoutExtension(args[0]);
        if (string.Equals(firstFileName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            index = 1;
        }

        if (index >= args.Count)
        {
            return (null, processWorkingDir);
        }

        // Handle `dotnet run [--project|-p <path>]` and `dotnet watch [run] [--project|-p <path>]`.
        var commandToken = args[index];
        var isRun = string.Equals(commandToken, "run", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(commandToken, "watch", StringComparison.OrdinalIgnoreCase);

        if (isRun)
        {
            var projectArg = ExtractOptionValue(args, index + 1, "--project", "-p");
            var configArg = ExtractOptionValue(args, index + 1, "--configuration", "-c");
            return ResolveDotnetRunProjectTarget(projectArg, configArg, processWorkingDir, fileExists, directoryExists);
        }

        for (var i = index; i < args.Count; i++)
        {
            var token = args[i];
            if (DotnetValueOptions.Contains(token))
            {
                i++;
                continue;
            }

            if (token.StartsWith('-'))
            {
                continue;
            }

            if (string.Equals(token, "exec", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!token.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var assemblyName = SafeGetFileNameWithoutExtension(token);
            if (string.IsNullOrWhiteSpace(assemblyName) || DotnetToolingAssemblies.Contains(assemblyName))
            {
                continue;
            }

            var candidatePath = ResolveCandidatePath(token, processWorkingDir);
            if (!string.IsNullOrWhiteSpace(candidatePath) && fileExists(candidatePath))
            {
                var workDir = !string.IsNullOrWhiteSpace(processWorkingDir)
                    ? processWorkingDir
                    : SafeGetDirectoryName(candidatePath);
                return (candidatePath, workDir);
            }
        }

        return (null, processWorkingDir);
    }

    internal static IReadOnlyList<string> SplitCommandLineArgs(string commandLine)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return args;
        }

        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var ch = commandLine[i];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    internal static string? InferBuildConfiguration(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.Contains("/publish/", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/publish", StringComparison.OrdinalIgnoreCase))
        {
            return "Publish";
        }

        if (normalized.Contains("/bin/Debug/", StringComparison.OrdinalIgnoreCase))
        {
            return "Debug";
        }

        if (normalized.Contains("/bin/Release/", StringComparison.OrdinalIgnoreCase))
        {
            return "Release";
        }

        return null;
    }

    internal static (string? RepositoryPath, string? Branch) InferGitRepository(
        string? primaryBinaryPath,
        string? workingDirectory)
    {
        foreach (var startPath in new[] { primaryBinaryPath, workingDirectory })
        {
            if (string.IsNullOrWhiteSpace(startPath))
            {
                continue;
            }

            try
            {
                var dir = Directory.Exists(startPath)
                    ? new DirectoryInfo(startPath)
                    : new FileInfo(startPath).Directory;

                for (var depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
                {
                    var gitMarker = Path.Combine(dir.FullName, ".git");
                    if (Directory.Exists(gitMarker) || File.Exists(gitMarker))
                    {
                        var branch = CapturePipelineService.ResolveGitBranch(dir.FullName);
                        return (dir.FullName, branch);
                    }
                }
            }
            catch
            {
                // Ignore invalid paths or permission errors.
            }
        }

        return (null, null);
    }

    private static (string? EntryAssemblyPath, string? WorkingDirectory) ResolveDotnetRunProjectTarget(
        string? projectArg,
        string? configArg,
        string? processWorkingDir,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists)
    {
        var candidate = !string.IsNullOrWhiteSpace(projectArg)
            ? ResolveCandidatePath(projectArg, processWorkingDir)
            : processWorkingDir;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return (null, processWorkingDir);
        }

        string? projectDir = null;
        string? projectName = null;

        if (candidate.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && fileExists(candidate))
        {
            projectDir = SafeGetDirectoryName(candidate);
            projectName = Path.GetFileNameWithoutExtension(candidate);
        }
        else if (directoryExists(candidate))
        {
            projectDir = candidate;
            try
            {
                var firstCsproj = Directory.EnumerateFiles(projectDir, "*.csproj", SearchOption.TopDirectoryOnly)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstCsproj))
                {
                    projectName = Path.GetFileNameWithoutExtension(firstCsproj);
                }
            }
            catch
            {
                // Ignore unreadable project directory.
            }
        }

        if (string.IsNullOrWhiteSpace(projectDir))
        {
            return (null, processWorkingDir);
        }

        var binDir = Path.Combine(projectDir, "bin");
        if (!directoryExists(binDir) || string.IsNullOrWhiteSpace(projectName))
        {
            return (null, projectDir);
        }

        try
        {
            var searchRoot = !string.IsNullOrWhiteSpace(configArg) && directoryExists(Path.Combine(binDir, configArg))
                ? Path.Combine(binDir, configArg)
                : binDir;

            var newestDll = Directory.EnumerateFiles(searchRoot, $"{projectName}.dll", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                            !p.Contains($"{Path.DirectorySeparatorChar}refint{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (newestDll is not null)
            {
                return (newestDll.FullName, projectDir);
            }
        }
        catch
        {
            // Ignore search errors in bin directory.
        }

        return (null, projectDir);
    }

    private static string? ExtractOptionValue(IReadOnlyList<string> args, int startIndex, params string[] optionNames)
    {
        for (var i = startIndex; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--", StringComparison.Ordinal))
            {
                break;
            }

            foreach (var option in optionNames)
            {
                if (string.Equals(arg, option, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Count)
                    {
                        return args[i + 1];
                    }
                }
                else if (arg.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))
                {
                    return arg[(option.Length + 1)..];
                }
            }
        }

        return null;
    }

    private static string? ResolveCandidatePath(string token, string? workingDir)
    {
        try
        {
            if (Path.IsPathRooted(token))
            {
                return Path.GetFullPath(token);
            }

            if (!string.IsNullOrWhiteSpace(workingDir))
            {
                return Path.GetFullPath(Path.Combine(workingDir, token));
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveVersionInspectionPath(string executablePath, string entryAssemblyPath)
    {
        if (!string.IsNullOrWhiteSpace(entryAssemblyPath) && File.Exists(entryAssemblyPath))
        {
            return entryAssemblyPath;
        }

        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                var dir = Path.GetDirectoryName(executablePath);
                var nameWithoutExt = Path.GetFileNameWithoutExtension(executablePath);
                if (!string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(nameWithoutExt))
                {
                    var siblingDll = Path.Combine(dir, $"{nameWithoutExt}.dll");
                    var runtimeConfig = Path.Combine(dir, $"{nameWithoutExt}.runtimeconfig.json");
                    if (File.Exists(siblingDll) && File.Exists(runtimeConfig))
                    {
                        return siblingDll;
                    }
                }
            }
            catch
            {
                // Ignore filesystem errors.
            }
        }

        return executablePath;
    }

    internal static (DateTimeOffset? LastWriteUtc, string? ProductVersion, string? FileVersion) ExtractBinaryVersionMetadata(
        string primaryInspectionPath,
        string fallbackExecutablePath)
    {
        foreach (var path in new[] { primaryInspectionPath, fallbackExecutablePath })
        {
            if (string.IsNullOrWhiteSpace(path) || IsDotnetHost(path, null) || !File.Exists(path))
            {
                continue;
            }

            try
            {
                var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                var versionInfo = FileVersionInfo.GetVersionInfo(path);
                var productVersion = string.IsNullOrWhiteSpace(versionInfo.ProductVersion)
                    ? null
                    : versionInfo.ProductVersion.Trim();
                var fileVersion = string.IsNullOrWhiteSpace(versionInfo.FileVersion)
                    ? null
                    : versionInfo.FileVersion.Trim();

                if (productVersion is null &&
                    !string.IsNullOrWhiteSpace(fallbackExecutablePath) &&
                    !IsDotnetHost(fallbackExecutablePath, null) &&
                    !string.Equals(path, fallbackExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(fallbackExecutablePath))
                {
                    var fallbackInfo = FileVersionInfo.GetVersionInfo(fallbackExecutablePath);
                    productVersion = string.IsNullOrWhiteSpace(fallbackInfo.ProductVersion)
                        ? null
                        : fallbackInfo.ProductVersion.Trim();
                    fileVersion ??= string.IsNullOrWhiteSpace(fallbackInfo.FileVersion)
                        ? null
                        : fallbackInfo.FileVersion.Trim();
                }

                return (lastWrite, productVersion, fileVersion);
            }
            catch
            {
                // Try fallback path.
            }
        }

        return (null, null, null);
    }

    private static int UnwrapWebViewHostPidIfNeeded(int pid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return pid;
        }

        var currentPid = pid;
        for (var hop = 0; hop < 3; hop++)
        {
            using var proc = TryGetProcessById(currentPid);
            if (proc is null)
            {
                break;
            }

            if (!string.Equals(proc.ProcessName, "msedgewebview2", StringComparison.OrdinalIgnoreCase))
            {
                return currentPid;
            }

            var parentPid = WindowsGetParentProcessId(currentPid);
            if (parentPid is null or <= 0 || parentPid.Value == currentPid)
            {
                break;
            }

            using var parentProc = TryGetProcessById(parentPid.Value);
            if (parentProc is null || IsSystemOrShellProcessName(parentProc.ProcessName))
            {
                break;
            }

            try
            {
                if (parentProc.StartTime > proc.StartTime)
                {
                    break;
                }
            }
            catch
            {
                // Protected/system processes may deny StartTime access.
            }

            currentPid = parentPid.Value;
        }

        return currentPid;
    }

    private static string? TryGetProcessCommandLine(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return WindowsGetProcessCommandLine(pid);
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var bytes = File.ReadAllBytes($"/proc/{pid}/cmdline");
                if (bytes.Length == 0)
                {
                    return null;
                }

                var sb = new StringBuilder(bytes.Length);
                var start = 0;
                for (var i = 0; i <= bytes.Length; i++)
                {
                    if (i == bytes.Length || bytes[i] == 0)
                    {
                        if (i > start)
                        {
                            var arg = Encoding.UTF8.GetString(bytes, start, i - start);
                            if (sb.Length > 0)
                            {
                                sb.Append(' ');
                            }

                            if (arg.Contains(' '))
                            {
                                sb.Append('"').Append(arg).Append('"');
                            }
                            else
                            {
                                sb.Append(arg);
                            }
                        }

                        start = i + 1;
                    }
                }

                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static string? TryGetProcessWorkingDirectory(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return WindowsGetProcessWorkingDirectory(pid);
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                return new FileInfo($"/proc/{pid}/cwd").LinkTarget;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static Process? TryGetProcessById(int pid)
    {
        try
        {
            return Process.GetProcessById(pid);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsDotnetHost(string? executablePath, string? processName)
    {
        if (string.Equals(processName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var exeName = SafeGetFileNameWithoutExtension(executablePath);
        return string.Equals(exeName, "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripExeExtension(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static string? SafeGetFileNameWithoutExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFileNameWithoutExtension(path);
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeGetDirectoryName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(path);
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? WindowsGetProcessImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == nint.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            if (QueryFullProcessImageNameW(handle, 0, buffer, ref size) && size > 0)
            {
                return new string(buffer, 0, (int)size);
            }

            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [SupportedOSPlatform("windows")]
    private static int? WindowsGetParentProcessId(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == nint.Zero)
        {
            return null;
        }

        try
        {
            // PROCESS_BASIC_INFORMATION contains 6 pointer-sized fields.
            var size = (uint)(IntPtr.Size * 6);
            var ptr = Marshal.AllocHGlobal((int)size);
            try
            {
                var status = NtQueryInformationProcess(handle, ProcessBasicInformationClass, ptr, size, out _);
                if (status != 0)
                {
                    return null;
                }

                // InheritedFromUniqueProcessId is the 6th pointer-sized field in PROCESS_BASIC_INFORMATION.
                var parentPidOffset = IntPtr.Size * 5;
                var parentPid = Marshal.ReadIntPtr(ptr, parentPidOffset);
                return parentPid > 0 ? (int)parentPid : null;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? WindowsGetProcessCommandLine(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == nint.Zero)
        {
            return null;
        }

        try
        {
            uint bufferSize = 4096;
            var ptr = Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                var status = NtQueryInformationProcess(
                    handle,
                    ProcessCommandLineInformationClass,
                    ptr,
                    bufferSize,
                    out var requiredSize);

                if ((status == StatusInfoLengthMismatch || status == StatusBufferTooSmall || status == StatusBufferOverflow) &&
                    requiredSize > bufferSize &&
                    requiredSize <= 128 * 1024)
                {
                    Marshal.FreeHGlobal(ptr);
                    bufferSize = requiredSize;
                    ptr = Marshal.AllocHGlobal((int)bufferSize);
                    status = NtQueryInformationProcess(
                        handle,
                        ProcessCommandLineInformationClass,
                        ptr,
                        bufferSize,
                        out requiredSize);
                }

                if (status != 0)
                {
                    return null;
                }

                var byteLength = (ushort)Marshal.ReadInt16(ptr, 0);
                var strPtr = Marshal.ReadIntPtr(ptr, IntPtr.Size);
                if (byteLength == 0 || strPtr == nint.Zero)
                {
                    return null;
                }

                var baseAddr = ptr.ToInt64();
                var strAddr = strPtr.ToInt64();
                if (strAddr < baseAddr || strAddr + byteLength > baseAddr + bufferSize)
                {
                    return null;
                }

                return Marshal.PtrToStringUni(strPtr, byteLength / 2);
            }
            finally
            {
                if (ptr != nint.Zero)
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [SupportedOSPlatform("windows")]
    internal static string? WindowsGetProcessWorkingDirectory(int pid)
    {
        if (!Environment.Is64BitProcess || IntPtr.Size != 8)
        {
            return null;
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, pid);
        if (handle == nint.Zero)
        {
            return null;
        }

        try
        {
            if (IsWow64Process(handle, out var isWow64) && isWow64)
            {
                return null;
            }

            var pbiSize = (uint)(IntPtr.Size * 6);
            var pbiPtr = Marshal.AllocHGlobal((int)pbiSize);
            nint pebAddress;
            try
            {
                var status = NtQueryInformationProcess(handle, ProcessBasicInformationClass, pbiPtr, pbiSize, out _);
                if (status != 0)
                {
                    return null;
                }

                // PebBaseAddress is the 2nd pointer-sized field in PROCESS_BASIC_INFORMATION.
                pebAddress = Marshal.ReadIntPtr(pbiPtr, IntPtr.Size);
            }
            finally
            {
                Marshal.FreeHGlobal(pbiPtr);
            }

            if (pebAddress == nint.Zero)
            {
                return null;
            }

            // PEB.ProcessParameters is at offset 0x20 on 64-bit Windows.
            var ptrBuffer = new byte[8];
            if (!ReadProcessMemory(handle, pebAddress + 0x20, ptrBuffer, 8, out var bytesRead) || bytesRead < 8)
            {
                return null;
            }

            var processParametersAddress = (nint)BitConverter.ToInt64(ptrBuffer, 0);
            if (processParametersAddress == nint.Zero)
            {
                return null;
            }

            // RTL_USER_PROCESS_PARAMETERS.CurrentDirectory.DosPath (UNICODE_STRING) is at offset 0x38 on 64-bit Windows.
            var unicodeStringBuffer = new byte[16];
            if (!ReadProcessMemory(handle, processParametersAddress + 0x38, unicodeStringBuffer, 16, out bytesRead) || bytesRead < 16)
            {
                return null;
            }

            var byteLength = BitConverter.ToUInt16(unicodeStringBuffer, 0);
            var stringAddress = (nint)BitConverter.ToInt64(unicodeStringBuffer, 8);
            if (byteLength == 0 || byteLength > 8192 || (byteLength % 2) != 0 || stringAddress == nint.Zero)
            {
                return null;
            }

            var pathBuffer = new byte[byteLength];
            if (!ReadProcessMemory(handle, stringAddress, pathBuffer, byteLength, out bytesRead) || bytesRead < 2)
            {
                return null;
            }

            var rawPath = Encoding.Unicode.GetString(pathBuffer, 0, (int)Math.Min(bytesRead, byteLength)).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(rawPath) ? null : Path.TrimEndingDirectorySeparator(rawPath);
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(nint hProcess, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        nint hProcess,
        nint lpBaseAddress,
        [Out] byte[] lpBuffer,
        nuint nSize,
        out nuint lpNumberOfBytesRead);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        nint hProcess,
        uint dwFlags,
        [Out] char[] lpExeName,
        ref uint lpdwSize);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        nint processHandle,
        int processInformationClass,
        nint processInformation,
        uint processInformationLength,
        out uint returnLength);
}
