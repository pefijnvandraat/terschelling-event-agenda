using System.Diagnostics;
using System.Text.Json;
using TerschellingAgenda.Collect;
using TerschellingAgenda.Models;
using TerschellingAgenda.Process;

namespace TerschellingAgenda.Api;

public sealed class SearchRequest
{
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    /// <summary>Ook zoekmachine-ontdekking uitvoeren (trager, maar completer).</summary>
    public bool DeepSearch { get; set; } = true;
    /// <summary>Maximaal aantal zoekopdrachten (0 = geen limiet).</summary>
    public int MaxQueries { get; set; } = 90;
    public int MaxDiscoveredPages { get; set; } = 120;
    /// <summary>Echte browser inzetten voor JavaScript-pagina's en sites die eenvoudige clients weigeren.</summary>
    public bool UseBrowser { get; set; } = true;
    /// <summary>Als laatste redmiddel een gearchiveerde momentopname gebruiken (gemarkeerd als mogelijk verouderd).</summary>
    public bool UseArchive { get; set; } = true;
}

public sealed class SearchResponse
{
    public RunReport Report { get; set; } = new();
    public List<ActivityEvent> Events { get; set; } = new();
}

/// <summary>Orkestreert de volledige zoek-, normalisatie-, dedup- en validatiepijplijn.</summary>
public sealed class AggregationService
{
    private readonly HttpFetcher _fetcher;
    private readonly ResilientFetcher _resilient;
    private readonly SourceCollector _collector;
    private readonly SearchDiscovery _discovery;
    private readonly PlaceResolver _places;
    private readonly RegistryStore _registries;
    private readonly ResultStore _results;
    private readonly HostHealthStore _health;
    private readonly ILogger<AggregationService> _log;

    private readonly SemaphoreSlim _runGate = new(1, 1);

    public AggregationService(
        HttpFetcher fetcher, ResilientFetcher resilient, SourceCollector collector,
        SearchDiscovery discovery, PlaceResolver places, RegistryStore registries,
        ResultStore results, HostHealthStore health, ILogger<AggregationService> log)
    {
        _fetcher = fetcher;
        _resilient = resilient;
        _collector = collector;
        _discovery = discovery;
        _places = places;
        _registries = registries;
        _results = results;
        _health = health;
        _log = log;
    }

    public bool IsRunning { get; private set; }
    public string Progress { get; private set; } = "";

    /// <summary>Gedetailleerde voortgang per stap, voor de voortgangsbalk.</summary>
    public ProgressTracker Tracker { get; } = new();

