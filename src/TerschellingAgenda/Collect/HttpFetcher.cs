using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace TerschellingAgenda.Collect;

public sealed record FetchResult(
    string Url,
    bool Success,
    int StatusCode,
    string? Html,
    string? Error,
    string? FinalUrl,
    string? ContentType,
    long DurationMs);

/// <summary>
/// Beleefde HTTP-client: per-host rate limiting, retries, caching binnen een run,
/// en foutafhandeling die de totale zoekopdracht nooit stopzet.
/// </summary>
public sealed class HttpFetcher : IDisposable
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostLocks = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastHit = new();
    private readonly ConcurrentDictionary<string, FetchResult> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<HttpFetcher> _log;

    /// <summary>
    /// Beleefdheidspauze tussen twee verzoeken aan dezelfde website. Kort genoeg om
    /// tientallen detailpagina's binnen enkele seconden te lezen, ruim genoeg om
    /// geen enkele site te belasten (hooguit een paar verzoeken per seconde).
    /// </summary>
    private const int PerHostDelayMs = 400;
    private const int SearchHostDelayMs = 2600;
    private static readonly int MaxBytes = Capacity.MaxResponseBytes;

    /// <summary>
    /// Wachttijd per poging. Een agendapagina die na acht seconden nog niets heeft
    /// gestuurd, is in de praktijk onbereikbaar; langer wachten levert geen gegevens op
    /// maar kost wel minuten bij een site die plat ligt.
    /// </summary>
    private const int RequestTimeoutSeconds = 8;
    /// <summary>Zoekmachines mogen wat langer doen; die zijn traag maar wel bereikbaar.</summary>
    private const int SearchTimeoutSeconds = 15;

    /// <summary>Zoekmachines krijgen een strengere vertraging en één verzoek tegelijk.</summary>
    private static readonly string[] SearchHosts =
    {
        "search.brave.com", "html.duckduckgo.com", "lite.duckduckgo.com",
        "www.startpage.com", "www.mojeek.com", "search.marginalia.nu", "www.bing.com"
    };

    private static bool IsSearchHost(string host) =>
        SearchHosts.Contains(host, StringComparer.OrdinalIgnoreCase);

    public HttpFetcher(ILogger<HttpFetcher> log)
    {
        _log = log;
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 6
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/126.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-NL,nl;q=0.9,en;q=0.6");
        _http.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,text/calendar;q=0.8,*/*;q=0.7");
    }

    public void ClearCache() => _cache.Clear();

    public async Task<FetchResult> GetAsync(string url, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(url, out var cached)) return cached;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Uri uri;
        try { uri = new Uri(url); }
        catch (Exception ex) { return Store(url, new FetchResult(url, false, 0, null, "Ongeldige URL: " + ex.Message, null, null, 0)); }

        if (uri.Scheme is not ("http" or "https"))
            return Store(url, new FetchResult(url, false, 0, null, "Niet-ondersteund schema", null, null, 0));

        bool searchHost = IsSearchHost(uri.Host);
        var gate = _hostLocks.GetOrAdd(uri.Host, _ => new SemaphoreSlim(searchHost ? 1 : 3, searchHost ? 1 : 3));
        await gate.WaitAsync(ct);
        try
        {
            // beleefde vertraging per host
            if (_lastHit.TryGetValue(uri.Host, out var last))
            {
                int minGap = searchHost ? SearchHostDelayMs : PerHostDelayMs;
                var wait = minGap - (int)(DateTimeOffset.UtcNow - last).TotalMilliseconds;
                if (wait > 0) await Task.Delay(wait, ct);
            }
            _lastHit[uri.Host] = DateTimeOffset.UtcNow;

            int timeoutSeconds = searchHost ? SearchTimeoutSeconds : RequestTimeoutSeconds;
            Exception? lastEx = null;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
                    int code = (int)resp.StatusCode;

                    // Alleen "kom straks terug"-antwoorden verdienen een nieuwe poging.
                    if (code is 429 or 503 && attempt < 2)
                    {
                        await Task.Delay(1200 * (attempt + 1), ct);
                        continue;
                    }

                    var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
                    if (!resp.IsSuccessStatusCode)
                        return Store(url, new FetchResult(url, false, code, null,
                            $"HTTP {code} {resp.ReasonPhrase}", resp.RequestMessage?.RequestUri?.ToString(), ctype, sw.ElapsedMilliseconds));

                    // De koptekst is binnen; geef het uitlezen van de inhoud een eigen budget.
                    attemptCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                    var body = await ReadLimitedAsync(resp, attemptCts.Token);
                    return Store(url, new FetchResult(url, true, code, body, null,
                        resp.RequestMessage?.RequestUri?.ToString() ?? url, ctype, sw.ElapsedMilliseconds));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // de hele zoekopdracht is afgebroken
                }
                catch (OperationCanceledException)
                {
                    // Onze eigen tijdslimiet. Een host die hier niet binnen antwoordt, doet dat
                    // bij herhaling zelden alsnog — nog twee keer wachten kost alleen tijd.
                    return Store(url, new FetchResult(url, false, 0, null,
                        $"Time-out na {timeoutSeconds}s", null, null, sw.ElapsedMilliseconds));
                }
                catch (HttpRequestException ex)
                {
                    lastEx = ex;
                    // Eén herkansing voor een kortstondige netwerkhapering.
                    if (attempt < 1) { await Task.Delay(500, ct); continue; }
                    break;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    break;
                }
            }
            _log.LogDebug("Fetch mislukt voor {Url}: {Err}", url, lastEx?.Message);
            return Store(url, new FetchResult(url, false, 0, null, lastEx?.Message ?? "Onbekende fout", null, null, sw.ElapsedMilliseconds));
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int read, total = 0;
        while ((read = await stream.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, read);
            total += read;
            if (total >= MaxBytes) break;
        }
        var bytes = ms.ToArray();

        var charset = resp.Content.Headers.ContentType?.CharSet?.Trim('"');
        Encoding enc = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { enc = Encoding.GetEncoding(charset); } catch { /* val terug op UTF-8 */ }
        }
        var text = enc.GetString(bytes);

        // meta charset detectie wanneer de header ontbreekt
        if (string.IsNullOrWhiteSpace(charset) && text.Contains("charset=", StringComparison.OrdinalIgnoreCase))
        {
            var m = System.Text.RegularExpressions.Regex.Match(text[..Math.Min(2000, text.Length)],
                @"charset\s*=\s*[""']?(?<c>[\w\-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success && !m.Groups["c"].Value.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            {
                try { text = Encoding.GetEncoding(m.Groups["c"].Value).GetString(bytes); } catch { }
            }
        }
        return text;
    }

    private FetchResult Store(string url, FetchResult r)
    {
        _cache[url] = r;
        return r;
    }

    public void Dispose() => _http.Dispose();
}
