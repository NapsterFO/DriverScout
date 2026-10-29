// DriverScout — модуль драйверпаков Snappy Driver Installer Origin.
// Переработка кода SDIO (indexing.cpp, common.cpp, © Glenn Delahoy и авторы SDI/SDIO), распространяется по GNU GPL v3.

using System.IO;
using System.Text;
using SharpCompress.Compressors.LZMA;

namespace DriverScout.Sdi;

/// <summary>Версия и дата драйвера из INF (DriverVer), как Version в SDIO.</summary>
public readonly record struct SdiVersion(int Day, int Month, int Year, int V1, int V2, int V3, int V4)
{
    public bool HasVersion => V1 >= 0;
    public bool HasDate => Year >= 1000;
    public DateTime? Date => HasDate && Month is >= 1 and <= 12 && Day >= 1 && Day <= DateTime.DaysInMonth(Year, Month)
        ? new DateTime(Year, Month, Day) : null;
    public override string ToString() => HasVersion ? $"{V1}.{V2}.{V3}.{V4}" : "";

    /// <summary>cmpdate из SDIO: сравнение по году, месяцу, дню.</summary>
    public static int CompareDate(SdiVersion a, SdiVersion b) =>
        a.Year != b.Year ? a.Year - b.Year : a.Month != b.Month ? a.Month - b.Month : a.Day - b.Day;

    /// <summary>cmpversion из SDIO.</summary>
    public static int CompareVersion(SdiVersion a, SdiVersion b) =>
        a.V1 != b.V1 ? a.V1 - b.V1 : a.V2 != b.V2 ? a.V2 - b.V2 : a.V3 != b.V3 ? a.V3 - b.V3 : a.V4 - b.V4;
}

/// <summary>Поля [Version] INF-файла в порядке SDIO (enum в indexing.h).</summary>
public enum InfField
{
    ClassGuid, Class, Provider, CatalogFile, CatalogFile_nt, CatalogFile_ntx86, CatalogFile_ntia64,
    CatalogFile_ntamd64, DriverVer, DriverPackageDisplayName, DriverPackageType
}

/// <summary>
/// Индекс драйверпака SDIO (indexes\SDIO\DP_*.bin): список INF-файлов архива, их моделей и Hardware ID.
/// Формат — дамп структур SDIO (data_inffile_t, data_manufacturer_t, data_desc_t, data_HWID_t), сжатый LZMA86.
/// </summary>
public sealed class SdiIndex
{
    private const int VerIndex = 0x205;
    private const int NumFields = 11;

    public sealed record InfFile(string Path, string Name, int[] Fields, int[] Cats, SdiVersion Version, int Size, int Crc);
    public sealed record Manufacturer(int InfFileIndex, int NameOffset, int SectionsOffset, int SectionsCount);
    public sealed record Desc(int ManufacturerIndex, int SectPos, int DescOffset, int InstallOffset, int InstallPickedOffset, int Feature);
    public sealed record Hwid(int DescIndex, int InfPos, string Id);

    /// <summary>Имя драйверпака без расширения: "DP_LAN_Realtek_26102".</summary>
    public string PackName { get; }
    public List<InfFile> InfFiles { get; } = [];
    public List<Manufacturer> Manufacturers { get; } = [];
    public List<Desc> Descs { get; } = [];
    public List<Hwid> Hwids { get; } = [];
    private byte[] _text = [];

    /// <summary>Hardware ID (в верхнем регистре) → номера записей в Hwids.</summary>
    public Dictionary<string, List<int>> ByHwid { get; } = new(StringComparer.Ordinal);

    private SdiIndex(string packName) => PackName = packName;

    /// <param name="wanted">Hardware/Compatible ID устройств (в верхнем регистре): в памяти остаются только они —
    /// во всех драйверпаках вместе 3+ млн записей.</param>
    public static SdiIndex Load(string binFile, IReadOnlySet<string> wanted)
    {
        var raw = File.ReadAllBytes(binFile);
        if (raw.Length < 7 || Encoding.ASCII.GetString(raw, 0, 3) != "SDW")
            throw new InvalidDataException("Не индекс SDIO: " + binFile);
        int version = BitConverter.ToInt32(raw, 3);
        if (version is not (VerIndex or 0x204))
            throw new InvalidDataException($"Неподдерживаемая версия индекса 0x{version:X}: {binFile}");

        var data = Lzma86Decode(raw.AsSpan(7));
        var index = new SdiIndex(Path.GetFileNameWithoutExtension(binFile));
        index.Parse(data, wanted);
        return index;
    }

