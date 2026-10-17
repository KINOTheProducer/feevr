namespace Tfmm.Core;

public enum ModSource { Staging, Manual, Subscribed }

public enum IssueLevel { Info, Warning, Error }

/// <summary>Reference to another mod with an optional revision range. -1 = no limit (wiki).</summary>
public sealed record ModRef(string ModId, int RevMin = -1, int RevMax = -1)
{
    public bool Accepts(int revision) =>
        (RevMin == -1 || revision >= RevMin) && (RevMax == -1 || revision <= RevMax);

    public string RangeText =>
        RevMin == -1 && RevMax == -1 ? "" :
        $" (rev {(RevMin == -1 ? "*" : RevMin)}..{(RevMax == -1 ? "*" : RevMax)})";
}

public sealed record Dependency(ModRef Ref, string DisplayName = "", string Url = "",
    bool LoadBefore = true, bool Optional = false)
{
    public string Label => string.IsNullOrEmpty(DisplayName) ? Ref.ModId : DisplayName;
}

/// <summary>A load-phase script from mod.json, e.g. preRunScript {"fileName": "curvy_cities::/mod.script@preRunFn"}
/// → Phase "preRunScript", File "mod.script", Function "preRunFn".</summary>
public sealed record LoadScript(string Phase, string File, string Function);

public sealed class Mod
{
    public required string ModId { get; init; }
    public int Revision { get; init; }
    public required string Path { get; init; }
    public ModSource Source { get; init; } = ModSource.Manual;
    public string SeverityAdd { get; init; } = "None";
    public string SeverityRemove { get; init; } = "Warning";
    public bool Visible { get; init; } = true;
    public bool Cosmetic { get; init; }
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Authors { get; } = [];
    public List<string> Tags { get; } = [];
    public List<Dependency> Dependencies { get; } = [];
    public List<ModRef> Incompatibilities { get; } = [];
    public bool HasRootScript { get; set; }
    public List<LoadScript> LoadScripts { get; } = [];
    /// <summary>mod.io mod id when the mod sits in mod.io's download cache (…\mod.io\&lt;game&gt;\mods\&lt;id&gt;).</summary>
    public long? ModIoId { get; set; }
    public string ProfileUrl { get; set; } = "";
    /// <summary>True when Name was made up from the mod id because no real name was found.</summary>
    public bool NameGuessed { get; set; }
    public List<string> Warnings { get; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? ModId : Name;
}

/// <summary>What a one-click fix does: enable <c>Target</c>, or turn off <c>ModId</c>.</summary>
public enum IssueFix { None, Enable, Disable }

public sealed record Issue(IssueLevel Level, string ModId, string Message, IssueFix Fix = IssueFix.None, string? Target = null);

public sealed class CheckReport
{
    public List<string> Order { get; set; } = [];
    public List<Issue> Issues { get; } = [];
    public List<string> Missing { get; } = [];
    public bool Ok => Issues.All(i => i.Level != IssueLevel.Error);
    public int Count(IssueLevel l) => Issues.Count(i => i.Level == l);
}

public sealed class Playset
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Untitled";
    public List<string> Mods { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public string Id { get; set; } = "";
}
