using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using TerschellingAgenda.Models;
using TerschellingAgenda.Process;

namespace TerschellingAgenda.Collect;

/// <summary>Een ruw geëxtraheerd item, vóór normalisatie.</summary>
public sealed class RawEvent
{
    public string? Name { get; set; }
    public string? DateText { get; set; }
    public string? StartIso { get; set; }
    public string? EndIso { get; set; }
    public string? TimeText { get; set; }
    public string? Description { get; set; }
    public string? LocationName { get; set; }
    public string? Address { get; set; }
    public string? Organizer { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string? PriceText { get; set; }
    public string? TicketUrl { get; set; }
    public string? DetailUrl { get; set; }
    public string? RawText { get; set; }
    public string Method { get; set; } = "html";
    public List<string> Categories { get; set; } = new();
}

public static class JsonLdExtractor
{
    /// <summary>Haalt schema.org Event-objecten uit alle ld+json-blokken.</summary>
    public static List<RawEvent> Extract(IDocument doc, string pageUrl)
    {
        var results = new List<RawEvent>();
        foreach (var script in doc.QuerySelectorAll("script[type='application/ld+json']"))
        {
            var json = script.TextContent?.Trim();
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                using var d = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
                WalkNode(d.RootElement, results, pageUrl, 0);
            }
            catch (JsonException) { /* ongeldige JSON-LD: overslaan, run gaat door */ }
        }
        return results;
    }

