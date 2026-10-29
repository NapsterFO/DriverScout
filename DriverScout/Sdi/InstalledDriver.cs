// DriverScout — модуль драйверпаков Snappy Driver Installer Origin.
// Переработка кода SDIO (enum.cpp: Driver, scaninf, © Glenn Delahoy и авторы SDI/SDIO), распространяется по GNU GPL v3.

using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DriverScout.Sdi;

/// <summary>
/// Установленный драйвер устройства (класс Driver в SDIO): данные из ключа класса в реестре
/// и ранг, посчитанный так же, как для драйверов из драйверпаков, — чтобы их можно было сравнить.
/// </summary>
public sealed class InstalledDriver
{
    public required string InfPath { get; init; }
    public required string InfSection { get; init; }
    public string InfSectionExt { get; init; } = "";
    public required string MatchingDeviceId { get; init; }
    public SdiVersion Version { get; init; }
    public uint Score { get; init; }

    /// <summary>Читает драйвер устройства из реестра; null — драйвер не установлен.</summary>
    public static InstalledDriver? Read(DeviceInfo device)
    {
        using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + device.DeviceId);
        if (enumKey?.GetValue("Driver") is not string driverKey) return null;
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driverKey);
        if (cls is null) return null;

        string Get(string name) => cls.GetValue(name) as string ?? "";
        var infPath = Get("InfPath");
        var matching = Get("MatchingDeviceId");
        if (infPath.Length == 0 || matching.Length == 0) return null;

        // calc_dev_pos: где среди ID устройства стоит тот, по которому Windows выбрала драйвер.
        bool ishw = true;
        int devPos = Array.FindIndex(device.HardwareIds, h => h.Equals(matching, StringComparison.OrdinalIgnoreCase));
        if (devPos < 0)
        {
            ishw = false;
            devPos = Array.FindIndex(device.CompatibleIds, h => h.Equals(matching, StringComparison.OrdinalIgnoreCase));
        }

        var section = Get("InfSection");
        var sectionExt = Get("InfSectionExt");
        var inf = ScanInf(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", infPath),
                          matching, section + sectionExt, section);

        bool isNt = (section + sectionExt).Contains(".nt", StringComparison.OrdinalIgnoreCase);
        var version = ParseVersion(Get("DriverDate"), Get("DriverVersion"));
        return new InstalledDriver
        {
            InfPath = infPath, InfSection = section, InfSectionExt = sectionExt, MatchingDeviceId = matching,
            Version = version,
            Score = SdiMatcher.CalcScore(inf.catalogFile, inf.feature, SdiMatcher.IdentifierScore(devPos, ishw, inf.infPos), isNt)
        };
    }

    /// <summary>"10-25-2025" и "32.0.21030.2001" → SdiVersion (Parser::readDate / readVersion).</summary>
    private static SdiVersion ParseVersion(string date, string version)
    {
        var d = Regex.Match(date, @"(\d+)[-/.](\d+)[-/.](\d+)");
        var v = version.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).Concat(Enumerable.Repeat(0, 4)).ToArray();
        return new SdiVersion(
            d.Success ? int.Parse(d.Groups[2].Value) : 0, d.Success ? int.Parse(d.Groups[1].Value) : 0,
            d.Success ? int.Parse(d.Groups[3].Value) : 0,
            version.Length > 0 ? v[0] : -2, v[1], v[2], v[3]);
    }

    /// <summary>
    /// Упрощённый scaninf/fillinfo: из установленного INF нужны наличие каталога подписи, позиция ID в строке
    /// модели (inf_pos) и FeatureScore секции установки. Если INF не прочитать — как в режиме failsafe SDIO.
    /// </summary>
    private static (int catalogFile, int feature, int infPos) ScanInf(string file, string matchingId, string ddInstall, string section)
    {
        int catalogFile = 0, feature = 0xFF, infPos = 0;
        string text;
        try { text = ReadInf(file); }
        catch (Exception) { return (catalogFile, feature, infPos); }

        string current = "";
        bool foundModel = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { current = line[1..^1].Trim(); continue; }

            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (current.Equals("Version", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<InfField>(key.Replace('.', '_'), true, out var f)
                && f is >= InfField.CatalogFile and <= InfField.CatalogFile_ntamd64)
                catalogFile |= 1 << (int)f;

            if ((current.Equals(ddInstall, StringComparison.OrdinalIgnoreCase) || current.Equals(section, StringComparison.OrdinalIgnoreCase))
                && key.Equals("FeatureScore", StringComparison.OrdinalIgnoreCase))
                feature = Convert.ToInt32(value.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16) & 0xFF;

            // Строка модели: "описание = секция_установки, hwid, совместимый_id, …".
            if (!foundModel)
            {
                var parts = value.Split(',').Select(p => p.Trim().Trim('"')).ToArray();
                if (parts.Length > 1 && parts[0].Equals(section, StringComparison.OrdinalIgnoreCase))
                {
                    int pos = Array.FindIndex(parts, 1, p => p.Equals(matchingId, StringComparison.OrdinalIgnoreCase));
                    if (pos > 0) { infPos = pos - 1; foundModel = true; }
                }
            }
        }
        return (catalogFile, feature, infPos);
    }

    private static string StripComment(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ';' && !quoted) return line[..i];
        }
        return line;
    }

    /// <summary>INF бывает в UTF-16 (с BOM) или в однобайтовой кодировке.</summary>
    public static string ReadInf(string file)
    {
        var bytes = File.ReadAllBytes(file);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.Latin1.GetString(bytes);
    }
}
