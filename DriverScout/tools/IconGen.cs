// Генератор иконки DriverScout: оранжевая шестерёнка + белый гаечный ключ на тёмной плашке.
// Рисуется векторно в WPF под каждый размер отдельно, затем упаковывается в .ico (PNG-кадры).
// Запуск: отдельный консольный проект net8.0-windows с UseWPF, аргумент — папка для результата;
// затем скопировать app.ico в Assets\.
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static class Program
{
    static readonly Color Bg1 = Color.FromRgb(0x2C, 0x2C, 0x2E);
    static readonly Color Bg2 = Color.FromRgb(0x0E, 0x0E, 0x0F);
    static readonly Color Accent = Color.FromRgb(0xFF, 0x7A, 0x00);
    static readonly Color Accent2 = Color.FromRgb(0xFF, 0x9A, 0x3C);
    static readonly Color White = Color.FromRgb(0xF5, 0xF5, 0xF5);

    [STAThread]
    static void Main(string[] args)
    {
        var outDir = args[0];
        Directory.CreateDirectory(outDir);
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
        var frames = new List<(int size, byte[] png)>();
        foreach (var s in sizes)
        {
            var png = Render(s);
            frames.Add((s, png));
            if (s is 256 or 48 or 32 or 16) File.WriteAllBytes(Path.Combine(outDir, $"icon_{s}.png"), png);
        }
        File.WriteAllBytes(Path.Combine(outDir, "preview_512.png"), Render(512));
        WriteIco(Path.Combine(outDir, "app.ico"), frames);
        Console.WriteLine("ok");
    }

    static byte[] Render(int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            bool tiny = size <= 24;

            // Плашка: скруглённый квадрат с лёгким вертикальным градиентом и тонкой обводкой.
            var plate = new RectangleGeometry(new Rect(8, 8, 240, 240), 56, 56);
            dc.DrawGeometry(new LinearGradientBrush(Bg1, Bg2, 90), null, plate);
            // Едва заметный светлый кант сверху — плашка не сливается с тёмной панелью задач.
            if (!tiny)
                dc.DrawGeometry(null, new Pen(new LinearGradientBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 90), 2),
                    new RectangleGeometry(new Rect(9, 9, 238, 238), 55, 55));

            // Шестерёнка.
            var gear = Gear(new Point(128, 128), outer: 92, inner: 72, hole: tiny ? 26 : 32, teeth: 8);
            dc.DrawGeometry(new LinearGradientBrush(Accent2, Accent, 90), null, gear);

            // Ключ поверх, с «вырезом» цвета фона, чтобы отделялся от шестерёнки.
            var wrench = Wrench(tiny);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Bg2), tiny ? 20 : 14) { LineJoin = PenLineJoin.Round }, wrench);
            dc.DrawGeometry(new SolidColorBrush(White), null, wrench);
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Шестерёнка: зубцы-трапеции по окружности и отверстие в центре.</summary>
    static Geometry Gear(Point c, double outer, double inner, double hole, int teeth)
    {
        var fig = new PathFigure { IsClosed = true, IsFilled = true };
        double step = 2 * Math.PI / teeth;
        Point P(double r, double a) => new(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        for (int i = 0; i < teeth; i++)
        {
            double a = i * step - Math.PI / 2;
            // доли шага: основание зубца шире вершины
            var p0 = P(inner, a - step * 0.27);
            var p1 = P(outer, a - step * 0.17);
            var p2 = P(outer, a + step * 0.17);
            var p3 = P(inner, a + step * 0.27);
            if (i == 0) fig.StartPoint = p0; else fig.Segments.Add(new ArcSegment(p0, new Size(inner, inner), 0, false, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(p1, true));
            fig.Segments.Add(new ArcSegment(p2, new Size(outer, outer), 0, false, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(p3, true));
        }
        fig.Segments.Add(new ArcSegment(fig.StartPoint, new Size(inner, inner), 0, false, SweepDirection.Clockwise, true));
        var body = new PathGeometry([fig]);
        return Geometry.Combine(body, new EllipseGeometry(c, hole, hole), GeometryCombineMode.Exclude, null);
    }

    /// <summary>Гаечный ключ с открытым зевом, по диагонали снизу-слева вверх-вправо.</summary>
    static Geometry Wrench(bool tiny)
    {
        double hw = tiny ? 17 : 14; // половина ширины рукояти (на маленьких размерах толще)
        Geometry g = new RectangleGeometry(new Rect(-86, -hw, 160, hw * 2), hw, hw);
        g = Geometry.Combine(g, new EllipseGeometry(new Point(74, 0), 38, 38), GeometryCombineMode.Union, null);
        // Зев: вырез в головке, открытый наружу.
        var jaw = new PathGeometry([new PathFigure(new Point(70, -13), [
            new LineSegment(new Point(120, -26), true),
            new LineSegment(new Point(120, 26), true),
            new LineSegment(new Point(70, 13), true),
            new ArcSegment(new Point(70, -13), new Size(13, 13), 0, false, SweepDirection.Clockwise, true)
        ], true)]);
        g = Geometry.Combine(g, jaw, GeometryCombineMode.Exclude, null);
        // Отверстие на конце рукояти.
        if (!tiny) g = Geometry.Combine(g, new EllipseGeometry(new Point(-70, 0), 6.5, 6.5), GeometryCombineMode.Exclude, null);

        var t = new TransformGroup();
        t.Children.Add(new RotateTransform(-45));
        t.Children.Add(new TranslateTransform(124, 132));
        g.Transform = t;
        return g.GetFlattenedPathGeometry();
    }

    /// <summary>ICO с PNG-кадрами (поддерживается Windows Vista+).</summary>
    static void WriteIco(string path, List<(int size, byte[] png)> frames)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)frames.Count);
        int offset = 6 + 16 * frames.Count;
        foreach (var (size, png) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(png.Length); w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in frames) w.Write(png);
    }
}
