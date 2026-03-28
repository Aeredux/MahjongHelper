using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SamplePlugin.Mahjong;

public static unsafe class MahjongHandReader
{
    private static readonly string CacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MahjongHelper");
    private static readonly string SnapshotCachePath = Path.Combine(CacheDirectory, "last_hand_snapshot.json");

    private sealed class PersistedTile
    {
        public int NodeIndex { get; set; }
        public uint NodeId { get; set; }
        public ushort NodeType { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public uint IconId { get; set; }
        public string? TileCode { get; set; }
    }

    private sealed class PersistedSnapshot
    {
        public List<PersistedTile> HandTiles { get; set; } = new();
        public PersistedTile? DrawnTile { get; set; }
    }

    public sealed record MahjongTileObservation(int NodeIndex, uint NodeId, ushort NodeType, float X, float Y, uint IconId, string? TileCode);

    public sealed record MahjongHandSnapshot(IReadOnlyList<MahjongTileObservation> HandTiles, MahjongTileObservation? DrawnTile, bool IsFromCache = false)
    {
        public string ToDisplayText()
        {
            var sb = new StringBuilder();

            sb.AppendLine("Mahjong Hand Snapshot");
            sb.AppendLine($"Hand tiles: {HandTiles.Count}");
            if (HandTiles.Count > 0)
            {
                sb.Append("Hand: ");
                for (int index = 0; index < HandTiles.Count; index++)
                {
                    if (index > 0) sb.Append(' ');
                    var tile = HandTiles[index];
                    sb.Append(tile.TileCode ?? $"ICON_{tile.IconId}");
                }
                sb.AppendLine();

                foreach (var tile in HandTiles)
                    sb.AppendLine($"  x={tile.X,4:F0} node={tile.NodeIndex,3} icon={tile.IconId} code={tile.TileCode ?? "(unknown)"}");
            }

            if (DrawnTile != null)
                sb.AppendLine($"Drawn: {DrawnTile.TileCode ?? $"ICON_{DrawnTile.IconId}"} (icon={DrawnTile.IconId}, node={DrawnTile.NodeIndex})");
            else
                sb.AppendLine("Drawn: (none)");

            if (IsFromCache)
                sb.AppendLine("Source: cached fallback (waiting for fresh icon captures)");

            return sb.ToString();
        }
    }

    public static MahjongHandSnapshot Read(AtkUnitBase* addon, IconIdCapture? capture, MahjongIconMap? iconMap)
    {
        var handTiles = new List<MahjongTileObservation>();
        MahjongTileObservation? drawnTile = null;

        if (addon == null || capture == null)
            return TryLoadSnapshot(out var cachedSnapshot)
                ? cachedSnapshot
                : new MahjongHandSnapshot(handTiles, drawnTile);

        var uld = addon->UldManager;
        for (int i = 0; i < uld.NodeListCount; i++)
        {
            try
            {
                var node = uld.NodeList[i];
                if (node == null) continue;

                bool visible = false;
                try { visible = node->IsVisible(); } catch { }
                if (!visible) continue;

                if ((int)node->Type == 1055 && node->Width == 42 && node->Height == 55)
                {
                    if (TryFindCapturedIcon(node, capture, out var iconId) && iconId > 0)
                    {
                        handTiles.Add(new MahjongTileObservation(
                            i,
                            node->NodeId,
                            (ushort)node->Type,
                            node->X,
                            node->Y,
                            iconId,
                            iconMap?.Resolve(iconId)));
                    }
                }

                if ((int)node->Type == 1022 && drawnTile == null)
                {
                    if (TryFindCapturedIcon(node, capture, out var iconId) && iconId > 0)
                    {
                        drawnTile = new MahjongTileObservation(
                            i,
                            node->NodeId,
                            (ushort)node->Type,
                            node->X,
                            node->Y,
                            iconId,
                            iconMap?.Resolve(iconId));
                    }
                }
            }
            catch
            {
            }
        }

        handTiles.Sort((left, right) => left.X.CompareTo(right.X));

        if (handTiles.Count > 0)
        {
            var liveSnapshot = new MahjongHandSnapshot(handTiles, drawnTile, IsFromCache: false);
            SaveSnapshot(liveSnapshot);
            return liveSnapshot;
        }

        return TryLoadSnapshot(out var fallbackSnapshot)
            ? fallbackSnapshot
            : new MahjongHandSnapshot(handTiles, drawnTile);
    }

