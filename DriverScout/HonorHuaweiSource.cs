using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DriverScout;

/// <summary>
/// Honor и Huawei: у обоих сайт поддержки работает на одном движке (CCPC). Тот же API, которым пользуется
/// страница модели, отдаёт дерево продуктов и список драйверов по категориям. Hardware ID и SHA-256 в нём нет,
/// поэтому драйвер подбирается по названию пакета («IntelGraphics 31.0.101.4032», «RealtekAudio 6.0.9721.1»):
/// тип устройства + производитель чипа, а версия в названии совпадает с версией драйвера в Windows.
/// </summary>
public sealed class HonorHuaweiSource : IVendorSource
{
    private sealed record Site(string Code, string Country);
    private sealed record Config(string Brand, string Manufacturer, string Name, string Host, string Channel,
                                 string LaptopCategory, Site[] Sites, string PageUrl, string Signer);

    // Российский каталог Honor неполный: модели новее 2025 года есть только на сайте «Евразия».
    private static readonly Config Honor = new("Honor", "HONOR", "honor.com", "selfservice-ap.honor.com", "MYHONORWEB",
        "CMCG10000120", [new("ru_RU", "RU"), new("ru_UZ_X", "UZ")], "https://www.honor.com/ru/support/", "HONOR");
    private static readonly Config Huawei = new("Huawei", "HUAWEI", "huawei.com", "ccpce-ru.consumer.huawei.com", "WEBSITE",
        "CMCG10000004", [new("ru_RU", "RU")], "https://consumer.huawei.com/ru/support/laptops/", "Huawei");

    private readonly Config _cfg;
    private Lazy<Task<Catalog>>? _catalog;

    private HonorHuaweiSource(Config cfg) => _cfg = cfg;
    public static HonorHuaweiSource ForHonor() => new(Honor);
    public static HonorHuaweiSource ForHuawei() => new(Huawei);

    public string Name => _cfg.Name;

    private sealed record Product(string Code, string DisplayName, string OfferingCode, Site Site);
    public sealed record Package(string Name, Version Version, string RawVersion, DateTime? Date, string Url, string FileName);
    private sealed record Catalog(string Model, List<Package> Packages);

    public bool Handles(DeviceInfo d, SystemProfile sys) =>
        sys.Manufacturer.Equals(_cfg.Manufacturer, StringComparison.OrdinalIgnoreCase) && sys.IsLaptop
        && d.HardwareIds.Length > 0 && ModelCode(sys.Model) is not null;

    /// <summary>Код модели из SMBIOS: "BRN-F56", "MCLF-XX", "KLVL-WXX9".</summary>
    public static string? ModelCode(string model) =>
        Regex.IsMatch(model.Trim(), @"^[A-Za-z0-9]{2,8}-[A-Za-z0-9]{1,8}$") ? model.Trim() : null;

    public async Task<VendorResult> CheckAsync(DeviceInfo d, SystemProfile sys)
    {
        _catalog ??= new Lazy<Task<Catalog>>(() => LoadCatalogAsync(sys));
        var catalog = await _catalog.Value.ConfigureAwait(false);

        var installed = VendorSources.ParseVersion(d.DriverVersion);
        var inbox = VendorSources.IsInboxDriver(d) || installed is null;
        var best = catalog.Packages
            .Where(p => Fits(p.Name, d, inbox))
            // Пакет без производителя чипа в названии («Audio 6.0.9963.1») — только того же семейства версий,
            // что уже стоит. С производителем (IntelWiFi 22.x → 24.x) версии растут как угодно.
            .Where(p => inbox || Chip(p.Name) is not null || p.Version.Major == installed!.Major)
            .OrderByDescending(p => p.Version).ThenByDescending(p => p.Date)
            .FirstOrDefault();

        if (best is null) return new VendorResult(null, null);
        if (!inbox && best.Version <= installed) return new VendorResult(null, _cfg.PageUrl);

        return new VendorResult(new DriverUpdate
        {
            Title = $"{_cfg.Brand}: {best.Name} {best.RawVersion}",
            Source = Name,
            Kind = DriverKind.Installer,
            Version = best.RawVersion,
            Date = best.Date,
            HardwareId = d.HardwareIds.FirstOrDefault(),
            DownloadUrl = best.Url,
            FileName = best.FileName,
            TrustArchiveFromVendor = true,
            ExpectedSigner = _cfg.Signer,
            PageUrl = _cfg.PageUrl,
            // Подобрано по названию, а не по Hardware ID — к установке не отмечаем, пусть решает пользователь.
            Note = $"{_cfg.Brand} не указывает Hardware ID — драйвер подобран по названию пакета для модели {catalog.Model}"
        }, _cfg.PageUrl);
    }

