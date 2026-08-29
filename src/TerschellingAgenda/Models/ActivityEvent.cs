using System.Text.Json.Serialization;

namespace TerschellingAgenda.Models;

/// <summary>Betrouwbaarheid van een (deel van een) gegeven.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Confidence
{
    Onbekend = 0,
    Onzeker = 1,
    Bevestigd = 2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceTier
{
    PrimaryOrganizer = 1,
    OfficialVenue = 2,
    OfficialLocal = 3,
    TouristCalendar = 4,
    Aggregator = 5,
    Social = 6
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PriceKind
{
    Onbekend = 0,
    Gratis = 1,
    Betaald = 2
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Reservation
{
    Onbekend = 0,
    Ja = 1,
    Nee = 2
}

public sealed class EventSourceRef
{
    public string SourceId { get; set; } = "";
    public string SourceName { get; set; } = "";
    public SourceTier Tier { get; set; } = SourceTier.Aggregator;
    public string Url { get; set; } = "";
    public DateTimeOffset RetrievedAt { get; set; }
    /// <summary>Hoe het item is geëxtraheerd: jsonld | ics | rss | html | microdata | discovery.</summary>
    public string Method { get; set; } = "html";
}

/// <summary>Een geregistreerd verschil tussen bronnen over hetzelfde veld.</summary>
public sealed class FieldConflict
{
    public string Field { get; set; } = "";
    public string ChosenValue { get; set; } = "";
    public string ChosenFrom { get; set; } = "";
    public List<string> RejectedValues { get; set; } = new();
    public bool Resolved { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>
    /// Weegt dit verschil mee in de betrouwbaarheid? Voor feitelijke velden (datum, tijd,
    /// plaats, adres, prijs) wél. Voor vrije tekst zoals de beschrijving niet: twee websites
    /// verwoorden dezelfde activiteit vrijwel nooit hetzelfde, en dat is geen tegenspraak.
    /// </summary>
    public bool AffectsConfidence { get; set; } = true;
}

public sealed class ActivityEvent
{
    public const string Unknown = "Onbekend";

    public string Id { get; set; } = "";

    // --- kern ---
    public string Name { get; set; } = "";
    public DateOnly? Date { get; set; }
    public DateOnly? EndDate { get; set; }
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public string Description { get; set; } = Unknown;

    // --- classificatie ---
    public List<string> Categories { get; set; } = new();

    // --- plaats ---
    public string Village { get; set; } = Unknown;
    public string LocationName { get; set; } = Unknown;
    public string Address { get; set; } = Unknown;

    // --- organisatie ---
    public string Organizer { get; set; } = Unknown;
    public string ContactPerson { get; set; } = Unknown;
    public string Phone { get; set; } = Unknown;
    public string Email { get; set; } = Unknown;
    public string Website { get; set; } = Unknown;

    // --- commercie ---
    public string Price { get; set; } = Unknown;
    public PriceKind PriceKind { get; set; } = PriceKind.Onbekend;
    public Reservation ReservationRequired { get; set; } = Reservation.Onbekend;
    public string TicketUrl { get; set; } = Unknown;

    // --- herkomst & betrouwbaarheid ---
    public string PrimarySourceUrl { get; set; } = "";
    public List<EventSourceRef> Sources { get; set; } = new();
    public Confidence Confidence { get; set; } = Confidence.Onzeker;
    public List<FieldConflict> Conflicts { get; set; } = new();
    public int DuplicateCount { get; set; } = 1;
    public DateTimeOffset LastCheckedAt { get; set; }

    /// <summary>Welke zoekterm/plaatsnaam dit item opleverde — voor kwaliteitscontrole.</summary>
    public List<string> MatchedPlaceTerms { get; set; } = new();
    public string DiscoveryQuery { get; set; } = Unknown;

    public static bool IsUnknown(string? s) =>
        string.IsNullOrWhiteSpace(s) || string.Equals(s.Trim(), Unknown, StringComparison.OrdinalIgnoreCase);

    /// <summary>Aantal ingevulde (niet-Onbekend) velden — gebruikt bij het kiezen van de beste variant.</summary>
    [JsonIgnore]
    public int Completeness
    {
        get
        {
            int n = 0;
            if (!IsUnknown(Name)) n++;
            if (Date is not null) n++;
            if (StartTime is not null) n++;
            if (EndTime is not null) n++;
            if (!IsUnknown(Description)) n++;
            if (Categories.Count > 0) n++;
            if (!IsUnknown(Village)) n++;
            if (!IsUnknown(LocationName)) n++;
            if (!IsUnknown(Address)) n++;
            if (!IsUnknown(Organizer)) n++;
            if (!IsUnknown(ContactPerson)) n++;
            if (!IsUnknown(Phone)) n++;
            if (!IsUnknown(Email)) n++;
            if (!IsUnknown(Website)) n++;
            if (!IsUnknown(Price)) n++;
            if (PriceKind != PriceKind.Onbekend) n++;
            if (ReservationRequired != Reservation.Onbekend) n++;
            if (!IsUnknown(TicketUrl)) n++;
            return n;
        }
    }

    /// <summary>Valt het evenement (deels) binnen [from,to]? Meerdaagse events tellen mee.</summary>
    public bool OverlapsRange(DateOnly from, DateOnly to)
    {
        if (Date is null) return false;
        var start = Date.Value;
        var end = EndDate ?? Date.Value;
        if (end < start) end = start;
        return start <= to && end >= from;
    }
}
