using Avalonia;
using Tfmm.Core;

namespace Tfmm.App;

public static class Program
{
    /// <summary>Args: --demo (sample mods in a temp dir), --home DIR (config + playsets).</summary>
    [STAThread]
    public static void Main(string[] args)
    {
        App.StateFactory = () => CreateState(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppState CreateState(string[] args)
    {
        var i = Array.IndexOf(args, "--home");
        var home = i >= 0 && i + 1 < args.Length ? args[i + 1]
            : args.Contains("--demo") ? AppState.DefaultHome()
            : AppState.MigrateHome(AppState.LegacyHome(), AppState.DefaultHome());
        if (!args.Contains("--demo"))
        {
            var state = new AppState(home);
            state.AutoAddModIo();  // first run: pick up mods downloaded through the game's mod browser
            return state;
        }

        var tmp = Directory.CreateTempSubdirectory("junction-demo-").FullName;
        var s = new AppState(Path.Combine(tmp, "home"));
        s.AddRoot(ModSource.Manual, Demo.Write(Path.Combine(tmp, "mods")));
        var main = s.CreatePlayset("Main Campaign", ["demo_line_manager", "demo_classic_trams", "demo_modern_tram", "demo_better_stations"]);
        s.CreatePlayset("Vanilla Plus", ["demo_autumn_trees", "demo_freight_wagons"]);
        s.SetActive(main.Id);
        var probe = Path.Combine(tmp, "probe.lua");
        Demo.WritePerf(s, probe);
        s.Config.ProbeFile = probe;
        s.Config.CrashDir = Demo.WriteCrashLogs(Path.Combine(tmp, "crash_dump"), Path.Combine(tmp, "mods"));
        // No game here, so the live monitor watches this app itself to show real readings.
        s.Config.GameProcess = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        s.SaveConfig();
        return s;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
