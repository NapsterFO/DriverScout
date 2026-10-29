using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DriverScout;

/// <summary>Результат проверки устройства на сайте производителя.</summary>
public sealed record VendorResult(DriverUpdate? Update, string? PageUrl);

/// <summary>Источник драйверов с сайта производителя.</summary>
public interface IVendorSource
{
    string Name { get; }
    bool Handles(DeviceInfo device, SystemProfile system);
    Task<VendorResult> CheckAsync(DeviceInfo device, SystemProfile system);
}

public static class VendorSources
{
    public static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// Источники в порядке приоритета. Производитель ноутбука первым: его драйверы
    /// проверены под конкретную модель (так советуют и AMD, и NVIDIA).
    /// Создаются заново на каждый скан — некоторые кэшируют каталог.
    /// </summary>
    public static IReadOnlyList<IVendorSource> Create() =>
        [new AsusSource(), new LenovoSource(), new HpSource(), new DellSource(), new NvidiaSource(), new AmdSource(), new IntelSource()];

    /// <summary>
    /// Устройство подходит под ID из каталога: точное совпадение с Hardware/Compatible ID или более общий ID
    /// ("PCI\VEN_10DE&amp;DEV_1F97" подходит к "PCI\VEN_10DE&amp;DEV_1F97&amp;SUBSYS_0A211028&amp;REV_A1").
    /// </summary>
    public static bool MatchesHardwareId(DeviceInfo d, string id) =>
        d.CompatibleIds.Contains(id, StringComparer.OrdinalIgnoreCase)
        || d.HardwareIds.Any(h => h.Equals(id, StringComparison.OrdinalIgnoreCase)
                                  || h.StartsWith(id + "&", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Стоит стандартный драйвер Windows (поставщик Microsoft). Он нумеруется по-своему (10.0.19041.x),
    /// поэтому версии не сравниваем: драйвер производителя для такого устройства всегда лучше.
    /// </summary>
    public static bool IsInboxDriver(DeviceInfo d) =>
        string.Equals(d.DriverProvider, "Microsoft", StringComparison.OrdinalIgnoreCase);

    /// <summary>"V1.2.0.126", "8.7.10700.22502^" → Version.</summary>
    public static Version? ParseVersion(string? s) =>
        s is not null && Version.TryParse(s.Trim().TrimStart('V', 'v').TrimEnd('^'), out var v) ? v : null;

    /// <summary>GET с повторами: сайты производителей иногда обрывают соединение.</summary>
    public static async Task<string> GetStringWithRetryAsync(string url, int attempts = 3)
    {
        for (int i = 1; ; i++)
        {
            try
            {
                return await Http.GetStringAsync(url).ConfigureAwait(false);
            }
            catch (Exception ex) when (i < attempts && ex is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * i)).ConfigureAwait(false);
            }
        }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DriverScout/0.2");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        return http;
    }

    /// <summary>VEN/DEV из PCI Hardware ID: "PCI\VEN_10DE&amp;DEV_2484&amp;..." → ("10DE", "2484").</summary>
    public static (string ven, string dev)? PciIds(DeviceInfo d)
    {
        var src = d.HardwareIds.FirstOrDefault() ?? d.DeviceId;
        var m = Regex.Match(src, @"PCI\\VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.ToUpperInvariant()) : null;
    }

    public static bool IsVendor(DeviceInfo d, string ven) => PciIds(d)?.ven == ven;
}

/// <summary>
/// NVIDIA: тот же сервис, которым пользуется GeForce Experience / NVIDIA App —
/// по Device ID видеокарты отдаёт последний драйвер и прямую ссылку.
/// </summary>
public sealed class NvidiaSource : IVendorSource
{
    public string Name => "nvidia.com";
    private const string Page = "https://www.nvidia.com/drivers/";

    public bool Handles(DeviceInfo d, SystemProfile sys) =>
        VendorSources.IsVendor(d, "10DE") && string.Equals(d.DeviceClass, "Display", StringComparison.OrdinalIgnoreCase);

