using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NightCodeAssay.App.ViewModels;
using NightCodeAssay.Core.Models;

namespace NightCodeAssay.App.Views;

public partial class QuickCaptureWindow : Window
{
    private Point _dragStart;
    private bool _isDraggingHighlight;
    private bool _isPanning;
    private Point _panStartPoint;
    private Vector _panStartOffset;
    private QuickCaptureViewModel? _attachedViewModel;

    public QuickCaptureWindow()
    {
        InitializeComponent();
        NoteTextBox.AddHandler(KeyDownEvent, OnNoteTextBoxKeyDown, RoutingStrategies.Tunnel);
        PreviewViewportBorder.AddHandler(PointerWheelChangedEvent, OnPreviewPointerWheelChanged, RoutingStrategies.Tunnel);
        Opened += OnWindowOpened;
        KeyDown += OnWindowKeyDown;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_attachedViewModel is not null)
        {
            _attachedViewModel.CloseRequested -= OnViewModelCloseRequested;
            _attachedViewModel.HighlightCleared -= OnViewModelHighlightCleared;
            _attachedViewModel.ZoomChanged -= OnViewModelZoomChanged;
            _attachedViewModel = null;
        }

        if (DataContext is QuickCaptureViewModel vm)
        {
            _attachedViewModel = vm;
            vm.CloseRequested += OnViewModelCloseRequested;
            vm.HighlightCleared += OnViewModelHighlightCleared;
            vm.ZoomChanged += OnViewModelZoomChanged;
            UpdatePreviewZoomLayout(vm);
        }
    }

    private void OnViewModelCloseRequested() => Close();

    private void OnViewModelHighlightCleared() => HighlightBox.IsVisible = false;

    private void OnViewModelZoomChanged()
    {
        if (DataContext is QuickCaptureViewModel vm)
        {
            UpdatePreviewZoomLayout(vm);
        }
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        Activate();
        if (DataContext is QuickCaptureViewModel vm)
        {
            UpdatePreviewZoomLayout(vm);
        }

        Dispatcher.UIThread.Post(() => NoteTextBox.Focus(), DispatcherPriority.Input);
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsInsideInteractiveControl(e.Source as Visual))
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
            e.Handled = true;
        }
    }

    private void OnWindowBorderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsInsideInteractiveControl(e.Source as Visual) || IsInsideElement(e.Source as Visual, PreviewViewportBorder))
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
            e.Handled = true;
        }
    }

    private void OnPreviewViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is QuickCaptureViewModel vm)
        {
            UpdatePreviewZoomLayout(vm);
        }
    }

    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not QuickCaptureViewModel vm || Math.Abs(e.Delta.Y) < double.Epsilon)
        {
            return;
        }

        var oldBoxWidth = PreviewViewbox.Bounds.Width > 0 ? PreviewViewbox.Bounds.Width : PreviewViewbox.Width;
        var oldBoxHeight = PreviewViewbox.Bounds.Height > 0 ? PreviewViewbox.Bounds.Height : PreviewViewbox.Height;
        var cursorInScrollViewer = e.GetPosition(PreviewScrollViewer);
        var cursorInBox = e.GetPosition(PreviewViewbox);

        var fx = oldBoxWidth > 0 ? Math.Clamp(cursorInBox.X / oldBoxWidth, 0d, 1d) : 0.5;
        var fy = oldBoxHeight > 0 ? Math.Clamp(cursorInBox.Y / oldBoxHeight, 0d, 1d) : 0.5;

        var factor = e.Delta.Y > 0
            ? QuickCaptureViewModel.ZoomStepMultiplier
            : 1.0 / QuickCaptureViewModel.ZoomStepMultiplier;

        vm.SetZoom(vm.ZoomFactor * factor);
        UpdatePreviewZoomLayout(vm);
        PreviewScrollViewer.UpdateLayout();

        var newBoxWidth = double.IsNaN(PreviewViewbox.Width) ? PreviewViewbox.Bounds.Width : PreviewViewbox.Width;
        var newBoxHeight = double.IsNaN(PreviewViewbox.Height) ? PreviewViewbox.Bounds.Height : PreviewViewbox.Height;

        var targetOffsetX = Math.Max(0d, (fx * newBoxWidth) - cursorInScrollViewer.X);
        var targetOffsetY = Math.Max(0d, (fy * newBoxHeight) - cursorInScrollViewer.Y);
        PreviewScrollViewer.Offset = new Vector(targetOffsetX, targetOffsetY);

        e.Handled = true;
    }

    private void UpdatePreviewZoomLayout(QuickCaptureViewModel vm)
    {
        var viewportWidth = PreviewViewportBorder.Bounds.Width - 2;
        var viewportHeight = PreviewViewportBorder.Bounds.Height - 2;

        if (viewportWidth <= 10 || viewportHeight <= 10)
        {
            return;
        }

        var fitScale = Math.Min(viewportWidth / vm.ImageWidth, viewportHeight / vm.ImageHeight);
        var fittedWidth = vm.ImageWidth * fitScale;
        var fittedHeight = vm.ImageHeight * fitScale;

        PreviewViewbox.Width = Math.Max(64d, fittedWidth * vm.ZoomFactor);
        PreviewViewbox.Height = Math.Max(64d, fittedHeight * vm.ZoomFactor);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not QuickCaptureViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            vm.CancelCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (TryHandleShortcutModifiers(e, vm))
        {
            e.Handled = true;
        }
    }

    private void OnNoteTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not QuickCaptureViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            vm.CancelCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (TryHandleShortcutModifiers(e, vm))
        {
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Enter or Key.Return && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            vm.SubmitCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static bool TryHandleShortcutModifiers(KeyEventArgs e, QuickCaptureViewModel vm)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return false;
        }

        switch (e.Key)
        {
            case Key.D1 or Key.NumPad1:
                vm.SelectCategoryCommand.Execute("Bug");
                return true;
            case Key.D2 or Key.NumPad2:
                vm.SelectCategoryCommand.Execute("UiUx");
                return true;
            case Key.D3 or Key.NumPad3:
                vm.SelectCategoryCommand.Execute("Enhancement");
                return true;
            case Key.D4 or Key.NumPad4:
                vm.SelectCategoryCommand.Execute("Question");
                return true;
            case Key.OemPlus or Key.Add:
                vm.ZoomInCommand.Execute(null);
                return true;
            case Key.OemMinus or Key.Subtract:
                vm.ZoomOutCommand.Execute(null);
                return true;
            case Key.D0 or Key.NumPad0:
                vm.ResetZoomCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }

    private void OnAnnotationPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not QuickCaptureViewModel vm)
        {
            return;
        }

        var props = e.GetCurrentPoint(AnnotationCanvas).Properties;
        if (props.IsRightButtonPressed || props.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _panStartPoint = e.GetPosition(PreviewScrollViewer);
            _panStartOffset = PreviewScrollViewer.Offset;
            AnnotationCanvas.Cursor = new Cursor(StandardCursorType.Hand);
            e.Pointer.Capture(AnnotationCanvas);
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed)
        {
            return;
        }

        var point = ClampToCanvas(e.GetPosition(AnnotationCanvas), vm);
        _dragStart = point;
        _isDraggingHighlight = true;
        e.Pointer.Capture(AnnotationCanvas);

        Canvas.SetLeft(HighlightBox, point.X);
        Canvas.SetTop(HighlightBox, point.Y);
        HighlightBox.Width = 0;
        HighlightBox.Height = 0;
        HighlightBox.IsVisible = true;
        e.Handled = true;
    }

    private void OnAnnotationPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isPanning)
        {
            var current = e.GetPosition(PreviewScrollViewer);
            var delta = current - _panStartPoint;
            PreviewScrollViewer.Offset = new Vector(
                Math.Max(0d, _panStartOffset.X - delta.X),
                Math.Max(0d, _panStartOffset.Y - delta.Y));
            e.Handled = true;
            return;
        }

        if (!_isDraggingHighlight || DataContext is not QuickCaptureViewModel vm)
        {
            return;
        }

        var clamped = ClampToCanvas(e.GetPosition(AnnotationCanvas), vm);
        var (x, y, width, height) = ComputeRect(_dragStart, clamped);

        Canvas.SetLeft(HighlightBox, x);
        Canvas.SetTop(HighlightBox, y);
        HighlightBox.Width = width;
        HighlightBox.Height = height;
    }

    private void OnAnnotationPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            AnnotationCanvas.Cursor = new Cursor(StandardCursorType.Cross);
            e.Handled = true;
            return;
        }

        if (!_isDraggingHighlight || DataContext is not QuickCaptureViewModel vm)
        {
            return;
        }

        _isDraggingHighlight = false;
        e.Pointer.Capture(null);

        var current = ClampToCanvas(e.GetPosition(AnnotationCanvas), vm);
        var (x, y, width, height) = ComputeRect(_dragStart, current);

        if (width < 6 || height < 6)
        {
            HighlightBox.IsVisible = false;
            vm.ClearHighlightCommand.Execute(null);
            return;
        }

        Canvas.SetLeft(HighlightBox, x);
        Canvas.SetTop(HighlightBox, y);
        HighlightBox.Width = width;
        HighlightBox.Height = height;
        HighlightBox.IsVisible = true;

        var normalized = new HighlightRect(
            x / vm.ImageWidth,
            y / vm.ImageHeight,
            width / vm.ImageWidth,
            height / vm.ImageHeight);
        vm.SetHighlight(normalized);
    }

    private static bool IsInsideInteractiveControl(Visual? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is Button or ToggleButton or TextBox or ScrollBar)
            {
                return true;
            }

            current = current.GetVisualParent();
        }

        return false;
    }

    private static bool IsInsideElement(Visual? source, Visual target)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, target))
            {
                return true;
            }

            current = current.GetVisualParent();
        }

        return false;
    }

    private static Point ClampToCanvas(Point p, QuickCaptureViewModel vm) =>
        new(Math.Clamp(p.X, 0, vm.ImageWidth), Math.Clamp(p.Y, 0, vm.ImageHeight));

    private static (double X, double Y, double Width, double Height) ComputeRect(Point a, Point b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        var w = Math.Abs(b.X - a.X);
        var h = Math.Abs(b.Y - a.Y);
        return (x, y, w, h);
    }
}
