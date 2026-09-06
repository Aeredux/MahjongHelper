using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MahjongHelper.Mahjong;

public enum MahjongStateSource
{
    Unknown,
    Probe,
    Node,
    Cached,
}

public sealed record StateField<T>(
    T? Value,
    MahjongStateSource Source,
    bool IsAuthoritative,
    bool IsFallback)
{
    public static StateField<T> Missing() => new(default, MahjongStateSource.Unknown, false, false);
}

public sealed record MahjongGameState(
    DateTime UtcCapturedAt,
    StateField<int> AgentState,
    StateField<IReadOnlyList<uint>> HandIconIds,
    StateField<uint> DrawIconId,
    StateField<string> HandDescription,
    StateField<IReadOnlyList<string>> PlayerDiscards,
    StateField<IReadOnlyList<string>> RightDiscards,
    StateField<IReadOnlyList<string>> OppositeDiscards,
    StateField<IReadOnlyList<string>> LeftDiscards,
    StateField<IReadOnlyList<string>> DoraIndicators,
    StateField<int> SeatWind,
    StateField<int> RoundWind,
    StateField<int> RoundNumber,
    StateField<IReadOnlyList<bool>> RiichiStatus,
    StateField<int> PlayerScore,
    StateField<int> RightScore,
    StateField<int> OppositeScore,
    StateField<int> LeftScore,
    StateField<string> AvailableCalls,
    StateField<string> GamePhase,
    StateField<string> CurrentTurn,
    StateField<string> InGameSuggestion,
    StateField<IReadOnlyList<bool>> PlayerTsumogiri,
    StateField<IReadOnlyList<bool>> RightTsumogiri,
    StateField<IReadOnlyList<bool>> OppositeTsumogiri,
    StateField<IReadOnlyList<bool>> LeftTsumogiri,
    StateField<IReadOnlyList<ObservedMeld>> PlayerMelds,
    StateField<IReadOnlyList<ObservedMeld>> RightMelds,
    StateField<IReadOnlyList<ObservedMeld>> OppositeMelds,
    StateField<IReadOnlyList<ObservedMeld>> LeftMelds)
{
    public string ToDisplayText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Captured: {UtcCapturedAt:O}");
        sb.AppendLine("Normalized Mahjong State");
        sb.AppendLine($"AgentState: {FormatValue(AgentState)}");
        sb.AppendLine($"GamePhase: {FormatValue(GamePhase)}");
        sb.AppendLine($"InGameSuggestion: {FormatValue(InGameSuggestion)}");
        sb.AppendLine($"SeatWind: {FormatValue(SeatWind)}");
        sb.AppendLine($"RoundWind: {FormatValue(RoundWind)}");
        sb.AppendLine($"RoundNumber: {FormatValue(RoundNumber)}");
        sb.AppendLine($"HandIconIds: {FormatValue(HandIconIds)}");
        sb.AppendLine($"DrawIconId: {FormatValue(DrawIconId)}");
        sb.AppendLine($"HandDescription: {FormatValue(HandDescription)}");
        sb.AppendLine($"Scores: Player={FormatValue(PlayerScore)} Right={FormatValue(RightScore)} Opposite={FormatValue(OppositeScore)} Left={FormatValue(LeftScore)}");
        sb.AppendLine($"RiichiStatus: {FormatValue(RiichiStatus)}");
        sb.AppendLine($"AvailableCalls: {FormatValue(AvailableCalls)}");
        sb.AppendLine($"CurrentTurn: {FormatValue(CurrentTurn)}");
        sb.AppendLine($"PlayerDiscards: {FormatPond(PlayerDiscards, PlayerTsumogiri)}");
        sb.AppendLine($"RightDiscards: {FormatPond(RightDiscards, RightTsumogiri)}");
        sb.AppendLine($"OppositeDiscards: {FormatPond(OppositeDiscards, OppositeTsumogiri)}");
        sb.AppendLine($"LeftDiscards: {FormatPond(LeftDiscards, LeftTsumogiri)}");
        sb.AppendLine($"DoraIndicators: {FormatValue(DoraIndicators)}  (Doman panel = dora, no Tenhou remap)");
        sb.AppendLine($"PlayerMelds: {FormatMelds(PlayerMelds)}");
        sb.AppendLine($"RightMelds: {FormatMelds(RightMelds)}");
        sb.AppendLine($"OppositeMelds: {FormatMelds(OppositeMelds)}");
        sb.AppendLine($"LeftMelds: {FormatMelds(LeftMelds)}");
        return sb.ToString();
    }

    private static string FormatValue<T>(StateField<T> field)
    {
        var valueText = field.Value switch
        {
            null => "(missing)",
            IReadOnlyList<uint> ids => ids.Count == 0 ? "(empty)" : string.Join(", ", ids),
            IReadOnlyList<string> strs => strs.Count == 0 ? "(empty)" : string.Join(" ", strs),
            IReadOnlyList<bool> bools => string.Join(", ", bools.Select(b => b ? "Y" : "N")),
            IReadOnlyList<ObservedMeld> melds => melds.Count == 0 ? "(empty)" : string.Join(" | ", melds),
            _ => field.Value!.ToString() ?? "(missing)",
        };

        var tags = new List<string>
        {
            $"src={field.Source}",
            field.IsAuthoritative ? "authoritative" : "non-authoritative",
        };

        if (field.IsFallback)
            tags.Add("fallback");

        return $"{valueText} [{string.Join(", ", tags)}]";
    }

    private static string FormatPond(StateField<IReadOnlyList<string>> tiles, StateField<IReadOnlyList<bool>> tsumogiri)
    {
        if (tiles.Value == null || tiles.Value.Count == 0)
            return FormatValue(tiles);

        var flags = tsumogiri.Value;
        var parts = new List<string>();
        for (var i = 0; i < tiles.Value.Count; i++)
        {
            var tile = tiles.Value[i];
            var mark = flags != null && i < flags.Count && flags[i] ? "*" : "";
            parts.Add(tile + mark);
        }

        var tags = new List<string>
        {
            $"src={tiles.Source}",
            tiles.IsAuthoritative ? "authoritative" : "non-authoritative",
        };
        if (tiles.IsFallback)
            tags.Add("fallback");
        return $"{string.Join(" ", parts)}  (* = tsumogiri) [{string.Join(", ", tags)}]";
    }

    private static string FormatMelds(StateField<IReadOnlyList<ObservedMeld>> field)
        => FormatValue(field);
}

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

        // New fields from UiGameInfo
        var mergedSeatWind = MergeNullableInt(gameInfo.SeatWind, previous?.SeatWind);
        var mergedRoundWind = MergeNullableInt(gameInfo.RoundWind, previous?.RoundWind);
        var mergedRoundNumber = MergeNullableInt(gameInfo.RoundNumber, previous?.RoundNumber);
        var mergedPlayerScore = MergeNullableInt(gameInfo.PlayerScore, previous?.PlayerScore);
        var mergedRightScore = MergeNullableInt(gameInfo.RightScore, previous?.RightScore);
        var mergedOppositeScore = MergeNullableInt(gameInfo.OppositeScore, previous?.OppositeScore);
        var mergedLeftScore = MergeNullableInt(gameInfo.LeftScore, previous?.LeftScore);

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

    private static StateField<int> MergeNullableInt(int? current, StateField<int>? previous)
    {
        if (current.HasValue)
            return new StateField<int>(current.Value, MahjongStateSource.Node, IsAuthoritative: true, IsFallback: false);

        if (previous is { Source: not MahjongStateSource.Unknown } prev)
            return prev with { Source = MahjongStateSource.Cached, IsAuthoritative = false, IsFallback = true };

        return StateField<int>.Missing();
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
