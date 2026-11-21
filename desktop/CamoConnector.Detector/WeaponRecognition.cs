#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CamoConnector.Detector;

public sealed record WeaponRead(string Name, bool Akimbo);
public sealed record WeaponLevelRead(string Weapon, int Level);
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
            if (Regex.IsMatch(key, @"\A[5s][0o][g6][s5]\z")) return new(".50 GS", false);
            if (Regex.IsMatch(key, @"\A(?:k[g6]7?)?vu[li1]can\z")) return new("KG-7 Vulcan", false);
        }
        return null;
    }

    public static WeaponRead? ReadHud(string[] lines, bool wideRegion)
    {
        var read = Read(lines, wideRegion);
        if (read != null || wideRegion) return read;
        foreach (var line in lines)
        {
            var name = Key(line) switch
            {
                "08gs" or "58gs" => ".50 GS",
                "patrietxmr" or "patrietkmr" => "Patriot XMR",
                "kg7drillcharge" or "k67drillcharge" or "kg7" or "k67" => "KG-7 Vulcan",
                "skgfergiean" => "KG-7 Vulcan",
                "mare" => "Mar-9",
                "anvil" => "PM-40 Anvil",
                "kraitp68commando" or "kraitp68comm" => "Krait P68",
                "w225striga" => "WZ.55 Striga",
                "raze12" or "razi12" or "razin12" => "Rezi 12",
                _ => null
            };
            if (name != null) return new(name, false);
        }
        return null;
    }

    internal static bool IsLevelUpHeading(string text) =>
        Key(text) is "newunlock" or "weaponlevelup" or "weaponleveledup" or "weaponlevelledup";

    public static WeaponLevelRead? ReadLevelUp(string heading, string name, string number)
    {
        if (!IsLevelUpHeading(heading)) return null;
        var weapon = Read([name], false);
        var match = Regex.Match(number.Trim(), @"\A(?:LEVEL\s+)?([0-9]{1,3})\z", RegexOptions.IgnoreCase);
        if (weapon == null || !match.Success || !int.TryParse(match.Groups[1].Value, out var level) ||
            level is < 1 or > 200) return null;
        return new(weapon.Name, level);
    }

    public static bool IsReplay(string text) => Regex.IsMatch(text,
        @"KILL\s*CAM|FINAL\s+KILL|BEST\s+PLAY|SPECTAT(?:ING|OR)|SKIP\s+(?:THE\s+)?KILL",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsWeaponSummary(string heading) =>
        Key(heading) is "topperformingweapon" or "topperformingweapons";

    public static MilitaryUnlock? ReadMilitarySummary(string heading, string weaponLabel, string card)
    {
        if (!IsWeaponSummary(heading)) return null;
        var weapon = Read(new[] { weaponLabel }, false);
        var tier = Regex.Match(Key(card), @"\Amilitarycam[o0]([1-8])\z");
        return weapon is { Akimbo: false } && tier.Success ? new(weapon.Name, tier.Groups[1].Value[0] - '0') : null;
    }

    internal static bool IsUnlockHeader(string text) =>
        Regex.IsMatch(Key(text), @"\Ai?un[l1iu][o0][ceg][i1]?ked\z");

    public static MilitaryUnlock? ReadMilitaryUnlock(string[] lines)
    {
        for (var header = 0; header + 2 < lines.Length; header++)
        {
            if (!IsUnlockHeader(lines[header])) continue;
            var weaponLabel = lines[header + 1];
            var weapon = Read(new[] { weaponLabel }, false);
            if (weapon == null && Key(weaponLabel) is "rpcb" or "robb" or "rpbb" or "rpb8")
                weapon = new WeaponRead("RPG-8", false);
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
    private DateTimeOffset? previousAt;

    public void Reset()
    {
        previous = published = null;
        previousAt = null;
    }

    public WeaponRead? Observe(WeaponRead? read, bool replay, DateTimeOffset? observedAt = null)
    {
        // A short HUD dropout can occur when switching weapons or aiming.
        if (replay || observedAt == null && read == null ||
            previousAt.HasValue && observedAt.HasValue && observedAt.Value - previousAt.Value > TimeSpan.FromMilliseconds(1750))
        {
            previous = null;
            previousAt = null;
        }
        if (replay || read == null) return null;
        var stable = read == previous;
        previous = read;
        previousAt = observedAt;
        if (!stable || read == published) return null;
        return published = read;
    }
}
