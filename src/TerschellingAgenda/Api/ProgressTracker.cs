using System.Diagnostics;
using TerschellingAgenda.Models;

namespace TerschellingAgenda.Api;

/// <summary>
/// Houdt bij hoever de zoekopdracht is. De tellers worden vanuit parallelle taken
/// opgehoogd, dus alle toegang loopt via één slot.
///
/// Het totaalpercentage is gewogen: de stappen duren niet even lang, en een balk die
/// bij elke stap even hard opschiet zou een verkeerd beeld geven van de wachttijd.
/// </summary>
public sealed class ProgressTracker
{
    private sealed class Step
    {
        public string Key = "";
        public string Label = "";
        public string State = "wachten";
        public string Detail = "";
        public int Done;
        public int Total;
        public double Weight;
    }

    private readonly object _lock = new();
    private readonly List<Step> _steps = new();
    private Stopwatch _sw = new();
    private bool _running;
    private bool _finished;

    public const string Sources = "bronnen";
    public const string Search = "zoeken";
    public const string Pages = "paginas";
    public const string Merge = "verwerken";

    /// <summary>Zet de stappen klaar voor een nieuwe zoekopdracht.</summary>
    public void Begin(bool deepSearch)
    {
        lock (_lock)
        {
            _sw = Stopwatch.StartNew();
            _running = true;
            _finished = false;
            _steps.Clear();
            _steps.Add(new Step { Key = Sources, Label = "Geregistreerde bronnen raadplegen", Weight = deepSearch ? 38 : 90 });
            _steps.Add(new Step { Key = Search, Label = "Zoekopdrachten uitvoeren", Weight = deepSearch ? 27 : 0, State = deepSearch ? "wachten" : "overgeslagen" });
            _steps.Add(new Step { Key = Pages, Label = "Gevonden pagina's uitlezen", Weight = deepSearch ? 30 : 0, State = deepSearch ? "wachten" : "overgeslagen" });
            _steps.Add(new Step { Key = Merge, Label = "Samenvoegen en controleren", Weight = deepSearch ? 5 : 10 });
        }
    }

    public void Start(string key, int total, string detail = "")
    {
        lock (_lock)
        {
            var s = Find(key);
            if (s is null || s.State == "overgeslagen") return;
            s.State = "bezig";
            s.Total = total;
            s.Done = 0;
            s.Detail = detail;
        }
    }

    public void Advance(string key, int by = 1)
    {
        lock (_lock)
        {
            var s = Find(key);
            if (s is null || s.State == "overgeslagen") return;
            s.Done += by;
            if (s.Total > 0 && s.Done > s.Total) s.Done = s.Total;
        }
    }

    public void Detail(string key, string detail)
    {
        lock (_lock)
        {
            var s = Find(key);
            if (s is not null && s.State != "overgeslagen") s.Detail = detail;
        }
    }

    public void Complete(string key)
    {
        lock (_lock)
        {
            var s = Find(key);
            if (s is null || s.State == "overgeslagen") return;
            s.State = "klaar";
            if (s.Total > 0) s.Done = s.Total;
        }
    }

    /// <summary>Markeert een stap als niet van toepassing; die telt niet mee in het totaal.</summary>
    public void Skip(string key, string reason = "")
    {
        lock (_lock)
        {
            var s = Find(key);
            if (s is null) return;
            s.State = "overgeslagen";
            s.Weight = 0;
            s.Detail = reason;
        }
    }

    /// <summary>Normale afronding: alles wat nog liep, is klaar.</summary>
    public void Finish()
    {
        lock (_lock)
        {
            _finished = true;
            _running = false;
            foreach (var s in _steps)
                if (s.State is "bezig" or "wachten") s.State = "klaar";
            _sw.Stop();
        }
    }

    /// <summary>
    /// Voortijdig gestopt (fout of afgebroken). Een stap die nog liep, wordt niet
    /// als voltooid getoond — dat zou een verkeerd beeld geven van wat er is opgehaald.
    /// </summary>
    public void Abort()
    {
        lock (_lock)
        {
            if (_finished) return;
            _running = false;
            foreach (var s in _steps)
            {
                if (s.State == "bezig") s.State = "afgebroken";
                else if (s.State == "wachten") s.State = "overgeslagen";
            }
            _sw.Stop();
        }
    }

    private Step? Find(string key) => _steps.FirstOrDefault(s => s.Key == key);

    private static int PercentOf(Step s) => s.State switch
    {
        "klaar" => 100,
        "overgeslagen" => 0,
        _ when s.Total > 0 => Math.Clamp((int)Math.Round(100.0 * s.Done / s.Total), 0, 100),
        _ => 0
    };

    public RunProgress Snapshot()
    {
        lock (_lock)
        {
            var steps = _steps.Select(s => new ProgressStep
            {
                Key = s.Key,
                Label = s.Label,
                State = s.State,
                Done = s.Done,
                Total = s.Total,
                Detail = s.Detail,
                Percent = PercentOf(s)
            }).ToList();

            double totalWeight = _steps.Sum(s => s.Weight);
            double achieved = totalWeight <= 0 ? 0
                : _steps.Sum(s => s.Weight * PercentOf(s) / 100.0);

            int percent = totalWeight <= 0 ? 0
                : Math.Clamp((int)Math.Round(100.0 * achieved / totalWeight), 0, 100);
            if (_finished) percent = 100;

            long elapsed = _sw.ElapsedMilliseconds;
            long remaining = 0;
            // Pas schatten als er genoeg is gebeurd; anders schiet de schatting alle kanten op.
            if (_running && percent >= 5 && percent < 100 && elapsed > 2000)
                remaining = (long)(elapsed / (percent / 100.0) - elapsed);

            var current = _steps.FirstOrDefault(s => s.State == "bezig");
            var summary = current is null
                ? (_running ? "Bezig met zoeken…" : _finished ? "Klaar." : "Gestopt.")
                : current.Total > 0
                    ? $"{current.Label} ({current.Done}/{current.Total})"
                    : current.Label;

            return new RunProgress
            {
                Running = _running,
                Percent = percent,
                Summary = summary,
                ElapsedMs = elapsed,
                RemainingMsEstimate = remaining,
                Steps = steps
            };
        }
    }
}
