using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongHelper.Mahjong;

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
        OpponentTurn,
        CallDecisionPrompt,
        RiichiDecisionPrompt,
        TsumoDecisionPrompt,
        RonDecisionPrompt,
        CallChoicePrompt,
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

    public enum SuggestionType
    {
        None,
        Discard,
        Pass,
        Chi,
        Pon,
        Kan,
        Ron,
        Tsumo,
        Riichi,
        Scoring,
    }

    public sealed record InGameSuggestion(SuggestionType Type, string RawText, string? TileName = null, int? TileIconId = null);

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
        int? CurrentTurn,
        IReadOnlyList<int> RawAtkInts,
        InGameSuggestion? Suggestion = null,
        Dictionary<CallOptions, nint>? CallButtonNodes = null)
    {
        public string ToDisplayText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"SeatWind: {FormatWind(SeatWind)}");
            sb.AppendLine($"RoundWind: {FormatWind(RoundWind)}");
            sb.AppendLine($"Round: {RoundNumber?.ToString() ?? "?"} Honba: {Honba?.ToString() ?? "?"} RiichiSticks: {RiichiSticks?.ToString() ?? "?"}");
            sb.AppendLine($"Scores: Player={PlayerScore?.ToString() ?? "?"} Right={RightScore?.ToString() ?? "?"} Opposite={OppositeScore?.ToString() ?? "?"} Left={LeftScore?.ToString() ?? "?"}");
            sb.AppendLine($"Riichi: Player={RiichiStatus[0]} Right={RiichiStatus[1]} Opposite={RiichiStatus[2]} Left={RiichiStatus[3]}");
            sb.AppendLine($"CurrentTurn: {FormatCurrentTurn(CurrentTurn)}");
            sb.AppendLine($"AvailableCalls: {AvailableCalls}");
            sb.AppendLine($"Phase: {Phase}");
            return sb.ToString();
        }

        private static string FormatCurrentTurn(int? turn) => turn switch
        {
            0 => "Player",
            1 => "Right",
            2 => "Opposite",
            3 => "Left",
            _ => "?",
        };

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
                new bool[4], CallOptions.None, GamePhase.Unknown, null, Array.Empty<int>());
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

            // 50x60 type=1006 = dora indicator slots (nodeIds 28-32, up to 5 kan dora)
            // Only nodes with a valid tile icon are revealed dora indicators.
            if (type == 1006 && node->Width == 50 && node->Height == 60)
            {
                uint iconId = 0;
                TryFindIcon(node, iconCapture, out iconId);

                if (iconId >= 76041 && iconId <= 76077)
                {
                    slots.Add(new UiSlot(
                        SlotKind.DoraIndicator,
                        0, // slot index assigned below after collecting all
                        i,
                        node->NodeId,
                        (ushort)node->Type,
                        visible,
                        node->X,
                        node->Y,
                        node->Width,
                        node->Height,
                        iconId,
                        iconMap?.Resolve(iconId)));
                }
            }
        }

        // Re-index dora indicator slots by X position (leftmost = index 0)
        var doraSlots = slots.Where(s => s.Kind == SlotKind.DoraIndicator).OrderBy(s => s.X).ToList();
        slots.RemoveAll(s => s.Kind == SlotKind.DoraIndicator);
        for (int di = 0; di < doraSlots.Count; di++)
            slots.Add(doraSlots[di] with { SlotIndex = di });

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

        var gameInfo = ReadGameInfo(addon, iconCapture, iconMap);

        return new UiState(slots, gameInfo, DateTime.UtcNow);
    }

    /// <summary>
    /// Classifies 34x45 tile nodes into discard pools (4 players) based on their
    /// component NodeType. Each player's discard pool uses a distinct component type:
    ///   1021 = local player discards
    ///   1022 = left player (kamicha) discards
    ///   1023 = right player (shimocha) discards
    ///   1024 = opposite player (toimen) discards
    /// Dora indicators are read separately via RootNode child-tree navigation.
    /// </summary>
    private static void ClassifySmallTiles(AtkUldManager uld, List<UiSlot> smallTiles, IconIdCapture? iconCapture, MahjongIconMap? iconMap, List<UiSlot> outputSlots)
    {
        if (smallTiles.Count == 0)
            return;

        // Only consider tiles that have an icon (face-up tiles)
        var tilesWithIcons = smallTiles.Where(t => t.IconId > 0).ToList();
        if (tilesWithIcons.Count == 0)
            return;

        // Classify by NodeType — each player's discard pool uses a distinct component type.
        // The game stores discard tiles newest-first in the node list, so we group by
        // player, reverse each group to get chronological order, then assign slot indices.
        var groups = new Dictionary<SlotKind, List<UiSlot>>
        {
            { SlotKind.PlayerDiscard, new List<UiSlot>() },
            { SlotKind.LeftDiscard, new List<UiSlot>() },
            { SlotKind.RightDiscard, new List<UiSlot>() },
            { SlotKind.OppositeDiscard, new List<UiSlot>() },
        };

        foreach (var tile in tilesWithIcons)
        {
            var kind = tile.NodeType switch
            {
                1021 => SlotKind.PlayerDiscard,
                1022 => SlotKind.LeftDiscard,
                1023 => SlotKind.RightDiscard,
                1024 => SlotKind.OppositeDiscard,
                _ => (SlotKind?)null,
            };

            if (kind == null)
                continue;

            groups[kind.Value].Add(tile);
        }

        foreach (var (kind, tiles) in groups)
        {
            tiles.Reverse();
            for (int i = 0; i < tiles.Count; i++)
                outputSlots.Add(tiles[i] with { Kind = kind, SlotIndex = i });
        }
    }

    /// <summary>
    /// Reads game info using RootNode child-tree navigation by NodeID,
    /// following the approach from DomanMahjongStatus.
    ///
    /// Known paths (NodeID chains from RootNode):
    ///   Round indicator:  root → 16 → 19  (image node, texture IconID 121451-121458)
    ///   Honba count:      root → 21 → 23  (text "×N")
    ///   Riichi sticks:    root → 21 → 22  (text "×N")
    ///   Player pane:      root → 36 → 37 → 38  (player), 36 → 39 → 40 (right), 36 → 41 → 42 (across), 36 → 43 → 44 (left)
    ///     Score:          pane → 10/11 → 12/13 → 2  (text)
    ///     Seat wind:      pane → 7/8 → 9/10  (text)
    ///     Current turn:   pane → 14/15  (has visible children when it's that player's turn)
    ///   Dora (score screen only): root → 46 → 54 → 80 → 83
    /// </summary>
    private static UiGameInfo ReadGameInfo(AtkUnitBase* addon, IconIdCapture? iconCapture, MahjongIconMap? iconMap)
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
        int? currentTurn = null;
        var rawAtkInts = new List<int>();

        try
        {
            var valCount = addon->AtkValuesCount;
            for (int i = 0; i < valCount && i < 500; i++)
            {
                try
                {
                    var val = addon->AtkValues[i];
                    if (val.Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int ||
                        val.Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt)
                        rawAtkInts.Add(val.Int);
                    else
                        rawAtkInts.Add(0);
                }
                catch { rawAtkInts.Add(0); }
            }

            var root = addon->RootNode;
            if (root != null)
            {
                // Round indicator: root → 16 → 19 (image with texture IconID)
                var roundImageNode = FindChildById(root, 16, 19);
                if (roundImageNode != null && roundImageNode->Type == NodeType.Image)
                {
                    var iconId = (int)TryReadIconIdFromStruct((AtkImageNode*)roundImageNode);
                    (roundWind, roundNumber) = iconId switch
                    {
                        121451 => (0, 1),  // East 1
                        121452 => (0, 2),  // East 2
                        121453 => (0, 3),  // East 3
                        121454 => (0, 4),  // East 4
                        121455 => (1, 1),  // South 1
                        121456 => (1, 2),  // South 2
                        121457 => (1, 3),  // South 3
                        121458 => (1, 4),  // South 4
                        _ => ((int?)null, (int?)null),
                    };
                }

                // Honba count: root → 21 → 23 (text "×N")
                honba = ReadTextNodeInt(root, 21, 23);

                // Riichi stick count: root → 21 → 22 (text "×N")
                riichiSticks = ReadTextNodeInt(root, 21, 22);

                // Player panes — score + seat wind
                ReadPlayerPane(root, new[] { 36, 37, 38 }, true,
                    ref playerScore, ref seatWind);
                int? rightSeatWind = null, oppositeSeatWind = null, leftSeatWind = null;
                ReadPlayerPane(root, new[] { 36, 39, 40 }, false,
                    ref rightScore, ref rightSeatWind);
                ReadPlayerPane(root, new[] { 36, 41, 42 }, false,
                    ref oppositeScore, ref oppositeSeatWind);
                ReadPlayerPane(root, new[] { 36, 43, 44 }, false,
                    ref leftScore, ref leftSeatWind);

                // Current turn detection: pane → child(14) for player, child(15) for opponents.
                // The pane whose indicator child has visible children is the current turn.
                currentTurn = DetectCurrentTurn(root);
            }
        }
        catch
        {
            // Never crash from game info reading
        }

        // Detect call prompts from visible button-like components
        var availableCalls = ReadCallPrompts(addon, out var callButtonNodes);

        // Flush any suggestion "!" nodes collected during the scan
        FlushSuggestionNodes();

        // Log AtkValues changes for call prompt discovery
        LogAtkValuesIfChanged(addon);

        // Deep scan: dump ALL text at any depth in visible components (one-time snapshot per change)
        DumpAllComponentTextDeep(addon);

        // Phase A instrumentation: log String8 AtkValues and suggestion nodes
        LogSuggestionProbe(addon, availableCalls);

        // Dora indicator probe: scan all component nodes for tile icons
        LogDoraProbe(addon, iconCapture, iconMap);

        // Read the in-game suggestion from AtkValues[6] + node 45 tile icon
        var suggestion = ReadInGameSuggestion(addon, iconCapture, iconMap);

        // Infer game phase from available signals
        var phase = InferGamePhase(addon, availableCalls, rawAtkInts, suggestion);

        return new UiGameInfo(
            seatWind, roundWind, roundNumber, honba, riichiSticks,
            playerScore, rightScore, oppositeScore, leftScore,
            riichiStatus, availableCalls, phase, currentTurn, rawAtkInts, suggestion, callButtonNodes);
    }

    /// <summary>
    /// Reads a player info pane and extracts score and seat wind.
    /// For the local player: score at pane → 10 → 12 → 2, wind at pane → 7 → 9.
    /// For opponents:        score at pane → 11 → 13 → 2, wind at pane → 8 → 10.
    /// </summary>
    private static void ReadPlayerPane(AtkResNode* root, int[] paneIds, bool isPlayer,
        ref int? score, ref int? wind)
    {
        try
        {
            var pane = FindChildById(root, paneIds);
            if (pane == null) return;

            // Score
            var scoreNode = isPlayer
                ? FindChildById(pane, 10, 12, 2)
                : FindChildById(pane, 11, 13, 2);
            if (scoreNode != null)
            {
                var text = ReadNodeText(scoreNode);
                if (text != null && int.TryParse(text.Replace(",", ""), out var s))
                    score = s;
            }

            // Seat wind
            var windNode = isPlayer
                ? FindChildById(pane, 7, 9)
                : FindChildById(pane, 8, 10);
            if (windNode != null)
            {
                var text = ReadNodeText(windNode);
                if (text != null)
                {
                    wind = text.Trim() switch
                    {
                        "East" or "東" => 0,
                        "South" or "南" => 1,
                        "West" or "西" => 2,
                        "North" or "北" => 3,
                        _ => null,
                    };
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Detects whose turn it is by checking each player pane's turn indicator.
    /// The player pane uses child(14), opponents use child(15).
    /// The one with visible children in that indicator node is the current turn.
    /// Returns: 0=Player, 1=Right, 2=Opposite, 3=Left, null=unknown.
    /// </summary>
    private static int? DetectCurrentTurn(AtkResNode* root)
    {
        try
        {
            // (paneIds, indicatorChildId, turnIndex)
            var panes = new[]
            {
                (new[] { 36, 37, 38 }, 14, 0), // player
                (new[] { 36, 39, 40 }, 15, 1), // right
                (new[] { 36, 41, 42 }, 15, 2), // opposite
                (new[] { 36, 43, 44 }, 15, 3), // left
            };

            foreach (var (paneIds, indicatorId, turnIndex) in panes)
            {
                var pane = FindChildById(root, paneIds);
                if (pane == null) continue;

                var indicator = FindDirectChildById(pane, (uint)indicatorId);
                if (indicator == null) continue;

                // Check if indicator has any visible children
                if (HasVisibleChild(indicator))
                    return turnIndex;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Checks if a node has any visible child (either via ChildNode list or component UldManager).
    /// </summary>
    private static bool HasVisibleChild(AtkResNode* node)
    {
        if (node == null) return false;
        try
        {
            // Check component children
            if ((int)node->Type >= 1000)
            {
                var comp = (AtkComponentNode*)node;
                if (comp->Component != null)
                {
                    var uldRoot = comp->Component->UldManager.RootNode;
                    var sibling = uldRoot;
                    int steps = 0;
                    while (sibling != null && steps++ < 100)
                    {
                        try { if (sibling->IsVisible()) return true; } catch { }
                        sibling = sibling->PrevSiblingNode;
                    }
                }
            }

            // Check direct children
            var child = node->ChildNode;
            int childSteps = 0;
            while (child != null && childSteps++ < 100)
            {
                try { if (child->IsVisible()) return true; } catch { }
                child = child->PrevSiblingNode;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Reads a text node at the given child ID path and parses it as an integer.
    /// Strips common prefixes like "×" used for honba/riichi stick counts.
    /// </summary>
    private static int? ReadTextNodeInt(AtkResNode* root, params int[] ids)
    {
        try
        {
            var node = FindChildById(root, ids);
            if (node == null) return null;
            var text = ReadNodeText(node);
            if (text == null) return null;
            var cleaned = text.Trim().TrimStart('×').Trim();
            if (int.TryParse(cleaned, out var val))
                return val;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Reads the text content of a Text node.
    /// </summary>
    private static string? ReadNodeText(AtkResNode* node)
    {
        try
        {
            if (node == null || node->Type != NodeType.Text) return null;
            var txt = (AtkTextNode*)node;
            return Marshal.PtrToStringUTF8((nint)txt->NodeText.StringPtr.Value);
        }
        catch { return null; }
    }

    /// <summary>
    /// Navigates the ATK node tree by following a chain of NodeIDs.
    /// Each step searches the children of the current node (or the component's internal
    /// UldManager children if the node is a component) for a child with the matching NodeID.
    /// This mirrors DomanMahjongStatus's GetChild(params int[] ids) approach.
    /// </summary>
    private static AtkResNode* FindChildById(AtkResNode* node, params int[] ids)
    {
        if (node == null || ids.Length == 0)
            return node;

        var current = node;
        foreach (var id in ids)
        {
            var child = FindDirectChildById(current, (uint)id);
            if (child == null)
                return null;
            current = child;
        }
        return current;
    }

    /// <summary>
    /// Finds a direct child node with the given NodeID.
    /// If the current node is a component, searches its internal UldManager children.
    /// Otherwise, searches ChildNode linked list.
    /// </summary>
    private static AtkResNode* FindDirectChildById(AtkResNode* node, uint id)
    {
        if (node == null) return null;

        try
        {
            // If it's a component node, search UldManager children
            if ((int)node->Type >= 1000)
            {
                var comp = (AtkComponentNode*)node;
                if (comp->Component != null)
                {
                    // Search via UldManager RootNode sibling chain (DomanMahjongStatus style)
                    var uldRoot = comp->Component->UldManager.RootNode;
                    var sibling = uldRoot;
                    int steps = 0;
                    while (sibling != null && steps++ < 200)
                    {
                        if (sibling->NodeId == id)
                            return sibling;
                        sibling = sibling->PrevSiblingNode;
                    }

                    // Also check NodeList as fallback
                    var childUld = comp->Component->UldManager;
                    for (int i = 0; i < childUld.NodeListCount && i < 200; i++)
                    {
                        var cn = childUld.NodeList[i];
                        if (cn != null && cn->NodeId == id)
                            return cn;
                    }
                }
            }

            // Search ChildNode linked list
            var child = node->ChildNode;
            int childSteps = 0;
            while (child != null && childSteps++ < 200)
            {
                if (child->NodeId == id)
                    return child;
                child = child->PrevSiblingNode;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Scans for visible call prompt buttons (chi, pon, kan, ron, tsumo, riichi, skip/pass).
    /// In FFXIV Mahjong, call prompts appear as clickable button components when the game
    /// offers a call decision. The presence/visibility of these components indicates
    /// which calls are available.
    /// </summary>
    private static string _lastAtkValuesSignature = "";
    private static string _lastDeepScanSignature = "";
    private static string _lastSuggestionProbeSignature = "";
    private static string _lastSuggestionNodesSignature = "";

    /// <summary>
    /// Deep-scans all component nodes up to 3 levels deep for ANY text, including
    /// invisible nodes. Writes snapshot to file only when content changes.
    /// </summary>
    private static void DumpAllComponentTextDeep(AtkUnitBase* addon)
    {
        try
        {
            var lines = new List<string>();
            var uld = addon->UldManager;

            for (int i = 0; i < uld.NodeListCount; i++)
            {
                try
                {
                    var n = uld.NodeList[i];
                    if (n == null) continue;
                    var nType = (int)n->Type;
                    if (nType < 1000) continue;

                    // Skip tile-sized
                    if ((n->Width == 34 && n->Height == 45) || (n->Width == 42 && n->Height == 55))
                        continue;

                    bool vis = false;
                    try { vis = n->IsVisible(); } catch { }

                    var comp = (AtkComponentNode*)n;
                    if (comp->Component == null) continue;

                    var texts = new List<string>();
                    CollectTextRecursive(comp->Component, texts, 0, 3);

                    if (texts.Count > 0)
                    {
                        lines.Add($"[{i}] id={n->NodeId} type={nType} size=({n->Width},{n->Height}) vis={vis}");
                        foreach (var t in texts)
                            lines.Add($"  {t}");
                    }
                }
                catch { }
            }

            if (lines.Count == 0) return;

            var sig = string.Join("\n", lines);
            if (sig == _lastDeepScanSignature) return;
            _lastDeepScanSignature = sig;

            var logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MahjongHelper");
            System.IO.Directory.CreateDirectory(logDir);
            var entry = $"[{DateTime.UtcNow:O}]\n{sig}\n\n";
            System.IO.File.AppendAllText(System.IO.Path.Combine(logDir, "deep_text_scan.log"), entry);
        }
        catch { }
    }

    private static void CollectTextRecursive(AtkComponentBase* comp, List<string> results, int depth, int maxDepth)
    {
        if (comp == null || depth > maxDepth) return;

        var childUld = comp->UldManager;
        var indent = new string(' ', depth * 2);

        for (int j = 0; j < childUld.NodeListCount && j < 64; j++)
        {
            try
            {
                var cn = childUld.NodeList[j];
                if (cn == null) continue;

                bool cVis = false;
                try { cVis = cn->IsVisible(); } catch { }

                if (cn->Type == NodeType.Text)
                {
                    var txt = (AtkTextNode*)cn;
                    string text = "";
                    try { text = Marshal.PtrToStringUTF8((nint)txt->NodeText.StringPtr.Value) ?? ""; } catch { }
                    if (!string.IsNullOrWhiteSpace(text))
                        results.Add($"{indent}d{depth}[{j}] TEXT id={cn->NodeId} vis={cVis} \"{text.Trim()}\"");
                }
                else if ((int)cn->Type >= 1000)
                {
                    var subComp = (AtkComponentNode*)cn;
                    if (subComp->Component != null)
                    {
                        results.Add($"{indent}d{depth}[{j}] COMP type={(int)cn->Type} id={cn->NodeId} vis={cVis} size=({cn->Width},{cn->Height})");
                        CollectTextRecursive(subComp->Component, results, depth + 1, maxDepth);
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Logs the first 16 AtkValues whenever they change, to discover which
    /// indices encode call prompt availability.
    /// </summary>
    private static void LogAtkValuesIfChanged(AtkUnitBase* addon)
    {
        try
        {
            var count = Math.Min((int)addon->AtkValuesCount, 16);
            var vals = new int[count];
            for (int i = 0; i < count; i++)
            {
                try { vals[i] = addon->AtkValues[i].Int; }
                catch { vals[i] = -1; }
            }

            var sig = string.Join(",", vals);
            if (sig == _lastAtkValuesSignature) return;
            _lastAtkValuesSignature = sig;

            var logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MahjongHelper");
            System.IO.Directory.CreateDirectory(logDir);
            var line = $"[{DateTime.UtcNow:O}] AtkValues[0..{count - 1}]: {sig}\n";
            System.IO.File.AppendAllText(System.IO.Path.Combine(logDir, "atkvalues_changes.log"), line);
        }
        catch { }
    }

    /// <summary>
    /// Phase A instrumentation: reads String8 AtkValues at key indices and logs them
    /// alongside rawAtk0, available calls, and visible suggestion text nodes.
    /// Logs only when something changes.
    /// </summary>
    private static void LogSuggestionProbe(AtkUnitBase* addon, CallOptions availableCalls)
    {
        try
        {
            var valCount = (int)addon->AtkValuesCount;
            int rawAtk0 = valCount > 0 ? addon->AtkValues[0].Int : -1;

            // Read Int AtkValues at key indices (icon IDs)
            int sugTileIcon = -1;
            if (valCount > 2)
            {
                try
                {
                    var v2 = addon->AtkValues[2];
                    if (v2.Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int ||
                        v2.Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt)
                        sugTileIcon = v2.Int;
                }
                catch { }
            }

            // Read String8 AtkValues at key indices
            int[] stringIndices = { 1, 6, 22, 23, 24, 45 };
            var strings = new Dictionary<int, string>();
            foreach (var idx in stringIndices)
            {
                if (idx >= valCount) continue;
                try
                {
                    var val = addon->AtkValues[idx];
                    if (val.Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String ||
                        val.Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String8)
                    {
                        strings[idx] = $"{val.String}";
                    }
                }
                catch { }
            }

            // Also read AtkValues[30-37] to see if they ever change
            var suggestionLabels = new Dictionary<int, string>();
            for (int idx = 30; idx <= 37 && idx < valCount; idx++)
            {
                try
                {
                    var val = addon->AtkValues[idx];
                    if (val.Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String ||
                        val.Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String8)
                    {
                        suggestionLabels[idx] = $"{val.String}";
                    }
                }
                catch { }
            }

            // Build signature for dedup
            var parts = new List<string>();
            parts.Add($"atk0={rawAtk0}");
            if (sugTileIcon > 0) parts.Add($"[2]icon={sugTileIcon}");
            foreach (var kv in strings.OrderBy(k => k.Key))
                parts.Add($"[{kv.Key}]=\"{kv.Value}\"");
            foreach (var kv in suggestionLabels.OrderBy(k => k.Key))
                parts.Add($"[{kv.Key}]=\"{kv.Value}\"");
            parts.Add($"calls={availableCalls}");

            var sig = string.Join(" | ", parts);
            if (sig == _lastSuggestionProbeSignature) return;
            _lastSuggestionProbeSignature = sig;

            var logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MahjongHelper");
            System.IO.Directory.CreateDirectory(logDir);
            var line = $"[{DateTime.UtcNow:O}] {sig}\n";
            System.IO.File.AppendAllText(System.IO.Path.Combine(logDir, "suggestion_probe.log"), line);
        }
        catch { }
    }

    /// <summary>
    /// Phase A2 instrumentation: logs visible suggestion "!" text nodes whenever the set changes.
    /// </summary>
    private static readonly List<string> _pendingSuggestionNodes = new();

    private static void LogSuggestionNode(string text, AtkResNode* cn, AtkComponentNode* ownerNode)
    {
        try
        {
            bool cnVis = false;
            try { cnVis = cn->IsVisible(); } catch { }
            bool ownerVis = false;
            try { ownerVis = ((AtkResNode*)ownerNode)->IsVisible(); } catch { }

            _pendingSuggestionNodes.Add($"\"{text}\" nodeVis={cnVis} ownerVis={ownerVis} ownerId={((AtkResNode*)ownerNode)->NodeId} ownerType={(int)((AtkResNode*)ownerNode)->Type}");
        }
        catch { }
    }

    /// <summary>
    /// Called after ReadCallPrompts completes to flush any pending suggestion node entries.
    /// </summary>
    private static void FlushSuggestionNodes()
    {
        try
        {
            var sig = _pendingSuggestionNodes.Count > 0
                ? string.Join(" ; ", _pendingSuggestionNodes)
                : "(none)";

            if (sig == _lastSuggestionNodesSignature)
            {
                _pendingSuggestionNodes.Clear();
                return;
            }
            _lastSuggestionNodesSignature = sig;

            var logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MahjongHelper");
            System.IO.Directory.CreateDirectory(logDir);
            var line = $"[{DateTime.UtcNow:O}] SuggestionNodes: {sig}\n";
            System.IO.File.AppendAllText(System.IO.Path.Combine(logDir, "suggestion_probe.log"), line);
        }
        catch { }
        finally
        {
            _pendingSuggestionNodes.Clear();
        }
    }

    /// <summary>
    /// Reads AtkValues[6] to determine the game's current action recommendation.
    /// This is the most reliable signal for what the game expects the player to do.
    /// </summary>

    private static string? _lastDoraProbeSignature;

    /// <summary>
    /// Diagnostic probe: scans ALL component nodes for mahjong tile icon IDs (76041-76077)
    /// to identify which nodes hold the dora indicator(s) during gameplay.
    /// Logs to %APPDATA%/MahjongHelper/dora_probe.log on change.
    /// </summary>
    private static void LogDoraProbe(AtkUnitBase* addon, IconIdCapture? iconCapture, MahjongIconMap? iconMap)
    {
        try
        {
            var uld = addon->UldManager;
            var entries = new List<string>();

            for (int i = 0; i < uld.NodeListCount; i++)
            {
                var node = uld.NodeList[i];
                if (node == null) continue;

                var type = (int)node->Type;
                if (type < 1000) continue;

                // Skip known discard tile types and hand tile types
                if (type == 1021 || type == 1022 || type == 1023 || type == 1024 || type == 1055)
                    continue;

                uint iconId = 0;
                try { TryFindIcon(node, iconCapture, out iconId); } catch { }

                // Only log nodes with valid mahjong tile icons
                if (iconId >= 76041 && iconId <= 76077)
                {
                    bool vis = false;
                    try { vis = node->IsVisible(); } catch { }
                    var tileCode = iconMap?.Resolve(iconId) ?? "?";
                    entries.Add($"idx={i} id={node->NodeId} type={type} vis={vis} pos=({node->X:F0},{node->Y:F0}) size=({node->Width},{node->Height}) icon={iconId} tile={tileCode}");
                }
            }

            // Also check root→46→54→80→83 (score screen dora path) and its siblings
            try
            {
                var root = addon->RootNode;
                if (root != null)
                {
                    var node83 = FindChildById(root, 46, 54, 80, 83);
                    if (node83 != null)
                    {
                        bool vis = false;
                        try { vis = node83->IsVisible(); } catch { }
                        uint iconId = 0;
                        try { TryFindIcon(node83, iconCapture, out iconId); } catch { }
                        var tileCode = iconId > 0 ? (iconMap?.Resolve(iconId) ?? "?") : "none";
                        entries.Add($"ROOT_PATH id={node83->NodeId} type={(int)node83->Type} vis={vis} icon={iconId} tile={tileCode}");
                    }
                }
            }
            catch { }

            var sig = entries.Count > 0 ? string.Join(" | ", entries) : "(none)";
            if (sig == _lastDoraProbeSignature) return;
            _lastDoraProbeSignature = sig;

            var logDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MahjongHelper");
            System.IO.Directory.CreateDirectory(logDir);
            var line = $"[{DateTime.UtcNow:O}] DoraNodes: {sig}\n";
            System.IO.File.AppendAllText(System.IO.Path.Combine(logDir, "dora_probe.log"), line);
        }
        catch { }
    }

    private static InGameSuggestion ReadInGameSuggestion(AtkUnitBase* addon, IconIdCapture? iconCapture, MahjongIconMap? iconMap)
    {
        try
        {
            var valCount = (int)addon->AtkValuesCount;
            if (valCount <= 6) return new InGameSuggestion(SuggestionType.None, "");

            var val = addon->AtkValues[6];
            if (val.Type != FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String &&
                val.Type != FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String8)
                return new InGameSuggestion(SuggestionType.None, "");

            var raw = $"{val.String}".Trim();
            if (string.IsNullOrEmpty(raw))
                return new InGameSuggestion(SuggestionType.None, "");

            // Read the suggested discard tile from node 45 (suggestion component).
            // Its visible child id=2 (type=1021) contains the actual recommended tile icon,
            // unlike AtkValues[1]/[2] which are just hover tooltips.
            string? tileName = null;
            int? tileIconId = null;
            try
            {
                // Find node 45 in the addon's ULD NodeList
                AtkResNode* sugNode = null;
                var uld = addon->UldManager;
                for (int i = 0; i < uld.NodeListCount; i++)
                {
                    var n = uld.NodeList[i];
                    if (n != null && n->NodeId == 45 && (int)n->Type >= 1000)
                    {
                        sugNode = n;
                        break;
                    }
                }

                if (sugNode != null)
                {
                    // Inside node 45, find the visible tile component (child id=2)
                    var tileNode = FindDirectChildById(sugNode, 2);
                    if (tileNode != null && tileNode->IsVisible())
                    {
                        uint iconId = 0;
                        if (TryFindIcon(tileNode, iconCapture, out iconId) && iconId > 0)
                        {
                            tileIconId = (int)iconId;
                            tileName = iconMap?.Resolve(iconId);
                        }
                    }
                }
            }
            catch { }

            var lower = raw.ToLowerInvariant();

            if (lower == "discard")
                return new InGameSuggestion(SuggestionType.Discard, raw, tileName, tileIconId);
            if (lower == "pass")
                return new InGameSuggestion(SuggestionType.Pass, raw);
            // "riichi" must be checked BEFORE "chi" — "riichi" contains the substring "chi"
            if (lower.Contains("riichi") || lower.Contains("reach"))
                return new InGameSuggestion(SuggestionType.Riichi, raw, tileName, tileIconId);
            if (lower.Contains("chi"))
                return new InGameSuggestion(SuggestionType.Chi, raw);
            if (lower.Contains("pon"))
                return new InGameSuggestion(SuggestionType.Pon, raw);
            if (lower.Contains("kan"))
                return new InGameSuggestion(SuggestionType.Kan, raw);
            if (lower.Contains("ron"))
                return new InGameSuggestion(SuggestionType.Ron, raw);
            if (lower.Contains("tsumo"))
                return new InGameSuggestion(SuggestionType.Tsumo, raw);
            if (lower.Contains("fu") && lower.Contains("han"))
                return new InGameSuggestion(SuggestionType.Scoring, raw);

            return new InGameSuggestion(SuggestionType.None, raw);
        }
        catch { return new InGameSuggestion(SuggestionType.None, ""); }
    }

    private static CallOptions ReadCallPrompts(AtkUnitBase* addon, out Dictionary<CallOptions, nint> buttonNodes)
    {
        var calls = CallOptions.None;
        buttonNodes = new Dictionary<CallOptions, nint>();

        try
        {
            var uld = addon->UldManager;

            for (int i = 0; i < uld.NodeListCount; i++)
            {
                try
                {
                    var n = uld.NodeList[i];
                    if (n == null) continue;
                    var nType = (int)n->Type;
                    if (nType < 1000) continue;

                    // Skip tile-sized components
                    if ((n->Width == 34 && n->Height == 45) || (n->Width == 42 && n->Height == 55))
                        continue;

                    bool vis = false;
                    try { vis = n->IsVisible(); } catch { }

                    // The call button container (type=1032) is marked invisible even when
                    // call buttons inside it are active. Always scan it.
                    if (!vis && nType != 1032)
                        continue;

                    var comp = (AtkComponentNode*)n;
                    if (comp->Component == null) continue;

                    // Scan up to 3 levels deep: container(1032) → list(1030) → button(1029) → text
                    ScanComponentForCalls(comp->Component, comp, ref calls, buttonNodes, 0, 3);
                }
                catch { }
            }
        }
        catch { }

        return calls;
    }

    private static void ScanComponentForCalls(AtkComponentBase* comp, AtkComponentNode* ownerNode,
        ref CallOptions calls, Dictionary<CallOptions, nint> buttonNodes, int depth, int maxDepth)
    {
        if (comp == null || depth > maxDepth) return;

        var childUld = comp->UldManager;
        for (int j = 0; j < childUld.NodeListCount && j < 64; j++)
        {
            try
            {
                var cn = childUld.NodeList[j];
                if (cn == null) continue;

                if (cn->Type == NodeType.Text)
                {
                    bool cVis = false;
                    try { cVis = cn->IsVisible(); } catch { }
                    if (!cVis) continue;

                    // Also verify parent component node is visible to avoid stale call buttons
                    // after a call prompt has been dismissed (text nodes inside invisible
                    // containers can still report IsVisible=true on their own flag).
                    bool ownerVis = false;
                    try { ownerVis = ((AtkResNode*)ownerNode)->IsVisible(); } catch { }
                    if (!ownerVis) continue;

                    var txt = (AtkTextNode*)cn;
                    string text;
                    try { text = Marshal.PtrToStringUTF8((nint)txt->NodeText.StringPtr.Value) ?? ""; }
                    catch { continue; }

                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var trimmed = text.Trim();

                    // Log AI suggestion labels on player panes (e.g., "Pon!", "Tsumo!")
                    // Actual call buttons use text without exclamation marks (e.g., "Pon", "Pass")
                    if (trimmed.EndsWith("!"))
                    {
                        LogSuggestionNode(trimmed, cn, ownerNode);
                        continue;
                    }

                    var lower = trimmed.ToLowerInvariant();

                    CallOptions? matched = null;
                    // IMPORTANT: Check riichi BEFORE chi — "riichi" contains "chi" as a substring.
                    if (lower.Contains("riichi") || lower.Contains("リーチ") || lower.Contains("reach"))
                        matched = CallOptions.Riichi;
                    else if (lower.Contains("chi") || lower.Contains("チー"))
                        matched = CallOptions.Chi;
                    else if (lower.Contains("pon") || lower.Contains("ポン"))
                        matched = CallOptions.Pon;
                    else if (lower.Contains("kan") || lower.Contains("カン"))
                        matched = CallOptions.Kan;
                    else if (lower.Contains("ron") || lower.Contains("ロン"))
                        matched = CallOptions.Ron;
                    else if (lower.Contains("tsumo") || lower.Contains("ツモ"))
                        matched = CallOptions.Tsumo;
                    else if (lower.Contains("skip") || lower.Contains("pass") || lower.Contains("cancel")
                             || lower.Contains("スキップ") || lower.Contains("キャンセル"))
                        matched = CallOptions.Skip;

                    if (matched.HasValue)
                    {
                        calls |= matched.Value;
                        // Store the owning AtkComponentNode* as the clickable button node
                        buttonNodes.TryAdd(matched.Value, (nint)ownerNode);
                    }
                }
                else if ((int)cn->Type >= 1000)
                {
                    var subComp = (AtkComponentNode*)cn;
                    if (subComp->Component != null)
                        ScanComponentForCalls(subComp->Component, subComp, ref calls, buttonNodes, depth + 1, maxDepth);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Infers the current game phase from available signals:
    /// - In-game suggestion from AtkValues[6] is the single source of truth
    /// - AtkVal[0] only used as fallback when suggestion is empty/None
    /// </summary>
    private static GamePhase InferGamePhase(AtkUnitBase* addon, CallOptions availableCalls, IReadOnlyList<int> rawAtkInts, InGameSuggestion? suggestion)
    {
        var atkPhase = rawAtkInts.Count > 0 ? rawAtkInts[0] : -1;
        var sugType = suggestion?.Type ?? SuggestionType.None;

        // === PRIMARY: In-game suggestion [6] is the source of truth ===

        if (sugType == SuggestionType.Scoring)
            return GamePhase.BetweenRounds;

        // Discard with a visible suggestion tile = definitely our turn.
        // Discard WITHOUT a tile icon = stale [6]="Discard" from a previous turn;
        // only trust it when atk0 also confirms (30 = draw turn, 2 = after-call discard).
        if (sugType == SuggestionType.Discard)
        {
            if (suggestion?.TileIconId != null)
                return GamePhase.WaitingForDiscard;
            if (atkPhase == 30 || atkPhase == 2)
                return GamePhase.WaitingForDiscard;
            // Stale Discard with no tile — fall through to AtkVal fallback
        }

        if (sugType == SuggestionType.Pass || sugType == SuggestionType.Chi ||
            sugType == SuggestionType.Pon || sugType == SuggestionType.Kan)
            return GamePhase.CallDecisionPrompt;

        if (sugType == SuggestionType.Ron)
            return GamePhase.RonDecisionPrompt;
        if (sugType == SuggestionType.Tsumo)
            return GamePhase.TsumoDecisionPrompt;
        if (sugType == SuggestionType.Riichi)
            return GamePhase.RiichiDecisionPrompt;

        // === FALLBACK: AtkVal[0] when suggestion is None/Discard-stale ===
        // [6]="Discard" persists stale into non-discard phases, so only trust
        // it when atk0 confirms (handled above). Otherwise fall through here.

        if (atkPhase == 30 || atkPhase == 2)
            return GamePhase.WaitingForDiscard;

        if (atkPhase == 15)
            return GamePhase.OpponentTurn;

        // atk0=25 = chi/pon choice sub-menu (multiple options for the same call)
        if (atkPhase == 25)
            return GamePhase.CallChoicePrompt;

        // atk0=29 = score screen (stable), atk0=32 = score animation / transition
        if (atkPhase == 29 || atkPhase == 32)
            return GamePhase.BetweenRounds;

        // atk0=6 with no clear suggestion — check call buttons for specific prompts.
        // AtkValues[6] often doesn't populate for Tsumo/Ron/Riichi — the game shows
        // buttons but the suggestion text stays empty. Use visible buttons as signal.
        if (atkPhase == 6)
        {
            if (availableCalls.HasFlag(CallOptions.Tsumo))
                return GamePhase.TsumoDecisionPrompt;
            if (availableCalls.HasFlag(CallOptions.Ron))
                return GamePhase.RonDecisionPrompt;
            if (availableCalls.HasFlag(CallOptions.Riichi))
                return GamePhase.RiichiDecisionPrompt;
            return GamePhase.CallDecisionPrompt;
        }

        return GamePhase.Unknown;

        // === DISABLED: Old call-button-based detection (stale buttons unreliable) ===
        // if (availableCalls.HasFlag(CallOptions.Ron))
        //     return GamePhase.RonDecisionPrompt;
        // if (availableCalls.HasFlag(CallOptions.Tsumo))
        //     return GamePhase.TsumoDecisionPrompt;
        // if (availableCalls.HasFlag(CallOptions.Riichi))
        //     return GamePhase.RiichiDecisionPrompt;
        // if (availableCalls != CallOptions.None && availableCalls != CallOptions.Skip)
        //     return GamePhase.CallDecisionPrompt;
    }

    private static List<UiSlot> BuildCanonicalHand(List<UiSlot> rawHand)
    {
        // Real hand tiles are nodes 59-71 (ids 134, 1340001-1340012) = 13 hand slots.
        // Node 54 (id=135) is the drawn tile slot — include it so BuildCanonicalDraw can detect the gap.
        // Nodes 55-58 (ids 1340013-1340016) are placeholders that can have stale icons — exclude them.
        var filtered = rawHand
            .Where(slot => slot.Visible)
            .Where(slot => slot.NodeIndex == 54 || (slot.NodeIndex >= 59 && slot.NodeIndex <= 71))
            .Where(slot => slot.X > 0 || slot.IconId > 0)
            .OrderBy(slot => slot.X)
            .ThenBy(slot => slot.SlotIndex)
            .ToList();

        if (filtered.Count == 0)
        {
            filtered = rawHand
                .Where(slot => slot.Visible)
                .Where(slot => slot.NodeIndex == 54 || (slot.NodeIndex >= 59 && slot.NodeIndex <= 71))
                .OrderBy(slot => slot.SlotIndex)
                .ToList();
        }

        // Remove duplicate tiles at the same X position (lingering discards).
        // When multiple tiles appear at the same X, keep only the one with the lowest SlotIndex.
        var deduped = filtered
            .GroupBy(slot => slot.X)
            .Select(g => g.OrderBy(s => s.SlotIndex).First())
            .OrderBy(slot => slot.X)
            .ThenBy(slot => slot.SlotIndex)
            .ToList();

        if (deduped.Count > 14)
            deduped = deduped.TakeLast(14).ToList();

        return deduped;
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

                // Primary path: read icon ID directly from the texture resource chain.
                // This is the ground truth — it reflects the currently loaded texture,
                // even when the game reuses image nodes for different tiles without
                // re-calling LoadIconTexture (which would leave the hook cache stale).
                iconId = TryReadIconIdFromStruct(image);
                if (iconId > 0)
                    return true;

                // Fallback: hook-based capture (useful after mid-game plugin reload
                // when the struct chain may not yet be populated).
                if (capture != null)
                {
                    iconId = capture.GetIconId((nint)image);
                    if (iconId > 0)
                        return true;
                }
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
    /// Public wrapper for TryReadIconIdFromStruct, used by MahjongHandReader.
    /// </summary>
    public static uint TryReadIconIdFromStructPublic(AtkImageNode* image)
        => TryReadIconIdFromStruct(image);

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

