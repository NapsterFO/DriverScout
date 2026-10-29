using System.Globalization;
using System.Xml.Linq;

namespace DriverScout;

/// <summary>
/// Lenovo: открытый каталог, которым пользуются Lenovo System Update и Vantage.
/// download.lenovo.com/catalog/{тип машины}_{Win10|Win11}.xml → список пакетов, у каждого пакета
/// свой XML с Hardware ID, минимальной версией драйвера и SHA-256 установщика.
/// Подходит и для ThinkPad, и для IdeaPad/Legion/Yoga.
/// </summary>
public sealed class LenovoSource : IVendorSource
{
    public string Name => "lenovo.com";

    private Lazy<Task<Catalog>>? _catalog;

    public sealed record Driver(List<string> HardwareIds, Version Version);
    public sealed record Package(string Title, string Version, DateTime? Date, string Url, string? Sha256, List<Driver> Drivers);
    public sealed record Catalog(string MachineType, string PageUrl, string? OsNote, List<Package> Packages);

    public bool Handles(DeviceInfo d, SystemProfile sys) =>
        sys.Manufacturer.Equals("LENOVO", StringComparison.OrdinalIgnoreCase)
        && d.HardwareIds.Length > 0 && MachineType(sys.Model) is not null;

    /// <summary>Тип машины — первые 4 символа модели: "82H8007HRK" → "82H8", "20XWCTO1WW" → "20XW".</summary>
    public static string? MachineType(string model) =>
        model.Length >= 4 && model[..4].All(char.IsLetterOrDigit) ? model[..4].ToUpperInvariant() : null;

    public async Task<VendorResult> CheckAsync(DeviceInfo d, SystemProfile sys)
    {
        _catalog ??= new Lazy<Task<Catalog>>(() => LoadCatalogAsync(MachineType(sys.Model)!, sys.IsWindows11));
        var catalog = await _catalog.Value.ConfigureAwait(false);

        var best = catalog.Packages
            .SelectMany(p => p.Drivers
                .Where(drv => drv.HardwareIds.Any(h => d.HardwareIds.Contains(h, StringComparer.OrdinalIgnoreCase)))
                .Select(drv => (pkg: p, version: drv.Version)))
            .OrderByDescending(x => x.version)
            .ThenByDescending(x => x.pkg.Date)
            .FirstOrDefault();

        if (best.pkg is null) return new VendorResult(null, null);

        var installed = VendorSources.ParseVersion(d.DriverVersion);
        if (!VendorSources.IsInboxDriver(d) && installed is not null && best.version <= installed)
            return new VendorResult(null, catalog.PageUrl);

        return new VendorResult(new DriverUpdate
        {
            Title = "Lenovo: " + best.pkg.Title,
            Source = Name,
            Kind = DriverKind.Installer,
            Version = best.version.ToString(),
            Date = best.pkg.Date,
            HardwareId = d.HardwareIds.FirstOrDefault(),
            DownloadUrl = best.pkg.Url,
            ExpectedSigner = "Lenovo",
            Sha256 = best.pkg.Sha256,
            PageUrl = catalog.PageUrl,
            Note = catalog.OsNote
        }, catalog.PageUrl);
    }

    public static async Task<Catalog> LoadCatalogAsync(string machineType, bool win11)
    {
        string[] oses = win11 ? ["Win11", "Win10"] : ["Win10", "Win11"];
        string? osNote = null;
        List<(string url, string category)> entries = [];
        foreach (var os in oses)
        {
            var xml = await VendorSources.GetStringWithRetryAsync($"https://download.lenovo.com/catalog/{machineType}_{os}.xml");
            entries = XDocument.Parse(xml.TrimStart('﻿')).Root!.Elements("package")
                .Select(p => ((string?)p.Element("location") ?? "", (string?)p.Element("category") ?? ""))
                .Where(p => p.Item1.StartsWith("https://download.lenovo.com/", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (entries.Count > 0) break;
            // Для своей ОС пусто — пробуем другую, но предупреждаем.
            osNote = $"Lenovo не выпускает для этой модели драйверы под {(win11 ? "Windows 11" : "Windows 10")} — взяты драйверы другой версии Windows";
        }
        if (entries.Count == 0) throw new InvalidOperationException($"В каталоге Lenovo нет модели {machineType}");

        // BIOS и прошивки не трогаем: их установка — отдельный рискованный процесс.
        entries = entries.Where(e => !e.category.Contains("BIOS", StringComparison.OrdinalIgnoreCase)
                                     && !e.category.Contains("Firmware", StringComparison.OrdinalIgnoreCase)).ToList();

        using var gate = new SemaphoreSlim(6);
        var tasks = entries.Select(async e =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { return ParsePackage(await VendorSources.GetStringWithRetryAsync(e.url).ConfigureAwait(false), e.url); }
            catch { return null; } // один битый пакет не должен ломать весь каталог
            finally { gate.Release(); }
        }).ToList();
        var packages = (await Task.WhenAll(tasks).ConfigureAwait(false)).OfType<Package>().ToList();

        return new Catalog(machineType, $"https://pcsupport.lenovo.com/search?query={machineType}", osNote, packages);
    }

    /// <summary>Разбирает XML пакета. null — пакет не драйвер (утилита, антивирус и т.п.).</summary>
    public static Package? ParsePackage(string xml, string descriptorUrl)
    {
        var root = XDocument.Parse(xml.TrimStart('﻿')).Root!;

        // Драйвером считаем пакет, у которого проверка установки идёт по драйверу устройства (_Driver).
        var drivers = root.Element("DetectInstall")?.Descendants("_Driver")
            .Select(drv => (ids: drv.Elements("HardwareID").Select(h => h.Value.Trim()).Where(h => h.Length > 0).ToList(),
                            version: VendorSources.ParseVersion((string?)drv.Element("Version"))))
            .Where(x => x.ids.Count > 0 && x.version is not null)
            .Select(x => new Driver(x.ids, x.version!))
            .ToList();
        if (drivers is null || drivers.Count == 0) return null;

        var file = root.Element("Files")?.Element("Installer")?.Element("File");
        var name = (string?)file?.Element("Name");
        if (string.IsNullOrEmpty(name)) return null;
        var baseUrl = descriptorUrl[..(descriptorUrl.LastIndexOf('/') + 1)];

        var crc = ((string?)file!.Element("CRC"))?.Trim();
        DateTime? date = DateTime.TryParseExact((string?)root.Element("ReleaseDate"), "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;

        return new Package(
            root.Element("Title")?.Elements("Desc").FirstOrDefault(e => (string?)e.Attribute("id") == "EN")?.Value.Trim()
                ?? (string?)root.Attribute("name") ?? name,
            (string?)root.Attribute("version") ?? "",
            date,
            baseUrl + name,
            crc is { Length: 64 } ? crc : null, // в поле CRC Lenovo кладёт SHA-256
            drivers);
    }
}
