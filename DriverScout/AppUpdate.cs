using System.Diagnostics;

namespace DriverScout;

/// <summary>Обновление самого DriverScout из релизов на GitHub.</summary>
public static class AppUpdate
{
    private const string Repo = "NapsterFO/DriverScout";

    public static Version Current { get; } = typeof(AppUpdate).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>Новый релиз или null, если стоит последняя версия (или GitHub недоступен — об этом молчим).</summary>
    public static async Task<GitHubReleases.Release?> CheckAsync()
    {
        try
        {
            var r = await GitHubReleases.LatestAsync(Repo, @"^DriverScout-Setup-[\d.]+\.exe$").ConfigureAwait(false);
            return r is not null && r.Version > Current && r.Sha256 is not null ? r : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Скачивает установщик, сверяет SHA-256 с GitHub и запускает его. Установщик в режиме /SILENT сам закроет
    /// DriverScout, обновит его и запустит снова (см. [Run] в DriverScout.iss). true — можно закрываться.
    /// </summary>
    public static async Task<(bool ok, string message)> InstallAsync(GitHubReleases.Release release, Action<string> progress)
    {
        var download = GitHubReleases.ToDownload(release, "DriverScout " + release.Version.ToString(3))!;
        var (file, error) = await VendorInstaller.DownloadVerifiedAsync(download, progress).ConfigureAwait(false);
        if (file is null) return (false, error!);
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, ArgumentList = { "/SILENT", "/CLOSEAPPLICATIONS", "/NORESTART" } });
        return (true, "Устанавливаю обновление…");
    }
}
