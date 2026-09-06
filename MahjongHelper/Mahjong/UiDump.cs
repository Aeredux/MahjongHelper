using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Offline UI capture for <c>/mj dump-ui</c>. Written under
/// %APPDATA%/MahjongHelper/ui-dumps/ and copyable into
/// MahjongHelper.Tests/Fixtures/ui/. Trim a full snap to the nodes
/// needed for classify + expected sidecar.
/// </summary>
public sealed class UiDump
{
    public string? Name { get; set; }
    public string? Source { get; set; }
    public string Command { get; set; } = "/mj dump-ui";
    public string? CapturedAtUtc { get; set; }
    public string? SeatWind { get; set; }
    public string? RoundWind { get; set; }
    public UiDumpExpected? Expected { get; set; }
    public List<UiDumpNode> AllIconNodes { get; set; } = [];
    public List<UiDumpNode> TileSizedNodes { get; set; } = [];
    public List<UiDumpNode> Trays { get; set; } = [];
}

public sealed class UiDumpExpected
{
    public string? SnapLine { get; set; }
    public List<MeldInfo>? Own { get; set; }
    public List<OpponentInfo>? Opponents { get; set; }
}

public sealed class UiDumpNode
{
    public ushort NodeType { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public float AbsX { get; set; }
    public float AbsY { get; set; }
    public uint IconId { get; set; }
    public string? TileCode { get; set; }
    public bool Visible { get; set; } = true;
    /// <summary>
    /// Live dumps store ancestor-AND in <see cref="Visible"/>. This flag
    /// is the same value when written by <c>/mj dump-ui</c>.
    /// </summary>
    public bool AncestorVisible { get; set; } = true;
    public uint ParentNodeId { get; set; }
    public float Rotation { get; set; }
    public int NodeIndex { get; set; }
    public uint NodeId { get; set; }
}

public static class UiDumpIO
{
    public static readonly string DumpsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MahjongHelper",
        "ui-dumps");

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return $"dump-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}";

        var chars = name.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(chars[i]) && chars[i] is not ('-' or '_'))
                chars[i] = '-';
        }

        var slug = new string(chars).Trim('-');
        return string.IsNullOrEmpty(slug)
            ? $"dump-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}"
            : slug;
    }

    public static string Write(UiDump dump, string? name = null)
    {
        Directory.CreateDirectory(DumpsDirectory);
        dump.Name ??= SanitizeName(name);
        dump.CapturedAtUtc ??= DateTime.UtcNow.ToString("O");
        var path = Path.Combine(DumpsDirectory, $"{SanitizeName(name ?? dump.Name)}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(dump, JsonOptions));
        return path;
    }

    public static UiDump Load(string path)
        => JsonSerializer.Deserialize<UiDump>(File.ReadAllText(path), JsonOptions)
           ?? throw new InvalidOperationException($"Empty UI dump: {path}");
}
