using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TerschellingAgenda.Process;

/// <summary>Parsing van Nederlandse datum- en tijdnotaties zoals die op lokale websites voorkomen.</summary>
public static partial class DutchDateParser
{
    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["januari"] = 1, ["january"] = 1,
        ["feb"] = 2, ["februari"] = 2, ["february"] = 2,
        ["mrt"] = 3, ["maart"] = 3, ["mar"] = 3, ["march"] = 3,
        ["apr"] = 4, ["april"] = 4,
        ["mei"] = 5, ["may"] = 5,
        ["jun"] = 6, ["juni"] = 6, ["june"] = 6,
        ["jul"] = 7, ["juli"] = 7, ["july"] = 7,
        ["aug"] = 8, ["augustus"] = 8, ["august"] = 8,
        ["sep"] = 9, ["sept"] = 9, ["september"] = 9,
        ["okt"] = 10, ["oktober"] = 10, ["oct"] = 10, ["october"] = 10,
        ["nov"] = 11, ["november"] = 11,
        ["dec"] = 12, ["december"] = 12
    };

    private static readonly string[] WeekDays =
    {
        "maandag","dinsdag","woensdag","donderdag","vrijdag","zaterdag","zondag",
        "ma","di","wo","do","vr","za","zo"
    };

    private static readonly string MonthAlt = string.Join("|", Months.Keys.OrderByDescending(k => k.Length));

    // 12 juni 2026 / 12 juni / 12-06-2026 / 2026-06-12 / 12/06/2026
    private static readonly Regex ReTextual =
        new(@"\b(?<d>\d{1,2})\s*(?:e|de|ste)?\s+(?<m>" + MonthAlt + @")\.?\s*(?<y>\d{4})?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReIso =
        new(@"\b(?<y>\d{4})-(?<m>\d{1,2})-(?<d>\d{1,2})\b", RegexOptions.Compiled);

    private static readonly Regex ReNumeric =
        new(@"\b(?<d>\d{1,2})[-/.](?<m>\d{1,2})[-/.](?<y>\d{2,4})\b", RegexOptions.Compiled);

    // "12 t/m 15 juni 2026", "12 - 15 juni", "12 tot en met 15 juni"
    private static readonly Regex ReRangeSameMonth =
        new(@"\b(?<d1>\d{1,2})\s*(?:t/m|tot en met|tm|–|—|-|/)\s*(?<d2>\d{1,2})\s+(?<m>" + MonthAlt + @")\.?\s*(?<y>\d{4})?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "30 juni t/m 3 juli 2026"
    private static readonly Regex ReRangeCrossMonth =
        new(@"\b(?<d1>\d{1,2})\s+(?<m1>" + MonthAlt + @")\.?\s*(?<y1>\d{4})?\s*(?:t/m|tot en met|tm|–|—|-)\s*(?<d2>\d{1,2})\s+(?<m2>" + MonthAlt + @")\.?\s*(?<y2>\d{4})?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 20.00 uur / 20:00 / 20u30 / 8 uur / 20.00 - 23.00
    private static readonly Regex ReTime =
        new(@"\b(?<h>[0-2]?\d)\s*(?:[:.uU]\s*(?<mi>[0-5]\d))?\s*(?:uur|u\.?|hrs?)?\b", RegexOptions.Compiled);

    private static readonly Regex ReTimeRange =
        new(@"\b(?<h1>[0-2]?\d)[:.u](?<mi1>[0-5]\d)\s*(?:-|–|—|tot|t/m|until|/)\s*(?<h2>[0-2]?\d)[:.u](?<mi2>[0-5]\d)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReStrictTime =
        new(@"\b(?<h>[0-2]?\d)[:.u](?<mi>[0-5]\d)\b", RegexOptions.Compiled);

    /// <summary>Vindt de eerste datum in een tekst. referenceYear vult ontbrekende jaartallen aan.</summary>
    public static DateOnly? ParseDate(string? text, int? referenceYear = null)
        => ParseDateRange(text, referenceYear)?.Start;

    public sealed record DateSpan(DateOnly Start, DateOnly? End);

    public static DateSpan? ParseDateRange(string? text, int? referenceYear = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = Normalize(text);
        int year = referenceYear ?? DateTime.Now.Year;

        // ISO eerst — meest betrouwbaar
        var iso = ReIso.Match(text);
        if (iso.Success && TryMake(int.Parse(iso.Groups["y"].Value), int.Parse(iso.Groups["m"].Value),
                                   int.Parse(iso.Groups["d"].Value), out var isoDate))
        {
            // eventueel tweede ISO-datum = einddatum
            var iso2 = ReIso.Match(text, iso.Index + iso.Length);
            if (iso2.Success && TryMake(int.Parse(iso2.Groups["y"].Value), int.Parse(iso2.Groups["m"].Value),
                                        int.Parse(iso2.Groups["d"].Value), out var isoEnd) && isoEnd >= isoDate)
                return new DateSpan(isoDate, isoEnd == isoDate ? null : isoEnd);
            return new DateSpan(isoDate, null);
        }

        var cross = ReRangeCrossMonth.Match(text);
        if (cross.Success)
        {
            int m1 = Months[cross.Groups["m1"].Value];
            int m2 = Months[cross.Groups["m2"].Value];
            int y2 = ParseYear(cross.Groups["y2"].Value, year);
            int y1 = ParseYear(cross.Groups["y1"].Value, m1 > m2 ? y2 - 1 : y2);
            if (TryMake(y1, m1, int.Parse(cross.Groups["d1"].Value), out var s) &&
                TryMake(y2, m2, int.Parse(cross.Groups["d2"].Value), out var e))
                return new DateSpan(s, e > s ? e : null);
        }

        var same = ReRangeSameMonth.Match(text);
        if (same.Success)
        {
            int m = Months[same.Groups["m"].Value];
            int y = ParseYear(same.Groups["y"].Value, year);
            if (TryMake(y, m, int.Parse(same.Groups["d1"].Value), out var s) &&
                TryMake(y, m, int.Parse(same.Groups["d2"].Value), out var e))
                return new DateSpan(s, e > s ? e : null);
        }

        var t = ReTextual.Match(text);
        if (t.Success)
        {
            int m = Months[t.Groups["m"].Value];
            int y = ParseYear(t.Groups["y"].Value, year);
            if (TryMake(y, m, int.Parse(t.Groups["d"].Value), out var d))
            {
                var t2 = ReTextual.Match(text, t.Index + t.Length);
                if (t2.Success)
                {
                    int m2 = Months[t2.Groups["m"].Value];
                    int y2 = ParseYear(t2.Groups["y"].Value, y);
                    if (TryMake(y2, m2, int.Parse(t2.Groups["d"].Value), out var d2) && d2 > d &&
                        (d2.DayNumber - d.DayNumber) <= 120)
                        return new DateSpan(d, d2);
                }
                return new DateSpan(d, null);
            }
        }

        var n = ReNumeric.Match(text);
        if (n.Success)
        {
            int y = int.Parse(n.Groups["y"].Value);
            if (y < 100) y += 2000;
            if (TryMake(y, int.Parse(n.Groups["m"].Value), int.Parse(n.Groups["d"].Value), out var d))
                return new DateSpan(d, null);
        }

        // "12 juni" zonder jaar wordt door ReTextual gedekt; dag-zonder-maand is te onbetrouwbaar => niet raden.
        return null;
    }

    private static int ParseYear(string raw, int fallback)
        => int.TryParse(raw, out var y) && y > 1900 && y < 2200 ? y : fallback;

    private static bool TryMake(int y, int m, int d, out DateOnly date)
    {
        date = default;
        if (y is < 1900 or > 2200 || m is < 1 or > 12 || d < 1) return false;
        if (d > DateTime.DaysInMonth(y, m)) return false;
        date = new DateOnly(y, m, d);
        return true;
    }

    /// <summary>Vindt (start, eind) tijd in een tekst. Alleen expliciete notaties — nooit raden.</summary>
    public static (TimeOnly? Start, TimeOnly? End) ParseTimes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        text = Normalize(text);

        var r = ReTimeRange.Match(text);
        if (r.Success)
        {
            var s = MakeTime(int.Parse(r.Groups["h1"].Value), int.Parse(r.Groups["mi1"].Value));
            var e = MakeTime(int.Parse(r.Groups["h2"].Value), int.Parse(r.Groups["mi2"].Value));
            if (s is not null) return (s, e);
        }

        // "van 20.00 tot 23.00 uur" met losse notatie, of "20.00 uur"
        var matches = ReStrictTime.Matches(text)
            .Select(m => MakeTime(int.Parse(m.Groups["h"].Value), int.Parse(m.Groups["mi"].Value)))
            .Where(t => t is not null).Select(t => t!.Value).ToList();

        if (matches.Count >= 2)
        {
            // alleen als er een expliciet bereikwoord tussen staat
            if (Regex.IsMatch(text, @"\b(tot|t/m|—|–|-|until)\b", RegexOptions.IgnoreCase))
                return (matches[0], matches[1]);
            return (matches[0], null);
        }
        if (matches.Count == 1) return (matches[0], null);

        // "om 20 uur"
        var loose = Regex.Match(text, @"\b(?:om|vanaf|start(?:t)?(?:\s+om)?|aanvang)\s+(?<h>[0-2]?\d)\s*uur\b",
            RegexOptions.IgnoreCase);
        if (loose.Success)
        {
            var s = MakeTime(int.Parse(loose.Groups["h"].Value), 0);
            if (s is not null) return (s, null);
        }
        return (null, null);
    }

    private static TimeOnly? MakeTime(int h, int mi)
        => h is >= 0 and <= 23 && mi is >= 0 and <= 59 ? new TimeOnly(h, mi) : null;

    /// <summary>Parseert een ISO 8601 datum/tijd (schema.org startDate) inclusief timezone-varianten.</summary>
    public static (DateOnly? Date, TimeOnly? Time) ParseIso(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, null);
        value = value.Trim();

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var dto))
        {
            bool hasTime = value.Contains('T') || value.Contains(':');
            return (DateOnly.FromDateTime(dto.DateTime), hasTime ? TimeOnly.FromDateTime(dto.DateTime) : null);
        }
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return (d, null);
        return (null, null);
    }

    public static string Normalize(string s)
    {
        s = s.Replace('\u00a0', ' ').Replace('\u2011', '-').Replace('\u2013', '-').Replace('\u2014', '-');
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>Verwijdert weekdagnamen zodat "zaterdag 12 juni" ook schoon te matchen is.</summary>
    public static string StripWeekdays(string s)
    {
        foreach (var w in WeekDays)
            s = Regex.Replace(s, $@"\b{w}\b\.?", " ", RegexOptions.IgnoreCase);
        return Normalize(s);
    }

    public static string ToDutch(DateOnly d)
    {
        var nl = CultureInfo.GetCultureInfo("nl-NL");
        return d.ToString("dddd d MMMM yyyy", nl);
    }
}

