namespace Tfmm.Core;

/// <summary>Checks a set of enabled mods: deps, revisions, incompatibilities, load order, savegame impact.</summary>
public static class Resolver
{
    /// <param name="autoAddDeps">Pull installed required deps into the set, like the game offers to do.</param>
    public static CheckReport Check(IEnumerable<string> enabled, IReadOnlyDictionary<string, Mod> catalog, bool autoAddDeps = false)
    {
        var rep = new CheckReport();
        var active = new List<string>();
        var seen = new HashSet<string>();
        foreach (var id in enabled)
        {
            if (!seen.Add(id)) continue;
            if (!catalog.ContainsKey(id))
            {
                rep.Issues.Add(new(IssueLevel.Error, id, "Enabled but not installed", IssueFix.Disable));
                rep.Missing.Add(id);
                continue;
            }
            active.Add(id);
        }

        if (autoAddDeps)
        {
            var queue = new Stack<string>(active);
            while (queue.Count > 0)
            {
                var m = catalog[queue.Pop()];
                foreach (var d in m.Dependencies)
                {
                    var did = d.Ref.ModId;
                    if (d.Optional || !catalog.ContainsKey(did) || !seen.Add(did)) continue;
                    active.Add(did);
                    queue.Push(did);
                    rep.Issues.Add(new(IssueLevel.Info, did, $"Added as dependency of {m.DisplayName}"));
                }
            }
        }

        var act = active.ToHashSet();
        foreach (var id in active)
        {
            var m = catalog[id];
            foreach (var d in m.Dependencies)
            {
                var did = d.Ref.ModId;
                catalog.TryGetValue(did, out var dep);
                if (!act.Contains(did))
                {
                    if (d.Optional)
                        rep.Issues.Add(new(IssueLevel.Info, id, $"Optional dependency {d.Label} not enabled",
                            dep is null ? IssueFix.None : IssueFix.Enable, did));
                    else if (dep is null)
                    {
                        rep.Issues.Add(new(IssueLevel.Error, id, $"Requires {d.Label}{d.Ref.RangeText}, not installed"));
                        if (!rep.Missing.Contains(did)) rep.Missing.Add(did);
                    }
                    else
                        rep.Issues.Add(new(IssueLevel.Error, id, $"Requires {d.Label}, installed but not enabled", IssueFix.Enable, did));
                    continue;
                }
                if (!d.Ref.Accepts(dep!.Revision))
                    rep.Issues.Add(new(d.Optional ? IssueLevel.Warning : IssueLevel.Error, id,
                        $"Needs {d.Label}{d.Ref.RangeText}, installed rev {dep.Revision}"));
            }
            foreach (var r in m.Incompatibilities)
                if (act.Contains(r.ModId) && catalog.TryGetValue(r.ModId, out var other) && r.Accepts(other.Revision))
                    rep.Issues.Add(new(IssueLevel.Error, id, $"Incompatible with {other.DisplayName} rev {other.Revision}", IssueFix.Disable));
            if (m.SeverityAdd == "Critical")
                rep.Issues.Add(new(IssueLevel.Warning, id, "Adding to an existing save is critical"));
            if (m.SeverityRemove == "Critical")
                rep.Issues.Add(new(IssueLevel.Warning, id, "Removing it later will break saves"));
            foreach (var w in m.Warnings)
                rep.Issues.Add(new(IssueLevel.Warning, id, w));
        }

        var (order, cycle) = LoadOrder(active, catalog);
        rep.Order = order;
        if (cycle.Count > 0)
            rep.Issues.Add(new(IssueLevel.Error, cycle[0], "Dependency cycle: " + string.Join(" → ", cycle)));
        return rep;
    }

    /// <summary>Topological sort: deps with loadBefore load first, otherwise stable on input order.
    /// On a cycle the remaining mods are appended in input order.</summary>
    public static (List<string> Order, List<string> Cycle) LoadOrder(IReadOnlyList<string> active, IReadOnlyDictionary<string, Mod> catalog)
    {
        var act = active.ToHashSet();
        var before = active.ToDictionary(id => id, id => catalog[id].Dependencies
            .Where(d => d.LoadBefore && act.Contains(d.Ref.ModId)).Select(d => d.Ref.ModId).ToList());
        var order = new List<string>();
        var state = new Dictionary<string, int>();
        var cycle = new List<string>();

        bool Visit(string id, List<string> stack)
        {
            state.TryGetValue(id, out var s);
            if (s == 2) return true;
            if (s == 1)
            {
                cycle.AddRange(stack.Skip(stack.IndexOf(id)));
                cycle.Add(id);
                return false;
            }
            state[id] = 1;
            stack.Add(id);
            foreach (var dep in before[id])
                if (!Visit(dep, stack)) return false;
            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
            order.Add(id);
            return true;
        }

        foreach (var id in active)
            if (!Visit(id, [])) break;
        if (cycle.Count > 0)
            order.AddRange(active.Where(id => !order.Contains(id)));
        return (order, cycle);
    }
}
