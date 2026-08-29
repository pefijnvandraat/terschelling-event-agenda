using System.Collections.Concurrent;
using System.Text.Json;

namespace TerschellingAgenda.Collect;

/// <summary>
/// Onthoudt welke websites onbereikbaar bleken, zodat een volgende zoekopdracht
/// daar geen minuten meer op wacht.
///
/// Alleen échte verbindingsfouten tellen mee: een time-out of een DNS-/netwerkfout,
/// dus géén HTTP-antwoord. Een site die netjes 403 of 404 teruggeeft leeft immers wel
/// en verdient een normale behandeling.
///
/// De registratie verloopt altijd: na de hersteltermijn krijgt een site vanzelf weer
/// een eerlijke kans, zodat tijdelijke storingen niet permanent doorwerken.
/// </summary>
public sealed class HostHealthStore
{
    private const int FailuresBeforeSkip = 2;
    private static readonly TimeSpan Recovery = TimeSpan.FromHours(6);

    private readonly string _path;
    private readonly ILogger<HostHealthStore> _log;
    private readonly ConcurrentDictionary<string, HostHealth> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public sealed class HostHealth
    {
        public string Host { get; set; } = "";
        public int ConsecutiveFailures { get; set; }
        public DateTimeOffset? LastFailureAt { get; set; }
        public DateTimeOffset? LastSuccessAt { get; set; }
        public string? LastError { get; set; }
    }

    public HostHealthStore(IWebHostEnvironment env, IConfiguration config, ILogger<HostHealthStore> log)
    {
        _log = log;
        _path = Path.Combine(DataPath.Resolve(env, config), "host-health.json");
        Load();
    }

    /// <summary>Hosts die in deze run zijn overgeslagen — voor het transparantierapport.</summary>
    public IReadOnlyCollection<string> SkippedThisRun
    {
        get { lock (_skipped) return _skipped.ToList(); }
    }

    private readonly HashSet<string> _skipped = new(StringComparer.OrdinalIgnoreCase);

    public void ResetRunState()
    {
        lock (_skipped) _skipped.Clear();
    }

    /// <summary>Legt vast dat deze host in deze run is overgeslagen — voor het rapport.</summary>
    public void NoteSkipped(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        lock (_skipped) _skipped.Add(host);
    }

    /// <summary>
    /// Moet deze host worden overgeslagen? Waar: hij faalde herhaaldelijk en de
    /// hersteltermijn is nog niet verstreken.
    /// </summary>
    public bool ShouldSkip(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (!_hosts.TryGetValue(host, out var h)) return false;
        if (h.ConsecutiveFailures < FailuresBeforeSkip) return false;
        if (h.LastFailureAt is null) return false;
        if (DateTimeOffset.UtcNow - h.LastFailureAt.Value > Recovery) return false;

        lock (_skipped) _skipped.Add(host);
        return true;
    }

    /// <summary>Registreert dat de host helemaal geen antwoord gaf.</summary>
    public void RecordUnreachable(string host, string? error)
    {
        if (string.IsNullOrWhiteSpace(host)) return;

        _hosts.AddOrUpdate(host,
            _ => new HostHealth
            {
                Host = host,
                ConsecutiveFailures = 1,
                LastFailureAt = DateTimeOffset.UtcNow,
                LastError = error
            },
            (_, h) =>
            {
                h.ConsecutiveFailures++;
                h.LastFailureAt = DateTimeOffset.UtcNow;
                h.LastError = error;
                return h;
            });
    }

    /// <summary>Registreert een geslaagd contact; de teller gaat terug naar nul.</summary>
    public void RecordReachable(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;

        _hosts.AddOrUpdate(host,
            _ => new HostHealth { Host = host, LastSuccessAt = DateTimeOffset.UtcNow },
            (_, h) =>
            {
                h.ConsecutiveFailures = 0;
                h.LastSuccessAt = DateTimeOffset.UtcNow;
                return h;
            });
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<HostHealth>>(File.ReadAllText(_path), Json);
            foreach (var h in list ?? new List<HostHealth>())
                if (!string.IsNullOrWhiteSpace(h.Host)) _hosts[h.Host] = h;

            _log.LogInformation("Hostgezondheid geladen: {Count} hosts", _hosts.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Kon hostgezondheid niet lezen; er wordt schoon begonnen.");
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            // Hosts die al lang niet meer zijn gezien, hoeven niet te blijven staan.
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(30);
            var keep = _hosts.Values
                .Where(h => (h.LastFailureAt ?? h.LastSuccessAt ?? DateTimeOffset.MinValue) > cutoff)
                .OrderBy(h => h.Host)
                .ToList();

            await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(keep, Json), ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Kon hostgezondheid niet opslaan.");
        }
        finally { _saveLock.Release(); }
    }
}
