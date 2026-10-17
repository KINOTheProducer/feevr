using System.Text;
using System.Text.RegularExpressions;

namespace Tfmm.Core;

/// <summary>TF3's settings.lua (Steam: userdata\&lt;user&gt;\3493540\local\settings.lua).
/// <c>mainMenuState.activeModsState</c> is the mod list the New Game screen starts with. Saves keep their own list.</summary>
public static partial class GameSettings
{
    [GeneratedRegex(@"(activeModsState\s*=\s*)\{[^{}]*\}")] private static partial Regex ActiveRe();
    [GeneratedRegex(@"mainMenuState\s*=\s*\{")] private static partial Regex MainMenuRe();
    [GeneratedRegex(@"return\s*\{")] private static partial Regex ReturnRe();

    /// <summary>settings.lua files that exist on this machine, most recently written first.</summary>
    public static List<string> DefaultFiles() =>
        CrashLogs.SteamGameDirs().Select(d => Path.Combine(d, "local", "settings.lua")).Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc).ToList();

    /// <summary>Returns <paramref name="lua"/> with activeModsState set to <paramref name="modIds"/>, in order.</summary>
    public static string WithActiveMods(string lua, IEnumerable<string> modIds)
    {
        var list = new StringBuilder("{ ");
        foreach (var id in modIds) list.Append('"').Append(id.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\", ");
        list.Append('}');
        var value = list.ToString();

        if (ActiveRe().IsMatch(lua)) return ActiveRe().Replace(lua, m => m.Groups[1].Value + value, 1);
        if (MainMenuRe().Match(lua) is { Success: true } mm)
            return lua.Insert(mm.Index + mm.Length, $" activeModsState = {value},");
        if (ReturnRe().Match(lua) is { Success: true } r)
            return lua.Insert(r.Index + r.Length, $" mainMenuState = {{ activeModsState = {value}, }},");
        throw new FormatException("settings.lua does not look like TF3's settings file");
    }

    /// <summary>Writes the mod list into <paramref name="path"/>, keeping the previous file as settings.lua.bak.</summary>
    public static void WriteActiveMods(string path, IEnumerable<string> modIds)
    {
        var old = File.ReadAllText(path);
        var text = WithActiveMods(old, modIds);
        if (text == old) return;
        File.Copy(path, path + ".bak", overwrite: true);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
