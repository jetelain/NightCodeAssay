using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NightCodeAssay.Core.Interfaces;
using NightCodeAssay.Core.Security;
using NightCodeAssay.Core.Services;

namespace NightCodeAssay.Tests;

public sealed class DefaultKeyReaderTests : IDisposable
{
    private readonly string _tempRoot;

    public DefaultKeyReaderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"NightCodeAssay_KeyTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void ReadDefaultKey_ExplicitApiKeyTakesPrecedenceOverFiles()
    {
        var configDir = Path.Combine(_tempRoot, "explicit");
        WriteEnvelope(Path.Combine(configDir, "default-key.dat"), "file-key-123", new PlainKeyProtector());

        var reader = new DefaultKeyReader(
            explicitApiKey: "explicit-remote-key",
            configDirectory: configDir,
            protector: new PlainKeyProtector());

        Assert.Equal("explicit-remote-key", reader.ReadDefaultKey());
    }

    [Fact]
    public void ReadDefaultKey_ConfigDirectoryTakesPrecedenceOverEnvVarAndLocalAppData()
    {
        var explicitDir = Path.Combine(_tempRoot, "explicit");
        var envHome = Path.Combine(_tempRoot, "envHome");
        var localAppData = Path.Combine(_tempRoot, "localAppData");

        WriteEnvelope(Path.Combine(explicitDir, "default-key.dat"), "from-explicit", new PlainKeyProtector());
        WriteEnvelope(Path.Combine(envHome, "config", "default-key.dat"), "from-env", new PlainKeyProtector());
        WriteEnvelope(Path.Combine(localAppData, "Programs", "NightCodeForge", "config", "default-key.dat"), "from-appdata", new PlainKeyProtector());

        var reader = new DefaultKeyReader(
            configDirectory: explicitDir,
            protector: new PlainKeyProtector(),
            environmentVariableAccessor: name => name == "NIGHTCODEFORGE_HOME" ? envHome : null,
            localAppDataDirectory: localAppData,
            baseDirectory: _tempRoot);

        Assert.Equal(Path.Combine(explicitDir, "default-key.dat"), reader.KeyFilePath);
        Assert.Equal("from-explicit", reader.ReadDefaultKey());
    }

    [Fact]
    public void ReadDefaultKey_ResolvesEnvironmentVariableBeforeLocalAppData()
    {
        var envHome = Path.Combine(_tempRoot, "envHome");
        var localAppData = Path.Combine(_tempRoot, "localAppData");

        WriteEnvelope(Path.Combine(envHome, "config", "default-key.dat"), "from-env", new PlainKeyProtector());
        WriteEnvelope(Path.Combine(localAppData, "Programs", "NightCodeForge", "config", "default-key.dat"), "from-appdata", new PlainKeyProtector());

        var reader = new DefaultKeyReader(
            configDirectory: Path.Combine(_tempRoot, "missing-dir"),
            protector: new PlainKeyProtector(),
            environmentVariableAccessor: name => name == "NIGHTCODEFORGE_HOME" ? envHome : null,
            localAppDataDirectory: localAppData,
            baseDirectory: _tempRoot);

        Assert.Equal(Path.Combine(envHome, "config", "default-key.dat"), reader.KeyFilePath);
        Assert.Equal("from-env", reader.ReadDefaultKey());
    }

    [Fact]
    public void ReadDefaultKey_ReReadsDiskOnRotation()
    {
        var configDir = Path.Combine(_tempRoot, "rot");
        var keyFile = Path.Combine(configDir, "default-key.dat");
        WriteEnvelope(keyFile, "initial-key", new PlainKeyProtector());

        var reader = new DefaultKeyReader(configDirectory: configDir, protector: new PlainKeyProtector());
        Assert.Equal("initial-key", reader.ReadDefaultKey());

        WriteEnvelope(keyFile, "rotated-key", new PlainKeyProtector());
        Assert.Equal("rotated-key", reader.ReadDefaultKey());
    }