    private void Parse(byte[] d, IReadOnlySet<string> wanted)
    {
        int p = 0;
        int Int() { var v = BitConverter.ToInt32(d, p); p += 4; return v; }
        // vector_load: int байтов, int элементов (0 — то же, что байтов), затем данные.
        (int count, int start) Vector(int itemSize)
        {
            int bytes = Int(), count = Int();
            if (count == 0) count = bytes / Math.Max(itemSize, 1);
            int start = p; p += bytes;
            return (count, start);
        }

        var (nInf, sInf) = Vector(132);
        var (nMan, sMan) = Vector(16);
        var (nDesc, sDesc) = Vector(24);
        var (nHwid, sHwid) = Vector(12);
        var (_, sText) = Vector(1);
        _text = d[sText..p];

        int I(int at) => BitConverter.ToInt32(d, at);
        for (int i = 0; i < nInf; i++)
        {
            int a = sInf + i * 132;
            var fields = Enumerable.Range(0, NumFields).Select(k => I(a + 8 + k * 4)).ToArray();
            var cats = Enumerable.Range(0, NumFields).Select(k => I(a + 52 + k * 4)).ToArray();
            int v = a + 96; // Version: d, m, y, v1..v4
            InfFiles.Add(new InfFile(Str(I(a)), Str(I(a + 4)), fields, cats,
                new SdiVersion(I(v), I(v + 4), I(v + 8), I(v + 12), I(v + 16), I(v + 20), I(v + 24)),
                I(a + 124), I(a + 128)));
        }
        for (int i = 0; i < nMan; i++)
        {
            int a = sMan + i * 16;
            Manufacturers.Add(new Manufacturer(I(a), I(a + 4), I(a + 8), I(a + 12)));
        }
        for (int i = 0; i < nDesc; i++)
        {
            int a = sDesc + i * 24;
            Descs.Add(new Desc(I(a), I(a + 4), I(a + 8), I(a + 12), I(a + 16), I(a + 20)));
        }
        for (int i = 0; i < nHwid; i++)
        {
            int a = sHwid + i * 12;
            var id = Str(I(a + 8)).ToUpperInvariant();
            if (!wanted.Contains(id)) continue;
            if (!ByHwid.TryGetValue(id, out var list)) ByHwid[id] = list = [];
            list.Add(Hwids.Count);
            Hwids.Add(new Hwid(I(a), I(a + 4), id));
        }
    }

    /// <summary>Строка из текстового блока индекса (ANSI, оканчивается нулём).</summary>
    public string Str(int offset)
    {
        if (offset <= 0 || offset >= _text.Length) return "";
        int end = Array.IndexOf(_text, (byte)0, offset);
        return Encoding.Latin1.GetString(_text, offset, (end < 0 ? _text.Length : end) - offset);
    }

    private int TextInt(int offset) => BitConverter.ToInt32(_text, offset);

    // --- Доступ к данным совпадения (getdrp_* в matcher.cpp) ---

    public Desc DescOf(int hwidIndex) => Descs[Hwids[hwidIndex].DescIndex];
    public Manufacturer ManufacturerOf(int hwidIndex) => Manufacturers[DescOf(hwidIndex).ManufacturerIndex];
    public InfFile InfOf(int hwidIndex) => InfFiles[ManufacturerOf(hwidIndex).InfFileIndex];

    /// <summary>getdrp_drvsectionAtPos: секция моделей с декорацией ("Realtek.NTamd64.10.0").</summary>
    public string SectionAt(int manufacturerIndex, int pos)
    {
        var m = Manufacturers[manufacturerIndex];
        int baseName = TextInt(m.SectionsOffset);
        return pos == 0 ? Str(baseName) : Str(baseName) + "." + Str(TextInt(m.SectionsOffset + pos * 4));
    }

