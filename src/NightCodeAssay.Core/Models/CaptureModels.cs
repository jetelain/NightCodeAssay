namespace NightCodeAssay.Core.Models;

public sealed record ScreenCaptureResult(
    byte[] PngBytes,
    int Width,
    int Height,
    DateTimeOffset CapturedAt);

public sealed record CaptureEntry(
    int Index,
    DateTimeOffset Timestamp,
    CaptureCategory Category,
    string UserNote,
    byte[] ScreenshotPngBytes,
    bool HasRedHighlight,
    TechnicalContext Context,
    string? ScreenshotFilePath = null,
    string? VlmDescription = null);

public sealed record SessionMetadata(
    string SessionId,
    DateTimeOffset StartedAt,
    string SessionDirectoryPath,
    string AssetsDirectoryPath,
    string ReportFilePath,
    string WorkspaceName,
    int? WorkspaceId,
    string RepositoryPath,
    string ActiveBranch,
    string OsVersion,
    string ScreenResolution)
{
    public const string StandaloneWorkspaceName = "<Unlinked / Standalone>";
}
