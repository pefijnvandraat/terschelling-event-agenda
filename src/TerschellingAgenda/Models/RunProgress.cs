namespace TerschellingAgenda.Models;

/// <summary>Eén stap van de zoekopdracht, zoals die in de voortgangsbalk verschijnt.</summary>
public sealed class ProgressStep
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>wachten | bezig | klaar | overgeslagen</summary>
    public string State { get; set; } = "wachten";
    public int Done { get; set; }
    public int Total { get; set; }
    public int Percent { get; set; }
    public string Detail { get; set; } = "";
}

/// <summary>Voortgang van de lopende zoekopdracht: totaal én per stap.</summary>
public sealed class RunProgress
{
    public bool Running { get; set; }
    public int Percent { get; set; }
    public string Summary { get; set; } = "";
    public long ElapsedMs { get; set; }
    /// <summary>Schatting van de resterende tijd; 0 zolang die nog niet betrouwbaar is.</summary>
    public long RemainingMsEstimate { get; set; }
    public List<ProgressStep> Steps { get; set; } = new();
}
