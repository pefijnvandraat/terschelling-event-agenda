using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using AngleSharp.Html.Parser;
using TerschellingAgenda.Models;
using TerschellingAgenda.Process;

namespace TerschellingAgenda.Collect;

public sealed record SearchHit(string Url, string Title, string Snippet, string Query, string Engine);

/// <summary>
/// Brede zoekmachine-gedreven ontdekking. Combineert elke Terschellinger plaatsnaam met
/// activiteit-gerelateerde zoektermen, zodat ook activiteiten worden gevonden die alleen
/// een dorpsnaam noemen en niet op een centrale evenementenkalender staan.
///
/// Gebruikt meerdere zoekmachines met rotatie en terugvalvolgorde. Machines die gaan
/// weigeren (rate limiting) worden tijdelijk overgeslagen; de zoekopdracht gaat door
/// met de overige machines.
/// </summary>
public sealed class SearchDiscovery
{
    private readonly HttpFetcher _fetcher;
    private readonly ILogger<SearchDiscovery> _log;
    private readonly HtmlParser _parser = new();
    private readonly Dictionary<string, DateTimeOffset> _cooldown = new();
    private readonly object _cooldownLock = new();

    public SearchDiscovery(HttpFetcher fetcher, ILogger<SearchDiscovery> log)
    {
        _fetcher = fetcher;
        _log = log;
    }

    private static readonly string[] EngineOrder = { "brave", "duckduckgo", "startpage", "mojeek", "marginalia" };

    /// <summary>Zoektermen die met plaatsnamen worden gecombineerd.</summary>
    public static readonly string[] ActivityTerms =
    {
        "agenda", "activiteiten", "evenementen", "wat te doen", "programma",
        "optreden", "concert", "live muziek", "festival", "markt", "braderie",
        "expositie", "tentoonstelling", "workshop", "excursie", "rondleiding",
        "wandeling", "wadlopen", "tocht", "sport", "wedstrijd", "toernooi",
        "natuur", "cultuur", "muziek", "theater", "voorstelling", "museum",
        "kinderen", "jeugd", "familie", "horeca", "feest", "dorpsfeest",
        "lezing", "film", "kunst", "dansen", "kermis", "uitagenda"
    };

    /// <summary>Eiland-brede zoekopdrachten.</summary>
    public static readonly string[] IslandQueries =
    {
        "evenementen Terschelling",
        "activiteiten Terschelling",
        "agenda Terschelling",
        "uitgaan Terschelling",
        "wat te doen Terschelling",
        "evenementenkalender Terschelling",
        "live muziek Terschelling",
        "excursies Terschelling",
        "kinderactiviteiten Terschelling",
        "exposities Terschelling",
        "concerten Terschelling",
        "markt braderie Terschelling",
        "theater voorstelling Terschelling",
        "wadlopen excursie Terschelling",
        "dorpshuis activiteiten Terschelling",
        "sportevenementen Terschelling"
    };

