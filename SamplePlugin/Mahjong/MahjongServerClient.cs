using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SamplePlugin.Mahjong;

/// <summary>
/// HTTP client for communicating with the local Mahjong solver server.
/// All methods are async and return null on failure (never throw).
/// </summary>
public sealed class MahjongServerClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions;

    private DateTime _lastHealthCheck;
    private bool? _lastHealthy;
    private string? _lastServerVersion;
    private string? _lastError;

    public bool? IsHealthy => _lastHealthy;
    public string? ServerVersion => _lastServerVersion;
    public string? LastError => _lastError;
    public string BaseUrl { get; }

    public MahjongServerClient(string baseUrl = "http://localhost:8080")
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(5),
        };
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
    }

    /// <summary>
    /// Checks server connectivity. Updates IsHealthy and ServerVersion.
    /// </summary>
    public async Task<bool> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/api/health", ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var health = JsonSerializer.Deserialize<HealthResponse>(json, _jsonOptions);
                _lastHealthy = true;
                _lastServerVersion = health?.Version;
                _lastError = null;
                _lastHealthCheck = DateTime.UtcNow;
                return true;
            }

            _lastHealthy = false;
            _lastError = $"Health check returned {(int)response.StatusCode}";
        }
        catch (Exception ex)
        {
            _lastHealthy = false;
            _lastError = ex is TaskCanceledException ? "Timeout" : ex.Message;
        }

        _lastHealthCheck = DateTime.UtcNow;
        return false;
    }

    /// <summary>
    /// Requests discard suggestions for the current hand.
    /// </summary>
    public async Task<SuggestMoveResponse?> SuggestMoveAsync(SuggestMoveRequest request, CancellationToken ct = default)
    {
        return await PostAsync<SuggestMoveRequest, SuggestMoveResponse>("/api/suggest-move", request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluates whether a call (chi/pon/kan/ron/tsumo/riichi) should be accepted.
    /// </summary>
    public async Task<EvaluateCallResponse?> EvaluateCallAsync(EvaluateCallRequest request, CancellationToken ct = default)
    {
        return await PostAsync<EvaluateCallRequest, EvaluateCallResponse>("/api/evaluate-call", request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates whether a specific discard is legal.
    /// </summary>
    public async Task<ValidateMoveResponse?> ValidateMoveAsync(ValidateMoveRequest request, CancellationToken ct = default)
    {
        return await PostAsync<ValidateMoveRequest, ValidateMoveResponse>("/api/validate-move", request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the time since last health check, or null if never checked.
    /// </summary>
    public TimeSpan? TimeSinceLastHealthCheck()
        => _lastHealthCheck == default ? null : DateTime.UtcNow - _lastHealthCheck;

    /// <summary>
    /// Returns a compact status string for UI display.
    /// </summary>
    public string GetStatusText()
    {
        if (_lastHealthy == null)
            return "Not checked";

        if (_lastHealthy == true)
        {
            var version = _lastServerVersion != null ? $" v{_lastServerVersion}" : "";
            return $"Connected{version}";
        }

        return $"Disconnected: {_lastError ?? "unknown error"}";
    }

    private async Task<TResponse?> PostAsync<TRequest, TResponse>(string endpoint, TRequest request, CancellationToken ct)
        where TResponse : class
    {
        try
        {
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(endpoint, content, ct).ConfigureAwait(false);

            var responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _lastError = $"{endpoint} returned {(int)response.StatusCode}: {Truncate(responseJson, 200)}";
                return null;
            }

            _lastError = null;
            return JsonSerializer.Deserialize<TResponse>(responseJson, _jsonOptions);
        }
        catch (Exception ex)
        {
            _lastError = ex is TaskCanceledException ? $"{endpoint} timeout" : $"{endpoint}: {ex.Message}";
            return null;
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";

    public void Dispose()
    {
        _http.Dispose();
    }
}
