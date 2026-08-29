using System.Text.RegularExpressions;
using TerschellingAgenda.Models;

namespace TerschellingAgenda.Process;

/// <summary>Kent categorieën toe op basis van trefwoorden in naam, beschrijving en locatie.</summary>
public static class Categorizer
{
    public static readonly string[] AllCategories =
    {
        "Muziek","Theater","Cultuur","Festival","Natuur","Excursie","Wandelen","Sport","Markt",
        "Eten & Drinken","Museum","Kunst","Workshop","Kinderen","Familie","Nachtleven","Overig"
    };

    private static readonly Dictionary<string, string[]> Keywords = new()
    {
        ["Muziek"] = new[]
        {
            "muziek","concert","live muziek","livemuziek","optreden","band","dj","zanger","zangeres","koor",
            "orkest","akoestisch","jazz","blues","rock","pop","shanty","klassiek","piano","gitaar","singer",
            "songwriter","fanfare","harmonie","gospel","kerkconcert","luisterconcert","muziekavond","open mic"
        },
        ["Theater"] = new[]
        {
            "theater","toneel","voorstelling","cabaret","kleinkunst","improvisatie","musical","poppenkast",
            "locatietheater","monoloog","try-out","tryout","stand-up","standup","mime"
        },
        ["Cultuur"] = new[]
        {
            "cultuur","cultureel","lezing","literatuur","poëzie","poezie","boekpresentatie","film","bioscoop",
            "documentaire","erfgoed","historisch","verhalen","voordracht","debat","dialezing","dia-avond",
            "ouwe sunderklaas","sunderklaas","traditie"
        },
        ["Festival"] = new[]
        {
            "festival","oerol","springtij","kermis","feestweek","dorpsfeest","evenemententerrein","festivalterrein"
        },
        ["Natuur"] = new[]
        {
            "natuur","vogel","vogels","zeehond","zeehonden","wad","waddenzee","duin","duinen","bos","strand",
            "eb","vloed","getij","staatsbosbeheer","natuurmonumenten","cranberry","boschplaat","landschap",
            "sterrenkijken","nachtvlinder","paddenstoel","flora","fauna","natuurwandeling","vogelexcursie"
        },
        ["Excursie"] = new[]
        {
            "excursie","rondleiding","tour","tocht","safari","boottocht","vaartocht","wadlopen","wadloop",
            "gids","gegidste","rondvaart","expeditie","survival","jutten","jutterstocht","garnalenvissen"
        },
        ["Wandelen"] = new[]
        {
            "wandel","wandeling","wandeltocht","struinen","struintocht","hike","voettocht","avondwandeling",
            "zonsopkomst","zonsondergang wandeling","blotevoetenpad"
        },
        ["Sport"] = new[]
        {
            "sport","wedstrijd","toernooi","loop","hardlopen","marathon","berenloop","fiets","fietstocht",
            "mountainbike","zeilen","zeilrace","surfen","kitesurf","suppen","sup","yoga","fitness","voetbal",
            "volleybal","kaatsen","tennis","zwemmen","triatlon","duatlon","blokarten","paardrijden","training",
            "beachvolleybal","klaverjas","darten","biljart"
        },
        ["Markt"] = new[]
        {
            "markt","braderie","rommelmarkt","boekenmarkt","vlooienmarkt","kunstmarkt","streekmarkt",
            "warenmarkt","kerstmarkt","fair","beurs","snuffelmarkt"
        },
        ["Eten & Drinken"] = new[]
        {
            "diner","dineren","proeverij","borrel","bbq","barbecue","high tea","brunch","lunch","wijn","bier",
            "brouwerij","kookworkshop","culinair","restaurant","eetcafé","eetcafe","pannenkoek","oesters",
            "streekproducten","koffie","taart","foodtruck"
        },
        ["Museum"] = new[]
        {
            "museum","musea","behouden huys","wrakkenmuseum","vuurtoren","brandaris","collectie","permanente"
        },
        ["Kunst"] = new[]
        {
            "kunst","expositie","tentoonstelling","galerie","atelier","schilder","beeldhouw","fotografie",
            "foto-expositie","keramiek","kunstenaar","vernissage","open atelier","kunstroute","installatie"
        },
        ["Workshop"] = new[]
        {
            "workshop","cursus","les","lessen","masterclass","clinic","leren","doe-mee","creatief","knutsel",
            "schilderworkshop","fotografieworkshop","kookles"
        },
        ["Kinderen"] = new[]
        {
            "kinder","kinderen","jeugd","peuter","kleuter","kids","kinderactiviteit","kindervoorstelling",
            "schminken","springkussen","speurtocht","knutselen","kinderdisco","kinderclub"
        },
        ["Familie"] = new[]
        {
            "familie","gezin","voor jong en oud","alle leeftijden","familiedag","gezinsactiviteit","samen"
        },
        ["Nachtleven"] = new[]
        {
            "feest","party","disco","dansen","nachtleven","club","late night","afterparty","dansavond",
            "discotheek","bal","danceclassics","nacht"
        }
    };

