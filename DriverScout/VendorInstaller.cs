using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace DriverScout;

/// <summary>
/// Скачивает установщик с сайта производителя, проверяет цифровую подпись и запускает его.
/// Установщик запускается в обычном режиме (не тихо): пользователь сам видит лицензию и параметры.
/// </summary>
public static class VendorInstaller
{
    public static string DownloadDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriverScout", "Downloads");

    public static async Task<(bool ok, string message)> DownloadAndRunAsync(DriverUpdate update, Action<string> progress)
    {
        if (update.DownloadUrl is null || update.ExpectedSigner is null)
            return (false, "Нет ссылки на установщик");

        Directory.CreateDirectory(DownloadDir);
        var file = Path.Combine(DownloadDir, Path.GetFileName(new Uri(update.DownloadUrl).LocalPath));

        // Уже скачанный и прошедший проверку файл повторно не качаем.
        if (!File.Exists(file) || Verify(file, update) is not null)
        {
            var error = await DownloadAsync(update, file, progress);
            if (error is not null) return (false, error);
        }

        progress("Проверяю контрольную сумму и цифровую подпись…");
        var verifyError = Verify(file, update);
        if (verifyError is not null)
        {
            File.Delete(file);
            return (false, verifyError);
        }

        var setup = file;
        if (IsZip(file))
        {
            progress("Распаковываю архив…");
            (setup, var zipError) = ExtractSetup(file);
            if (setup is null) return (false, zipError!);
        }

        progress("Запущен установщик — следуйте его указаниям");
        using var proc = Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(setup) });
        if (proc is null) return (false, "Не удалось запустить установщик");
        await proc.WaitForExitAsync();
        return proc.ExitCode == 0
            ? (true, "Установщик завершился успешно")
            : (false, $"Установщик завершился с кодом {proc.ExitCode}");
    }

    private static async Task<string?> DownloadAsync(DriverUpdate update, string file, Action<string> progress)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, update.DownloadUrl);
        if (update.Referer is not null) req.Headers.Referrer = new Uri(update.Referer);

        using var resp = await VendorSources.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode) return $"Ошибка загрузки: HTTP {(int)resp.StatusCode}";
        if (resp.Content.Headers.ContentType?.MediaType == "text/html")
            return "Сайт вернул страницу вместо файла — ссылка устарела или загрузка запрещена";

        var total = resp.Content.Headers.ContentLength;
        var tmp = file + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync())
        await using (var dst = File.Create(tmp))
        {
            var buffer = new byte[1 << 20];
            long done = 0;
            int lastPercent = -1, read;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                int percent = total > 0 ? (int)(done * 100 / total.Value) : -1;
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress(percent >= 0 ? $"Скачано {percent}% из {total / 1048576} МБ" : $"Скачано {done / 1048576} МБ");
                }
            }
        }
        File.Move(tmp, file, overwrite: true);
        return null;
    }

    /// <summary>
    /// Проверка скачанного файла: SHA-256 (если производитель его публикует), затем подпись .exe.
    /// Архив подписи не имеет — для него контрольная сумма обязательна.
    /// </summary>
    private static string? Verify(string file, DriverUpdate update)
    {
        if (update.Sha256 is { Length: > 0 } expected)
        {
            using var stream = File.OpenRead(file);
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return "Контрольная сумма не совпадает с опубликованной — файл повреждён или подменён";
        }
        if (IsZip(file))
            return update.Sha256 is { Length: > 0 } ? null : "Архив без контрольной суммы — установка отменена";
        return CheckSignature(file, update.ExpectedSigner!);
    }

    private static bool IsZip(string file) => file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>Распаковывает архив и находит в нём установщик (у ASUS обычно AsusSetup.exe).</summary>
    private static (string? setup, string? error) ExtractSetup(string zip)
    {
        var dir = Path.Combine(DownloadDir, Path.GetFileNameWithoutExtension(zip));
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        System.IO.Compression.ZipFile.ExtractToDirectory(zip, dir);

        var exes = Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories);
        var setup = exes.FirstOrDefault(f => Path.GetFileName(f).Equals("AsusSetup.exe", StringComparison.OrdinalIgnoreCase))
                    ?? exes.FirstOrDefault(f => Path.GetFileName(f).Equals("Setup.exe", StringComparison.OrdinalIgnoreCase))
                    ?? (exes.Length == 1 ? exes[0] : null);
        if (setup is null) return (null, "В архиве не найден установщик — откройте папку и установите вручную: " + dir);

        // Внутри архива установщик может быть подписан не ASUS, а производителем чипа — проверяем только действительность.
        int trust = WinVerifyTrust(setup);
        return trust == 0 ? (setup, null) : (null, $"Подпись установщика в архиве недействительна (0x{trust:X8})");
    }

    /// <summary>
    /// null — подпись действительна и принадлежит нужной компании, иначе текст ошибки.
    /// Несколько допустимых имён через «|»: "HP Inc|Hewlett-Packard" (старые пакеты подписаны старым именем).
    /// </summary>
    public static string? CheckSignature(string file, string expectedSigner)
    {
        int trust = WinVerifyTrust(file);
        if (trust != 0) return $"Подпись файла недействительна (0x{trust:X8}) — установка отменена";

        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            var subject = cert.GetNameInfo(X509NameType.SimpleName, false);
            return expectedSigner.Split('|').Any(s => subject.Contains(s, StringComparison.OrdinalIgnoreCase))
                ? null
                : $"Файл подписан «{subject}», а ожидался «{expectedSigner.Replace("|", "» или «")}» — установка отменена";
        }
        catch (Exception ex)
        {
            return "Не удалось прочитать подпись: " + ex.Message;
        }
    }

    // --- WinVerifyTrust: проверка Authenticode-подписи средствами Windows ---

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;          // 2 = WTD_UI_NONE
        public uint fdwRevocationChecks; // 0 = WTD_REVOKE_NONE
        public uint dwUnionChoice;       // 1 = WTD_CHOICE_FILE
        public IntPtr pFile;
        public uint dwStateAction;       // 0 = WTD_STATEACTION_IGNORE
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", EntryPoint = "WinVerifyTrust", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrustNative(IntPtr hwnd, [In] ref Guid action, IntPtr data);

    private static int WinVerifyTrust(string file)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = file
        };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        IntPtr pData = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,
                dwUnionChoice = 1,
                pFile = pFile
            };
            Marshal.StructureToPtr(data, pData, false);
            var action = GenericVerifyV2;
            return WinVerifyTrustNative(new IntPtr(-1), ref action, pData);
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(pFile);
            Marshal.FreeHGlobal(pFile);
            Marshal.FreeHGlobal(pData);
        }
    }
}
