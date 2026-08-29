using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TerschellingAgenda.Collect;

/// <summary>
/// Rendert pagina's met een echte (headless) browser via het Chrome DevTools Protocol.
///
/// Dit lost twee problemen op die met gewone HTTP-verzoeken niet op te lossen zijn:
///  1. JavaScript-only pagina's (SPA's) waarvan de agenda pas na uitvoeren van scripts bestaat;
///  2. sites die eenvoudige HTTP-clients weigeren omdat er geen echte browser achter zit.
///
/// Er wordt bewust GEEN techniek gebruikt om beveiliging te omzeilen: er worden geen
/// proxies geroteerd, geen CAPTCHA's opgelost en geen IP-adressen verhuld. We gebruiken
/// simpelweg de browser die al op deze machine staat, met normaal navigatiegedrag.
/// Sites die ook een echte browser weigeren, blijven geweigerd en worden als zodanig gerapporteerd.
/// </summary>
public sealed class BrowserFetcher : IAsyncDisposable
{
    private readonly ILogger<BrowserFetcher> _log;
    private readonly SemaphoreSlim _launchLock = new(1, 1);
    private readonly SemaphoreSlim _tabLimit = new(4, 4);

    private System.Diagnostics.Process? _browser;
    private string? _devToolsBase;
    private string? _profileDir;
    private bool _unavailable;

    private static readonly string[] BrowserPaths =
    {
        // Windows
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        // Linux (bijvoorbeeld in een container met een browser aan boord).
        // Staat er geen browser, dan meldt de app dat en gaat verder zonder.
        "/usr/bin/microsoft-edge",
        "/usr/bin/google-chrome",
        "/usr/bin/chromium",
        "/usr/bin/chromium-browser"
    };

    public BrowserFetcher(ILogger<BrowserFetcher> log) => _log = log;

    /// <summary>Is er een bruikbare browser op deze machine?</summary>
    public bool IsSupported => !_unavailable && BrowserPaths.Any(File.Exists);

    public string? ExecutablePath => BrowserPaths.FirstOrDefault(File.Exists);

    /// <summary>
    /// Haalt de volledig gerenderde HTML van een pagina op. Geeft null terug wanneer
    /// dat niet lukt; de aanroeper gaat dan verder met de volgende terugvaloptie.
    /// </summary>
    /// <param name="loadTimeoutSeconds">
    /// Bovengrens voor het laden. Bij een host die op gewone verzoeken al niet reageerde
    /// is een korte grens genoeg: dit is een laatste kans, geen geduldige poging.
    /// </param>
    public async Task<string?> GetRenderedHtmlAsync(string url, int settleMs, CancellationToken ct,
        int loadTimeoutSeconds = 25)
    {
        if (!IsSupported) return null;
        if (!await EnsureBrowserAsync(ct)) return null;

        await _tabLimit.WaitAsync(ct);
        string? targetId = null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

            // Open een leeg tabblad en navigeer daarna via CDP, zodat we het laadmoment kennen.
            var newTabJson = await http.PutAsync($"{_devToolsBase}/json/new?about:blank", null, ct);
            if (!newTabJson.IsSuccessStatusCode) return null;

            using var tabDoc = JsonDocument.Parse(await newTabJson.Content.ReadAsStringAsync(ct));
            targetId = tabDoc.RootElement.GetProperty("id").GetString();
            var wsUrl = tabDoc.RootElement.GetProperty("webSocketDebuggerUrl").GetString();
            if (string.IsNullOrWhiteSpace(wsUrl)) return null;

            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(15));
            await ws.ConnectAsync(new Uri(wsUrl), connectCts.Token);

            int id = 0;
            await SendAsync(ws, ++id, "Page.enable", null, ct);
            await SendAsync(ws, ++id, "Network.setUserAgentOverride", new
            {
                userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                            "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Edg/126.0.0.0",
                acceptLanguage = "nl-NL,nl;q=0.9,en;q=0.8"
            }, ct);

            int navId = ++id;
            await SendAsync(ws, navId, "Page.navigate", new { url }, ct);

            // Wacht op het load-event, met harde bovengrens zodat één trage site de run niet ophoudt.
            await WaitForLoadAsync(ws, TimeSpan.FromSeconds(Math.Clamp(loadTimeoutSeconds, 5, 60)), ct);

            // Extra rusttijd zodat client-side gerenderde agenda's daadwerkelijk in de DOM staan.
            await Task.Delay(Math.Clamp(settleMs, 300, 6000), ct);

            int evalId = ++id;
            await SendAsync(ws, evalId, "Runtime.evaluate", new
            {
                expression = "document.documentElement.outerHTML",
                returnByValue = true
            }, ct);

