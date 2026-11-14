using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using NightCodeAssay.Core.Interfaces;
using NightCodeAssay.Core.Models;

namespace NightCodeAssay.Infrastructure.Browser;

/// <summary>
/// Locates and launches Microsoft Edge, Google Chrome, and Mozilla Firefox with an isolated
/// Assay profile and remote debugging enabled on the configured port.
/// </summary>
public sealed class BrowserLauncherService : IBrowserLauncherService
{
    private readonly ILogger<BrowserLauncherService> _logger;
    private readonly string _profilesRootDirectory;
    private readonly Func<DebugBrowserKind, string?>? _executableResolver;
    private readonly Func<ProcessStartInfo, bool>? _processStarter;

    public BrowserLauncherService(ILogger<BrowserLauncherService> logger)
        : this(logger, GetDefaultProfilesRoot(), executableResolver: null, processStarter: null)
    {
    }

    internal BrowserLauncherService(
        ILogger<BrowserLauncherService> logger,
        string profilesRootDirectory,
        Func<DebugBrowserKind, string?>? executableResolver = null,
        Func<ProcessStartInfo, bool>? processStarter = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _profilesRootDirectory = string.IsNullOrWhiteSpace(profilesRootDirectory)
            ? GetDefaultProfilesRoot()
            : profilesRootDirectory;
        _executableResolver = executableResolver;
        _processStarter = processStarter;
    }

    public string? ResolveBrowserExecutable(DebugBrowserKind browser)
    {
        if (_executableResolver is not null)
        {
            return _executableResolver(browser);
        }

        return FindBrowserExecutable(browser);
    }

    public BrowserLaunchResult LaunchWithDebugPort(DebugBrowserKind browser, int port)
    {
        if (port is <= 0 or > 65535)
        {
            return new BrowserLaunchResult(
                Succeeded: false,
                Browser: browser,
                ExecutablePath: null,
                Arguments: string.Empty,
                Message: string.Create(CultureInfo.InvariantCulture, $"Invalid debug port {port}. Must be between 1 and 65535."));
        }

        var exePath = ResolveBrowserExecutable(browser);
        var displayName = GetDisplayName(browser);
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return new BrowserLaunchResult(
                Succeeded: false,
                Browser: browser,
                ExecutablePath: null,
                Arguments: string.Empty,
                Message: $"{displayName} executable was not found on this machine.");
        }

        try
        {
            var profileDir = PrepareProfileDirectory(browser);
            var arguments = BuildLaunchArguments(browser, port, profileDir);
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false
            };

            bool started;
            if (_processStarter is not null)
            {
                started = _processStarter(startInfo);
            }
            else
            {
                using var process = Process.Start(startInfo);
                started = process is not null;
            }

            if (!started)
            {
                return new BrowserLaunchResult(
                    Succeeded: false,
                    Browser: browser,
                    ExecutablePath: exePath,
                    Arguments: arguments,
                    Message: $"Failed to start {displayName} from '{exePath}'.");
            }

            var protocolHint = browser == DebugBrowserKind.Firefox ? "WebDriver BiDi" : "CDP";
            _logger.LogInformation("Launched {Browser} ({Executable}) on debug port {Port}.", displayName, exePath, port);
            return new BrowserLaunchResult(
                Succeeded: true,
                Browser: browser,
                ExecutablePath: exePath,
                Arguments: arguments,
                Message: string.Create(
                    CultureInfo.InvariantCulture,
                    $"Launched {displayName} with {protocolHint} debug port {port} (isolated Assay profile)."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to launch {Browser} with debug port {Port}.", displayName, port);
            return new BrowserLaunchResult(
                Succeeded: false,
                Browser: browser,
                ExecutablePath: exePath,
                Arguments: string.Empty,
                Message: $"Unable to launch {displayName}: {ex.Message}");
        }
    }

    internal static string BuildLaunchArguments(DebugBrowserKind browser, int port, string profileDirectory) =>
        browser switch
        {
            DebugBrowserKind.Edge or DebugBrowserKind.Chrome => string.Create(
                CultureInfo.InvariantCulture,
                $"--remote-debugging-port={port} --remote-allow-origins=* --no-first-run --no-default-browser-check --user-data-dir=\"{profileDirectory}\""),
            DebugBrowserKind.Firefox => string.Create(
                CultureInfo.InvariantCulture,
                $"--no-remote --profile \"{profileDirectory}\" --remote-debugging-port {port}"),
            _ => throw new ArgumentOutOfRangeException(nameof(browser), browser, null)
        };