    public async Task<SearchResponse> RunAsync(SearchRequest req, CancellationToken ct)
    {
        await _runGate.WaitAsync(ct);
        IsRunning = true;
        var sw = Stopwatch.StartNew();
        _resilient.ResetRunState();
        Tracker.Begin(req.DeepSearch);

        var report = new RunReport
        {
            SearchedAt = DateTimeOffset.Now,
            From = req.From,
            To = req.To
        };

        var geo = _registries.Geo;
        var sources = _registries.Sources.Sources.Where(s => s.Enabled).ToList();

        report.PlacesIncluded = geo.Places
            .Where(p => p.Type is "dorp" or "buurtschap" or "gehucht")
            .Select(p => p.Name).OrderBy(n => n).ToList();

        var allEvents = new List<ActivityEvent>();

        try
        {
            // ---------- Fase 1: geregistreerde bronnen ----------
            Progress = $"Fase 1/3 — {sources.Count} geregistreerde bronnen raadplegen…";
            Tracker.Start(ProgressTracker.Sources, sources.Count);
            var throttler = new SemaphoreSlim(12, 12);
            var tasks = sources.Select(async src =>
            {
                await throttler.WaitAsync(ct);
                try { return await _collector.CollectAsync(src, req.From, req.To, req.UseBrowser, req.UseArchive, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    return new SourceCollector.CollectResult(new List<ActivityEvent>(), new SourceOutcome
                    {
                        SourceId = src.Id, SourceName = src.Name, Category = src.Category, Tier = src.Tier,
                        Status = "fout", Error = ex.Message
                    });
                }
                finally
                {
                    throttler.Release();
                    Tracker.Advance(ProgressTracker.Sources);
                }
            }).ToList();

            foreach (var result in await Task.WhenAll(tasks))
            {
                report.SourceOutcomes.Add(result.Outcome);
                allEvents.AddRange(result.Events);
            }
            Tracker.Complete(ProgressTracker.Sources);

            // ---------- Fase 2: zoekmachine-ontdekking ----------
            if (req.DeepSearch)
            {
                var queries = SearchDiscovery.BuildQueries(geo, req.From, req.To, placeTermsPerPlace: 6);
                if (req.MaxQueries > 0 && queries.Count > req.MaxQueries)
                    queries = queries.Take(req.MaxQueries).ToList();
                report.SearchQueriesUsed = queries;

                Progress = $"Fase 2/3 — {queries.Count} zoekopdrachten uitvoeren…";
                Tracker.Start(ProgressTracker.Search, queries.Count);
                _discovery.ResetCooldowns();
                var hits = new List<SearchHit>();
                var searchGate = new SemaphoreSlim(4, 4);
                int done = 0;

                var searchTasks = queries.Select(async (q, idx) =>
                {
                    await searchGate.WaitAsync(ct);
                    try { return await _discovery.SearchAsync(q, idx, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        lock (report) report.Warnings.Add($"Zoekopdracht mislukt: \"{q}\" ({ex.Message})");
                        return new List<SearchHit>();
                    }
                    finally
                    {
                        searchGate.Release();
                        int n = Interlocked.Increment(ref done);
                        Tracker.Advance(ProgressTracker.Search);
                        if (n % 5 == 0) Progress = $"Fase 2/3 — zoekopdracht {n}/{queries.Count}…";
                    }
                }).ToList();

                foreach (var r in await Task.WhenAll(searchTasks)) hits.AddRange(r);
                Tracker.Complete(ProgressTracker.Search);

                var filtered = SearchDiscovery.FilterHits(hits);

                if (filtered.Count == 0)
                    report.Warnings.Add("Geen enkele zoekmachine gaf resultaten terug. " +
                                        "Mogelijk is de verbinding geblokkeerd of wordt er te snel gezocht. " +
                                        "De geregistreerde bronnen zijn wel geraadpleegd.");

                // Meest relevante pagina's eerst, en hooguit een paar per domein: tien
                // pagina's van dezelfde website leveren zelden tien verschillende agenda's op.
                var ordered = filtered
                    .OrderByDescending(SearchDiscovery.RelevanceScore)
                    .GroupBy(h => Uri.TryCreate(h.Url, UriKind.Absolute, out var hu)
                        ? hu.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase)
                        : h.Url, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(g => g.Take(2))
                    .OrderByDescending(SearchDiscovery.RelevanceScore)
                    .Take(req.MaxDiscoveredPages)
                    .ToList();

                report.Warnings.Add($"Zoekmachines leverden {hits.Count} resultaten op, " +
                                    $"waarvan {filtered.Count} unieke bruikbare pagina's; " +
                                    $"{ordered.Count} daarvan zijn uitgelezen.");

                Progress = $"Fase 3/3 — {ordered.Count} gevonden pagina's uitlezen…";
                Tracker.Start(ProgressTracker.Pages, ordered.Count);
                var pageGate = new SemaphoreSlim(12, 12);
                var pageTasks = ordered.Select(async hit =>
                {
                    await pageGate.WaitAsync(ct);
                    try
                    {
                        var adhoc = SearchDiscovery.ToAdHocSource(hit);
                        // Ontdekte pagina's: browser alleen als de bron hem echt nodig heeft,
                        // en geen archief — dat is voorbehouden aan geregistreerde bronnen.
                        var r = await _collector.CollectAsync(adhoc, req.From, req.To,
                            allowBrowser: req.UseBrowser, allowArchive: false, ct);
                        foreach (var e in r.Events) e.DiscoveryQuery = hit.Query;
                        return r;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        return new SourceCollector.CollectResult(new List<ActivityEvent>(), new SourceOutcome
                        {
                            SourceId = "discovery", SourceName = hit.Url, Category = "gevonden via zoekmachine",
                            Status = "fout", Error = ex.Message
                        });
                    }
                    finally { pageGate.Release(); Tracker.Advance(ProgressTracker.Pages); }
                }).ToList();

                foreach (var r in await Task.WhenAll(pageTasks))
                {
                    if (r.Outcome.Status != "ok" || r.Events.Count > 0)
                        report.SourceOutcomes.Add(r.Outcome);
                    allEvents.AddRange(r.Events);
                }
                Tracker.Complete(ProgressTracker.Pages);
            }
            else
            {
                Tracker.Skip(ProgressTracker.Search, "Zoekmachine-ontdekking stond uit.");
                Tracker.Skip(ProgressTracker.Pages, "Zoekmachine-ontdekking stond uit.");
                report.Warnings.Add("Zoekmachine-ontdekking stond uit: alleen geregistreerde bronnen geraadpleegd.");
            }

            // ---------- Verwerking ----------
            Progress = "Dedupliceren en valideren…";
            Tracker.Start(ProgressTracker.Merge, 0);
            report.RawEventsCollected = allEvents.Count;

            var dedup = new Deduplicator().Deduplicate(allEvents);
            foreach (var e in dedup.Unique) Validator.Apply(e);

            var final = dedup.Unique.Where(e => e.OverlapsRange(req.From, req.To)).ToList();

            report.UniqueEvents = final.Count;
            report.DuplicatesMerged = dedup.Merged;
            report.EventsConfirmed = final.Count(e => e.Confidence == Confidence.Bevestigd);
            report.EventsUncertain = final.Count(e => e.Confidence == Confidence.Onzeker);
            report.EventsUnknownData = final.Count(e => e.Confidence == Confidence.Onbekend);
            report.UnverifiedFields = Validator.SummariseUnverified(final);

            report.SourcesTotal = report.SourceOutcomes.Count;
            report.SourcesOk = report.SourceOutcomes.Count(o => o.Status is "ok" or "leeg");
            report.SourcesFailed = report.SourceOutcomes.Count(o => o.Status is "fout" or "geblokkeerd");
            report.UnreachableSources = report.SourceOutcomes
                .Where(o => o.Status is "fout" or "geblokkeerd")
                .Select(o => $"{o.SourceName} — {o.Error ?? o.Status}")
                .Distinct().ToList();

            report.SourceTypesInvestigated = report.SourceOutcomes
                .Select(o => o.Category).Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct().OrderBy(c => c).ToList();

            // Transparantie over de terugvalladder.
            report.BrowserAvailable = _resilient.BrowserAvailable;
            report.FetchStrategies = _resilient.Statistics.Snapshot();
            report.SourcesNeedingBrowser = report.SourceOutcomes
                .Where(o => o.Strategies.Contains("Browser"))
                .Select(o => o.SourceName).Distinct().ToList();
            report.SourcesFromArchive = report.SourceOutcomes
                .Where(o => o.FromArchive)
                .Select(o => $"{o.SourceName} (momentopname {o.ArchivedAt:d MMMM yyyy})")
                .Distinct().ToList();

            if (report.SourcesFromArchive.Count > 0)
                report.Warnings.Add(
                    $"{report.SourcesFromArchive.Count} bron(nen) waren niet bereikbaar; " +
                    "daarvoor is een gearchiveerde momentopname gebruikt. Die gegevens zijn " +
                    "gemarkeerd als 'Onzeker' omdat ze mogelijk verouderd zijn.");

            report.SkippedHosts = _health.SkippedThisRun.OrderBy(h => h).ToList();
            if (report.SkippedHosts.Count > 0)
                report.Warnings.Add(
                    $"{report.SkippedHosts.Count} website(s) zijn overgeslagen omdat ze bij een " +
                    "eerdere poging helemaal niet reageerden: " + string.Join(", ", report.SkippedHosts) +
                    ". Over enkele uren worden ze vanzelf opnieuw geprobeerd.");

            await _health.SaveAsync(ct);

            if (!report.BrowserAvailable)
                report.Warnings.Add(
                    "Er is geen browser op deze machine gevonden. JavaScript-only agenda's " +
                    "konden daardoor niet worden uitgelezen.");

            report.DurationMs = sw.ElapsedMilliseconds;

            await _results.SaveAsync(report, final, ct);
            Progress = "Klaar.";
            Tracker.Complete(ProgressTracker.Merge);
            Tracker.Finish();
            return new SearchResponse { Report = report, Events = final };
        }
        finally
        {
            IsRunning = false;
            Tracker.Abort();   // doet niets wanneer de run normaal is afgerond
            _runGate.Release();
        }
    }
}

/// <summary>Laadt en bewaart de geografische lijst en het bronnenregister.</summary>
public sealed class RegistryStore
{
    private readonly string _dir;
    private readonly ILogger<RegistryStore> _log;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public GeoRegistry Geo { get; private set; } = new();
    public SourceRegistry Sources { get; private set; } = new();

    public RegistryStore(IWebHostEnvironment env, ILogger<RegistryStore> log)
    {
        _log = log;
        _dir = Path.Combine(env.ContentRootPath, "Data");
        Reload();
    }

    public void Reload()
    {
        Geo = Load<GeoRegistry>("geo-registry.json") ?? new GeoRegistry();
        Sources = Load<SourceRegistry>("source-registry.json") ?? new SourceRegistry();
        _log.LogInformation("Registers geladen: {Places} plaatsen, {Sources} bronnen",
            Geo.Places.Count, Sources.Sources.Count);
    }

    private T? Load<T>(string file) where T : class
    {
        var path = Path.Combine(_dir, file);
        if (!File.Exists(path)) { _log.LogWarning("Bestand ontbreekt: {Path}", path); return null; }
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json); }
        catch (Exception ex) { _log.LogError(ex, "Kon {File} niet lezen", file); return null; }
    }
}

