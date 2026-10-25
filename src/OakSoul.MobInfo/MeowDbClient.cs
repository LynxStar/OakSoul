using System.Net;

namespace OakSoul.MobInfo;

/// <summary>Raised on any HTTP outcome other than 200 or 404; the run stops instead of retrying.</summary>
internal sealed class FetchStoppedException(string message) : Exception(message);

/// <summary>
/// Polite, cached fetcher for <c>https://meowdb.com/msclassic/monsters/{id}</c>. Pages are cached as
/// <c>{cacheDir}/{id}.html</c> (and <c>{id}.404</c> for ids the site does not know). A network request is only
/// made for an uncached id (or with refresh), at most once per id per run, at least <see cref="Delay"/> apart.
/// </summary>
internal sealed class MeowDbClient : IDisposable
{
    public const string BaseUrl = "https://meowdb.com/msclassic/monsters/";
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(500);

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly bool _refresh;
    private readonly HashSet<int> _fetchedThisRun = [];
    private DateTime _lastRequestUtc = DateTime.MinValue;

    public MeowDbClient(string cacheDir, bool refresh)
    {
        _cacheDir = cacheDir;
        _refresh = refresh;
        Directory.CreateDirectory(cacheDir);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "OakSoul.MobInfo/1.0 (personal non-commercial local research tool; sequential requests, 500ms delay, responses cached)");
    }

    public int NetworkRequests => _fetchedThisRun.Count;

    public enum Outcome { Cached, Fetched, NotFound, NotFoundCached }

    /// <summary>Returns the page html, or null when the site has no such monster.</summary>
    public async Task<(string? Html, Outcome Outcome)> GetAsync(int id, CancellationToken ct)
    {
        string htmlPath = Path.Combine(_cacheDir, id + ".html");
        string missPath = Path.Combine(_cacheDir, id + ".404");
        if (!_refresh || _fetchedThisRun.Contains(id))
        {
            if (File.Exists(htmlPath))
                return (await File.ReadAllTextAsync(htmlPath, ct), Outcome.Cached);
            if (File.Exists(missPath))
                return (null, Outcome.NotFoundCached);
        }
        if (!_fetchedThisRun.Add(id))
            throw new InvalidOperationException($"Refusing to fetch id {id} twice in one run.");

        var wait = _lastRequestUtc + Delay - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, ct);

        string url = BaseUrl + id;
        HttpResponseMessage response;
        try
        {
            _lastRequestUtc = DateTime.UtcNow;
            response = await _http.GetAsync(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new FetchStoppedException($"Request for {url} failed ({ex.Message}). Stopping; rerun later, cached pages are kept.");
        }
        _lastRequestUtc = DateTime.UtcNow;

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                File.Delete(htmlPath);
                await File.WriteAllTextAsync(missPath, "404 " + DateTime.UtcNow.ToString("O"), ct);
                return (null, Outcome.NotFound);
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                string retry = response.Headers.RetryAfter?.ToString() is { } r ? $" Retry-After: {r}." : "";
                throw new FetchStoppedException(
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} for {url}.{retry} Stopping so the site is not hammered; cached pages are kept, rerun later.");
            }
            string html = await response.Content.ReadAsStringAsync(ct);
            File.Delete(missPath);
            await File.WriteAllTextAsync(htmlPath, html, ct);
            return (html, Outcome.Fetched);
        }
    }

    public void Dispose() => _http.Dispose();
}
