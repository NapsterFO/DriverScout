using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace DriverScout;

/// <summary>
/// HP: справочники HP Image Assistant (hpia.hpcloud.hp.com).
/// platformList.cab — какие модели и выпуски Windows поддерживаются;
/// ref/{SystemID}/{SystemID}_64_{10.0|11.0}.{выпуск}.cab — для модели: устройства с Hardware ID,
/// версия драйвера и ссылка на пакет SoftPaq с SHA-256. Оба архива подписаны HP Inc.
/// </summary>
public sealed class HpSource : IVendorSource
{
    public string Name => "hp.com";
    private const string Root = "https://hpia.hpcloud.hp.com/ref/";
    private const string Signer = "HP Inc|Hewlett-Packard";
    private const string Page = "https://support.hp.com/drivers";

    private Lazy<Task<Catalog>>? _catalog;

    public sealed record SoftPaq(string Id, string Name, string Version, DateTime? Date, string Url, string? Sha256);
    public sealed record Entry(string HardwareId, Version Version, SoftPaq Package);
    public sealed record Catalog(string SystemId, string? OsNote, List<Entry> Entries);

    public bool Handles(DeviceInfo d, SystemProfile sys) =>
        IsHp(sys) && d.HardwareIds.Length > 0 && SystemId(sys) is not null;

    private static bool IsHp(SystemProfile sys) =>
        sys.Manufacturer.Equals("HP", StringComparison.OrdinalIgnoreCase)
        || sys.Manufacturer.StartsWith("Hewlett", StringComparison.OrdinalIgnoreCase);

    /// <summary>У HP идентификатор платформы — код материнской платы: 4 hex-символа ("8846").</summary>
    public static string? SystemId(SystemProfile sys) =>
        sys.BoardProduct is { Length: 4 } b && b.All(Uri.IsHexDigit) ? b.ToUpperInvariant() : null;

    public async Task<VendorResult> CheckAsync(DeviceInfo d, SystemProfile sys)
    {
        _catalog ??= new Lazy<Task<Catalog>>(() => LoadCatalogAsync(SystemId(sys)!, sys.IsWindows11, sys.OsRelease));
        var catalog = await _catalog.Value.ConfigureAwait(false);

        var best = catalog.Entries
            .Where(e => VendorSources.MatchesHardwareId(d, e.HardwareId))
            .OrderByDescending(e => e.Version)
            .ThenByDescending(e => e.Package.Date)
            .FirstOrDefault();
        if (best is null) return new VendorResult(null, null);

        var installed = VendorSources.ParseVersion(d.DriverVersion);
        if (!VendorSources.IsInboxDriver(d) && installed is not null && best.Version <= installed)
            return new VendorResult(null, Page);

        return new VendorResult(new DriverUpdate
        {
            Title = $"HP: {best.Package.Name} ({best.Package.Id})",
            Source = Name,
            Kind = DriverKind.Installer,
            Version = best.Version.ToString(),
            Date = best.Package.Date,
            HardwareId = d.HardwareIds.FirstOrDefault(),
            DownloadUrl = best.Package.Url,
            ExpectedSigner = Signer,
            Sha256 = best.Package.Sha256,
            PageUrl = Page,
            Note = catalog.OsNote
        }, Page);
    }

