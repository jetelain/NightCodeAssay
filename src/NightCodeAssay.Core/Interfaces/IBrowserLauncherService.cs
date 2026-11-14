using NightCodeAssay.Core.Models;

namespace NightCodeAssay.Core.Interfaces;

public interface IBrowserLauncherService
{
    BrowserLaunchResult LaunchWithDebugPort(DebugBrowserKind browser, int port);

    string? ResolveBrowserExecutable(DebugBrowserKind browser);
}
