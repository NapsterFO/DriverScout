// DriverScout — модуль драйверпаков Snappy Driver Installer Origin.
// Переработка кода SDIO (matcher.cpp, enum.cpp, © Glenn Delahoy и авторы SDI/SDIO), распространяется по GNU GPL v3.

namespace DriverScout.Sdi;

/// <summary>Статусы драйвера относительно установленного (DRIVER_STATUS в SDIO).</summary>
[Flags]
public enum SdiStatus
{
    None = 0,
    Better = 0x001, Same = 0x002, Worse = 0x004, Invalid = 0x008,
    Missing = 0x010, New = 0x020, Current = 0x040, Old = 0x080,
    Dup = 0x800
}

/// <summary>Кандидат из драйверпака для устройства (Hwidmatch в SDIO).</summary>
public sealed class SdiCandidate
{
    public required SdiIndex Pack { get; init; }
    public required int HwidIndex { get; init; }
    public int IdentifierScore { get; init; }
    public int DecorScore { get; init; }
    public int MarkerScore { get; init; }
    public int AltSectScore { get; set; }
    public uint Score { get; init; }
    public SdiStatus Status { get; set; }

    public SdiIndex.InfFile Inf => Pack.InfOf(HwidIndex);
    public SdiVersion Version => Inf.Version;
    public string HardwareId => Pack.Hwids[HwidIndex].Id;
    public string Description => Pack.Str(Pack.DescOf(HwidIndex).DescOffset);
    public string Section => Pack.Section(HwidIndex);
    public string Provider => Pack.Field(HwidIndex, InfField.Provider);

    /// <summary>isdrivervalid: секция подходит к этой Windows.</summary>
    public bool IsValid => AltSectScore > 0 && DecorScore > 0;

    /// <summary>Hwidmatch::cmp — больше значит лучше.</summary>
    public int CompareTo(SdiCandidate b)
    {
        int res = AltSectScore - b.AltSectScore;
        if (res != 0) return res;
        res = Score.CompareTo(b.Score);
        if (res != 0) return -res; // меньший ранг лучше
        res = SdiVersion.CompareDate(Version, b.Version);
        if (res != 0) return res;
        res = DecorScore - b.DecorScore;
        if (res != 0) return res;
        res = MarkerScore - b.MarkerScore;
        if (res != 0) return res;
        return (int)(Status & ~SdiStatus.Dup) - (int)(b.Status & ~SdiStatus.Dup);
    }
}

/// <summary>
/// Подбор драйверов из драйверпаков по алгоритму SDIO: для каждого Hardware/Compatible ID устройства
/// ищутся записи в индексах, каждой считается ранг (как у самой Windows), пригодность секции INF
/// для текущей ОС и архитектуры, затем кандидаты сортируются и сравниваются с установленным драйвером.
/// </summary>
public sealed class SdiMatcher
{
    // Состояние системы (State в SDIO). DriverScout работает только на Windows 10/11 x64.
    private const int Arch = 1;            // 0 — x86, 1 — amd64
    private const int Major = 10, Minor = 0;
    private readonly int _build;
    private readonly bool _isLaptop;
    private readonly string _marker;

    public SdiMatcher(SystemProfile sys)
    {
        _build = sys.OsBuild;
        _isLaptop = sys.IsLaptop;
        _marker = GenMarker(sys.Manufacturer);
    }

    /// <summary>Кандидаты для устройства, лучший первым. Пустой список — в драйверпаках ничего нет.</summary>
    public List<SdiCandidate> Match(DeviceInfo device, InstalledDriver? installed, IEnumerable<SdiIndex> packs)
    {
        var list = new List<SdiCandidate>();
        foreach (var pack in packs)
        {
            Find(device.HardwareIds, ishw: true);
            Find(device.CompatibleIds, ishw: false);

            void Find(string[] ids, bool ishw)
            {
                for (int pos = 0; pos < ids.Length; pos++)
                    if (pack.ByHwid.TryGetValue(ids[pos].ToUpperInvariant(), out var hits))
                        foreach (var h in hits) list.Add(Create(pack, h, pos, ishw, device, installed));
            }
        }

        // sort(): лучший первым, затем помечаем дубликаты (тот же INF, ID и секция).
        list.Sort((a, b) => b.CompareTo(a));
        for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
                if (list[i].Inf.Crc == list[j].Inf.Crc && list[i].HardwareId == list[j].HardwareId && list[i].Section == list[j].Section)
                    list[j].Status |= SdiStatus.Dup;
        return list;
    }

