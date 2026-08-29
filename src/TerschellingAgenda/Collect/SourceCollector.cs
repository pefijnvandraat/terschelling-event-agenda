using System.Diagnostics;
using AngleSharp.Html.Parser;
using TerschellingAgenda.Models;
using TerschellingAgenda.Process;

namespace TerschellingAgenda.Collect;

/// <summary>
/// Haalt één bron op en levert genormaliseerde activiteiten. Een falende bron
/// stopt nooit de totale zoekopdracht — de fout wordt geregistreerd in de SourceOutcome.
/// </summary>
public sealed class SourceCollector
{
    private readonly HttpFetcher _fetcher;
    private readonly ResilientFetcher _resilient;
    private readonly Normalizer _normalizer;
    private readonly PlaceResolver _places;
    private readonly ILogger<SourceCollector> _log;
    private readonly HtmlParser _parser = new();

    public SourceCollector(HttpFetcher fetcher, ResilientFetcher resilient, Normalizer normalizer,
        PlaceResolver places, ILogger<SourceCollector> log)
    {
        _fetcher = fetcher;
        _resilient = resilient;
        _normalizer = normalizer;
        _places = places;
        _log = log;
    }

    public sealed record CollectResult(List<ActivityEvent> Events, SourceOutcome Outcome);

    public async Task<CollectResult> CollectAsync(
        EventSource source, DateOnly from, DateOnly to, CancellationToken ct)
        => await CollectAsync(source, from, to, allowBrowser: true, allowArchive: true, ct);

    public async Task<CollectResult> CollectAsync(
        EventSource source, DateOnly from, DateOnly to,
        bool allowBrowser, bool allowArchive, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var outcome = new SourceOutcome
        {
            SourceId = source.Id,
            SourceName = source.Name,
            Category = source.Category,
            Tier = source.Tier
        };
        var events = new List<ActivityEvent>();
        var now = DateTimeOffset.Now;

        if (!source.Enabled)
        {
            outcome.Status = "overgeslagen";
            outcome.Error = "Bron staat uit in het bronnenregister.";
            outcome.DurationMs = sw.ElapsedMilliseconds;
            return new CollectResult(events, outcome);
        }

        var urls = BuildUrls(source, from, to);
        var raws = new List<(RawEvent Raw, string PageUrl)>();
        var errors = new List<string>();

        // 1) Feed heeft de voorkeur: het meest betrouwbaar en goedkoopst.
        if (!string.IsNullOrWhiteSpace(source.FeedUrl))
        {
            outcome.UrlsTried.Add(source.FeedUrl!);
            var f = await _fetcher.GetAsync(source.FeedUrl!, ct);
            if (f.Success && f.Html is not null)
            {
                try
                {
                    var fromFeed = source.FeedType?.ToLowerInvariant() switch
                    {
                        "ics" => IcsExtractor.Extract(f.Html, source.FeedUrl!),
                        "rss" or "atom" => RssExtractor.Extract(f.Html, source.FeedUrl!),
                        _ => f.Html.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)
                            ? IcsExtractor.Extract(f.Html, source.FeedUrl!)
                            : RssExtractor.Extract(f.Html, source.FeedUrl!)
                    };
                    foreach (var r in fromFeed) raws.Add((r, source.FeedUrl!));
                    if (fromFeed.Count > 0) outcome.Methods.Add(fromFeed[0].Method);
                }
                catch (Exception ex) { errors.Add($"feed: {ex.Message}"); }
            }
            else errors.Add($"feed {source.FeedUrl}: {f.Error}");
        }

        // 2) Agendapagina's — met automatische opschaling bij weigering of leegte.
        DateTimeOffset? archiveStamp = null;
        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            outcome.UrlsTried.Add(url);

            var res = await _resilient.GetAsync(url, allowBrowser, allowArchive, ct);
            outcome.Strategies.Add(res.Strategy.ToString());
            if (res.AttemptLog.Count > 1) outcome.AttemptLog.AddRange(res.AttemptLog);

            if (!res.Success || res.Html is null)
            {
                errors.Add($"{url}: {res.Error}");
                if (res.StatusCode is 403 or 401) outcome.Status = "geblokkeerd";
                if (outcome.HttpStatus == 0) outcome.HttpStatus = res.StatusCode;
                continue;
            }
            outcome.HttpStatus = res.StatusCode;

            if (res.Strategy == FetchStrategy.WebArchive)
            {
                archiveStamp = res.ArchivedAt;
                outcome.FromArchive = true;
                outcome.ArchivedAt = res.ArchivedAt;
            }

            var effectiveUrl = res.EffectiveUrl ?? url;
            var ctype = res.ContentType ?? "";