            var html = await ReadEvaluateResultAsync(ws, evalId, TimeSpan.FromSeconds(20), ct);

            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "klaar", CancellationToken.None);
            }
            catch { /* sluiten mag falen */ }

            return string.IsNullOrWhiteSpace(html) ? null : html;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug("Browserrendering mislukt voor {Url}: {Msg}", url, ex.Message);
            return null;
        }
        finally
        {
            if (targetId is not null)
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                    await http.GetAsync($"{_devToolsBase}/json/close/{targetId}", CancellationToken.None);
                }
                catch { /* opruimen mag falen */ }
            }
            _tabLimit.Release();
        }
    }

    private async Task<bool> EnsureBrowserAsync(CancellationToken ct)
    {
        if (_browser is { HasExited: false } && _devToolsBase is not null) return true;

        await _launchLock.WaitAsync(ct);
        try
        {
            if (_browser is { HasExited: false } && _devToolsBase is not null) return true;

            var exe = ExecutablePath;
            if (exe is null) { _unavailable = true; return false; }

            int port = FindFreePort();
            _profileDir = Path.Combine(Path.GetTempPath(), "terschelling-agenda-browser-" + Guid.NewGuid().ToString("n")[..8]);
            Directory.CreateDirectory(_profileDir);

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (var arg in new[]
                     {
                         "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
                         "--disable-extensions", "--disable-background-networking", "--mute-audio",
                         "--disable-dev-shm-usage", "--window-size=1400,2200",
                         $"--remote-debugging-port={port}", $"--user-data-dir={_profileDir}",
                         "about:blank"
                     })
                psi.ArgumentList.Add(arg);

            _browser = System.Diagnostics.Process.Start(psi);
            if (_browser is null) { _unavailable = true; return false; }

            // Voorkom dat de uitvoerbuffers vollopen en het proces blokkeert.
            _ = Task.Run(() => _browser.StandardOutput.ReadToEnd());
            _ = Task.Run(() => _browser.StandardError.ReadToEnd());

            _devToolsBase = $"http://127.0.0.1:{port}";

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            for (int i = 0; i < 25; i++)
            {
                if (_browser.HasExited) break;
                try
                {
                    var r = await http.GetAsync($"{_devToolsBase}/json/version", ct);
                    if (r.IsSuccessStatusCode)
                    {
                        _log.LogInformation("Headless browser gestart op poort {Port}", port);
                        return true;
                    }
                }
                catch { /* nog niet klaar */ }
                await Task.Delay(400, ct);
            }

            _log.LogWarning("Headless browser kon niet worden gestart; terugval op gewone HTTP-verzoeken.");
            _unavailable = true;
            await KillBrowserAsync();
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Browser starten mislukt");
            _unavailable = true;
            return false;
        }
        finally { _launchLock.Release(); }
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task SendAsync(ClientWebSocket ws, int id, string method, object? parameters, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { id, method, @params = parameters ?? new { } });
        await ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task WaitForLoadAsync(ClientWebSocket ws, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var msg = await ReceiveAsync(ws, cts.Token);
                if (msg is null) return;
                if (msg.Contains("\"Page.loadEventFired\"", StringComparison.Ordinal)) return;
            }
        }
        catch (OperationCanceledException) { /* time-out: we proberen alsnog de DOM te lezen */ }
    }

    private static async Task<string?> ReadEvaluateResultAsync(ClientWebSocket ws, int evalId, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var msg = await ReceiveAsync(ws, cts.Token);
                if (msg is null) return null;

                using var doc = JsonDocument.Parse(msg);
                if (!doc.RootElement.TryGetProperty("id", out var idEl) || idEl.GetInt32() != evalId) continue;
                if (!doc.RootElement.TryGetProperty("result", out var res)) return null;
                if (!res.TryGetProperty("result", out var inner)) return null;
                if (!inner.TryGetProperty("value", out var val)) return null;
                return val.GetString();
            }
        }
        catch (OperationCanceledException) { }
        catch (JsonException) { }
        return null;
    }

    private async Task KillBrowserAsync()
    {
        try
        {
            if (_browser is { HasExited: false })
            {
                _browser.Kill(entireProcessTree: true);
                await _browser.WaitForExitAsync();
            }
        }
        catch { /* afsluiten mag falen */ }
        finally
        {
            _browser?.Dispose();
            _browser = null;
            if (_profileDir is not null)
            {
                try { Directory.Delete(_profileDir, recursive: true); } catch { }
                _profileDir = null;
            }
        }
    }

    public async ValueTask DisposeAsync() => await KillBrowserAsync();
}
