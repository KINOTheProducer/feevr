using Avalonia.Controls;

namespace Tfmm.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    protected override void OnClosed(EventArgs e)
    {
        // Saves a session that is still recording.
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
