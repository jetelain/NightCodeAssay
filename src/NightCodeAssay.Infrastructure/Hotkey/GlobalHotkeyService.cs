using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NightCodeAssay.Core.Interfaces;

namespace NightCodeAssay.Infrastructure.Hotkey;

/// <summary>
/// Registers a system-wide Win32 global hotkey via <c>RegisterHotKey</c> on a dedicated
/// message-loop thread so exploratory captures work regardless of which application has focus.
/// </summary>
public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    private const int HotkeyId = 0x4E43; // 'NC'
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private readonly ILogger<GlobalHotkeyService> _logger;
    private readonly object _syncLock = new();

    private Thread? _messageLoopThread;
    private uint _nativeThreadId;
    private bool _disposed;

    public GlobalHotkeyService(ILogger<GlobalHotkeyService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event EventHandler? HotkeyPressed;

    public bool IsRegistered { get; private set; }

    public string ActiveHotkey { get; private set; } = string.Empty;

    public string? RegistrationError { get; private set; }

    public bool Register(string hotkey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Unregister();

        if (!OperatingSystem.IsWindows())
        {
            RegistrationError = "Global hotkeys are only supported on Windows.";
            ActiveHotkey = hotkey;
            return false;
        }

        if (!TryParseHotkey(hotkey, out var modifiers, out var virtualKey, out var parseError))
        {
            RegistrationError = parseError;
            ActiveHotkey = hotkey;
            _logger.LogWarning("Invalid hotkey specification '{Hotkey}': {Error}", hotkey, parseError);
            return false;
        }

        using var ready = new ManualResetEventSlim(false);
        var registered = false;
        string? registrationError = null;

        lock (_syncLock)
        {
            _messageLoopThread = new Thread(() =>
            {
                _nativeThreadId = GetCurrentThreadId();
                registered = RegisterHotKey(nint.Zero, HotkeyId, modifiers | ModNoRepeat, virtualKey);
                if (!registered)
                {
                    var win32Err = Marshal.GetLastWin32Error();
                    registrationError = $"Win32 RegisterHotKey failed for '{hotkey}' (error {win32Err}). Another application may have claimed it.";
                    ready.Set();
                    return;
                }

                ready.Set();

                try
                {
                    while (GetMessage(out var msg, nint.Zero, 0, 0) > 0)
                    {
                        if (msg.message == WmHotkey && msg.wParam == HotkeyId)
                        {
                            try
                            {
                                HotkeyPressed?.Invoke(this, EventArgs.Empty);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Unhandled exception inside global hotkey listener.");
                            }
                        }
                    }
                }
                finally
                {
                    UnregisterHotKey(nint.Zero, HotkeyId);
                }
            })
            {
                IsBackground = true,
                Name = "NightCodeAssay.GlobalHotkeyLoop"
            };

            _messageLoopThread.Start();
        }

        ready.Wait(TimeSpan.FromSeconds(3));
        IsRegistered = registered;
        ActiveHotkey = hotkey;
        RegistrationError = registrationError;

        if (registered)
        {
            _logger.LogInformation("Registered global hotkey '{Hotkey}'.", hotkey);
        }
        else
        {
            _logger.LogWarning("Could not register global hotkey '{Hotkey}': {Error}", hotkey, registrationError);
            Unregister();
        }

        return registered;
    }

    public void Unregister()
    {
        Thread? threadToJoin;
        uint threadId;

        lock (_syncLock)
        {
            threadToJoin = _messageLoopThread;
            threadId = _nativeThreadId;
            _messageLoopThread = null;
            _nativeThreadId = 0;
            IsRegistered = false;
        }

        if (OperatingSystem.IsWindows() && threadId != 0)
        {
            PostThreadMessage(threadId, WmQuit, nint.Zero, nint.Zero);
        }

        if (threadToJoin is not null && threadToJoin.IsAlive && Thread.CurrentThread != threadToJoin)
        {
            threadToJoin.Join(TimeSpan.FromSeconds(1));
        }
    }

    internal static bool TryParseHotkey(string? hotkey, out uint modifiers, out uint virtualKey, out string? error)
    {
        modifiers = 0;
        virtualKey = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(hotkey))
        {
            error = "Hotkey cannot be empty.";
            return false;
        }

        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "Hotkey cannot be empty.";
            return false;
        }

        foreach (var rawPart in parts)
        {
            var part = rawPart.ToUpperInvariant();
            switch (part)
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModControl;
                    break;
                case "ALT":
                case "MENU":
                    modifiers |= ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    break;
                case "WIN":
                case "WINDOWS":
                case "SUPER":
                    modifiers |= ModWin;
                    break;
                default:
                    if (virtualKey != 0)
                    {
                        error = $"Multiple primary keys specified in hotkey '{hotkey}'.";
                        return false;
                    }

                    if (!TryParseVirtualKey(part, out virtualKey))
                    {
                        error = $"Unrecognized key token '{rawPart}' in hotkey '{hotkey}'.";
                        return false;
                    }

                    break;
            }
        }

        if (virtualKey == 0)
        {
            error = $"No primary key specified in hotkey '{hotkey}'.";
            return false;
        }

        if (modifiers == 0)
        {
            error = $"Global hotkey '{hotkey}' should include at least one modifier (Ctrl, Alt, Shift, or Win).";
            return false;
        }

        return true;
    }

    private static bool TryParseVirtualKey(string token, out uint vk)
    {
        vk = 0;
        if (token.Length == 1)
        {
            var ch = token[0];
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = ch;
                return true;
            }
        }

        if (token.StartsWith('F') && int.TryParse(token[1..], out var fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + (fn - 1));
            return true;
        }

        vk = token switch
        {
            "SPACE" => 0x20,
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "PRINTSCREEN" or "SNAPSHOT" or "PRTSC" => 0x2C,
            "INSERT" or "INS" => 0x2D,
            "DELETE" or "DEL" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            _ => 0u
        };

        return vk != 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMsg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMsg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }
}