    public static List<string> Classify(params string?[] texts)
    {
        var haystack = TextUtil.Slug(string.Join(" \n ", texts.Where(t => !string.IsNullOrWhiteSpace(t))));
        if (haystack.Length == 0) return new List<string>();

        var found = new List<string>();
        foreach (var (cat, words) in Keywords)
        {
            foreach (var w in words)
            {
                var slug = TextUtil.Slug(w);
                if (slug.Length == 0) continue;
                if (Regex.IsMatch(haystack, $@"(?<![a-z0-9]){Regex.Escape(slug)}"))
                {
                    found.Add(cat);
                    break;
                }
            }
        }

        // Festival impliceert Cultuur; Wandelen impliceert vaak Natuur niet automatisch — laat staan.
        if (found.Contains("Festival") && !found.Contains("Cultuur")) found.Add("Cultuur");
        if (found.Contains("Kinderen") && !found.Contains("Familie")) found.Add("Familie");

        if (found.Count == 0) found.Add("Overig");
        return found.Distinct().OrderBy(c => Array.IndexOf(AllCategories, c)).ToList();
    }
}

/// <summary>Bepaalt prijs, gratis/betaald en reserveringsplicht — uitsluitend uit expliciete tekst.</summary>
public static class PriceParser
{
    private static readonly Regex ReAmount =
        new(@"(?:€|EUR\s?)\s?(?<v>\d{1,4}(?:[.,]\d{1,2})?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] FreeWords =
    {
        "gratis","gratis toegang","vrij entree","vrije entree","entree vrij","toegang vrij","vrij toegankelijk",
        "geen entree","kosteloos","free entrance","gratis te bezoeken","toegang gratis","gratis deelname"
    };

    private static readonly string[] DonationWords = { "vrije gift", "donatie", "fooienpot", "hoed rond" };

    public static (string Price, PriceKind Kind) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (ActivityEvent.Unknown, PriceKind.Onbekend);
        var slug = TextUtil.Slug(text);
        var raw = DutchDateParser.Normalize(text);

        var amounts = ReAmount.Matches(raw)
            .Select(m => m.Groups["v"].Value)
            .Where(v => decimal.TryParse(v.Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0)
            .Distinct().ToList();

        bool free = FreeWords.Any(w => slug.Contains(TextUtil.Slug(w)));

        if (amounts.Count > 0)
        {
            var priceText = amounts.Count == 1
                ? $"€ {amounts[0]}"
                : $"€ {amounts.First()} – € {amounts.Last()}";
            if (free) priceText += " (deels gratis)";
            return (priceText, PriceKind.Betaald);
        }

        if (free) return ("Gratis", PriceKind.Gratis);
        if (DonationWords.Any(w => slug.Contains(TextUtil.Slug(w))))
            return ("Vrije gift", PriceKind.Gratis);

        return (ActivityEvent.Unknown, PriceKind.Onbekend);
    }

    private static readonly string[] ReservationYes =
    {
        "reserveren verplicht","reservering verplicht","aanmelden verplicht","vooraf reserveren",
        "vooraf aanmelden","reserveer je plek","reserveren noodzakelijk","tickets verplicht",
        "alleen op reservering","inschrijven verplicht","aanmelding vereist","kaartverkoop","koop je ticket",
        "reserveren gewenst","graag reserveren","reserveer nu","tickets bestellen","meld je aan"
    };

    private static readonly string[] ReservationNo =
    {
        "reserveren niet nodig","geen reservering nodig","zonder aanmelding","vrije inloop","inloop",
        "aanmelden niet nodig","reserveren is niet nodig","gewoon langskomen","geen aanmelding"
    };

    public static Reservation ParseReservation(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Reservation.Onbekend;
        var slug = TextUtil.Slug(text);
        if (ReservationNo.Any(w => slug.Contains(TextUtil.Slug(w)))) return Reservation.Nee;
        if (ReservationYes.Any(w => slug.Contains(TextUtil.Slug(w)))) return Reservation.Ja;
        return Reservation.Onbekend;
    }
}
