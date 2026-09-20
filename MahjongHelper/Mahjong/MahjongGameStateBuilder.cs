using System;
using System.Collections.Generic;
using System.Linq;

namespace MahjongHelper.Mahjong;

public static class MahjongGameStateBuilder
{
    internal static readonly PondTsumogiriTracker TsumogiriTracker = new();

    public static MahjongGameState Merge(int? probeAgentState, EmjUiReader.UiState nodeState, MahjongGameState? previous)
    {
        var now = DateTime.UtcNow;
        var gameInfo = nodeState.GameInfo;

        var nodeHand = nodeState.Slots
            .Where(s => s.Kind == EmjUiReader.SlotKind.CanonicalPlayerHand)
            .OrderBy(s => s.SlotIndex)
            .Select(s => s.IconId)
            .Where(id => id > 0)
            .ToArray();

        var nodeDraw = nodeState.Slots
            .Where(s => s.Kind == EmjUiReader.SlotKind.CanonicalPlayerDraw)
            .Select(s => s.IconId)
            .FirstOrDefault();

        var handDescription = BuildHandDescription(nodeState);

        var mergedAgentState = probeAgentState.HasValue
            ? new StateField<int>(probeAgentState.Value, MahjongStateSource.Probe, IsAuthoritative: true, IsFallback: false)
            : previous?.AgentState is { Source: not MahjongStateSource.Unknown } prevAgent
                ? prevAgent with { Source = MahjongStateSource.Cached, IsAuthoritative = false, IsFallback = true }
                : StateField<int>.Missing();

        var mergedHand = nodeHand.Length > 0
            ? new StateField<IReadOnlyList<uint>>(nodeHand, MahjongStateSource.Node, IsAuthoritative: true, IsFallback: false)
            : previous?.HandIconIds is { Value: not null } prevHand && prevHand.Value.Count > 0
                ? prevHand with { Source = MahjongStateSource.Cached, IsAuthoritative = false, IsFallback = true }
                : StateField<IReadOnlyList<uint>>.Missing();

        var mergedDraw = nodeDraw > 0
            ? new StateField<uint>(nodeDraw, MahjongStateSource.Node, IsAuthoritative: true, IsFallback: false)
            : previous?.DrawIconId is { Value: > 0 } prevDraw
                ? prevDraw with { Source = MahjongStateSource.Cached, IsAuthoritative = false, IsFallback = true }
                : StateField<uint>.Missing();

        var mergedDescription = !string.IsNullOrWhiteSpace(handDescription)
            ? new StateField<string>(handDescription, MahjongStateSource.Node, IsAuthoritative: true, IsFallback: false)
            : previous?.HandDescription is { Value: not null } prevDesc
                ? prevDesc with { Source = MahjongStateSource.Cached, IsAuthoritative = false, IsFallback = true }
                : StateField<string>.Missing();

        var mergedPlayerDiscards = MergeDiscardField(nodeState, EmjUiReader.SlotKind.PlayerDiscard, previous?.PlayerDiscards);
        var mergedRightDiscards = MergeDiscardField(nodeState, EmjUiReader.SlotKind.RightDiscard, previous?.RightDiscards);
        var mergedOppositeDiscards = MergeDiscardField(nodeState, EmjUiReader.SlotKind.OppositeDiscard, previous?.OppositeDiscards);
        var mergedLeftDiscards = MergeDiscardField(nodeState, EmjUiReader.SlotKind.LeftDiscard, previous?.LeftDiscards);
        var mergedDoraIndicators = MergeDiscardField(nodeState, EmjUiReader.SlotKind.DoraIndicator, previous?.DoraIndicators);

        // Table meta from UiGameInfo (honba / riichi sticks were previously dropped here)
        var mergedSeatWind = TableMeta.MergeInt(gameInfo.SeatWind, previous?.SeatWind);
        var mergedRoundWind = TableMeta.MergeInt(gameInfo.RoundWind, previous?.RoundWind);
        var mergedRoundNumber = TableMeta.MergeInt(gameInfo.RoundNumber, previous?.RoundNumber);
        var mergedHonba = TableMeta.MergeInt(gameInfo.Honba, previous?.Honba);
        var mergedRiichiSticks = TableMeta.MergeInt(gameInfo.RiichiSticks, previous?.RiichiSticks);
        var mergedPlayerScore = TableMeta.MergeInt(gameInfo.PlayerScore, previous?.PlayerScore);
        var mergedRightScore = TableMeta.MergeInt(gameInfo.RightScore, previous?.RightScore);
        var mergedOppositeScore = TableMeta.MergeInt(gameInfo.OppositeScore, previous?.OppositeScore);
        var mergedLeftScore = TableMeta.MergeInt(gameInfo.LeftScore, previous?.LeftScore);

        var riichiList = gameInfo.RiichiStatus.ToList() as IReadOnlyList<bool>;
        var anyRiichiKnown = gameInfo.RiichiStatus.Any(r => r);
        var mergedRiichi = anyRiichiKnown
            ? new StateField<IReadOnlyList<bool>>(riichiList, MahjongStateSource.Node, IsAuthoritative: true, IsFallback: false)
            : previous?.RiichiStatus is { Value: not null } prevRiichi && prevRiichi.Value.Count > 0
                ? prevRiichi with { Source = MahjongStateSource.Cached, IsAuthoritative = false, IsFallback = true }
                : new StateField<IReadOnlyList<bool>>(riichiList, MahjongStateSource.Node, IsAuthoritative: false, IsFallback: false);

        var callsStr = gameInfo.AvailableCalls != EmjUiReader.CallOptions.None
            ? gameInfo.AvailableCalls.ToString()
            : "None";
        var mergedCalls = new StateField<string>(callsStr, MahjongStateSource.Node, IsAuthoritative: true, IsFallback: false);

        var phaseStr = gameInfo.Phase.ToString();
        var mergedPhase = new StateField<string>(phaseStr, MahjongStateSource.Node, IsAuthoritative: true, IsFallback: false);

        var sugStr = "None";
        if (gameInfo.Suggestion != null)
        {
            var s = gameInfo.Suggestion;
            sugStr = $"{s.Type}:{s.RawText}";
            if (s.TileName != null) sugStr += $" tile={s.TileName}";
            if (s.TileIconId.HasValue) sugStr += $" icon={s.TileIconId.Value}";
        }
        var mergedSuggestion = new StateField<string>(sugStr, MahjongStateSource.Node, IsAuthoritative: true, IsFallback: false);

        var turnStr = gameInfo.CurrentTurn switch
        {
            0 => "Player",
            1 => "Right",
            2 => "Opposite",
            3 => "Left",
            _ => null,
        };
        var mergedCurrentTurn = turnStr != null
            ? new StateField<string>(turnStr, MahjongStateSource.Node, IsAuthoritative: true, IsFallback: false)
            : StateField<string>.Missing();

        if (phaseStr is "BetweenRounds" or "GameOver")
            TsumogiriTracker.Reset();

        var drawCode = nodeState.Slots
            .Where(s => s.Kind == EmjUiReader.SlotKind.CanonicalPlayerDraw)
            .Select(s => s.TileCode)
            .FirstOrDefault(MeldClassifier.IsUsableTile);

        var mergedPlayerTsumogiri = MergeTsumogiri(
            PondTsumogiriTracker.Player, nodeState, EmjUiReader.SlotKind.PlayerDiscard,
            mergedPlayerDiscards, drawCode, previous?.PlayerTsumogiri);
        var mergedRightTsumogiri = MergeTsumogiri(
            PondTsumogiriTracker.Right, nodeState, EmjUiReader.SlotKind.RightDiscard,
            mergedRightDiscards, null, previous?.RightTsumogiri);
        var mergedOppositeTsumogiri = MergeTsumogiri(
            PondTsumogiriTracker.Opposite, nodeState, EmjUiReader.SlotKind.OppositeDiscard,
            mergedOppositeDiscards, null, previous?.OppositeTsumogiri);
        var mergedLeftTsumogiri = MergeTsumogiri(
            PondTsumogiriTracker.Left, nodeState, EmjUiReader.SlotKind.LeftDiscard,
            mergedLeftDiscards, null, previous?.LeftTsumogiri);

        var mergedPlayerMelds = MergeMeldField(nodeState, EmjUiReader.SlotKind.PlayerMeld, previous?.PlayerMelds);
        var mergedRightMelds = MergeMeldField(nodeState, EmjUiReader.SlotKind.RightMeld, previous?.RightMelds);
        var mergedOppositeMelds = MergeMeldField(nodeState, EmjUiReader.SlotKind.OppositeMeld, previous?.OppositeMelds);
        var mergedLeftMelds = MergeMeldField(nodeState, EmjUiReader.SlotKind.LeftMeld, previous?.LeftMelds);

        return new MahjongGameState(
            now,
            mergedAgentState,
            mergedHand,
            mergedDraw,
            mergedDescription,
            mergedPlayerDiscards,
            mergedRightDiscards,
            mergedOppositeDiscards,
            mergedLeftDiscards,
            mergedDoraIndicators,
            mergedSeatWind,
            mergedRoundWind,
            mergedRoundNumber,
            mergedHonba,
            mergedRiichiSticks,
            mergedRiichi,
            mergedPlayerScore,
            mergedRightScore,
            mergedOppositeScore,
            mergedLeftScore,
            mergedCalls,
            mergedPhase,
            mergedCurrentTurn,
            mergedSuggestion,
            mergedPlayerTsumogiri,
            mergedRightTsumogiri,
            mergedOppositeTsumogiri,
            mergedLeftTsumogiri,
            mergedPlayerMelds,
            mergedRightMelds,
            mergedOppositeMelds,
            mergedLeftMelds);
    }

