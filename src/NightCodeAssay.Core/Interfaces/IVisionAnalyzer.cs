using NightCodeAssay.Core.Models;

namespace NightCodeAssay.Core.Interfaces;

public interface IVisionAnalyzer
{
    Task<string> AnalyzeAsync(
        byte[] pngBytes,
        string userNote,
        CaptureCategory category,
        bool hasRedHighlight,
        TechnicalContext context,
        CompanionConfigDto? companionConfig,
        CancellationToken cancellationToken = default);

    Task<bool> CheckAvailabilityAsync(
        CompanionConfigDto? companionConfig,
        CancellationToken cancellationToken = default);
}