    /// <summary>Bouwt de volledige zoekopdrachtenlijst voor een periode.</summary>
    public static List<string> BuildQueries(GeoRegistry geo, DateOnly from, DateOnly to, int placeTermsPerPlace)
    {
        var nl = System.Globalization.CultureInfo.GetCultureInfo("nl-NL");
        var queries = new List<string>();

        var monthTokens = new List<string>();
        for (var d = new DateOnly(from.Year, from.Month, 1); d <= to; d = d.AddMonths(1))
        {
            monthTokens.Add($"{d.ToString("MMMM", nl)} {d.Year}");
            if (monthTokens.Count >= 4) break;
        }

        foreach (var q in IslandQueries)
        {
            queries.Add(q);
            foreach (var m in monthTokens.Take(2)) queries.Add($"{q} {m}");
        }

        var places = geo.Places
            .Where(p => p.UseAsSearchTerm && p.Type is "dorp" or "buurtschap" or "gehucht" or "venue" or "landmark")
            .OrderBy(p => p.Type switch { "dorp" => 0, "buurtschap" => 1, "gehucht" => 2, "venue" => 3, _ => 4 })
            .ThenBy(p => p.Name)
            .ToList();

        foreach (var p in places)
        {
            // Ambigue of zeer korte namen krijgen altijd "Terschelling" erbij om ruis te voorkomen.
            string baseName = p.Ambiguous || p.Name.Length <= 5 ? $"{p.Name} Terschelling" : p.Name;

            int n = p.Type switch
            {
                "dorp" => placeTermsPerPlace,
                "buurtschap" or "gehucht" => Math.Max(3, placeTermsPerPlace / 2),
                _ => 2
            };

            foreach (var term in ActivityTerms.Take(n))
                queries.Add($"{baseName} {term}");

            if (p.Type == "dorp" && monthTokens.Count > 0)
                queries.Add($"{baseName} agenda {monthTokens[0]}");
        }

        return queries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Voert één zoekopdracht uit. <paramref name="rotation"/> bepaalt met welke zoekmachine
    /// wordt begonnen, zodat de belasting over machines wordt verdeeld.
    /// </summary>
    public async Task<List<SearchHit>> SearchAsync(string query, int rotation, CancellationToken ct)
    {
        var order = Enumerable.Range(0, EngineOrder.Length)
            .Select(i => EngineOrder[((rotation % EngineOrder.Length) + i) % EngineOrder.Length])
            .ToList();

        foreach (var engine in order)
        {
            if (IsCoolingDown(engine)) continue;
            try
            {
                var hits = await RunEngineAsync(engine, query, ct);
                if (hits.Count > 0) return hits;
                MarkEmpty(engine);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogDebug("Zoekmachine {Engine} faalde voor \"{Q}\": {Msg}", engine, query, ex.Message);
                MarkEmpty(engine);
            }
        }
        return new List<SearchHit>();
    }

    private bool IsCoolingDown(string engine)
    {
        lock (_cooldownLock)
            return _cooldown.TryGetValue(engine, out var until) && until > DateTimeOffset.UtcNow;
    }

    /// <summary>Een lege of geweigerde respons betekent doorgaans rate limiting: kort afkoelen.</summary>
    private void MarkEmpty(string engine)
    {
        lock (_cooldownLock)
            _cooldown[engine] = DateTimeOffset.UtcNow.AddSeconds(40);
    }

    public IReadOnlyDictionary<string, DateTimeOffset> CooldownSnapshot()
    {
        lock (_cooldownLock) return new Dictionary<string, DateTimeOffset>(_cooldown);
    }

    public void ResetCooldowns()
    {
        lock (_cooldownLock) _cooldown.Clear();
    }

    private async Task<List<SearchHit>> RunEngineAsync(string engine, string query, CancellationToken ct)
    {
        var q = Uri.EscapeDataString(query);
        var url = engine switch
        {
            "brave" => $"https://search.brave.com/search?q={q}&source=web",
            "duckduckgo" => $"https://html.duckduckgo.com/html/?kl=nl-nl&q={q}",
            "startpage" => $"https://www.startpage.com/sp/search?query={q}&language=nederlands",
            "mojeek" => $"https://www.mojeek.com/search?q={q}",
            "marginalia" => $"https://search.marginalia.nu/search?query={q}",
            _ => throw new ArgumentOutOfRangeException(nameof(engine))
        };

        var res = await _fetcher.GetAsync(url, ct);
        if (!res.Success || string.IsNullOrWhiteSpace(res.Html)) return new List<SearchHit>();

        using var doc = await _parser.ParseDocumentAsync(res.Html, ct);
        return engine switch
        {
            "brave" => ParseBrave(doc, query),
            "duckduckgo" => ParseDuckDuckGo(doc, query),
            "startpage" => ParseGeneric(doc, query, "startpage",
                "a.result-link, a.w-gl__result-title, .w-gl__result a[href^='http'], .result a[href^='http']"),
            "mojeek" => ParseGeneric(doc, query, "mojeek",
                "ul.results-standard li a.title, .results a[href^='http']"),
            "marginalia" => ParseGeneric(doc, query, "marginalia",
                ".card a[href^='http'], .search-result a[href^='http'], main a[href^='http']"),
            _ => new List<SearchHit>()
        };
    }

    private static List<SearchHit> ParseBrave(AngleSharp.Dom.IDocument doc, string query)
    {
        var hits = new List<SearchHit>();
        foreach (var snip in doc.QuerySelectorAll("div.snippet[data-type='web'], div.snippet[data-pos]"))
        {
            var a = snip.QuerySelector("a[href^='http']");
            var href = a?.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            var title = TextUtil.Collapse(snip.QuerySelector(".title, .snippet-title")?.TextContent);
            if (title.Length == 0) title = TextUtil.Collapse(a!.TextContent);
            var desc = TextUtil.Collapse(snip.QuerySelector(".snippet-description, .snippet-content")?.TextContent);
            hits.Add(new SearchHit(href, title, desc, query, "brave"));
        }
        return hits;
    }

    private static List<SearchHit> ParseDuckDuckGo(AngleSharp.Dom.IDocument doc, string query)
    {
        var hits = new List<SearchHit>();
        foreach (var result in doc.QuerySelectorAll("div.result, div.web-result"))
        {
            var a = result.QuerySelector("a.result__a");
            var href = UnwrapDdg(a?.GetAttribute("href"));
            if (href is null) continue;
            hits.Add(new SearchHit(href, TextUtil.Collapse(a?.TextContent),
                TextUtil.Collapse(result.QuerySelector(".result__snippet")?.TextContent), query, "duckduckgo"));
        }
        return hits;
    }

    private static List<SearchHit> ParseGeneric(AngleSharp.Dom.IDocument doc, string query, string engine, string selectors)
    {
        var hits = new List<SearchHit>();
        foreach (var a in doc.QuerySelectorAll(selectors))
        {
            var href = a.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(href, UriKind.Absolute, out var u)) continue;
            if (u.Host.Contains(engine, StringComparison.OrdinalIgnoreCase)) continue;
            hits.Add(new SearchHit(u.ToString(), TextUtil.Collapse(a.TextContent), "", query, engine));
        }
        return hits;
    }

