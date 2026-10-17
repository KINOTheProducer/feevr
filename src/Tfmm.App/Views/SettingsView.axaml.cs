using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Tfmm.App.ViewModels;

namespace Tfmm.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SourceGroup group } || DataContext is not SettingsViewModel vm) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Choose a folder for {group.Title.ToLowerInvariant()}",
            AllowMultiple = false,
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
            vm.AddFolder(group.Source, path);
    }

    async void BrowseProbe_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the probe file",
            AllowMultiple = false,
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
            vm.ProbeFile = path;
    }

    async void BrowseCrashDir_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the game's log folder (crash_dump)",
            AllowMultiple = false,
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
            vm.CrashDir = path;
    }
}
