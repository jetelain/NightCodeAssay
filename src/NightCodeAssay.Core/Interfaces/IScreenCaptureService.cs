using NightCodeAssay.Core.Models;

namespace NightCodeAssay.Core.Interfaces;

public interface IScreenCaptureService
{
    Task<ScreenCaptureResult> CaptureAsync(bool activeWindowOnly, CancellationToken cancellationToken = default);

    byte[] AnnotateWithHighlight(byte[] pngBytes, HighlightRect normalizedRect);

    string GetPrimaryScreenResolution();
}
