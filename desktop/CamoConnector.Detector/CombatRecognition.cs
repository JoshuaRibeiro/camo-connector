#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CamoConnector.Detector;

public sealed record CombatRead(bool Kill, bool Death, int? Ordinal);
public sealed record CombatEvent(string Kind, int Kills, int Deaths, int KillStreak, int BestStreak, int EndedStreak);

public static class CombatRecognition
{
    private const RegexOptions IgnoreCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static CombatRead Read(string kill, string death, string card)
    {
        var ordinal = Regex.Match(card, @"\b([0-9]{1,3})(?:ST|ND|RD|TH)\s+K[I1L]LL\b", IgnoreCase);
        return new(ordinal.Success || Regex.IsMatch(kill, @"\A\s*[^a-z0-9]*K[I1L]LL\s*[^a-z0-9]*\z", IgnoreCase),
            Regex.IsMatch(Regex.Replace(death, "[^a-zA-Z0-9]", ""), "K[I1L]LLEDBY", IgnoreCase),
            ordinal.Success ? int.Parse(ordinal.Groups[1].Value) : null);
    }

    public static bool IsLongshot(string text) => Regex.IsMatch(text, @"\bL[O0]NG[\s-]*SH[O0]T\b", IgnoreCase);
    public static bool IsPointBlank(string text) => Regex.IsMatch(text, @"\bP[O0][I1L]NT[\s-]*BLANK\b", IgnoreCase);

    public static string? ReadLobby(string text)
    {
        if (Regex.IsMatch(text, @"\bMATCH[\s.:|]+(?:IS[\s.:|]+)?ABOUT[\s.:|]+TO[\s.:|]+BEG[I1L]N\b", IgnoreCase))
            return "match-confirmed";
        if (Regex.IsMatch(text, @"\bMAP\s+VOTING\b", IgnoreCase) &&
            Regex.IsMatch(text, @"\bTIME\s+REMAINING\b", IgnoreCase)) return "match-voting";
        if (Regex.IsMatch(text, @"\b(?:VICTORY|DEFEAT|DRAW)\b", IgnoreCase) &&
            Regex.IsMatch(text, @"\b(?:LIMIT\s+REACHED|MATCH\s+ENDED)\b", IgnoreCase)) return "match-ended";
        if (Regex.IsMatch(text, @"\bSEARCHING\s+FOR\s+A\s+MATCH\b", IgnoreCase))
            return "match-ended";
        return null;
    }

    public static int ReadCompletionistCount(string text)
    {
        var counter = Regex.Match(text, @"\bCOMPLETIONIST\s*[\[|(]?\s*([0-4])\s*/\s*4\b", IgnoreCase);
        return counter.Success ? counter.Groups[1].Value[0] - '0' : 0;
    }

    // Interior of the Platinum swatch from a verified in-game completion banner.
    private static readonly byte[] PlatinumArtwork = Convert.FromBase64String(
        "q6CalpCNgnRrbGxrp5uSjYuLgnd0cGdZm5aQioOGgnp2cmhPmJCNi4Z/e3BsamFGlIuGhn9yaGBdV05Chn9xbGliWVZSTUdBdWtlYl9VU1BLSkNAbGdfWlxWV09LSUVHZ2dgWFhXVVJNSUlGYV1dWlhWU1NRTUpGYFtdWldXVFFRTktLX11cWllYVVNQTk5K");
    private static readonly double[] PlatinumTexture = ArtworkTexture(Array.ConvertAll(PlatinumArtwork, value => (double)value));

