using MahjongHelper.Mahjong;
using Xunit;

namespace MahjongHelper.Tests;

public class HintedDiscardPlannerTests
{
    private const uint NorthIcon = 76071;
    private const uint RedIcon = 76074;
    private const uint P6Icon = 76020;

    [Fact]
    public void Live_north_draw_on_node_54_maps_callback_7_pos_13()
    {
        var closed = Closed59To71Without("NORTH");
        var draw = T(54, 560, "NORTH", NorthIcon, 1055);

        var plan = HintedDiscardPlanner.PlanHintedDiscard(
            "NORTH", (int)NorthIcon, closed, closed, draw);

        Assert.Equal(HintedDiscardPlanner.Kind.FireCallback7, plan.Kind);
        Assert.Equal(13, plan.Callback7Pos);
        Assert.Equal(54, plan.NodeIndex);
        Assert.Contains("tsumogiri", plan.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not callback 8", plan.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(HintedDiscardPlanner.IsDiscardReadyAtk(6));
        Assert.True(HintedDiscardPlanner.Callback8IsSkip(6));
        Assert.False(HintedDiscardPlanner.Callback8IsSafeTsumogiri(6, waitingForDiscard: true));
        Assert.False(HintedDiscardPlanner.Callback8IsSafeTsumogiri(30, waitingForDiscard: true));
    }

    [Fact]
    public void Live_drawn_p6_on_node_54_maps_callback_7_not_a_random_closed_tile()
    {
        var closed = Closed59To71Without("P6");
        var draw = T(54, 560, "P6", P6Icon, 1055);

        var plan = HintedDiscardPlanner.PlanHintedDiscard("P6", (int)P6Icon, closed, closed, draw);

        Assert.Equal(HintedDiscardPlanner.Kind.FireCallback7, plan.Kind);
        Assert.Equal(13, plan.Callback7Pos);
        Assert.Equal(54, plan.NodeIndex);
        Assert.NotEqual(closed[0].NodeIndex, plan.NodeIndex);
    }

    [Fact]
    public void Red_at_eligible_13_is_closed_tile_not_type_1022_draw()
    {
        var closed = new List<HintedDiscardPlanner.TileRef>();
        for (var i = 0; i < 13; i++)
            closed.Add(T(59 + i, i * 42, $"M{(i % 9) + 1}", 70000u + (uint)i));
        closed.Add(T(54, 13 * 42, "RED", RedIcon, 1055));
        var draw = T(102, 900, "M1", 70100, 1022);

        var plan = HintedDiscardPlanner.PlanHintedDiscard("RED", (int)RedIcon, closed, closed, draw);

        Assert.Equal(HintedDiscardPlanner.Kind.FireCallback7, plan.Kind);
        Assert.Equal(13, plan.Callback7Pos);
        Assert.Equal(54, plan.NodeIndex);
    }

    [Fact]
    public void Hint_matching_draw_icon_already_in_closed_uses_that_slot()
    {
        var closed = Closed59To71Without("NORTH");
        closed[2] = T(61, 2 * 42, "NORTH", NorthIcon);
        var draw = T(54, 560, "NORTH", NorthIcon, 1055);

        var plan = HintedDiscardPlanner.PlanHintedDiscard("NORTH", (int)NorthIcon, closed, closed, draw);

        Assert.Equal(HintedDiscardPlanner.Kind.FireCallback7, plan.Kind);
        Assert.Equal(2, plan.Callback7Pos);
        Assert.Equal(61, plan.NodeIndex);
    }

    [Fact]
    public void Type_1022_only_draw_is_not_callback_8_or_a_random_closed_tile()
    {
        var closed = Closed59To71Without("NORTH");
        var draw = T(102, 900, "NORTH", NorthIcon, 1022);

        var plan = HintedDiscardPlanner.PlanHintedDiscard("NORTH", (int)NorthIcon, closed, closed, draw);

        Assert.Equal(HintedDiscardPlanner.Kind.MissingHint, plan.Kind);
        Assert.Null(plan.Callback7Pos);
        Assert.Contains("not firing callback 8", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Mislabelled_type_1022_on_node_54_still_maps_as_real_draw()
    {
        var closed = Closed59To71Without("NORTH");
        var draw = T(54, 560, "NORTH", NorthIcon, 1022);

        var plan = HintedDiscardPlanner.PlanHintedDiscard("NORTH", (int)NorthIcon, closed, closed, draw);

        Assert.Equal(HintedDiscardPlanner.Kind.FireCallback7, plan.Kind);
        Assert.Equal(13, plan.Callback7Pos);
        Assert.Equal(54, plan.NodeIndex);
    }

    [Fact]
    public void Stale_m6_hint_not_in_hand_or_draw_fails_instead_of_looping()
    {
        var closed = Closed59To71Without("M6");
        var draw = T(54, 560, "NORTH", NorthIcon, 1055);

        var plan = HintedDiscardPlanner.PlanHintedDiscard("M6", 76016, closed, closed, draw);

        Assert.Equal(HintedDiscardPlanner.Kind.MissingHint, plan.Kind);
        Assert.Null(plan.Callback7Pos);
        Assert.Contains("failing hint", plan.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not looping", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Placeholder_node_58_is_not_a_callback_7_slot()
    {
        var closed = Closed59To71Without("WEST");
        var logged = closed.ToList();
        logged.Insert(0, T(58, -40, "WEST", 76070));

        var plan = HintedDiscardPlanner.PlanHintedDiscard("WEST", 76070, logged, closed, drawn: null);

        Assert.Equal(HintedDiscardPlanner.Kind.MissingHint, plan.Kind);
    }

    [Fact]
    public void After_meld_draw_maps_to_closed_count()
    {
        var closed = new List<HintedDiscardPlanner.TileRef>();
        var codes = new[] { "P1", "P2", "P3", "P4", "P5", "P7", "P8", "P9", "S1", "S2" };
        for (var i = 0; i < codes.Length; i++)
            closed.Add(T(59 + i, i * 42, codes[i], 71000u + (uint)i));
        var draw = T(54, 500, "P6", P6Icon, 1055);

        var plan = HintedDiscardPlanner.PlanHintedDiscard("P6", (int)P6Icon, closed, closed, draw);

        Assert.Equal(HintedDiscardPlanner.Kind.FireCallback7, plan.Kind);
        Assert.Equal(10, plan.Callback7Pos);
        Assert.Equal(54, plan.NodeIndex);
    }

    [Fact]
    public void Aka_m0_hint_matches_closed_m5()
    {
        var closed = Closed59To71Without("M5");
        closed[4] = T(63, 4 * 42, "M5", 76005);
        var plan = HintedDiscardPlanner.PlanHintedDiscard("M0", 76005, closed, closed, drawn: null);

        Assert.Equal(HintedDiscardPlanner.Kind.FireCallback7, plan.Kind);
        Assert.Equal(4, plan.Callback7Pos);
    }

    [Fact]
    public void Unhinted_real_draw_maps_when_allowed()
    {
        var closed = Closed59To71Without("NORTH");
        var draw = T(54, 560, "NORTH", NorthIcon, 1055);

        var plan = HintedDiscardPlanner.PlanHintedDiscard(
            null, null, closed, closed, draw, allowUnhintedDrawn: true);

        Assert.Equal(HintedDiscardPlanner.Kind.FireCallback7, plan.Kind);
        Assert.Equal(13, plan.Callback7Pos);
    }

    [Fact]
    public void Incomplete_snapshot_does_not_fail_a_live_hint()
    {
        var plan = HintedDiscardPlanner.PlanHintedDiscard(
            "NORTH", (int)NorthIcon, [], [], drawn: null);

        Assert.Equal(HintedDiscardPlanner.Kind.NoSafeAction, plan.Kind);
        Assert.Contains("incomplete", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unhinted_draw_without_flag_is_not_a_random_closed_tile()
    {
        var closed = Closed59To71Without("NORTH");
        var draw = T(54, 560, "NORTH", NorthIcon, 1055);

        var plan = HintedDiscardPlanner.PlanHintedDiscard(null, null, closed, closed, draw);

        Assert.Equal(HintedDiscardPlanner.Kind.NoSafeAction, plan.Kind);
        Assert.Null(plan.Callback7Pos);
    }

    [Fact]
    public void Discard_ready_atk_set_is_2_6_30()
    {
        Assert.True(HintedDiscardPlanner.IsDiscardReadyAtk(2));
        Assert.True(HintedDiscardPlanner.IsDiscardReadyAtk(6));
        Assert.True(HintedDiscardPlanner.IsDiscardReadyAtk(30));
        Assert.False(HintedDiscardPlanner.IsDiscardReadyAtk(15));
        Assert.False(HintedDiscardPlanner.IsDiscardReadyAtk(22));
        Assert.True(HintedDiscardPlanner.Callback8IsSkip(6));
        Assert.False(HintedDiscardPlanner.Callback8IsSkip(30));
    }

    private static List<HintedDiscardPlanner.TileRef> Closed59To71Without(string banned)
    {
        var codes = new[]
        {
            "M1", "M2", "M3", "M4", "P1", "P2", "P3", "P4", "S1", "S2", "S3", "EAST", "WHITE"
        };
        var tiles = new List<HintedDiscardPlanner.TileRef>();
        for (var i = 0; i < codes.Length; i++)
        {
            var code = codes[i] == banned ? "GREEN" : codes[i];
            tiles.Add(T(59 + i, i * 42, code, 80000u + (uint)i));
        }
        return tiles;
    }

    private static HintedDiscardPlanner.TileRef T(
        int node, float x, string code, uint icon, ushort type = 1055)
        => new(node, type, icon, code, x);
}
