using System.Globalization;
using System.Text.Json;

namespace DriverScout;

/// <summary>
/// ASUS: тот же API, которым пользуется страница поддержки asus.com.
/// Для каждого пакета ASUS публикует список Hardware ID с версиями и SHA-256 файла,
/// поэтому сопоставление с устройствами точное.
/// </summary>
public sealed class AsusSource : IVendorSource
{
    public string Name => "asus.com";
    private const string Api = "https://www.asus.com/support";
    private const string Cdn = "https://dlcdnets.asus.com";

    private Lazy<Task<Catalog>>? _catalog;

    private sealed record Package(string Title, string FileVersion, DateTime? Date, string Url, string? Sha256,
                                  List<(string hwid, string version)> HardwareIds);
    private sealed record Catalog(string Model, string PageUrl, string? OsNote, List<Package> Packages);

    public bool Handles(DeviceInfo d, SystemProfile sys) =>
        sys.Manufacturer.StartsWith("ASUS", StringComparison.OrdinalIgnoreCase) && sys.IsLaptop
        && d.HardwareIds.Length > 0 && ModelCode(sys.Model) is not null;

    public async Task<VendorResult> CheckAsync(DeviceInfo d, SystemProfile sys)
    {
        _catalog ??= new Lazy<Task<Catalog>>(() => LoadCatalogAsync(sys));
        var catalog = await _catalog.Value.ConfigureAwait(false);

        // Все пакеты, где есть Hardware ID этого устройства; берём самую новую версию драйвера.
        var best = catalog.Packages
            .SelectMany(p => p.HardwareIds
                .Where(h => !h.hwid.StartsWith(@"MONITOR\", StringComparison.OrdinalIgnoreCase)) // это утилиты цвета, а не драйверы
                .Where(h => d.HardwareIds.Contains(h.hwid, StringComparer.OrdinalIgnoreCase))
                .Select(h => (pkg: p, version: ParseVersion(h.version), raw: h.version)))
            .Where(x => x.version is not null)
            .OrderByDescending(x => x.version)
            .ThenByDescending(x => x.pkg.Date)
            .FirstOrDefault();

        if (best.pkg is null) return new VendorResult(null, null); // ASUS для этого устройства ничего не публикует

        var installed = ParseVersion(d.DriverVersion);
        if (!VendorSources.IsInboxDriver(d) && installed is not null && best.version <= installed)
            return new VendorResult(null, catalog.PageUrl);

        return new VendorResult(new DriverUpdate
        {
            Title = $"ASUS: {best.pkg.Title} {best.pkg.FileVersion}",
            Source = Name,
            Kind = DriverKind.Installer,
            Version = best.raw,
            Date = best.pkg.Date,
            HardwareId = d.HardwareIds.FirstOrDefault(),
            DownloadUrl = best.pkg.Url,
            ExpectedSigner = "ASUSTeK",
            Sha256 = best.pkg.Sha256,
            PageUrl = catalog.PageUrl,
            Note = catalog.OsNote
        }, catalog.PageUrl);
    }

    /// <summary>"Vivobook Go E1504FA_E1504FA" → "E1504FA".</summary>
    public static string? ModelCode(string model)
    {
        var last = model.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        var code = last?.Split('_')[0];
        return code is { Length: >= 4 } && code.Any(char.IsDigit) ? code : null;
    }

    private static async Task<Catalog> LoadCatalogAsync(SystemProfile sys)
    {
        var model = ModelCode(sys.Model)!;
        var common = $"website=global&model={Uri.EscapeDataString(model.ToLowerInvariant())}&pdhashedid=&pdid=99999";

        // 1. Вариант комплектации («cpu» в терминах сайта): обычно совпадает с кодом модели.
        string cpu = "";
        using (var doc = JsonDocument.Parse(await VendorSources.GetStringWithRetryAsync($"{Api}/api/product.asmx/GetPDCPUName?{common}")))
        {
            var names = Items(doc.RootElement).Select(e => e.GetProperty("Name").GetString() ?? "").ToList();
            cpu = names.FirstOrDefault(n => n.Equals(model, StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault() ?? "";
        }
        common += "&cpu=" + Uri.EscapeDataString(cpu);

        // 2. ОС: ищем свою; если ASUS выпускает драйверы только под другую версию Windows — берём её с пометкой.
        string? osId = null, osNote = null;
        using (var doc = JsonDocument.Parse(await VendorSources.GetStringWithRetryAsync($"{Api}/api/product.asmx/GetPDOS?{common}")))
        {
            var oses = Items(doc.RootElement)
                .Select(e => (id: e.GetProperty("Id").GetString(), name: e.GetProperty("Name").GetString() ?? ""))
                .Where(o => o.name.Contains("Windows", StringComparison.OrdinalIgnoreCase) && o.name.Contains("64"))
                .ToList();
            var wanted = sys.IsWindows11 ? "Windows 11" : "Windows 10";
            var match = oses.FirstOrDefault(o => o.name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
            if (match.id is null && oses.Count > 0)
            {
                match = oses[0];
                osNote = $"ASUS выпускает драйверы для этой модели только под {match.name}";
            }
            osId = match.id;
        }
        if (osId is null) throw new InvalidOperationException($"ASUS не публикует драйверы Windows для модели {model}");

        // 3. Список пакетов.
        var packages = new List<Package>();
        using (var doc = JsonDocument.Parse(await VendorSources.GetStringWithRetryAsync($"{Api}/webapi/ProductV2/GetPDDrivers?{common}&osid={osId}")))
        {
            foreach (var group in Items(doc.RootElement))
            foreach (var f in group.GetProperty("Files").EnumerateArray())
            {
                var path = f.GetProperty("DownloadUrl").TryGetProperty("Global", out var g) ? g.GetString() : null;
                // Ссылки на Microsoft Store и прочие внешние страницы пропускаем — ставим только файлы с CDN ASUS.
                if (path is null || !path.StartsWith("/pub/", StringComparison.OrdinalIgnoreCase)) continue;
                if (f.GetProperty("HardwareInfoList").ValueKind != JsonValueKind.Array) continue;

                var ids = f.GetProperty("HardwareInfoList").EnumerateArray()
                    .Select(h => (hwid: h.GetProperty("hardwareid").GetString() ?? "", version: h.GetProperty("version").GetString() ?? ""))
                    .Where(h => h.hwid.Length > 0)
                    .ToList();
                if (ids.Count == 0) continue;

                packages.Add(new Package(
                    f.GetProperty("Title").GetString() ?? "",
                    f.GetProperty("Version").GetString() ?? "",
                    DateTime.TryParseExact(f.GetProperty("ReleaseDate").GetString(), "yyyy/MM/dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null,
                    Cdn + path,
                    f.TryGetProperty("sha256", out var sha) ? sha.GetString() : null,
                    ids));
            }
        }

        return new Catalog(model, $"https://www.asus.com/supportonly/{model.ToLowerInvariant()}/helpdesk_download/", osNote, packages);
    }

    private static IEnumerable<JsonElement> Items(JsonElement root) =>
        root.TryGetProperty("Result", out var r) && r.ValueKind == JsonValueKind.Object
        && r.TryGetProperty("Obj", out var obj) && obj.ValueKind == JsonValueKind.Array
            ? obj.EnumerateArray()
            : [];

    private static Version? ParseVersion(string? s) => VendorSources.ParseVersion(s);
}
