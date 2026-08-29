using System.Text.Json.Serialization;

namespace TerschellingAgenda.Models;

/// <summary>Een plaatsnaam op Terschelling (dorp, buurtschap, gehucht, natuurgebied, locatie, venue).</summary>
public sealed class Place
{
    public string Name { get; set; } = "";
    /// <summary>dorp | buurtschap | gehucht | natuurgebied | strand | landmark | venue | overig</summary>
    public string Type { get; set; } = "overig";
    /// <summary>Bovenliggend dorp/gebied, indien van toepassing.</summary>
    public string? Parent { get; set; }
    public List<string> Variants { get; set; } = new();
    public string Source { get; set; } = "";
    /// <summary>True als de naam ook elders in Nederland voorkomt — zoekresultaten extra filteren.</summary>
    public bool Ambiguous { get; set; }
    /// <summary>Gebruik deze naam als losse zoekterm bij event-discovery.</summary>
    public bool UseAsSearchTerm { get; set; } = true;

    [JsonIgnore]
    public IEnumerable<string> AllNames => new[] { Name }.Concat(Variants).Where(s => !string.IsNullOrWhiteSpace(s));
}

public sealed class GeoRegistry
{
    public string Island { get; set; } = "Terschelling";
    public string Municipality { get; set; } = "Gemeente Terschelling";
    public string Province { get; set; } = "Fryslân";
    public DateTimeOffset CompiledAt { get; set; }
    public List<string> VerificationSources { get; set; } = new();
    public List<Place> Places { get; set; } = new();

    public IEnumerable<Place> Villages => Places.Where(p => p.Type is "dorp");
    public IEnumerable<Place> Hamlets => Places.Where(p => p.Type is "buurtschap" or "gehucht");
    public IEnumerable<Place> SearchTerms => Places.Where(p => p.UseAsSearchTerm);
}
