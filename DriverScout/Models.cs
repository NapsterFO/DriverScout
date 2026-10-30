using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DriverScout;

/// <summary>Устройство из диспетчера устройств вместе с установленным драйвером.</summary>
public sealed class DeviceInfo
{
    public required string Name { get; init; }
    public required string DeviceId { get; init; }
    public string? DeviceClass { get; init; }
    public string[] HardwareIds { get; init; } = [];
    /// <summary>Более общие ID ("PCI\VEN_1022&amp;DEV_15E2&amp;REV_00") — Windows тоже подбирает по ним драйверы.</summary>
    public string[] CompatibleIds { get; init; } = [];

    /// <summary>ConfigManagerErrorCode: 0 — всё хорошо, 28 — драйвер не установлен.</summary>
    public int ErrorCode { get; init; }

    public string? DriverVersion { get; set; }
    public DateTime? DriverDate { get; set; }
    public string? DriverProvider { get; set; }
    public string? InfName { get; set; }

    public bool IsMissingDriver => ErrorCode == 28;
    public bool HasProblem => ErrorCode != 0 && ErrorCode != 28;
}

public enum DriverKind
{
    WindowsUpdate, // ставится через Windows Update Agent
    Installer,     // .exe-установщик с сайта производителя
    DriverPack     // INF из драйверпака SDIO
}

/// <summary>Драйвер, найденный в одном из источников.</summary>
public sealed class DriverUpdate
{
    public required string Title { get; init; }
    public required string Source { get; init; }
    public DriverKind Kind { get; init; } = DriverKind.WindowsUpdate;

    // Только для Kind == Installer.
    public string? DownloadUrl { get; init; }
    /// <summary>Имя файла, если в ссылке его нет (у Honor и Huawei вместо имени длинный токен).</summary>
    public string? FileName { get; init; }
    public string? Referer { get; init; }
    /// <summary>
    /// Архив без контрольной суммы, но скачан по HTTPS с сервера самого производителя (Honor, Huawei).
    /// Установщик внутри всё равно проверяется по цифровой подписи, драйверы INF — самой Windows.
    /// </summary>
    public bool TrustArchiveFromVendor { get; init; }

    // Только для Kind == DriverPack.
    /// <summary>Драйверпак SDIO без расширения: "DP_Chipset_26082".</summary>
    public string? PackName { get; init; }
    /// <summary>Путь к INF внутри драйверпака: "AMD\FORCED\Audio\10x64\ACP_6.0.2.118\amdacpbus.inf".</summary>
    public string? PackInf { get; init; }
    /// <summary>Драйверпак встроен в DriverScout (сетевые драйверы) — ставится без интернета.</summary>
    public bool Bundled { get; init; }
    /// <summary>Отметить к установке, несмотря на примечание: встроенный драйвер для сетевой карты без драйвера.</summary>
    public bool AutoSelect { get; init; }
    /// <summary>Подпись .exe должна принадлежать этой компании, иначе не запускаем.</summary>
    public string? ExpectedSigner { get; init; }
    /// <summary>Контрольная сумма от производителя, если он её публикует (ASUS).</summary>
    public string? Sha256 { get; init; }
    public string? PageUrl { get; init; }
    /// <summary>Предупреждение для пользователя (например, «для ноутбуков лучше драйвер от производителя»).</summary>
    public string? Note { get; init; }

    public string? HardwareId { get; init; }
    public string? Version { get; init; }
    public DateTime? Date { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public long SizeBytes { get; init; }

    /// <summary>Исходный COM-объект IUpdate, нужен для установки.</summary>
    public object? Handle { get; init; }
}

public enum RowStatus
{
    Missing,   // драйвера нет вообще
    Update,    // есть более новый драйвер
    Problem,   // устройство с ошибкой, драйвер не найден
    UpToDate
}

/// <summary>Строка таблицы в окне: устройство + найденное обновление.</summary>
public sealed class DriverRow : INotifyPropertyChanged
{
    public required RowStatus Status { get; init; }
    public required string Name { get; init; }
    public string? DeviceClass { get; init; }
    public string? HardwareId { get; init; }
    public string? CurrentVersion { get; init; }
    public DateTime? CurrentDate { get; init; }
    public DriverUpdate? Update { get; init; }
    /// <summary>Страница драйверов на сайте производителя (для ручной проверки).</summary>
    public string? VendorPage { get; init; }

    /// <summary>Найденный драйвер старше установленного — подозрительно, по умолчанию не отмечаем.</summary>
    public bool IsOlderThanInstalled =>
        Update?.Date is { } d && CurrentDate is { } c && d.Date < c.Date;

    public string StatusText => Status switch
    {
        RowStatus.Missing => "Нет драйвера",
        RowStatus.Update => IsOlderThanInstalled ? "Обновление (старее!)" : "Есть обновление",
        RowStatus.Problem => "Ошибка устройства",
        _ => "Актуально"
    };

    /// <summary>Короткая надпись для метки на карточке.</summary>
    public string ChipText => Status switch
    {
        RowStatus.Missing => "нет драйвера",
        RowStatus.Update => IsOlderThanInstalled ? "старее установленного" : "обновление",
        RowStatus.Problem => "ошибка устройства",
        _ => "актуально"
    };

    /// <summary>Карточка требует внимания — выделяется оранжевой обводкой.</summary>
    public bool IsAttention => Status is RowStatus.Missing or RowStatus.Update;

    public string Current => Format(CurrentVersion, CurrentDate);
    public string Available => Update is null ? "" : Format(Update.Version, Update.Date);
    public string Source => Update?.Source ?? "";
    public bool CanInstall => Update is not null;

    private string _result = "";
    public string Result
    {
        get => _result;
        set { _result = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasResult)); }
    }
    public bool HasResult => !string.IsNullOrEmpty(_result);

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value && CanInstall; OnPropertyChanged(); }
    }

    private static string Format(string? version, DateTime? date) =>
        (version, date) switch
        {
            (null, null) => "—",
            (_, null) => version!,
            (null, _) => date!.Value.ToString("dd.MM.yyyy"),
            _ => $"{version} ({date!.Value:dd.MM.yyyy})"
        };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
