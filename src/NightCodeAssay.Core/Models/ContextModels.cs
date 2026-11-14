namespace NightCodeAssay.Core.Models;

public sealed record DesktopContext(
    string ProcessName,
    string ExecutablePath,
    string WindowTitle,
    int WindowX,
    int WindowY,
    int WindowWidth,
    int WindowHeight,
    IReadOnlyList<string> EventLogEntries,
    int ProcessId = 0)
{
    public static DesktopContext Empty { get; } = new(
        ProcessName: "unknown",
        ExecutablePath: string.Empty,
        WindowTitle: string.Empty,
        WindowX: 0,
        WindowY: 0,
        WindowWidth: 0,
        WindowHeight: 0,
        EventLogEntries: [],
        ProcessId: 0);
}

public sealed record WebContext(
    string? ActiveUrl,
    string? PageTitle,
    IReadOnlyList<string> ConsoleAndNetworkErrors,
    bool IsCdpConnected,
    IReadOnlyList<int>? ObservedLocalhostPorts = null)
{
    public IReadOnlyList<int> EffectiveLocalhostPorts => ObservedLocalhostPorts ?? [];

    public static WebContext Empty { get; } = new(
        ActiveUrl: null,
        PageTitle: null,
        ConsoleAndNetworkErrors: [],
        IsCdpConnected: false,
        ObservedLocalhostPorts: []);
}

public sealed record TargetConfigFileSnapshot(
    string FileName,
    string FilePath,
    string RedactedContent,
    DateTimeOffset LastWriteUtc);

public sealed record TargetExecutableInfo(
    int ProcessId,
    string ProcessName,
    string ExecutablePath,
    string EntryAssemblyPath,
    string WorkingDirectory,
    DateTimeOffset? FileLastWriteUtc,
    string? ProductVersion,
    string? FileVersion,
    string? BuildConfiguration,
    string? InferredRepositoryPath,
    string? InferredBranch,
    DateTimeOffset? ProcessStartedAt,
    long? WorkingSetBytes,
    string ResolutionSource,
    IReadOnlyList<TargetConfigFileSnapshot> ConfigurationFiles)
{
    public string PrimaryBinaryPath =>
        !string.IsNullOrWhiteSpace(EntryAssemblyPath) ? EntryAssemblyPath : ExecutablePath;
}

public sealed record TechnicalContext(
    DesktopContext Desktop,
    WebContext Web,
    IReadOnlyList<string> LocalLogLines,
    DateTimeOffset CapturedAt,
    TargetExecutableInfo? TargetExecutable = null)
{
    public static TechnicalContext Empty(DateTimeOffset? capturedAt = null) => new(
        DesktopContext.Empty,
        WebContext.Empty,
        [],
        capturedAt ?? DateTimeOffset.Now,
        TargetExecutable: null);
}
