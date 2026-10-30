using System.Globalization;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DriverScout;

/// <summary>Установленный BIOS: из SMBIOS (Win32_BIOS).</summary>
public sealed record BiosInfo(string Version, DateTime? Date, string Vendor, string Device, string Board);

/// <summary>Версия BIOS у производителя.</summary>
public sealed record BiosRelease(string Version, DateTime? Date, string Title, string? DownloadUrl)
{
    /// <summary>true — новее установленной, false — та же или старее, null — версии в разных форматах, не сравнить.</summary>
    public bool? IsNewer { get; init; }
    public bool IsInstalled { get; init; }
    public string DateText => Date?.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU")) ?? "";
}

/// <summary>Что известно про BIOS этого компьютера.</summary>
public sealed record BiosReport(BiosInfo Installed, string? SourceName, string? PageUrl, List<BiosRelease> Releases, string? Error)
{
    public BiosRelease? Latest => Releases.FirstOrDefault();
}

/// <summary>
/// Версии BIOS от производителя. DriverScout BIOS не прошивает — это делается утилитой производителя
/// или из меню BIOS, с подключённым питанием; здесь только показываем, что вышло, и даём ссылку.
/// </summary>
public static class BiosService
{
    private const int MaxReleases = 10;

    public static BiosInfo ReadInstalled(SystemProfile sys)
    {
        string version = "", vendor = "";
        DateTime? date = null;
        using (var s = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, Manufacturer, ReleaseDate FROM Win32_BIOS"))
            foreach (ManagementObject mo in s.Get())
                using (mo)
                {
                    version = (mo["SMBIOSBIOSVersion"] as string ?? "").Trim();
                    vendor = (mo["Manufacturer"] as string ?? "").Trim();
                    // Дата в формате CIM: "20240315000000.000000+000".
                    if (mo["ReleaseDate"] is string d && d.Length >= 8 &&
                        DateTime.TryParseExact(d[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                        date = dt;
                }
        var device = string.Join(" ", new[] { sys.Manufacturer, sys.Family.Length > 0 && !sys.Model.Contains(sys.Family) ? sys.Family : "", sys.Model }
            .Where(x => x.Length > 0 && !IsPlaceholder(x)));
        var board = string.Join(" ", new[] { sys.BoardManufacturer, sys.BoardProduct }.Where(x => x.Length > 0 && !IsPlaceholder(x)));
        return new BiosInfo(version, date, vendor, device, board);
    }

    public static async Task<BiosReport> CheckAsync(SystemProfile sys)
    {
        var installed = ReadInstalled(sys);
        string? source = null, page = null;
        try
        {
            List<BiosRelease> releases;
            if (AsusModel(sys) is { } asus)
            {
                (source, page) = ("asus.com", asus.page);
                releases = await AsusAsync(asus.model, asus.cpu).ConfigureAwait(false);
            }
            else if (sys.Manufacturer.Equals("LENOVO", StringComparison.OrdinalIgnoreCase) && LenovoSource.MachineType(sys.Model) is { } mt)
            {
                (source, page) = ("lenovo.com", $"https://pcsupport.lenovo.com/search?query={mt}");
                releases = await LenovoAsync(mt, sys.IsWindows11).ConfigureAwait(false);
            }
            else if (sys.Manufacturer.StartsWith("Dell", StringComparison.OrdinalIgnoreCase) && DellSource.SystemId(sys) is { } dell)
            {
                (source, page) = ("dell.com", "https://www.dell.com/support/home/");
                releases = DellBios(await DellSource.LoadModelCatalogAsync(dell).ConfigureAwait(false));
            }
            else if ((sys.Manufacturer.Equals("HP", StringComparison.OrdinalIgnoreCase) || sys.Manufacturer.StartsWith("Hewlett", StringComparison.OrdinalIgnoreCase))
                     && HpSource.SystemId(sys) is { } hp)
            {
                (source, page) = ("hp.com", "https://support.hp.com/drivers");
                var (doc, _) = await HpSource.LoadReferenceAsync(hp, sys.IsWindows11, sys.OsRelease).ConfigureAwait(false);
                releases = HpBios(doc);
            }
            else
            {
                return new BiosReport(installed, null, SupportPage(sys), [], null);
            }

            releases = releases
                .GroupBy(r => r.Version, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderByDescending(r => r.Date ?? DateTime.MinValue)
                .Take(MaxReleases)
                .Select(r => r with { IsNewer = Compare(r.Version, installed.Version) is { } c ? c > 0 : null,
                                      IsInstalled = Compare(r.Version, installed.Version) == 0 })
                .ToList();
            return new BiosReport(installed, source, page, releases, releases.Count == 0 ? "Производитель не публикует BIOS для этой модели" : null);
        }
        catch (Exception ex)
        {
            return new BiosReport(installed, source, page ?? SupportPage(sys), [], ex.Message);
        }
    }

    // --- Сравнение версий ---

    /// <summary>
    /// Сравнивает версию производителя с установленной строкой SMBIOS. null — форматы разные, честно не сравнить.
    /// Установленная бывает разной: "E1504FA.320" (ASUS), "N3KET45W (1.45 )" (Lenovo), "1.18.0" (Dell), "U23 Ver. 01.12.00" (HP).
    /// </summary>
    public static int? Compare(string candidate, string installed)
    {
        var a = Numbers(candidate);
        var b = Numbers(InstalledToken(installed));
        if (a is null || b is null || a.Length != b.Length) return null;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return 0;
    }

    private static string InstalledToken(string installed)
    {
        if (Regex.Match(installed, @"\(\s*([\d.]+)\s*\)") is { Success: true } paren) return paren.Groups[1].Value; // Lenovo
        if (Regex.Match(installed, @"^[A-Z0-9_-]+\.(\d+)$", RegexOptions.IgnoreCase) is { Success: true } asus) return asus.Groups[1].Value; // ASUS
        return Regex.Matches(installed, @"\d+(?:\.\d+)*").LastOrDefault()?.Value ?? installed;
    }

    private static long[]? Numbers(string s)
    {
        var m = Regex.Match(s.Trim(), @"^\D*?(\d+(?:\.\d+)*)");
        if (!m.Success) return null;
        var parts = m.Groups[1].Value.Split('.');
        return parts.All(p => long.TryParse(p, out _)) ? parts.Select(long.Parse).ToArray() : null;
    }

    // --- ASUS: тот же API, что у страницы поддержки ---

    private static (string model, string cpu, string page)? AsusModel(SystemProfile sys)
    {
        if (sys.Manufacturer.StartsWith("ASUS", StringComparison.OrdinalIgnoreCase) && sys.IsLaptop && AsusSource.ModelCode(sys.Model) is { } code)
            return (code, code, $"https://www.asus.com/supportonly/{code.ToLowerInvariant()}/helpdesk_bios/");
        // Настольный компьютер: BIOS принадлежит материнской плате ("ROG STRIX B550-F GAMING").
        if (sys.BoardManufacturer.StartsWith("ASUS", StringComparison.OrdinalIgnoreCase) && sys.BoardProduct.Length > 0 && !IsPlaceholder(sys.BoardProduct))
            return (sys.BoardProduct, "", $"https://www.asus.com/search/?searchKey={Uri.EscapeDataString(sys.BoardProduct)}");
        return null;
    }

    private static async Task<List<BiosRelease>> AsusAsync(string model, string cpu)
    {
        var url = "https://www.asus.com/support/api/product.asmx/GetPDBIOS?website=global&model=" + Uri.EscapeDataString(model) +
                  "&pdhashedid=&pdid=99999&cpu=" + Uri.EscapeDataString(cpu);
        using var doc = JsonDocument.Parse(await VendorSources.GetStringWithRetryAsync(url).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("Result", out var r) || r.ValueKind != JsonValueKind.Object
            || !r.TryGetProperty("Obj", out var obj) || obj.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"ASUS не нашёл модель {model}");

        // Группы: "BIOS" (для EZ Flash), "BIOS Update(Windows)", "Firmware"… — берём именно BIOS.
        var groups = obj.EnumerateArray().ToList();
        var group = groups.FirstOrDefault(g => g.GetProperty("Name").GetString() == "BIOS");
        if (group.ValueKind == JsonValueKind.Undefined)
            group = groups.FirstOrDefault(g => (g.GetProperty("Name").GetString() ?? "").Contains("BIOS", StringComparison.OrdinalIgnoreCase));
        if (group.ValueKind == JsonValueKind.Undefined) return [];

        return group.GetProperty("Files").EnumerateArray().Select(f =>
        {
            var link = f.GetProperty("DownloadUrl").TryGetProperty("Global", out var g) ? g.GetString() : null;
            if (link is not null && link.StartsWith('/')) link = "https://dlcdnets.asus.com" + link;
            var title = f.GetProperty("Title").GetString();
            return new BiosRelease(
                f.GetProperty("Version").GetString() ?? "",
                DateTime.TryParseExact(f.GetProperty("ReleaseDate").GetString(), "yyyy/MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
                string.IsNullOrWhiteSpace(title) ? "BIOS" : title.Trim(),
                link);
        }).Where(x => x.Version.Length > 0).ToList();
    }

    // --- Lenovo: в каталоге модели у BIOS своя категория ---

    private static async Task<List<BiosRelease>> LenovoAsync(string machineType, bool win11)
    {
        var xml = await VendorSources.GetStringWithRetryAsync(
            $"https://download.lenovo.com/catalog/{machineType}_{(win11 ? "Win11" : "Win10")}.xml").ConfigureAwait(false);
        var urls = XDocument.Parse(xml.TrimStart('﻿')).Root!.Elements("package")
            .Where(p => ((string?)p.Element("category") ?? "").Contains("BIOS", StringComparison.OrdinalIgnoreCase))
            .Select(p => (string?)p.Element("location") ?? "")
            .Where(u => u.StartsWith("https://download.lenovo.com/", StringComparison.OrdinalIgnoreCase))
            .Take(MaxReleases).ToList();

        var result = new List<BiosRelease>();
        foreach (var url in urls)
        {
            try
            {
                var root = XDocument.Parse((await VendorSources.GetStringWithRetryAsync(url).ConfigureAwait(false)).TrimStart('﻿')).Root!;
                var name = (string?)root.Element("Files")?.Element("Installer")?.Element("File")?.Element("Name");
                result.Add(new BiosRelease(
                    (string?)root.Attribute("version") ?? "",
                    DateTime.TryParseExact((string?)root.Element("ReleaseDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
                    root.Element("Title")?.Elements("Desc").FirstOrDefault(e => (string?)e.Attribute("id") == "EN")?.Value.Trim() ?? "BIOS Update",
                    name is null ? null : url[..(url.LastIndexOf('/') + 1)] + name));
            }
            catch { /* битый пакет пропускаем */ }
        }
        return result.Where(r => r.Version.Length > 0).ToList();
    }

    // --- Dell и HP: в тех же каталогах, что и драйверы ---

    private static readonly XNamespace DellNs = "openmanage/cm/dm";

    private static List<BiosRelease> DellBios(XDocument doc) =>
        doc.Root!.Elements(DellNs + "SoftwareComponent")
            .Where(c => (string?)c.Element(DellNs + "ComponentType")?.Attribute("value") == "BIOS")
            .Select(c => new BiosRelease(
                (string?)c.Attribute("vendorVersion") ?? "",
                DateTime.TryParse((string?)c.Attribute("dateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : null,
                c.Element(DellNs + "Name")?.Element(DellNs + "Display")?.Value.Trim() ?? "BIOS",
                (string?)c.Attribute("path") is { Length: > 0 } p ? "https://downloads.dell.com/" + p : null))
            .Where(r => r.Version.Length > 0).ToList();

    private static List<BiosRelease> HpBios(XDocument doc) =>
        (doc.Root!.Element("Solutions")?.Elements("UpdateInfo") ?? [])
            .Where(u => ((string?)u.Element("Category") ?? "").Contains("BIOS", StringComparison.OrdinalIgnoreCase))
            .Select(u => new BiosRelease(
                (string?)u.Element("Version") ?? "",
                DateTime.TryParseExact((string?)u.Element("DateReleased"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
                (string?)u.Element("Name") ?? "BIOS",
                (string?)u.Element("Url") is { Length: > 0 } url ? (url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url) : null))
            .Where(r => r.Version.Length > 0).ToList();

    // --- Остальные: страница поддержки ---

    /// <summary>Страница поддержки, где искать BIOS вручную: бренд ноутбука или производитель материнской платы.</summary>
    public static string? SupportPage(SystemProfile sys)
    {
        var board = Uri.EscapeDataString(sys.BoardProduct);
        var maker = sys.BoardManufacturer;
        if (!sys.IsLaptop && sys.BoardProduct.Length > 0)
        {
            // MSI: "MPG B650 EDGE WIFI (MS-7E10)" → страница платы msi.com/Motherboard/MPG-B650-EDGE-WIFI/support, раздел BIOS.
            if (maker.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase))
                return $"https://www.msi.com/Motherboard/{Regex.Replace(Regex.Replace(sys.BoardProduct, @"\(.*?\)", "").Trim(), @"\s+", "-")}/support#bios";
            if (maker.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase)) return $"https://www.gigabyte.com/Search?kw={board}";
            if (maker.Contains("ASRock", StringComparison.OrdinalIgnoreCase)) return $"https://www.asrock.com/search/index.asp?Keyword={board}";
        }
        return OemBrands.Detect(sys)?.SupportUrl;
    }

    private static bool IsPlaceholder(string s) => OemBrands.Placeholder(s);
}
