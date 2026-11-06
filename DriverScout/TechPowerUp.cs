using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace DriverScout;

/// <summary>
/// TechPowerUp (techpowerup.com/download) — зеркало официальных установщиков драйверов на своих серверах в Европе и США.
/// Нужно потому, что intel.com и downloadmirror.intel.com из России не открываются, а здесь лежат те же файлы Intel
/// с опубликованным SHA-256. Скачанный файл проверяется и по SHA-256, и по цифровой подписи Intel.
/// </summary>
public static class TechPowerUp
{
    private const string Base = "https://www.techpowerup.com/download/";

    /// <summary>Выпуск драйвера на странице: название, версия, дата, файл и его SHA-256, ID для формы загрузки.</summary>
    public sealed record Release(string Title, string Version, DateTime? Date, string FileName, string Sha256, string Id);

    // Сервер с файлом отдают через редирект с одноразовой ссылкой — её надо прочитать, а не идти по ней.
    private static readonly HttpClient NoRedirect = CreateNoRedirect();
    private static readonly Dictionary<string, Task<List<Release>>> Pages = [];

    public static string PageUrl(string slug) => Base + slug + "/";

    /// <summary>Перед новым сканом: страницы с выпусками читаем заново.</summary>
    public static void Reset()
    {
        lock (Pages) Pages.Clear();
    }

    /// <summary>Выпуски со страницы (самый свежий первым). Страница читается один раз за скан.</summary>
    public static Task<List<Release>> ReleasesAsync(string slug)
    {
        lock (Pages)
        {
            if (!Pages.TryGetValue(slug, out var task) || task.IsFaulted)
                Pages[slug] = task = LoadAsync(slug);
            return task;
        }
    }

    private static async Task<List<Release>> LoadAsync(string slug)
    {
        var html = await VendorSources.GetStringWithRetryAsync(PageUrl(slug)).ConfigureAwait(false);
        var list = new List<Release>();
        foreach (var block in html.Split("<div class=\"version").Skip(1))
        {
            var title = Text(Regex.Match(block, @"<h3 class=""title"">(.*?)</h3>", RegexOptions.Singleline).Groups[1].Value);
            var version = Regex.Match(title, @"\d+(?:\.\d+)+").Value;
            var dateText = Regex.Replace(Text(Regex.Match(block, @"<span class=""date"">(.*?)</span>").Groups[1].Value), @"(\d)(st|nd|rd|th)", "$1");
            DateTime? date = DateTime.TryParseExact(dateText, "MMMM d, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            // В выпуске бывает несколько файлов (Windows 10/11, 32/64 бит) — берём 64-битный.
            foreach (Match file in Regex.Matches(block, @"<li class=""file.*?</li>", RegexOptions.Singleline))
            {
                var f = file.Value;
                var os = Text(Regex.Match(f, @"<div class=""os"">(.*?)</div>").Groups[1].Value);
                var name = Text(Regex.Match(f, @"<div class=""filename""[^>]*>(.*?)</div>").Groups[1].Value);
                var sha = Regex.Match(f, @"SHA256:</div>\s*<div class=""hash-value"">([0-9A-F]{64})<", RegexOptions.IgnoreCase).Groups[1].Value;
                var id = Regex.Match(f, @"name=""id"" value=""(\d+)""").Groups[1].Value;
                if (version.Length == 0 || name.Length == 0 || sha.Length == 0 || id.Length == 0) continue;
                if (!os.Contains("64", StringComparison.Ordinal) && os.Length > 0) continue;
                list.Add(new Release(title, version, date, name, sha, id));
                break;
            }
        }
        return list;
    }

    /// <summary>Файл на странице зеркала: имя, SHA-256 и ID для формы загрузки.</summary>
    public sealed record MirrorFile(string FileName, string Sha256, string Id);

    /// <summary>
    /// Все файлы со страницы (у NVIDIA и AMD выпуски размечены иначе, чем у Intel, но строки файлов одинаковые).
    /// Ищет первый, чьё имя подходит под <paramref name="fileNamePattern"/>.
    /// </summary>
    public static async Task<MirrorFile?> FindFileAsync(string slug, string fileNamePattern)
    {
        var html = await VendorSources.GetStringWithRetryAsync(PageUrl(slug)).ConfigureAwait(false);
        foreach (Match file in Regex.Matches(html, @"<li class=""file.*?</li>", RegexOptions.Singleline))
        {
            var f = file.Value;
            var name = Text(Regex.Match(f, @"<div class=""filename""[^>]*>(.*?)</div>").Groups[1].Value);
            if (!Regex.IsMatch(name, fileNamePattern, RegexOptions.IgnoreCase)) continue;
            var sha = Regex.Match(f, @"SHA256:</div>\s*<div class=""hash-value"">([0-9A-F]{64})<", RegexOptions.IgnoreCase).Groups[1].Value;
            var id = Regex.Match(f, @"name=""id"" value=""(\d+)""").Groups[1].Value;
            if (sha.Length > 0 && id.Length > 0) return new MirrorFile(name, sha, id);
        }
        return null;
    }

    /// <summary>
    /// Прямая ссылка на файл: страница выбора сервера, затем запрос к серверу (ближайшие к России — Германия,
    /// Нидерланды, Великобритания). Ссылка одноразовая и живёт несколько часов.
    /// </summary>
    public static async Task<string?> ResolveAsync(string slug, string id)
    {
        using var choose = new FormUrlEncodedContent([new("id", id)]);
        var html = await (await VendorSources.Http.PostAsync(PageUrl(slug), choose).ConfigureAwait(false))
            .Content.ReadAsStringAsync().ConfigureAwait(false);
        var servers = Regex.Matches(html, @"name=""server_id"" value=""(\d+)"">(.*?)</button>", RegexOptions.Singleline)
            .Select(m => (id: m.Groups[1].Value, name: Text(m.Groups[2].Value)))
            .OrderBy(s => s.name.Contains(" DE", StringComparison.Ordinal) ? 0
                        : s.name.Contains(" NL", StringComparison.Ordinal) ? 1
                        : s.name.Contains(" UK", StringComparison.Ordinal) ? 2 : 3)
            .ToList();
        foreach (var (server, _) in servers.Take(4))
        {
            using var pick = new FormUrlEncodedContent([new("id", id), new("server_id", server)]);
            using var resp = await NoRedirect.PostAsync(PageUrl(slug), pick).ConfigureAwait(false);
            if (resp.Headers.Location is { } loc && loc.IsAbsoluteUri && loc.Scheme == Uri.UriSchemeHttps
                && loc.Host.EndsWith(".techpowerup.com", StringComparison.OrdinalIgnoreCase))
                return loc.ToString();
        }
        return null;
    }

    private static string Text(string html) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ").Trim();

    private static HttpClient CreateNoRedirect()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DriverScout/1.6");
        return http;
    }
}

