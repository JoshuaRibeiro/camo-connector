#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CamoConnector.Bridge;

public sealed class ProgressFeed
{
    private readonly JsonElement catalog;
    private readonly string session = Guid.NewGuid().ToString();
    private readonly Dictionary<int, object> akimboTiming = new();
    private readonly Dictionary<string, TrackedRow> trackedRows = new();
    private readonly Dictionary<string, List<int>> catalogMilitary = new();
    private readonly List<int> military = new();
    private readonly List<string> challenges = new();
    private readonly HashSet<string> goldUnlocks = new();
    private readonly object stateLock = new();
    private int count, akimboCount, progressId;
    private string? weapon;
    private object? matchState, progress;

    public ProgressFeed(string catalogPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(catalogPath));
        catalog = document.RootElement.Clone();
    }

    internal static JsonElement Field(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : default;

    internal static string? Text(JsonElement item, string name) =>
        Field(item, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    internal static int? Number(JsonElement item, string name) =>
        Field(item, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number) ? number : null;

    private static object? Optional(JsonElement item, string name) =>
        Field(item, name) is { ValueKind: not JsonValueKind.Undefined } value ? value.Clone() : null;

    private static bool IsWeapon(string? value) =>
        value != null && Regex.IsMatch(value, @"\A[a-zA-Z0-9 .-]{2,32}\z");

    private static string CamoKey(string value) => Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]", "");

    public void Accept(string line)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(line.Trim().TrimStart('\uFEFF')); }
        catch (JsonException) { return; }
        using (document)
        lock (stateLock)
        {
            var item = document.RootElement;
            if (Text(item, "source") != "live") return;
            var kind = Text(item, "kind");
            var name = Text(item, "weapon");
            if (kind is "match-voting" or "match-confirmed" or "match-started" or "match-ended")
            {
                matchState = new { kind, match = Optional(item, "match"), matchMode = Text(item, "matchMode") };
                if (kind == "match-voting") weapon = null;
            }
            if (Text(item, "mode") != "mp") return;
            if (Text(item, "key") == "signal-50:sp-3" && Number(item, "delta") == 1) count++;
            switch (kind)
            {
                case "weapon" when IsWeapon(name):
                    weapon = name;
                    break;
                case "completionist-unlock" when IsWeapon(name) && Text(item, "challenge") == "Gold":
                    goldUnlocks.Add(name!);
                    break;
                case "military-unlock" when IsWeapon(name) && Number(item, "tier") is >= 1 and <= 8:
                    var tiers = military;
                    if (name != "Sang 9mm" && !catalogMilitary.TryGetValue(name!, out tiers!))
                        catalogMilitary.Add(name!, tiers = new List<int>());
                    var tier = Number(item, "tier")!.Value;
                    if (!tiers.Contains(tier)) tiers.Add(tier);
                    break;
                case "akimbo-kill" when name == "Sang 9mm":
                    akimboTiming[++akimboCount] = new
                    {
                        capturedAt = Text(item, "at"), detectedAt = Text(item, "detectedAt"),
                        writtenAt = Text(item, "writtenAt"), bridgeReadAt = DateTimeOffset.UtcNow.ToString("O")
                    };
                    break;
                case "challenge-completed":
                    var challenge = Text(item, "challenge");
                    var banner = challenge is "ads-25" or "streak-3x5";
                    var menu = challenge is "menu-autumn" or "menu-snakeskin" or "menu-space-time";
                    var id = name == "Sang 9mm" && (banner || menu) ? challenge :
                        string.IsNullOrEmpty(name) && banner ? "selected-" + challenge : null;
                    if (id != null && !challenges.Contains(id)) challenges.Add(id);
                    break;
                case "progress-snapshot":
                    AcceptSnapshot(item, name);
                    break;
                case "catalog-special-unlocked" when IsWeapon(name):
                    var camo = Text(item, "challenge");
                    var entry = CatalogEntry(name!, camo);
                    if (camo != null && Number(entry, "goal") is int goal)
                        SaveRow(name!, camo, "camo", goal, goal, null, null, true);
                    break;
            }
        }
    }

    private JsonElement CatalogEntry(string weaponName, string? camo)
    {
        var entries = Field(catalog, weaponName);
        return entries.ValueKind == JsonValueKind.Array
            ? entries.EnumerateArray().FirstOrDefault(item => Text(item, "name") == camo)
            : default;
    }

    private void AcceptSnapshot(JsonElement item, string? name)
    {
        if (Number(item, "count") is not int value || Number(item, "goal") is not int goal ||
            goal is <= 0 or > 9999 || value < 0 || value > goal) return;
        if (Text(item, "key") == "signal-50:sp-3")
        {
            if (goal == 40) progress = new { id = ++progressId, value, through = count };
            return;
        }
        var type = Text(item, "type");
        if (!IsWeapon(name) || type is not ("camo" or "weapon-level")) return;
        var camo = Text(item, "camo");
        var objective = Text(item, "objective");
        var identity = Text(item, "identity") == "artwork" ? "artwork" : null;
        if (type == "camo")
        {
            if (camo == null || camo.Length > 80 || CamoKey(camo).Length == 0) return;
            if (identity != null && string.IsNullOrEmpty(Text(CatalogEntry(name!, camo), "artworkRGB12"))) return;
        }
        else
        {
            if (objective == null || objective.Length is 0 or > 160) return;
            identity = null;
        }
        SaveRow(name!, camo, type, value, goal, objective, identity, false);
    }

    private void SaveRow(string name, string? camo, string type, int value, int goal, string? objective,
        string? identity, bool confirmed)
    {
        var spaceTime = name == "Sang 9mm" && type == "camo" && goal == 50 && CamoKey(camo!) == "spacetime";
        var key = spaceTime ? "sang9mm:space-time" :
            (type == "camo" ? name + "|" + CamoKey(camo!) : name + "|" + objective + "|" + goal).ToLowerInvariant();
        int? through = spaceTime ? akimboCount : null;
        if (trackedRows.TryGetValue(key, out var previous) &&
            (previous.confirmed == true || !confirmed && previous.value == value &&
                previous.goal == goal && previous.camo == camo && previous.akimboThrough == through)) return;
        trackedRows[key] = new TrackedRow(++progressId, name, camo, type, value, goal,
            type == "weapon-level" ? objective : null, identity, confirmed ? true : null, through);
    }

    public string Snapshot()
    {
        lock (stateLock)
        {
            return JsonSerializer.Serialize(new
            {
                session, count, akimboCount, akimboTiming, weapon, matchState, military, challenges, progress,
                trackedRows = trackedRows.Values, catalogMilitary = catalogMilitary.Select(pair => new object[] { pair.Key, pair.Value }),
                goldUnlocks
            });
        }
    }

    private sealed record TrackedRow(int id, string weapon, string? camo, string type, int value, int goal,
        string? objective, string? identity, bool? confirmed, int? akimboThrough);
}