    public static bool IsPlatinumArtwork(byte[]? rgb)
    {
        if (rgb is not { Length: 432 }) return false;
        var luminance = new double[144];
        var colorDifference = 0d;
        var brightnessDifference = 0d;
        for (var i = 0; i < luminance.Length; i++)
        {
            var red = rgb[i * 3];
            var green = rgb[i * 3 + 1];
            var blue = rgb[i * 3 + 2];
            luminance[i] = (red + green + blue) / 3d;
            colorDifference += Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue));
            brightnessDifference += Math.Abs(luminance[i] - PlatinumArtwork[i]);
        }
        if (colorDifference / 144 > 17 || brightnessDifference / 144 > 23) return false;
        var texture = ArtworkTexture(luminance);
        var overlap = 0d;
        var energy = 0d;
        var referenceEnergy = 0d;
        for (var i = 0; i < texture.Length; i++)
        {
            overlap += texture[i] * PlatinumTexture[i];
            energy += texture[i] * texture[i];
            referenceEnergy += PlatinumTexture[i] * PlatinumTexture[i];
        }
        return energy >= referenceEnergy * .2 && overlap >= .65 * Math.Sqrt(energy * referenceEnergy);
    }

    private static double[] ArtworkTexture(double[] pixels)
    {
        // Compare texture after removing the horizontal and vertical lighting gradients.
        var rows = new double[12];
        var columns = new double[12];
        var mean = 0d;
        for (var i = 0; i < pixels.Length; i++)
        {
            rows[i / 12] += pixels[i] / 12;
            columns[i % 12] += pixels[i] / 12;
            mean += pixels[i] / pixels.Length;
        }
        var texture = new double[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
            texture[i] = pixels[i] - rows[i / 12] - columns[i % 12] + mean;
        return texture;
    }

    public static bool IsGoldBanner(string heading, string detail, bool goldTile)
    {
        string Compact(string text) => Regex.Replace(text.ToUpperInvariant(), "[^A-Z0-9]", "");
        var objective = Compact(detail);
        const string goldGoal = @"15PLAYERKIL[LIE][S5]P?INASINGLEMATCH";
        return (goldTile || Compact(heading).Contains("CHALLENGECOMPLETED", StringComparison.Ordinal) ||
            Regex.IsMatch(heading, @"\bCOMPLETED\b", IgnoreCase)) && Regex.IsMatch(objective, goldGoal) ||
            goldTile && Regex.IsMatch(objective, @"\A(?:GET)?15PLAYER(?:KILLS)?(?:IN)?ASINGLEMATCH\z");
    }

    internal static bool IsSplitGoldObjective(string start, string end) =>
        Regex.IsMatch(start, @"\b15\s+PLAYER\b", IgnoreCase) &&
        Regex.IsMatch(end, @"\bIN\s+A\s+SINGLE\s+MATCH", IgnoreCase);
}

public sealed class CombatConfirmation
{
    private DateTimeOffset? killSeen, deathSeen;
    private int? lastOrdinal;
    public int Kills { get; private set; }
    public int Deaths { get; private set; }
    public int Streak { get; private set; }
    public int Best { get; private set; }

    public void Reset()
    {
        killSeen = deathSeen = null;
        lastOrdinal = null;
        Kills = Deaths = Streak = Best = 0;
    }

    public CombatEvent[] Observe(CombatRead? read, DateTimeOffset now)
    {
        if (read == null) return [];
        var events = new List<CombatEvent>(2);
        if (read.Kill)
        {
            if (killSeen == null || read.Ordinal != null && lastOrdinal != null && read.Ordinal != lastOrdinal)
            {
                Kills++;
                Best = Math.Max(++Streak, Best);
                events.Add(new("kill", Kills, Deaths, Streak, Best, 0));
            }
            killSeen = now;
            if (read.Ordinal.HasValue) lastOrdinal = read.Ordinal;
        }
        else if (killSeen.HasValue && now - killSeen.Value >= TimeSpan.FromMilliseconds(800))
        {
            killSeen = null;
            lastOrdinal = null;
        }
        if (read.Death)
        {
            if (deathSeen == null)
            {
                Deaths++;
                var ended = Streak;
                Streak = 0;
                events.Add(new("death", Kills, Deaths, Streak, Best, ended));
            }
            deathSeen = now;
        }
        else if (deathSeen.HasValue && now - deathSeen.Value >= TimeSpan.FromSeconds(2))
            deathSeen = null;
        return events.ToArray();
    }
}
