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

    public static string? ReadLobby(string text)
    {
        if (Regex.IsMatch(text, @"\bMATCH[\s.:|]+(?:IS[\s.:|]+)?ABOUT[\s.:|]+TO[\s.:|]+BEG[I1L]N\b", IgnoreCase))
            return "match-confirmed";
        if (Regex.IsMatch(text, @"\bMAP\s+VOTING\b", IgnoreCase) &&
            Regex.IsMatch(text, @"\bTIME\s+REMAINING\b", IgnoreCase)) return "match-voting";
        if (Regex.IsMatch(text, @"\b(?:VICTORY|DEFEAT|DRAW)\b", IgnoreCase) &&
            Regex.IsMatch(text, @"\b(?:LIMIT\s+REACHED|MATCH\s+ENDED)\b", IgnoreCase)) return "match-ended";
        return null;
    }

    public static bool HasCompletionistGold(string text) =>
        Regex.IsMatch(text, @"\bCOMPLETIONIST\s*\[?\s*[1-4]\s*/\s*4\b", IgnoreCase);

    public static bool IsGoldBanner(string heading, string detail, bool goldTile)
    {
        string Compact(string text) => Regex.Replace(text.ToUpperInvariant(), "[^A-Z0-9]", "");
        var objective = Compact(detail);
        return (Compact(heading).Contains("CHALLENGECOMPLETED", StringComparison.Ordinal) ||
            Regex.IsMatch(heading, @"\bCOMPLETED\b", IgnoreCase)) &&
            objective.Contains("15PLAYERKILLSINASINGLEMATCH", StringComparison.Ordinal) ||
            goldTile && Regex.IsMatch(objective, @"\AGET15PLAYER(?:KILLS)?(?:IN)?ASINGLEMATCH\z");
    }
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
