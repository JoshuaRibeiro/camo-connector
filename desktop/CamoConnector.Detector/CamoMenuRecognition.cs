#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CamoConnector.Detector;

public sealed record CamoProgress(string Weapon, string Camo, int Count, int Goal, string Identity = "name");

public static class CamoMenuRecognition
{
    private static string Key(string text) => Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]", "");

    public static bool IsCamoMenu(string text) =>
        Key(text) is "camoselect" or "select" ||
        Regex.IsMatch(text, @"\bCAM[O0]\s+SELECT\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static int? ReadMilitaryMenuCount(string text)
    {
        var match = Regex.Match(text, @"\bMILITARY\s*\[?\s*([0-8])\s*/\s*8\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value[0] - '0' : null;
    }

    public static string? ReadProgressionWeapon(string text)
    {
        var key = Key(text);
        const string suffix = "progression";
        if (!key.EndsWith(suffix, StringComparison.Ordinal)) return null;
        var name = key[..^suffix.Length];
        if (name == "08gs" || Regex.IsMatch(name, @"\A[5s][0o][g6][s5]\z")) return ".50 GS";
        if (Regex.IsMatch(name, @"\A(?:krait|karit|crate)p[g6][8b]\z")) return "Krait P68";
        return WeaponRecognition.Read(new[] { name }, false)?.Name;
    }

    public static string? ReadKnownCamoTitle(string text, string[] knownCamos)
    {
        var lines = text.Split('\n').Select(Key).ToHashSet(StringComparer.Ordinal);
        var matches = knownCamos.Where(name => lines.Contains(Key(name))).Distinct().Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
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
