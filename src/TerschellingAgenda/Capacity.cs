namespace TerschellingAgenda;

/// <summary>
/// Bepaalt hoeveel er tegelijk mag gebeuren, op basis van het geheugen dat deze
/// machine werkelijk beschikbaar heeft.
///
/// Een agendapagina van enkele megabytes groeit als ontleed document uit tot een
/// veelvoud daarvan. Twaalf van zulke pagina's tegelijk passen prima op een
/// werkstation, maar niet in een kleine container: die wordt dan door het platform
/// afgebroken, met een onbegrijpelijke foutmelding voor de gebruiker als gevolg.
///
/// Liever iets trager dan onbetrouwbaar.
/// </summary>
public static class Capacity
{
    /// <summary>Beschikbaar werkgeheugen in megabytes, zoals de runtime het ziet.</summary>
    public static int AvailableMemoryMb { get; } = DetectMemoryMb();

    /// <summary>Aantal bronnen dat tegelijk wordt geraadpleegd.</summary>
    public static int Sources { get; } = Scale(12, 6, 3);

    /// <summary>Aantal gevonden pagina's dat tegelijk wordt uitgelezen.</summary>
    public static int Pages { get; } = Scale(12, 6, 3);

    /// <summary>Aantal detailpagina's dat per bron tegelijk wordt gevolgd.</summary>
    public static int DetailPages { get; } = Scale(4, 3, 2);

    /// <summary>
    /// Maximale grootte van een opgehaalde pagina.
    ///
    /// Deze grens hangt samen met de gelijktijdigheid hierboven: het geheugenbeslag is
    /// grofweg het aantal parallelle pagina's maal deze grootte maal de groeifactor van
    /// het ontlede document. Omdat er op een kleine machine ook minder tegelijk gebeurt,
    /// hoeft de grens per pagina niet extreem laag te zijn — een agenda-overzichtspagina
    /// van twee megabyte moet gewoon volledig gelezen kunnen worden.
    /// </summary>
    public static int MaxResponseBytes { get; } =
        AvailableMemoryMb >= 3000 ? 4 * 1024 * 1024 :
                                    2 * 1024 * 1024;

    /// <summary>Korte omschrijving voor het transparantierapport.</summary>
    public static string Description =>
        $"{AvailableMemoryMb} MB werkgeheugen; {Sources} bronnen tegelijk, " +
        $"{DetailPages} detailpagina's per bron, pagina's tot {MaxResponseBytes / (1024 * 1024)} MB.";

    private static int Scale(int roomy, int modest, int tight) =>
        AvailableMemoryMb >= 3000 ? roomy :
        AvailableMemoryMb >= 1500 ? modest :
                                    tight;

    private static int DetectMemoryMb()
    {
        try
        {
            // In een container geeft dit de cgroup-limiet, niet het geheugen van de host.
            var info = GC.GetGCMemoryInfo();
            if (info.TotalAvailableMemoryBytes > 0)
                return (int)(info.TotalAvailableMemoryBytes / (1024 * 1024));
        }
        catch { /* onbekend: ga uit van krap */ }

        return 1024;
    }
}