public static class TextUtil
{
    /// <summary>
    /// Normaliseert witruimte en zet HTML-entiteiten om naar gewone tekens.
    /// JSON-velden zoals WordPress' <c>title.rendered</c> bevatten letterlijk "&amp;#039;";
    /// zonder deze stap belandt dat in de activiteitnaam en mislukt het samenvoegen.
    /// </summary>
    public static string Collapse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        if (s.Contains('&')) s = System.Net.WebUtility.HtmlDecode(s);
        return Regex.Replace(s.Replace('\u00a0', ' '), @"\s+", " ").Trim();
    }

    public static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max].TrimEnd() + "…";

    /// <summary>Diacriticloze, lowercase, alfanumerieke sleutel voor vergelijking.</summary>
    public static string Slug(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var norm = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        foreach (var ch in norm)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch is ' ' or '-' or '_' or '\'' or '’') sb.Append(' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>Jaccard-overeenkomst op woordniveau (0..1).</summary>
    public static double TokenSimilarity(string a, string b)
    {
        var sa = Slug(a).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var sb = Slug(b).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (sa.Count == 0 || sb.Count == 0) return 0;
        int inter = sa.Intersect(sb).Count();
        return (double)inter / (sa.Count + sb.Count - inter);
    }

    /// <summary>Verwijdert losstaande jaartallen ("Oerol 2026" → "Oerol").</summary>
    public static string StripYears(string? s) =>
        Collapse(Regex.Replace(s ?? "", @"\b(?:19|20)\d{2}\b", " "));

    /// <summary>Horen twee losse woorden bij elkaar? Tolerant voor meervoud en samenstellingen.</summary>
    private static bool TokensMatch(string a, string b)
    {
        if (a == b) return true;
        if (a.Length < 5 || b.Length < 5) return false;
        if (a.Contains(b) || b.Contains(a)) return true;
        return Similarity(a, b) >= 0.80;
    }

    /// <summary>
    /// Woordovereenkomst die meervoud en schrijfwijzeverschillen verdraagt, en
    /// ongevoelig is voor woordvolgorde: "Demonstratie paardenreddingboot" en
    /// "Paardenroeireddingboot demonstraties" tellen als dezelfde naam.
    /// </summary>
    public static double FuzzyTokenSimilarity(string a, string b)
    {
        var sa = Slug(StripYears(a)).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var sb = Slug(StripYears(b)).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (sa.Count == 0 || sb.Count == 0) return 0;

        var free = new List<string>(sb);
        int matched = 0;
        foreach (var t in sa)
        {
            int i = free.FindIndex(o => TokensMatch(t, o));
            if (i < 0) continue;
            free.RemoveAt(i);
            matched++;
        }
        return (double)matched / (sa.Count + sb.Count - matched);
    }

    /// <summary>
    /// Zit de kortste naam volledig in de langste ("Beestemerk" in "Veemarkt Beestenmerk")?
    /// Levert een middelmatige score op: op zichzelf niet genoeg om samen te voegen,
    /// maar samen met dezelfde datum en plaats wel een sterke aanwijzing.
    /// </summary>
    public static double ContainmentScore(string a, string b)
    {
        var sa = Slug(StripYears(a)).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var sb = Slug(StripYears(b)).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (sa.Count == 0 || sb.Count == 0) return 0;

        var (shorter, longer) = sa.Count <= sb.Count ? (sa, sb) : (sb, sa);
        if (shorter.Sum(t => t.Length) < 8) return 0;

        var free = new List<string>(longer);
        foreach (var t in shorter)
        {
            int i = free.FindIndex(o => TokensMatch(t, o));
            if (i < 0) return 0;
            free.RemoveAt(i);
        }
        return 0.75;
    }

    /// <summary>Genormaliseerde Levenshtein-gelijkenis (0..1).</summary>
    public static double Similarity(string a, string b)
    {
        a = Slug(a); b = Slug(b);
        if (a.Length == 0 && b.Length == 0) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        int[] prev = new int[b.Length + 1], cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return 1.0 - (double)prev[b.Length] / Math.Max(a.Length, b.Length);
    }

    private static readonly Regex ReEmail = new(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled);
    private static readonly Regex RePhone = new(
        @"(?<!\d)(?:\+31[\s\-]?\(?0?\)?[\s\-]?|0)(?:\d[\s\-]?){8,10}\d(?!\d)", RegexOptions.Compiled);
    private static readonly Regex ReAddress = new(
        @"\b([A-ZÀ-Ý][\w'’\-]*(?:\s+[A-ZÀ-Ýa-zà-ÿ][\w'’\-]*){0,3}(?:straat|laan|weg|plein|dijk|pad|kade|singel|dwarsstraat|buren|wal|hoek|steeg|gracht))\s+(\d+\s?[a-zA-Z]?)\b",
        RegexOptions.Compiled);
    private static readonly Regex RePostcode = new(@"\b\d{4}\s?[A-Z]{2}\b", RegexOptions.Compiled);

    public static string? FindEmail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = ReEmail.Match(text);
        if (!m.Success) return null;
        var v = m.Value;
        // filter placeholders / afbeeldingsnamen
        if (v.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            v.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            v.Contains("example.", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("sentry", StringComparison.OrdinalIgnoreCase)) return null;
        return v;
    }

    public static string? FindPhone(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (Match m in RePhone.Matches(text))
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (digits.Length is >= 9 and <= 12) return Collapse(m.Value);
        }
        return null;
    }

    public static string? FindAddress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = ReAddress.Match(text);
        if (!m.Success) return null;
        var addr = Collapse(m.Value);
        var pc = RePostcode.Match(text, Math.Min(m.Index + m.Length, text.Length - 1));
        if (pc.Success && pc.Index - (m.Index + m.Length) < 30) addr += ", " + pc.Value;
        return addr;
    }
}
