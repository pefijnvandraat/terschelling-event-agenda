namespace TerschellingAgenda.Models;

/// <summary>Resultaat van één bron tijdens een verzamelronde.</summary>
public sealed class SourceOutcome
{
    public string SourceId { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string Category { get; set; } = "";
    public SourceTier Tier { get; set; }
    public List<string> UrlsTried { get; set; } = new();
    /// <summary>ok | leeg | fout | geblokkeerd | overgeslagen</summary>
    public string Status { get; set; } = "ok";
    public int HttpStatus { get; set; }
    public int RawEventsFound { get; set; }
    public int InRangeEvents { get; set; }
    public string? Error { get; set; }
    public long DurationMs { get; set; }
    public List<string> Methods { get; set; } = new();
    /// <summary>Welke ophaalstrategieën zijn gebruikt (Direct, HostVariant, Browser, WebArchive…).</summary>
    public List<string> Strategies { get; set; } = new();
    /// <summary>Beknopt logboek van de opschalingspogingen, alleen gevuld als er is opgeschaald.</summary>
    public List<string> AttemptLog { get; set; } = new();
    /// <summary>True wanneer de gegevens uit een gearchiveerde momentopname komen.</summary>
    public bool FromArchive { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

/// <summary>Volledig transparantierapport van één zoekopdracht.</summary>
public sealed class RunReport
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("n")[..12];
    public DateTimeOffset SearchedAt { get; set; }
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }

    public List<string> PlacesIncluded { get; set; } = new();
    public List<string> SourceTypesInvestigated { get; set; } = new();
    public List<SourceOutcome> SourceOutcomes { get; set; } = new();
    public List<string> SearchQueriesUsed { get; set; } = new();

    public int SourcesTotal { get; set; }
    public int SourcesOk { get; set; }
    public int SourcesFailed { get; set; }
    public int RawEventsCollected { get; set; }
    public int UniqueEvents { get; set; }
    public int DuplicatesMerged { get; set; }

    public int EventsConfirmed { get; set; }
    public int EventsUncertain { get; set; }
    public int EventsUnknownData { get; set; }

    /// <summary>Velden die voor minstens één activiteit niet konden worden geverifieerd.</summary>
    public List<UnverifiedField> UnverifiedFields { get; set; } = new();
    public List<string> UnreachableSources { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public long DurationMs { get; set; }

    /// <summary>Hoe vaak elke ophaalstrategie nodig was — laat zien wat de terugvalladder oplevert.</summary>
    public Dictionary<string, int> FetchStrategies { get; set; } = new();
    /// <summary>Bronnen waarvoor een echte browser nodig was (JavaScript of weigering van eenvoudige clients).</summary>
    public List<string> SourcesNeedingBrowser { get; set; } = new();
    /// <summary>Bronnen waarvan de gegevens uit een gearchiveerde momentopname komen — mogelijk verouderd.</summary>
    public List<string> SourcesFromArchive { get; set; } = new();
    /// <summary>Websites die zijn overgeslagen omdat ze niet reageerden — bespaart wachttijd.</summary>
    public List<string> SkippedHosts { get; set; } = new();
    public bool BrowserAvailable { get; set; }

    public string Disclaimer { get; set; } =
        "Zo compleet mogelijk overzicht op basis van de geraadpleegde openbare bronnen. " +
        "Er kan niet worden gegarandeerd dat alle activiteiten op Terschelling zijn gevonden.";
}

public sealed class UnverifiedField
{
    public string Field { get; set; } = "";
    public int MissingCount { get; set; }
    public int TotalEvents { get; set; }
}
