using TerschellingAgenda.Models;

namespace TerschellingAgenda.Process;

/// <summary>
/// Bepaalt de betrouwbaarheid van een activiteit volgens de bronprioriteit:
/// 1 organisator/primaire bron, 2 officiële locatie, 3 betrouwbare lokale bron,
/// 4 toeristische/evenementenkalender, 5 overige openbare bronnen.
/// </summary>
public static class Validator
{
    public static void Apply(ActivityEvent e)
    {
        var bestTier = e.Sources.Count > 0 ? e.Sources.Min(s => (int)s.Tier) : (int)SourceTier.Social;

        // Twee pagina's van dezelfde website zijn geen twee bronnen. Tel daarom
        // onafhankelijke wébsites, niet losse vermeldingen of pagina-ids.
        var origins = new OriginResolver(e.Sources);
        int distinctSources = origins.ResolveAll(e.Sources).Count;
        if (distinctSources == 0 && e.Sources.Count > 0) distinctSources = 1;

        bool unresolvedConflict = e.Conflicts.Any(c => !c.Resolved && c.AffectsConfidence);

        // Kernvelden aanwezig?
        bool hasCore = e.Date is not null && !ActivityEvent.IsUnknown(e.Name);
        bool hasDetail = e.StartTime is not null ||
                         !ActivityEvent.IsUnknown(e.LocationName) ||
                         !ActivityEvent.IsUnknown(e.Village);

        if (!hasCore)
        {
            e.Confidence = Confidence.Onbekend;
            return;
        }

        if (unresolvedConflict)
        {
            e.Confidence = Confidence.Onzeker;
            return;
        }

        // Bronprioriteit 1–2 (organisator / officiële locatie) en 3 (officiële lokale bron,
        // zoals de gemeentelijke agenda) gelden zelfstandig als primaire bevestiging.
        bool primary = bestTier <= (int)SourceTier.OfficialLocal;
        // Een toeristische kalender alléén is niet genoeg; bevestiging door een tweede
        // onafhankelijke bron maakt het wel betrouwbaar.
        bool multiSource = distinctSources >= 2 && bestTier <= (int)SourceTier.Aggregator;

        if ((primary || multiSource) && hasDetail)
            e.Confidence = Confidence.Bevestigd;
        else
            e.Confidence = Confidence.Onzeker;
    }

    /// <summary>Telt per veld hoe vaak het niet kon worden geverifieerd.</summary>
    public static List<UnverifiedField> SummariseUnverified(IReadOnlyList<ActivityEvent> events)
    {
        if (events.Count == 0) return new List<UnverifiedField>();

        var checks = new (string Field, Func<ActivityEvent, bool> Missing)[]
        {
            ("Starttijd",       e => e.StartTime is null),
            ("Eindtijd",        e => e.EndTime is null),
            ("Beschrijving",    e => ActivityEvent.IsUnknown(e.Description)),
            ("Dorp/plaats",     e => ActivityEvent.IsUnknown(e.Village)),
            ("Locatie",         e => ActivityEvent.IsUnknown(e.LocationName)),
            ("Adres",           e => ActivityEvent.IsUnknown(e.Address)),
            ("Organisator",     e => ActivityEvent.IsUnknown(e.Organizer)),
            ("Contactpersoon",  e => ActivityEvent.IsUnknown(e.ContactPerson)),
            ("Telefoonnummer",  e => ActivityEvent.IsUnknown(e.Phone)),
            ("E-mailadres",     e => ActivityEvent.IsUnknown(e.Email)),
            ("Website",         e => ActivityEvent.IsUnknown(e.Website)),
            ("Prijs",           e => e.PriceKind == PriceKind.Onbekend),
            ("Reserveren",      e => e.ReservationRequired == Reservation.Onbekend),
            ("Ticketlink",      e => ActivityEvent.IsUnknown(e.TicketUrl))
        };

        return checks
            .Select(c => new UnverifiedField
            {
                Field = c.Field,
                MissingCount = events.Count(c.Missing),
                TotalEvents = events.Count
            })
            .Where(u => u.MissingCount > 0)
            .OrderByDescending(u => u.MissingCount)
            .ToList();
    }
}
