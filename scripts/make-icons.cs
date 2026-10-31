#:property TargetFramework=net10.0-windows10.0.19041.0
#:property UseWPF=true
#:property PublishAot=false
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Generates the icon of LocalBell: src\LocalBell.App\Assets\LocalBell.ico (16 to 256 px), LocalBell.png (256 px, shown
// inside the app: an .ico would show its first, 16 px, frame), docs\icon-256.png (the README) and the logos of the
// Microsoft Store package (store\Assets, as NowPlaying's).
// The generated files are committed: run this again only to change the design.
// Design (chosen on October 31, 2026 among the proposals of .local\icon-mockups.cs): a white counter bell (push button,
// dome, base) on a rounded square with an amber-to-orange diagonal gradient; 16, 20, 24 and 32 px drawn by hand on whole
// pixels, a one pixel line between the dome and its base.
// Run from the repository root: dotnet run scripts\make-icons.cs
string root = Directory.GetCurrentDirectory();
if (!File.Exists(Path.Combine(root, "LocalBell.slnx"))) throw new InvalidOperationException("Run it from the repository root.");

var thread = new Thread(() => Icons.Write(root));
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

static class Icons
{
    static readonly Color From = Color.FromRgb(0xFF, 0xC2, 0x4B);
    static readonly Color To = Color.FromRgb(0xF2, 0x6B, 0x2A);
    static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 256];

    public static void Write(string root)
    {
        string assets = Path.Combine(root, "src", "LocalBell.App", "Assets");
        Directory.CreateDirectory(assets);
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        SaveIco(Path.Combine(assets, "LocalBell.ico"));
        SavePng(Draw(256), Path.Combine(assets, "LocalBell.png"));
        SavePng(Draw(256), Path.Combine(root, "docs", "icon-256.png"));

        // Logos of the Store package (store\AppxManifest.xml, scripts\build-store.ps1). Windows picks the size it needs:
        // a scale for the Start menu and the Store, a target size for the taskbar and File Explorer ("unplated": the
        // icon as it is, on no colored plate).
        string store = Path.Combine(root, "store", "Assets");
        Directory.CreateDirectory(store);
        foreach (int scale in new[] { 100, 200 })
        {
            SavePng(Draw(50 * scale / 100), Path.Combine(store, $"StoreLogo.scale-{scale}.png"));
            SavePng(Draw(44 * scale / 100), Path.Combine(store, $"Square44x44Logo.scale-{scale}.png"));
            SavePng(Draw(150 * scale / 100), Path.Combine(store, $"Square150x150Logo.scale-{scale}.png"));
        }
        foreach (int size in new[] { 16, 20, 24, 30, 32, 36, 40, 48, 64, 256 })
        {
            SavePng(Draw(size), Path.Combine(store, $"Square44x44Logo.targetsize-{size}.png"));
            SavePng(Draw(size), Path.Combine(store, $"Square44x44Logo.targetsize-{size}_altform-unplated.png"));
        }
    }

    // Small sizes on whole pixels (no 4 % margin), larger ones from the 256 design.
    static readonly Dictionary<int, double> HintedRadius = new() { [16] = 3.5, [20] = 4.5, [24] = 5.5, [32] = 7.5 };

    public static BitmapSource Draw(int size)
    {
        bool hinted = HintedRadius.ContainsKey(size);
        double m = hinted ? 0 : size * 0.04, s = size - 2 * m, k = s / 256;
        double radius = hinted ? HintedRadius[size] : s * 0.23;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(new LinearGradientBrush(From, To, new Point(0, 0), new Point(1, 1)), null,
                new Rect(m, m, s, s), radius, radius);
            Geometry bell = hinted ? Hinted(size) : Design();
            if (!hinted) bell.Transform = new MatrixTransform(k, 0, 0, k, m, m);
            dc.DrawGeometry(Brushes.White, null, bell);
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    // The counter bell: its push button (cap and stem), its dome, its base.
    static Geometry Bell(Rect cap, double capRadius, Rect stem, double cx, double cy, double r, Rect foot, double footRadius)
    {
        var dome = new PathFigure { StartPoint = new Point(cx - r, cy), IsClosed = true };
        dome.Segments.Add(new ArcSegment(new Point(cx + r, cy), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
        var bell = new GeometryGroup { FillRule = FillRule.Nonzero };
        bell.Children.Add(new RectangleGeometry(cap, capRadius, capRadius));
        bell.Children.Add(new RectangleGeometry(stem));
        bell.Children.Add(new PathGeometry([dome]));
        bell.Children.Add(new RectangleGeometry(foot, footRadius, footRadius));
        return bell;
    }

    // The 256 design, in the coordinates of the background square.
    static Geometry Design() =>
        Bell(new Rect(110, 58, 36, 14), 7, new Rect(121, 68, 14, 30), 128, 164, 68, new Rect(48, 174, 160, 22), 9);

    static Geometry Hinted(int size) => size switch
    {
        16 => Bell(new Rect(6, 3, 4, 1), 0, new Rect(7, 4, 2, 2), 8, 10, 4, new Rect(3, 11, 10, 2), 0.5),
        20 => Bell(new Rect(8, 4, 4, 1), 0, new Rect(9, 5, 2, 3), 10, 13, 5, new Rect(4, 14, 12, 2), 0.5),
        24 => Bell(new Rect(9, 4, 6, 2), 1, new Rect(11, 5, 2, 4), 12, 15, 6, new Rect(4, 16, 16, 3), 1),
        _ => Bell(new Rect(13, 7, 6, 2), 1, new Rect(15, 8, 2, 5), 16, 21, 8, new Rect(6, 22, 20, 3), 1.5),
    };

    static byte[] PngBytes(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    static void SavePng(BitmapSource bitmap, string path)
    {
        File.WriteAllBytes(path, PngBytes(bitmap));
        Console.WriteLine("  " + path);
    }

    // An ICO made of PNG-compressed entries (read by Windows since Vista).
    static void SaveIco(string path)
    {
        var images = IcoSizes.Select(size => PngBytes(Draw(size))).ToList();
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)IcoSizes.Length);
        int offset = 6 + 16 * IcoSizes.Length;
        for (int i = 0; i < IcoSizes.Length; i++)
        {
            byte dim = (byte)(IcoSizes[i] >= 256 ? 0 : IcoSizes[i]);
            writer.Write(dim);
            writer.Write(dim);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)images[i].Length);
            writer.Write((uint)offset);
            offset += images[i].Length;
        }
        foreach (var image in images) writer.Write(image);
        Console.WriteLine("  " + path);
    }
}