/// <summary>
/// Intel: сайт intel.com из России не открывается, поэтому установщики Intel берём с зеркала TechPowerUp
/// (графика, Wi-Fi, Bluetooth, проводная сеть). Остальное от Intel — чипсет, ME, Serial IO, GPIO, звук —
/// находится в каталоге Microsoft. Если на зеркале новее ничего нет, даём ссылку на Intel Driver &amp; Support Assistant.
/// </summary>
public sealed class IntelSource : IVendorSource
{
    public string Name => "intel.com (зеркало TechPowerUp)";
    private const string Page = "https://www.intel.com/content/www/us/en/support/detect.html";

    public bool Handles(DeviceInfo d, SystemProfile sys) => Kind(d) is not null;

    /// <summary>Какая страница зеркала подходит устройству.</summary>
    private static string? Kind(DeviceInfo d)
    {
        bool intelPci = VendorSources.IsVendor(d, "8086");
        bool Is(string cls) => string.Equals(d.DeviceClass, cls, StringComparison.OrdinalIgnoreCase);
        if (intelPci && Is("Display")) return "intel-graphics-drivers";
        if (intelPci && Is("Net"))
            return Regex.IsMatch(d.Name, @"Wi-?Fi|Wireless|WLAN", RegexOptions.IgnoreCase)
                ? "intel-wireless-networking-wifi-adapter-drivers"
                : Regex.IsMatch(d.Name, @"Ethernet|Connection|I2\d\d|Network", RegexOptions.IgnoreCase) ? "intel-ethernet-networking-drivers" : null;
        if (Is("Bluetooth") && d.HardwareIds.Any(h => h.Contains("VID_8087", StringComparison.OrdinalIgnoreCase)))
            return "intel-wireless-bluetooth-drivers";
        return null;
    }

    public async Task<VendorResult> CheckAsync(DeviceInfo d, SystemProfile sys)
    {
        var slug = Kind(d)!;
        var installed = VendorSources.ParseVersion(d.DriverVersion);
        // Нет драйвера или стоит стандартный Microsoft: поколение устройства по ID не понять — пусть решает каталог Microsoft.
        if (installed is null || VendorSources.IsInboxDriver(d)) return new VendorResult(null, Page);

        var latest = (await TechPowerUp.ReleasesAsync(slug).ConfigureAwait(false)).FirstOrDefault();
        if (latest is null || !IsNewer(slug, latest, installed, d.DriverDate)) return new VendorResult(null, Page);

        var url = await TechPowerUp.ResolveAsync(slug, latest.Id).ConfigureAwait(false);
        if (url is null) return new VendorResult(null, Page);
        return new VendorResult(new DriverUpdate
        {
            Title = latest.Title,
            Source = "TechPowerUp (Intel)",
            Kind = DriverKind.Installer,
            Version = latest.Version,
            Date = latest.Date,
            HardwareId = d.HardwareIds.FirstOrDefault(),
            DownloadUrl = url,
            FileName = latest.FileName,
            Sha256 = latest.Sha256,
            ExpectedSigner = "Intel Corporation",
            PageUrl = TechPowerUp.PageUrl(slug),
            Note = slug == "intel-graphics-drivers" && sys.IsLaptop
                ? "Ноутбук: универсальный драйвер Intel может заменить доработанный драйвер производителя ноутбука" : null
        }, TechPowerUp.PageUrl(slug));
    }

    /// <summary>
    /// Версии пакета и драйвера Windows связаны по-разному:
    /// графика «101.9175» = «32.0.101.9175»; Wi-Fi и Bluetooth «24.80.0» = «24.80.0.x»;
    /// у проводной сети номер пакета (31.3) с версией драйвера не связан — сравниваем по дате.
    /// </summary>
    private static bool IsNewer(string slug, TechPowerUp.Release r, Version installed, DateTime? installedDate)
    {
        var pkg = VendorSources.ParseVersion(r.Version);
        switch (slug)
        {
            case "intel-graphics-drivers":
                // Новый драйвер — только для Xe и Arc (ветка 101.4xxx и новее); у старой графики (101.2xxx) своя ветка.
                return pkg is { Major: 101 } && installed is { Build: 101, Revision: >= 4000 } && pkg.Minor > installed.Revision;
            case "intel-ethernet-networking-drivers":
                return r.Date is { } date && installedDate is { } inst && date > inst.AddDays(30);
            default:
                // Пакеты 22.x+ — для адаптеров AX и BE; старым AC в новых пакетах драйверов уже нет.
                return pkg is not null && installed.Major >= 22
                       && (pkg.Major, pkg.Minor).CompareTo((installed.Major, installed.Minor)) > 0;
        }
    }
}
