using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TerschellingAgenda.Collect;
using TerschellingAgenda.Models;

namespace TerschellingAgenda.Process;

/// <summary>Zet een RawEvent om naar het vaste gegevensmodel. Vult nooit iets in wat niet in de bron staat.</summary>
public sealed class Normalizer
{
    private readonly PlaceResolver _places;

    public Normalizer(PlaceResolver places) => _places = places;

    public ActivityEvent? Normalize(RawEvent raw, EventSource source, string pageUrl, DateTimeOffset now, int referenceYear)
    {
        var name = TextUtil.Collapse(raw.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Length < 4) return null;
        name = StripNoise(name);
        if (name.Length < 4 || !LooksLikeEventName(name)) return null;

        // --- datum & tijd ---
        DateOnly? date = null, endDate = null;
        TimeOnly? startTime = null, endTime = null;

        if (!string.IsNullOrWhiteSpace(raw.StartIso))
        {
            var (d, t) = DutchDateParser.ParseIso(raw.StartIso);
            date = d; startTime = t;
        }
        if (!string.IsNullOrWhiteSpace(raw.EndIso))
        {
            var (d, t) = DutchDateParser.ParseIso(raw.EndIso);
            endDate = d; endTime = t;
        }

        if (date is null)
        {
            var span = DutchDateParser.ParseDateRange(raw.DateText ?? raw.RawText, referenceYear);
            if (span is not null) { date = span.Start; endDate ??= span.End; }
        }
        if (date is null) return null; // zonder datum geen bruikbare agenda-activiteit

        if (startTime is null)
        {
            var (s, e) = DutchDateParser.ParseTimes(raw.TimeText ?? raw.RawText);
            startTime ??= s;
            endTime ??= e;
        }

        // Middernacht is vrijwel altijd een parse-artefact: een datum zonder tijd
        // ("2026-09-11T00:00:00") of een hele-dag-item uit een agendafeed of microdata,
        // dat als 00:00–23:59 wordt weggeschreven. Zo'n tijdvak zegt niets over de
        // aanvang, en zou anders ten onrechte botsen met een echte starttijd.
        bool startAtMidnight = startTime is { Hour: 0, Minute: 0 };
        bool endOnDayBoundary = endTime is null ||
                                endTime is { Hour: 0, Minute: 0 } ||
                                endTime is { Hour: 23, Minute: 59 };

        if (startAtMidnight && endOnDayBoundary)
        {
            startTime = null;
            endTime = null;
        }

        // eindtijd op een andere dag dan de startdag: alleen bewaren als er ook een einddatum is
        if (endDate is not null && endDate < date) endDate = null;
        if (endDate == date) endDate = null;

        // Een "meerdaags" evenement van maanden is vrijwel altijd een parsefout
        // (bijvoorbeeld twee losse jaartallen in een voettekst).
        if (endDate is not null && (endDate.Value.DayNumber - date.Value.DayNumber) > 92) endDate = null;

        if (endTime is not null && endDate is null && startTime is not null && endTime <= startTime)
            endTime = null; // 23:00-01:00 kunnen we niet betrouwbaar duiden

        // --- tekstvelden ---
        var description = Clean(raw.Description);
        var haystack = string.Join(" \n ", new[]
        {
            name, raw.Description, raw.LocationName, raw.Address, raw.Organizer, raw.RawText, pageUrl
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var (village, terms) = _places.ResolveVillage(name, raw.LocationName, raw.Address, raw.Description, raw.RawText);
        if (village == ActivityEvent.Unknown && !string.IsNullOrWhiteSpace(source.DefaultVillage))
            village = source.DefaultVillage!;

        // Een "activiteit" waarvan de naam louter een plaatsnaam is (bijv. "Midsland"
        // of "Midsland centrum") is een locatievermelding, geen activiteit.
        if (IsJustAPlaceName(name)) return null;

        var locationName = DedupeParts(Clean(raw.LocationName));
        if (ActivityEvent.IsUnknown(locationName) && !string.IsNullOrWhiteSpace(source.DefaultLocationName))
            locationName = source.DefaultLocationName!;

        var address = DedupeParts(Clean(raw.Address));
        if (ActivityEvent.IsUnknown(address))
            address = DedupeParts(Clean(TextUtil.FindAddress(raw.RawText)));
        if (ActivityEvent.IsUnknown(address) && !string.IsNullOrWhiteSpace(source.DefaultAddress))
            address = source.DefaultAddress!;

        var organizer = Clean(raw.Organizer);
        if (!ActivityEvent.IsUnknown(organizer) && !LooksLikeOrganizer(organizer!)) organizer = null;

        // Terugval op de bronnaam mag alleen bij een geregistreerde organisatorbron.
        // Bij een via de zoekmachine gevonden pagina is de "naam" de paginatitel.
        if (ActivityEvent.IsUnknown(organizer) && source.Tier == SourceTier.PrimaryOrganizer &&
            !source.Discovered && LooksLikeOrganizer(source.Name))
            organizer = source.Name;

        var phone = Clean(raw.Phone) is var ph && !ActivityEvent.IsUnknown(ph) ? ph : Clean(TextUtil.FindPhone(raw.RawText));
        var email = Clean(raw.Email) is var em && !ActivityEvent.IsUnknown(em) ? em : Clean(TextUtil.FindEmail(raw.RawText));

        // --- prijs & reservering ---
        var (priceText, priceKind) = PriceParser.Parse(raw.PriceText ?? raw.RawText);
        if (!string.IsNullOrWhiteSpace(raw.PriceText) && priceKind == PriceKind.Onbekend)
        {
            var direct = TextUtil.Collapse(raw.PriceText);
            if (direct.Equals("Gratis", StringComparison.OrdinalIgnoreCase)) (priceText, priceKind) = ("Gratis", PriceKind.Gratis);
        }
        var reservation = PriceParser.ParseReservation(raw.RawText ?? raw.Description);
        if (reservation == Reservation.Onbekend && !string.IsNullOrWhiteSpace(raw.TicketUrl))
            reservation = Reservation.Ja;

        // --- categorieën ---
        // Bewust NIET op de volledige paginatekst classificeren: dat levert bij
        // overzichtspagina's tientallen valse categorieën op.
        var cats = Categorizer.Classify(name, description, locationName,
            string.Join(" ", raw.Categories), string.Join(" ", source.DefaultCategories));
        if (source.DefaultCategories.Count > 0)
        {
            foreach (var c in source.DefaultCategories)
                if (Categorizer.AllCategories.Contains(c) && !cats.Contains(c)) cats.Add(c);
            if (cats.Count > 1) cats.Remove("Overig");
            cats = cats.Distinct().OrderBy(c => Array.IndexOf(Categorizer.AllCategories, c)).ToList();
        }

        var detailUrl = raw.DetailUrl ?? pageUrl;

        var ev = new ActivityEvent
        {
            Name = name,
            Date = date,
            EndDate = endDate,
            StartTime = startTime,
            EndTime = endTime,
            Description = ActivityEvent.IsUnknown(description) ? ActivityEvent.Unknown : description!,
            Categories = cats,
            Village = village,
            LocationName = locationName ?? ActivityEvent.Unknown,
            Address = address ?? ActivityEvent.Unknown,
            Organizer = organizer ?? ActivityEvent.Unknown,
            ContactPerson = ActivityEvent.Unknown,
            Phone = phone ?? ActivityEvent.Unknown,
            Email = email ?? ActivityEvent.Unknown,
            Website = Clean(raw.Website) ?? ActivityEvent.Unknown,
            Price = priceText,
            PriceKind = priceKind,
            ReservationRequired = reservation,
            TicketUrl = Clean(raw.TicketUrl) ?? ActivityEvent.Unknown,
            PrimarySourceUrl = detailUrl,
            LastCheckedAt = now,
            MatchedPlaceTerms = terms,
            Sources =
            {
                new EventSourceRef
                {
                    SourceId = source.Id,
                    SourceName = source.Name,
                    Tier = source.Tier,
                    Url = detailUrl,
                    RetrievedAt = now,
                    Method = raw.Method
                }
            }
        };

        ev.ContactPerson = FindContactPerson(raw.RawText) ?? ActivityEvent.Unknown;
        ev.Id = ComputeId(ev);
        return ev;
    }

    private static readonly Regex ReContact = new(
        @"\b(?:contact(?:persoon)?|informatie bij|inlichtingen bij|meer info(?:rmatie)? bij|neem contact op met|aanmelden bij)\s*[:\-]?\s*(?<v>[A-ZÀ-Ý][\p{L}'’\-]+(?:\s+(?:van|de|den|der|ter|te)\s+)?(?:\s+[A-ZÀ-Ý][\p{L}'’\-]+){0,2})",
        RegexOptions.Compiled);

    private static string? FindContactPerson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = ReContact.Match(text);
        if (!m.Success) return null;
        var v = TextUtil.Collapse(m.Groups["v"].Value);
        return v.Length is >= 3 and <= 60 ? v : null;
    }

    private static string StripNoise(string s)
    {
        s = Regex.Replace(s, @"^\s*(?:agenda|evenement|activiteit)\s*[:\-–]\s*", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s*[|·•]\s*(?:VVV|Terschelling|Agenda)\s*$", "", RegexOptions.IgnoreCase);
        return TextUtil.Collapse(s).Trim(' ', '-', '–', '|', '•', ':', '»', '«');
    }

    private static readonly Regex ReNavNoise = new(
        @"^(lees\s+meer|lees\s+verder|meer\s+info\w*|bekijk\s+\w+|klik\s+hier|terug|volgende|vorige|home|menu|" +
        @"cookie\w*|privacy\w*|nieuwsbrief|inloggen|zoeken|filter\w*|alle\s+\w+|toon\s+\w+|" +
        @"vandaag|morgen|gisteren|overmorgen|today|tomorrow|bewerken|edit|" +
        @"(?:event|evenement)\s*details?|details|read\s+more|book\s+now|" +
        @"boek(?:en|ing)?\s+(?:uw|je|nu|hier|online)|reserveer|bestel|koop\s+(?:uw|je|nu|tickets?|kaarten)|" +
        @"plan\s+(?:uw|je)|aanmelden|inschrijven|" +
        @"datum|tijd|locatie|adres|prijs|entree|organisator|categorie)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Titels van overzichts- en sitepagina's, die geen organisatornaam zijn.</summary>
    private static readonly Regex ReSiteTitle = new(
        @"^(?:evenementen\w*|agenda|activiteiten|uitagenda|uitjes|wat\s+te\s+(?:doen|beleven)|" +
        @"home|welkom|vakantie\w*|toerisme)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Weert pagina- en sitetitels die per ongeluk als organisator zijn opgepikt, bijvoorbeeld
    /// "Evenementen Terschelling 2021 - Agenda Terschelling Midsland". Zulke waarden lijken op
    /// tegenspraak tussen bronnen terwijl er alleen een titel is meegelezen.
    /// </summary>
    private static bool LooksLikeOrganizer(string value)
    {
        if (value.Length is < 2 or > 80) return false;
        if (ReSiteTitle.IsMatch(value)) return false;
        if (value.Contains('|')) return false;
        if (Regex.IsMatch(value, @"\b(?:19|20)\d{2}\b")) return false;

        int words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words > 9) return false;
        if (words > 5 && Regex.IsMatch(value, @"\s[-–—]\s")) return false;
        return true;
    }

    /// <summary>
    /// Haalt herhaling uit samengestelde plaats- en adresnamen: "Midsland, Midsland"
    /// wordt "Midsland". Zulke echo's veroorzaken anders schijnbare verschillen tussen bronnen.
    /// </summary>
    private static string? DedupeParts(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains(',')) return value;

        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = parts.Where(p => seen.Add(TextUtil.Slug(p))).ToList();

        return kept.Count == parts.Length ? value : string.Join(", ", kept);
    }

    /// <summary>
    /// Laatste kwaliteitspoort: weert navigatie-elementen, datum-only-titels en
    /// metadata-blobs die per ongeluk als activiteitnaam zijn opgepikt.
    /// </summary>
    private static bool LooksLikeEventName(string name)
    {
        if (ReNavNoise.IsMatch(name)) return false;
        if (Regex.IsMatch(name, @"\d(?:[A-Z][a-z])")) return false;   // "…2026Tijd: 14:30…"
        if (name.Count(char.IsDigit) > name.Length / 2) return false;  // overwegend cijfers

        var wordy = Regex.Replace(name, @"[\d:.\-–—/]+", " ");
        wordy = DutchDateParser.StripWeekdays(wordy);
        wordy = Regex.Replace(wordy,
            @"\b(jan|feb|mrt|maart|apr|mei|jun|juni|jul|juli|aug|augustus|sep|sept|september|okt|oktober|nov|november|dec|december|januari|februari|april|uur|t/m|tot|en|van|de|het|een)\b",
            " ", RegexOptions.IgnoreCase);

        var words = TextUtil.Collapse(wordy).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2 && w.Any(char.IsLetter)).ToList();
        return words.Count >= 1 && string.Join("", words).Length >= 4;
    }

    /// <summary>
    /// Is de naam feitelijk alleen een plaatsaanduiding ("Midsland", "Midsland centrum",
    /// "Dorpshuis Hoorn")? Dan is het geen activiteit maar een locatievermelding.
    /// </summary>
    private bool IsJustAPlaceName(string name)
    {
        var hits = _places.FindPlaces(name);
        if (hits.Count == 0) return false;

        var rest = TextUtil.Slug(name);
        foreach (var h in hits)
            foreach (var variant in h.AllNames)
                rest = rest.Replace(TextUtil.Slug(variant), " ");

        // Woorden die een plaatsnaam kwalificeren maar er geen activiteit van maken.
        rest = Regex.Replace(rest,
            @"\b(centrum|dorp|dorpshuis|haven|strand|paal|kerk|plein|straat|weg|west|oost|noord|zuid|" +
            @"aan|zee|terschelling|het|de|een|en|van|bij|op|in|nl|nederland)\b", " ");

        return TextUtil.Collapse(rest).Length < 4;
    }

    private static string? Clean(string? s)
    {
        var v = TextUtil.Collapse(s);
        if (v.Length == 0) return null;
        if (v.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        if (v.Equals("n.v.t.", StringComparison.OrdinalIgnoreCase)) return null;
        return TextUtil.Truncate(v, 1200);
    }

    /// <summary>Stabiele id op basis van de identificerende kenmerken.</summary>
    public static string ComputeId(ActivityEvent e)
    {
        var key = string.Join("|",
            TextUtil.Slug(e.Name),
            e.Date?.ToString("yyyy-MM-dd") ?? "",
            e.StartTime?.ToString("HH:mm") ?? "",
            TextUtil.Slug(e.Village),
            TextUtil.Slug(e.LocationName));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
