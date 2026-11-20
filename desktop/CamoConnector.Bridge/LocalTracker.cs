using System.Text.Json;
using System.Text.RegularExpressions;

namespace CamoConnector.Bridge;

internal sealed class LocalTracker
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly string path;
    private readonly string backup;
    private readonly Dictionary<string, Weapon> weapons;
    private readonly Dictionary<string, Rule> rules = new(StringComparer.Ordinal);
    private readonly Catalog catalog;
    private State state;

    internal sealed record Rule(string Id, string Type, string Name, string? Unlock, int? UnlockTotal,
        string? Objective, int? Goal, string? Category);
    internal sealed record Weapon(string Id, string Name, string Category, Rule[] Camos);
    internal sealed record Catalog(Weapon[] Weapons, Rule[] Shared);
    internal sealed record Entry(int Count, bool Done);
    internal sealed record State(string? Focus, Dictionary<string, Entry> Progress);
    internal sealed record Edit(string? Weapon, string? Camo, int? Count, bool? Done);

    public LocalTracker(string data)
    {
        path = Path.Combine(data, "local-tracker.json");
        backup = Path.Combine(data, "mw4-camos.latest.json");
        using var stream = typeof(LocalTracker).Assembly.GetManifestResourceStream("CamoConnector.MultiplayerCatalog")!;
        var source = JsonSerializer.Deserialize<Catalog>(stream, Json)!;
        var completion = source.Shared.Where(rule => rule.Type == "Completionist").ToLookup(rule => rule.Category);
        catalog = new(source.Weapons.Select(weapon => weapon with
        {
            Camos = [.. weapon.Camos, .. completion[weapon.Category == "Melee" ? "Melee" : weapon.Category + "s"]]
        }).ToArray(), source.Shared.Where(rule => rule.Type is "Prestige" or "Multiplayer").ToArray());
        weapons = catalog.Weapons.ToDictionary(weapon => weapon.Id);
        foreach (var weapon in catalog.Weapons)
        foreach (var rule in weapon.Camos)
            rules.Add(weapon.Id + ":" + rule.Id, rule);
        foreach (var rule in catalog.Shared)
            rules.Add("shared:" + rule.Id, rule);

        state = File.Exists(path)
            ? JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Invalid local tracker data.")
            : new(null, new(StringComparer.Ordinal));
        if (!File.Exists(path) && File.Exists(backup))
        {
            try { ImportBackup(); }
            catch (Exception error) when (error is IOException or JsonException)
            {
                Console.Error.WriteLine("Tracker backup import: " + error.Message);
            }
        }
    }

    public Catalog GetCatalog() => catalog;

    public State Snapshot()
    {
        lock (gate) return state;
    }

    public object Overlay()
    {
        lock (gate)
        {
            if (state.Focus == null || !weapons.TryGetValue(state.Focus, out var weapon))
                return new { weapon = (string?)null };
            var remaining = weapon.Camos.Where(camo => state.Progress.GetValueOrDefault(weapon.Id + ":" + camo.Id)?.Done != true).ToArray();
            var rule = remaining.FirstOrDefault(camo => camo.Type == "Special" &&
                (camo.UnlockTotal ?? 0) <= weapon.Camos.Where(item => item.Type == "Military" &&
                    state.Progress.GetValueOrDefault(weapon.Id + ":" + item.Id)?.Done == true)
                    .Select(item => item.UnlockTotal ?? 0).DefaultIfEmpty().Max())
                ?? remaining.FirstOrDefault();
            var progress = rule == null ? null : state.Progress.GetValueOrDefault(weapon.Id + ":" + rule.Id);
            return new { weapon = weapon.Name, camo = rule?.Name, objective = rule?.Objective ?? rule?.Unlock,
                count = progress?.Count ?? 0, goal = rule?.Goal };
        }
    }

    public bool ApplyEdit(Edit edit)
    {
        if (edit.Weapon == null) return false;
        if (edit.Camo == null)
        {
            if (!weapons.ContainsKey(edit.Weapon)) return false;
            Change((next, _) => edit.Weapon);
            return true;
        }
        var key = edit.Weapon + ":" + edit.Camo;
        if (!rules.TryGetValue(key, out var rule) || edit.Count is not int count || edit.Done is not bool done ||
            count < 0 || count > (rule.Goal ?? 0)) return false;
        Change((next, focus) =>
        {
            next[key] = new(done && rule.Goal is int goal ? goal : count, done || count == rule.Goal);
            return focus;
        });
        return true;
    }

    public bool ImportBackup()
    {
        if (!File.Exists(backup)) return false;
        using var document = JsonDocument.Parse(File.ReadAllText(backup));
        var root = document.RootElement;
        if (ProgressFeed.Text(root, "mode") is not ("mp" or "multiplayer") ||
            ProgressFeed.Field(root, "counts").ValueKind != JsonValueKind.Object ||
            ProgressFeed.Field(root, "done").ValueKind != JsonValueKind.Object) return false;
        var counts = root.GetProperty("counts");
        var done = root.GetProperty("done");
        var focus = ProgressFeed.Text(root, "focus");
        Change((next, current) =>
        {
            foreach (var key in rules.Keys)
            {
                var count = ProgressFeed.Number(counts, key);
                var completed = ProgressFeed.Field(done, key).ValueKind == JsonValueKind.True;
                if (completed || count is > 0)
                    ApplyProgress(next, key, count ?? 0, completed);
            }
            return current ?? (focus != null && weapons.ContainsKey(focus) ? focus : null);
        });
        return true;
    }

    public void Accept(string line)
    {
        using var document = JsonDocument.Parse(line.Trim().TrimStart('\uFEFF'));
        var item = document.RootElement;
        if (ProgressFeed.Text(item, "source") != "live" || ProgressFeed.Text(item, "mode") != "mp") return;
        var name = ProgressFeed.Text(item, "weapon");
        if (name == null || !weapons.TryGetValue(Key(name), out var weapon)) return;
        var kind = ProgressFeed.Text(item, "kind");
        if (kind == "weapon")
        {
            Change((_, _) => weapon.Id);
            return;
        }

        string? camo = kind switch
        {
            "completionist-unlock" or "catalog-special-unlocked" => ProgressFeed.Text(item, "challenge"),
            "progress-snapshot" when ProgressFeed.Text(item, "type") == "camo" => ProgressFeed.Text(item, "camo"),
            "challenge-completed" when weapon.Id == "sang-9mm" => ProgressFeed.Text(item, "challenge") switch
            {
                "menu-autumn" or "ads-25" => "Autumn",
                "menu-snakeskin" or "streak-3x5" => "Snakeskin",
                "menu-space-time" => "Space Time",
                _ => null
            },
            _ => null
        };
        if (kind == "military-unlock" && ProgressFeed.Number(item, "tier") is int tier && tier is >= 1 and <= 8)
        {
            Change((next, focus) =>
            {
                for (var i = 1; i <= tier; i++) ApplyProgress(next, weapon.Id + ":mil-" + i, 0, true);
                return focus;
            });
        }
        else if (camo != null && weapon.Camos.SingleOrDefault(rule => Key(rule.Name) == Key(camo)) is { } rule)
        {
            var count = ProgressFeed.Number(item, "count");
            if (kind == "progress-snapshot" && (count is null or < 0 ||
                ProgressFeed.Number(item, "goal") != rule.Goal || rule.Goal == null || count > rule.Goal)) return;
            Change((next, focus) =>
            {
                ApplyProgress(next, weapon.Id + ":" + rule.Id, count ?? 0, kind != "progress-snapshot");
                return focus;
            });
        }
        else if (kind == "akimbo-kill" && weapon.Id == "sang-9mm" ||
            ProgressFeed.Text(item, "key") == "signal-50:sp-3" && ProgressFeed.Number(item, "delta") == 1 && weapon.Id == "signal-50")
        {
            var key = weapon.Id + ":sp-3";
            Change((next, focus) =>
            {
                ApplyProgress(next, key, (next.GetValueOrDefault(key)?.Count ?? 0) + 1, false);
                return focus;
            });
        }
    }

    private void ApplyProgress(Dictionary<string, Entry> progress, string key, int count, bool done)
    {
        var rule = rules[key];
        var before = progress.GetValueOrDefault(key) ?? new(0, false);
        var value = Math.Clamp(Math.Max(before.Count, count), 0, rule.Goal ?? 0);
        var completed = before.Done || done || value == rule.Goal;
        if (completed && rule.Goal is int goal) value = goal;
        if (value != 0 || completed) progress[key] = new(value, completed);
    }

    private void Change(Func<Dictionary<string, Entry>, string?, string?> update)
    {
        lock (gate)
        {
            var next = new Dictionary<string, Entry>(state.Progress, StringComparer.Ordinal);
            var focus = update(next, state.Focus);
            if (focus == state.Focus && next.Count == state.Progress.Count &&
                next.All(pair => state.Progress.GetValueOrDefault(pair.Key) == pair.Value)) return;
            var changed = new State(focus, next);
            var temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(changed, Json));
                File.Move(temporary, path, overwrite: true);
                state = changed;
            }
            finally { File.Delete(temporary); }
        }
    }

    private static string Key(string text) => Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
}