    private static void WalkNode(JsonElement el, List<RawEvent> results, string pageUrl, int depth)
    {
        if (depth > 8) return;
        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) WalkNode(item, results, pageUrl, depth + 1);
                break;
            case JsonValueKind.Object:
                if (IsEventType(el))
                {
                    var ev = MapEvent(el, pageUrl);
                    if (ev is not null) results.Add(ev);
                }
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.NameEquals("@context")) continue;
                    WalkNode(prop.Value, results, pageUrl, depth + 1);
                }
                break;
        }
    }

    private static bool IsEventType(JsonElement obj)
    {
        if (!obj.TryGetProperty("@type", out var t)) return false;
        static bool Match(string s) =>
            s.Equals("Event", StringComparison.OrdinalIgnoreCase) ||
            (s.EndsWith("Event", StringComparison.OrdinalIgnoreCase) && s.Length > 5);
        if (t.ValueKind == JsonValueKind.String) return Match(t.GetString() ?? "");
        if (t.ValueKind == JsonValueKind.Array)
            return t.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && Match(x.GetString() ?? ""));
        return false;
    }

    private static RawEvent? MapEvent(JsonElement e, string pageUrl)
    {
        var name = Str(e, "name");
        if (string.IsNullOrWhiteSpace(name)) return null;

        var ev = new RawEvent
        {
            Method = "jsonld",
            Name = name,
            StartIso = Str(e, "startDate"),
            EndIso = Str(e, "endDate"),
            Description = Str(e, "description"),
            DetailUrl = AbsUrl(Str(e, "url"), pageUrl) ?? pageUrl
        };

        if (e.TryGetProperty("location", out var loc)) ReadLocation(loc, ev, pageUrl);
        if (e.TryGetProperty("organizer", out var org)) ReadOrganizer(org, ev, pageUrl);
        if (e.TryGetProperty("performer", out var perf) && string.IsNullOrWhiteSpace(ev.Organizer))
            ev.Organizer = FirstName(perf);
        if (e.TryGetProperty("offers", out var offers)) ReadOffers(offers, ev, pageUrl);

        if (e.TryGetProperty("eventStatus", out var st) && st.ValueKind == JsonValueKind.String)
        {
            var s = st.GetString() ?? "";
            if (s.Contains("Cancelled", StringComparison.OrdinalIgnoreCase)) return null;
        }

        // categorieën uit schema.org
        foreach (var key in new[] { "keywords", "genre", "about", "eventAttendanceMode" })
        {
            var v = Str(e, key);
            if (!string.IsNullOrWhiteSpace(v)) ev.Categories.Add(v);
        }

        ev.RawText = string.Join(" \n ", new[]
        {
            ev.Name, ev.Description, ev.LocationName, ev.Address, ev.Organizer, ev.PriceText
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return ev;
    }

    private static void ReadLocation(JsonElement loc, RawEvent ev, string pageUrl)
    {
        if (loc.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in loc.EnumerateArray()) { ReadLocation(l, ev, pageUrl); if (ev.LocationName is not null) return; }
            return;
        }
        if (loc.ValueKind == JsonValueKind.String) { ev.LocationName = loc.GetString(); return; }
        if (loc.ValueKind != JsonValueKind.Object) return;

        ev.LocationName ??= Str(loc, "name");
        ev.Phone ??= Str(loc, "telephone");
        ev.Email ??= Str(loc, "email");

        if (loc.TryGetProperty("address", out var addr))
        {
            if (addr.ValueKind == JsonValueKind.String) ev.Address = addr.GetString();
            else if (addr.ValueKind == JsonValueKind.Object)
            {
                var parts = new[] { "streetAddress", "postalCode", "addressLocality", "addressRegion" }
                    .Select(k => Str(addr, k))
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
                if (parts.Count > 0) ev.Address = string.Join(", ", parts);
            }
        }
    }

    private static void ReadOrganizer(JsonElement org, RawEvent ev, string pageUrl)
    {
        if (org.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in org.EnumerateArray()) { ReadOrganizer(o, ev, pageUrl); if (ev.Organizer is not null) return; }
            return;
        }
        if (org.ValueKind == JsonValueKind.String) { ev.Organizer = org.GetString(); return; }
        if (org.ValueKind != JsonValueKind.Object) return;

        ev.Organizer ??= Str(org, "name");
        ev.Phone ??= Str(org, "telephone");
        ev.Email ??= Str(org, "email");
        ev.Website ??= AbsUrl(Str(org, "url"), pageUrl);
    }

    private static void ReadOffers(JsonElement offers, RawEvent ev, string pageUrl)
    {
        if (offers.ValueKind == JsonValueKind.Array)
        {
            var prices = new List<string>();
            string? url = null;
            foreach (var o in offers.EnumerateArray())
            {
                var (p, u) = ReadOffer(o, pageUrl);
                if (p is not null) prices.Add(p);
                url ??= u;
            }
            if (prices.Count > 0)
                ev.PriceText = prices.Distinct().Count() == 1
                    ? FormatPrice(prices[0])
                    : $"{FormatPrice(prices.Min()!)} – {FormatPrice(prices.Max()!)}";
            ev.TicketUrl ??= url;
            return;
        }
        var (price, turl) = ReadOffer(offers, pageUrl);
        if (price is not null) ev.PriceText = FormatPrice(price);
        ev.TicketUrl ??= turl;
    }

    private static (string? Price, string? Url) ReadOffer(JsonElement o, string pageUrl)
    {
        if (o.ValueKind != JsonValueKind.Object) return (null, null);
        string? price = null;
        if (o.TryGetProperty("price", out var p))
            price = p.ValueKind == JsonValueKind.Number ? p.GetRawText() : p.GetString();
        if (string.IsNullOrWhiteSpace(price) && o.TryGetProperty("lowPrice", out var lp))
            price = lp.ValueKind == JsonValueKind.Number ? lp.GetRawText() : lp.GetString();
        return (string.IsNullOrWhiteSpace(price) ? null : price, AbsUrl(Str(o, "url"), pageUrl));
    }

    private static string FormatPrice(string raw)
    {
        if (decimal.TryParse(raw.Replace(',', '.'), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d == 0 ? "Gratis" : "€ " + d.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("nl-NL"));
        return raw;
    }

    private static string? FirstName(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String) return e.GetString();
        if (e.ValueKind == JsonValueKind.Object) return Str(e, "name");
        if (e.ValueKind == JsonValueKind.Array)
            foreach (var i in e.EnumerateArray()) { var n = FirstName(i); if (n is not null) return n; }
        return null;
    }

    private static string? Str(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => TextUtil.Collapse(v.GetString()) is { Length: > 0 } s ? s : null,
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.Array => v.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)),
            JsonValueKind.Object => v.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() : null,
            _ => null
        };
    }

    internal static string? AbsUrl(string? url, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs)) return abs.ToString();
        if (Uri.TryCreate(new Uri(baseUrl), url, out var rel)) return rel.ToString();
        return null;
    }
}

/// <summary>schema.org microdata (itemscope/itemprop) fallback.</summary>
public static class MicrodataExtractor
{
    public static List<RawEvent> Extract(IDocument doc, string pageUrl)
    {
        var results = new List<RawEvent>();
        foreach (var scope in doc.QuerySelectorAll("[itemscope][itemtype*='Event' i]"))
        {
            string? Prop(string name)
            {
                var el = scope.QuerySelector($"[itemprop='{name}' i]");
                if (el is null) return null;
                var content = el.GetAttribute("content") ?? el.GetAttribute("datetime");
                return TextUtil.Collapse(!string.IsNullOrWhiteSpace(content) ? content : el.TextContent);
            }

            var name = Prop("name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var link = scope.QuerySelector("a[href]")?.GetAttribute("href");
            results.Add(new RawEvent
            {
                Method = "microdata",
                Name = name,
                StartIso = Prop("startDate"),
                EndIso = Prop("endDate"),
                Description = Prop("description"),
                LocationName = Prop("location"),
                Address = Prop("address"),
                Organizer = Prop("organizer"),
                PriceText = Prop("price"),
                DetailUrl = JsonLdExtractor.AbsUrl(link, pageUrl) ?? pageUrl,
                RawText = TextUtil.Collapse(scope.TextContent)
            });
        }
        return results;
    }
}
