namespace NightCodeAssay.Core.Interfaces;

public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler? HotkeyPressed;

    bool IsRegistered { get; }

    string ActiveHotkey { get; }

    string? RegistrationError { get; }

    bool Register(string hotkey);

    void Unregister();
}
