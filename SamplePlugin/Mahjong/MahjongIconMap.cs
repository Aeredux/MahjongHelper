using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SamplePlugin.Mahjong;

public sealed unsafe class MahjongIconMap
{
    private static readonly string[] ExpectedTileCodes =
    {
        "M1", "M2", "M3", "M4", "M5", "M6", "M7", "M8", "M9",
        "P1", "P2", "P3", "P4", "P5", "P6", "P7", "P8", "P9",
        "S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8", "S9",
        "EAST", "SOUTH", "WEST", "NORTH", "WHITE", "GREEN", "RED",
    };

    private static readonly string CacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MahjongHelper");
    private static readonly string CacheFilePath = Path.Combine(CacheDirectory, "icon_name_cache.json");
    private static readonly IReadOnlyDictionary<uint, string> BuiltInMappings = new Dictionary<uint, string>
    {
        // Sequential Doman Mahjong tile icons: 76041–76074
        // Manzu (Characters) M1–M9
        [76041] = "M1", [76042] = "M2", [76043] = "M3", [76044] = "M4", [76045] = "M5",
        [76046] = "M6", [76047] = "M7", [76048] = "M8", [76049] = "M9",
        // Pinzu (Circles) P1–P9
        [76050] = "P1", [76051] = "P2", [76052] = "P3", [76053] = "P4", [76054] = "P5",
        [76055] = "P6", [76056] = "P7", [76057] = "P8", [76058] = "P9",
        // Souzu (Bamboo) S1–S9
        [76059] = "S1", [76060] = "S2", [76061] = "S3", [76062] = "S4", [76063] = "S5",
        [76064] = "S6", [76065] = "S7", [76066] = "S8", [76067] = "S9",
        // Honors: Winds then Dragons
        [76068] = "EAST", [76069] = "SOUTH", [76070] = "WEST", [76071] = "NORTH",
        [76072] = "WHITE", [76073] = "GREEN", [76074] = "RED",
    };
    private static readonly HashSet<uint> LockedBuiltInIconIds = new(BuiltInMappings.Keys);

    private readonly ConcurrentDictionary<uint, string> _iconIdToTileCode = new();
    private readonly ConcurrentDictionary<uint, string> _conflictingMappings = new();
    private uint _pendingIconId;
    private string? _pendingTileCode;
    private int _pendingPairCount;

    public MahjongIconMap()
    {
        SeedBuiltInMappings();
        LoadCache();
    }

    public void ObserveHover(AtkUnitBase* addon, IReadOnlySet<uint>? eligibleIconIds = null)
    {
        try
        {
            if (addon == null || addon->AtkValuesCount <= 2)
                return;

            var nameValue = addon->AtkValues[1];
            var iconValue = addon->AtkValues[2];

            if (nameValue.Type != FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String && nameValue.Type != FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String8)
                return;

            if (iconValue.Type != FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int && iconValue.Type != FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt)
                return;

            var tileCode = TryParseTileCode($"{nameValue.String}");
            var iconId = iconValue.UInt;
            if (tileCode == null || iconId == 0)
                return;

            // Only learn from likely Mahjong tile icon IDs.
            if (!IsLikelyMahjongIconId(iconId))
                return;

            // Only learn when the icon ID is currently present in visible hand/drawn tiles.
            // This filters out stale or unrelated tooltip values.
            if (eligibleIconIds != null && eligibleIconIds.Count > 0 && !eligibleIconIds.Contains(iconId))
                return;

            // Avoid learning honor names from unstable hover contexts for now.
            // Suit tiles (with numeric rank) are significantly more reliable.
            if (!IsSuitTileCode(tileCode))
                return;

            // Require the same (iconId, tileCode) pair to be observed repeatedly
            // before learning. This avoids transient/stale hover value mismatches.
            if (_pendingIconId == iconId && string.Equals(_pendingTileCode, tileCode, StringComparison.Ordinal))
                _pendingPairCount++;
            else
            {
                _pendingIconId = iconId;
                _pendingTileCode = tileCode;
                _pendingPairCount = 1;
            }

            if (_pendingPairCount < 3)
                return;

            if (_iconIdToTileCode.TryGetValue(iconId, out var existingTileCode))
            {
                if (string.Equals(existingTileCode, tileCode, StringComparison.Ordinal))
                    return;

                // Never overwrite an existing learned mapping automatically.
                _conflictingMappings[iconId] = $"existing={existingTileCode}, observed={tileCode}";
                return;
            }

            _iconIdToTileCode[iconId] = tileCode;
            SaveCache();
        }
        catch
        {
        }
    }

    public string? Resolve(uint iconId)
        => _iconIdToTileCode.TryGetValue(iconId, out var tileCode) ? tileCode : null;

    public IReadOnlyDictionary<uint, string> Snapshot()
        => new Dictionary<uint, string>(_iconIdToTileCode);

    public void ResetLearnedMappings()
    {
        _iconIdToTileCode.Clear();
        _conflictingMappings.Clear();
        _pendingIconId = 0;
        _pendingTileCode = null;
        _pendingPairCount = 0;
        SeedBuiltInMappings();
        SaveCache();
    }

    public bool ResetLearnedMapping(uint iconId)
    {
        if (LockedBuiltInIconIds.Contains(iconId))
            return false;

        var removed = _iconIdToTileCode.TryRemove(iconId, out _);
        _conflictingMappings.TryRemove(iconId, out _);
        if (removed)
            SaveCache();

        return removed;
    }

