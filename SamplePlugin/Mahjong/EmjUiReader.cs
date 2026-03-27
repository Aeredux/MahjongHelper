using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SamplePlugin.Mahjong;

public static unsafe class EmjUiReader
{
    // Player hand area uses nodes 54–71 (18 raw slots; not all are active at once).
    // The drawn tile is identified by position gap, not a fixed node index.
    private static readonly int[] PlayerHandNodeIndices = Enumerable.Range(54, 18).ToArray();

    public enum SlotKind
    {
        CanonicalPlayerHand,
        CanonicalPlayerDraw,
        PlayerHand,
        PlayerDraw,
        VisibleTileCandidate,
        PlayerDiscard,
        RightDiscard,
        OppositeDiscard,
        LeftDiscard,
        DoraIndicator,
    }

    public enum GamePhase
    {
        Unknown,
        WaitingForDiscard,
        WaitingForDraw,
        CallDecisionPrompt,
        RiichiDecisionPrompt,
        TsumoDecisionPrompt,
        RonDecisionPrompt,
        BetweenRounds,
        GameOver,
    }

    [Flags]
    public enum CallOptions
    {
        None = 0,
        Chi = 1 << 0,
        Pon = 1 << 1,
        Kan = 1 << 2,
        Ron = 1 << 3,
        Tsumo = 1 << 4,
        Riichi = 1 << 5,
        Skip = 1 << 6,
    }

