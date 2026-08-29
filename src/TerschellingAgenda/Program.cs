using System.Text.Json;
using System.Text.Json.Serialization;
using TerschellingAgenda.Api;
using TerschellingAgenda.Collect;
using TerschellingAgenda.Models;
using TerschellingAgenda.Process;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddSingleton<HttpFetcher>();
builder.Services.AddSingleton<BrowserFetcher>();
builder.Services.AddSingleton<HostHealthStore>();
builder.Services.AddSingleton<ResilientFetcher>();
builder.Services.AddSingleton<RegistryStore>();
builder.Services.AddSingleton<ResultStore>();
builder.Services.AddSingleton<PlaceResolver>(sp => new PlaceResolver(sp.GetRequiredService<RegistryStore>().Geo));
builder.Services.AddSingleton<Normalizer>();
builder.Services.AddSingleton<SourceCollector>();
builder.Services.AddSingleton<SearchDiscovery>();
builder.Services.AddSingleton<AggregationService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // De interface is een lokaal hulpmiddel dat regelmatig verandert. Zonder deze regel
    // houdt de browser een oude pagina vast en lijkt een verbetering niet te werken.
    // "no-cache" betekent niet "niet bewaren", maar "altijd even navragen": bij een
    // ongewijzigd bestand antwoordt de server met 304 en wordt niets opnieuw verstuurd.
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate";
    }
});

// ---------------------------------------------------------------- API

app.MapGet("/api/geo", (RegistryStore r) => Results.Ok(r.Geo));

app.MapGet("/api/sources", (RegistryStore r) => Results.Ok(new
{
    compiledAt = r.Sources.CompiledAt,
    total = r.Sources.Sources.Count,
    byCategory = r.Sources.Sources.GroupBy(s => s.Category).ToDictionary(g => g.Key, g => g.Count()),
    sources = r.Sources.Sources.OrderBy(s => (int)s.Tier).ThenBy(s => s.Name)
}));

app.MapGet("/api/categories", () => Results.Ok(Categorizer.AllCategories));

app.MapGet("/api/status", (AggregationService agg, ResultStore store) => Results.Ok(new
{
    running = agg.IsRunning,
    progress = agg.Progress,
    detail = agg.Tracker.Snapshot(),
    lastRun = store.LastReport?.SearchedAt,
    lastRunId = store.LastReport?.RunId,
    cachedEvents = store.LastEvents.Count,
    cachedFrom = store.LastReport?.From,
    cachedTo = store.LastReport?.To
}));

app.MapGet("/api/runs", (ResultStore store) => Results.Ok(store.History.Select(h => new
{
    h.RunId, h.SearchedAt, h.From, h.To, h.UniqueEvents, h.DuplicatesMerged,
    h.SourcesTotal, h.SourcesOk, h.SourcesFailed, h.DurationMs
})));

app.MapGet("/api/report", (ResultStore store) =>
    store.LastReport is null ? Results.NoContent() : Results.Ok(store.LastReport));

// Live zoeken op het openbare internet.
app.MapPost("/api/search", async (SearchQuery q, AggregationService agg, CancellationToken ct) =>
{
    var (from, to, err) = ParseRange(q.From, q.To);
    if (err is not null) return Results.BadRequest(new { error = err });

    var resp = await agg.RunAsync(new SearchRequest
    {
        From = from,
        To = to,
        DeepSearch = q.DeepSearch ?? true,
        MaxQueries = q.MaxQueries ?? 90,
        MaxDiscoveredPages = q.MaxPages ?? 120,
        UseBrowser = q.UseBrowser ?? true,
        UseArchive = q.UseArchive ?? true
    }, ct);

    return Results.Ok(new { report = resp.Report, events = Filter(resp.Events, q, from, to) });
});

// Filteren binnen het laatst opgehaalde resultaat (zonder opnieuw het internet op te gaan).
app.MapGet("/api/events", (
    string? from, string? to, string? village, string? category, string? price,
    string? q, string? confidence, string? reservation, string? sort, ResultStore store) =>
{
    var (f, t, err) = ParseRange(from, to);
    if (err is not null) return Results.BadRequest(new { error = err });

    var query = new SearchQuery
    {
        From = from, To = to, Village = village, Category = category,
        Price = price, Q = q, Confidence = confidence, Reservation = reservation, Sort = sort
    };

    return Results.Ok(new { report = store.LastReport, events = Filter(store.LastEvents, query, f, t) });
});

app.MapGet("/api/facets", (ResultStore store) => Results.Ok(new
{
    villages = store.LastEvents.Select(e => e.Village)
        .Where(v => !ActivityEvent.IsUnknown(v)).Distinct().OrderBy(v => v).ToList(),
    categories = store.LastEvents.SelectMany(e => e.Categories).Distinct()
        .OrderBy(c => Array.IndexOf(Categorizer.AllCategories, c)).ToList(),
    organizers = store.LastEvents.Select(e => e.Organizer)
        .Where(v => !ActivityEvent.IsUnknown(v)).Distinct().OrderBy(v => v).Take(200).ToList()
}));

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.Now }));