    public static void ResetCachedSnapshot()
    {
        try
        {
            if (File.Exists(SnapshotCachePath))
                File.Delete(SnapshotCachePath);
        }
        catch
        {
        }
    }

    private static void SaveSnapshot(MahjongHandSnapshot snapshot)
    {
        try
        {
            var persisted = new PersistedSnapshot
            {
                HandTiles = snapshot.HandTiles
                    .Select(tile => new PersistedTile
                    {
                        NodeIndex = tile.NodeIndex,
                        NodeId = tile.NodeId,
                        NodeType = tile.NodeType,
                        X = tile.X,
                        Y = tile.Y,
                        IconId = tile.IconId,
                        TileCode = tile.TileCode,
                    })
                    .ToList(),
                DrawnTile = snapshot.DrawnTile == null
                    ? null
                    : new PersistedTile
                    {
                        NodeIndex = snapshot.DrawnTile.NodeIndex,
                        NodeId = snapshot.DrawnTile.NodeId,
                        NodeType = snapshot.DrawnTile.NodeType,
                        X = snapshot.DrawnTile.X,
                        Y = snapshot.DrawnTile.Y,
                        IconId = snapshot.DrawnTile.IconId,
                        TileCode = snapshot.DrawnTile.TileCode,
                    }
            };

            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllText(SnapshotCachePath, JsonSerializer.Serialize(persisted));
        }
        catch
        {
        }
    }

    private static bool TryLoadSnapshot(out MahjongHandSnapshot snapshot)
    {
        snapshot = new MahjongHandSnapshot(Array.Empty<MahjongTileObservation>(), null);

        try
        {
            if (!File.Exists(SnapshotCachePath))
                return false;

            var json = File.ReadAllText(SnapshotCachePath);
            var persisted = JsonSerializer.Deserialize<PersistedSnapshot>(json);
            if (persisted == null || persisted.HandTiles.Count == 0)
                return false;

            var handTiles = persisted.HandTiles
                .Select(tile => new MahjongTileObservation(tile.NodeIndex, tile.NodeId, tile.NodeType, tile.X, tile.Y, tile.IconId, tile.TileCode))
                .OrderBy(tile => tile.X)
                .ToList();

            MahjongTileObservation? drawnTile = null;
            if (persisted.DrawnTile != null)
            {
                var tile = persisted.DrawnTile;
                drawnTile = new MahjongTileObservation(tile.NodeIndex, tile.NodeId, tile.NodeType, tile.X, tile.Y, tile.IconId, tile.TileCode);
            }

            snapshot = new MahjongHandSnapshot(handTiles, drawnTile, IsFromCache: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFindCapturedIcon(AtkResNode* root, IconIdCapture capture, out uint iconId)
    {
        iconId = 0;
        var visited = new HashSet<nint>();
        return TryFindCapturedIconRecursive(root, capture, visited, 0, out iconId);
    }

    private static bool TryFindCapturedIconRecursive(AtkResNode* root, IconIdCapture capture, HashSet<nint> visited, int depth, out uint iconId)
    {
        iconId = 0;
        if (root == null || depth > 32)
            return false;

        nint addr = (nint)root;
        if (!visited.Add(addr))
            return false;

        try
        {
            if (root->Type == NodeType.Image)
            {
                var image = (AtkImageNode*)root;

                // Primary: read ground-truth icon ID from the texture resource chain.
                iconId = EmjUiReader.TryReadIconIdFromStructPublic(image);
                if (iconId > 0)
                    return true;

                // Fallback: hook-based capture (useful after mid-game plugin reload).
                iconId = capture.GetIconId((nint)image);
                if (iconId > 0)
                    return true;
            }

            for (var child = root->ChildNode; child != null; child = child->NextSiblingNode)
            {
                if (TryFindCapturedIconRecursive(child, capture, visited, depth + 1, out iconId))
                    return true;
            }

            if ((int)root->Type >= 1000)
            {
                var componentNode = (AtkComponentNode*)root;
                if (componentNode->Component != null)
                {
                    var childUld = componentNode->Component->UldManager;
                    for (int i = 0; i < childUld.NodeListCount && i < 64; i++)
                    {
                        var child = childUld.NodeList[i];
                        if (child == null) continue;
                        if (TryFindCapturedIconRecursive(child, capture, visited, depth + 1, out iconId))
                            return true;
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }
}
