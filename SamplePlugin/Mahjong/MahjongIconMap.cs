using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SamplePlugin.Mahjong;

public sealed unsafe class MahjongIconMap
{
    private static readonly string CacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MahjongHelper");
    private static readonly string CacheFilePath = Path.Combine(CacheDirectory, "icon_name_cache.json");
    private static readonly IReadOnlyDictionary<uint, string> BuiltInMappings = new Dictionary<uint, string>
    {
        // Conservative baseline from confirmed captures.
        [76069] = "P4",
        [76070] = "S9",
        [76071] = "S9",
    };

    private readonly ConcurrentDictionary<uint, string> _iconIdToTileCode = new();

    public MahjongIconMap()
    {
        SeedBuiltInMappings();
        LoadCache();
    }

    public void ObserveHover(AtkUnitBase* addon)
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
                _iconIdToTileCode[pair.Key] = pair.Value;
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