namespace TerschellingAgenda;

/// <summary>
/// Bepaalt waar de resultaten en de hostgezondheid worden bewaard.
///
/// Lokaal is dat de map <c>data</c> naast de broncode. In Azure App Service ligt de
/// applicatie op een alleen-lezen of vluchtig pad; alleen de map onder <c>HOME</c>
/// blijft daar bewaard bij een herstart of nieuwe versie. Zonder dit onderscheid
/// zou de app bij elke publicatie zijn geschiedenis kwijtraken.
/// </summary>
public static class DataPath
{
    public static string Resolve(IWebHostEnvironment env, IConfiguration config)
    {
        // 1. Expliciete instelling wint altijd — handig voor eigen hosting of tests.
        var configured = config["DataDirectory"];
        if (!string.IsNullOrWhiteSpace(configured)) return Ensure(configured!);

        // 2. Azure App Service: persistente gedeelde opslag onder HOME.
        var home = Environment.GetEnvironmentVariable("HOME");
        bool onAppService = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"));

        if (onAppService && !string.IsNullOrWhiteSpace(home))
            return Ensure(Path.Combine(home!, "data"));

        // 3. Lokaal: de map naast src/, zodat resultaten buiten de broncode blijven.
        return Ensure(Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "..", "data")));
    }

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
