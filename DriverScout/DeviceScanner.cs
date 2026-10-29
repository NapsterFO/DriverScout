using System.Management;

namespace DriverScout;

/// <summary>Читает список устройств и установленных драйверов через WMI.</summary>
public static class DeviceScanner
{
    public static List<DeviceInfo> Scan()
    {
        var devices = new Dictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);

        using (var searcher = new ManagementObjectSearcher(
            "SELECT Name, DeviceID, PNPClass, HardwareID, CompatibleID, ConfigManagerErrorCode FROM Win32_PnPEntity"))
        {
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var id = mo["DeviceID"] as string;
                    if (string.IsNullOrEmpty(id)) continue;
                    devices[id] = new DeviceInfo
                    {
                        DeviceId = id,
                        Name = mo["Name"] as string ?? id,
                        DeviceClass = mo["PNPClass"] as string,
                        HardwareIds = mo["HardwareID"] as string[] ?? [],
                        CompatibleIds = mo["CompatibleID"] as string[] ?? [],
                        ErrorCode = Convert.ToInt32(mo["ConfigManagerErrorCode"] ?? 0)
                    };
                }
            }
        }

        using (var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, DriverVersion, DriverDate, DriverProviderName, InfName FROM Win32_PnPSignedDriver"))
        {
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    if (mo["DeviceID"] is not string id || !devices.TryGetValue(id, out var dev)) continue;
                    dev.DriverVersion = mo["DriverVersion"] as string;
                    dev.DriverProvider = mo["DriverProviderName"] as string;
                    dev.InfName = mo["InfName"] as string;
                    if (mo["DriverDate"] is string cimDate && cimDate.Length >= 14)
                    {
                        try { dev.DriverDate = ManagementDateTimeConverter.ToDateTime(cimDate); }
                        catch (ArgumentOutOfRangeException) { }
                    }
                }
            }
        }

        return devices.Values.ToList();
    }
}
