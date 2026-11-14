namespace NightCodeAssay.Core.Models;

public enum DebugBrowserKind
{
    Edge,
    Chrome,
    Firefox
}

public sealed record BrowserLaunchResult(
    bool Succeeded,
    DebugBrowserKind Browser,
    string? ExecutablePath,
    string Arguments,
    string Message);
