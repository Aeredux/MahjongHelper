namespace MahjongHelper.Mahjong;

public static partial class GameStateMapper
{
    public static SuggestMoveRequest? BuildSuggestMoveRequest(MahjongGameState state, MahjongIconMap iconMap)
        => BuildSuggestMoveRequest(state, iconMap.Resolve);

    public static EvaluateCallRequest? BuildEvaluateCallRequest(
        MahjongGameState state, MahjongIconMap iconMap, string? callTile, string? callType)
        => BuildEvaluateCallRequest(state, iconMap.Resolve, callTile, callType);
}
