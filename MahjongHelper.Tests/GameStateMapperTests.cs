using System.Text.Json;
using MahjongHelper.Mahjong;
using Xunit;

namespace MahjongHelper.Tests;

public class GameStateMapperTests
{
    /// <summary>Same field names UiGameInfo already reads from EmjL.</summary>
    private sealed record UiGameInfoTableMeta(
        int? Honba,
        int? RiichiSticks,
        int? RoundNumber,
        int? PlayerScore,
        int? RightScore,
        int? OppositeScore,
        int? LeftScore);

    private static string? ResolveTile(uint iconId) => iconId switch
    {
        76041 => "M1",
        76042 => "M2",
        76043 => "M3",
        76075 => "M0",
        _ => null,
    };

    private static MahjongGameState StateFromUiGameInfo(UiGameInfoTableMeta info, MahjongGameState? previous = null)
    {
        return MahjongGameState.Empty() with
        {
            HandIconIds = new StateField<IReadOnlyList<uint>>(
                [76041, 76042, 76043], MahjongStateSource.Node, true, false),
            DrawIconId = new StateField<uint>(76075, MahjongStateSource.Node, true, false),
            Honba = TableMeta.MergeInt(info.Honba, previous?.Honba),
            RiichiSticks = TableMeta.MergeInt(info.RiichiSticks, previous?.RiichiSticks),
            RoundNumber = TableMeta.MergeInt(info.RoundNumber, previous?.RoundNumber),
            PlayerScore = TableMeta.MergeInt(info.PlayerScore, previous?.PlayerScore),
            RightScore = TableMeta.MergeInt(info.RightScore, previous?.RightScore),
            OppositeScore = TableMeta.MergeInt(info.OppositeScore, previous?.OppositeScore),
            LeftScore = TableMeta.MergeInt(info.LeftScore, previous?.LeftScore),
            RightDiscards = new StateField<IReadOnlyList<string>>(
                ["S9"], MahjongStateSource.Node, true, false),
            OppositeDiscards = new StateField<IReadOnlyList<string>>(
                ["P1"], MahjongStateSource.Node, true, false),
            LeftDiscards = new StateField<IReadOnlyList<string>>(
                ["EAST"], MahjongStateSource.Node, true, false),
        };
    }

    [Fact]
    public void MergeInt_from_UiGameInfo_keeps_zero_honba_and_caches_when_unread()
    {
        var info = new UiGameInfoTableMeta(0, 1, 3, 25000, 24000, 28000, 23000);

        var honba = TableMeta.MergeInt(info.Honba, null);
        Assert.Equal(0, honba.Value);
        Assert.Equal(MahjongStateSource.Node, honba.Source);
        Assert.True(honba.IsAuthoritative);

        var cached = TableMeta.MergeInt(null, honba);
        Assert.Equal(0, cached.Value);
        Assert.Equal(MahjongStateSource.Cached, cached.Source);
        Assert.True(cached.IsFallback);
        Assert.False(cached.IsAuthoritative);

        var missing = TableMeta.MergeInt(null, null);
        Assert.Equal(MahjongStateSource.Unknown, missing.Source);
        Assert.Null(TableMeta.Known(missing));
    }

    [Fact]
    public void Mapper_posts_table_meta_from_UiGameInfo_on_suggest_and_evaluate()
    {
        var info = new UiGameInfoTableMeta(2, 3, 4, 25100, 24200, 28300, 22400);
        var state = StateFromUiGameInfo(info);

        var suggest = GameStateMapper.BuildSuggestMoveRequest(state, ResolveTile);
        Assert.NotNull(suggest);
        Assert.Equal(2, suggest!.Honba);
        Assert.Equal(3, suggest.RiichiSticks);
        Assert.Equal(4, suggest.RoundNumber);
        Assert.Equal(25100, suggest.PlayerScore);
        Assert.Equal(24200, suggest.RightScore);
        Assert.Equal(28300, suggest.OppositeScore);
        Assert.Equal(22400, suggest.LeftScore);
        Assert.Equal(3, suggest.Opponents!.Count);

        var evaluate = GameStateMapper.BuildEvaluateCallRequest(state, ResolveTile, "S9", "PON");
        Assert.NotNull(evaluate);
        Assert.Equal(2, evaluate!.Honba);
        Assert.Equal(3, evaluate.RiichiSticks);
        Assert.Equal(4, evaluate.RoundNumber);
        Assert.Equal(25100, evaluate.PlayerScore);
        Assert.Equal(24200, evaluate.RightScore);
        Assert.Equal(28300, evaluate.OppositeScore);
        Assert.Equal(22400, evaluate.LeftScore);
    }

    [Fact]
    public void Serialize_uses_snake_case_and_omits_unread_table_meta()
    {
        var populated = new SuggestMoveRequest
        {
            Hand = ["M1"],
            Honba = 0,
            RiichiSticks = 1,
            RoundNumber = 2,
            PlayerScore = 25000,
            RightScore = 24000,
            OppositeScore = 26000,
            LeftScore = 25000,
        };

        var json = SolverJson.Serialize(populated);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(0, root.GetProperty("honba").GetInt32());
        Assert.Equal(1, root.GetProperty("riichi_sticks").GetInt32());
        Assert.Equal(2, root.GetProperty("round_number").GetInt32());
        Assert.Equal(25000, root.GetProperty("player_score").GetInt32());
        Assert.Equal(24000, root.GetProperty("right_score").GetInt32());
        Assert.Equal(26000, root.GetProperty("opposite_score").GetInt32());
        Assert.Equal(25000, root.GetProperty("left_score").GetInt32());
        Assert.False(root.TryGetProperty("scores", out _));

        var unread = GameStateMapper.BuildSuggestMoveRequest(StateFromUiGameInfo(
            new UiGameInfoTableMeta(null, null, null, null, null, null, null)), ResolveTile);
        Assert.NotNull(unread);
        var unreadJson = SolverJson.Serialize(unread!);
        using var unreadDoc = JsonDocument.Parse(unreadJson);
        var unreadRoot = unreadDoc.RootElement;
        Assert.False(unreadRoot.TryGetProperty("honba", out _));
        Assert.False(unreadRoot.TryGetProperty("riichi_sticks", out _));
        Assert.False(unreadRoot.TryGetProperty("round_number", out _));
        Assert.False(unreadRoot.TryGetProperty("player_score", out _));
        Assert.False(unreadRoot.TryGetProperty("right_score", out _));
        Assert.False(unreadRoot.TryGetProperty("opposite_score", out _));
        Assert.False(unreadRoot.TryGetProperty("left_score", out _));
    }

    [Fact]
    public void Display_and_snap_show_honba_sticks_round_and_scores()
    {
        var info = new UiGameInfoTableMeta(0, 2, 1, 25000, 24000, 28000, 23000);
        var state = StateFromUiGameInfo(info);
        var display = state.ToDisplayText();
        Assert.Contains("Honba:", display);
        Assert.Contains("RiichiSticks:", display);
        Assert.Contains("RoundNumber:", display);
        Assert.Contains("Scores: Player=", display);

        var request = GameStateMapper.BuildSuggestMoveRequest(state, ResolveTile);
        var summary = SolverJson.BuildSnapSummary(request);
        Assert.Contains("honba=0", summary);
        Assert.Contains("riichi_sticks=2", summary);
        Assert.Contains("round_number=1", summary);
        Assert.Contains("scores=P25000/R24000/O28000/L23000", summary);
    }
}
