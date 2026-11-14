using Avalonia;
using Avalonia.Win32;
using Avalonia.X11;

namespace NightCodeAssay.App;

/// <summary>
/// Desktop entry point for NightCode Assay. Uses software-only rendering on Win32 and X11
/// so the exploratory testing companion consumes 0 MB of GPU VRAM.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
            .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] })
            .LogToTrace();
}
