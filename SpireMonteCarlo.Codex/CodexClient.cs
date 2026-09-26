using System.Net;

namespace SpireMonteCarlo.Codex;

/// <summary>
/// Minimal Spire Codex HTTP client. Only used when refreshing the cache, never while advising.
/// Rate limits are per endpoint (15/min without a key, 60/min with one), so requests are spaced out
/// and a 429 is retried once after the server's Retry-After.
/// </summary>
public sealed class CodexClient : IDisposable
{
    public const string DefaultBaseUrl = "https://spire-codex.com/api";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _minInterval;
    private DateTime _lastRequestUtc = DateTime.MinValue;

    public CodexClient(string? apiKey = null, HttpClient? http = null, string baseUrl = DefaultBaseUrl, TimeSpan? minInterval = null)
    {
        _ownsHttp = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        _minInterval = minInterval ?? TimeSpan.FromSeconds(1.5);
    }

    /// <param name="path">Relative to the API base, e.g. "runs/metrics/cards?bracket=wr50".</param>
    public async Task<byte[]> GetBytesAsync(string path, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            TimeSpan wait = _minInterval - (DateTime.UtcNow - _lastRequestUtc);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastRequestUtc = DateTime.UtcNow;

            using HttpResponseMessage response = await _http.GetAsync(path, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
            {
                TimeSpan retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                await Task.Delay(retryAfter > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2) : retryAfter, ct);
                continue;
            }
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