    public string Section(int hwidIndex) => SectionAt(DescOf(hwidIndex).ManufacturerIndex, DescOf(hwidIndex).SectPos);
    public string Field(int hwidIndex, InfField f) => Str(InfOf(hwidIndex).Fields[(int)f]);
    public string Cat(int hwidIndex, InfField f) => Str(InfOf(hwidIndex).Cats[(int)f]);

    // --- LZMA86 (Lzma86Dec.c из LZMA SDK, общественное достояние) ---

    /// <summary>Заголовок: фильтр (0 — нет, 1 — x86 BCJ), 5 байт свойств LZMA, 8 байт размера, затем поток LZMA.</summary>
    private static byte[] Lzma86Decode(ReadOnlySpan<byte> src)
    {
        byte filter = src[0];
        var props = src.Slice(1, 5).ToArray();
        long size = BitConverter.ToInt64(src.Slice(6, 8));
        if (size is < 0 or > 512 * 1024 * 1024) throw new InvalidDataException("Повреждённый индекс SDIO");

        var output = new byte[size];
        using (var input = new MemoryStream(src[14..].ToArray()))
        using (var lzma = LzmaStream.Create(props, input, input.Length, size, false))
        {
            int done = 0;
            while (done < size)
            {
                int n = lzma.Read(output, done, (int)Math.Min(size - done, 1 << 20));
                if (n <= 0) throw new InvalidDataException("Индекс SDIO обрывается");
                done += n;
            }
        }
        if (filter == 1) X86Decode(output);
        else if (filter != 0) throw new InvalidDataException($"Неизвестный фильтр LZMA86: {filter}");
        return output;
    }

    /// <summary>x86_Convert (Bra86.c) в режиме распаковки: возвращает относительные адреса вызовов E8/E9.</summary>
    private static void X86Decode(byte[] data)
    {
        ReadOnlySpan<byte> allowed = [1, 1, 1, 0, 1, 0, 0, 0];
        ReadOnlySpan<byte> bitNumber = [0, 1, 2, 2, 3, 3, 3, 3];
        static bool Test86MSByte(byte b) => b == 0 || b == 0xFF;

        int size = data.Length;
        if (size < 5) return;
        uint ip = 5;
        int bufferPos = 0;
        long prevPosT = -1;
        uint prevMask = 0;
        while (true)
        {
            int p = bufferPos, limit = size - 4;
            while (p < limit && (data[p] & 0xFE) != 0xE8) p++;
            bufferPos = p;
            if (p >= limit) break;

            prevPosT = bufferPos - prevPosT;
            if (prevPosT > 3) prevMask = 0;
            else
            {
                prevMask = (prevMask << ((int)prevPosT - 1)) & 0x7;
                if (prevMask != 0)
                {
                    byte b = data[p + 4 - bitNumber[(int)prevMask]];
                    if (allowed[(int)prevMask] == 0 || Test86MSByte(b))
                    {
                        prevPosT = bufferPos;
                        prevMask = ((prevMask << 1) & 0x7) | 1;
                        bufferPos++;
                        continue;
                    }
                }
            }
            prevPosT = bufferPos;

            if (Test86MSByte(data[p + 4]))
            {
                uint src = ((uint)data[p + 4] << 24) | ((uint)data[p + 3] << 16) | ((uint)data[p + 2] << 8) | data[p + 1];
                uint dest;
                while (true)
                {
                    dest = src - (ip + (uint)bufferPos);
                    if (prevMask == 0) break;
                    int index = bitNumber[(int)prevMask] * 8;
                    byte b = (byte)(dest >> (24 - index));
                    if (!Test86MSByte(b)) break;
                    src = dest ^ (uint)((1L << (32 - index)) - 1);
                }
                data[p + 4] = (byte)~(((dest >> 24) & 1) - 1);
                data[p + 3] = (byte)(dest >> 16);
                data[p + 2] = (byte)(dest >> 8);
                data[p + 1] = (byte)dest;
                bufferPos += 5;
            }
            else
            {
                prevMask = ((prevMask << 1) & 0x7) | 1;
                bufferPos++;
            }
        }
    }
}
