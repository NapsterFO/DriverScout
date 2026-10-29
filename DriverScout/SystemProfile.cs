using System.Management;

namespace DriverScout;

/// <summary>Сведения о компьютере, от которых зависит выбор драйвера.</summary>
public sealed class SystemProfile
{
    public bool IsLaptop { get; init; }
    public int OsBuild { get; init; }
    public bool IsWindows11 => OsBuild >= 22000;
    public string CpuName { get; init; } = "";
    /// <summary>Производитель и модель компьютера: "ASUSTeK COMPUTER INC.", "Vivobook Go E1504FA_E1504FA".</summary>
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    /// <summary>Понятное название линейки, если производитель его заполняет ("IdeaPad 3 15ITL6").</summary>
    public string Family { get; init; } = "";
    /// <summary>Материнская плата — у небольших брендов в Manufacturer часто пусто или "To be filled by O.E.M.".</summary>
    public string BoardManufacturer { get; init; } = "";
    public string BoardProduct { get; init; } = "";
    /// <summary>SKU системы. У Dell это System ID модели ("0A21"), по нему ищется каталог.</summary>
    public string SystemSku { get; init; } = "";
    /// <summary>Выпуск Windows: "22H2", "24H2". У HP справочник драйверов свой для каждого выпуска.</summary>
    public string OsRelease { get; init; } = "";

    public static SystemProfile Load()
    {
        // Типы корпуса SMBIOS: 8–10, 14, 30–32 — портативные.
        int[] portable = [8, 9, 10, 14, 30, 31, 32];
        bool laptop = false;
        using (var s = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
            foreach (ManagementObject mo in s.Get())
                using (mo)
                    if (mo["ChassisTypes"] is ushort[] types && types.Any(t => portable.Contains(t)))
                        laptop = true;

        string cpu = "";
        using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
            foreach (ManagementObject mo in s.Get())
                using (mo)
                    cpu = (mo["Name"] as string ?? "").Trim();

        string manufacturer = "", model = "", family = "", sku = "";
        using (var s = new ManagementObjectSearcher("SELECT Manufacturer, Model, SystemFamily, SystemSKUNumber FROM Win32_ComputerSystem"))
            foreach (ManagementObject mo in s.Get())
                using (mo)
                {
                    manufacturer = (mo["Manufacturer"] as string ?? "").Trim();
                    model = (mo["Model"] as string ?? "").Trim();
                    family = (mo["SystemFamily"] as string ?? "").Trim();
                    sku = (mo["SystemSKUNumber"] as string ?? "").Trim();
                }

        var release = Microsoft.Win32.Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null) as string ?? "";

        string boardMaker = "", boardProduct = "";
        using (var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
            foreach (ManagementObject mo in s.Get())
                using (mo)
                {
                    boardMaker = (mo["Manufacturer"] as string ?? "").Trim();
                    boardProduct = (mo["Product"] as string ?? "").Trim();
                }

        return new SystemProfile
        {
            IsLaptop = laptop, OsBuild = Environment.OSVersion.Version.Build, CpuName = cpu,
            Manufacturer = manufacturer, Model = model, Family = family,
            BoardManufacturer = boardMaker, BoardProduct = boardProduct,
            SystemSku = sku, OsRelease = release.Trim()
        };
    }
}
