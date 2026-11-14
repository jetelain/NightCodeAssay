using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightCodeAssay.Core.Models;

namespace NightCodeAssay.App.ViewModels;

public sealed partial class QuickCaptureViewModel : ObservableObject, IDisposable
{
    public const double MinZoomFactor = 0.5;
    public const double MaxZoomFactor = 5.0;
    public const double ZoomStepMultiplier = 1.25;

    private readonly MemoryStream _bitmapStream;

    [ObservableProperty]
    private CaptureCategory _selectedCategory = CaptureCategory.Bug;

    [ObservableProperty]
    private string _userNote = string.Empty;

    [ObservableProperty]
    private HighlightRect _highlightRect;

    [ObservableProperty]
    private bool _hasHighlight;

    [ObservableProperty]
    private double _zoomFactor = 1.0;

    [ObservableProperty]
    private string _contextSummary = "Collecting desktop, browser & log context...";

    public QuickCaptureViewModel(ScreenCaptureResult capture)
    {
        Capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _bitmapStream = new MemoryStream(capture.PngBytes, writable: false);
        PreviewBitmap = new Bitmap(_bitmapStream);
    }

    public event Action? CloseRequested;

    public event Action? HighlightCleared;

    public event Action? ZoomChanged;

    public ScreenCaptureResult Capture { get; }

    public Bitmap PreviewBitmap { get; }

    public double ImageWidth => Math.Max(1, Capture.Width);

    public double ImageHeight => Math.Max(1, Capture.Height);

    public double HighlightStrokeThickness => Math.Clamp(ImageWidth / 280.0, 3.0, 8.0);

    public string ZoomPercentageText => $"{Math.Round(ZoomFactor * 100):0}%";

    public bool IsSubmitted { get; private set; }

    public bool IsBugSelected => SelectedCategory == CaptureCategory.Bug;

    public bool IsUiUxSelected => SelectedCategory == CaptureCategory.UiUx;

    public bool IsEnhancementSelected => SelectedCategory == CaptureCategory.Enhancement;

    public bool IsQuestionSelected => SelectedCategory == CaptureCategory.Question;

    partial void OnSelectedCategoryChanged(CaptureCategory value)
    {
        OnPropertyChanged(nameof(IsBugSelected));
        OnPropertyChanged(nameof(IsUiUxSelected));
        OnPropertyChanged(nameof(IsEnhancementSelected));
        OnPropertyChanged(nameof(IsQuestionSelected));
    }

    partial void OnZoomFactorChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomPercentageText));
        ZoomChanged?.Invoke();
    }

    public void UpdateContextSummary(TechnicalContext context)
    {
        var appPart = !string.IsNullOrWhiteSpace(context.Desktop.ProcessName)
            ? $"{context.Desktop.ProcessName} — \"{context.Desktop.WindowTitle}\""
            : "Desktop window";

        var urlPart = !string.IsNullOrWhiteSpace(context.Web.ActiveUrl)
            ? $" | {context.Web.ActiveUrl}"
            : string.Empty;

        var targetPart = context.TargetExecutable is { } target &&
                         !string.Equals(target.ProcessName, context.Desktop.ProcessName, StringComparison.OrdinalIgnoreCase)
            ? $" | Target: {target.ProcessName}"
            : string.Empty;

        ContextSummary = $"{Capture.Width}x{Capture.Height} | {appPart}{urlPart}{targetPart}";
    }

    public void SetHighlight(HighlightRect normalizedRect)
    {
        var norm = normalizedRect.Normalized();
        HighlightRect = norm;
        HasHighlight = !norm.IsEmpty;
    }

    public void SetZoom(double newZoom)
    {
        var clamped = Math.Clamp(newZoom, MinZoomFactor, MaxZoomFactor);
        if (Math.Abs(clamped - 1.0) < 0.03)
        {
            clamped = 1.0;
        }

        ZoomFactor = clamped;
    }

    [RelayCommand]
    private void ZoomIn() => SetZoom(ZoomFactor * ZoomStepMultiplier);

    [RelayCommand]
    private void ZoomOut() => SetZoom(ZoomFactor / ZoomStepMultiplier);

    [RelayCommand]
    private void ResetZoom() => SetZoom(1.0);

    [RelayCommand]
    private void SelectCategory(string? categoryName)
    {
        if (Enum.TryParse<CaptureCategory>(categoryName, ignoreCase: true, out var parsed))
        {
            SelectedCategory = parsed;
        }
    }

    [RelayCommand]
    private void ClearHighlight()
    {
        HighlightRect = default;
        HasHighlight = false;
        HighlightCleared?.Invoke();
    }

    [RelayCommand]
    private void Submit()
    {
        IsSubmitted = true;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        IsSubmitted = false;
        CloseRequested?.Invoke();
    }

    public void Dispose()
    {
        PreviewBitmap.Dispose();
        _bitmapStream.Dispose();
    }
}
