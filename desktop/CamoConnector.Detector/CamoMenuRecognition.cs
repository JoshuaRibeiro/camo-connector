#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CamoConnector.Detector;

public sealed record CamoProgress(string Weapon, string Camo, int Count, int Goal, string Identity = "name")
{
    public int? WeaponUnlocksRemaining { get; init; }
    public string? WeaponLevelObjective { get; init; }
}

public static class CamoMenuRecognition
{
    private static string Key(string text) => Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]", "");

    public static bool IsCamoMenu(string text) =>
        Key(text) is "camoselect" or "select" ||
        Regex.IsMatch(text, @"\bCAM[O0]\s+SELECT\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static int? ReadMilitaryMenuCount(string text)
    {
        var match = Regex.Match(text, @"\bMILITARY\s*[\[|L1]?\s*([0-8])\s*/\s*8\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value[0] - '0' : null;
    }

    public static bool ConfirmsMilitaryCounter(int tier, string text) =>
        Regex.IsMatch(text.Trim(), @"\A[\[|(L1]?\s*" + tier + @"\s*/\s*8\s*[\]|)]?\z",
            RegexOptions.CultureInvariant);

    public static string? ReadProgressionWeapon(string text)
    {
        // The short active tab can share a crop with Mode Mastery or stray UI glyphs.
        var label = Regex.Match(text, @"\bPROGRESSION\b(?=\s*[|=])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (label.Success) text = text[..(label.Index + label.Length)];
        // The tab font can make a zero look like an at sign in OCR.
        var key = Key(text.Replace('@', '0'));
        const string suffix = "progression";
        // The crop can end partway through the neighboring Mode Mastery label.
        key = Regex.Replace(key, @"(?<=progression)modemast(?:e(?:ry?)?)?\z", "");
        if (Regex.IsMatch(key, @"\Afsscarbonedge1[068]95progressionfi\z"))
            return "FSS Carbon Edge 1095";
        if (!key.EndsWith(suffix, StringComparison.Ordinal)) return null;
        var name = key[..^suffix.Length];
        // Exact confusions observed in the progression tab at lower capture resolutions.
        if (name == "signal58") return "Signal 50";
        if (name == "pm48anvil") return "PM-40 Anvil";
        if (name == "tsonightshade") return "ISO Nightshade";
        if (name is "fsscarbonedge1695" or "fsscarbonedge1895") return "FSS Carbon Edge 1095";
        if (name == "58gs") return ".50 GS";
        if (name == "08gs" || Regex.IsMatch(name, @"\A[5s][0o][g6][s5]\z")) return ".50 GS";
        if (Regex.IsMatch(name, @"\A(?:krait|karit|crate)p[g6][8b]\z")) return "Krait P68";
        return WeaponRecognition.Read(new[] { name }, false)?.Name;
    }

    public static string? ReadBreadcrumbWeapon(string text)
    {
        var parts = text.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length is < 3 or > 4 || (parts.Length == 4 && parts[3].Length != 0) ||
            Key(parts[0]) != "createaclass" || Key(parts[1]) != "editcustomclass") return null;
        return WeaponRecognition.Read([parts[2]], false)?.Name;
    }

    public static string? ReadKnownCamoTitle(string text, string[] knownCamos)
    {
        var lines = text.Split('\n').Select(Key).ToHashSet(StringComparer.Ordinal);
        var matches = knownCamos.Where(name => lines.Contains(Key(name)) ||
            lines.Contains("6" + Key(name)) ||
            (name == "Oni" && (lines.Contains("uni") || lines.Contains("oonl")))).Distinct().Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public static CamoProgress? CheckObjectiveGoal(CamoProgress? progress, int? catalogGoal, string objectives)
    {
        if (progress == null || catalogGoal == null || progress.Goal == catalogGoal) return progress;
        var goal = catalogGoal.Value;
        // Correct a trailing zero read as eight only when the objective text independently agrees with the catalog.
        if (goal % 10 != 0 || progress.Goal != goal + 8 || progress.Count > goal ||
            !Regex.IsMatch(objectives, @"(?im)^\s*[-–—]*\s*get\s+" + goal + @"\b")) return null;
        return progress with { Goal = goal };
    }

    public static bool MatchesCamoObjective(string text, string? objective)
    {
        if (string.IsNullOrWhiteSpace(objective)) return false;
        var expected = Key(objective);
        if (expected.StartsWith("get", StringComparison.Ordinal)) expected = expected[3..];
        return Key(text).Contains(expected, StringComparison.Ordinal);
    }

    public static CamoProgress? ConfirmCounter(CamoProgress? context, CamoProgress? counter, CamoProgress? alternate)
    {
        if (counter == null || (context != null && context != counter)) return null;
        return context == counter || alternate == counter ? counter : null;
    }

    public static CamoProgress? ReadWeaponUnlocks(string weapon, string title, string objectives, int total)
    {
        var requirements = Regex.Matches(objectives, @"\bEarn\s+(\d{1,3})\s+more\s+weapon\s+unlocks?\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (requirements.Count != 1) return null;
        var remaining = int.Parse(requirements[0].Groups[1].Value);
        return remaining > 0 && remaining < total
            ? new(weapon, title, total - remaining, total) { WeaponUnlocksRemaining = remaining }
            : null;
    }

    public static CamoProgress? ReadMilitaryLevel(string weapon, string title, string objectives)
    {
        if (!Regex.IsMatch(Key(title), @"\Amilitarycam[o0][1-8]\z")) return null;
        var requirement = Regex.Match(objectives, @"\bReach\s+Weapon\s+Level\s+(\d{1,3})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!requirement.Success) return null;
        var meters = Regex.Matches(objectives, @"(?<![\d/])(\d{1,3})\s*/\s*(\d{1,3})(?![\d/])");
        if (meters.Count != 1 || meters[0].Groups[2].Value != requirement.Groups[1].Value) return null;
        var goal = int.Parse(requirement.Groups[1].Value);
        var count = int.Parse(meters[0].Groups[1].Value);
        var objective = $"Reach Weapon Level {goal}";
        return count >= 1 && count <= goal
            ? new(weapon, objective, count, goal) { WeaponLevelObjective = objective }
            : null;
    }

    public static CamoProgress? ReadProgress(string weapon, string title, string[] lines, string[] knownCamos)
    {
        title = title.Trim();
        if (!Regex.IsMatch(title, @"\A[a-z0-9][a-z0-9 .-]{1,65}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            Regex.IsMatch(Key(title), @"\Amilitarycam[o0]")) return null;
        var matches = knownCamos.Where(name => Key(name) == Key(title)).Distinct(StringComparer.Ordinal).ToArray();
        if (matches.Length > 1) return null;
        var camo = matches.Length == 1 ? matches[0] : title;
        var counts = new HashSet<(int Count, int Goal)>();
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var fraction = Regex.Match(line, @"(?<![\d/])(\d{1,4})\s*/\s*(\d{1,4})(?![\d/])");
            if (!fraction.Success) continue;
            var description = line[..fraction.Index].Trim();
            if (description.Length == 0 && index > 0) description = lines[index - 1];
            if (Regex.IsMatch(description, @"weapon unlock|weapon level|camo challenges|complete\s+.*camos?",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
            if (Regex.IsMatch(line[(fraction.Index + fraction.Length)..], @"\d\s*/\s*\d")) return null;
            var count = int.Parse(fraction.Groups[1].Value);
            var goal = int.Parse(fraction.Groups[2].Value);
            if (goal > 0 && count <= goal) counts.Add((count, goal));
        }
        if (counts.Count != 1) return null;
        var meter = counts.Single();
        return new(weapon, camo, meter.Count, meter.Goal);
    }
}

public sealed class CamoProgressConfirmation
{
    private Dictionary<(string Weapon, string Camo), CamoProgress> previous = new();
    private readonly Dictionary<(string Weapon, string Camo), CamoProgress> published = new();

    public void Forget(string weapon, string camo)
    {
        var key = (weapon, camo.ToLowerInvariant());
        previous.Remove(key);
        published.Remove(key);
    }

    public CamoProgress? Observe(CamoProgress? read)
        => ObserveRows(read == null ? [] : [read]).SingleOrDefault();

    public CamoProgress[] ObserveRows(CamoProgress[] reads)
    {
        var current = reads.GroupBy(read => (read.Weapon, read.Camo.ToLowerInvariant()))
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single());
        var confirmed = new List<CamoProgress>();
        foreach (var (key, read) in current)
        {
            if (!previous.TryGetValue(key, out var candidate) || candidate.Count != read.Count || candidate.Goal != read.Goal ||
                (published.TryGetValue(key, out var old) && old.Count == read.Count && old.Goal == read.Goal)) continue;
            published[key] = read;
            confirmed.Add(read);
        }
        previous = current;
        return confirmed.ToArray();
    }
}
