using System.Text.Json;
using System.Text.Json.Serialization;

namespace MahjongHelper.Mahjong;

/// <summary>
/// Shared snake_case JSON options matching <see cref="MahjongServerClient"/> POST bodies.
/// </summary>
public static class SolverJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