    public async Task<VendorResult> CheckAsync(DeviceInfo d, SystemProfile sys)
    {
        var devId = VendorSources.PciIds(d)!.Value.dev;
        var query = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["dIDa"] = new[] { $"{devId}_10DE" },
            ["osC"] = "10.0", ["osB"] = sys.OsBuild.ToString(), ["is6"] = "1", ["lg"] = "1033",
            ["iLp"] = sys.IsLaptop ? "1" : "0", ["prvMd"] = "0", ["gcV"] = "3.28.0.417",
            ["gIsB"] = "1", ["dch"] = "1", ["upCRD"] = "0", ["isCRD"] = "0"
        });
        var url = "https://gfwsl.geforce.com/nvidia_web_services/controller.gfeclientcontent.NG.php/" +
                  "com.nvidia.services.GFEClientContent_NG.getDispDrvrByDevid/" + Uri.EscapeDataString(query);

        using var resp = await VendorSources.Http.GetAsync(url).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return new VendorResult(null, Page); // карта не поддерживается
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("DriverAttributes", out var attr) || attr.ValueKind != JsonValueKind.Object)
            return new VendorResult(null, Page);

        var latest = attr.GetProperty("Version").GetString();
        var details = attr.TryGetProperty("DetailsURL", out var du) ? du.GetString() : Page;
        if (latest is null || !IsNewer(latest, ToNvidiaVersion(d.DriverVersion)))
            return new VendorResult(null, details);

        DateTime? date = DateTime.TryParse(attr.GetProperty("ReleaseDateTime").GetString(), out var dt) ? dt.Date : null;
        return new VendorResult(new DriverUpdate
        {
            Title = attr.GetProperty("Name").GetString() + " " + latest,
            Source = Name,
            Kind = DriverKind.Installer,
            Version = latest,
            Date = date,
            HardwareId = d.HardwareIds.FirstOrDefault(),
            DownloadUrl = attr.GetProperty("DownloadURL").GetString(),
            ExpectedSigner = "NVIDIA Corporation",
            PageUrl = details
        }, details);
    }

    /// <summary>Версия драйвера Windows "32.0.15.6636" → версия NVIDIA "566.36".</summary>
    public static string? ToNvidiaVersion(string? winVersion)
    {
        var parts = winVersion?.Split('.');
        if (parts is not { Length: 4 }) return null;
        var digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5) return null;
        digits = digits[^5..];
        return $"{int.Parse(digits[..3])}.{digits[3..]}";
    }

    private static bool IsNewer(string latest, string? installed) =>
        installed is null || (decimal.TryParse(latest, System.Globalization.CultureInfo.InvariantCulture, out var a)
                              && decimal.TryParse(installed, System.Globalization.CultureInfo.InvariantCulture, out var b)
                              && a > b);
}

/// <summary>
/// AMD: публичного API нет, читаем страницу продукта на amd.com
/// (для встроенной графики — страницу процессора, для Radeon RX — страницу видеокарты).
/// </summary>
public sealed class AmdSource : IVendorSource
{
    public string Name => "amd.com";
    private const string FallbackPage = "https://www.amd.com/en/support/download/drivers.html";

    public bool Handles(DeviceInfo d, SystemProfile sys) =>
        VendorSources.IsVendor(d, "1002") && string.Equals(d.DeviceClass, "Display", StringComparison.OrdinalIgnoreCase);

    public async Task<VendorResult> CheckAsync(DeviceInfo d, SystemProfile sys)
    {
        var page = ProductPage(d.Name, sys.CpuName);
        if (page is null) return new VendorResult(null, FallbackPage);

        using var resp = await VendorSources.Http.GetAsync(page).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return new VendorResult(null, FallbackPage);
        var html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

        var info = ParseDriverSection(html, sys.IsWindows11);
        if (info is null) return new VendorResult(null, page);
        var (title, version, date, link) = info.Value;

        // Версии Adrenalin (26.10.1) и драйвера Windows (32.0.21030.2001) не связаны напрямую —
        // сравниваем по дате. Запас 14 дней: дата в INF обычно раньше даты релиза.
        bool newer = d.DriverDate is not { } installed || date > installed.AddDays(14);
        if (!newer) return new VendorResult(null, page);

        return new VendorResult(new DriverUpdate
        {
            Title = "AMD Software: " + title,
            Source = Name,
            Kind = DriverKind.Installer,
            Version = version,
            Date = date,
            HardwareId = d.HardwareIds.FirstOrDefault(),
            DownloadUrl = link,
            Referer = "https://www.amd.com/", // без Referer drivers.amd.com отдаёт HTML вместо файла
            ExpectedSigner = "Advanced Micro Devices",
            PageUrl = page,
            Note = sys.IsLaptop ? "Ноутбук: AMD рекомендует драйвер от производителя ноутбука" : null
        }, page);
    }