    /// <summary>DuckDuckGo verpakt resultaten in een redirect; die pakken we uit.</summary>
    private static string? UnwrapDdg(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        href = WebUtility.HtmlDecode(href);
        if (href.StartsWith("//")) href = "https:" + href;

        var m = Regex.Match(href, @"[?&]uddg=(?<u>[^&]+)");
        if (m.Success)
        {
            try { href = HttpUtility.UrlDecode(m.Groups["u"].Value); }
            catch { return null; }
        }
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        if (uri.Host.Contains("duckduckgo.com")) return null;
        return uri.ToString();
    }

    private static readonly string[] BlockedHosts =
    {
        "facebook.com","instagram.com","tiktok.com","pinterest.","youtube.com","youtu.be","twitter.com","x.com",
        "linkedin.com","booking.com","tripadvisor.","airbnb.","expedia.","zoover.","google.","gstatic",
        "wikipedia.org","wikimedia","marktplaats.nl","funda.nl","werkenbij","amazon.","bol.com",
        "brave.com","duckduckgo.com","startpage.com","mojeek.com","marginalia.nu","bing.com","ecosia.org",
        "spotify.com","apple.com","adobe.com","wordpress.org","w3.org","schema.org",
        "hema.nl","calendar.google","iagenda.com","uitagendarotterdam"
    };

