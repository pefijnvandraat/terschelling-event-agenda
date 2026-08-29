using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using TerschellingAgenda.Models;
using TerschellingAgenda.Process;

namespace TerschellingAgenda.Collect;

/// <summary>
/// Generieke HTML-extractor voor agendapagina's zonder structured data.
/// Zoekt herhalende blokken die een datum bevatten en leidt daar een activiteit uit af.
/// Verzint niets: een blok zonder herkenbare datum wordt niet als activiteit opgevoerd.
/// </summary>
public static class HtmlEventExtractor
{
    private static readonly string[] CandidateSelectors =
    {
        "[class*='event' i]", "[class*='agenda' i]", "[class*='activiteit' i]",
        "[class*='evenement' i]", "[class*='programma' i]", "[class*='kalender' i]",
        "[class*='calendar' i]", "article", "li[class]", "[class*='card' i]",
        "[class*='listing' i]", "[class*='tribe' i]", "[class*='post' i]", "tr"
    };

    private static readonly Regex ReDateish = new(
        @"\b\d{1,2}\s*(?:jan|feb|mrt|maart|apr|mei|jun|jul|aug|sep|okt|nov|dec)|" +
        @"\b\d{4}-\d{2}-\d{2}\b|\b\d{1,2}[-/]\d{1,2}[-/]\d{2,4}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<RawEvent> Extract(IDocument doc, string pageUrl, string? selectorHint, int? referenceYear)
    {
        var results = new List<RawEvent>();
        var seenText = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var selectors = new List<string>();
        if (!string.IsNullOrWhiteSpace(selectorHint)) selectors.Add(selectorHint);
        selectors.AddRange(CandidateSelectors);

        foreach (var sel in selectors)
        {
            IEnumerable<IElement> nodes;
            try { nodes = doc.QuerySelectorAll(sel); }
            catch { continue; }

            var blocks = nodes
                .Where(n => IsPlausibleBlock(n))
                .ToList();

            if (blocks.Count == 0) continue;

            int addedForSelector = 0;
            foreach (var block in blocks)
            {
                var ev = BuildFromBlock(block, pageUrl, referenceYear);
                if (ev is null) continue;

                var key = TextUtil.Slug(ev.Name) + "|" + (ev.DateText ?? ev.StartIso);
                if (!seenText.Add(key)) continue;

                results.Add(ev);
                addedForSelector++;
                if (results.Count >= 400) return results;
            }

            // Zodra een selector een bruikbare oogst geeft, stoppen we met bredere (ruisgevoelige) selectors.
            if (addedForSelector >= 3) break;
        }

        return results;
    }

    private static bool IsPlausibleBlock(IElement el)
    {
        var text = TextUtil.Collapse(el.TextContent);
        if (text.Length is < 12 or > 1400) return false;
        if (!ReDateish.IsMatch(text)) return false;

        // sla containers over die zelf weer meerdere datum-bevattende kinderen hebben
        int dateChildren = el.Children.Count(c => ReDateish.IsMatch(TextUtil.Collapse(c.TextContent)));
        if (dateChildren > 1) return false;

        return true;
    }

    private static RawEvent? BuildFromBlock(IElement block, string pageUrl, int? referenceYear)
    {
        var text = TextUtil.Collapse(block.TextContent);
        var span = DutchDateParser.ParseDateRange(text, referenceYear);
        if (span is null) return null;

        var name = FindTitle(block, text);
        if (!IsUsableTitle(name)) return null;

        var link = block.QuerySelector("a[href]")?.GetAttribute("href")
                   ?? (block is IHtmlAnchorElement a ? a.GetAttribute("href") : null)
                   ?? block.Closest("a[href]")?.GetAttribute("href");

        var (start, end) = DutchDateParser.ParseTimes(text);

        var ev = new RawEvent
        {
            Method = "html",
            Name = name,
            DateText = text,
            TimeText = text,
            StartIso = span.Start.ToString("yyyy-MM-dd") + (start is not null ? "T" + start.Value.ToString("HH:mm:ss") : ""),
            EndIso = span.End?.ToString("yyyy-MM-dd") + (end is not null && span.End is not null ? "T" + end.Value.ToString("HH:mm:ss") : ""),
            Description = FindDescription(block, name!, text),
            LocationName = FindLocation(block, text),
            Address = TextUtil.FindAddress(text),
            Phone = TextUtil.FindPhone(text),
            Email = TextUtil.FindEmail(text),
            DetailUrl = JsonLdExtractor.AbsUrl(link, pageUrl) ?? pageUrl,
            PriceText = text,
            RawText = text
        };

        if (span.End is not null && string.IsNullOrEmpty(ev.EndIso))
            ev.EndIso = span.End.Value.ToString("yyyy-MM-dd");

        return ev;
    }

    private static readonly Regex ReMetaLabel = new(
        @"^\s*(datum|date|tijd|time|wanneer|waar|locatie|location|adres|prijs|entree|kosten|aanvang|" +
        @"organisator|categorie|type|start|einde|van|tot|info|lees\s+meer|meer\s+info|bekijk|klik|" +
        @"terug|volgende|vorige|home|menu|zoeken|filter|alle\s+evenementen|agenda|" +
        @"vandaag|morgen|gisteren|overmorgen|deze\s+week|volgende\s+week|dit\s+weekend|" +
        @"today|tomorrow|bewerken|edit|delen|share|reserveren|boeken|tickets?)\b\s*[:\-–,]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Titels die eigenlijk een volzin/beschrijving zijn, geen activiteitnaam.</summary>
    private static readonly Regex ReSentenceLike = new(
        @"^(?:met|voor|in|op|de|het|een|dit|deze|tijdens|onder|bij|na|door|" +
        @"with|for|during|the|this|at)\b.{28,}|" +
        @".{45,}\b(?:en|and|of|or|maar|omdat|zodat|waarbij|waaronder|inclusief|" +
        @"including|meer|more)\b.{12,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? FindTitle(IElement block, string fallbackText)
    {
        // Uitsluitend echte titel-elementen; een willekeurige <p> levert beschrijvingen op.
        foreach (var sel in new[]
                 {
                     "h1","h2","h3","h4","h5",
                     "[class*='title' i]","[class*='titel' i]","[class*='naam' i]","[class*='heading' i]",
                     "[class*='summary' i]","a[href] strong","a[href]"
                 })
        {
            var el = block.QuerySelector(sel);
            var t = TextUtil.Collapse(el?.TextContent);
            if (IsUsableTitle(t)) return Clean(t);
        }
        return null;
    }

    /// <summary>
    /// Een bruikbare titel is niet leeg, niet louter een datum/tijd, geen veldlabel
    /// ("Datum: …"), geen volzin-beschrijving en geen samengeklonterde metadata-blob.
    /// </summary>
    private static bool IsUsableTitle(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return false;
        t = TextUtil.Collapse(t);
        if (t.Length is < 4 or > 120) return false;
        if (ReMetaLabel.IsMatch(t)) return false;
        if (LooksLikeOnlyDate(t)) return false;
        if (ReSentenceLike.IsMatch(t)) return false;
        if (t.EndsWith('.') && t.Split(' ').Length > 6) return false;

        // Metadata-blobs missen spaties rond hoofdletters, bijv. "…2026Tijd: 14:30…"
        if (Regex.IsMatch(t, @"\d(?:[A-Z][a-z])")) return false;

        // Minstens twee betekenisvolle woorden, buiten datum/tijd om.
        var wordy = Regex.Replace(t, @"[\d:.\-–—/]+", " ");
        var words = wordy.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2 && w.Any(char.IsLetter)).ToList();
        if (words.Count < 2) return false;

        // Een titel die na verwijdering van maand-/dagnamen niets overhoudt, is geen activiteit.
        var stripped = DutchDateParser.StripWeekdays(wordy);
        stripped = Regex.Replace(stripped,
            @"\b(jan|feb|mrt|maart|apr|mei|jun|juni|jul|juli|aug|augustus|sep|sept|september|okt|oktober|nov|november|dec|december|januari|februari|april|" +
            @"january|february|march|may|june|july|august|october|december|" +
            @"uur|t/m|tot|en|van|de|het|een|gehele|dag|hele)\b",
            " ", RegexOptions.IgnoreCase);
        return TextUtil.Collapse(stripped).Length >= 4;
    }

    private static bool LooksLikeOnlyDate(string s)
    {
        var stripped = DutchDateParser.StripWeekdays(s);
        stripped = ReDateish.Replace(stripped, " ");
        stripped = Regex.Replace(stripped, @"\b(uur|t/m|tot en met|tot|en)\b", " ", RegexOptions.IgnoreCase);
        stripped = Regex.Replace(stripped, @"[\d\s:.\-–—/]+", " ");
        return TextUtil.Collapse(stripped).Length < 4;
    }

    private static string Clean(string s)
    {
        s = Regex.Replace(s, @"^\s*(?:lees meer|meer info|bekijk)\s*[:\-–]?\s*", "", RegexOptions.IgnoreCase);
        return TextUtil.Collapse(s).Trim(' ', '-', '–', '|', '•', ':');
    }

    private static string? FindDescription(IElement block, string title, string fullText)
    {
        foreach (var sel in new[]
                 {
                     "[class*='description' i]","[class*='beschrijving' i]","[class*='intro' i]",
                     "[class*='excerpt' i]","[class*='samenvatting' i]","[class*='summary' i]","p"
                 })
        {
            var t = TextUtil.Collapse(block.QuerySelector(sel)?.TextContent);
            if (t.Length >= 25 && !t.Equals(title, StringComparison.OrdinalIgnoreCase))
                return TextUtil.Truncate(t, 800);
        }

        var rest = fullText;
        int i = rest.IndexOf(title, StringComparison.OrdinalIgnoreCase);
        if (i >= 0) rest = rest.Remove(i, title.Length);
        rest = TextUtil.Collapse(rest);
        return rest.Length >= 30 ? TextUtil.Truncate(rest, 800) : null;
    }

    private static string? FindLocation(IElement block, string text)
    {
        foreach (var sel in new[]
                 {
                     "[class*='location' i]","[class*='locatie' i]","[class*='venue' i]",
                     "[class*='plaats' i]","[class*='where' i]","[class*='address' i]","[class*='adres' i]"
                 })
        {
            var t = TextUtil.Collapse(block.QuerySelector(sel)?.TextContent);
            if (t.Length is >= 2 and <= 140) return t;
        }

        var m = Regex.Match(text, @"\b(?:locatie|plaats|waar|adres)\s*[:\-]\s*(?<v>[^|•\n]{2,120})",
            RegexOptions.IgnoreCase);
        return m.Success ? TextUtil.Collapse(m.Groups["v"].Value) : null;
    }

    /// <summary>Zoekt op een overzichtspagina naar links die waarschijnlijk detailpagina's van activiteiten zijn.</summary>
    public static List<string> FindDetailLinks(IDocument doc, string pageUrl, int max)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Uri? baseUri = Uri.TryCreate(pageUrl, UriKind.Absolute, out var b) ? b : null;

        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href");
            var abs = JsonLdExtractor.AbsUrl(href, pageUrl);
            if (abs is null || !seen.Add(abs)) continue;
            if (!Uri.TryCreate(abs, UriKind.Absolute, out var u)) continue;
            if (baseUri is not null && !u.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase)) continue;
            if (u.AbsolutePath.Length < 6) continue;

            var path = u.AbsolutePath.ToLowerInvariant();
            if (Regex.IsMatch(path, @"\.(jpg|jpeg|png|gif|svg|pdf|zip|mp4|webp|ico|css|js)$")) continue;
            if (Regex.IsMatch(path, @"/(wp-admin|wp-login|privacy|cookie|contact|nieuwsbrief|sitemap|tag|author|feed)")) continue;

            var linkText = TextUtil.Collapse(a.TextContent);
            bool pathHints = Regex.IsMatch(path,
                @"(event|agenda|activiteit|evenement|programma|voorstelling|concert|expositie|workshop|excursie|kalender|uitagenda|/\d{4}/\d{2})");
            bool textHints = ReDateish.IsMatch(linkText) || linkText.Length > 12;

            if (pathHints && textHints) found.Add(abs);
            if (found.Count >= max) break;
        }
        return found;
    }
}
