namespace TerschellingAgenda.Models;

/// <summary>
/// Bepaalt welke wébsite achter een bronvermelding zit.
///
/// Dat is iets anders dan het bron-id: pagina's die via de zoekmachine worden gevonden
/// krijgen een id per pagina ("discovery:&lt;domein&gt;:&lt;hash&gt;"), en één geregistreerde bron
/// levert vaak zowel een overzichtspagina als losse detailpagina's op. Zulke vermeldingen
/// zijn géén onafhankelijke bevestiging van elkaar en kunnen elkaar dus ook niet tegenspreken.
/// </summary>
public sealed class OriginResolver
{
    private readonly Dictionary<string, string> _parent = new(StringComparer.Ordinal);

    /// <summary>Bouwt de indeling op basis van alle bronvermeldingen van één activiteit.</summary>
    public OriginResolver(IEnumerable<EventSourceRef> sources)
    {
        foreach (var s in sources) Register(s);
    }

    /// <summary>Naam van de website waartoe deze vermelding hoort. Leeg als die niet te bepalen is.</summary>
    public string Resolve(EventSourceRef source)
    {
        var tokens = Register(source);
        return tokens.Count == 0 ? "" : Find(tokens[0]);
    }

    /// <summary>Websites achter een reeks vermeldingen, zonder dubbele.</summary>
    public HashSet<string> ResolveAll(IEnumerable<EventSourceRef> sources)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in sources)
        {
            var origin = Resolve(s);
            if (origin.Length > 0) set.Add(origin);
        }
        return set;
    }

    private List<string> Register(EventSourceRef source)
    {
        var tokens = new List<string>(2);

        var host = NormalisedHost(source.Url);
        if (host.Length > 0) tokens.Add("host:" + host);

        var baseId = RegistryId(source.SourceId);
        if (baseId.Length > 0) tokens.Add("id:" + baseId);

        foreach (var t in tokens)
            if (!_parent.ContainsKey(t)) _parent[t] = t;

        // Domein en bron-id van dezelfde vermelding horen per definitie bij elkaar.
        for (int i = 1; i < tokens.Count; i++) Union(tokens[0], tokens[i]);

        return tokens;
    }

    private string Find(string token)
    {
        if (!_parent.TryGetValue(token, out var parent)) return token;
        while (!string.Equals(parent, token, StringComparison.Ordinal))
        {
            token = parent;
            if (!_parent.TryGetValue(token, out parent)) return token;
        }
        return token;
    }

    private void Union(string a, string b)
    {
        var ra = Find(a);
        var rb = Find(b);
        if (!string.Equals(ra, rb, StringComparison.Ordinal)) _parent[rb] = ra;
    }

    /// <summary>Domeinnaam zonder "www.", in kleine letters.</summary>
    public static string NormalisedHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return "";
        var host = u.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>
    /// Bron-id zonder het pagina-deel. "discovery:oerol-nl:1a2b" wordt "discovery:oerol-nl",
    /// zodat alle via de zoekmachine gevonden pagina's van één site samenvallen.
    /// </summary>
    public static string RegistryId(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return "";
        var id = sourceId.Trim().ToLowerInvariant();
        int first = id.IndexOf(':');
        if (first < 0) return id;
        int second = id.IndexOf(':', first + 1);
        return second > first ? id[..second] : id;
    }
}
