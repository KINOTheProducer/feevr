using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tfmm.App;
using Tfmm.App.ViewModels;
using Tfmm.App.Views;
using Tfmm.Core;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Tfmm.App.Tests.TestApp))]

namespace Tfmm.App.Tests;

public class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Drives the real window headlessly. Set TFMM_SHOTS=dir to save a PNG per page.</summary>
public class UiTests
{
    static (MainWindow, MainViewModel) Open()
    {
        var vm = new MainViewModel(Program.CreateState(["--demo"]), startTimers: false);
        var w = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        w.Show();
        Pump();
        return (w, vm);
    }

    static void Pump()
    {
        for (int i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Feeds the live monitor real readings of this test process (demo mode watches itself).</summary>
    static void Record(MainViewModel vm, int seconds)
    {
        var t = DateTime.Now;
        for (int i = 0; i <= seconds; i++)
        {
            var spin = System.Diagnostics.Stopwatch.StartNew();
            while (spin.ElapsedMilliseconds < 15) { }
            vm.TickPerf(t.AddSeconds(i));
        }
        Pump();
    }

    static void Shot(MainWindow w, string name, double height = 900)
    {
        w.Height = height;
        Pump();
        var dir = Environment.GetEnvironmentVariable("TFMM_SHOTS");
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            frame!.Save(Path.Combine(dir, name + ".png"));
        }
    }

    [AvaloniaFact]
    public void RendersEveryPage()
    {
        var (w, vm) = Open();
        vm.Library.Selected = vm.Library.Items.First(i => i.ModId == "demo_line_manager");
        Shot(w, "1-library");
        vm.Page = "playsets";
        Shot(w, "2-playsets");
        Record(vm, 40);
        vm.Page = "performance";
        Shot(w, "3-performance", 1900);
        vm.Page = "settings";
        Shot(w, "4-settings", 1100);
        vm.Page = "crashes";
        vm.Crashes.Apply(vm.Crashes.Scan());
        Shot(w, "7-crashes");
    }

    [AvaloniaFact]
    public void LiveTrackingRecordsASession()
    {
        var (_, vm) = Open();
        Assert.False(vm.IsGameRunning);
        Assert.Equal(4, vm.Performance.Sessions.Count);  // demo history

        Record(vm, 5);
        Assert.True(vm.IsGameRunning);
        Assert.StartsWith("Recording · Main Campaign", vm.LiveTitle);
        Assert.Equal(5, vm.CpuSeries.Count);
        Assert.NotEqual("--", vm.LiveRam);

        // the process "exits": point the monitor at nothing and let the session time out
        vm.State.Config.GameProcess = "definitely-not-running-xyz";
        vm.Dispose();  // finishing saves the session
        vm.Performance.ReloadSessions();
        Assert.Equal(5, vm.Performance.Sessions.Count);
        Assert.Contains(vm.Performance.Sessions, s => s.Playset == "Main Campaign");
    }

    [AvaloniaFact]
    public void ComparesPlaysetsAndShowsProbeTimings()
    {
        var (_, vm) = Open();
        var p = vm.Performance;
        Assert.True(p.CanCompare);
        Assert.Equal(["Main Campaign", "Vanilla Plus"], p.Compare.Select(c => c.Name));
        Assert.Equal("Lightest", p.Compare[1].Delta);
        Assert.StartsWith("+", p.Compare[0].Delta);
        Assert.NotNull(p.SessionDetail);

        Assert.True(p.HasProbe);
        Assert.Equal("demo_line_manager", p.Rows[0].ModId);  // measured cost sorts first
        Assert.Equal("0.70 ms", p.Rows[0].Measured);
        Assert.Equal("–", p.Rows.First(r => r.ModId == "demo_modern_tram").Measured);
        p.ScopeAll = true;
        Assert.Equal(vm.State.Catalog.Count, p.Rows.Count);
    }

    [AvaloniaFact]
    public void OneClickFixesAndPlaysets()
    {
        var (w, vm) = Open();
        Assert.Equal("Main Campaign", vm.ActivePlayset?.Name);
        var pg = vm.PlaysetsPage;
        Assert.Contains(pg.Stops, s => s.HasSpur && s.Spur == "UI Toolkit");

        var enable = pg.Issues.First(i => i.Fix == IssueFix.Enable && i.Target == "demo_ui_toolkit");
        pg.ApplyFixCommand.Execute(enable);
        Pump();
        Assert.Contains(pg.Stops, s => s.ModId == "demo_ui_toolkit");

        var clash = pg.Issues.First(i => i.Fix == IssueFix.Disable && i.ModId == "demo_modern_tram");
        pg.ApplyFixCommand.Execute(clash);
        Pump();
        Assert.DoesNotContain(pg.Stops, s => s.ModId == "demo_modern_tram");

        pg.FixDepsCommand.Execute(null);
        Pump();
        Assert.Equal("demo_core_lib", pg.Stops[0].ModId);
        Assert.DoesNotContain(pg.Stops, s => s.HasSpur);
        vm.Page = "playsets";
        Shot(w, "5-playsets-fixed");

        vm.ActivePlayset = vm.Playsets.First(p => p.Name == "Vanilla Plus");
        Pump();
        Assert.Equal(2, vm.Library.Items.Count(i => i.IsEnabled));

        pg.NewCommand.Execute(null);
        Pump();
        Assert.Equal(3, vm.Playsets.Count);
        pg.Name = "Renamed";
        Pump();
        Assert.Contains(vm.Playsets, p => p.Name == "Renamed");
    }

    [AvaloniaFact]
    public void ShowsUpdateBanner()
    {
        var (w, vm) = Open();
        Assert.False(vm.HasUpdate);
        Assert.Equal("v0.0.0", vm.AppVersionText);  // local builds; releases stamp the tag's version
        vm.Update = new UpdateInfo(new Version(0, 2, 0), "v0.2.0", "## What's new\n- Faster live charts\n- Fixes", "https://github.com/KINOTheProducer/feevr/releases/tag/v0.2.0", null, null);
        Assert.True(vm.HasUpdate);
        Assert.Equal("Junction 0.2.0 is out", vm.UpdateTitle);
        Assert.Equal("Faster live charts", vm.UpdateNotes);  // headings skipped
        Shot(w, "6-update-banner");
        vm.DismissUpdateCommand.Execute(null);
        Assert.False(vm.HasUpdate);
    }

    [AvaloniaFact]
    public void FiltersAndSearch()
    {
        var (_, vm) = Open();
        var all = vm.Library.Items.Count;
        vm.Library.Search = "tram";
        Assert.Equal(2, vm.Library.Items.Count);
        vm.Library.Search = "";
        vm.Library.FilterEnabled = true;
        Assert.Equal(4, vm.Library.Items.Count);
        vm.Library.FilterAll = true;
        vm.Library.Tag = "Vehicle";
        Assert.Equal(3, vm.Library.Items.Count);
        vm.Library.Tag = LibraryViewModel.AllTags;
        Assert.Equal(all, vm.Library.Items.Count);
        Assert.Equal("AL", vm.Library.Items.First(i => i.ModId == "demo_line_manager").Mono);
    }

    [AvaloniaFact]
    public void ModIoNamesAndLink()
    {
        var root = Directory.CreateTempSubdirectory("junction-ui-").FullName;
        try
        {
            var mods = Path.Combine(root, "mod.io", "10640", "mods");
            foreach (var (id, modId) in new[] { ("6416560", "apasz_dark_ui_1"), ("6416561", "auto_signals_1") })
            {
                Directory.CreateDirectory(Path.Combine(mods, id));
                File.WriteAllText(Path.Combine(mods, id, "mod.json"), $$"""{"modId":"{{modId}}","revision":1}""");
            }
            var home = Path.Combine(root, "home");
            ModIo.SaveCache(Path.Combine(home, "modio.json"), new()
            {
                [6416560] = new ModIoInfo { Id = 6416560, Name = "Dark UI", Author = "apasz", ProfileUrl = "https://mod.io/g/tf3/m/dark-ui", Fetched = DateTime.UtcNow },
            });
            var state = new AppState(home);
            Assert.Equal(mods, state.AutoAddModIo([Path.Combine(root, "mod.io")]));
            var vm = new MainViewModel(state, startTimers: false);
            var w = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
            w.Show();
            Pump();

            vm.Library.Selected = vm.Library.Items.First(i => i.ModId == "apasz_dark_ui_1");
            Pump();
            Assert.Equal("Dark UI", vm.Library.Detail!.Item.Name);
            Assert.True(vm.Library.Detail.HasProfile);
            Assert.Empty(vm.Library.Detail.Mod.Warnings);
            Shot(w, "5-modio-library");

            vm.Library.Selected = vm.Library.Items.First(i => i.ModId == "auto_signals_1");
            Pump();
            Assert.Equal("Auto Signals", vm.Library.Detail!.Item.Name);
            Assert.True(vm.Library.Detail.NameGuessed);
            Assert.False(vm.Library.Detail.HasProfile);

            vm.Page = "settings";
            Assert.Equal("1 of 2 mod.io mods have real names", vm.Settings.ModIoState);
            Shot(w, "6-modio-settings", 1300);
            w.Close();
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void CrashesBlameAndTurnOff()
    {
        var (w, vm) = Open();
        vm.Page = "crashes";
        vm.Crashes.Apply(vm.Crashes.Scan());
        Pump();
        Assert.Equal(3, vm.Crashes.Items.Count);
        Assert.Equal("02", vm.Crashes.CrashCount);
        var d = vm.Crashes.Detail!;
        Assert.Equal("Crash", d.Item.Title);
        Assert.Equal("Likely cause: Advanced Line Manager", d.Item.Headline);
        var top = d.Suspects[0];
        Assert.True(top.Enabled && top.InFatal);
        Assert.Contains(d.Suspects, s => s.ModId == "demo_ui_toolkit" && !s.InFatal);

        vm.Crashes.DisableCommand.Execute(top);
        Pump();
        Assert.DoesNotContain("demo_line_manager", vm.Active!.Mods);
        Assert.False(vm.Crashes.Detail!.Suspects[0].Enabled);

        vm.Crashes.Selected = vm.Crashes.Items.Single(i => i.Title == "Clean run");
        Assert.Empty(vm.Crashes.Detail!.Suspects);
        w.Close();
    }

    [AvaloniaFact]
    public void LogoTrainRunsWhileLoading()
    {
        var (w, vm) = Open();
        var logo = w.GetVisualDescendants().OfType<Logo>().Single();
        var cars = logo.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>().ToList();
        Assert.Equal(3, cars.Count);
        Assert.False(logo.Loading);
        Assert.All(cars, c => Assert.False(c.IsVisible));

        vm.ModIoBusy = true;
        Assert.True(logo.Loading);
        Assert.True(logo.Draw(0.7));  // mid-route, rounding the hook
        Assert.All(cars, c => Assert.True(c.IsVisible));
        Shot(w, "5-logo-loading");

        Assert.False(logo.Draw(5));  // run over: off the track
        Assert.All(cars, c => Assert.False(c.IsVisible));
        vm.ModIoBusy = false;
        Assert.False(logo.Loading);
        w.Close();
    }
}