    /// <summary>Подходит ли пакет к устройству: тип устройства по названию пакета и производитель чипа.</summary>
    public static bool Fits(string name, DeviceInfo d, bool inbox)
    {
        var cls = d.DeviceClass ?? "";
        var ids = string.Join(" ", d.HardwareIds);
        var chip = Chip(name);

        if (Regex.IsMatch(name, "Graphics|GFX", RegexOptions.IgnoreCase))
            return cls.Equals("Display", StringComparison.OrdinalIgnoreCase) && chip switch
            {
                "INTEL" => VendorSources.IsVendor(d, "8086"),
                "AMD" => VendorSources.IsVendor(d, "1002"),
                "NVIDIA" => VendorSources.IsVendor(d, "10DE"),
                _ => false
            };

        if (Regex.IsMatch(name, "WLAN|WiFi|Wireless", RegexOptions.IgnoreCase))
            return cls.Equals("Net", StringComparison.OrdinalIgnoreCase)
                   && Regex.IsMatch(d.Name, @"Wi-?Fi|Wireless|WLAN|802\.11", RegexOptions.IgnoreCase)
                   && ChipMatches(chip, ids, "VEN_8086", "VEN_10EC", "VEN_14C3", "VEN_17CB");

        if (Regex.IsMatch(name, @"BT$|Bluetooth", RegexOptions.IgnoreCase))
            // Только сам адаптер (USB), а не виртуальные устройства профилей Bluetooth.
            return cls.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase)
                   && ids.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)
                   && ChipMatches(chip, ids, "VID_8087", "VID_0BDA", "VID_0E8D", "VID_0CF3");

        if (Regex.IsMatch(name, "Audio$", RegexOptions.IgnoreCase) && !name.StartsWith("Intel", StringComparison.OrdinalIgnoreCase))
        {
            // Аудиокодек на шине HD Audio; звук по HDMI (кодеки Intel, AMD, NVIDIA) — не он.
            if (!cls.Equals("MEDIA", StringComparison.OrdinalIgnoreCase)
                || !ids.StartsWith(@"HDAUDIO\FUNC_01", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(ids, "VEN_(8086|1002|10DE)", RegexOptions.IgnoreCase)) return false;
            if (chip == "REALTEK") return ids.Contains("VEN_10EC", StringComparison.OrdinalIgnoreCase);
            // Название без производителя («Audio», «SenaryAudio») — только поверх уже стоящего драйвера того же семейства.
            return !inbox;
        }
        return false; // чипсет, NPU, камера, сканер отпечатков и прочее — по названию надёжно не сопоставить
    }

    /// <summary>Производитель чипа в начале названия пакета: "IntelWLAN" → "INTEL".</summary>
    private static string? Chip(string name) =>
        Regex.Match(name, @"^(Intel|AMD|Nvidia|Realtek|MediaTek|MTK|Qualcomm)", RegexOptions.IgnoreCase) is { Success: true } m
            ? m.Value.ToUpperInvariant() : null;

    private static bool ChipMatches(string? chip, string ids, string intel, string realtek, string mediatek, string qualcomm)
    {
        var tag = chip switch
        {
            "INTEL" => intel,
            "REALTEK" => realtek,
            "MEDIATEK" or "MTK" => mediatek,
            "QUALCOMM" => qualcomm,
            _ => null
        };
        return tag is not null && ids.Contains(tag, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "IntelGraphics 31.0.101.4032_FirmwareAATN" → ("IntelGraphics", "31.0.101.4032");
    /// "HUAWEI_MateBook_D_16_2024_12th _Gen_Core_SenaryAudio_3.40.0.40" → ("SenaryAudio", "3.40.0.40").
    /// </summary>
    public static (string name, string version)? ParseTitle(string title)
    {
        var v = Regex.Matches(title, @"\d+\.\d+\.\d+(?:\.\d+)?").LastOrDefault();
        if (v is null) return null;
        var name = Regex.Match(title[..v.Index], @"([A-Za-z]+)[\s_-]*$");
        return name.Success ? (name.Groups[1].Value, v.Value) : null;
    }

    private async Task<Catalog> LoadCatalogAsync(SystemProfile sys)
    {
        var model = ModelCode(sys.Model)!;
        foreach (var site in _cfg.Sites)
        {
            var product = PickProduct(await LoadProductsAsync(site).ConfigureAwait(false), model, sys.CpuName);
            if (product is null) continue;
            return new Catalog(product.DisplayName, await LoadPackagesAsync(product).ConfigureAwait(false));
        }
        throw new InvalidOperationException($"Модели {model} нет в каталоге {_cfg.Brand}");
    }

    /// <summary>Все ноутбуки сайта: серия → продукт → комплектация (у неё код модели в скобках и offeringCode).</summary>
    private async Task<List<Product>> LoadProductsAsync(Site site)
    {
        async Task<List<JsonElement>> List(string id, string level)
        {
            var json = await GetAsync($"ccpc/queryNewCommodityList/1000?productLevel={level}&productId={id}", site).ConfigureAwait(false);
            return [.. Arr(json, "productList")];
        }

        using var gate = new SemaphoreSlim(6);
        async Task<List<JsonElement>> Gated(string id, string level)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { return await List(id, level).ConfigureAwait(false); }
            finally { gate.Release(); }
        }

        var series = await List(_cfg.LaptopCategory, "lv3").ConfigureAwait(false);
        var products = (await Task.WhenAll(series.Select(s => Gated(Str(s, "productId"), "lv4"))).ConfigureAwait(false)).SelectMany(x => x);
        var items = (await Task.WhenAll(products.Select(p => Gated(Str(p, "productId"), "lv5"))).ConfigureAwait(false)).SelectMany(x => x);

        return items
            .Select(e => (name: Str(e, "displayName").Trim(), offering: Str(e, "offeringCode")))
            .Select(e => (e.name, e.offering, code: Regex.Match(e.name, @"\(([A-Za-z0-9]+-[A-Za-z0-9]+)\s*\)$")))
            .Where(e => e.code.Success && e.offering.Length > 0)
            .Select(e => new Product(e.code.Groups[1].Value, e.name, e.offering, site))
            .ToList();
    }

    /// <summary>
    /// Точное совпадение кода или та же серия: в SMBIOS часть после дефиса бывает обобщённой
    /// ("MCLF-XX" на сайте "MCLF-X", "KLVL-WXX9" — "KLVL-W56W"), а первая буква после дефиса различает
    /// платформы ("BRN-F56" Intel, "BRN-H56" AMD). Из комплектаций берём ту, где упомянут наш процессор.
    /// </summary>
    private static Product? PickProduct(List<Product> products, string model, string cpu)
    {
        var exact = products.Where(p => p.Code.Equals(model, StringComparison.OrdinalIgnoreCase)).ToList();
        var dash = model.IndexOf('-');
        var candidates = exact.Count > 0 ? exact : products.Where(p =>
        {
            var i = p.Code.IndexOf('-');
            return p.Code[..i].Equals(model[..dash], StringComparison.OrdinalIgnoreCase)
                   && char.ToUpperInvariant(p.Code[i + 1]) == char.ToUpperInvariant(model[dash + 1]);
        }).ToList();

        var cpuModel = Regex.Match(cpu, @"\b(\d{4,5}[A-Z]{0,2})\b").Value;
        var tier = Regex.Match(cpu, @"\b(i[3579])-|Ultra (\d)|Ryzen (\d)") is { Success: true } t
            ? t.Groups[1].Success ? t.Groups[1].Value : t.Groups[2].Success ? "Ultra " + t.Groups[2].Value : "R" + t.Groups[3].Value
            : null;
        return candidates
            .OrderByDescending(p => (cpuModel.Length > 0 && p.DisplayName.Contains(cpuModel) ? 2 : 0)
                                    + (tier is not null && p.DisplayName.Contains(tier) ? 1 : 0))
            .FirstOrDefault();
    }

    private async Task<List<Package>> LoadPackagesAsync(Product product)
    {
        var q = $"orderType=1&page=1&offeringCode={product.OfferingCode}&cflag=1&driverDown=true";
        var catalog = await GetAsync($"knowledgeBase/getKnowCatalog/1000?knowTypeId=63&pageSize=20&{q}", product.Site).ConfigureAwait(false);
        var categories = Arr(catalog, "catalogList").Select(e => Str(e, "knowTypeId"))
            // PC Manager — приложение, BIOS не трогаем (как и у Lenovo).
            .Where(id => id.Length > 0 && id is not ("630" or "6310")).ToList();

        var lists = await Task.WhenAll(categories.Select(id =>
            GetAsync($"knowledgeBase/getServicePolicy/1000?knowTypeId={id}&pageSize=50&{q}", product.Site))).ConfigureAwait(false);

        var packages = new List<Package>();
        foreach (var list in lists)
        {
            foreach (var e in Arr(list, "detailList"))
            {
                var url = Str(e, "downloadUrl");
                if (ParseTitle(Str(e, "resourceTitle")) is not { } t || VendorSources.ParseVersion(t.version) is not { } version
                    || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
                packages.Add(new Package(t.name, version, t.version,
                    DateTime.TryParseExact(Str(e, "updateTime"), "yyyy/M/d H:mm:ss", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var dt) ? dt.Date : null,
                    url, Str(e, "attachmentName")));
            }
        }
        return packages;
    }

    /// <summary>Запрос к API. Ответ бывает обёрнут в JSONP "(…)" — берём объект внутри; возвращаем responseData.</summary>
    private async Task<JsonElement> GetAsync(string pathAndQuery, Site site)
    {
        var url = $"https://{_cfg.Host}/ccpcmd/services/dispatch/secured/CCPC/EN/{pathAndQuery}" +
                  $"&siteCode={site.Code}&channelCode={_cfg.Channel}&countryCode={site.Country}&areaCode={site.Country}" +
                  $"&country={site.Country}&langCode=ru&language=ru";
        var text = await VendorSources.GetStringWithRetryAsync(url).ConfigureAwait(false);
        int start = text.IndexOf('{'), end = text.LastIndexOf('}');
        if (start < 0 || end < start) throw new InvalidOperationException($"{_cfg.Brand}: неожиданный ответ сервера");
        using var doc = JsonDocument.Parse(text[start..(end + 1)]);
        return doc.RootElement.TryGetProperty("responseData", out var data) && data.ValueKind == JsonValueKind.Object
            ? data.Clone() : default;
    }

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray() : [];

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