    [Fact]
    public void ReadDefaultKey_MissingOrCorruptOrUnwrappableReturnsNullWithoutThrowing()
    {
        var missingReader = new DefaultKeyReader(
            configDirectory: Path.Combine(_tempRoot, "nonexistent"),
            protector: new PlainKeyProtector(),
            environmentVariableAccessor: _ => null,
            localAppDataDirectory: Path.Combine(_tempRoot, "emptyLocalAppData"),
            baseDirectory: _tempRoot,
            currentDirectory: _tempRoot);

        Assert.Null(missingReader.ReadDefaultKey());

        var corruptDir = Path.Combine(_tempRoot, "corrupt");
        Directory.CreateDirectory(corruptDir);
        File.WriteAllText(Path.Combine(corruptDir, "default-key.dat"), "{not-valid-json");

        var corruptReader = new DefaultKeyReader(
            configDirectory: corruptDir,
            protector: new PlainKeyProtector(),
            environmentVariableAccessor: _ => null,
            localAppDataDirectory: Path.Combine(_tempRoot, "emptyLocalAppData"),
            baseDirectory: _tempRoot,
            currentDirectory: _tempRoot);
        Assert.Null(corruptReader.ReadDefaultKey());

        var cryptoDir = Path.Combine(_tempRoot, "cryptoFail");
        WriteEnvelope(Path.Combine(cryptoDir, "default-key.dat"), "secret", new PlainKeyProtector());
        var failingReader = new DefaultKeyReader(
            configDirectory: cryptoDir,
            protector: new ThrowingKeyProtector(),
            environmentVariableAccessor: _ => null,
            localAppDataDirectory: Path.Combine(_tempRoot, "emptyLocalAppData"),
            baseDirectory: _tempRoot,
            currentDirectory: _tempRoot);
        Assert.Null(failingReader.ReadDefaultKey());
    }

    [Fact]
    public void ReadDefaultKey_DiscoversKeyFileCreatedAfterConstructionInSiblingBinOutput()
    {
        var assayBinDir = Path.Combine(_tempRoot, "repos", "NightCodeAssay", "src", "NightCodeAssay.App", "bin", "Debug", "net10.0");
        var forgeDebugKeyFile = Path.Combine(_tempRoot, "repos", "NightCodeForge", "src", "NightCodeForge.Web", "bin", "Debug", "net10.0", "config", "default-key.dat");
        var envHome = Path.Combine(_tempRoot, "envHomeDelayed");
        var envKeyFile = Path.Combine(envHome, "config", "default-key.dat");
        Directory.CreateDirectory(assayBinDir);

        var reader = new DefaultKeyReader(
            configDirectory: null,
            protector: new PlainKeyProtector(),
            environmentVariableAccessor: name => name == "NIGHTCODEFORGE_HOME" ? envHome : null,
            localAppDataDirectory: Path.Combine(_tempRoot, "emptyLocalAppData"),
            baseDirectory: assayBinDir,
            currentDirectory: assayBinDir);

        Assert.Null(reader.ReadDefaultKey());

        // NightCodeForge starts later and generates default-key.dat under bin/Debug/net10.0/config/
        WriteEnvelope(forgeDebugKeyFile, "late-sibling-key", new PlainKeyProtector());
        Assert.Equal(forgeDebugKeyFile, reader.KeyFilePath);
        Assert.Equal("late-sibling-key", reader.ReadDefaultKey());

        // Higher-priority NIGHTCODEFORGE_HOME file created later also takes precedence dynamically
        WriteEnvelope(envKeyFile, "late-env-key", new PlainKeyProtector());
        Assert.Equal(envKeyFile, reader.KeyFilePath);
        Assert.Equal("late-env-key", reader.ReadDefaultKey());
    }

    [Fact]
    public async Task AppBuildServiceProvider_ResolvesAllRegisteredServicesWithoutAmbiguousConstructors()
    {
        await using var provider = NightCodeAssay.App.App.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IDefaultKeyReader>());
        Assert.NotNull(provider.GetRequiredService<INightCodeForgeClient>());
        Assert.NotNull(provider.GetRequiredService<CapturePipelineService>());
        Assert.NotNull(provider.GetRequiredService<IVisionAnalyzer>());
        Assert.NotNull(provider.GetRequiredService<IReportWriter>());
        Assert.NotNull(provider.GetRequiredService<IContextCollector>());
        Assert.NotNull(provider.GetRequiredService<IGlobalHotkeyService>());
        Assert.NotNull(provider.GetRequiredService<IBrowserLauncherService>());
        Assert.NotNull(provider.GetRequiredService<IScreenCaptureService>());
    }

    private static void WriteEnvelope(string path, string key, IKeyProtector protector)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(new { key, generatedAt = DateTimeOffset.UtcNow.ToString("O") });
        var protectedBytes = protector.Protect(Encoding.UTF8.GetBytes(json));
        File.WriteAllBytes(path, protectedBytes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private sealed class ThrowingKeyProtector : IKeyProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;

        public byte[] Unprotect(byte[] protectedData) => throw new CryptographicException("Unable to unwrap.");
    }
}
