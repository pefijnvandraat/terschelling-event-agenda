namespace TerschellingAgenda.Models;

/// <summary>Een openbare bron die activiteiten publiceert.</summary>
public sealed class EventSource
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Homepage { get; set; } = "";
    /// <summary>Deep link(s) naar de agenda/evenementenpagina.</summary>
    public List<string> AgendaUrls { get; set; } = new();
    /// <summary>officieel | toeristisch | evenementenkalender | organisator | cultuur | museum | theater |
    /// horeca | natuur | excursie | sport | vereniging | ticketplatform | aggregator | nieuws | sociaal | overig</summary>
    public string Category { get; set; } = "overig";
    public SourceTier Tier { get; set; } = SourceTier.Aggregator;

    // technische hints
    public bool HasJsonLd { get; set; }
    public string? FeedUrl { get; set; }
    /// <summary>ics | rss | atom | json</summary>
    public string? FeedType { get; set; }
    /// <summary>server | spa</summary>
    public string Rendering { get; set; } = "server";
    public bool Blocked { get; set; }
    public string? SelectorHint { get; set; }
    /// <summary>URL-sjabloon met {from} en {to} (yyyy-MM-dd) voor datumfiltering.</summary>
    public string? DateQueryTemplate { get; set; }
    public string Notes { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Ad-hoc bron die uit een zoekresultaat is afgeleid. De naam is dan een paginatitel,
    /// geen geverifieerde organisatienaam, en mag dus niet als organisator worden gebruikt.
    /// </summary>
    public bool Discovered { get; set; }
    /// <summary>Standaard dorp voor deze bron (bijv. de vaste locatie van een café).</summary>
    public string? DefaultVillage { get; set; }
    public string? DefaultLocationName { get; set; }
    public string? DefaultAddress { get; set; }
    public List<string> DefaultCategories { get; set; } = new();
    /// <summary>Max aantal detailpagina's dat per run wordt gevolgd.</summary>
    public int MaxDetailPages { get; set; } = 25;
}

public sealed class SourceRegistry
{
    public DateTimeOffset CompiledAt { get; set; }
    public List<EventSource> Sources { get; set; } = new();
}
