using System;
using System.Collections.Generic;
using System.Linq;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Offline classify for <see cref="UiDump"/> fixtures. Mirrors leftover +
/// pond peel + hand-strip paths that feed <c>/mj snap</c> meld lists.
/// </summary>
public static class UiDumpReplay
{
    public sealed record Result(
        SuggestMoveRequest Request,
        IReadOnlyList<string> SnapLines);

    public static Result Classify(UiDump dump)
    {
        ArgumentNullException.ThrowIfNull(dump);

        var nodes = dump.AllIconNodes.Count > 0
            ? dump.AllIconNodes
            : dump.TileSizedNodes;
        var onScreen = nodes
            .Where(n => n.Visible && n.AncestorVisible)
            .ToList();

        var trays = onScreen
            .Where(n => IconNodeScan.IsPopulatedFuuroSlot(
                n.NodeType, n.Width, n.Height, n.IconId, n.TileCode))
            .Select(ToTray)
            .ToList();
        foreach (var trayNode in dump.Trays)
        {
            if (IconNodeScan.IsPopulatedFuuroSlot(
                    trayNode.NodeType, trayNode.Width, trayNode.Height,
                    trayNode.IconId, trayNode.TileCode))
                trays.Add(ToTray(trayNode));
        }

        var pondHints = PondHints(onScreen);
        var stripY = StripAbsY(onScreen);
        var packMaxAbsX = PackMaxAbsX(onScreen);

        var leftover = OpponentAreaClassifier.Classify(
            LeftoverTiles(onScreen),
            pondHints,
            stripY,
            packMaxAbsX,
            trays);

        // Pond + mid-table leftover only. Own-strip type-2 (AbsY≥900) is
        // PlayerMeld via OpponentArea/HandStrip; feeding it here seats those
        // PONs as Right when a tiled 1062 is live.
        var small = SmallTileClassifier.Classify(
            onScreen
                .Where(n => n.AbsY < 900 || n.NodeType is 1021 or 1022 or 1023 or 1024)
                .Select((n, i) => new SmallTileClassifier.Tile(
                    i, n.NodeType, n.AbsX, n.AbsY, n.AbsX, n.AbsY,
                    n.Width, n.Height, n.Rotation, n.ParentNodeId, n.TileCode)).ToList(),
            trays);

        var stripTiles = StripTiles(onScreen);
        var strip = HandStripClassifier.Split(stripTiles, trays);

        var own = new List<MeldInfo>();
        AddInferred(own, leftover, SmallTileClassifier.Kind.PlayerMeld, onScreen, LeftoverTiles(onScreen));
        AddSmallMelds(own, small, SmallTileClassifier.Kind.PlayerMeld);
        AddStripMelds(own, strip, stripTiles);
        own = DedupeMelds(own);

        var byKind = new Dictionary<SmallTileClassifier.Kind, List<MeldInfo>>
        {
            [SmallTileClassifier.Kind.RightMeld] = [],
            [SmallTileClassifier.Kind.OppositeMeld] = [],
            [SmallTileClassifier.Kind.LeftMeld] = [],
        };
        foreach (var kind in byKind.Keys.ToList())
        {
            AddInferred(byKind[kind], leftover, kind, onScreen, LeftoverTiles(onScreen));
            AddSmallMelds(byKind[kind], small, kind);
            byKind[kind] = DedupeMelds(byKind[kind]);
        }

        if (dump.Expected?.Own is { Count: > 0 } && own.Count == 0)
            own.AddRange(dump.Expected.Own);

        var opponents = new List<OpponentInfo>();
        foreach (var (kind, offset) in new[]
        {
            (SmallTileClassifier.Kind.RightMeld, 1),
            (SmallTileClassifier.Kind.OppositeMeld, 2),
            (SmallTileClassifier.Kind.LeftMeld, 3),
        })
        {
            var wind = SolverJson.RelativeWind(dump.SeatWind, offset);
            if (wind == null)
                continue;
            opponents.Add(new OpponentInfo
            {
                Wind = wind,
                Melds = byKind[kind],
            });
        }

        var hand = dump.Expected?.Own != null && own.Count == 0
            ? new List<string>()
            : ClosedHand(strip, stripTiles);
        if (hand.Count == 0)
            hand = ["M1", "M2", "M3", "M4", "M5", "M6", "M7", "M8", "M9", "P1", "P2", "P3", "P4"];

        var request = new SuggestMoveRequest
        {
            Hand = hand,
            DrawnTile = strip.DrawId is int drawId
                ? stripTiles.FirstOrDefault(t => t.Id == drawId).TileCode
                : "SOUTH",
            SeatWind = dump.SeatWind,
            RoundWind = dump.RoundWind,
            Melds = own,
            Opponents = opponents,
        };

        return new Result(request, SolverJson.BuildSnapSummaryLines(request));
    }

