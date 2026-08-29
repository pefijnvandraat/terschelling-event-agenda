using TerschellingAgenda.Models;

namespace TerschellingAgenda.Process;

/// <summary>
/// Detecteert dezelfde activiteit op meerdere websites en voegt die samen tot één activiteit.
/// Vergelijkt naam, datum, starttijd, locatie, adres en organisator.
/// </summary>
public sealed class Deduplicator
{
    public sealed record Result(List<ActivityEvent> Unique, int Merged);

    public Result Deduplicate(IEnumerable<ActivityEvent> events)
    {
        // Groepeer eerst per datum — activiteiten op verschillende dagen zijn nooit hetzelfde item.
        var byDate = events
            .Where(e => e.Date is not null)
            .GroupBy(e => e.Date!.Value);

        var unique = new List<ActivityEvent>();
        int merged = 0;

        foreach (var group in byDate)
        {
            var buckets = new List<List<ActivityEvent>>();

            foreach (var ev in group.OrderByDescending(e => (int)e.Sources.Min(s => s.Tier) * -1)
                                    .ThenByDescending(e => e.Completeness))
            {
                var target = buckets.FirstOrDefault(b => b.Any(x => IsSameEvent(x, ev)));
                if (target is null) buckets.Add(new List<ActivityEvent> { ev });
                else { target.Add(ev); merged++; }
            }

            foreach (var bucket in buckets)
                unique.Add(bucket.Count == 1 ? bucket[0] : Merge(bucket));
        }

        merged += AbsorbIntoMultiDay(unique);

        return new Result(
            unique.OrderBy(e => e.Date).ThenBy(e => e.StartTime ?? TimeOnly.MaxValue)
                  .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            merged);
    }

    /// <summary>
    /// Naamovereenkomst die meervoud, woordvolgorde en jaartallen verdraagt.
    /// </summary>
    private static double NameSimilarity(string a, string b) =>
        Math.Max(
            Math.Max(TextUtil.Similarity(a, b), TextUtil.TokenSimilarity(a, b)),
            Math.Max(TextUtil.FuzzyTokenSimilarity(a, b), TextUtil.ContainmentScore(a, b)));

    /// <summary>
    /// Vergelijkt namen ook zonder de plaatsaanduiding erin: in "Veemarkt Midsland"
    /// is het dorp een locatievermelding, geen deel van de naam. Zo herkent de
    /// vergelijking dat het dezelfde markt is als "Veemarkt Beestenmerk".
    /// </summary>
    private static double NameSimilarity(ActivityEvent a, ActivityEvent b) =>
        Math.Max(NameSimilarity(a.Name, b.Name),
                 NameSimilarity(NameWithoutVillage(a), NameWithoutVillage(b)));

