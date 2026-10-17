using Avalonia.Media;

namespace Tfmm.App.Theme;

/// <summary>Palette for brushes picked in view models (status colors, meters). Mirrors Palette.axaml.</summary>
public static class Pal
{
    static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex)).ToImmutable();

    public static readonly IBrush Transparent = Brushes.Transparent;
    public static readonly IBrush Ink = B("#0B0D10");
    public static readonly IBrush Text = B("#E8ECF2");
    public static readonly IBrush Muted = B("#A3ADBC");
    public static readonly IBrush Muted2 = B("#7D8797");
    public static readonly IBrush Faint2 = B("#5E6878");
    public static readonly IBrush Dim = B("#4E5767");
    public static readonly IBrush Off = B("#2C333E");
    public static readonly IBrush Empty = B("#1E242D");
    public static readonly IBrush Tag = B("#1A1F27");
    public static readonly IBrush Hover = B("#161A21");
    public static readonly IBrush Accent = B("#FF8A3D");
    public static readonly IBrush AccentHi = B("#FFB37E");
    public static readonly IBrush Amber = B("#FFD9A8");
    public static readonly IBrush Ok = B("#4BE39A");
    public static readonly IBrush OkText = B("#6BE3A4");
    public static readonly IBrush OkSoft = B("#163226");
    public static readonly IBrush Warn = B("#F5D06A");
    public static readonly IBrush WarnSoft = B("#3A3018");
    public static readonly IBrush Err = B("#FF6B76");
    public static readonly IBrush ErrText = B("#FF8A93");
    public static readonly IBrush ErrSoft = B("#3A1A1E");
    public static readonly IBrush Info = B("#7CC4F5");
    public static readonly IBrush InfoSoft = B("#162A3A");
    public static readonly IBrush BannerErr = B("#1E1214");
    public static readonly IBrush BannerErrLine = B("#4A2329");
    public static readonly IBrush BannerWarn = B("#1D1A10");
    public static readonly IBrush BannerWarnLine = B("#4A3F1E");
    public static readonly IBrush BannerOk = B("#0F1D16");
    public static readonly IBrush BannerOkLine = B("#1F4A33");

    /// <summary>Orange ramp for ranked shares, strongest first.</summary>
    public static readonly IBrush[] Shares = new[] { "#FF8A3D", "#C76A2F", "#8E4E26", "#5F3820", "#40291A", "#2E2117" }.Select(B).ToArray();

    public static readonly IBrush KindTextures = B("#FF8A3D");
    public static readonly IBrush KindModels = B("#7CC4F5");
    public static readonly IBrush KindScripts = B("#F5D06A");
    public static readonly IBrush KindData = B("#8C96A6");

    /// <summary>Status level ("ok", "warn", "err", "info", "idle") to dot, text and soft-background brushes.</summary>
    public static IBrush Dot(string level) => level switch { "err" => Err, "warn" => Warn, "ok" => Ok, "info" => Info, _ => Off };
    public static IBrush Fg(string level) => level switch { "err" => ErrText, "warn" => Warn, "ok" => OkText, "info" => Info, _ => Faint2 };
    public static IBrush Soft(string level) => level switch { "err" => ErrSoft, "warn" => WarnSoft, "ok" => OkSoft, "info" => InfoSoft, _ => Tag };

    /// <summary>20-cell meter: first 8 cells green, next 6 amber, last 6 red.</summary>
    public static List<IBrush> Meter(double value, double max = 100, int cells = 20)
    {
        var lit = max <= 0 ? 0 : (int)Math.Round(Math.Clamp(value / max, 0, 1) * cells);
        if (value > 0 && lit == 0) lit = 1;
        var list = new List<IBrush>(cells);
        for (int i = 0; i < cells; i++)
            list.Add(i >= lit ? Empty : i < cells * 0.4 ? Ok : i < cells * 0.7 ? Warn : Err);
        return list;
    }
}
