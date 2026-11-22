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

    internal const string ArtworkResourcePrefix = "CamoConnector.CamoArtwork.";

    internal sealed record Rule(string Id, string Type, string Name, string? Unlock, int? UnlockTotal,
        string? Objective, int? Goal, string? Category)
    {
        public string? Artwork { get; init; }
    }
    internal sealed record Weapon(string Id, string Name, string Category, Rule[] Camos);
    internal sealed record Catalog(Weapon[] Weapons, Rule[] Shared);
    internal sealed record Entry(int Count, bool Done);
    internal sealed record State(string? Focus, Dictionary<string, Entry> Progress, string? OverlayLayout = null,
        string? PinnedWeapon = null);
    internal sealed record Edit(string? Weapon, string? Camo, int? Count, bool? Done, int? Increment = null);

    public LocalTracker(string data)
    {
        path = Path.Combine(data, "local-tracker.json");
        backup = Path.Combine(data, "mw4-camos.latest.json");
        using var stream = typeof(LocalTracker).Assembly.GetManifestResourceStream("CamoConnector.MultiplayerCatalog")!;
        var source = JsonSerializer.Deserialize<Catalog>(stream, Json)!;
        var embedded = typeof(LocalTracker).Assembly.GetManifestResourceNames().ToHashSet(StringComparer.Ordinal);
        Rule WithArtwork(Rule rule)
        {
            var filename = Key(rule.Name) + ".png";
            return embedded.Contains(ArtworkResourcePrefix + filename)
                ? rule with { Artwork = "/local/artwork/" + filename } : rule;
        }
        var completion = source.Shared.Select(WithArtwork).Where(rule => rule.Type == "Completionist").ToLookup(rule => rule.Category);
        catalog = new(source.Weapons.Select(weapon => weapon with
        {
            Camos = [.. weapon.Camos.Select(WithArtwork), .. completion[weapon.Category == "Melee" ? "Melee" : weapon.Category + "s"]]
        }).ToArray(), source.Shared.Where(rule => rule.Type is "Prestige" or "Multiplayer").Select(WithArtwork).ToArray());
        weapons = catalog.Weapons.ToDictionary(weapon => weapon.Id);
        foreach (var weapon in catalog.Weapons)
        foreach (var rule in weapon.Camos)
            rules.Add(weapon.Id + ":" + rule.Id, rule);
        foreach (var rule in catalog.Shared)
            rules.Add("shared:" + rule.Id, rule);

        state = File.Exists(path)
            ? JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Invalid local tracker data.")
            : new(null, new(StringComparer.Ordinal));
        if (File.Exists(path)) Change((_, focus) => focus); // Repair prerequisites in existing saves.
        else if (File.Exists(backup))
        {
            try { ImportBackup(); }
            catch (Exception error) when (error is IOException or JsonException)
            {
                Console.Error.WriteLine("Tracker backup import: " + error.Message);
            }
        }
    }

    public Catalog GetCatalog() => catalog;

    public object TrackerView()
    {
        lock (gate) return new { state.Focus, state.Progress, state.OverlayLayout, overview = Overlay() };
    }

    public bool SetOverlayLayout(string? layout)
    {
        if (layout is not ("default" or "11annah")) return false;
        lock (gate)
        {
            if (state.OverlayLayout != layout) SaveState(state with { OverlayLayout = layout });
        }
        return true;
    }

    public object Overlay()
    {
        lock (gate)
        {
            if (state.Focus == null || !weapons.TryGetValue(state.Focus, out var weapon))
                return new { weapon = (string?)null, state.OverlayLayout };
            bool Done(Weapon item, Rule camo) => state.Progress.GetValueOrDefault(item.Id + ":" + camo.Id)?.Done == true;
            bool HasCamo(Weapon item, string id) => item.Camos.Any(camo => camo.Id == id && Done(item, camo));
            int ConfirmedRequirement(Func<Rule, bool> matches) => weapon.Camos
                .Where(camo => matches(camo) && Done(weapon, camo))
                .Select(camo => camo.UnlockTotal ?? 0).DefaultIfEmpty().Max();

            var level = state.Progress.GetValueOrDefault(weapon.Id + ":weapon-level")?.Count;
            var maxLevel = state.Progress.GetValueOrDefault(weapon.Id + ":weapon-level")?.Done == true;
            var knownLevel = Math.Max(level ?? 0, ConfirmedRequirement(IsLevelRule));
            var observedUnlocks = state.Progress.GetValueOrDefault(weapon.Id + ":weapon-unlocks")?.Count;
            var minimumUnlocks = Math.Max(observedUnlocks ?? 0, ConfirmedRequirement(IsWeaponUnlockRule));
            var weaponUnlocks = minimumUnlocks == observedUnlocks ? observedUnlocks : null;
            var maximumCamoLevel = weapon.Camos.Where(IsLevelRule)
                .Select(camo => camo.UnlockTotal ?? 0).DefaultIfEmpty().Max();
            if (maxLevel) knownLevel = Math.Max(knownLevel, maximumCamoLevel);
            var maximumCamoUnlocks = weapon.Camos.Where(IsWeaponUnlockRule)
                .Select(camo => camo.UnlockTotal ?? 0).DefaultIfEmpty().Max();
            bool IsLaunchWeapon(Weapon item) =>
                item.Id is not ("cronen-osprey" or "csg-35" or "jak-12" or "zlr-talonhawk");

            (int Count, int Goal)? CompletionGate(Rule camo)
            {
                if (camo.Id == "platinum" && camo.Unlock == "Complete the Gold Camo Challenge")
                    return (HasCamo(weapon, "gold") ? 1 : 0, 1);
                var required = Regex.Match(camo.Unlock ?? "", @"^Complete (\d+) ");
                if (!required.Success) return null;
                var goal = int.Parse(required.Groups[1].Value);
                var count = camo.Id switch
                {
                    "platinum" => catalog.Weapons.Count(item => IsLaunchWeapon(item) &&
                        item.Category == weapon.Category && HasCamo(item, "gold")),
                    "harmonic-break" => catalog.Weapons.Count(item => IsLaunchWeapon(item) && HasCamo(item, "platinum")),
                    "divergence" => catalog.Weapons.Count(item => IsLaunchWeapon(item) && HasCamo(item, "harmonic-break")),
                    _ => -1
                };
                return count < 0 ? null : (Math.Min(count, goal), goal);
            }

            bool Available(Rule camo)
            {
                // Progress on the objective confirms that the game already opened the challenge.
                if (state.Progress.GetValueOrDefault(weapon.Id + ":" + camo.Id)?.Count > 0) return true;
                if (camo.Type == "Special")
                    return (camo.UnlockTotal ?? 0) <= (IsLevelRule(camo) ? knownLevel : Math.Max(observedUnlocks ?? 0, ConfirmedRequirement(IsWeaponUnlockRule)));
                if (camo.Type != "Completionist") return false;
                if (camo.Id == "gold")
                    return weapon.Camos.Where(item => item.Type is "Military" or "Special").All(item => Done(weapon, item));

                if (CompletionGate(camo) is not { } gate || gate.Count < gate.Goal) return false;
                return camo.Id switch
                {
                    "platinum" => HasCamo(weapon, "gold"),
                    "harmonic-break" => HasCamo(weapon, "platinum"),
                    "divergence" => HasCamo(weapon, "harmonic-break"),
                    _ => false
                };
            }

            var remaining = weapon.Camos.Where(camo => !Done(weapon, camo)).ToArray();
            var nextMilitary = remaining.FirstOrDefault(camo => camo.Type == "Military");
            var rows = remaining.Where(camo => camo == nextMilitary || Available(camo))
                .Select(camo =>
                {
                    var military = camo.Type == "Military";
                    var levelBased = IsLevelRule(camo);
                    return new
                    {
                        camo.Id, name = camo.Name, camo.Artwork, objective = camo.Objective ?? camo.Unlock,
                        count = military ? (levelBased ? level : (int?)null) :
                            state.Progress.GetValueOrDefault(weapon.Id + ":" + camo.Id)?.Count ?? 0,
                        goal = military && levelBased ? camo.UnlockTotal : camo.Goal
                    };
                }).ToArray();
            var next = rows.Length == 0 ? remaining.FirstOrDefault() : null;
            var prerequisite = next == null ? null : CompletionGate(next);
            return new { weapon = weapon.Name, level = knownLevel == level ? level : null,
                maxLevel, minimumLevel = knownLevel, weaponUnlocks, minimumUnlocks, maximumCamoLevel, maximumCamoUnlocks, rows, state.OverlayLayout,
                waiting = next == null ? null : new { next.Id, next.Name, next.Artwork, next.Unlock, count = prerequisite?.Count, goal = prerequisite?.Goal },
                complete = remaining.Length == 0 };
        }
    }

    public bool ApplyEdit(Edit edit)
    {
        if (edit.Weapon == null) return false;
        if (edit.Camo == null)
        {
            if (!weapons.ContainsKey(edit.Weapon) || edit.Increment != null) return false;
            lock (gate)
            {
                if (state.Focus != edit.Weapon || state.PinnedWeapon != edit.Weapon)
                    SaveState(state with { Focus = edit.Weapon, PinnedWeapon = edit.Weapon });
            }
            return true;
        }
        var key = edit.Weapon + ":" + edit.Camo;
        if (edit.Camo == "weapon-level")
        {
            if (!weapons.ContainsKey(edit.Weapon) || edit.Count is not (>= 1 and <= 200) ||
                edit.Done != null || edit.Increment != null) return false;
            Change((next, focus) =>
            {
                next[key] = new(edit.Count.Value, false);
                return focus;
            });
            return true;
        }
        if (!rules.TryGetValue(key, out var rule)) return false;
        if (edit.Increment is int increment)
        {
            if (rule.Goal is not int target || increment is not (1 or 5) || edit.Count != null || edit.Done != null)
                return false;
            Change((next, focus) =>
            {
                if (next.GetValueOrDefault(key)?.Done != true)
                    ApplyProgress(next, key, (next.GetValueOrDefault(key)?.Count ?? 0) + increment, false);
                return focus;
            });
            return true;
        }
        if (edit.Count is not int count || edit.Done is not bool done ||
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
        var kind = ProgressFeed.Text(item, "kind");
        var context = ProgressFeed.Text(item, "context");
        if (kind == "completionist-unlock" && context is ("unlock-banner" or "weapon-objective-banner") &&
            ProgressFeed.Text(item, "challenge") is { } challenge)
        {
            Change((next, focus) =>
            {
                var equipped = name == null ? null : Key(name);
                var candidates = context == "weapon-objective-banner" ? new[] { equipped } : new[] { state.PinnedWeapon, equipped, focus };
                foreach (var id in candidates)
                {
                    if (id == null || !weapons.TryGetValue(id, out var candidate)) continue;
                    var camo = candidate.Camos.FirstOrDefault(rule =>
                        rule.Type == "Completionist" && Key(rule.Name) == Key(challenge));
                    if (camo == null) continue;
                    ApplyProgress(next, id + ":" + camo.Id, 0, true);
                    break;
                }
                return focus;
            });
            return;
        }
        if (name == null || !weapons.TryGetValue(Key(name), out var weapon)) return;
        if (kind == "weapon")
        {
            Change((_, _) => weapon.Id);
            return;
        }

        if (kind == "weapon-level-max")
        {
            Change((next, focus) =>
            {
                var key = weapon.Id + ":weapon-level";
                next[key] = new(next.GetValueOrDefault(key)?.Count ?? 0, true);
                foreach (var camo in weapon.Camos.Where(rule => rule.Type == "Military" && IsLevelRule(rule)))
                    ApplyProgress(next, weapon.Id + ":" + camo.Id, 0, true);
                return focus;
            });
            return;
        }
        if (kind == "weapon-level-up" ||
            kind == "progress-snapshot" && ProgressFeed.Text(item, "type") == "weapon-level")
        {
            var level = ProgressFeed.Number(item, kind == "weapon-level-up" ? "level" : "count");
            if (level is not (>= 1 and <= 200)) return;
            if (kind == "progress-snapshot" &&
                (ProgressFeed.Number(item, "goal") is not int goal || level > goal ||
                    !string.Equals(ProgressFeed.Text(item, "objective"), $"Reach Weapon Level {goal}",
                        StringComparison.OrdinalIgnoreCase))) return;
            var key = weapon.Id + ":weapon-level";
            Change((next, focus) =>
            {
                var exactMenuLevel = kind == "progress-snapshot" && ProgressFeed.Text(item, "context") == "camo-menu" &&
                    level.Value < ProgressFeed.Number(item, "goal");
                if (exactMenuLevel)
                    next[key] = new(level.Value, false);
                else if (level.Value > (next.GetValueOrDefault(key)?.Count ?? 0))
                    next[key] = new(level.Value, next.GetValueOrDefault(key)?.Done == true);
                return focus;
            });
            return;
        }

        if (kind == "progress-snapshot" && ProgressFeed.Text(item, "type") == "post-match-unlocks")
        {
            if (ProgressFeed.Number(item, "goal") is not int goalValue || goalValue is <= 0 or > 150 ||
                ProgressFeed.Number(item, "count") is not int observed || observed < 0 || observed > goalValue) return;
            var unlockKey = weapon.Id + ":weapon-unlocks";
            Change((next, focus) => {
                if (observed > (next.GetValueOrDefault(unlockKey)?.Count ?? 0)) next[unlockKey] = new(observed, false);
                return focus;
            });
            return;
        }

        if (kind == "progress-snapshot" && ProgressFeed.Text(item, "type") == "weapon-unlocks")
        {
            var nameOfCamo = ProgressFeed.Text(item, "camo");
            var unlockRule = weapon.Camos.SingleOrDefault(camo => camo.Name.Equals(nameOfCamo, StringComparison.OrdinalIgnoreCase));
            if (unlockRule == null || !IsWeaponUnlockRule(unlockRule) || unlockRule.UnlockTotal is not int total ||
                ProgressFeed.Number(item, "remaining") is not int remaining || remaining <= 0 || remaining >= total ||
                ProgressFeed.Number(item, "goal") != total || ProgressFeed.Number(item, "count") != total - remaining) return;
            var key = weapon.Id + ":weapon-unlocks";
            Change((next, focus) =>
            {
                if (total - remaining > (next.GetValueOrDefault(key)?.Count ?? 0))
                    next[key] = new(total - remaining, false);
                return focus;
            });
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
                var key = weapon.Id + ":" + rule.Id;
                if (key == "signal-50:sp-3" && kind == "progress-snapshot" &&
                    ProgressFeed.Text(item, "context") == "camo-menu" && count is int menuCount && menuCount < rule.Goal)
                    next[key] = new(menuCount, false);
                else
                    ApplyProgress(next, key, count ?? 0, kind != "progress-snapshot");
                return focus;
            });
        }
        else if (kind == "point-blank-kill")
        {
            var objectives = weapon.Camos.Where(rule => rule.Type == "Special" && rule.Goal > 0 &&
                rule.Objective?.Contains("Point Blank Kills", StringComparison.OrdinalIgnoreCase) == true).ToArray();
            if (objectives.Length == 0) return;
            Change((next, focus) =>
            {
                foreach (var objective in objectives)
                {
                    var key = weapon.Id + ":" + objective.Id;
                    var current = next.GetValueOrDefault(key);
                    if (current?.Done != true) ApplyProgress(next, key, (current?.Count ?? 0) + 1, false);
                }
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
            foreach (var weapon in catalog.Weapons)
            {
                var prefix = weapon.Id + ":";
                var platinum = next.GetValueOrDefault(prefix + "platinum")?.Done == true;
                if (!platinum && next.GetValueOrDefault(prefix + "gold")?.Done != true) continue;
                foreach (var camo in weapon.Camos)
                {
                    if (camo.Type is not ("Military" or "Special") && !(platinum && camo.Id == "gold")) continue;
                    var key = prefix + camo.Id;
                    if (next.GetValueOrDefault(key)?.Done != true) ApplyProgress(next, key, 0, true);
                }
            }
            if (focus == state.Focus && next.Count == state.Progress.Count &&
                next.All(pair => state.Progress.GetValueOrDefault(pair.Key) == pair.Value)) return;
            SaveState(state with { Focus = focus, Progress = next });
        }
    }

    private void SaveState(State changed)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(changed, Json));
            File.Move(temporary, path, overwrite: true);
            state = changed;
        }
        finally { File.Delete(temporary); }
    }

    private static bool IsLevelRule(Rule camo) =>
        camo.Unlock?.StartsWith("Reach Weapon Level ", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsWeaponUnlockRule(Rule camo) =>
        camo.Unlock?.Contains("weapon unlock", StringComparison.OrdinalIgnoreCase) == true;

    private static string Key(string text) => Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
}
