using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TerschellingAgenda.Process;

namespace TerschellingAgenda.Collect;

/// <summary>Parser voor iCalendar (.ics) feeds — de meest betrouwbare bron als een site die aanbiedt.</summary>
public static class IcsExtractor
{
    public static List<RawEvent> Extract(string ics, string sourceUrl)
    {
        var results = new List<RawEvent>();
        if (string.IsNullOrWhiteSpace(ics) || !ics.Contains("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            return results;

        foreach (var block in SplitEvents(Unfold(ics)))
        {
            var props = ParseProps(block);
            if (!props.TryGetValue("SUMMARY", out var summary) || string.IsNullOrWhiteSpace(summary.Value))
                continue;
            if (props.TryGetValue("STATUS", out var status) &&
                status.Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase))
                continue;

            var ev = new RawEvent
            {
                Method = "ics",
                Name = Unescape(summary.Value),
                Description = props.TryGetValue("DESCRIPTION", out var d) ? Unescape(d.Value) : null,
                LocationName = props.TryGetValue("LOCATION", out var l) ? Unescape(l.Value) : null,
                Organizer = props.TryGetValue("ORGANIZER", out var o) ? CleanOrganizer(o) : null,
                DetailUrl = props.TryGetValue("URL", out var u) && !string.IsNullOrWhiteSpace(u.Value)
                    ? u.Value : sourceUrl
            };

            if (props.TryGetValue("DTSTART", out var dtstart)) ev.StartIso = IcsDateToIso(dtstart);
            if (props.TryGetValue("DTEND", out var dtend)) ev.EndIso = IcsDateToIso(dtend);

            if (props.TryGetValue("CATEGORIES", out var cats) && !string.IsNullOrWhiteSpace(cats.Value))
                ev.Categories.AddRange(cats.Value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(c => c.Trim()));

            ev.RawText = string.Join(" \n ", new[] { ev.Name, ev.Description, ev.LocationName, ev.Organizer }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

            if (ev.StartIso is not null) results.Add(ev);
        }
        return results;
    }

    private static string Unfold(string ics) =>
        ics.Replace("\r\n ", "").Replace("\r\n\t", "").Replace("\n ", "").Replace("\n\t", "");

    private static IEnumerable<string> SplitEvents(string ics)
    {
        int idx = 0;
        while (true)
        {
            int start = ics.IndexOf("BEGIN:VEVENT", idx, StringComparison.OrdinalIgnoreCase);
            if (start < 0) yield break;
            int end = ics.IndexOf("END:VEVENT", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) yield break;
            yield return ics[start..end];
            idx = end + 10;
        }
    }

    private sealed record Prop(string Value, Dictionary<string, string> Params);

    private static Dictionary<string, Prop> ParseProps(string block)
    {
        var map = new Dictionary<string, Prop>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in block.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var l = line.TrimEnd('\r');
            int colon = l.IndexOf(':');
            if (colon <= 0) continue;
            var left = l[..colon];
            var value = l[(colon + 1)..].Trim();

            var segs = left.Split(';');
            var key = segs[0].Trim().ToUpperInvariant();
            var ps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < segs.Length; i++)
            {
                var kv = segs[i].Split('=', 2);
                if (kv.Length == 2) ps[kv[0].Trim()] = kv[1].Trim('"');
            }
            if (!map.ContainsKey(key)) map[key] = new Prop(value, ps);
        }
        return map;
    }

    private static string? IcsDateToIso(Prop p)
    {
        var v = p.Value.Trim();
        // 20260612T200000Z / 20260612T200000 / 20260612
        var m = Regex.Match(v, @"^(?<y>\d{4})(?<mo>\d{2})(?<d>\d{2})(?:T(?<h>\d{2})(?<mi>\d{2})(?<s>\d{2})(?<z>Z)?)?$");
        if (!m.Success) return DateTimeOffset.TryParse(v, out var dto) ? dto.ToString("o") : null;

        var sb = new StringBuilder($"{m.Groups["y"].Value}-{m.Groups["mo"].Value}-{m.Groups["d"].Value}");
        if (m.Groups["h"].Success)
        {
            sb.Append($"T{m.Groups["h"].Value}:{m.Groups["mi"].Value}:{m.Groups["s"].Value}");
            if (m.Groups["z"].Success) sb.Append('Z');
        }
        return sb.ToString();
    }

    private static string Unescape(string s) =>
        TextUtil.Collapse(s.Replace("\\n", " ").Replace("\\N", " ")
                           .Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\"));

    private static string? CleanOrganizer(Prop p)
    {
        if (p.Params.TryGetValue("CN", out var cn) && !string.IsNullOrWhiteSpace(cn)) return cn;
        var v = p.Value;
        return v.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? v[7..] : (string.IsNullOrWhiteSpace(v) ? null : v);
    }
}

/// <summary>RSS 2.0 / Atom feed parser.</summary>
public static class RssExtractor
{
    public static List<RawEvent> Extract(string xml, string sourceUrl)
    {
        var results = new List<RawEvent>();
        XDocument doc;
        try { doc = XDocument.Parse(xml, LoadOptions.None); }
        catch { return results; }

        XNamespace atom = "http://www.w3.org/2005/Atom";

        foreach (var item in doc.Descendants().Where(e =>
                     e.Name.LocalName is "item" or "entry"))
        {
            string? Get(string n) => TextUtil.Collapse(
                item.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(n, StringComparison.OrdinalIgnoreCase))?.Value);

            var title = Get("title");
            if (string.IsNullOrWhiteSpace(title)) continue;

            var link = item.Elements().FirstOrDefault(e => e.Name.LocalName == "link");
            var href = link?.Attribute("href")?.Value ?? TextUtil.Collapse(link?.Value);

            var desc = Get("description") ?? Get("summary") ?? Get("content");
            desc = StripHtml(desc);

            var ev = new RawEvent
            {
                Method = "rss",
                Name = title,
                Description = desc,
                DetailUrl = JsonLdExtractor.AbsUrl(href, sourceUrl) ?? sourceUrl,
                DateText = $"{title} {desc}",
                RawText = $"{title} \n {desc}"
            };
            results.Add(ev);
        }
        return results;
    }

    private static string? StripHtml(string? s) =>
        s is null ? null : TextUtil.Collapse(Regex.Replace(System.Net.WebUtility.HtmlDecode(s), "<[^>]+>", " "));
}
