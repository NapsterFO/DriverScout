using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DriverScout;

/// <summary>
/// Последний релиз репозитория на GitHub: версия и установщик. GitHub сам считает SHA-256 каждого файла релиза
/// (поле digest) — по нему проверяем скачанный установщик, у которого нет цифровой подписи.
/// </summary>
public static class GitHubReleases
{
    public sealed record Release(Version Version, string Tag, string Name, string PageUrl, string? Notes,
                                 string AssetName, string AssetUrl, long AssetSize, string? Sha256);

    /// <summary>null — у последнего релиза нет подходящего установщика.</summary>
    public static async Task<Release?> LatestAsync(string repo, string assetPattern)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await VendorSources.Http.SendAsync(req).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;

        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (!Regex.IsMatch(name, assetPattern, RegexOptions.IgnoreCase)) continue;
            var digest = a.TryGetProperty("digest", out var d) ? d.GetString() : null;
            return new Release(version, tag,
                root.GetProperty("name").GetString() ?? tag,
                root.GetProperty("html_url").GetString() ?? $"https://github.com/{repo}/releases",
                root.TryGetProperty("body", out var b) ? b.GetString() : null,
                name,
                a.GetProperty("browser_download_url").GetString()!,
                a.GetProperty("size").GetInt64(),
                digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null);
        }
        return null;
    }

    /// <summary>Задание на скачивание установщика из релиза: без SHA-256 не качаем — файл нечем проверить.</summary>
    public static DriverUpdate? ToDownload(Release r, string title) => r.Sha256 is null ? null : new DriverUpdate
    {
        Title = title,
        Source = "GitHub",
        Kind = DriverKind.Installer,
        Version = r.Version.ToString(3),
        DownloadUrl = r.AssetUrl,
        FileName = r.AssetName,
        Sha256 = r.Sha256,
        PageUrl = r.PageUrl
    };
}
