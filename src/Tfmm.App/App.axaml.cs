using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Tfmm.App.ViewModels;
using Tfmm.App.Views;
using Tfmm.Core;

namespace Tfmm.App;

public partial class App : Application
{
    public static Func<AppState> StateFactory { get; set; } = () => new AppState(AppState.DefaultHome());

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(StateFactory()) };
        base.OnFrameworkInitializationCompleted();
    }
}
