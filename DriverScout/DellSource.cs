using System.IO;
using System.Xml.Linq;

namespace DriverScout;

/// <summary>
/// Dell: каталоги Dell Command | Update (downloads.dell.com/catalog).
/// CatalogIndexPC.cab — по System ID модели находит её каталог; в каталоге модели у каждого драйвера
/// перечислены PCI/USB ID устройств, поддерживаемые Windows и SHA-256 пакета. Архивы подписаны Dell.
/// </summary>
public sealed class DellSource : IVendorSource
{
    public string Name => "dell.com";
    private const string Base = "https://downloads.dell.com/";
    private const string Signer = "Dell";
    private const string Page = "https://www.dell.com/support/home/";
    private static readonly XNamespace Ns = "openmanage/cm/dm";

    private Lazy<Task<Catalog>>? _catalog;

    /// <summary>ID устройства и версия драйвера, которую пакет для него ставит.</summary>
    public sealed record DeviceEntry(string HardwareId, Version Version);
    public sealed record Package(string Name, string Version, DateTime? Date, string Url, string? Sha256,
                                 List<DeviceEntry> Devices, bool Win10, bool Win11);
    public sealed record Catalog(string SystemId, string? OsNote, List<Package> Packages);

    public bool Handles(DeviceInfo d, SystemProfile sys) =>
        sys.Manufacturer.StartsWith("Dell", StringComparison.OrdinalIgnoreCase)
        && d.HardwareIds.Length > 0 && SystemId(sys) is not null;

    /// <summary>System ID модели Dell — SKU из SMBIOS: 4 hex-символа ("0A21").</summary>
    public static string? SystemId(SystemProfile sys) =>
        sys.SystemSku is { Length: 4 } s && s.All(Uri.IsHexDigit) ? s.ToUpperInvariant() : null;

    public async Task<VendorResult> CheckAsync(DeviceInfo d, SystemProfile sys)
    {
        _catalog ??= new Lazy<Task<Catalog>>(() => LoadCatalogAsync(SystemId(sys)!, sys.IsWindows11));
        var catalog = await _catalog.Value.ConfigureAwait(false);

        var best = catalog.Packages
            .SelectMany(p => p.Devices
                .Where(e => VendorSources.MatchesHardwareId(d, e.HardwareId))
                .Select(e => (pkg: p, version: e.Version)))
            .OrderByDescending(x => x.version)
            .ThenByDescending(x => x.pkg.Date)
            .FirstOrDefault();
        if (best.pkg is null) return new VendorResult(null, null);

        var installed = VendorSources.ParseVersion(d.DriverVersion);
        if (!VendorSources.IsInboxDriver(d) && installed is not null && best.version <= installed)
            return new VendorResult(null, Page);

        return new VendorResult(new DriverUpdate
        {
            Title = "Dell: " + best.pkg.Name,
            Source = Name,
            Kind = DriverKind.Installer,
            Version = best.version.ToString(),
            Date = best.pkg.Date,
            HardwareId = d.HardwareIds.FirstOrDefault(),
            DownloadUrl = best.pkg.Url,
            ExpectedSigner = Signer,
            Sha256 = best.pkg.Sha256,
            PageUrl = Page,
            Note = catalog.OsNote
        }, Page);
    }

    public static async Task<Catalog> LoadCatalogAsync(string systemId, bool win11)
    {
        var all = Parse(await LoadModelCatalogAsync(systemId));

        // Драйверы под свою Windows; если их нет совсем — под другую, с пометкой.
        var own = all.Where(p => win11 ? p.Win11 : p.Win10).ToList();
        if (own.Count > 0) return new Catalog(systemId, null, own);
        var other = all.Where(p => win11 ? p.Win10 : p.Win11).ToList();
        return new Catalog(systemId, $"Dell выпускает драйверы для этой модели только под Windows {(win11 ? 10 : 11)}", other);
    }

    /// <summary>Каталог модели целиком: драйверы, BIOS, прошивки.</summary>
    public static async Task<XDocument> LoadModelCatalogAsync(string systemId)
    {
        var work = CabCatalog.NewWorkDir("dell");
        try
        {
            // 1. Индекс: System ID → путь к каталогу модели и его SHA-256.
            var indexXml = await CabCatalog.DownloadAndExtractAsync(Base + "catalog/CatalogIndexPC.cab", Signer, Path.Combine(work, "index"));
            var manifest = XDocument.Load(indexXml).Root!.Elements(Ns + "GroupManifest")
                .Where(g => g.Descendants(Ns + "Model").Any(m =>
                    string.Equals((string?)m.Attribute("systemID"), systemId, StringComparison.OrdinalIgnoreCase)))
                .Select(g => g.Element(Ns + "ManifestInformation"))
                .FirstOrDefault(m => m?.Attribute("path") is not null)
                ?? throw new InvalidOperationException($"Модели Dell с System ID {systemId} нет в каталоге");

            // 2. Каталог модели.
            var modelXml = await CabCatalog.DownloadAndExtractAsync(
                Base + (string)manifest.Attribute("path")!, Signer, Path.Combine(work, "model"), Sha256Of(manifest));
            return XDocument.Load(modelXml);
        }
        finally
        {
            CabCatalog.TryDelete(work);
        }
    }