    public sealed record UiGameInfo(
        int? SeatWind,
        int? RoundWind,
        int? RoundNumber,
        int? Honba,
        int? RiichiSticks,
        int? PlayerScore,
        int? RightScore,
        int? OppositeScore,
        int? LeftScore,
        bool[] RiichiStatus,
        CallOptions AvailableCalls,
        GamePhase Phase,
        IReadOnlyList<int> RawAtkInts)
    {
        public string ToDisplayText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"SeatWind: {FormatWind(SeatWind)}");
            sb.AppendLine($"RoundWind: {FormatWind(RoundWind)}");
            sb.AppendLine($"Round: {RoundNumber?.ToString() ?? "?"} Honba: {Honba?.ToString() ?? "?"} RiichiSticks: {RiichiSticks?.ToString() ?? "?"}");
            sb.AppendLine($"Scores: Player={PlayerScore?.ToString() ?? "?"} Right={RightScore?.ToString() ?? "?"} Opposite={OppositeScore?.ToString() ?? "?"} Left={LeftScore?.ToString() ?? "?"}");
            sb.AppendLine($"Riichi: Player={RiichiStatus[0]} Right={RiichiStatus[1]} Opposite={RiichiStatus[2]} Left={RiichiStatus[3]}");
            sb.AppendLine($"AvailableCalls: {AvailableCalls}");
            sb.AppendLine($"Phase: {Phase}");
            return sb.ToString();
        }

        private static string FormatWind(int? wind) => wind switch
        {
            0 => "East",
            1 => "South",
            2 => "West",
            3 => "North",
            _ => wind?.ToString() ?? "?",
        };
    }

    public sealed record UiSlot(
        SlotKind Kind,
        int SlotIndex,
        int NodeIndex,
        uint NodeId,
        ushort NodeType,
        bool Visible,
        float X,
        float Y,
        int Width,
        int Height,
        uint IconId,
        string? TileCode);

    public sealed record UiState(IReadOnlyList<UiSlot> Slots, UiGameInfo GameInfo, DateTime UtcCapturedAt)
    {
        public string ToDisplayText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Captured: {UtcCapturedAt:O}");

            // Game info
            sb.AppendLine("--- Game Info ---");
            sb.Append(GameInfo.ToDisplayText());

            var canonicalHand = Slots.Where(s => s.Kind == SlotKind.CanonicalPlayerHand).OrderBy(s => s.SlotIndex).ToList();
            var canonicalDraw = Slots.FirstOrDefault(s => s.Kind == SlotKind.CanonicalPlayerDraw);
            var hand = Slots.Where(s => s.Kind == SlotKind.PlayerHand).OrderBy(s => s.SlotIndex).ToList();
            var draw = Slots.FirstOrDefault(s => s.Kind == SlotKind.PlayerDraw);
            var visible = Slots.Where(s => s.Kind == SlotKind.VisibleTileCandidate).OrderBy(s => s.X).ToList();

            sb.AppendLine($"Canonical player hand slots: {canonicalHand.Count}");
            foreach (var slot in canonicalHand)
                sb.AppendLine(DescribeSlot(slot));

            sb.AppendLine("Canonical player draw slot:");
            sb.AppendLine(canonicalDraw == null ? "  (missing)" : DescribeSlot(canonicalDraw));

            // Discard pools
            foreach (var kind in new[] { SlotKind.PlayerDiscard, SlotKind.RightDiscard, SlotKind.OppositeDiscard, SlotKind.LeftDiscard })
            {
                var discards = Slots.Where(s => s.Kind == kind).OrderBy(s => s.SlotIndex).ToList();
                sb.AppendLine($"{kind}: {discards.Count} tiles");
                if (discards.Count > 0)
                {
                    sb.Append("  ");
                    sb.AppendLine(string.Join(" ", discards.Select(s => s.TileCode ?? (s.IconId > 0 ? $"ICON_{s.IconId}" : "(no-icon)"))));
                    foreach (var slot in discards)
                        sb.AppendLine(DescribeSlot(slot));
                }
            }

            // Dora indicators
            var dora = Slots.Where(s => s.Kind == SlotKind.DoraIndicator).OrderBy(s => s.SlotIndex).ToList();
            sb.AppendLine($"DoraIndicator: {dora.Count} tiles");
            if (dora.Count > 0)
            {
                sb.Append("  ");
                sb.AppendLine(string.Join(" ", dora.Select(s => s.TileCode ?? (s.IconId > 0 ? $"ICON_{s.IconId}" : "(no-icon)"))));
                foreach (var slot in dora)
                    sb.AppendLine(DescribeSlot(slot));
            }

            sb.AppendLine($"Player hand slots: {hand.Count}");
            foreach (var slot in hand)
                sb.AppendLine(DescribeSlot(slot));

            sb.AppendLine("Player draw slot:");
            sb.AppendLine(draw == null ? "  (missing)" : DescribeSlot(draw));

            sb.AppendLine($"Visible tile candidates: {visible.Count}");
            foreach (var slot in visible)
                sb.AppendLine(DescribeSlot(slot));

            return sb.ToString();
        }

        private static string DescribeSlot(UiSlot slot)
        {
            var tile = slot.TileCode ?? (slot.IconId > 0 ? $"ICON_{slot.IconId}" : "(no-icon)");
            return $"  [{slot.Kind}:{slot.SlotIndex}] node={slot.NodeIndex} id={slot.NodeId} type={slot.NodeType} vis={slot.Visible} pos=({slot.X:F0},{slot.Y:F0}) size=({slot.Width},{slot.Height}) {tile}";
        }
    }

    public static UiState Read(AtkUnitBase* addon, IconIdCapture? iconCapture, MahjongIconMap? iconMap)
    {
        var slots = new List<UiSlot>();

        if (addon == null)
        {
            var emptyInfo = new UiGameInfo(null, null, null, null, null, null, null, null, null,
                new bool[4], CallOptions.None, GamePhase.Unknown, Array.Empty<int>());
            return new UiState(slots, emptyInfo, DateTime.UtcNow);
        }

        var uld = addon->UldManager;

        var rawHand = new List<UiSlot>();
        for (var i = 0; i < PlayerHandNodeIndices.Length; i++)
        {
            var nodeIndex = PlayerHandNodeIndices[i];
            if (!TryReadNodeSlot(uld, nodeIndex, iconCapture, iconMap, SlotKind.PlayerHand, i, out var slot))
                continue;
            rawHand.Add(slot);
            slots.Add(slot);
        }

        var visibleCandidates = new List<UiSlot>();

        // Scan all nodes for visible tile components.
        // 34x45 nodes are discard pool / dora tiles; 42x55 nodes are player hand tiles.
        // We also track 34x45 tiles separately for discard/dora classification.
        var smallTiles = new List<UiSlot>();

        for (int i = 0; i < uld.NodeListCount; i++)
        {
            var node = uld.NodeList[i];
            if (node == null)
                continue;

            var type = (int)node->Type;
            if (type < 1000)
                continue;

            bool visible;
            try { visible = node->IsVisible(); }
            catch { visible = false; }
            if (!visible)
                continue;

            // 42x55 = player hand tile candidates (existing logic)
            if (type == 1055 && node->Width == 42 && node->Height == 55)
            {
                uint iconId = 0;
                TryFindIcon(node, iconCapture, out iconId);

                var candidate = new UiSlot(
                    SlotKind.VisibleTileCandidate,
                    visibleCandidates.Count,
                    i,
                    node->NodeId,
                    (ushort)node->Type,
                    visible,
                    node->X,
                    node->Y,
                    node->Width,
                    node->Height,
                    iconId,
                    iconId > 0 ? iconMap?.Resolve(iconId) : null);

                visibleCandidates.Add(candidate);
                slots.Add(candidate);
            }

            // 34x45 = discard pool / dora indicator tile candidates
            if (node->Width == 34 && node->Height == 45)
            {
                uint iconId = 0;
                TryFindIcon(node, iconCapture, out iconId);

                smallTiles.Add(new UiSlot(
                    SlotKind.PlayerDiscard, // placeholder kind, will be reclassified
                    smallTiles.Count,
                    i,
                    node->NodeId,
                    (ushort)node->Type,
                    visible,
                    node->X,
                    node->Y,
                    node->Width,
                    node->Height,
                    iconId,
                    iconId > 0 ? iconMap?.Resolve(iconId) : null));
            }
        }

        // Classify 34x45 tiles into discard pools and dora indicators by spatial position.
        // In the Mahjong UI, tiles are arranged with the local player at the bottom.
        // The classification uses parent node grouping and Y-position heuristics.
        ClassifySmallTiles(uld, smallTiles, iconCapture, iconMap, slots);

        var canonicalHand = BuildCanonicalHand(rawHand);
        for (var i = 0; i < canonicalHand.Count; i++)
        {
            var source = canonicalHand[i];
            slots.Add(source with { Kind = SlotKind.CanonicalPlayerHand, SlotIndex = i });
        }

        var canonicalDraw = BuildCanonicalDraw(canonicalHand);
        if (canonicalDraw != null)
        {
            // Remove the draw tile from the canonical hand list so it's not double-counted.
            // Rebuild canonical hand slots without the draw tile and re-add them.
            slots.RemoveAll(s => s.Kind == SlotKind.CanonicalPlayerHand);
            var handWithoutDraw = canonicalHand.Where(s => s.NodeIndex != canonicalDraw.NodeIndex).ToList();
            for (var i = 0; i < handWithoutDraw.Count; i++)
                slots.Add(handWithoutDraw[i] with { Kind = SlotKind.CanonicalPlayerHand, SlotIndex = i });

            slots.Add(canonicalDraw with { Kind = SlotKind.CanonicalPlayerDraw, SlotIndex = 0 });
        }

        var gameInfo = ReadGameInfo(addon);

        return new UiState(slots, gameInfo, DateTime.UtcNow);
    }

    /// <summary>
    /// Classifies 34x45 tile nodes into discard pools (4 players) and dora indicators
    /// based on their parent node grouping and spatial position.
    ///
    /// The EmjL addon groups discard tiles under parent container nodes. By examining
    /// which parent a tile belongs to and its Y-position, we can determine which
    /// player's discard pool it represents and whether it's a dora indicator.
    ///
    /// Classification strategy (initial heuristic, will be refined with live data):
    /// - Tiles are grouped by their parent node ID.
    /// - Groups with very few tiles (1-5) in a central area are likely dora indicators.
    /// - Larger groups are discard pools, classified by spatial position:
    ///   - Bottom area (highest Y values) = local player discards
    ///   - Right area = right player (shimocha)
    ///   - Top area (lowest Y values) = opposite player (toimen)
    ///   - Left area = left player (kamicha)
    /// </summary>
    private static void ClassifySmallTiles(AtkUldManager uld, List<UiSlot> smallTiles, IconIdCapture? iconCapture, MahjongIconMap? iconMap, List<UiSlot> outputSlots)
    {
        if (smallTiles.Count == 0)
            return;

        // Only consider tiles that have an icon (face-up tiles)
        var tilesWithIcons = smallTiles.Where(t => t.IconId > 0).ToList();
        if (tilesWithIcons.Count == 0)
            return;

        // Group tiles by parent node to find structural groups.
        // Each discard pool and the dora indicator area should be under different parent nodes.
        var parentGroups = new Dictionary<uint, List<UiSlot>>();
        foreach (var tile in tilesWithIcons)
        {
            try
            {
                if (tile.NodeIndex < 0 || tile.NodeIndex >= uld.NodeListCount)
                    continue;

                var node = uld.NodeList[tile.NodeIndex];
                if (node == null || node->ParentNode == null)
                    continue;

                var parentId = node->ParentNode->NodeId;
                if (!parentGroups.ContainsKey(parentId))
                    parentGroups[parentId] = new List<UiSlot>();
                parentGroups[parentId].Add(tile);
            }
            catch
            {
            }
        }

        // Compute Y-band center for each parent group
        var groupStats = parentGroups.Select(g =>
        {
            var tiles = g.Value;
            var avgY = tiles.Average(t => t.Y);
            var avgX = tiles.Average(t => t.X);
            var minY = tiles.Min(t => t.Y);
            var maxY = tiles.Max(t => t.Y);
            var minX = tiles.Min(t => t.X);
            var maxX = tiles.Max(t => t.X);
            return new
            {
                ParentId = g.Key,
                Tiles = tiles,
                AvgY = avgY,
                AvgX = avgX,
                MinY = minY,
                MaxY = maxY,
                MinX = minX,
                MaxX = maxX,
                YSpread = maxY - minY,
                XSpread = maxX - minX,
                Count = tiles.Count,
            };
        }).ToList();

        if (groupStats.Count == 0)
            return;

        // Heuristic: dora indicators are typically a small group (<=5 tiles) positioned
        // near the center of the board. Discard pools have more tiles and spread across
        // a wider area. If we can't clearly identify dora, we skip it and log everything
        // as discard tiles for now.

        // Sort groups by Y to assign player positions.
        // In the FFXIV Mahjong UI (as observed):
        // - Local player (bottom of screen): highest Y values
        // - Opposite player (top): lowest Y values
        // - Left player: left side
        // - Right player: right side
        // But discard pools for all 4 players are actually in the center area of the board,
        // distinguished primarily by Y-position bands.

        // Separate potential dora indicators: small group with narrow spatial spread
        var doraGroups = groupStats.Where(g => g.Count <= 5 && g.XSpread < 200 && g.YSpread < 100).ToList();
        var discardGroups = groupStats.Where(g => !doraGroups.Contains(g)).ToList();

        // If we have exactly 4 discard groups + potentially some dora, great.
        // Otherwise, fall back to Y-based sorting of all non-dora groups.

        // Assign dora indicators
        foreach (var doraGroup in doraGroups)
        {
            for (int i = 0; i < doraGroup.Tiles.Count; i++)
            {
                var tile = doraGroup.Tiles[i];
                outputSlots.Add(tile with { Kind = SlotKind.DoraIndicator, SlotIndex = i });
            }
        }

        // Sort discard groups by average Y position to assign player seats.
        // Highest Y = bottom of screen = local player.
        var sortedDiscardGroups = discardGroups.OrderByDescending(g => g.AvgY).ToList();

        // Map groups to player positions based on Y ordering.
        // With 4 groups, the order should be: player (bottom), right, opposite, left
        // But in practice, the spatial layout varies. We use the Y-sort as primary heuristic.
        var kindMapping = new[]
        {
            SlotKind.PlayerDiscard,    // highest Y = bottom = local player
            SlotKind.RightDiscard,     // next
            SlotKind.LeftDiscard,      // next
            SlotKind.OppositeDiscard,  // lowest Y = top = opposite player
        };

        for (int g = 0; g < sortedDiscardGroups.Count; g++)
        {
            var kind = g < kindMapping.Length ? kindMapping[g] : SlotKind.PlayerDiscard;
            var group = sortedDiscardGroups[g];
            var orderedTiles = group.Tiles.OrderBy(t => t.Y).ThenBy(t => t.X).ToList();

            for (int i = 0; i < orderedTiles.Count; i++)
            {
                outputSlots.Add(orderedTiles[i] with { Kind = kind, SlotIndex = i });
            }
        }
    }

    /// <summary>
    /// Reads game info from AtkValues and visible UI elements.
    /// AtkValues indices are discovered via the UI element discovery dump and may need
    /// adjustment after live validation. Initial mapping is best-effort based on
    /// common FFXIV addon patterns for Doman Mahjong.
    /// </summary>
    private static UiGameInfo ReadGameInfo(AtkUnitBase* addon)
    {
        int? seatWind = null;
        int? roundWind = null;
        int? roundNumber = null;
        int? honba = null;
        int? riichiSticks = null;
        int? playerScore = null;
        int? rightScore = null;
        int? oppositeScore = null;
        int? leftScore = null;
        var riichiStatus = new bool[4]; // player, right, opposite, left
        var rawAtkInts = new List<int>();

        try
        {
            var valCount = addon->AtkValuesCount;

            // Collect all int/uint AtkValues for discovery logging.
            // Also attempt to read known game state values.
            // NOTE: The exact AtkValue indices for wind/round/scores are not yet confirmed.
            // The discovery dump (DumpUiElementDiscovery) will help identify them.
            // For now, we capture all small ints for the raw log and attempt common patterns.
            for (int i = 0; i < valCount && i < 500; i++)
            {
                try
                {
                    var val = addon->AtkValues[i];
                    if (val.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int ||
                        val.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt)
                    {
                        rawAtkInts.Add(val.Int);
                    }
                    else
                    {
                        rawAtkInts.Add(0);
                    }
                }
                catch
                {
                    rawAtkInts.Add(0);
                }
            }

            // Read scores and wind from text nodes.
            // The EmjL addon displays wind/round info and scores as text nodes.
            ReadWindAndScoresFromTextNodes(addon, ref seatWind, ref roundWind, ref roundNumber,
                ref playerScore, ref rightScore, ref oppositeScore, ref leftScore);
        }
        catch
        {
            // Never crash from game info reading
        }

        // Detect call prompts from visible button-like components
        var availableCalls = ReadCallPrompts(addon);

        // Detect riichi status from visual indicators
        ReadRiichiStatus(addon, riichiStatus);

        // Infer game phase from available signals
        var phase = InferGamePhase(addon, availableCalls, rawAtkInts);

        return new UiGameInfo(
            seatWind, roundWind, roundNumber, honba, riichiSticks,
            playerScore, rightScore, oppositeScore, leftScore,
            riichiStatus, availableCalls, phase, rawAtkInts);
    }

    /// <summary>
    /// Scans visible text nodes in the addon for wind indicators, round info, and scores.
    /// Wind text typically appears as directional kanji/English near the center of the board.
    /// Scores appear as numeric text near each player's position.
    /// </summary>
    private static void ReadWindAndScoresFromTextNodes(AtkUnitBase* addon,
        ref int? seatWind, ref int? roundWind, ref int? roundNumber,
        ref int? playerScore, ref int? rightScore, ref int? oppositeScore, ref int? leftScore)
    {
        var uld = addon->UldManager;

        for (int i = 0; i < uld.NodeListCount; i++)
        {
            try
            {
                var n = uld.NodeList[i];
                if (n == null || n->Type != NodeType.Text) continue;

                bool vis = false;
                try { vis = n->IsVisible(); } catch { }
                if (!vis) continue;

                var txt = (AtkTextNode*)n;
                string text;
                try
                {
                    text = Marshal.PtrToStringUTF8((nint)txt->NodeText.StringPtr.Value) ?? "";
                }
                catch { continue; }

                if (string.IsNullOrWhiteSpace(text)) continue;

                var trimmed = text.Trim();

                // Wind detection: look for wind kanji or English wind names
                // Common patterns: "東" (East), "南" (South), "西" (West), "北" (North)
                // Also "East X局" style round indicators
                if (trimmed.Contains('東') || trimmed.Contains("East", StringComparison.OrdinalIgnoreCase))
                {
                    // If this is a small central text (round indicator), it's round wind
                    if (n->Width <= 100 && n->Height <= 50)
                        roundWind ??= 0; // East
                }
                else if (trimmed.Contains('南') || trimmed.Contains("South", StringComparison.OrdinalIgnoreCase))
                {
                    if (n->Width <= 100 && n->Height <= 50)
                        roundWind ??= 1; // South
                }
                else if (trimmed.Contains('西') || trimmed.Contains("West", StringComparison.OrdinalIgnoreCase))
                {
                    if (n->Width <= 100 && n->Height <= 50)
                        roundWind ??= 2; // West
                }
                else if (trimmed.Contains('北') || trimmed.Contains("North", StringComparison.OrdinalIgnoreCase))
                {
                    if (n->Width <= 100 && n->Height <= 50)
                        roundWind ??= 3; // North
                }

                // Score detection: purely numeric text that looks like a Mahjong score
                if (int.TryParse(trimmed.Replace(",", ""), out var score) && score >= 0 && score <= 100000)
                {
                    // Assign score by Y-position: bottom = player, top = opposite, left/right by X
                    if (n->Y > 350)
                        playerScore ??= score;
                    else if (n->Y < 100)
                        oppositeScore ??= score;
                    else if (n->X < 200)
                        leftScore ??= score;
                    else if (n->X > 600)
                        rightScore ??= score;
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Scans for visible call prompt buttons (chi, pon, kan, ron, tsumo, riichi, skip/pass).
    /// In FFXIV Mahjong, call prompts appear as clickable button components when the game
    /// offers a call decision. The presence/visibility of these components indicates
    /// which calls are available.
    /// </summary>
    private static CallOptions ReadCallPrompts(AtkUnitBase* addon)
    {
        var calls = CallOptions.None;

        try
        {
            var uld = addon->UldManager;

            for (int i = 0; i < uld.NodeListCount; i++)
            {
                try
                {
                    var n = uld.NodeList[i];
                    if (n == null) continue;
                    if ((int)n->Type < 1000) continue;

                    bool vis = false;
                    try { vis = n->IsVisible(); } catch { }
                    if (!vis) continue;

                    // Skip tile-sized components
                    if ((n->Width == 34 && n->Height == 45) || (n->Width == 42 && n->Height == 55))
                        continue;

                    // Look for button-like components with text children
                    var comp = (AtkComponentNode*)n;
                    if (comp->Component == null) continue;
                    var childUld = comp->Component->UldManager;

                    for (int j = 0; j < childUld.NodeListCount && j < 32; j++)
                    {
                        try
                        {
                            var cn = childUld.NodeList[j];
                            if (cn == null || cn->Type != NodeType.Text) continue;

                            bool cVis = false;
                            try { cVis = cn->IsVisible(); } catch { }
                            if (!cVis) continue;

                            var txt = (AtkTextNode*)cn;
                            string text;
                            try
                            {
                                text = Marshal.PtrToStringUTF8((nint)txt->NodeText.StringPtr.Value) ?? "";
                            }
                            catch { continue; }

                            if (string.IsNullOrWhiteSpace(text)) continue;
                            var lower = text.Trim().ToLowerInvariant();

                            // Match common call prompt text (English and Japanese)
                            if (lower.Contains("chi") || lower.Contains("チー"))
                                calls |= CallOptions.Chi;
                            else if (lower.Contains("pon") || lower.Contains("ポン"))
                                calls |= CallOptions.Pon;
                            else if (lower.Contains("kan") || lower.Contains("カン"))
                                calls |= CallOptions.Kan;
                            else if (lower.Contains("ron") || lower.Contains("ロン"))
                                calls |= CallOptions.Ron;
                            else if (lower.Contains("tsumo") || lower.Contains("ツモ"))
                                calls |= CallOptions.Tsumo;
                            else if (lower.Contains("riichi") || lower.Contains("リーチ") || lower.Contains("reach"))
                                calls |= CallOptions.Riichi;
                            else if (lower.Contains("skip") || lower.Contains("pass") || lower.Contains("cancel")
                                     || lower.Contains("スキップ") || lower.Contains("キャンセル"))
                                calls |= CallOptions.Skip;
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { }

        return calls;
    }

    /// <summary>
    /// Detects riichi status for each player by looking for riichi stick indicators
    /// in the discard pool areas. Riichi sticks are typically horizontal bar images
    /// placed across the discard pool when a player declares riichi.
    /// This is a heuristic that will be refined with live data.
    /// </summary>
    private static void ReadRiichiStatus(AtkUnitBase* addon, bool[] riichiStatus)
    {
        // Riichi sticks are typically narrow horizontal image nodes placed sideways
        // across or near the discard pool. Without confirmed node IDs, this is
        // a best-effort scan for distinctive narrow rectangular image nodes
        // that are visible and positioned near the board center.
        try
        {
            var uld = addon->UldManager;

            for (int i = 0; i < uld.NodeListCount; i++)
            {
                try
                {
                    var n = uld.NodeList[i];
                    if (n == null) continue;
                    if (n->Type != NodeType.Image) continue;

                    bool vis = false;
                    try { vis = n->IsVisible(); } catch { }
                    if (!vis) continue;

                    // Riichi sticks are typically long and thin (e.g., ~80-120 x 5-15)
                    var w = n->Width;
                    var h = n->Height;
                    bool isHorizontalStick = (w > 50 && h > 0 && h < 20 && w > h * 3);
                    bool isVerticalStick = (h > 50 && w > 0 && w < 20 && h > w * 3);

                    if (!isHorizontalStick && !isVerticalStick)
                        continue;

                    // Classify by position: bottom=player, top=opposite, left=left, right=right
                    if (n->Y > 300)
                        riichiStatus[0] = true; // player
                    else if (n->Y < 150)
                        riichiStatus[2] = true; // opposite
                    else if (n->X < 250)
                        riichiStatus[3] = true; // left
                    else if (n->X > 550)
                        riichiStatus[1] = true; // right
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Infers the current game phase from available signals:
    /// - Call prompts visible = CallDecisionPrompt (or specific call type)
    /// - Player has 14 tiles (13 hand + 1 draw) = WaitingForDiscard
    /// - Player has 13 tiles (no draw) = WaitingForDraw
    /// - AgentState from AtkValues may provide additional signal
    /// </summary>
    private static GamePhase InferGamePhase(AtkUnitBase* addon, CallOptions availableCalls, IReadOnlyList<int> rawAtkInts)
    {
        // If call prompts are visible, we're in a call decision
        if (availableCalls.HasFlag(CallOptions.Ron))
            return GamePhase.RonDecisionPrompt;
        if (availableCalls.HasFlag(CallOptions.Tsumo))
            return GamePhase.TsumoDecisionPrompt;
        if (availableCalls.HasFlag(CallOptions.Riichi))
            return GamePhase.RiichiDecisionPrompt;
        if (availableCalls != CallOptions.None && availableCalls != CallOptions.Skip)
            return GamePhase.CallDecisionPrompt;

        return GamePhase.Unknown;
    }

    private static List<UiSlot> BuildCanonicalHand(List<UiSlot> rawHand)
    {
        // Known noisy pattern: some fixed hand indices are placeholders at x=0 with no icon.
        // Canonical view prioritizes visible, positioned slots and falls back to icon-positive slots.
        var filtered = rawHand
            .Where(slot => slot.Visible)
            .Where(slot => slot.X > 0 || slot.IconId > 0)
            .OrderBy(slot => slot.X)
            .ThenBy(slot => slot.SlotIndex)
            .ToList();

        if (filtered.Count == 0)
        {
            filtered = rawHand
                .Where(slot => slot.Visible)
                .OrderBy(slot => slot.SlotIndex)
                .ToList();
        }

        if (filtered.Count > 14)
            filtered = filtered.TakeLast(14).ToList();

        return filtered;
    }

    /// <summary>
    /// Detects the drawn tile by finding a position gap significantly larger than
    /// the normal tile spacing. In Mahjong UI, the drawn tile is visually separated
    /// from the sorted hand by a wider gap (typically ~10+ pixels vs ~42 normal).
    /// </summary>
    private static UiSlot? BuildCanonicalDraw(List<UiSlot> canonicalHand)
    {
        if (canonicalHand.Count < 2)
            return null;

        // Tiles should already be sorted by X.
        // Find the largest gap between consecutive tiles.
        float maxGap = 0;
        int maxGapIndex = -1;

        for (int i = 1; i < canonicalHand.Count; i++)
        {
            var gap = canonicalHand[i].X - canonicalHand[i - 1].X;
            if (gap > maxGap)
            {
                maxGap = gap;
                maxGapIndex = i;
            }
        }

        // Normal tile spacing is ~42px (tile width). A drawn tile gap is noticeably larger.
        // Use a threshold of 1.2x normal spacing to detect.
        const float gapThreshold = 50f;

        if (maxGap < gapThreshold || maxGapIndex < 0)
            return null;

        // The drawn tile is the one after the largest gap (rightmost separated tile).
        return canonicalHand[maxGapIndex];
    }

    private static bool TryReadNodeSlot(AtkUldManager uld, int nodeIndex, IconIdCapture? iconCapture, MahjongIconMap? iconMap, SlotKind kind, int slotIndex, out UiSlot slot)
    {
        slot = null!;
        if (nodeIndex < 0 || nodeIndex >= uld.NodeListCount)
            return false;

        var node = uld.NodeList[nodeIndex];
        if (node == null)
            return false;

        bool visible;
        try { visible = node->IsVisible(); }
        catch { visible = false; }

        uint iconId = 0;
        TryFindIcon(node, iconCapture, out iconId);

        slot = new UiSlot(
            kind,
            slotIndex,
            nodeIndex,
            node->NodeId,
            (ushort)node->Type,
            visible,
            node->X,
            node->Y,
            node->Width,
            node->Height,
            iconId,
            iconId > 0 ? iconMap?.Resolve(iconId) : null);

        return true;
    }

    private static bool TryFindIcon(AtkResNode* root, IconIdCapture? capture, out uint iconId)
    {
        iconId = 0;
        var visited = new HashSet<nint>();
        return TryFindIconRecursive(root, capture, visited, 0, out iconId);
    }

    /// <summary>
    /// Public wrapper for TryFindIcon, used by TileDataDumper for spatial classification.
    /// </summary>
    public static bool TryFindIconPublic(AtkResNode* root, IconIdCapture? capture, out uint iconId)
        => TryFindIcon(root, capture, out iconId);

    private static bool TryFindIconRecursive(AtkResNode* root, IconIdCapture? capture, HashSet<nint> visited, int depth, out uint iconId)
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

                // Primary path: hook-based capture (fastest, works when hook has fired)
                if (capture != null)
                {
                    iconId = capture.GetIconId((nint)image);
                    if (iconId > 0)
                        return true;
                }

                // Fallback: read icon ID directly from the texture resource chain.
                // This works even after mid-game plugin reload when the LoadIconTexture
                // hook hasn't fired yet for already-loaded tile textures.
                iconId = TryReadIconIdFromStruct(image);
                if (iconId > 0)
                    return true;
            }

            for (var child = root->ChildNode; child != null; child = child->NextSiblingNode)
            {
                if (TryFindIconRecursive(child, capture, visited, depth + 1, out iconId))
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
                        if (TryFindIconRecursive(child, capture, visited, depth + 1, out iconId))
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

    /// <summary>
    /// Reads the icon ID directly from the AtkImageNode texture resource chain:
    /// PartsList → Parts[PartId] → UldAsset → AtkTexture.Resource → IconId.
    /// Returns 0 if the chain is broken or the node is not icon-mode.
    /// </summary>
    private static uint TryReadIconIdFromStruct(AtkImageNode* image)
    {
        try
        {
            if (image == null)
                return 0;

            // Icon-mode images have Flags containing 0x80
            if (((byte)image->Flags & 0x80) == 0)
                return 0;

            var partsList = image->PartsList;
            if (partsList == null || partsList->Parts == null)
                return 0;

            if (image->PartId >= partsList->PartCount)
                return 0;

            var part = partsList->Parts[image->PartId];
            if (part.UldAsset == null)
                return 0;

            var tex = part.UldAsset->AtkTexture;
            if (tex.Resource == null)
                return 0;

            return tex.Resource->IconId;
        }
        catch
        {
            return 0;
        }
    }
}