            if (ctype.Contains("calendar") || res.Html.StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var r in IcsExtractor.Extract(res.Html, effectiveUrl)) raws.Add((r, effectiveUrl));
                outcome.Methods.Add("ics");
                continue;
            }
            if ((ctype.Contains("xml") || ctype.Contains("rss")) && !ctype.Contains("html"))
            {
                foreach (var r in RssExtractor.Extract(res.Html, effectiveUrl)) raws.Add((r, effectiveUrl));
                outcome.Methods.Add("rss");
                continue;
            }
            if (ctype.Contains("json") || (res.Strategy == FetchStrategy.AlternatePath &&
                                           res.Html.TrimStart().StartsWith("[")))
            {
                foreach (var r in WpJsonExtractor.Extract(res.Html, effectiveUrl)) raws.Add((r, effectiveUrl));
                outcome.Methods.Add("json");
                continue;
            }

            var (pageRaws, methods, detailLinks) =
                await ParseHtmlPageAsync(source, res.Html, effectiveUrl, from, ct);
            foreach (var r in pageRaws) raws.Add((r, effectiveUrl));
            outcome.Methods.AddRange(methods);

            // 3) Detailpagina's volgen: daar staan tijd, adres, prijs en contact vaak wél.
            //    Parallel, want één voor één stapelt de beleefdheidspauze per pagina op:
            //    twintig detailpagina's kostten zo bijna een minuut per bron.
            if (detailLinks.Count > 0)
            {
                var detailGate = new SemaphoreSlim(Capacity.DetailPages, Capacity.DetailPages);
                var detailTasks = detailLinks.Select(async link =>
                {
                    await detailGate.WaitAsync(ct);
                    try
                    {
                        var d = await _resilient.GetAsync(link, allowBrowser, allowArchive: false, ct);
                        if (!d.Success || d.Html is null) return (Link: link, Error: d.Error, Raws: (List<RawEvent>?)null, Methods: (List<string>?)null, Strategy: d.Strategy, Url: link);

                        var (detailRaws, dMethods, _) =
                            await ParseHtmlPageAsync(source, d.Html, d.EffectiveUrl ?? link, from, ct, followDetails: false);
                        return (Link: link, Error: (string?)null, Raws: (List<RawEvent>?)detailRaws,
                                Methods: (List<string>?)dMethods, Strategy: d.Strategy, Url: d.EffectiveUrl ?? link);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        return (Link: link, Error: (string?)ex.Message, Raws: (List<RawEvent>?)null,
                                Methods: (List<string>?)null, Strategy: FetchStrategy.Failed, Url: link);
                    }
                    finally { detailGate.Release(); }
                }).ToList();

                foreach (var d in await Task.WhenAll(detailTasks))
                {
                    if (d.Error is not null || d.Raws is null) { errors.Add($"{d.Link}: {d.Error}"); continue; }
                    outcome.Strategies.Add(d.Strategy.ToString());
                    foreach (var r in d.Raws) raws.Add((r, d.Url));
                    if (d.Methods is not null) outcome.Methods.AddRange(d.Methods);
                }
            }
        }

        outcome.RawEventsFound = raws.Count;

        // 4) Normaliseren + filteren
        bool islandSpecific = IsIslandSpecific(source);
        foreach (var (raw, pageUrl) in raws)
        {
            ActivityEvent? ev;
            try { ev = _normalizer.Normalize(raw, source, pageUrl, now, from.Year); }
            catch (Exception ex) { errors.Add($"normalisatie: {ex.Message}"); continue; }
            if (ev is null) continue;

            var haystack = string.Join(" \n ", ev.Name, ev.Description, ev.LocationName, ev.Address,
                ev.Village, raw.RawText, pageUrl);

            if (!_places.IsOnTerschelling(haystack, pageUrl, islandSpecific)) continue;
            if (_places.MentionsOtherPlaceOnly(ev.Name)) continue;
            if (!ev.OverlapsRange(from, to)) continue;

            ev.DiscoveryQuery = $"bron: {source.Name}";

            // Uit een archiefmomentopname afkomstige informatie is per definitie niet
            // gegarandeerd actueel; dat mag nooit als bevestigd worden gepresenteerd.
            if (archiveStamp is not null)
            {
                ev.Confidence = Confidence.Onzeker;
                ev.Conflicts.Add(new FieldConflict
                {
                    Field = "Actualiteit",
                    ChosenValue = $"Momentopname van {archiveStamp.Value:d MMMM yyyy}",
                    ChosenFrom = "Internet Archive",
                    Resolved = false,
                    Reason = "De oorspronkelijke website was niet bereikbaar. Deze gegevens komen uit " +
                             "een gearchiveerde momentopname en kunnen inmiddels gewijzigd zijn. " +
                             "Controleer bij de organisator."
                });
                foreach (var s in ev.Sources) s.Method = "webarchief";
            }

            events.Add(ev);
        }

        outcome.InRangeEvents = events.Count;
        outcome.Methods = outcome.Methods.Distinct().ToList();

        if (errors.Count > 0)
        {
            outcome.Error = string.Join(" | ", errors.Take(4));
            if (events.Count == 0 && outcome.Status == "ok")
                outcome.Status = raws.Count == 0 ? "fout" : "leeg";
        }
        else if (events.Count == 0 && outcome.Status == "ok")
        {
            outcome.Status = "leeg";
        }

        outcome.DurationMs = sw.ElapsedMilliseconds;
        return new CollectResult(events, outcome);
    }

    private async Task<(List<RawEvent> Raws, List<string> Methods, List<string> DetailLinks)> ParseHtmlPageAsync(
        EventSource source, string html, string url, DateOnly from, CancellationToken ct, bool followDetails = true)
    {
        var raws = new List<RawEvent>();
        var methods = new List<string>();
        var detailLinks = new List<string>();

        AngleSharp.Dom.IDocument doc;
        try
        {
            // Een zeer grote pagina levert een ontleed document op van een veelvoud
            // van die omvang. Bij tientallen pagina's tegelijk loopt een kleine
            // container daarop vast; afkappen is beter dan omvallen. Agendagegevens
            // staan vrijwel altijd in het eerste deel van de pagina.
            int cap = Capacity.MaxResponseBytes;
            if (html.Length > cap) html = html[..cap];

            doc = await _parser.ParseDocumentAsync(html, ct);
        }
        catch (Exception ex) { _log.LogDebug("Parsefout {Url}: {Msg}", url, ex.Message); return (raws, methods, detailLinks); }

        var jsonLd = JsonLdExtractor.Extract(doc, url);
        if (jsonLd.Count > 0) { raws.AddRange(jsonLd); methods.Add("jsonld"); }

        var micro = MicrodataExtractor.Extract(doc, url);
        if (micro.Count > 0) { raws.AddRange(micro); methods.Add("microdata"); }

        // HTML-heuristiek altijd draaien: JSON-LD op overzichtspagina's is vaak incompleet.
        var heuristic = HtmlEventExtractor.Extract(doc, url, source.SelectorHint, from.Year);
        if (heuristic.Count > 0) { raws.AddRange(heuristic); methods.Add("html"); }

        if (followDetails && source.MaxDetailPages > 0)
        {
            // Links van de gevonden items zelf zijn het waardevolst: die horen bij een
            // echte activiteit. Generieke links van de pagina zijn een gok en worden
            // alleen gevolgd wanneer de overzichtspagina zelf weinig opleverde.
            var fromItems = raws
                .Where(r => !string.IsNullOrWhiteSpace(r.DetailUrl) &&
                            !r.DetailUrl!.Equals(url, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.DetailUrl!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var generic = fromItems.Count >= 3
                ? new List<string>()
                : HtmlEventExtractor.FindDetailLinks(doc, url, source.MaxDetailPages);

            // Geen extra begrenzing: het bronnenregister bepaalt hoeveel detailpagina's
            // zinvol zijn, en ze worden parallel opgehaald, dus het aantal kost nauwelijks tijd.
            detailLinks = fromItems.Concat(generic)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(l => !l.Equals(url, StringComparison.OrdinalIgnoreCase))
                .Take(source.MaxDetailPages)
                .ToList();
        }

        doc.Dispose();
        return (raws, methods, detailLinks);
    }

    /// <summary>Is deze bron per definitie over Terschelling? Dan hoeft er geen eiland-signaal in de tekst te staan.</summary>
    private static bool IsIslandSpecific(EventSource source)
    {
        var hay = (source.Homepage + " " + string.Join(" ", source.AgendaUrls) + " " + source.Name)
            .ToLowerInvariant();
        return hay.Contains("terschelling") || hay.Contains("oerol") || hay.Contains("skylge")
               || hay.Contains("schylge") || !string.IsNullOrWhiteSpace(source.DefaultVillage);
    }

    private static List<string> BuildUrls(EventSource source, DateOnly from, DateOnly to)
    {
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(source.DateQueryTemplate))
        {
            urls.Add(source.DateQueryTemplate!
                .Replace("{from}", from.ToString("yyyy-MM-dd"))
                .Replace("{to}", to.ToString("yyyy-MM-dd"))
                .Replace("{fromNl}", from.ToString("dd-MM-yyyy"))
                .Replace("{toNl}", to.ToString("dd-MM-yyyy"))
                .Replace("{year}", from.Year.ToString())
                .Replace("{month}", from.Month.ToString("00")));
        }
        urls.AddRange(source.AgendaUrls);
        if (urls.Count == 0 && !string.IsNullOrWhiteSpace(source.Homepage)) urls.Add(source.Homepage);
        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
