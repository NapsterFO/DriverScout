using System.Text.RegularExpressions;

namespace DriverScout;

/// <summary>Что известно о производителе компьютера.</summary>
/// <param name="AutoCheck">true — драйверы проверяются автоматически по официальному каталогу.</param>
public sealed record OemInfo(string Brand, string Model, string SupportUrl, string? Utility, bool AutoCheck, string? Remark);

/// <summary>
/// Справочник брендов ноутбуков: как узнать бренд по данным SMBIOS, где его страница драйверов
/// и какая фирменная утилита обновляет драйверы.
/// </summary>
public static class OemBrands
{
    private sealed record Brand(
        string Name,
        string Pattern,                 // регулярка по Manufacturer / Model / материнской плате
        string SupportUrl,
        string? Utility = null,
        bool AutoCheck = false,
        string? Remark = null,
        Func<SystemProfile, string?>? ModelUrl = null);

    // Порядок важен: HONOR раньше HUAWEI, Redmi/TIMI относятся к Xiaomi.
    private static readonly Brand[] All =
    [
        new("ASUS", @"\bASUS", "https://www.asus.com/ru/support/download-center/", "MyASUS", AutoCheck: true,
            ModelUrl: s => AsusSource.ModelCode(s.Model) is { } m ? $"https://www.asus.com/supportonly/{m.ToLowerInvariant()}/helpdesk_download/" : null),
        new("Lenovo", @"\bLENOVO\b", "https://pcsupport.lenovo.com/", "Lenovo Vantage", AutoCheck: true,
            ModelUrl: s => LenovoSource.MachineType(s.Model) is { } m ? $"https://pcsupport.lenovo.com/search?query={m}" : null),
        // Каталоги HP и Dell из некоторых сетей недоступны — тогда в окне будет ошибка источника, а ссылка останется.
        new("HP", @"\b(HP|Hewlett[- ]Packard)\b", "https://support.hp.com/drivers", "HP Support Assistant", AutoCheck: true),
        new("Dell", @"\bDell\b", "https://www.dell.com/support/home/", "Dell SupportAssist / Dell Command | Update", AutoCheck: true),
        new("MSI", @"Micro-Star|\bMSI\b", "https://www.msi.com/support/download", "MSI Center",
            ModelUrl: s => $"https://www.msi.com/search/{Uri.EscapeDataString(s.Model)}"),
        new("Acer", @"\bAcer\b", "https://www.acer.com/ru-ru/support/drivers-and-manuals", "Acer Care Center"),
        // У Honor и Huawei нет Hardware ID в каталоге — драйверы подбираются по названию пакета.
        new("Honor", @"\bHONOR\b", "https://www.honor.com/ru/support/", "HONOR PC Manager",
            AutoCheck: true, Remark: "Драйверы Honor подбираются по названию пакета — проверьте их перед установкой"),
        new("Huawei", @"\bHUAWEI\b", "https://consumer.huawei.com/ru/support/laptops/", "HUAWEI PC Manager",
            AutoCheck: true, Remark: "Драйверы Huawei подбираются по названию пакета — проверьте их перед установкой"),
        new("Xiaomi", @"\b(Xiaomi|TIMI|Redmi)\b", "https://www.mi.com/global/support/", "Xiaomi PC Manager"),
        new("Razer", @"\bRazer\b", "https://mysupport.razer.com/", "Razer Synapse"),
        new("Getac", @"\bGetac\b", "https://www.getac.com/"),
        new("Tecno", @"\bTECNO\b", "https://www.tecno-mobile.com/"),
        new("Infinix", @"\bInfinix\b", "https://www.infinixmobility.com/"),
        new("Realme", @"\brealme\b", "https://www.realme.com/global/support"),
        new("Maibenben", @"\bMaibenben\b", "https://maibenben.ru/"),
        new("Chuwi", @"\bCHUWI\b", "https://www.chuwi.com/"),
        new("Thunderobot", @"\bThunderobot\b", "https://thunderobot.ru/"),
        new("Machenike", @"\bMachenike\b", "https://global.machenike.com/"),
        new("Samsung", @"\bSAMSUNG\b", "https://www.samsung.com/ru/support/", "Samsung Update"),
        new("F+", @"(\bF\+|\bFplus\b|\bFPLUS\b)", "https://fplustech.ru/"),
        new("Echips", @"\bEchips\b", "https://echips.ru/"),
        new("Fujitsu", @"\bFUJITSU\b", "https://support.ts.fujitsu.com/", "Fujitsu DeskUpdate"),
        new("Apple", @"\bApple\b", "https://support.apple.com/ru-ru/guide/bootcamp-assistant/welcome/mac", "Boot Camp / Apple Software Update",
            Remark: "На Mac драйверы Windows ставятся пакетом Boot Camp, по одному не обновляются"),
        new("Digma", @"\bDIGMA\b", "https://digma.ru/support/"),
        new("Irbis", @"\bIRBIS\b", "https://irbis.biz/"),
        new("Osio", @"\bOSIO\b", "https://osio.ru/"),
        // Последним: платы Gigabyte стоят и в ноутбуках других брендов, их собственное имя важнее.
        // Список драйверов сайт отдаёт только браузеру (защита Akamai), поэтому автопроверки нет.
        new("Gigabyte", @"GIGA-?BYTE|\bAORUS\b", "https://www.gigabyte.com/ru/Support/Consumer/Download", "GIGABYTE Control Center",
            Remark: "Сайт Gigabyte выдаёт список драйверов только в браузере — откройте страницу модели или GIGABYTE Control Center"),
    ];

    public static OemInfo? Detect(SystemProfile sys)
    {
        // У небольших брендов имя часто есть только в модели или на плате.
        string[] fields = [sys.Manufacturer, sys.Model, sys.Family, sys.BoardManufacturer, sys.BoardProduct];
        foreach (var b in All)
        {
            if (!fields.Any(f => Regex.IsMatch(f, b.Pattern, RegexOptions.IgnoreCase))) continue;
            // ASUS пишет модель дважды: "Vivobook Go E1504FA_E1504FA" → "Vivobook Go E1504FA".
            var cleanModel = Regex.Replace(sys.Model, @"\b(\w+)_\1$", "$1");
            var model = string.IsNullOrEmpty(sys.Family) || cleanModel.Contains(sys.Family, StringComparison.OrdinalIgnoreCase)
                ? cleanModel : $"{sys.Family} ({cleanModel})";
            return new OemInfo(b.Name, model, b.ModelUrl?.Invoke(sys) ?? b.SupportUrl, b.Utility, b.AutoCheck, b.Remark);
        }
        return null;
    }
}