    public static async Task<Catalog> LoadCatalogAsync(string systemId, bool win11, string osRelease)
    {
        var work = CabCatalog.NewWorkDir("hp");
        try
        {
            // 1. Какие выпуски Windows HP поддерживает для этой модели.
            var listXml = await CabCatalog.DownloadAndExtractAsync(Root + "platformList.cab", Signer, Path.Combine(work, "list"));
            var platform = XDocument.Load(listXml).Root!.Elements("Platform")
                .FirstOrDefault(p => ((string?)p.Element("SystemID") ?? "")
                    .Split(',', StringSplitOptions.TrimEntries)
                    .Contains(systemId, StringComparer.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Модели HP с SystemID {systemId} нет в каталоге");

            var oses = platform.Elements("OS")
                .Select(o => (win11: (string?)o.Element("IsWindows11") == "true",
                              release: (string?)o.Element("OSReleaseIdFilename") ?? "",
                              build: (int?)o.Element("OSBuildId") ?? 0))
                .Where(o => o.release.Length > 0)
                .ToList();

            // 2. Свой выпуск, иначе самый свежий той же Windows, иначе другая Windows с пометкой.
            string? note = null;
            var sameMajor = oses.Where(o => o.win11 == win11).OrderByDescending(o => o.build).ToList();
            var os = sameMajor.FirstOrDefault(o => o.release.Equals(osRelease, StringComparison.OrdinalIgnoreCase));
            if (os.release is null) os = sameMajor.FirstOrDefault();
            if (os.release is null)
            {
                os = oses.OrderByDescending(o => o.build).FirstOrDefault();
                if (os.release is null) throw new InvalidOperationException("HP не публикует драйверы Windows для этой модели");
                note = $"HP выпускает драйверы для этой модели только под Windows {(os.win11 ? 11 : 10)}";
            }

            // 3. Справочник модели. В адресе выпуск пишется строчными: 8846_64_11.0.24h2.cab
            var refUrl = $"{Root}{systemId}/{systemId}_64_{(os.win11 ? "11.0" : "10.0")}.{os.release.ToLowerInvariant()}.cab";
            var refXml = await CabCatalog.DownloadAndExtractAsync(refUrl, Signer, Path.Combine(work, "ref"));
            return Parse(XDocument.Load(refXml), systemId, note);
        }
        finally
        {
            CabCatalog.TryDelete(work);
        }
    }

    public static Catalog Parse(XDocument doc, string systemId, string? note)
    {
        var root = doc.Root!;
        // Только драйверы: BIOS, прошивки, док-станции и утилиты не трогаем.
        var packages = root.Element("Solutions")?.Elements("UpdateInfo")
            .Where(u => ((string?)u.Element("Category") ?? "").StartsWith("Driver", StringComparison.OrdinalIgnoreCase))
            .Select(u => new SoftPaq(
                (string?)u.Element("Id") ?? "",
                (string?)u.Element("Name") ?? "",
                (string?)u.Element("Version") ?? "",
                DateTime.TryParseExact((string?)u.Element("DateReleased"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var dt) ? dt : null,
                ToHttps((string?)u.Element("Url") ?? ""),
                (string?)u.Element("SHA256")))
            .Where(p => p.Id.Length > 0 && p.Url.Length > 0)
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, SoftPaq>();

        // Устройства эталонной машины: Hardware ID → какой SoftPaq его обслуживает и какая версия драйвера в нём.
        var entries = new List<Entry>();
        foreach (var dev in root.Element("Devices")?.Elements("Device") ?? [])
        {
            // В части справочников HardwareId пустой, а ID лежит в DeviceId ("ACPI\HPQ6001").
            var hwid = ((string?)dev.Element("HardwareId"))?.Trim();
            if (string.IsNullOrEmpty(hwid)) hwid = ((string?)dev.Element("DeviceId"))?.Trim();
            var version = VendorSources.ParseVersion((string?)dev.Element("DriverVersion"));
            if (string.IsNullOrEmpty(hwid) || version is null) continue;
            foreach (var r in dev.Element("Solutions")?.Elements("UpdateInfo") ?? [])
                if ((string?)r.Attribute("IdRef") is { } id && packages.TryGetValue(id, out var pkg))
                    entries.Add(new Entry(hwid, version, pkg));
        }
        return new Catalog(systemId, note, entries);
    }

    // В справочнике ссылки без схемы: "ftp.hp.com/pub/softpaq/sp135501-136000/sp135756.exe".
    private static string ToHttps(string url) =>
        url.Length == 0 || url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;
}