/// <summary>Bewaart het laatste resultaat en de runhistorie op schijf.</summary>
public sealed class ResultStore
{
    private readonly string _dir;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public RunReport? LastReport { get; private set; }
    public List<ActivityEvent> LastEvents { get; private set; } = new();
    public List<RunReport> History { get; private set; } = new();

    public ResultStore(IWebHostEnvironment env)
    {
        _dir = Path.Combine(env.ContentRootPath, "..", "..", "data");
        Directory.CreateDirectory(_dir);
        TryLoad();
    }

    private void TryLoad()
    {
        try
        {
            var ep = Path.Combine(_dir, "last-events.json");
            var rp = Path.Combine(_dir, "last-report.json");
            var hp = Path.Combine(_dir, "runs.json");
            if (File.Exists(ep)) LastEvents = JsonSerializer.Deserialize<List<ActivityEvent>>(File.ReadAllText(ep), Json) ?? new();
            if (File.Exists(rp)) LastReport = JsonSerializer.Deserialize<RunReport>(File.ReadAllText(rp), Json);
            if (File.Exists(hp)) History = JsonSerializer.Deserialize<List<RunReport>>(File.ReadAllText(hp), Json) ?? new();
        }
        catch { /* corrupte cache mag de app niet blokkeren */ }
    }

    public async Task SaveAsync(RunReport report, List<ActivityEvent> events, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            LastReport = report;
            LastEvents = events;
            History.Insert(0, report);
            if (History.Count > 40) History = History.Take(40).ToList();

            await File.WriteAllTextAsync(Path.Combine(_dir, "last-events.json"),
                JsonSerializer.Serialize(events, Json), ct);
            await File.WriteAllTextAsync(Path.Combine(_dir, "last-report.json"),
                JsonSerializer.Serialize(report, Json), ct);
            await File.WriteAllTextAsync(Path.Combine(_dir, "runs.json"),
                JsonSerializer.Serialize(History, Json), ct);
        }
        finally { _lock.Release(); }
    }
}
