using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerschellingAgenda.Collect;

/// <summary>Hoe een pagina uiteindelijk is opgehaald — voor transparantie in het rapport.</summary>
public enum FetchStrategy
{
    Direct,
    HostVariant,
    AlternatePath,
    Browser,
    WebArchive,
    Failed
}

public sealed record ResilientResult(
    string RequestedUrl,
    string? EffectiveUrl,
    bool Success,
    int StatusCode,
    string? Html,
    string? ContentType,
    string? Error,
    FetchStrategy Strategy,
    /// <summary>Alleen gevuld bij WebArchive: het moment waarop de momentopname is gemaakt.</summary>
    DateTimeOffset? ArchivedAt,
    List<string> AttemptLog);

/// <summary>
/// Haalt een pagina op en schakelt bij weigering of leegte automatisch op naar een
/// zwaardere strategie. De volgorde is bewust van goedkoop naar duur:
///
///   1. Direct        — gewoon HTTP-verzoek.
///   2. HostVariant   — www ↔ hoofddomein; sommige servers weigeren één van beide.
///   3. AlternatePath — feeds en machineleesbare varianten (ICS, RSS, WordPress REST).
///   4. Browser       — echte headless browser: lost SPA's en JS-controles op.
///   5. WebArchive    — publieke momentopname uit het Internet Archive.
///                      Uitsluitend als laatste redmiddel én altijd gemarkeerd als
///                      mogelijk verouderd, zodat actualiteit nooit wordt gesuggereerd.
///
/// Er wordt geen enkele beveiliging omzeild: geen proxyrotatie, geen CAPTCHA-omzeiling,
/// geen IP-verhulling. Weigert een site ook een echte browser, dan blijft dat een
/// geregistreerde, gerapporteerde weigering.
/// </summary>
public sealed class ResilientFetcher
{
    private readonly HttpFetcher _http;
    private readonly BrowserFetcher _browser;
    private readonly HostHealthStore _health;
    private readonly ILogger<ResilientFetcher> _log;

    private readonly HashSet<string> _hostsNeedingBrowser = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hostsUnreachable = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public ResilientFetcher(HttpFetcher http, BrowserFetcher browser, HostHealthStore health,
        ILogger<ResilientFetcher> log)
    {
        _http = http;
        _browser = browser;
        _health = health;
        _log = log;
    }

    /// <summary>Per run bijgehouden statistiek over gebruikte strategieën.</summary>
    public sealed class Stats
    {
        private readonly Dictionary<FetchStrategy, int> _counts = new();
        private readonly object _l = new();

        public void Record(FetchStrategy s)
        {
            lock (_l) _counts[s] = _counts.GetValueOrDefault(s) + 1;
        }
        public Dictionary<string, int> Snapshot()
        {
            lock (_l) return _counts.ToDictionary(k => k.Key.ToString(), v => v.Value);
        }
        public void Reset() { lock (_l) _counts.Clear(); }
    }

    public Stats Statistics { get; } = new();

    public void ResetRunState()
    {
        lock (_lock)
        {
            _hostsNeedingBrowser.Clear();
            _hostsUnreachable.Clear();
        }
        Statistics.Reset();
        _http.ClearCache();
        _health.ResetRunState();
    }

    public bool BrowserAvailable => _browser.IsSupported;