    private static string NameWithoutVillage(ActivityEvent e)
    {
        if (ActivityEvent.IsUnknown(e.Village)) return e.Name;

        var stripped = TextUtil.Collapse(System.Text.RegularExpressions.Regex.Replace(
                e.Name,
                System.Text.RegularExpressions.Regex.Escape(e.Village),
                " ",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Trim(' ', '-', '–', ',', ':', '|');

        // Blijft er te weinig over, dan was het dorp juist wél de naam.
        return TextUtil.Slug(stripped).Length >= 4 ? stripped : e.Name;
    }

    /// <summary>
    /// Voegt losse dagvermeldingen samen met het meerdaagse evenement waarbinnen ze vallen.
    /// De ene site noemt een festival als bereik ("11 t/m 13 september"), de andere per dag;
    /// dat blijft dezelfde activiteit. De meerdaagse vermelding blijft leidend.
    /// </summary>
    private static int AbsorbIntoMultiDay(List<ActivityEvent> events)
    {
        var multiDay = events
            .Where(e => e.Date is not null && e.EndDate is not null && e.EndDate > e.Date)
            .OrderByDescending(e => e.EndDate!.Value.DayNumber - e.Date!.Value.DayNumber)
            .ToList();
        if (multiDay.Count == 0) return 0;

        var absorbed = new HashSet<ActivityEvent>();
        int count = 0;

        foreach (var host in multiDay)
        {
            if (absorbed.Contains(host)) continue;

            foreach (var part in events)
            {
                if (ReferenceEquals(part, host) || absorbed.Contains(part) || part.Date is null) continue;
                if (part.Date < host.Date || part.Date > host.EndDate) continue;
                if ((part.EndDate ?? part.Date) > host.EndDate) continue;

                // Over dagen heen samenvoegen mag alleen bij een duidelijk gelijke naam.
                if (NameSimilarity(host, part) < 0.80) continue;
                if (!ActivityEvent.IsUnknown(host.Village) && !ActivityEvent.IsUnknown(part.Village) &&
                    !host.Village.Equals(part.Village, StringComparison.OrdinalIgnoreCase)) continue;

                Absorb(host, part);
                absorbed.Add(part);
                count++;
            }
        }

        events.RemoveAll(absorbed.Contains);
        return count;
    }

    /// <summary>Vult het meerdaagse evenement aan met wat de dagvermelding extra weet.</summary>
    private static void Absorb(ActivityEvent host, ActivityEvent part)
    {
        static void Fill(ActivityEvent h, ActivityEvent p, Func<ActivityEvent, string> get, Action<string> set)
        {
            if (ActivityEvent.IsUnknown(get(h)) && !ActivityEvent.IsUnknown(get(p))) set(get(p));
        }

        Fill(host, part, e => e.Description, v => host.Description = v);
        Fill(host, part, e => e.Village, v => host.Village = v);
        Fill(host, part, e => e.LocationName, v => host.LocationName = v);
        Fill(host, part, e => e.Address, v => host.Address = v);
        Fill(host, part, e => e.Organizer, v => host.Organizer = v);
        Fill(host, part, e => e.ContactPerson, v => host.ContactPerson = v);
        Fill(host, part, e => e.Phone, v => host.Phone = v);
        Fill(host, part, e => e.Email, v => host.Email = v);
        Fill(host, part, e => e.Website, v => host.Website = v);
        Fill(host, part, e => e.Price, v => host.Price = v);
        Fill(host, part, e => e.TicketUrl, v => host.TicketUrl = v);

        host.StartTime ??= part.StartTime;
        host.EndTime ??= part.EndTime;
        if (host.PriceKind == PriceKind.Onbekend) host.PriceKind = part.PriceKind;
        if (host.ReservationRequired == Reservation.Onbekend) host.ReservationRequired = part.ReservationRequired;

        host.Categories = host.Categories.Concat(part.Categories).Distinct()
            .OrderBy(c => Array.IndexOf(Categorizer.AllCategories, c)).ToList();
        if (host.Categories.Count > 1) host.Categories.Remove("Overig");

        host.MatchedPlaceTerms = host.MatchedPlaceTerms.Concat(part.MatchedPlaceTerms).Distinct().ToList();

        host.Sources = host.Sources.Concat(part.Sources)
            .GroupBy(s => s.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(s => (int)s.Tier).First())
            .OrderBy(s => (int)s.Tier)
            .ToList();

        host.DuplicateCount += part.DuplicateCount;
        if (part.LastCheckedAt > host.LastCheckedAt) host.LastCheckedAt = part.LastCheckedAt;
        host.Id = Normalizer.ComputeId(host);
    }

    /// <summary>Beslist of twee activiteiten hetzelfde zijn. Conservatief: bij twijfel niet samenvoegen.</summary>
    public static bool IsSameEvent(ActivityEvent a, ActivityEvent b)
    {
        if (a.Date != b.Date) return false;

        double nameSim = NameSimilarity(a, b);

        // Naamgelijkenis is altijd een voorwaarde. Meerdere activiteiten kunnen immers
        // van dezelfde overzichtspagina komen en dus dezelfde bron-URL delen.
        if (nameSim < 0.62) return false;

        // Zelfde bron-URL of dezelfde pagina in een andere taalversie: zeker hetzelfde.
        if (!string.IsNullOrWhiteSpace(a.PrimarySourceUrl) &&
            a.PrimarySourceUrl.Equals(b.PrimarySourceUrl, StringComparison.OrdinalIgnoreCase)) return true;
        if (IsSamePageOtherLanguage(a.PrimarySourceUrl, b.PrimarySourceUrl)) return true;

        // Tijden mogen niet hard conflicteren.
        // Let op: TimeOnly - TimeOnly rekent rond middernacht door (11:00 - 11:30 wordt 23u30),
        // dus vergelijken we het aantal minuten sinds middernacht.
        if (a.StartTime is not null && b.StartTime is not null)
        {
            var diff = Math.Abs(a.StartTime.Value.ToTimeSpan().TotalMinutes -
                                b.StartTime.Value.ToTimeSpan().TotalMinutes);
            if (diff > 45) return false;
        }

        // Verschillende, beide-bekende dorpen => verschillende activiteiten
        if (!ActivityEvent.IsUnknown(a.Village) && !ActivityEvent.IsUnknown(b.Village) &&
            !a.Village.Equals(b.Village, StringComparison.OrdinalIgnoreCase))
            return false;

        // Zeer sterke naamovereenkomst is voldoende
        if (nameSim >= 0.88) return true;

        // Anders moet minstens één andere identificerende factor overeenkomen
        int corroboration = 0;
        if (SoftMatch(a.LocationName, b.LocationName, 0.75)) corroboration++;
        if (SoftMatch(a.Address, b.Address, 0.80)) corroboration++;
        if (SoftMatch(a.Organizer, b.Organizer, 0.80)) corroboration++;
        if (a.StartTime is not null && b.StartTime is not null && a.StartTime == b.StartTime) corroboration++;
        if (!ActivityEvent.IsUnknown(a.Village) && a.Village.Equals(b.Village, StringComparison.OrdinalIgnoreCase))
            corroboration++;

        return corroboration >= 1;
    }

    private static bool SoftMatch(string a, string b, double threshold)
    {
        if (ActivityEvent.IsUnknown(a) || ActivityEvent.IsUnknown(b)) return false;
        return TextUtil.Similarity(a, b) >= threshold ||
               TextUtil.TokenSimilarity(a, b) >= threshold ||
               TextUtil.Slug(a).Contains(TextUtil.Slug(b)) ||
               TextUtil.Slug(b).Contains(TextUtil.Slug(a));
    }

    private static readonly System.Text.RegularExpressions.Regex ReLangSegment =
        new(@"/(?:nl|en|de|fr|nl-nl|en-gb|en-us|de-de)(?=/|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Herkent dezelfde pagina in een andere taalversie, bijvoorbeeld
    /// /nl/agenda/veemarkt en /en/agenda/veemarkt.
    /// </summary>
    private static bool IsSamePageOtherLanguage(string? urlA, string? urlB)
    {
        if (string.IsNullOrWhiteSpace(urlA) || string.IsNullOrWhiteSpace(urlB)) return false;
        if (!Uri.TryCreate(urlA, UriKind.Absolute, out var a)) return false;
        if (!Uri.TryCreate(urlB, UriKind.Absolute, out var b)) return false;
        if (!a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase)) return false;

        var pa = ReLangSegment.Replace(a.AbsolutePath.TrimEnd('/'), "");
        var pb = ReLangSegment.Replace(b.AbsolutePath.TrimEnd('/'), "");
        return pa.Length > 4 && pa.Equals(pb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Voegt duplicaten samen. De meest betrouwbare bron wint per veld.</summary>
    private static ActivityEvent Merge(List<ActivityEvent> group)
    {
        // Beste basis: laagste tier-nummer (= betrouwbaarst), dan meest compleet.
        var ordered = group
            .OrderBy(e => e.Sources.Count > 0 ? (int)e.Sources.Min(s => s.Tier) : 99)
            .ThenByDescending(e => e.Completeness)
            .ThenBy(e => e.Name.Length)
            .ToList();

        var best = ordered[0];
        var result = new ActivityEvent
        {
            Name = best.Name,
            Date = best.Date,
            EndDate = best.EndDate,
            StartTime = best.StartTime,
            EndTime = best.EndTime,
            PrimarySourceUrl = best.PrimarySourceUrl,
            LastCheckedAt = group.Max(e => e.LastCheckedAt),
            DuplicateCount = group.Count
        };

        // Welke wébsite zit achter elke vermelding? Varianten van dezelfde site
        // (overzichtspagina, detailpagina, taalvariant) mogen elkaar niet tegenspreken.
        var origins = new OriginResolver(group.SelectMany(e => e.Sources));

        // Per veld: neem de waarde van de betrouwbaarste bron die hem heeft.
        result.Description = PickText(ordered, origins, e => e.Description, result.Conflicts, "Beschrijving",
            preferLongest: true, affectsConfidence: false);
        result.Village = PickText(ordered, origins, e => e.Village, result.Conflicts, "Dorp/plaats");
        result.LocationName = PickText(ordered, origins, e => e.LocationName, result.Conflicts, "Locatie");
        result.Address = PickText(ordered, origins, e => e.Address, result.Conflicts, "Adres");
        result.Organizer = PickText(ordered, origins, e => e.Organizer, result.Conflicts, "Organisator");
        result.ContactPerson = PickText(ordered, origins, e => e.ContactPerson, result.Conflicts, "Contactpersoon");
        result.Phone = PickText(ordered, origins, e => e.Phone, result.Conflicts, "Telefoonnummer");
        result.Email = PickText(ordered, origins, e => e.Email, result.Conflicts, "E-mailadres");
        result.Website = PickText(ordered, origins, e => e.Website, result.Conflicts, "Website");
        result.Price = PickText(ordered, origins, e => e.Price, result.Conflicts, "Prijs");
        result.TicketUrl = PickText(ordered, origins, e => e.TicketUrl, result.Conflicts, "Ticketlink");

        result.PriceKind = ordered.Select(e => e.PriceKind).FirstOrDefault(k => k != PriceKind.Onbekend);
        result.ReservationRequired = ordered.Select(e => e.ReservationRequired)
            .FirstOrDefault(k => k != Reservation.Onbekend);

        // ontbrekende tijden/einddatum aanvullen uit andere bronnen
        result.StartTime ??= ordered.Select(e => e.StartTime).FirstOrDefault(t => t is not null);
        result.EndTime ??= ordered.Select(e => e.EndTime).FirstOrDefault(t => t is not null);
        result.EndDate ??= ordered.Select(e => e.EndDate).FirstOrDefault(t => t is not null);

        // conflict op starttijd registreren — alleen tussen verschillende wébsites
        var timeByOrigin = group
            .Where(e => e.StartTime is not null)
            .Select(e => (Time: e.StartTime!.Value,
                          Origins: origins.ResolveAll(e.Sources),
                          Tier: e.Sources.Count > 0 ? (int)e.Sources.Min(s => s.Tier) : 99))
            .ToList();

        var chosenTimeOrigins = timeByOrigin
            .Where(t => t.Time == result.StartTime)
            .SelectMany(t => t.Origins)
            .ToHashSet(StringComparer.Ordinal);

        var conflictingTimes = timeByOrigin
            .Where(t => t.Time != result.StartTime)
            .Where(t => !t.Origins.Overlaps(chosenTimeOrigins))
            .ToList();

        if (conflictingTimes.Count > 0 && result.StartTime is not null)
        {
            int chosenTier = timeByOrigin
                .Where(t => t.Time == result.StartTime)
                .Select(t => t.Tier)
                .DefaultIfEmpty(best.Sources.Count > 0 ? (int)best.Sources.Min(s => s.Tier) : 99)
                .Min();

            result.Conflicts.Add(new FieldConflict
            {
                Field = "Starttijd",
                ChosenValue = result.StartTime.Value.ToString("HH:mm"),
                ChosenFrom = best.Sources.FirstOrDefault()?.SourceName ?? "",
                RejectedValues = conflictingTimes.Select(t => t.Time.ToString("HH:mm")).Distinct().ToList(),
                Resolved = conflictingTimes.All(t => t.Tier > chosenTier),
                Reason = "Bronnen noemen verschillende starttijden."
            });
        }

        result.Categories = group.SelectMany(e => e.Categories).Distinct()
            .OrderBy(c => Array.IndexOf(Categorizer.AllCategories, c)).ToList();
        if (result.Categories.Count > 1) result.Categories.Remove("Overig");
        if (result.Categories.Count == 0) result.Categories.Add("Overig");

        result.MatchedPlaceTerms = group.SelectMany(e => e.MatchedPlaceTerms).Distinct().ToList();

        // alle bronnen bewaren, betrouwbaarste eerst, per URL uniek
        result.Sources = group.SelectMany(e => e.Sources)
            .GroupBy(s => s.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(s => (int)s.Tier).First())
            .OrderBy(s => (int)s.Tier)
            .ToList();

        var primary = result.Sources.FirstOrDefault();
        if (primary is not null) result.PrimarySourceUrl = primary.Url;

        result.Id = Normalizer.ComputeId(result);
        return result;
    }

    private static string PickText(
        List<ActivityEvent> ordered,
        OriginResolver origins,
        Func<ActivityEvent, string> selector,
        List<FieldConflict> conflicts,
        string fieldLabel,
        bool preferLongest = false,
        bool affectsConfidence = true)
    {
        var candidates = ordered
            .Select(e => (
                Value: selector(e),
                Tier: e.Sources.Count > 0 ? (int)e.Sources.Min(s => s.Tier) : 99,
                Source: e.Sources.FirstOrDefault()?.SourceName ?? "",
                Origins: origins.ResolveAll(e.Sources)))
            .Where(c => !ActivityEvent.IsUnknown(c.Value))
            .ToList();

        if (candidates.Count == 0) return ActivityEvent.Unknown;

        var chosen = preferLongest
            ? candidates.OrderBy(c => c.Tier).ThenByDescending(c => c.Value.Length).First()
            : candidates.First();

        // Alle websites die de gekozen waarde noemen — daar hoort geen tegenspraak bij.
        var chosenOrigins = candidates
            .Where(c => string.Equals(c.Value, chosen.Value, StringComparison.Ordinal))
            .SelectMany(c => c.Origins)
            .ToHashSet(StringComparer.Ordinal);

        // Alleen echte tegenspraak registreren, en uitsluitend tussen VERSCHILLENDE websites.
        // Varianten binnen één site (overzichtspagina vs. detailpagina, of twee via de
        // zoekmachine gevonden pagina's van hetzelfde domein) zijn geen conflict.
        var conflicting = candidates
            .Where(c => !c.Origins.Overlaps(chosenOrigins))
            .Where(c => TextUtil.Similarity(c.Value, chosen.Value) < 0.7 &&
                        !TextUtil.Slug(c.Value).Contains(TextUtil.Slug(chosen.Value)) &&
                        !TextUtil.Slug(chosen.Value).Contains(TextUtil.Slug(c.Value)))
            .Select(c => c.Value)
            .Distinct()
            .ToList();

        if (conflicting.Count > 0)
        {
            // Opgelost wanneer de gekozen bron aantoonbaar betrouwbaarder is dan de tegensprekers.
            int chosenTier = chosen.Tier;
            bool resolved = candidates
                .Where(c => conflicting.Contains(c.Value))
                .All(c => c.Tier > chosenTier);

            conflicts.Add(new FieldConflict
            {
                Field = fieldLabel,
                ChosenValue = TextUtil.Truncate(chosen.Value, 160),
                ChosenFrom = chosen.Source,
                RejectedValues = conflicting.Select(v => TextUtil.Truncate(v, 160)).ToList(),
                Resolved = resolved,
                AffectsConfidence = affectsConfidence,
                Reason = !affectsConfidence
                    ? "Websites beschrijven dezelfde activiteit in andere woorden. " +
                      "Dat is geen tegenspraak; de uitgebreidste tekst is getoond."
                    : resolved
                        ? "Gekozen op basis van de betrouwbaarste bron."
                        : "Bronnen van gelijke betrouwbaarheid spreken elkaar tegen."
            });
        }

        return chosen.Value;
    }
}