    public static List<Package> Parse(XDocument doc)
    {
        var result = new List<Package>();
        foreach (var c in doc.Root!.Elements(Ns + "SoftwareComponent"))
        {
            // Только драйверы: BIOS (BIOS), прошивки (FRMW) и приложения (APAC) не трогаем,
            // пакеты для док-станций (DK) тоже — Dell иногда указывает в них ID чужих устройств.
            if ((string?)c.Element(Ns + "ComponentType")?.Attribute("value") != "DRVR") continue;
            if ((string?)c.Element(Ns + "Category")?.Attribute("value") == "DK") continue;
            var path = (string?)c.Attribute("path");
            if (string.IsNullOrEmpty(path)) continue;

            var packageVersion = VendorSources.ParseVersion((string?)c.Attribute("vendorVersion"));
            var devices = new List<DeviceEntry>();
            // Два формата: новый SupportedDCHDevices (с версией драйвера для каждого устройства) и старый SupportedDevices.
            var deviceNodes = (c.Element(Ns + "SupportedDCHDevices")?.Elements(Ns + "Device") ?? [])
                .Concat(c.Element(Ns + "SupportedDevices")?.Elements(Ns + "Device") ?? []);
            foreach (var dev in deviceNodes)
            {
                var version = VendorSources.ParseVersion((string?)dev.Attribute("version")) ?? packageVersion;
                if (version is null) continue;
                foreach (var id in DeviceIds(dev)) devices.Add(new DeviceEntry(id, version));
            }
            if (devices.Count == 0) continue; // без ID устройства не сопоставить

            var oses = (c.Element(Ns + "SupportedOperatingSystems")?.Elements(Ns + "OperatingSystem") ?? [])
                .Where(o => (string?)o.Attribute("osArch") == "x64")
                .Select(o => o.Element(Ns + "Display")?.Value.Trim() ?? "")
                .ToList();

            result.Add(new Package(
                c.Element(Ns + "Name")?.Element(Ns + "Display")?.Value.Trim() ?? Path.GetFileName(path),
                (string?)c.Attribute("vendorVersion") ?? "",
                DateTime.TryParse((string?)c.Attribute("dateTime"), out var dt) ? dt.Date : null,
                Base + path,
                Sha256Of(c),
                devices,
                Win10: oses.Any(o => o.StartsWith("Windows 10 64", StringComparison.OrdinalIgnoreCase)),
                Win11: oses.Any(o => o.StartsWith("Windows 11", StringComparison.OrdinalIgnoreCase))));
        }
        return result;
    }

    /// <summary>ID устройства из PCIInfo (VEN/DEV/SUBSYS), PnPInfo (USB VID/PID) или Generic (готовая строка).</summary>
    private static IEnumerable<string> DeviceIds(XElement dev)
    {
        foreach (var pci in dev.Elements(Ns + "PCIInfo"))
        {
            string ven = (string?)pci.Attribute("vendorID") ?? "", id = (string?)pci.Attribute("deviceID") ?? "";
            string subDev = (string?)pci.Attribute("subDeviceID") ?? "", subVen = (string?)pci.Attribute("subVendorID") ?? "";
            if (ven.Length == 0 || id.Length == 0) continue;
            yield return subDev.Length > 0 && subVen.Length > 0
                ? $@"PCI\VEN_{ven}&DEV_{id}&SUBSYS_{subDev}{subVen}"
                : $@"PCI\VEN_{ven}&DEV_{id}";
        }
        foreach (var pnp in dev.Elements(Ns + "PnPInfo"))
        {
            string vid = (string?)pnp.Element(Ns + "PNPID") ?? "", pid = (string?)pnp.Element(Ns + "PnPProductID") ?? "";
            if (vid.Length == 4 && pid.Length == 4 && vid.All(Uri.IsHexDigit) && pid.All(Uri.IsHexDigit))
                yield return $@"USB\VID_{vid}&PID_{pid}";
        }
        foreach (var g in dev.Elements(Ns + "Generic"))
            if (g.Value.Trim() is { Length: > 0 } s) yield return s;
    }

    private static string? Sha256Of(XElement e) =>
        e.Element(Ns + "Cryptography")?.Elements(Ns + "Hash")
            .FirstOrDefault(h => (string?)h.Attribute("algorithm") == "SHA256")?.Value.Trim();
}
