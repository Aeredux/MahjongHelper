using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SamplePlugin.Mahjong;

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
    StateField<string> InGameSuggestion)
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
        sb.AppendLine($"PlayerDiscards: {FormatValue(PlayerDiscards)}");
        sb.AppendLine($"RightDiscards: {FormatValue(RightDiscards)}");
        sb.AppendLine($"OppositeDiscards: {FormatValue(OppositeDiscards)}");
        sb.AppendLine($"LeftDiscards: {FormatValue(LeftDiscards)}");
        sb.AppendLine($"DoraIndicators: {FormatValue(DoraIndicators)}");
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
}

public static class MahjongGameStateBuilder
{
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
            mergedSuggestion);
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
