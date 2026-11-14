using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeAssay.Core.Configuration;
using NightCodeAssay.Core.Models;
using NightCodeAssay.Core.Services;
using NightCodeAssay.Infrastructure.Context;

namespace NightCodeAssay.Tests;

public sealed class TargetProcessAndConfigTests : IDisposable
{
    private readonly string _tempRoot;

    public TargetProcessAndConfigTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"NightCodeAssay_TargetTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void ResolveDotnetTarget_UnwrapsDotnetExecAndDotnetRunProject()
    {
        var execDir = Path.Combine(_tempRoot, "exec-app");
        Directory.CreateDirectory(execDir);
        var execDll = Path.Combine(execDir, "MyWeb.dll");
        File.WriteAllBytes(execDll, [1, 2, 3]);

        var (resolvedExecDll, resolvedExecWorkDir) = TargetProcessInspector.ResolveDotnetTarget(
            $"\"C:\\Program Files\\dotnet\\dotnet.exe\" exec --depsfile app.deps.json \"{execDll}\" --urls http://localhost:5270",
            execDir);

        Assert.Equal(execDll, resolvedExecDll);
        Assert.Equal(execDir, resolvedExecWorkDir);

        var runProjectDir = Path.Combine(_tempRoot, "run-app");
        var runBinDir = Path.Combine(runProjectDir, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(runBinDir);
        var csprojPath = Path.Combine(runProjectDir, "MyProject.csproj");
        var builtDll = Path.Combine(runBinDir, "MyProject.dll");
        File.WriteAllText(csprojPath, "<Project />");
        File.WriteAllBytes(builtDll, [4, 5, 6]);

        var (resolvedRunDll, resolvedRunWorkDir) = TargetProcessInspector.ResolveDotnetTarget(
            $"dotnet run --project \"{csprojPath}\" -c Debug",
            _tempRoot);

        Assert.Equal(builtDll, resolvedRunDll);
        Assert.Equal(runProjectDir, resolvedRunWorkDir);

        // `dotnet watch -p <csproj>` (without explicit `run` verb) and stopping option parsing at `--`.
        var (resolvedWatchDll, resolvedWatchWorkDir) = TargetProcessInspector.ResolveDotnetTarget(
            $"dotnet watch -p \"{csprojPath}\" -- --project ignored.csproj",
            _tempRoot);

        Assert.Equal(builtDll, resolvedWatchDll);
        Assert.Equal(runProjectDir, resolvedWatchWorkDir);

        // Relative assembly path must not resolve against Assay's own CWD when workingDir is unknown.
        var (unresolvedDll, _) = TargetProcessInspector.ResolveDotnetTarget(
            "dotnet exec relative-nonexistent-or-cwd.dll",
            processWorkingDir: null,
            fileExists: _ => true);
        Assert.Null(unresolvedDll);
    }

    [Fact]
    public void ResolveGitBranch_SupportsGitWorktreeFilePointer()
    {
        var mainGitDir = Path.Combine(_tempRoot, "main-repo", ".git", "worktrees", "feature-wt");
        Directory.CreateDirectory(mainGitDir);
        File.WriteAllText(Path.Combine(mainGitDir, "HEAD"), "ref: refs/heads/feat/worktree-support\n");

        var worktreeDir = Path.Combine(_tempRoot, "feature-wt");
        Directory.CreateDirectory(worktreeDir);
        var relativeGitDir = Path.GetRelativePath(worktreeDir, mainGitDir);
        File.WriteAllText(Path.Combine(worktreeDir, ".git"), $"gitdir: {relativeGitDir}\n");

        var branch = CapturePipelineService.ResolveGitBranch(worktreeDir);

        Assert.Equal("feat/worktree-support", branch);
    }

    [Fact]
    public void TargetConfigCollector_DiscoversAndRedactsAppSettingsAndEnv()
    {
        var projectRoot = Path.Combine(_tempRoot, "SampleWeb");
        var binDir = Path.Combine(projectRoot, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(binDir);

        var entryExe = Path.Combine(binDir, "SampleWeb.exe");
        var entryDll = Path.Combine(binDir, "SampleWeb.dll");
        File.WriteAllBytes(entryExe, [1]);
        File.WriteAllBytes(entryDll, [1]);

        const string baseSettings = """
        {
          "ConnectionStrings": {
            "Default": "Server=localhost;Database=SampleDb;Password=TopSecretPassword;"
          },
          "ApiKey": "sk-test-key-secret-value",
          "AllowedHosts": "*"
        }
        """;

        File.WriteAllText(Path.Combine(projectRoot, "appsettings.json"), baseSettings);
        // Duplicate copy in bin/ should be deduplicated when identical.
        File.WriteAllText(Path.Combine(binDir, "appsettings.json"), baseSettings);
        File.WriteAllText(
            Path.Combine(projectRoot, "appsettings.Development.json"),
            """{"DetailedErrors": true, "JwtSigningKey": "super-secret-jwt-key"}""");
        File.WriteAllText(
            Path.Combine(projectRoot, ".env"),
            "ASPNETCORE_ENVIRONMENT=Development\nSERVICE_TOKEN=ghp_1234567890abcdefghij1234567890abcd\n");

        // Non-scoped config files must NOT be collected.
        File.WriteAllText(Path.Combine(binDir, "SampleWeb.deps.json"), """{"runtimeTarget": {}}""");
        File.WriteAllText(Path.Combine(projectRoot, "appsettings.schema.json"), """{"$schema": "http://json-schema.org/draft-07/schema#"}""");
        var propsDir = Path.Combine(projectRoot, "Properties");
        Directory.CreateDirectory(propsDir);
        File.WriteAllText(Path.Combine(propsDir, "launchSettings.json"), """{"profiles": {}}""");

        var collector = new TargetConfigCollector(NullLogger<TargetConfigCollector>.Instance);
        var configs = collector.CollectConfigurations(entryExe, entryDll, projectRoot, inferredRepoPath: null);

        Assert.Equal(3, configs.Count);
        Assert.Equal(1, configs.Count(c => c.FileName.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(configs, c => c.FileName == "appsettings.Development.json");
        Assert.Contains(configs, c => c.FileName == ".env");
        Assert.DoesNotContain(configs, c => c.FileName.Equals("appsettings.schema.json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(configs, c => c.FileName.Contains("deps.json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(configs, c => c.FileName.Contains("launchSettings.json", StringComparison.OrdinalIgnoreCase));

        var appSettings = configs.Single(c => c.FileName == "appsettings.json");
        Assert.Contains("Password=[REDACTED]", appSettings.RedactedContent);
        Assert.Contains("\"ApiKey\": \"[REDACTED]\"", appSettings.RedactedContent);
        Assert.Contains("\"AllowedHosts\": \"*\"", appSettings.RedactedContent);
        Assert.DoesNotContain("TopSecretPassword", appSettings.RedactedContent);

        var env = configs.Single(c => c.FileName == ".env");
        Assert.Contains("ASPNETCORE_ENVIRONMENT=Development", env.RedactedContent);
        Assert.Contains("SERVICE_TOKEN=[REDACTED]", env.RedactedContent);
        Assert.DoesNotContain("ghp_1234567890", env.RedactedContent);
    }

    [Fact]
    public void TargetProcessInspector_InspectsCurrentProcessMetadata()
    {
        var inspector = new TargetProcessInspector(
            new TargetConfigCollector(NullLogger<TargetConfigCollector>.Instance),
            NullLogger<TargetProcessInspector>.Instance);

        var info = inspector.InspectProcess(Environment.ProcessId, "ForegroundWindow");

        Assert.NotNull(info);
        Assert.Equal(Environment.ProcessId, info!.ProcessId);
        Assert.Equal("ForegroundWindow", info.ResolutionSource);
        Assert.False(string.IsNullOrWhiteSpace(info.PrimaryBinaryPath));
        Assert.NotNull(info.FileLastWriteUtc);
        Assert.True(info.WorkingSetBytes > 0);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Environment.CurrentDirectory),
                info.WorkingDirectory,
                ignoreCase: true);
        }
    }

    [Fact]
    public void ExtractBinaryVersionMetadata_SkipsDotnetHostExecutable()
    {
        var hostDir = Path.Combine(_tempRoot, "dotnet-host");
        Directory.CreateDirectory(hostDir);
        var fakeDotnetExe = Path.Combine(hostDir, "dotnet.exe");
        File.WriteAllBytes(fakeDotnetExe, [1, 2, 3]);

        var (lastWrite, productVersion, fileVersion) = TargetProcessInspector.ExtractBinaryVersionMetadata(
            primaryInspectionPath: Path.Combine(hostDir, "missing.dll"),
            fallbackExecutablePath: fakeDotnetExe);

        Assert.Null(lastWrite);
        Assert.Null(productVersion);
        Assert.Null(fileVersion);
    }

    [Fact]
    public void HybridContextCollector_PrioritizesDesktopProcessOverWebPortsAndRespectsRemoteUrls()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var options = Options.Create(new AssayOptions());
        var desktopCollector = new WindowsDesktopContextCollector(NullLogger<WindowsDesktopContextCollector>.Instance);
        using var httpClient = new HttpClient();
        using var cdpCollector = new CdpContextCollector(options, httpClient, NullLogger<CdpContextCollector>.Instance);
        var logTailer = new WorkspaceLogTailer(options, NullLogger<WorkspaceLogTailer>.Instance);
        var configCollector = new TargetConfigCollector(NullLogger<TargetConfigCollector>.Instance);
        var processInspector = new TargetProcessInspector(configCollector, NullLogger<TargetProcessInspector>.Instance);
        var hybrid = new HybridContextCollector(
            desktopCollector,
            cdpCollector,
            logTailer,
            options,
            new LocalPortProcessResolver(),
            processInspector);

        // Foreground concrete desktop app must win over a background localhost browser session.
        var desktopTarget = hybrid.ResolveTargetExecutable(
            new DesktopContext("MyDesktopApp", string.Empty, "Main Window", 0, 0, 800, 600, [], Environment.ProcessId),
            new WebContext($"http://localhost:{port}/dashboard", "Dashboard", [], true, [port]),
            explicitRepositoryPath: null);

        Assert.NotNull(desktopTarget);
        Assert.Equal("ForegroundWindow", desktopTarget!.ResolutionSource);

        // Foreground browser on localhost resolves the listening port process.
        var localWebTarget = hybrid.ResolveTargetExecutable(
            new DesktopContext("msedge", string.Empty, "Dashboard - Edge", 0, 0, 800, 600, [], Environment.ProcessId),
            new WebContext($"http://localhost:{port}/dashboard", "Dashboard", [], true, [port]),
            explicitRepositoryPath: null);

        Assert.NotNull(localWebTarget);
        Assert.Equal($"localhost:{port}", localWebTarget!.ResolutionSource);

        // Foreground browser on a remote URL must NOT resolve stale ObservedLocalhostPorts.
        var remoteWebTarget = hybrid.ResolveTargetExecutable(
            new DesktopContext("msedge", string.Empty, "Example - Edge", 0, 0, 800, 600, [], Environment.ProcessId),
            new WebContext("https://example.com/home", "Example", [], true, [port]),
            explicitRepositoryPath: null);

        Assert.Null(remoteWebTarget);

        // Path containment check must respect directory boundaries.
        Assert.True(HybridContextCollector.IsPathUnderDirectory(@"C:\repos\App\bin\app.dll", @"C:\repos\App"));
        Assert.False(HybridContextCollector.IsPathUnderDirectory(@"C:\repos\App2\bin\app.dll", @"C:\repos\App"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
