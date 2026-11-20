#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CamoConnector.Detector;

public sealed record HudLine(string Text, double Y);
public sealed record CamoReference(string Name, byte[]? Rgb, int Slot = 0, int? Goal = null, byte[]? BannerRgb = null)
{
    public CamoReference(string name, byte[]? rgb) : this(name, rgb, 0, null) { }
}

public static class TrackedCamoRecognition
{
    private static string Key(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]", "");

    public static bool IsPanel(string text) => Regex.IsMatch(text, @"\bcam[o0]s\b", RegexOptions.IgnoreCase);

    public static HudLine[] ReadTsv(string tsv, int height)
    {
        var words = new List<(string Line, string Text, int Y, int X)>();
        foreach (var item in tsv.Split('\n'))
        {
            var columns = item.Split('\t', 12);
            if (columns.Length != 12 || columns[0] != "5" || string.IsNullOrWhiteSpace(columns[11]) ||
                !int.TryParse(columns[6], NumberStyles.None, CultureInfo.InvariantCulture, out var x) ||
                !int.TryParse(columns[7], NumberStyles.None, CultureInfo.InvariantCulture, out var y)) continue;
            words.Add((string.Join(":", columns[1..5]), columns[11].Trim(), y, x));
        }
        return words.GroupBy(word => word.Line).Select(line => new HudLine(
            string.Join(" ", line.OrderBy(word => word.X).Select(word => word.Text)), line.Min(word => word.Y) / (double)height))
            .OrderBy(line => line.Y).ToArray();
    }

    public static CamoProgress[] ReadRows(HudLine[] lines, IReadOnlyDictionary<string, CamoReference[]> catalog,
        Func<double, byte[]?> thumbnail, HudLine[]? counterLines = null)
    {
        var headers = lines.Select(line => (line.Y, Name: ReadWeapon(line.Text))).Where(row => row.Name != null).ToArray();
        var result = new List<CamoProgress>();
        for (var i = 0; i < headers.Length; i++)
        {
            var (y, name) = headers[i];
            var limit = Math.Min(y + .40, i + 1 < headers.Length ? headers[i + 1].Y : 1);
            var rows = lines.Where(line => line.Y > y && line.Y < limit).Select(line => line.Text).ToArray();
            var text = string.Join(" ", rows);
            var level = Regex.Match(text, @"\bReach\s+Weapon\s+Level\s+([0-9]{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var counterText = counterLines == null ? text :
                string.Join(" ", counterLines.Where(line => line.Y > y && line.Y < limit).Select(line => line.Text));
            var counters = Regex.Matches(counterText, @"(?<![0-9/])([0-9]{1,4})\s*[/|]\s*([0-9]{1,4})(?![0-9/])");
            if (counters.Count != 1) continue;
            var count = int.Parse(counters[0].Groups[1].Value, CultureInfo.InvariantCulture);
            var goal = int.Parse(counters[0].Groups[2].Value, CultureInfo.InvariantCulture);
            if (goal == 0 || count > goal) continue;
            if (level.Success)
            {
                if (int.Parse(level.Groups[1].Value, CultureInfo.InvariantCulture) == goal)
                {
                    var objective = $"Reach Weapon Level {goal}";
                    result.Add(new(name!, objective, count, goal) { WeaponLevelObjective = objective });
                }
                continue;
            }

            if (!catalog.TryGetValue(name!, out var entries)) continue;
            if (goal == 75 && (Regex.IsMatch(text, @"\bplatinum\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(Key(text), @"whilepr[o0]ne")))
            {
                result.Add(new(name!, "Platinum", count, goal, "name"));
                continue;
            }
            var names = rows.Select(row => Key(Regex.Split(row, @"\s*[·•:]\s*|\s+(?:[-–—]|[0-9]+\s*[/|]\s*[0-9]+)", RegexOptions.CultureInvariant)[0]))
                .ToHashSet(StringComparer.Ordinal);
            var named = entries.Select(entry => entry.Name).Where(camo => names.Contains(Key(camo))).Distinct().ToArray();
            if (named.Length > 1) continue;
            var objectiveCamo = name == ".50 GS" ? (goal, Key(text)) switch
            {
                (20, var objective) when objective.Contains("get20headshotkills", StringComparison.Ordinal) => "Frosted",
                (30, var objective) when objective.Contains("get30pointblankkills", StringComparison.Ordinal) => "Red Tiger",
                _ => null
            } : null;
            if (objectiveCamo != null && !entries.Any(entry => entry.Name == objectiveCamo)) objectiveCamo = null;
            var camo = named.SingleOrDefault() ?? objectiveCamo;
            if (camo == null)
            {
                var artwork = MatchArtwork(thumbnail(y), entries);
                if (entries.Any(entry => entry.Name == artwork &&
                    (entry.Goal == goal || (entry.Goal == null && goal != 75)))) camo = artwork;
            }
            if (camo != null) result.Add(new(name!, camo, count, goal, named.Length == 1 || objectiveCamo != null ? "name" : "artwork"));
        }
        return result.GroupBy(row => (row.Weapon, row.Camo))
            .Select(group => group.Distinct().ToArray()).Where(group => group.Length == 1).Select(group => group[0]).ToArray();
    }

    public static CamoProgress[] MergeRows(CamoProgress[] primary, CamoProgress[] fallback) =>
        primary.Concat(fallback.Where(candidate =>
            !primary.Any(confirmed => confirmed.Weapon == candidate.Weapon && confirmed.Camo == candidate.Camo)))
            .ToArray();

    internal static string? ReadWeapon(string label)
    {
        label = Regex.Replace(label, @"\s*\bLAST\s+USED\b.*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var key = Key(label);
        // The lobby's last-used badge can be joined to the weapon name or misread as LA5T U5ED.
        if (Regex.IsMatch(key, @"\Asignal[5s][0o8](?:la[s5]tu[s5]ed)?\z")) return "Signal 50";
        if (key == "rpgb") return "RPG-8";
        if (Regex.IsMatch(key, @"\A(?:(?:krait|karit|crate)p?[g6][8b]|krait)(?:xp)?\z")) return "Krait P68";
        return WeaponRecognition.Read(new[] { label }, false)?.Name;
    }

    public static string? MatchArtwork(byte[]? sample, CamoReference[] entries)
    {
        if (sample is not { Length: 432 }) return null;
        var ranked = entries.Where(entry => entry.Rgb is { Length: 432 })
            .Select(entry => (entry.Name, Score: entry.Rgb!.Select((channel, index) => Math.Abs(channel - sample[index])).Average()))
            .OrderBy(entry => entry.Score).Take(2).ToArray();
        return ranked.Length == 2 && ranked[0].Score <= 31 && ranked[1].Score - ranked[0].Score >= 8 ? ranked[0].Name : null;
    }
}