// Diagnose: toont wat één bron oplevert vóór deduplicatie. Bedoeld voor kwaliteitscontrole.
app.MapGet("/api/debug/source", async (
    string id, string? from, string? to, bool? browser,
    RegistryStore reg, SourceCollector collector, CancellationToken ct) =>
{
    var src = reg.Sources.Sources.FirstOrDefault(s =>
        s.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
        s.Name.Contains(id, StringComparison.OrdinalIgnoreCase));
    if (src is null) return Results.NotFound(new { error = $"Bron '{id}' niet gevonden." });

    var (f, t, err) = ParseRange(from, to);
    if (err is not null) return Results.BadRequest(new { error = err });

    var r = await collector.CollectAsync(src, f, t, browser ?? true, false, ct);
    return Results.Ok(new
    {
        source = new { src.Id, src.Name, src.Tier, src.AgendaUrls },
        outcome = r.Outcome,
        events = r.Events.Select(e => new
        {
            e.Name, e.Date, e.StartTime, e.EndTime, e.Village, e.LocationName,
            e.Organizer, e.Price, e.Confidence, e.PrimarySourceUrl, e.Categories
        })
    });
});

app.Run();


// ---------------------------------------------------------------- helpers

static (DateOnly From, DateOnly To, string? Error) ParseRange(string? from, string? to)
{
    var today = DateOnly.FromDateTime(DateTime.Now);
    DateOnly f = today, t = today.AddDays(13);

    if (!string.IsNullOrWhiteSpace(from))
    {
        if (!DateOnly.TryParse(from, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out f))
            return (today, today, $"Ongeldige startdatum: '{from}'. Gebruik JJJJ-MM-DD.");
        t = f;
    }
    if (!string.IsNullOrWhiteSpace(to))
    {
        if (!DateOnly.TryParse(to, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out t))
            return (today, today, $"Ongeldige einddatum: '{to}'. Gebruik JJJJ-MM-DD.");
    }
    if (t < f) (f, t) = (t, f);
    if ((t.DayNumber - f.DayNumber) > 400) return (f, t, "Periode is te lang (maximaal 400 dagen).");
    return (f, t, null);
}

static List<ActivityEvent> Filter(IEnumerable<ActivityEvent> src, SearchQuery q, DateOnly from, DateOnly to)
{
    var list = src.Where(e => e.OverlapsRange(from, to));

    if (!string.IsNullOrWhiteSpace(q.Village) && q.Village != "*")
    {
        var wanted = q.Village.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        list = list.Where(e => wanted.Any(w => e.Village.Equals(w, StringComparison.OrdinalIgnoreCase)));
    }

    if (!string.IsNullOrWhiteSpace(q.Category) && q.Category != "*")
    {
        var wanted = q.Category.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        list = list.Where(e => e.Categories.Any(c => wanted.Contains(c, StringComparer.OrdinalIgnoreCase)));
    }

    if (!string.IsNullOrWhiteSpace(q.Price) && q.Price != "*")
    {
        list = q.Price.ToLowerInvariant() switch
        {
            "gratis" => list.Where(e => e.PriceKind == PriceKind.Gratis),
            "betaald" => list.Where(e => e.PriceKind == PriceKind.Betaald),
            "onbekend" => list.Where(e => e.PriceKind == PriceKind.Onbekend),
            _ => list
        };
    }

    if (!string.IsNullOrWhiteSpace(q.Confidence) && q.Confidence != "*")
    {
        var wanted = q.Confidence.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        list = list.Where(e => wanted.Contains(e.Confidence.ToString(), StringComparer.OrdinalIgnoreCase));
    }

    if (!string.IsNullOrWhiteSpace(q.Reservation) && q.Reservation != "*")
        list = list.Where(e => e.ReservationRequired.ToString().Equals(q.Reservation, StringComparison.OrdinalIgnoreCase));

    if (!string.IsNullOrWhiteSpace(q.Q))
    {
        var needles = TextUtil.Slug(q.Q).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        list = list.Where(e =>
        {
            var hay = TextUtil.Slug(string.Join(" ", e.Name, e.Description, e.Village, e.LocationName,
                e.Address, e.Organizer, string.Join(" ", e.Categories)));
            return needles.All(n => hay.Contains(n));
        });
    }

    var result = list.ToList();

    return (q.Sort?.ToLowerInvariant()) switch
    {
        "naam" => result.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
        "dorp" => result.OrderBy(e => e.Village, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(e => e.Date).ThenBy(e => e.StartTime ?? TimeOnly.MaxValue).ToList(),
        "betrouwbaarheid" => result.OrderByDescending(e => (int)e.Confidence)
                        .ThenBy(e => e.Date).ThenBy(e => e.StartTime ?? TimeOnly.MaxValue).ToList(),
        _ => result.OrderBy(e => e.Date)
                   .ThenBy(e => e.StartTime ?? TimeOnly.MaxValue)
                   .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
    };
}

public sealed class SearchQuery
{
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Village { get; set; }
    public string? Category { get; set; }
    public string? Price { get; set; }
    public string? Q { get; set; }
    public string? Confidence { get; set; }
    public string? Reservation { get; set; }
    public string? Sort { get; set; }
    public bool? DeepSearch { get; set; }
    public int? MaxQueries { get; set; }
    public int? MaxPages { get; set; }
    public bool? UseBrowser { get; set; }
    public bool? UseArchive { get; set; }
}
