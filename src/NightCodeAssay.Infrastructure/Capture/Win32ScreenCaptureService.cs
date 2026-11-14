using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NightCodeAssay.Core.Interfaces;
using NightCodeAssay.Core.Models;
using SkiaSharp;

namespace NightCodeAssay.Infrastructure.Capture;

/// <summary>
/// Captures the full desktop or foreground Win32 window before any overlay renders,
/// and composites user-drawn red highlight boxes using <see cref="SKCanvas"/>.
/// </summary>
public sealed class Win32ScreenCaptureService : IScreenCaptureService
{
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private readonly ILogger<Win32ScreenCaptureService> _logger;

    public Win32ScreenCaptureService(ILogger<Win32ScreenCaptureService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<ScreenCaptureResult> CaptureAsync(bool activeWindowOnly, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capturedAt = DateTimeOffset.Now;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                return Task.FromResult(CaptureWindows(activeWindowOnly, capturedAt));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Win32 screen capture failed; falling back to synthetic image.");
            }
        }

        var fallback = CreateFallbackPng(1280, 720);
        return Task.FromResult(new ScreenCaptureResult(fallback, 1280, 720, capturedAt));
    }

    public byte[] AnnotateWithHighlight(byte[] pngBytes, HighlightRect normalizedRect)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        var rect = normalizedRect.Normalized();
        if (rect.IsEmpty || pngBytes.Length == 0)
        {
            return pngBytes;
        }

        using var bitmap = SKBitmap.Decode(pngBytes);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return pngBytes;
        }

        using var surface = SKSurface.Create(new SKImageInfo(bitmap.Width, bitmap.Height, bitmap.ColorType, bitmap.AlphaType));
        if (surface is null)
        {
            return pngBytes;
        }

        var canvas = surface.Canvas;
        canvas.DrawBitmap(bitmap, 0, 0);

        var left = (float)(rect.X * bitmap.Width);
        var top = (float)(rect.Y * bitmap.Height);
        var width = (float)(rect.Width * bitmap.Width);
        var height = (float)(rect.Height * bitmap.Height);
        var skRect = SKRect.Create(left, top, Math.Max(2f, width), Math.Max(2f, height));

        using var fillPaint = new SKPaint
        {
            Color = new SKColor(255, 46, 77, 36),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        canvas.DrawRect(skRect, fillPaint);

        var strokeWidth = Math.Clamp(bitmap.Width / 320f, 3f, 8f);
        using var strokePaint = new SKPaint
        {
            Color = new SKColor(255, 46, 77, 255),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeWidth,
            IsAntialias = true
        };
        canvas.DrawRect(skRect, strokePaint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public string GetPrimaryScreenResolution()
    {
        if (OperatingSystem.IsWindows())
        {
            var width = GetSystemMetrics(SmCxScreen);
            var height = GetSystemMetrics(SmCyScreen);
            if (width > 0 && height > 0)
            {
                return $"{width}x{height}";
            }
        }

        return "Unknown";
    }

    [SupportedOSPlatform("windows")]
    private static ScreenCaptureResult CaptureWindows(bool activeWindowOnly, DateTimeOffset capturedAt)
    {
        Rectangle bounds = default;

        if (activeWindowOnly)
        {
            var hwnd = GetForegroundWindow();
            if (hwnd != nint.Zero && GetWindowRect(hwnd, out var rect))
            {
                var w = rect.Right - rect.Left;
                var h = rect.Bottom - rect.Top;
                if (w > 16 && h > 16)
                {
                    bounds = new Rectangle(rect.Left, rect.Top, w, h);
                }
            }
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            var vx = GetSystemMetrics(SmXVirtualScreen);
            var vy = GetSystemMetrics(SmYVirtualScreen);
            var vw = GetSystemMetrics(SmCxVirtualScreen);
            var vh = GetSystemMetrics(SmCyVirtualScreen);

            if (vw > 0 && vh > 0)
            {
                bounds = new Rectangle(vx, vy, vw, vh);
            }
            else
            {
                var pw = Math.Max(1, GetSystemMetrics(SmCxScreen));
                var ph = Math.Max(1, GetSystemMetrics(SmCyScreen));
                bounds = new Rectangle(0, 0, pw, ph);
            }
        }

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return new ScreenCaptureResult(ms.ToArray(), bounds.Width, bounds.Height, capturedAt);
    }

    internal static byte[] CreateFallbackPng(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(24, 28, 36));

        using var borderPaint = new SKPaint
        {
            Color = new SKColor(63, 158, 255),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4
        };
        canvas.DrawRect(SKRect.Create(16, 16, width - 32, height - 32), borderPaint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
