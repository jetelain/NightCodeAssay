namespace NightCodeAssay.Core.Models;

/// <summary>
/// Exploratory finding category chosen by the tester in the quick-capture overlay.
/// </summary>
public enum CaptureCategory
{
    Bug,
    UiUx,
    Enhancement,
    Question
}

public static class CaptureCategoryExtensions
{
    public static string ToBracketTag(this CaptureCategory category) => category switch
    {
        CaptureCategory.Bug => "[Bug]",
        CaptureCategory.UiUx => "[UI/UX]",
        CaptureCategory.Enhancement => "[Enhancement]",
        CaptureCategory.Question => "[Question]",
        _ => "[Bug]"
    };

    public static string ToDisplayName(this CaptureCategory category) => category switch
    {
        CaptureCategory.Bug => "Bug",
        CaptureCategory.UiUx => "UI/UX",
        CaptureCategory.Enhancement => "Enhancement",
        CaptureCategory.Question => "Question",
        _ => "Bug"
    };
}