    private static IconNodeScan.Tray ToTray(UiDumpNode n)
        => new(n.AbsX, n.AbsY, n.Width, n.Height, n.NodeType, n.IconId, n.TileCode);

    private static List<OpponentAreaClassifier.PondHint> PondHints(IReadOnlyList<UiDumpNode> nodes)
    {
        var hints = new List<OpponentAreaClassifier.PondHint>();
        void Add(ushort type, SmallTileClassifier.Kind kind)
        {
            var pond = nodes.Where(n => n.NodeType == type && MeldClassifier.IsUsableTile(n.TileCode)).ToList();
            if (pond.Count == 0)
                return;
            hints.Add(new OpponentAreaClassifier.PondHint(
                kind, pond.Average(n => n.AbsX), pond.Average(n => n.AbsY)));
        }

        Add(1021, SmallTileClassifier.Kind.PlayerDiscard);
        Add(1022, SmallTileClassifier.Kind.LeftDiscard);
        Add(1023, SmallTileClassifier.Kind.RightDiscard);
        Add(1024, SmallTileClassifier.Kind.OppositeDiscard);
        return hints;
    }

    private static List<OpponentAreaClassifier.Tile> LeftoverTiles(IReadOnlyList<UiDumpNode> nodes)
    {
        var tiles = new List<OpponentAreaClassifier.Tile>();
        var i = 0;
        foreach (var n in nodes)
        {
            if (!IconNodeScan.IsMahjongTileIcon(n.IconId) && !MeldClassifier.IsUsableTile(n.TileCode))
                continue;
            if (n.NodeType is 1009 or 1006 or 1021 or 1022 or 1023 or 1024 or 1038)
                continue;
            if (!IconNodeScan.IsTileSized(n.Width, n.Height)
                && !IconNodeScan.IsFaceLeaf(n.NodeType, n.Width, n.Height)
                && !IconNodeScan.IsCallCueNode(n.NodeType, n.Width, n.Height, n.Rotation))
                continue;
            if (IconNodeScan.IsType1045PondEcho(n.NodeType, n.Width, n.Height))
                continue;
            tiles.Add(new OpponentAreaClassifier.Tile(
                i++, n.AbsX, n.AbsY, n.AbsX, n.AbsY, n.Width, n.Height,
                n.Rotation, n.ParentNodeId, n.TileCode, n.NodeType, n.NodeIndex));
        }

        return tiles;
    }

    private static List<HandStripClassifier.Tile> StripTiles(IReadOnlyList<UiDumpNode> nodes)
    {
        var tiles = new List<HandStripClassifier.Tile>();
        var i = 0;
        foreach (var n in nodes)
        {
            if (!MeldClassifier.IsUsableTile(n.TileCode))
                continue;
            if (n.AbsY < 900)
                continue;
            tiles.Add(new HandStripClassifier.Tile(
                i++, n.AbsX, n.AbsY, n.Width, n.Height, n.Rotation,
                n.ParentNodeId, n.TileCode, n.NodeIndex, n.AbsX, n.AbsY, n.NodeType));
        }

        return tiles;
    }

    private static float? StripAbsY(IReadOnlyList<UiDumpNode> nodes)
    {
        var strip = nodes.Where(n => n.AbsY >= 900 && MeldClassifier.IsUsableTile(n.TileCode)).ToList();
        return strip.Count > 0 ? strip.Average(n => n.AbsY) : null;
    }

    private static float? PackMaxAbsX(IReadOnlyList<UiDumpNode> nodes)
    {
        var pack = nodes
            .Where(n => n.NodeType == 1055 && n.AbsY >= 900 && n.AbsX > 0 && n.AbsX < 1500)
            .ToList();
        return pack.Count >= 7 ? pack.Max(n => n.AbsX) : null;
    }

