#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CamoConnector.Detector;

public sealed record WeaponRead(string Name, bool Akimbo);
public sealed record MilitaryUnlock(string Weapon, int Tier);

public static class WeaponRecognition
{
    private static readonly Dictionary<string, string> Roster = BuildRoster();

    private static Dictionary<string, string> BuildRoster()
    {
        var names = new[]
        {
            "Kastov 762", "Finn LMG", "FSS Carbon Edge 1095", "Han 86", "ISO Nightshade",
            "KG-7 Vulcan", "Mar-9", "Pila", "Rezi 12", "Sang 9mm", "Vel 46", "Krait P68",
            "Type 73", "Oris 8.6", "M4", "WZ.55 Striga", "Hyeon Burst", "RPG-8", "Astaroth .57",
            "Patriot XMR", "PPSH-41", "Warlord", "Mosin-Nagant", "Oden", ".50 GS", "JOKR", "APOC-338",
            "X-58 Nyx", "Mojave .357", "Signal 50", "Axion", "PM-40 Anvil", "Cronen Squall",
            "Cronen Osprey", "CSG-35", "JAK 12", "ZLR Talonhawk"
        };
        var result = names.ToDictionary(Key);
        result.Add("signal5o", "Signal 50");
        result.Add("oris", "Oris 8.6");
        result.Add("0ris86", "Oris 8.6");
        result.Add("mosinnagan", "Mosin-Nagant");
        result.Add("sang", "Sang 9mm");
        return result;
    }

    private static string Key(string text) => Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]", "");

    public static WeaponRead? Read(string[] lines, bool wideRegion)
    {
        foreach (var line in lines.Prepend(string.Join(" ", lines)))
        {
            var key = Key(line);
            if (key is "sang9mmakimbo" or "sang9mmakimb0") return new("Sang 9mm", true);
            if (wideRegion) continue;
            if (Roster.TryGetValue(key, out var name)) return new(name, false);
            if (Regex.IsMatch(key, @"\A5[0o][g6][s5]\z")) return new(".50 GS", false);
            if (Regex.IsMatch(key, @"\A(?:k[g6]7?)?vu[li1]can\z")) return new("KG-7 Vulcan", false);
        }
        return null;
    }

    public static bool IsReplay(string text) => Regex.IsMatch(text,
        @"KILL\s*CAM|FINAL\s+KILL|BEST\s+PLAY|SPECTAT(?:ING|OR)|SKIP\s+(?:THE\s+)?KILL",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static MilitaryUnlock? ReadMilitaryUnlock(string[] lines)
    {
        for (var header = 0; header + 2 < lines.Length; header++)
        {
            if (!Regex.IsMatch(Key(lines[header]), @"\Ai?un[l1i][o0]c[i1]?ked\z")) continue;
            var weapon = Read(new[] { lines[header + 1] }, false);
            if (weapon == null || weapon.Akimbo) continue;
            var military = Key(lines[header + 2]);
            if (military == "militarycamo" && header + 3 < lines.Length)
                military += Key(lines[header + 3]);
            var match = Regex.Match(military, @"\Am[i1l][l1i][i1l]tarycam[o0]([1-8sg])\z");
            if (!match.Success) continue;
            var digit = match.Groups[1].Value[0];
            if (digit is 's' or 'g' && weapon.Name != "Sang 9mm") continue;
            return new(weapon.Name, digit switch { 's' => 5, 'g' => 6, _ => digit - '0' });
        }
        return null;
    }
}

public sealed class WeaponConfirmation
{
    private WeaponRead? previous;
    private WeaponRead? published;

    public void Reset() => previous = published = null;

    public WeaponRead? Observe(WeaponRead? read, bool replay)
    {
        if (replay || read == null) { previous = null; return null; }
        var stable = read == previous;
        previous = read;
        if (!stable || read == published) return null;
        return published = read;
    }
}