    /// <summary>Строит адрес страницы продукта на amd.com.</summary>
    public static string? ProductPage(string gpuName, string cpuName)
    {
        const string root = "https://www.amd.com/en/support/downloads/drivers.html/";

        // Дискретная: "AMD Radeon RX 7800 XT" → graphics/radeon-rx/radeon-rx-7000-series/amd-radeon-rx-7800-xt.html
        var rx = Regex.Match(gpuName, @"Radeon(?:\(TM\))?\s+RX\s+((\d)\d{3}(?:\s*(?:XTX|XT|GRE))?)", RegexOptions.IgnoreCase);
        if (rx.Success)
        {
            var model = Slug(rx.Groups[1].Value);
            return $"{root}graphics/radeon-rx/radeon-rx-{rx.Groups[2].Value}000-series/amd-radeon-rx-{model}.html";
        }

        // Встроенная: "AMD Ryzen 5 7520U with Radeon Graphics" → processors/ryzen/ryzen-7000-series/amd-ryzen-5-7520u.html
        var cpu = Regex.Match(cpuName, @"Ryzen\s+(\d)\s+((\d)\d{3}\w*)", RegexOptions.IgnoreCase);
        if (cpu.Success)
            return $"{root}processors/ryzen/ryzen-{cpu.Groups[3].Value}000-series/" +
                   $"amd-ryzen-{cpu.Groups[1].Value}-{Slug(cpu.Groups[2].Value)}.html";

        return null;
    }

    /// <summary>Достаёт из страницы раздел нужной ОС: название, версию, дату и ссылку на установщик.</summary>
    public static (string title, string version, DateTime date, string link)? ParseDriverSection(string html, bool win11)
    {
        string[] headings = win11
            ? ["Windows 11 - 64-Bit Edition", "Windows 10 - 64-Bit Edition"]
            : ["Windows 10 - 64-Bit Edition", "Windows 11 - 64-Bit Edition"];

        // Заголовок ОС может встречаться несколько раз (вкладки, характеристики) — пробуем каждое вхождение.
        foreach (var heading in headings)
        for (int start = html.IndexOf(heading, StringComparison.Ordinal); start >= 0;
             start = html.IndexOf(heading, start + heading.Length, StringComparison.Ordinal))
        {
            var next = html.IndexOf("64-Bit Edition", start + heading.Length, StringComparison.Ordinal);
            var section = next > 0 ? html[start..next] : html[start..];

            // Полный пакет предпочтительнее веб-установщика (minimalsetup), который докачивает файлы сам.
            var link = Regex.Matches(section, @"https://drivers\.amd\.com/drivers/[^""'<>\s]+\.exe")
                .OrderBy(m => m.Value.Contains("minimalsetup", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault() ?? Match.Empty;
            var text = WebUtility.HtmlDecode(Regex.Replace(section, "<[^>]+>", " "));
            text = Regex.Replace(text, @"\s+", " ");
            var rev = Regex.Match(text, @"Revision Number\s+(Adrenalin\s+([\d\.]+)[^|]*?)\s+File Size");
            var date = Regex.Match(text, @"Release Date\s+(\d{4}-\d{2}-\d{2})");
            if (link.Success && rev.Success && date.Success)
                return (rev.Groups[1].Value.Trim(), rev.Groups[2].Value, DateTime.Parse(date.Groups[1].Value), link.Value);
        }
        return null;
    }

    private static string Slug(string s) => Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", "-");
}

/// <summary>
/// Intel: сайт intel.com закрыт от автоматических запросов, поэтому только даём ссылку
/// на Intel Driver &amp; Support Assistant. Драйверы Intel в основном приходят и через Windows Update.
/// </summary>
public sealed class IntelSource : IVendorSource
{
    public string Name => "intel.com";
    private const string Page = "https://www.intel.com/content/www/us/en/support/detect.html";
    private static readonly string[] Classes = ["Display", "Net", "Bluetooth"];

    public bool Handles(DeviceInfo d, SystemProfile sys) =>
        (VendorSources.IsVendor(d, "8086") || d.HardwareIds.Any(h => h.Contains("VID_8087", StringComparison.OrdinalIgnoreCase)))
        && Classes.Contains(d.DeviceClass, StringComparer.OrdinalIgnoreCase);

    public Task<VendorResult> CheckAsync(DeviceInfo d, SystemProfile sys) =>
        Task.FromResult(new VendorResult(null, Page));
}