    public async Task<ResilientResult> GetAsync(
        string url, bool allowBrowser, bool allowArchive, CancellationToken ct)
    {
        var attempts = new List<string>();
        Uri.TryCreate(url, UriKind.Absolute, out var u0);
        var host = u0?.Host ?? "";

        // ---------- 0. Host waarvan al vaststaat dat hij niet antwoordt ----------
        // Zonder deze afslag betaalt elke pagina van een platliggende site opnieuw
        // de volledige ladder: acht URL's plus een browserpoging.
        if (host.Length > 0)
        {
            bool downThisRun;
            lock (_lock) downThisRun = _hostsUnreachable.Contains(host);
            if (downThisRun || _health.ShouldSkip(host))
            {
                _health.NoteSkipped(host);
                attempts.Add($"overgeslagen — {host} gaf eerder geen enkel antwoord");
                Statistics.Record(FetchStrategy.Failed);
                return new ResilientResult(url, null, false, 0, null, null,
                    $"{host} was niet bereikbaar; overgeslagen om tijd te besparen.",
                    FetchStrategy.Failed, null, attempts);
            }
        }

        // Host waarvan we in deze run al weten dat alleen een browser werkt: sla het
        // nutteloze HTTP-verzoek over.
        bool skipDirect;
        lock (_lock) skipDirect = host.Length > 0 && _hostsNeedingBrowser.Contains(host);

        bool hostGaveNoAnswer = false;
        FetchResult? direct = null;
        if (!skipDirect)
        {
            // ---------- 1. Direct ----------
            direct = await _http.GetAsync(url, ct);
            attempts.Add($"direct → {Describe(direct)}");
            if (IsUsable(direct))
            {
                _health.RecordReachable(host);
                Statistics.Record(FetchStrategy.Direct);
                return Ok(url, direct, FetchStrategy.Direct, attempts);
            }

            // ---------- 2. Hostvariant (www ↔ hoofddomein) ----------
            // Bij een 404 heeft de server juist wél geantwoord: de pagina bestaat niet.
            // De www-variant is dezelfde server, en feeds van een niet-bestaand pad
            // bestaan evenmin. Doorgaan levert acht verzoeken op voor niets.
            bool pageDoesNotExist = direct.StatusCode is 404 or 410;
            FetchResult? variantResult = null;

            if (!pageDoesNotExist)
            {
                var variant = HostVariant(url);
                if (variant is not null)
                {
                    variantResult = await _http.GetAsync(variant, ct);
                    attempts.Add($"hostvariant {variant} → {Describe(variantResult)}");
                    if (IsUsable(variantResult))
                    {
                        _health.RecordReachable(host);
                        Statistics.Record(FetchStrategy.HostVariant);
                        return Ok(variant, variantResult, FetchStrategy.HostVariant, attempts);
                    }
                }
            }

            // Statuscode 0 betekent: geen enkel HTTP-antwoord, dus geen server aan de lijn.
            // De feedvarianten op diezelfde host zijn dan zinloos — maar een echte browser
            // krijgt soms wél verbinding waar een kale client wordt genegeerd, dus die
            // ene poging blijft staan.
            bool noAnswerAtAll = direct.StatusCode == 0 &&
                                 (variantResult is null || variantResult.StatusCode == 0);

            if (noAnswerAtAll)
            {
                hostGaveNoAnswer = true;
                attempts.Add("feedvarianten overgeslagen — de host geeft geen enkel antwoord");
            }
            else if (pageDoesNotExist)
            {
                // De server leeft prima; alleen deze pagina bestaat niet.
                _health.RecordReachable(host);
                attempts.Add("feedvarianten overgeslagen — de pagina bestaat niet (404)");
                Statistics.Record(FetchStrategy.Failed);
                return new ResilientResult(url, null, false, direct.StatusCode, null, null,
                    direct.Error, FetchStrategy.Failed, null, attempts);
            }
            else
            {
                // De server leeft, ook al is déze pagina onbruikbaar.
                _health.RecordReachable(host);

                // ---------- 3. Machineleesbare varianten ----------
                foreach (var candidate in AlternatePaths(url))
                {
                    var alt = await _http.GetAsync(candidate, ct);
                    attempts.Add($"altpad {candidate} → {Describe(alt)}");
                    if (IsUsable(alt) && LooksLikeData(alt))
                    {
                        Statistics.Record(FetchStrategy.AlternatePath);
                        return Ok(candidate, alt, FetchStrategy.AlternatePath, attempts);
                    }
                }
            }
        }
        else
        {
            attempts.Add("direct overgeslagen (host vereist eerder al een browser)");
        }

        // ---------- 4. Echte browser ----------
        if (allowBrowser && _browser.IsSupported)
        {
            // Gaf de host op gewone verzoeken al niets terug, dan is dit een laatste kans
            // met een korte grens in plaats van een geduldige poging.
            var html = await _browser.GetRenderedHtmlAsync(url, settleMs: hostGaveNoAnswer ? 600 : 2200, ct,
                loadTimeoutSeconds: hostGaveNoAnswer ? 10 : 25);
            attempts.Add($"browser → {(html is null ? "mislukt" : $"{html.Length} tekens")}");
            if (!string.IsNullOrWhiteSpace(html) && html.Length > 800)
            {
                if (host.Length > 0)
                {
                    lock (_lock) _hostsNeedingBrowser.Add(host);
                    _health.RecordReachable(host);
                }

                Statistics.Record(FetchStrategy.Browser);
                return new ResilientResult(url, url, true, 200, html, "text/html", null,
                    FetchStrategy.Browser, null, attempts);
            }
        }
        else if (allowBrowser)
        {
            attempts.Add("browser niet beschikbaar op deze machine");
        }

        // ---------- 5. Publiek webarchief ----------
        if (allowArchive)
        {
            var archived = await TryWebArchiveAsync(url, ct);
            if (archived is not null)
            {
                attempts.Add($"webarchief {archived.Value.Timestamp:yyyy-MM-dd} → {archived.Value.Html.Length} tekens");
                Statistics.Record(FetchStrategy.WebArchive);
                return new ResilientResult(url, archived.Value.Url, true, 200, archived.Value.Html,
                    "text/html", null, FetchStrategy.WebArchive, archived.Value.Timestamp, attempts);
            }
            attempts.Add("webarchief → geen bruikbare momentopname");
        }

        // Alles is geprobeerd en de host heeft geen enkele keer geantwoord: onthoud dat,
        // zodat de rest van deze run — en de eerstvolgende uren — er niet meer op wacht.
        if (hostGaveNoAnswer && host.Length > 0)
        {
            lock (_lock) _hostsUnreachable.Add(host);
            _health.RecordUnreachable(host, direct?.Error);
        }

        Statistics.Record(FetchStrategy.Failed);
        var err = direct?.Error ?? "Niet bereikbaar via alle beschikbare strategieën.";
        return new ResilientResult(url, null, false, direct?.StatusCode ?? 0, null, null, err,
            FetchStrategy.Failed, null, attempts);
    }

