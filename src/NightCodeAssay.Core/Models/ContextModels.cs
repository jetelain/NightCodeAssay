namespace NightCodeAssay.Core.Models;

public sealed record DesktopContext(
    string ProcessName,
    string ExecutablePath,
    string WindowTitle,
    int WindowX,
    int WindowY,
    int WindowWidth,
    int WindowHeight,
    IReadOnlyList<string> EventLogEntries)
{
    public static DesktopContext Empty { get; } = new(
        ProcessName: "unknown",
        ExecutablePath: string.Empty,
        WindowTitle: string.Empty,
        WindowX: 0,
        WindowY: 0,
        WindowWidth: 0,
        WindowHeight: 0,
        EventLogEntries: []);
}

public sealed record WebContext(
    string? ActiveUrl,
    string? PageTitle,
    IReadOnlyList<string> ConsoleAndNetworkErrors,
    bool IsCdpConnected)
{
    public static WebContext Empty { get; } = new(
        ActiveUrl: null,
        PageTitle: null,
        ConsoleAndNetworkErrors: [],
        IsCdpConnected: false);
}

public sealed record TechnicalContext(
    DesktopContext Desktop,
    WebContext Web,
    IReadOnlyList<string> LocalLogLines,
    DateTimeOffset CapturedAt)
{
    public static TechnicalContext Empty(DateTimeOffset? capturedAt = null) => new(
        DesktopContext.Empty,
        WebContext.Empty,
        [],
        capturedAt ?? DateTimeOffset.Now);
}
