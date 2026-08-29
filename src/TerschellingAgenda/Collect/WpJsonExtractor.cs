using System.Text.Json;
using System.Text.RegularExpressions;
using TerschellingAgenda.Process;

namespace TerschellingAgenda.Collect;

/// <summary>
/// Leest evenementen uit JSON-API's die veel Nederlandse sites (ongemerkt) aanbieden:
/// de WordPress REST API en The Events Calendar. Deze endpoints zijn vaak wél bereikbaar
/// wanneer de gewone HTML-pagina achter een JavaScript-laag of weigering zit.
/// </summary>
public static class WpJsonExtractor
{
    public static List<RawEvent> Extract(string json, string sourceUrl)
    {
        var results = new List<RawEvent>();
        if (string.IsNullOrWhiteSpace(json)) return results;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // The Events Calendar: { "events": [ ... ] }
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("events", out var evArr) &&
                evArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in evArr.EnumerateArray())
                {
                    var mapped = MapTribeEvent(e, sourceUrl);
                    if (mapped is not null) results.Add(mapped);
                }
                return results;
            }

            // WordPress REST posts: [ { title: { rendered }, ... } ]
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in root.EnumerateArray())
                {
                    var mapped = MapWpPost(e, sourceUrl);
                    if (mapped is not null) results.Add(mapped);
                }
            }
        }
        catch (JsonException) { /* geen bruikbare JSON: stil overslaan */ }

        return results;
    }

    private static RawEvent? MapTribeEvent(JsonElement e, string sourceUrl)
    {
        var title = Str(e, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;

        var ev = new RawEvent
        {
            Method = "json",
            Name = StripHtml(title),
            StartIso = Str(e, "start_date") ?? Str(e, "utc_start_date"),
            EndIso = Str(e, "end_date") ?? Str(e, "utc_end_date"),
            Description = StripHtml(Str(e, "description") ?? Str(e, "excerpt")),
            DetailUrl = Str(e, "url") ?? sourceUrl,
            PriceText = Str(e, "cost")
        };

        if (e.TryGetProperty("venue", out var venue) && venue.ValueKind == JsonValueKind.Object)
        {
            ev.LocationName = Str(venue, "venue");
            var parts = new[] { "address", "zip", "city" }
                .Select(k => Str(venue, k)).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (parts.Count > 0) ev.Address = string.Join(", ", parts);
            ev.Phone = Str(venue, "phone");
            ev.Website = Str(venue, "website");
        }

        if (e.TryGetProperty("organizer", out var org))
        {
            if (org.ValueKind == JsonValueKind.Array && org.GetArrayLength() > 0)
                ev.Organizer = Str(org[0], "organizer");
            else if (org.ValueKind == JsonValueKind.Object)
                ev.Organizer = Str(org, "organizer");
        }

        if (e.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
            foreach (var c in cats.EnumerateArray())
            {
                var n = Str(c, "name");
                if (!string.IsNullOrWhiteSpace(n)) ev.Categories.Add(n);
            }

        ev.RawText = string.Join(" \n ", new[]
        {
            ev.Name, ev.Description, ev.LocationName, ev.Address, ev.Organizer, ev.PriceText
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return ev.StartIso is null ? null : ev;
    }

    private static RawEvent? MapWpPost(JsonElement e, string sourceUrl)
    {
        var title = StripHtml(Rendered(e, "title"));
        if (string.IsNullOrWhiteSpace(title)) return null;

        var content = StripHtml(Rendered(e, "content") ?? Rendered(e, "excerpt"));

        var ev = new RawEvent
        {
            Method = "json",
            Name = title,
            Description = content,
            DetailUrl = Str(e, "link") ?? sourceUrl,
            RawText = title + " \n " + content
        };

        // Datum bij voorkeur uit een meta-veld; anders uit de tekst zelf.
        if (e.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            ev.StartIso = Str(meta, "_EventStartDate") ?? Str(meta, "event_start") ?? Str(meta, "start_date");
            ev.EndIso = Str(meta, "_EventEndDate") ?? Str(meta, "event_end") ?? Str(meta, "end_date");
        }
        ev.DateText = ev.RawText;

        return ev;
    }

    private static string? Rendered(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("rendered", out var r))
            return r.GetString();
        return null;
    }

    private static string? Str(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => TextUtil.Collapse(v.GetString()) is { Length: > 0 } s ? s : null,
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
    }

    private static string? StripHtml(string? s) =>
        s is null ? null : TextUtil.Collapse(Regex.Replace(System.Net.WebUtility.HtmlDecode(s), "<[^>]+>", " "));
}
