using System.Diagnostics;
using System.Management;
using System.Security.Principal;

namespace DriverScout;

public static class SystemTools
{
    public static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Перезапускает приложение с правами администратора (покажет запрос UAC).</summary>
    public static bool RestartAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // пользователь отказал в UAC
        }
    }

    /// <summary>
    /// Создаёт точку восстановления. Windows не создаёт больше одной точки за 24 часа
    /// (если не изменён SystemRestorePointCreationFrequency) — тогда вызов тоже вернёт успех.
    /// </summary>
    public static (bool ok, string message) CreateRestorePoint(string description)
    {
        try
        {
            using var cls = new ManagementClass(new ManagementScope(@"\\.\root\default"),
                new ManagementPath("SystemRestore"), null);
            using var inParams = cls.GetMethodParameters("CreateRestorePoint");
            inParams["Description"] = description;
            inParams["RestorePointType"] = 10;  // DEVICE_DRIVER_INSTALL
            inParams["EventType"] = 100;        // BEGIN_SYSTEM_CHANGE
            using var outParams = cls.InvokeMethod("CreateRestorePoint", inParams, null);
            var code = Convert.ToUInt32(outParams["ReturnValue"]);
            return code == 0
                ? (true, "Точка восстановления создана")
                : (false, $"Не удалось создать точку восстановления (код {code}). Возможно, защита системы отключена.");
        }
        catch (Exception ex)
        {
            return (false, "Не удалось создать точку восстановления: " + ex.Message);
        }
    }
}
