using Avalonia.Controls;

namespace NightCodeAssay.App.Views;

public partial class SessionControlWindow : Window
{
    public SessionControlWindow()
    {
        InitializeComponent();
    }

    public bool AllowClose { get; set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