    private static StateField<IReadOnlyList<string>> MergeDiscardField(
        EmjUiReader.UiState nodeState,
        EmjUiReader.SlotKind kind,
        StateField<IReadOnlyList<string>>? previous)
    {
        var tileCodes = nodeState.Slots
            .Where(s => s.Kind == kind)
            .OrderBy(s => s.SlotIndex)
            .Select(s => s.TileCode ?? (s.IconId > 0 ? $"ICON_{s.IconId}" : "?"))
            .ToArray();

        if (tileCodes.Length > 0)
            return new StateField<IReadOnlyList<string>>(tileCodes, MahjongStateSource.Node, IsAuthoritative: true, IsFallback: false);

        if (previous is { Value: not null } prev && prev.Value.Count > 0)
            return prev with { Source = MahjongStateSource.Cached, IsAuthoritative = false, IsFallback = true };

        return StateField<IReadOnlyList<string>>.Missing();
    }

    private static StateField<IReadOnlyList<bool>> MergeTsumogiri(
        int playerIndex,
        EmjUiReader.UiState nodeState,
        EmjUiReader.SlotKind kind,
        StateField<IReadOnlyList<string>> discards,
        string? playerDraw,
        StateField<IReadOnlyList<bool>>? previous)
    {
        var slots = nodeState.Slots
            .Where(s => s.Kind == kind)
            .OrderBy(s => s.SlotIndex)
            .ToArray();

        if (slots.Length == 0)
        {
            if (previous is { Value: not null } prev && prev.Value.Count > 0)
                return prev with { Source = MahjongStateSource.Cached, IsAuthoritative = false, IsFallback = true };
            return StateField<IReadOnlyList<bool>>.Missing();
        }

        var observed = slots.Select(s => s.Tsumogiri).ToArray();
        var flags = TsumogiriTracker.Update(playerIndex, discards.Value, observed, playerDraw);
        return new StateField<IReadOnlyList<bool>>(flags, MahjongStateSource.Node, IsAuthoritative: true, IsFallback: false);
    }