    public string BuildProgressReport(IEnumerable<uint>? observedIconIds = null)
    {
        var sb = new StringBuilder();
        var snapshot = Snapshot();
        var baselineMappings = snapshot
            .Where(pair => LockedBuiltInIconIds.Contains(pair.Key))
            .OrderBy(pair => pair.Key)
            .ToList();
        var learnedMappings = snapshot
            .Where(pair => !LockedBuiltInIconIds.Contains(pair.Key))
            .OrderBy(pair => pair.Key)
            .ToList();

        var knownTileCodes = new HashSet<string>(snapshot.Values, StringComparer.OrdinalIgnoreCase);
        var missingTileCodes = ExpectedTileCodes
            .Where(code => !knownTileCodes.Contains(code))
            .ToList();

        var unknownObserved = (observedIconIds ?? Array.Empty<uint>())
            .Distinct()
            .Where(IsLikelyMahjongIconId)
            .Where(iconId => !snapshot.ContainsKey(iconId))
            .OrderBy(iconId => iconId)
            .ToList();

        sb.AppendLine("Mahjong Icon Mapping Progress");
        sb.AppendLine($"Active icon mappings: {snapshot.Count}");
        sb.AppendLine($"Locked baseline mappings: {baselineMappings.Count}");
        sb.AppendLine($"Learned mappings: {learnedMappings.Count}");
        sb.AppendLine($"Known tile codes: {ExpectedTileCodes.Length - missingTileCodes.Count}/{ExpectedTileCodes.Length}");
        sb.AppendLine();

        sb.AppendLine("Missing tile codes:");
        if (missingTileCodes.Count == 0)
            sb.AppendLine("  (none)");
        else
            sb.AppendLine($"  {string.Join(" ", missingTileCodes)}");

        sb.AppendLine();
        sb.AppendLine("Unknown observed icon IDs (likely Mahjong range):");
        if (unknownObserved.Count == 0)
            sb.AppendLine("  (none)");
        else
            sb.AppendLine($"  {string.Join(", ", unknownObserved)}");

        sb.AppendLine();
        sb.AppendLine("Locked baseline icon -> tile mappings:");
        foreach (var pair in baselineMappings)
            sb.AppendLine($"  {pair.Key} -> {pair.Value}");

        sb.AppendLine();
        sb.AppendLine("Learned icon -> tile mappings:");
        if (learnedMappings.Count == 0)
            sb.AppendLine("  (none)");
        else
        {
            foreach (var pair in learnedMappings)
                sb.AppendLine($"  {pair.Key} -> {pair.Value}");
        }

        sb.AppendLine();
        sb.AppendLine("All active icon -> tile mappings:");
        foreach (var pair in snapshot.OrderBy(pair => pair.Key))
            sb.AppendLine($"  {pair.Key} -> {pair.Value}");

        sb.AppendLine();
        sb.AppendLine("Conflicting hover observations:");
        if (_conflictingMappings.IsEmpty)
            sb.AppendLine("  (none)");
        else
        {
            foreach (var pair in _conflictingMappings.OrderBy(pair => pair.Key))
                sb.AppendLine($"  {pair.Key}: {pair.Value}");
        }

        return sb.ToString();
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFilePath))
                return;

            var json = File.ReadAllText(CacheFilePath);
            var entries = JsonSerializer.Deserialize<Dictionary<uint, string>>(json);
            if (entries == null)
                return;

            foreach (var pair in entries)
            {
                // Never let cached values replace locked built-in icon IDs.
                if (LockedBuiltInIconIds.Contains(pair.Key))
                    continue;

                _iconIdToTileCode[pair.Key] = pair.Value;
            }
        }
        catch
        {
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllText(CacheFilePath, JsonSerializer.Serialize(new Dictionary<uint, string>(_iconIdToTileCode)));
        }
        catch
        {
        }
    }

    private void SeedBuiltInMappings()
    {
        foreach (var pair in BuiltInMappings)
            _iconIdToTileCode[pair.Key] = pair.Value;
    }

    private static bool IsLikelyMahjongIconId(uint iconId)
        => iconId >= 76041 && iconId <= 76150;

    private static bool IsSuitTileCode(string tileCode)
        => tileCode.Length == 2 && (tileCode[0] == 'M' || tileCode[0] == 'P' || tileCode[0] == 'S') && tileCode[1] >= '1' && tileCode[1] <= '9';

    private static string? TryParseTileCode(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return null;

        var name = rawName.Trim();
        var lower = name.ToLowerInvariant();

        if (lower.StartsWith("east")) return "EAST";
        if (lower.StartsWith("south")) return "SOUTH";
        if (lower.StartsWith("west")) return "WEST";
        if (lower.StartsWith("north")) return "NORTH";
        if (lower.StartsWith("white")) return "WHITE";
        if (lower.StartsWith("green")) return "GREEN";
        if (lower.StartsWith("red")) return "RED";

        int openParen = name.IndexOf('(');
        int closeParen = name.IndexOf(')');
        if (openParen < 0 || closeParen <= openParen + 1)
            return null;

        if (!int.TryParse(name.Substring(openParen + 1, closeParen - openParen - 1), out int rank))
            return null;

        if (rank < 1 || rank > 9)
            return null;

        if (lower.Contains("bamboo")) return $"S{rank}";
        if (lower.Contains("pin") || lower.Contains("circle") || lower.Contains("dot")) return $"P{rank}";
        if (lower.Contains("character") || lower.Contains("man") || lower.Contains("wan")) return $"M{rank}";

        return null;
    }
}
