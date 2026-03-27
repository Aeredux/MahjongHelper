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
    StateField<string> HandDescription)
{
    public string ToDisplayText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Captured: {UtcCapturedAt:O}");
        sb.AppendLine("Normalized Mahjong State");
        sb.AppendLine($"AgentState: {FormatValue(AgentState)}");
        sb.AppendLine($"HandIconIds: {FormatValue(HandIconIds)}");
        sb.AppendLine($"DrawIconId: {FormatValue(DrawIconId)}");
        sb.AppendLine($"HandDescription: {FormatValue(HandDescription)}");
        return sb.ToString();
    }

    private static string FormatValue<T>(StateField<T> field)
    {
        var valueText = field.Value switch
        {
            null => "(missing)",
            IReadOnlyList<uint> ids => ids.Count == 0 ? "(empty)" : string.Join(", ", ids),
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

        return new MahjongGameState(
            now,
            mergedAgentState,
            mergedHand,
            mergedDraw,
            mergedDescription);
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
