namespace NightCodeAssay.Core.Models;

/// <summary>
/// Normalized rectangle (<c>0..1</c> relative to screenshot dimensions) representing a user-drawn
/// highlight region on the captured screenshot.
/// </summary>
public readonly record struct HighlightRect(double X, double Y, double Width, double Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public HighlightRect Normalized()
    {
        var x = Math.Clamp(X, 0d, 1d);
        var y = Math.Clamp(Y, 0d, 1d);
        var right = Math.Clamp(X + Width, 0d, 1d);
        var bottom = Math.Clamp(Y + Height, 0d, 1d);

        var left = Math.Min(x, right);
        var top = Math.Min(y, bottom);
        return new HighlightRect(left, top, Math.Max(0d, right - left), Math.Max(0d, bottom - top));
    }
}