    /// <summary>Filtert zoekresultaten tot plausibele agendapagina's.</summary>
    public static List<SearchHit> FilterHits(IEnumerable<SearchHit> hits)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keep = new List<SearchHit>();
        foreach (var h in hits)
        {
            if (!Uri.TryCreate(h.Url, UriKind.Absolute, out var u)) continue;
            if (u.Scheme is not ("http" or "https")) continue;
            var host = u.Host.ToLowerInvariant();
            if (BlockedHosts.Any(b => host.Contains(b))) continue;
            if (Regex.IsMatch(u.AbsolutePath, @"\.(jpg|jpeg|png|gif|svg|pdf|zip|mp4|webp|css|js|ico)$",
                    RegexOptions.IgnoreCase)) continue;

            var norm = u.GetLeftPart(UriPartial.Path).TrimEnd('/');
            if (!seen.Add(norm)) continue;
            keep.Add(h with { Url = u.ToString() });
        }
        return keep;
    }

    /// <summary>Rangschikt gevonden pagina's: eilandspecifieke domeinen en agenda-achtige paden eerst.</summary>
    public static int RelevanceScore(SearchHit hit)
    {
        if (!Uri.TryCreate(hit.Url, UriKind.Absolute, out var u)) return 0;
        var host = u.Host.ToLowerInvariant();
        var path = u.AbsolutePath.ToLowerInvariant();
        var text = (hit.Title + " " + hit.Snippet).ToLowerInvariant();

        int score = 0;
        if (host.Contains("terschelling") || host.Contains("skylge") || host.Contains("oerol")) score += 40;
        if (text.Contains("terschelling")) score += 12;
        if (Regex.IsMatch(path, @"(agenda|event|evenement|activiteit|programma|kalender|uitagenda|voorstelling|concert|expositie)"))
            score += 25;
        if (Regex.IsMatch(host, @"(vvv|wadden|uitagenda|agenda)")) score += 10;
        if (host.EndsWith(".nl")) score += 6;
        if (path.Length <= 1) score -= 5;
        return score;
    }

    /// <summary>Maakt een ad-hoc bron van een zoekresultaat, zodat de gewone collector-pijplijn werkt.</summary>
    public static EventSource ToAdHocSource(SearchHit hit)
    {
        var host = Uri.TryCreate(hit.Url, UriKind.Absolute, out var u) ? u.Host : "onbekend";
        return new EventSource
        {
            Id = "discovery:" + TextUtil.Slug(host).Replace(' ', '-') + ":" +
                 Math.Abs(StringComparer.Ordinal.GetHashCode(hit.Url)).ToString("x"),
            Name = string.IsNullOrWhiteSpace(hit.Title) ? host : TextUtil.Truncate(hit.Title, 90),
            Homepage = u is not null ? $"{u.Scheme}://{u.Host}" : hit.Url,
            AgendaUrls = { hit.Url },
            Category = "gevonden via zoekmachine",
            Tier = GuessTier(host),
            Rendering = "server",
            Discovered = true,
            MaxDetailPages = 8,
            Notes = $"Ontdekt via zoekopdracht: \"{hit.Query}\" ({hit.Engine})"
        };
    }

    private static SourceTier GuessTier(string host)
    {
        if (host.Contains("terschelling.nl") || host.Contains("gemeente")) return SourceTier.OfficialLocal;
        if (host.Contains("vvv") || host.Contains("wadden.nl") || host.Contains("friesland.nl")) return SourceTier.TouristCalendar;
        if (host.Contains("uitagenda") || host.Contains("allevents") || host.Contains("eventbrite") ||
            host.Contains("langetermijnagenda") || host.Contains("dagjeweg") || host.Contains("festivalinfo") ||
            host.Contains("evenementen")) return SourceTier.Aggregator;
        // Een site op een eigen domein die zelf over Terschelling gaat, is vaak de organisator zelf.
        if (host.Contains("terschelling") || host.Contains("skylge") || host.Contains("oerol")) return SourceTier.PrimaryOrganizer;
        return SourceTier.Aggregator;
    }
}