    private static void AddInferred(
        List<MeldInfo> dest,
        IReadOnlyList<OpponentAreaClassifier.Assignment> leftover,
        SmallTileClassifier.Kind kind,
        IReadOnlyList<UiDumpNode> _,
        IReadOnlyList<OpponentAreaClassifier.Tile> leftoverTiles)
    {
        foreach (var assignment in leftover.Where(a => a.Kind == kind))
        {
            var codes = assignment.TileIds
                .Where(id => id >= 0 && id < leftoverTiles.Count)
                .Select(id => leftoverTiles[id].TileCode)
                .Where(MeldClassifier.IsUsableTile)
                .Select(c => c!)
                .ToList();
            var meld = MeldClassifier.InferMeld(codes);
            if (meld == null)
                continue;
            dest.Add(new MeldInfo { Type = meld.Type, Tiles = SnapTiles(meld) });
        }
    }

    private static void AddSmallMelds(
        List<MeldInfo> dest,
        IReadOnlyList<SmallTileClassifier.ClassifiedTile> classified,
        SmallTileClassifier.Kind kind)
    {
        var ofKind = classified.Where(c => c.Kind == kind).ToList();
        if (ofKind.Count < 3)
            return;
        var codes = ofKind.Select(c => c.Tile.TileCode).Where(MeldClassifier.IsUsableTile).Select(c => c!).ToList();
        foreach (var window in PeelCodes(codes))
        {
            var meld = MeldClassifier.InferMeld(window);
            if (meld != null)
                dest.Add(new MeldInfo { Type = meld.Type, Tiles = SnapTiles(meld) });
        }
    }

    private static void AddStripMelds(
        List<MeldInfo> dest,
        HandStripClassifier.Result strip,
        IReadOnlyList<HandStripClassifier.Tile> tiles)
    {
        foreach (var group in strip.MeldGroups)
        {
            var codes = group
                .Select(id => tiles.FirstOrDefault(t => t.Id == id).TileCode)
                .Where(MeldClassifier.IsUsableTile)
                .Select(c => c!)
                .ToList();
            var meld = MeldClassifier.InferMeld(codes);
            if (meld != null)
                dest.Add(new MeldInfo { Type = meld.Type, Tiles = SnapTiles(meld) });
        }
    }

    /// <summary>
    /// Live <c>/mj snap</c> prints CHI in rank order (S7-S8-S9). Leftover
    /// clusters are AbsX-sorted (S9-S7-S8). Replay only.
    /// </summary>
    private static List<string> SnapTiles(ObservedMeld meld)
    {
        var tiles = meld.Tiles.ToList();
        if (!meld.Type.Equals("CHI", StringComparison.Ordinal) || tiles.Count != 3)
            return tiles;
        return tiles
            .OrderBy(MeldClassifier.CanonicalKey, StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> ClosedHand(
        HandStripClassifier.Result strip, IReadOnlyList<HandStripClassifier.Tile> tiles)
        => strip.ClosedIds
            .Select(id => tiles.FirstOrDefault(t => t.Id == id).TileCode)
            .Where(MeldClassifier.IsUsableTile)
            .Select(c => c!)
            .ToList();

    /// <summary>
    /// Same window order as leftover <c>PeelOrdered</c>: open KAN before PON
    /// so four P8 faces do not become a ghost <c>PON P8×3</c>.
    /// </summary>
    private static List<List<string>> PeelCodes(IReadOnlyList<string> codes)
    {
        var groups = new List<List<string>>();
        var i = 0;
        while (i < codes.Count)
        {
            if (codes.Count - i >= 4)
            {
                var four = codes.Skip(i).Take(4).ToList();
                var meld4 = MeldClassifier.InferMeld(four);
                if (meld4 != null && meld4.Type.StartsWith("KAN", StringComparison.Ordinal))
                {
                    groups.Add(four);
                    i += 4;
                    continue;
                }
            }

            if (codes.Count - i >= 3 && MeldClassifier.InferMeld(codes.Skip(i).Take(3).ToList()) != null)
            {
                groups.Add(codes.Skip(i).Take(3).ToList());
                i += 3;
                continue;
            }

            i++;
        }

        return groups;
    }

    private static List<MeldInfo> DedupeMelds(List<MeldInfo> melds)
    {
        var kept = new List<MeldInfo>();
        foreach (var meld in melds)
        {
            var key = $"{meld.Type}:{string.Join(",", (meld.Tiles ?? []).Select(MeldClassifier.CanonicalKey).OrderBy(t => t))}";
            if (kept.Any(k =>
                    $"{k.Type}:{string.Join(",", (k.Tiles ?? []).Select(MeldClassifier.CanonicalKey).OrderBy(t => t))}"
                    == key))
                continue;
            kept.Add(meld);
        }

        return kept;
    }
}
