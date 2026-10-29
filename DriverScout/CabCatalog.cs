using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace DriverScout;

/// <summary>
/// HP и Dell публикуют каталоги драйверов в подписанных .cab-архивах.
/// Скачиваем, проверяем подпись (и SHA-256, если он известен), распаковываем штатным expand.exe.
/// </summary>
public static class CabCatalog
{
    /// <summary>Возвращает путь к распакованному XML. Вызывающий удаляет папку <paramref name="workDir"/>.</summary>
    public static async Task<string> DownloadAndExtractAsync(string url, string expectedSigner, string workDir, string? sha256 = null)
    {
        Directory.CreateDirectory(workDir);
        var cab = Path.Combine(workDir, "catalog.cab");
        await DownloadWithRetryAsync(url, cab).ConfigureAwait(false);

        if (sha256 is { Length: > 0 })
        {
            await using var s = File.OpenRead(cab);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(s).ConfigureAwait(false));
            if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Контрольная сумма каталога не совпадает — каталог не используем");
        }
        if (VendorInstaller.CheckSignature(cab, expectedSigner) is { } sigError)
            throw new InvalidOperationException("Каталог: " + sigError);

        var outDir = Path.Combine(workDir, "x");
        Directory.CreateDirectory(outDir);
        var expand = Path.Combine(Environment.SystemDirectory, "expand.exe");
        using (var p = Process.Start(new ProcessStartInfo(expand)
               {
                   ArgumentList = { cab, "-F:*", outDir },
                   CreateNoWindow = true,
                   UseShellExecute = false,
                   RedirectStandardOutput = true
               })!)
        {
            await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
        }

        // В архиве один XML; expand.exe может назвать его по имени архива, поэтому расширение не проверяем.
        return Directory.GetFiles(outDir).SingleOrDefault()
               ?? throw new InvalidOperationException("Не удалось распаковать каталог");
    }

    public static string NewWorkDir(string vendor) =>
        Path.Combine(Path.GetTempPath(), "DriverScout", vendor + "-" + Guid.NewGuid().ToString("N")[..8]);

    public static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* временная папка — не страшно */ }
    }

    private static async Task DownloadWithRetryAsync(string url, string file)
    {
        for (int i = 1; ; i++)
        {
            try
            {
                using var resp = await VendorSources.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                await using var dst = File.Create(file);
                await resp.Content.CopyToAsync(dst).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (i < 3 && ex is HttpRequestException or TaskCanceledException or IOException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * i)).ConfigureAwait(false);
            }
        }
    }
}
