using System.Text.RegularExpressions;

namespace DriverScout;

/// <summary>
/// Поиск и установка драйверов через Windows Update Agent API (COM "Microsoft.Update.Session").
/// Драйверы отсюда подписаны Microsoft и подобраны по Hardware ID самим Windows Update.
/// </summary>
public sealed class WindowsUpdateSource
{
    public const string SourceName = "Windows Update";

    private readonly dynamic _session;

    public WindowsUpdateSource()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session")
                   ?? throw new InvalidOperationException("Windows Update Agent недоступен.");
        _session = Activator.CreateInstance(type)!;
        _session.ClientApplicationID = "DriverScout";
    }

    /// <summary>Ищет все неустановленные драйверы, которые Windows Update предлагает для этого ПК.</summary>
    public List<DriverUpdate> Search()
    {
        dynamic searcher = _session.CreateUpdateSearcher();
        searcher.Online = true;
        dynamic result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");

        var list = new List<DriverUpdate>();
        dynamic updates = result.Updates;
        int count = updates.Count;
        for (int i = 0; i < count; i++)
        {
            dynamic u = updates.Item(i);
            string title = u.Title;
            list.Add(new DriverUpdate
            {
                Title = title,
                Source = SourceName,
                HardwareId = TryGet(() => (string)u.DriverHardwareID),
                Date = TryGet(() => (DateTime?)(DateTime)u.DriverVerDate),
                Manufacturer = TryGet(() => (string)u.DriverManufacturer),
                Model = TryGet(() => (string)u.DriverModel),
                Version = ParseVersion(title),
                SizeBytes = TryGet(() => Convert.ToInt64(u.MaxDownloadSize)),
                Handle = u
            });
        }
        return list;
    }

    /// <summary>Скачивает и ставит один драйвер. Возвращает текст результата.</summary>
    public (bool ok, bool rebootRequired, string message) Install(DriverUpdate update)
    {
        dynamic u = update.Handle ?? throw new ArgumentException("Нет COM-объекта обновления.");
        if (!(bool)u.EulaAccepted) u.AcceptEula();

        dynamic coll = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
        coll.Add(u);

        dynamic downloader = _session.CreateUpdateDownloader();
        downloader.Updates = coll;
        dynamic dl = downloader.Download();
        int dlCode = dl.ResultCode;
        if (dlCode is not (2 or 3))
            return (false, false, $"Ошибка загрузки ({ResultText(dlCode)})");

        dynamic installer = _session.CreateUpdateInstaller();
        installer.Updates = coll;
        dynamic res = installer.Install();
        int code = res.GetUpdateResult(0).ResultCode;
        bool reboot = res.RebootRequired;
        bool ok = code is 2 or 3;
        return (ok, reboot, ok ? (reboot ? "Установлено, нужна перезагрузка" : "Установлено")
                               : $"Ошибка установки ({ResultText(code)})");
    }

    private static string ResultText(int code) => code switch
    {
        0 => "не начато", 1 => "в процессе", 2 => "успешно",
        3 => "с ошибками", 4 => "сбой", 5 => "прервано", _ => code.ToString()
    };

    // Заголовок вида "Intel - System - 10.1.1.44" или "... Update (24.80.0.2)" — версия в конце.
    private static string? ParseVersion(string title)
    {
        var m = Regex.Match(title, @"(\d+(\.\d+){1,3})\)?\s*$");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static T? TryGet<T>(Func<T> get)
    {
        try { return get(); } catch { return default; }
    }
}
