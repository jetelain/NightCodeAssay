using NightCodeAssay.Core.Models;

namespace NightCodeAssay.Core.Interfaces;

public interface IReportWriter
{
    Task InitializeSessionReportAsync(SessionMetadata metadata, CancellationToken cancellationToken = default);

    Task<string> AppendEntryAsync(SessionMetadata metadata, CaptureEntry entry, CancellationToken cancellationToken = default);

    Task<string> GetReportForHandoffAsync(SessionMetadata metadata, CancellationToken cancellationToken = default);
}