    /// <summary>
    /// Что предложить к установке (фильтр списка в manager.cpp при настройках «Отсутствующие», «Лучше», «Новее»):
    /// лучший пригодный кандидат, если драйвера нет, ранг лучше установленного или он новее и не хуже по рангу.
    /// </summary>
    public static SdiCandidate? Pick(List<SdiCandidate> candidates, DeviceInfo device, InstalledDriver? installed)
    {
        foreach (var c in candidates)
        {
            if (!c.IsValid || c.Status.HasFlag(SdiStatus.Dup)) continue;
            // Для исправного устройства с драйвером — только с действительной подписью каталога под эту ОС.
            if (device.ErrorCode == 0 && installed is not null && c.AltSectScore < 2) continue;
            if (c.Status.HasFlag(SdiStatus.Missing) || c.Status.HasFlag(SdiStatus.Better)) return c;
            if (c.Status.HasFlag(SdiStatus.New) && !c.Status.HasFlag(SdiStatus.Worse)) return c;
            return null; // лучший пригодный кандидат не лучше установленного — остальные тем более
        }
        return null;
    }

    private SdiCandidate Create(SdiIndex pack, int h, int devPos, bool ishw, DeviceInfo device, InstalledDriver? installed)
    {
        var section = pack.Section(h);
        int decor = DecorScore(SectType(section));
        var c = new SdiCandidate
        {
            Pack = pack,
            HwidIndex = h,
            IdentifierScore = IdentifierScore(devPos, ishw, pack.Hwids[h].InfPos),
            DecorScore = decor,
            MarkerScore = MarkerScore(pack.InfOf(h).Path),
            Score = CalcScore(CatalogFileMask(pack, h), Feature(pack, h),
                IdentifierScore(devPos, ishw, pack.Hwids[h].InfPos),
                pack.Str(pack.DescOf(h).InstallPickedOffset).Contains(".nt", StringComparison.OrdinalIgnoreCase))
        };
        c.AltSectScore = AltSectScore(c, device);
        c.Status = CalcStatus(c, device, installed);
        return c;
    }

    // --- Ранг (enum.cpp) ---

    /// <summary>calc_identifierscore: насколько точно ID устройства совпал с ID в INF (как ранжирует Windows).</summary>
    public static int IdentifierScore(int devPos, bool devIsHw, int infPos)
    {
        if (devIsHw && infPos == 0) return 0x0000 + devPos;            // Hardware ID устройства = Hardware ID в INF
        if (devIsHw) return 0x1000 + devPos + 0x100 * infPos;          // Hardware ID устройства = Compatible ID в INF
        if (infPos == 0) return 0x2000 + devPos;                       // Compatible ID устройства = Hardware ID в INF
        return 0x3000 + devPos + 0x100 * infPos;                       // Compatible ID = Compatible ID
    }

    /// <summary>calc_score: подпись (наличие каталога), FeatureScore и ранг ID. Меньше — лучше.</summary>
    public static uint CalcScore(int catalogFile, int feature, int rank, bool isNt) =>
        (uint)((Signature(catalogFile, isNt) << 16) + (feature << 16) + rank);

    private static int Signature(int catalogFile, bool isNt)
    {
        int signed = 1 << (int)InfField.CatalogFile | 1 << (int)InfField.CatalogFile_nt
                     | 1 << (int)InfField.CatalogFile_ntamd64 | 1 << (int)InfField.CatalogFile_ntia64;
        if ((catalogFile & signed) != 0) return 0;
        return isNt ? 0x8000 : 0xC000;
    }

    private static int CatalogFileMask(SdiIndex pack, int h)
    {
        int r = 0;
        for (var f = InfField.CatalogFile; f <= InfField.CatalogFile_ntamd64; f++)
            if (pack.Field(h, f).Length > 0) r += 1 << (int)f;
        return r;
    }

