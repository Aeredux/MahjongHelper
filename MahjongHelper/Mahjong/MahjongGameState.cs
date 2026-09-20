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
    StateField<int> Honba,
    StateField<int> RiichiSticks,
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
        sb.AppendLine($"Honba: {FormatValue(Honba)}");
        sb.AppendLine($"RiichiSticks: {FormatValue(RiichiSticks)}");
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

    public static MahjongGameState Empty(DateTime? utcCapturedAt = null) => new(
        utcCapturedAt ?? DateTime.UnixEpoch,
        StateField<int>.Missing(),
        StateField<IReadOnlyList<uint>>.Missing(),
        StateField<uint>.Missing(),
        StateField<string>.Missing(),
        StateField<IReadOnlyList<string>>.Missing(),
        StateField<IReadOnlyList<string>>.Missing(),
        StateField<IReadOnlyList<string>>.Missing(),
        StateField<IReadOnlyList<string>>.Missing(),
        StateField<IReadOnlyList<string>>.Missing(),
        StateField<int>.Missing(),
        StateField<int>.Missing(),
        StateField<int>.Missing(),
        StateField<int>.Missing(),
        StateField<int>.Missing(),
        StateField<IReadOnlyList<bool>>.Missing(),
        StateField<int>.Missing(),
        StateField<int>.Missing(),
        StateField<int>.Missing(),
        StateField<int>.Missing(),
        StateField<string>.Missing(),
        StateField<string>.Missing(),
        StateField<string>.Missing(),
        StateField<string>.Missing(),
        StateField<IReadOnlyList<bool>>.Missing(),
        StateField<IReadOnlyList<bool>>.Missing(),
        StateField<IReadOnlyList<bool>>.Missing(),
        StateField<IReadOnlyList<bool>>.Missing(),
        StateField<IReadOnlyList<ObservedMeld>>.Missing(),
        StateField<IReadOnlyList<ObservedMeld>>.Missing(),
        StateField<IReadOnlyList<ObservedMeld>>.Missing(),
        StateField<IReadOnlyList<ObservedMeld>>.Missing());
}

/// <summary>
/// Table-meta merge + POST mapping for KAN-91 / KAN-92.
/// Honba and riichi sticks send 0 when known; omitted only when unread.
/// Seat-relative scores are flat: player / right / opposite / left, matching opponents[].
/// </summary>
public static class TableMeta
{
    public static StateField<int> FromNode(int value)
        => new(value, MahjongStateSource.Node, IsAuthoritative: true, IsFallback: false);

    /// <summary>
    /// Same merge used for UiGameInfo honba, riichi sticks, round number, and seat scores.
    /// </summary>
    public static StateField<int> MergeInt(int? current, StateField<int>? previous)
    {
        if (current.HasValue)
            return FromNode(current.Value);

        if (previous is { Source: not MahjongStateSource.Unknown } prev)
            return prev with { Source = MahjongStateSource.Cached, IsAuthoritative = false, IsFallback = true };

        return StateField<int>.Missing();
    }

    public static void Apply(ITableMetaRequest request, MahjongGameState state)
    {
        request.Honba = Known(state.Honba);
        request.RiichiSticks = Known(state.RiichiSticks);
        request.RoundNumber = Known(state.RoundNumber);
        request.PlayerScore = Known(state.PlayerScore);
        request.RightScore = Known(state.RightScore);
        request.OppositeScore = Known(state.OppositeScore);
        request.LeftScore = Known(state.LeftScore);
    }

    /// <summary>
    /// <see cref="StateField{T}.Missing"/> uses default(int)=0, so honba 0 vs unread is distinguished by Source.
    /// </summary>
    public static int? Known(StateField<int> field)
        => field.Source == MahjongStateSource.Unknown ? null : field.Value;
}