    private static ResilientResult Ok(string url, FetchResult r, FetchStrategy s, List<string> attempts) =>
        new(url, r.FinalUrl ?? url, true, r.StatusCode, r.Html, r.ContentType, null, s, null, attempts);

    private static string Describe(FetchResult r) =>
        r.Success ? $"HTTP {r.StatusCode} ({r.Html?.Length ?? 0} tekens)" : (r.Error ?? "fout");

    private static bool IsUsable(FetchResult r) =>
        r.Success && !string.IsNullOrWhiteSpace(r.Html) && r.Html.Length > 400;

    private static bool LooksLikeData(FetchResult r)
    {
        var h = r.Html ?? "";
        var ctype = r.ContentType ?? "";
        return ctype.Contains("json") || ctype.Contains("xml") || ctype.Contains("calendar") ||
               h.StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase) ||
               h.TrimStart().StartsWith("[") || h.TrimStart().StartsWith("{") ||
               h.Contains("<rss", StringComparison.OrdinalIgnoreCase) ||
               h.Contains("<feed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>www.example.nl ↔ example.nl — sommige servers accepteren maar één vorm.</summary>
    private static string? HostVariant(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var b = new UriBuilder(u);
        if (u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            b.Host = u.Host[4..];
        else if (u.Host.Count(c => c == '.') == 1)
            b.Host = "www." + u.Host;
        else return null;
        return b.Uri.ToString();
    }

    /// <summary>Machineleesbare varianten van dezelfde agenda.</summary>
    private static IEnumerable<string> AlternatePaths(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) yield break;
        var root = $"{u.Scheme}://{u.Host}";
        var path = u.AbsolutePath.TrimEnd('/');

        // The Events Calendar (WordPress) en varianten
        yield return $"{root}{path}/?ical=1";
        yield return $"{root}{path}/feed/";
        // WordPress REST API voor evenementen
        yield return $"{root}/wp-json/tribe/events/v1/events?per_page=50";
        yield return $"{root}/wp-json/wp/v2/tribe_events?per_page=50";
        // algemene feeds op domeinniveau
        yield return $"{root}/feed/";
        yield return $"{root}/events.ics";
    }

    /// <summary>
    /// Laatste redmiddel: publieke momentopname uit het Internet Archive.
    /// Alleen als de opname niet stokoud is, en altijd gemarkeerd als mogelijk verouderd.
    /// </summary>
    private async Task<(string Html, string Url, DateTimeOffset Timestamp)?> TryWebArchiveAsync(
        string url, CancellationToken ct)
    {
        try
        {
            var api = "https://archive.org/wayback/available?url=" + Uri.EscapeDataString(url);
            var probe = await _http.GetAsync(api, ct);
            if (!probe.Success || string.IsNullOrWhiteSpace(probe.Html)) return null;

            using var doc = JsonDocument.Parse(probe.Html);
            if (!doc.RootElement.TryGetProperty("archived_snapshots", out var snaps)) return null;
            if (!snaps.TryGetProperty("closest", out var closest)) return null;
            if (!closest.TryGetProperty("available", out var av) || !av.GetBoolean()) return null;

            var snapUrl = closest.GetProperty("url").GetString();
            var stamp = closest.GetProperty("timestamp").GetString();
            if (string.IsNullOrWhiteSpace(snapUrl) || string.IsNullOrWhiteSpace(stamp)) return null;

            if (!DateTimeOffset.TryParseExact(stamp, "yyyyMMddHHmmss", null,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var when)) return null;

            // Een momentopname ouder dan 60 dagen zegt niets zinnigs meer over een actuele agenda.
            if ((DateTimeOffset.UtcNow - when).TotalDays > 60) return null;

            if (snapUrl.StartsWith("http://")) snapUrl = "https://" + snapUrl[7..];
            var page = await _http.GetAsync(snapUrl, ct);
            if (!page.Success || string.IsNullOrWhiteSpace(page.Html)) return null;

            return (StripArchiveChrome(page.Html), snapUrl, when);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug("Webarchief mislukt voor {Url}: {Msg}", url, ex.Message);
            return null;
        }
    }

    /// <summary>Verwijdert de navigatiebalk die het Internet Archive in de pagina injecteert.</summary>
    private static string StripArchiveChrome(string html)
    {
        html = Regex.Replace(html, @"<!--\s*BEGIN WAYBACK TOOLBAR INSERT\s*-->.*?<!--\s*END WAYBACK TOOLBAR INSERT\s*-->",
            " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        // Archief-prefixen uit links halen zodat detailpagina's naar de echte site wijzen.
        html = Regex.Replace(html, @"https?://web\.archive\.org/web/\d+(?:id_)?/", "", RegexOptions.IgnoreCase);
        return html;
    }
}