    /// <summary>getdrp_drvfeature: «feature_NN» в пути или FeatureScore из INF.</summary>
    private static int Feature(SdiIndex pack, int h)
    {
        var path = pack.InfOf(h).Path;
        int i = path.IndexOf("feature_", StringComparison.OrdinalIgnoreCase);
        if (i >= 0) return int.TryParse(new string(path[(i + 8)..].TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0;
        return pack.DescOf(h).Feature & 0xFF;
    }

    // --- Статус относительно установленного драйвера ---

    private SdiStatus CalcStatus(SdiCandidate c, DeviceInfo device, InstalledDriver? installed)
    {
        if (IsMissing(device, installed)) return SdiStatus.Missing;
        var r = SdiStatus.None;
        if (installed is not null)
        {
            int res = SdiVersion.CompareDate(installed.Version, c.Version);
            r |= res < 0 ? SdiStatus.New : res > 0 ? SdiStatus.Old : SdiStatus.Current;

            uint mine = installed.Score;
            int cmp = mine.CompareTo(c.Score);
            if (r.HasFlag(SdiStatus.Current) && c.Inf.Path.Contains("feature_", StringComparison.OrdinalIgnoreCase))
                cmp = (mine & 0xFF00FFFF).CompareTo(c.Score & 0xFF00FFFF);
            r |= cmp > 0 ? SdiStatus.Better : cmp < 0 ? SdiStatus.Worse : SdiStatus.Same;
        }
        else r |= SdiStatus.Better;
        if (c.AltSectScore == 0) r |= SdiStatus.Invalid;
        return r;
    }

    /// <summary>Devicematch::isMissing.</summary>
    public static bool IsMissing(DeviceInfo device, InstalledDriver? installed)
    {
        const int disabled = 22; // CM_PROB_DISABLED
        if (device.ErrorCode == disabled) return false;
        if (device.ErrorCode != 0 && device.HardwareIds.Length > 0) return true;
        var first = device.HardwareIds.FirstOrDefault() ?? "";
        if (installed is null && (first.Contains("USBPRINT", StringComparison.OrdinalIgnoreCase)
                                  || first.Contains("DOT4PRT", StringComparison.OrdinalIgnoreCase)
                                  || first.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase))) return true;
        // Стоит «Базовый видеоадаптер» по классу PCI\CC_0300 — драйвера по сути нет.
        return installed is not null && installed.MatchingDeviceId.Equals(@"PCI\CC_0300", StringComparison.OrdinalIgnoreCase);
    }

    // --- Пригодность секции INF для этой ОС (calc_secttype, calc_decorscore, calc_altsectscore) ---

    private sealed record Decoration(string Name, int Version, int Build, int Arch, int Score);
    private static readonly Decoration[] Nts = BuildNts();

    /// <summary>Таблицы nts/nts_version/nts_build/nts_arch/nts_score из matcher.cpp.</summary>
    private static Decoration[] BuildNts()
    {
        string[] archs = ["nt", "ntx86", "ntamd64", "ntia64", "ntarm", "ntarm64"];
        var rows = new List<(string suffix, int ver, int build, int score)>
        {
            (".5", 50, 0, 50), (".5.0", 50, 0, 50), (".5.1", 51, 0, 51), (".5.2", 52, 0, 52),
            (".6", 60, 0, 60), (".6.0", 60, 0, 60), (".6.1", 61, 0, 61), (".6.2", 62, 0, 62), (".6.3", 63, 0, 63),
            (".6.4", 64, 0, 64), (".10", 100, 0, 64), (".10.0", 100, 0, 64),
            (".10.0...10240", 100, 0, 64), (".10.0...10586", 100, 0, 64),
        };
        foreach (var b in new[] { 14310, 14393, 15063, 16209, 16225, 16273, 16288, 16299 }) rows.Add(($".10.0...{b}", 100, b, 64));
        rows.Add((".10....16299", 100, 16299, 64));
        foreach (var b in new[] { 17134, 17704, 17735, 17763, 18362, 18363, 18900, 19041, 19042, 19043, 19044, 19045,
                                  19099, 19586, 20190, 20348, 21250, 21262 }) rows.Add(($".10.0...{b}", 100, b, 64));
        foreach (var b in new[] { 22000, 22621, 22631, 22635, 25216, 25336, 25398, 25952, 26052, 26063, 26080, 26100, 26200 })
            rows.Add(($".10.0...{b}", 100, b, 65));
        rows.Add(("", 0, 0, 10));
        rows.Add(("..", 0, 0, 10));

        var list = new List<Decoration>();
        foreach (var (suffix, ver, build, score) in rows)
            for (int a = 0; a < archs.Length; a++)
                list.Add(new Decoration(archs[a] + suffix, ver, build, a,
                    a == 0 ? score : ver == 0 ? 100 : score + 100));
        return [.. list];
    }

    /// <summary>calc_secttype: номер декорации секции ("Realtek.NTamd64.10.0...19041") в таблице или -1.</summary>
    public static int SectType(string section)
    {
        int at = section.IndexOf(".nt", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return -1;
        // NT[Architecture][.[OSMajor][.[OSMinor][.[ProductType][.[SuiteMask][.[BuildNumber]]]]]] —
        // ProductType и SuiteMask не учитываем, BuildNumber учитываем.
        var parts = section[at..].Split('.');
        string P(int i) => i < parts.Length ? parts[i] : "";
        var key = "." + P(1);
        if (P(2).Length > 0) key += "." + P(2);
        if (P(3).Length > 0) key += "." + P(3);
        if (P(6).Length > 0) key += "..." + P(6);
        var tail = key.Length > 3 ? key[3..] : "";
        for (int i = 0; i < Nts.Length; i++)
            if (string.Equals(tail, Nts[i].Name[2..], StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>calc_decorscore: 0 — секция не для этой Windows/архитектуры.</summary>
    public int DecorScore(int id)
    {
        if (id < 0) return 1;
        var d = Nts[id];
        int arch = Arch + 1; // в таблице: 1 — x86, 2 — amd64
        if (d.Version != 0 && Major * 10 + Minor < d.Version) return 0;
        if (d.Arch != 0 && arch != d.Arch) return 0;
        if (d.Version >= 100 && d.Build >= 14310 && Major * 10 + Minor == d.Version && d.Build > _build) return 0;
        return d.Score;
    }

    private sealed record Marker(string Name, int Major, int Minor, int Arch);
    private static readonly Marker[] Markers =
    [
        new("5x86", 5, 0, 0), new("5x86", 5, 1, 0), new("5x86", 5, 2, 0), new("6x86", 6, 0, 0), new("7x86", 6, 1, 0),
        new("8x86", 6, 2, 0), new("81x86", 6, 3, 0), new("10x86", 6, 4, 0), new("10x86", 10, 0, 0), new("U10x86", 10, 0, 0),
        new("11x86", 11, 0, 0), new("67x86", 6, 0, 0), new("6xx86", 6, 0, 0), new("78x86", 6, 1, 0), new("781x86", 6, 1, 0),
        new("710x86", 6, 1, 0), new("88110x86", 6, 3, 0), new("8110x86", 6, 3, 0),
        new("5x64", 5, 1, 1), new("5x64", 5, 2, 1), new("6x64", 6, 0, 1), new("7x64", 6, 1, 1), new("8x64", 6, 2, 1),
        new("81x64", 6, 3, 1), new("10x64", 6, 4, 1), new("10x64", 10, 0, 1), new("U10x64", 10, 0, 1), new("11x64", 11, 0, 1),
        new("67x64", 6, 0, 1), new("6xx64", 6, 0, 1), new("78x64", 6, 1, 1), new("781x64", 6, 1, 1), new("710x64", 6, 1, 1),
        new("88110x64", 6, 3, 1), new("8110x64", 6, 3, 1),
        new("5ia64", 5, 1, 2), new("5ia64", 5, 2, 2), new("6ia64", 6, 0, 2), new("7ia64", 6, 1, 2), new("8ia64", 6, 2, 2),
        new("10ia64", 10, 0, 2), new("11ia64", 11, 0, 2),
        new("5arm", 5, 1, 3), new("5arm", 5, 2, 3), new("6arm", 6, 0, 3), new("7arm", 6, 1, 3), new("8arm", 6, 2, 3),
        new("10arm", 10, 0, 3), new("11arm", 11, 0, 3),
        new("5arm64", 5, 1, 4), new("5arm64", 5, 2, 4), new("6arm64", 6, 0, 4), new("7arm64", 6, 1, 4), new("8arm64", 6, 2, 4),
        new("10arm64", 10, 0, 4), new("11arm64", 11, 0, 4),
        new("allnt", 4, 0, -1), new("allxp", 5, 1, -1), new("allxp", 5, 2, -1), new("all6", 6, 0, -1), new("all7", 6, 1, -1),
        new(@"all8\", 6, 2, -1), new("all81", 6, 3, -1), new("all10", 6, 4, -1), new("all10", 10, 0, -1), new("all11", 11, 0, -1),
        new("allntx64x86", 4, 0, -1), new("all5x86x64", 5, 1, -1), new("all5x86x64", 5, 2, -1), new("all6x86x64", 6, 0, -1),
        new("all7x86x64", 6, 1, -1), new("all8x86x64", 6, 2, -1), new("all10x86x64", 10, 0, -1), new("all11x86x64", 11, 0, -1),
        new("allx86", -1, -1, 0), new("allx64", -1, -1, 1), new("all8x86", 6, 2, 0), new("all8x64", 6, 2, 1),
        new("allia64", -1, -1, 2), new("allarm", -1, -1, 3), new("allarm64", -1, -1, 4), new("ntx86", -1, -1, 0),
        new("ntx64", -1, -1, 1), new(@"x86\", -1, -1, 0), new(@"x64\", -1, -1, 1), new("winall", -1, -1, -1),
    ];

    /// <summary>calc_markerscore: метки ОС и архитектуры в пути внутри драйверпака ("…\10x64\…").</summary>
    public int MarkerScore(string path)
    {
        int curMaj = -1, curMin = -1, curArch = -1, score = 0;
        foreach (var m in Markers)
        {
            if (!path.Contains(m.Name, StringComparison.OrdinalIgnoreCase)) continue;
            score = 1;
            if (m.Major == 0) continue;
            curMaj = Math.Max(curMaj, m.Major);
            curMin = Math.Max(curMin, m.Minor);
            curArch = Math.Max(curArch, m.Arch);
        }
        // +1 метка есть, +2 версия ОС подходит, +4 архитектура подходит, +8 версия совпадает, +16 архитектура совпадает
        if (curMaj >= 0 && curMin >= 0 && Major == curMaj && Minor == curMin) score |= 8;
        if (curMaj >= 0 && curMin >= 0 && Major >= curMaj && Minor >= curMin) score |= 2;
        if (curMaj < 0 && score != 0) score |= 2;
        if (curArch >= 0 && curArch == Arch) score |= 4 + 16;
        if (curArch < 0 && score != 0) score |= 4;
        return score;
    }

    /// <summary>
    /// calc_altsectscore: 0 — есть секция лучше под эту ОС или драйвер не для этого компьютера,
    /// 1 — подходит, 2 — подходит и каталог подписи действителен для этой версии Windows.
    /// </summary>
    private int AltSectScore(SdiCandidate c, DeviceInfo device)
    {
        var pack = c.Pack;
        int manuf = pack.DescOf(c.HwidIndex).ManufacturerIndex;
        for (int pos = 0; pos < pack.Manufacturers[manuf].SectionsCount; pos++)
            if (DecorScore(SectType(pack.SectionAt(manuf, pos))) > c.DecorScore) return 0;

        var path = c.Inf.Path;
        if (!IsForThisNotebook(path)) return 0;

        // Драйверы USB 3.0 Intel 2-го и 4-го поколений — только для своих корневых концентраторов.
        var pv = System.Text.RegularExpressions.Regex.Match(pack.PackName, @"_(\d+)");
        int packVersion = pv.Success && int.TryParse(pv.Groups[1].Value, out var v) ? v : 0;
        string intel2 = packVersion > 16073 ? @"intel_sdi_2nd\" : @"intel_2nd\";
        string intel4 = packVersion > 16073 ? @"intel_sdi_4th\" : @"intel_4th\";
        var ids = string.Join(" ", device.HardwareIds);
        if (path.Contains(intel2, StringComparison.OrdinalIgnoreCase)
            && !ids.Contains(@"IUSB3\ROOT_HUB30&VID_8086&PID_1E31", StringComparison.OrdinalIgnoreCase)) return 0;
        if (path.Contains(intel4, StringComparison.OrdinalIgnoreCase)
            && !new[] { "8C31", "8D31", "8C7F", "9C7F", "9C31", "9CB1", "A12F", "A22F", "9D2F", "A2AF", "22B5", "15B5",
                        "15B6", "15C1", "15DB", "15D4", "0F35" }
                .Any(pid => ids.Contains(@"IUSB3\ROOT_HUB30&VID_8086&PID_" + pid, StringComparison.OrdinalIgnoreCase))) return 0;

        if ((path.Contains(@"matchver\", StringComparison.OrdinalIgnoreCase)
             || path.Contains(@"L\Realtek\", StringComparison.OrdinalIgnoreCase)
             || path.Contains(@"L\R\", StringComparison.OrdinalIgnoreCase))
            && c.Version.V1 is 5 or 106) return 0; // isvalid_ver: драйверы только для XP и Vista

        if (ids.Contains("VEN_168C&DEV_002B&SUBSYS_30A117AA", StringComparison.OrdinalIgnoreCase)
            && c.Section.Contains("Realtek", StringComparison.OrdinalIgnoreCase)) return 0;

        if (path.Contains(@"matchmarker\", StringComparison.OrdinalIgnoreCase) && (MarkerScore(path) & 7) != 7) return 0;

        if (path.Contains("tweak", StringComparison.OrdinalIgnoreCase)
            || c.Inf.Name.Contains("tweak", StringComparison.OrdinalIgnoreCase)) return 1;
        return IsValidCat(c) ? 2 : 1;
    }

    /// <summary>calc_notebook: драйверы из папок «…_nb\» — только для ноутбуков своего бренда.</summary>
    private bool IsForThisNotebook(string path)
    {
        if (!path.Contains(@"_nb\", StringComparison.OrdinalIgnoreCase)
            && !path.Contains(@"Touchpad_Mouse\", StringComparison.OrdinalIgnoreCase)) return true;
        return _isLaptop && _marker.Length > 0 && path.Contains(_marker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>isvalidcat: в каталоге подписи указана эта версия Windows ("2:10.0").</summary>
    private static bool IsValidCat(SdiCandidate c)
    {
        var pack = c.Pack;
        int h = c.HwidIndex;
        var f = pack.Cat(h, InfField.CatalogFile_ntamd64).Length > 0 ? InfField.CatalogFile_ntamd64
              : pack.Cat(h, InfField.CatalogFile_ntx86).Length > 0 ? InfField.CatalogFile_ntx86
              : pack.Cat(h, InfField.CatalogFile_nt).Length > 0 ? InfField.CatalogFile_nt
              : InfField.CatalogFile;
        var s = pack.Cat(h, f);
        return s.Length > 0 && s.Contains($"2:{Major}.{Minor}", StringComparison.Ordinal);
    }

    /// <summary>State::genmarker: метка бренда ноутбука для папок драйверпаков ("Asus_nb").</summary>
    private static string GenMarker(string manufacturer)
    {
        string[][] filters =
        [
            ["Acer", "acer", "emachines", "packard", "bell", "gateway", "aspire"], ["Apple", "apple"], ["Asus", "asus"],
            ["OEM", "clevo", "eurocom", "sager", "iru", "viewsonic", "viewbook"],
            ["Dell", "dell", "alienware", "arima", "jetway", "gericom"], ["Fujitsu", "fujitsu", "sieme"],
            ["OEM", "ecs", "elitegroup", "roverbook", "rover", "shuttle"], ["HP", "hp", "hewle", "compaq"],
            ["OEM", "intel", "wistron"], ["Lenovo", "lenovo", "compal", "ibm"], ["LG", "lg"],
            ["OEM", "mitac", "mtc", "depo", "getac"], ["MSI", "msi", "micro-star"], ["Panasonic", "panasonic", "matsushita"],
            ["OEM", "quanta", "prolink", "nec", "k-systems", "benq", "vizio"], ["OEM", "pegatron", "medion"],
            ["Samsung", "samsung"], ["Gigabyte", "gigabyte"], ["Sony", "sony", "vaio"], ["Toshiba", "toshiba"],
            ["OEM", "twinhead", "durabook"], ["NEC", "Nec_brand", "nec"],
        ];
        var marker = "OEM_nb";
        if (string.IsNullOrEmpty(manufacturer)) return marker;
        foreach (var f in filters)
            for (int j = 1; j < f.Length; j++)
                if (manufacturer.Contains(f[j], StringComparison.OrdinalIgnoreCase)) marker = f[0] + "_nb";
        return marker;
    }
}