    private string PrepareProfileDirectory(DebugBrowserKind browser)
    {
        var profileDir = Path.Combine(_profilesRootDirectory, browser.ToString().ToLowerInvariant());
        Directory.CreateDirectory(profileDir);

        if (browser == DebugBrowserKind.Firefox)
        {
            var userJsPath = Path.Combine(profileDir, "user.js");
            const string firefoxPrefs =
                """
                // NightCode Assay isolated Firefox remote debugging preferences
                user_pref("devtools.debugger.remote-enabled", true);
                user_pref("devtools.chrome.enabled", true);
                user_pref("devtools.debugger.prompt-connection", false);
                user_pref("remote.active-protocols", 3);
                user_pref("browser.shell.checkDefaultBrowser", false);
                """;
            File.WriteAllText(userJsPath, firefoxPrefs + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        return profileDir;
    }

    private static string GetDefaultProfilesRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.GetTempPath();
        }

        return Path.Combine(localAppData, "NightCodeAssay", "browser-profiles");
    }

    private static string GetDisplayName(DebugBrowserKind browser) =>
        browser switch
        {
            DebugBrowserKind.Edge => "Microsoft Edge",
            DebugBrowserKind.Chrome => "Google Chrome",
            DebugBrowserKind.Firefox => "Mozilla Firefox",
            _ => browser.ToString()
        };

    private static string? FindBrowserExecutable(DebugBrowserKind browser)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var candidate in GetWindowsCandidatePaths(browser))
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }

            var exeName = GetWindowsExecutableName(browser);
            var fromRegistry = TryResolveFromWindowsAppPaths(exeName);
            if (!string.IsNullOrWhiteSpace(fromRegistry) && File.Exists(fromRegistry))
            {
                return fromRegistry;
            }

            return TryResolveFromPath(exeName);
        }

        return browser switch
        {
            DebugBrowserKind.Edge => TryResolveFromPath("microsoft-edge") ?? TryResolveFromPath("msedge"),
            DebugBrowserKind.Chrome => TryResolveFromPath("google-chrome") ?? TryResolveFromPath("chromium"),
            DebugBrowserKind.Firefox => TryResolveFromPath("firefox"),
            _ => null
        };
    }

    private static IEnumerable<string> GetWindowsCandidatePaths(DebugBrowserKind browser)
    {
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var relativePath = browser switch
        {
            DebugBrowserKind.Edge => Path.Combine("Microsoft", "Edge", "Application", "msedge.exe"),
            DebugBrowserKind.Chrome => Path.Combine("Google", "Chrome", "Application", "chrome.exe"),
            DebugBrowserKind.Firefox => Path.Combine("Mozilla Firefox", "firefox.exe"),
            _ => throw new ArgumentOutOfRangeException(nameof(browser), browser, null)
        };

        var roots = browser switch
        {
            DebugBrowserKind.Edge => new[] { programFilesX86, programFiles, localAppData },
            DebugBrowserKind.Chrome => new[] { programFiles, programFilesX86, localAppData },
            DebugBrowserKind.Firefox => new[] { programFiles, programFilesX86, localAppData },
            _ => []
        };

        foreach (var root in roots)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                yield return Path.Combine(root, relativePath);
            }
        }
    }

    private static string GetWindowsExecutableName(DebugBrowserKind browser) =>
        browser switch
        {
            DebugBrowserKind.Edge => "msedge.exe",
            DebugBrowserKind.Chrome => "chrome.exe",
            DebugBrowserKind.Firefox => "firefox.exe",
            _ => throw new ArgumentOutOfRangeException(nameof(browser), browser, null)
        };

    private static string? TryResolveFromWindowsAppPaths(string executableName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string[] roots =
        [
            $@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executableName}",
            $@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executableName}"
        ];

        foreach (var keyPath in roots)
        {
            try
            {
                var value = Registry.GetValue(keyPath, valueName: null, defaultValue: null) as string;
                var cleaned = value?.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(cleaned) && File.Exists(cleaned))
                {
                    return cleaned;
                }
            }
            catch
            {
                // Registry key may not exist or be unreadable.
            }
        }

        return null;
    }

    private static string? TryResolveFromPath(string executableName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        foreach (var segment in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(segment.Trim('"'), executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }
}
