#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CamoConnector.Detector;

public sealed record SpecialUnlock(string Weapon, string Challenge, string Kind);

public static class SpecialUnlocks
{
    public static string? ReadBanner(string heading, string detail)
    {
        string Compact(string text) => Regex.Replace(text.ToUpperInvariant(), "[^A-Z0-9]", "");
        if (!Regex.IsMatch(Compact(heading), "CHALLENGE(?:C[O0])?MPLETED")) return null;
        var text = Compact(detail);
        if (Regex.IsMatch(text, "25PLAYERKILLSWHILEAIMINGDOWNSIGHTS?")) return "ads-25";
        if (text.Contains("3PLAYERKILLSWITHOUTDYING5TIMES", StringComparison.Ordinal)) return "streak-3x5";
        if (text.Contains("50PLAYERKILLSWHILEUSINGAKIMBO", StringComparison.Ordinal)) return "menu-space-time";
        return null;
    }
    public static int? ReadCount(string text)
    {
        var match = Regex.Match(text, @"\bSPECIAL\s*(?:\[|1)?\s*([0-3])\s*/\s*3(?:\]|1)?(?![0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    public static SpecialUnlock[] Resolve(string weapon, int count, double[] scores, CamoReference[] catalog)
    {
        if (count is <= 0 or > 3) return [];
        var slots = Enumerable.Range(1, 3).ToArray();
        if (count != 3)
        {
            if (scores.Length != 3 || scores.Any(score => !double.IsFinite(score) || score is >= .55 and < .70))
                return [];
            slots = slots.Where(slot => scores[slot - 1] < .55).ToArray();
            if (slots.Length != count) return [];
        }
        if (weapon == "Sang 9mm")
        {
            string[] names = ["menu-autumn", "menu-snakeskin", "menu-space-time"];
            return slots.Select(slot => new SpecialUnlock(weapon, names[slot - 1], "challenge-completed")).ToArray();
        }
        if (catalog.Length != 3 || catalog.Select(entry => entry.Slot).Distinct().Count() != 3 ||
            catalog.Any(entry => entry.Slot is < 1 or > 3)) return [];
        return catalog.Where(entry => entry.Goal is > 0 && slots.Contains(entry.Slot))
            .Select(entry => new SpecialUnlock(weapon, entry.Name, "catalog-special-unlocked")).ToArray();
    }
}

public sealed class SpecialUnlockConfirmation
{
    private HashSet<SpecialUnlock> previous = new();
    private readonly HashSet<SpecialUnlock> published = new();

    public SpecialUnlock[] Observe(SpecialUnlock[] reads)
    {
        var current = reads.ToHashSet();
        var confirmed = current.Where(unlock => previous.Contains(unlock) && published.Add(unlock)).ToArray();
        previous = current;
        return confirmed;
    }
}
