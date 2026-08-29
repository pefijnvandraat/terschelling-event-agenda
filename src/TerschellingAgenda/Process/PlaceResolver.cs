using System.Text.RegularExpressions;
using TerschellingAgenda.Models;

namespace TerschellingAgenda.Process;

/// <summary>
/// Koppelt vrije tekst aan Terschellinger plaatsnamen en bepaalt of een item
/// überhaupt op Terschelling speelt. Voorkomt valse positieven van gelijknamige
/// plaatsen elders in Nederland.
/// </summary>
public sealed class PlaceResolver
{
    private readonly GeoRegistry _geo;
    private readonly List<(Regex Rx, Place Place, int Weight)> _patterns = new();

    // Plaatsnamen die elders in NL veel voorkomen: alleen tellen als er ook een
    // eiland-signaal in de tekst staat. Let op: Harlingen is de veerhaven op het
    // vasteland en is dus juist GEEN Terschelling-signaal.
    private static readonly Regex IslandSignal = new(
        @"\b(terschelling|schylge|skylge|west-?terschelling|midsland|formerum|" +
        @"baaiduinen|landerum|kinnum|striep|baaiduinen|oosterend\s*\(?terschelling|" +
        @"brandaris|boschplaat|oerol|noordsvaarder|koegelwieck|griltjeplak|" +
        @"888[0-9]|889[0-9])\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Duidelijke tegen-signalen: het gaat over een andere plaats of ander eiland.
    private static readonly Regex OtherPlace = new(
        @"\b(texel|vlieland|ameland|schiermonnikoog|den\s+helder|oudeschild|den\s+burg|" +
        @"harlingen|leeuwarden|franeker|bolsward|sneek|dokkum|groningen|" +
        @"nes\s*\(?ameland|hollum|ballum|buren\s*\(?ameland|oost-?vlieland)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public PlaceResolver(GeoRegistry geo)
    {
        _geo = geo;
        foreach (var p in geo.Places)
        {
            int weight = p.Type switch
            {
                "dorp" => 100,
                "buurtschap" => 90,
                "gehucht" => 85,
                "venue" => 80,
                "landmark" => 60,
                "natuurgebied" => 50,
                "strand" => 45,
                _ => 40
            };
            foreach (var name in p.AllNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (name.Length < 3) continue;
                var pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(name).Replace(@"\ ", @"[\s\-]+") + @"(?![\p{L}\p{N}])";
                _patterns.Add((new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled), p,
                    weight + Math.Min(name.Length, 20)));
            }
        }
        // langste/zwaarste patronen eerst
        _patterns = _patterns.OrderByDescending(t => t.Weight).ToList();
    }

    public GeoRegistry Geo => _geo;

    /// <summary>Alle Terschellinger plaatsnamen die in de tekst voorkomen, zwaarste eerst.</summary>
    public List<Place> FindPlaces(params string?[] texts)
    {
        var haystack = string.Join(" \n ", texts.Where(t => !string.IsNullOrWhiteSpace(t)));
        if (string.IsNullOrWhiteSpace(haystack)) return new List<Place>();
        haystack = haystack.Replace('\u00a0', ' ');

        var hits = new List<Place>();
        foreach (var (rx, place, _) in _patterns)
        {
            if (hits.Any(h => h.Name == place.Name)) continue;
            if (rx.IsMatch(haystack)) hits.Add(place);
        }
        return hits;
    }

    /// <summary>Bepaalt het dorp/de plaats van een activiteit. Geeft "Onbekend" als niets gevonden is.</summary>
    public (string Village, List<string> MatchedTerms) ResolveVillage(params string?[] texts)
    {
        var hits = FindPlaces(texts);
        var terms = hits.Select(h => h.Name).ToList();

        // eerst een echt dorp
        var village = hits.FirstOrDefault(h => h.Type == "dorp");
        // dan buurtschap/gehucht
        village ??= hits.FirstOrDefault(h => h.Type is "buurtschap" or "gehucht");
        // dan de parent van een venue/landmark/natuurgebied
        if (village is null)
        {
            var withParent = hits.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h.Parent));
            if (withParent is not null)
            {
                var parent = _geo.Places.FirstOrDefault(p =>
                    p.Name.Equals(withParent.Parent, StringComparison.OrdinalIgnoreCase));
                if (parent is not null) village = parent;
            }
        }

        return (village?.Name ?? ActivityEvent.Unknown, terms);
    }

    /// <summary>
    /// Is dit item plausibel een activiteit óp Terschelling?
    /// Een expliciet eiland-signaal telt, tenzij een andere plaats duidelijk dominant is.
    /// </summary>
    public bool IsOnTerschelling(string? text, string? sourceUrl, bool sourceIsIslandSpecific)
    {
        var hay = (text ?? "") + " \n " + (sourceUrl ?? "");

        // Een concurrerende plaatsnaam in de titel is een sterk tegensignaal, ook bij
        // een eilandspecifieke bron (bijv. de veerdienst die over Harlingen bericht).
        var head = hay.Length > 220 ? hay[..220] : hay;
        bool otherInTitle = OtherPlace.IsMatch(head);
        bool islandInTitle = IslandSignal.IsMatch(head);
        if (otherInTitle && !islandInTitle) return false;

        if (sourceIsIslandSpecific) return true;
        if (IslandSignal.IsMatch(hay)) return true;

        // Een niet-ambigue lokale naam (bv. "Baaiduinen", "Formerum") is op zichzelf voldoende.
        var hits = FindPlaces(hay);
        return hits.Any(h => !h.Ambiguous && h.Type is "dorp" or "buurtschap" or "gehucht" or "venue" or "landmark");
    }

    /// <summary>Waarschuwt als de tekst duidelijk over een andere plaats gaat.</summary>
    public bool MentionsOtherPlaceOnly(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return OtherPlace.IsMatch(text) && !IslandSignal.IsMatch(text);
    }
}