    private static StateField<IReadOnlyList<ObservedMeld>> MergeMeldField(
        EmjUiReader.UiState nodeState,
        EmjUiReader.SlotKind kind,
        StateField<IReadOnlyList<ObservedMeld>>? previous)
    {
        var slots = nodeState.Slots
            .Where(s => s.Kind == kind)
            .OrderBy(s => s.SlotIndex)
            .ToArray();

        if (slots.Length == 0)
        {
            if (previous is { Value: not null } prev && prev.Value.Count > 0)
                return prev with { Source = MahjongStateSource.Cached, IsAuthoritative = false, IsFallback = true };
            return StateField<IReadOnlyList<ObservedMeld>>.Missing();
        }

        var byParent = slots
            .GroupBy(s => s.ParentNodeId)
            .OrderBy(g => g.Min(s => s.SlotIndex))
            .ToList();

        var melds = new List<ObservedMeld>();
        foreach (var group in byParent)
        {
            var tiles = group
                .OrderBy(s => s.SlotIndex)
                .Select(s => s.TileCode ?? (s.IconId > 0 ? $"ICON_{s.IconId}" : "?"))
                .Where(MeldClassifier.IsUsableTile)
                .ToArray();
            // Pair remainder: called set whose sideways tile was a 55×42 / leftover
            // the 42×55 strip never collected (live AZPC Green pon showed 2 uprights).
            melds.AddRange(MeldClassifier.SplitIntoMelds(tiles, acceptPairRemainder: true));
        }

        if (melds.Count == 0)
        {
            if (previous is { Value: not null } prev && prev.Value.Count > 0)
                return prev with { Source = MahjongStateSource.Cached, IsAuthoritative = false, IsFallback = true };
            return StateField<IReadOnlyList<ObservedMeld>>.Missing();
        }

        return new StateField<IReadOnlyList<ObservedMeld>>(melds, MahjongStateSource.Node, IsAuthoritative: true, IsFallback: false);
    }

    private static string BuildHandDescription(EmjUiReader.UiState nodeState)
    {
        var desc = nodeState.Slots
            .Where(s => s.Kind == EmjUiReader.SlotKind.CanonicalPlayerHand)
            .OrderBy(s => s.SlotIndex)
            .Select(s => s.TileCode ?? (s.IconId > 0 ? $"ICON_{s.IconId}" : string.Empty))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        return desc.Length == 0 ? string.Empty : string.Join(" ", desc);
    }
}
