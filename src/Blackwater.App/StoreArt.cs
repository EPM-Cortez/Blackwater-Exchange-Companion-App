using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace BlackwaterExchange.App;

/// <summary>
/// Draws the package's images from the logo in App.xaml (the website's shield), so they never drift from it:
/// <c>BlackwaterExchange.exe --assets FOLDER</c> writes the MSIX logos at every scale and app.ico, and
/// <c>--store-shots FOLDER</c> writes 1920×1080 Store screenshots of the window, with sample data.
/// </summary>
public static class StoreArt
{
    private static readonly int[] Scales = [100, 125, 150, 200, 400];

    public static void WriteAssets(string folder)
    {
        Directory.CreateDirectory(folder);
        var logo = (ImageSource)Application.Current.FindResource("Logo");
        foreach (var scale in Scales)
        {
            double s = scale / 100.0;
            Save(Draw(logo, (int)Math.Round(44 * s), (int)Math.Round(44 * s), 0.92), Path.Combine(folder, $"Square44x44Logo.scale-{scale}.png"));
            Save(Draw(logo, (int)Math.Round(150 * s), (int)Math.Round(150 * s), 0.62), Path.Combine(folder, $"Square150x150Logo.scale-{scale}.png"));
            Save(Draw(logo, (int)Math.Round(310 * s), (int)Math.Round(150 * s), 0.62), Path.Combine(folder, $"Wide310x150Logo.scale-{scale}.png"));
            Save(Draw(logo, (int)Math.Round(50 * s), (int)Math.Round(50 * s), 0.92), Path.Combine(folder, $"StoreLogo.scale-{scale}.png"));
        }
        // Taskbar, Start and file icons pick these by pixel size; "unplated" ones are drawn without a tile behind them.
        foreach (var size in (int[])[16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256])
        {
            var image = Draw(logo, size, size, 1.0);
            Save(image, Path.Combine(folder, $"Square44x44Logo.targetsize-{size}.png"));
            Save(image, Path.Combine(folder, $"Square44x44Logo.targetsize-{size}_altform-unplated.png"));
            Save(image, Path.Combine(folder, $"Square44x44Logo.targetsize-{size}_altform-lightunplated.png"));
        }
        WriteIcon(logo, Path.Combine(folder, "app.ico"), [16, 20, 24, 32, 40, 48, 64, 256]);
    }

    /// <summary>The logo centred on a transparent canvas, its height <paramref name="fill"/> of the canvas's.</summary>
    private static BitmapSource Draw(ImageSource logo, int width, int height, double fill)
    {
        var size = Math.Min(width, height) * fill;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawImage(logo, new Rect((width - size) / 2, (height - size) / 2, size, size));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void Save(BitmapSource image, string file) => File.WriteAllBytes(file, Png(image));

    /// <summary>A Windows .ico holding a PNG image at each size (supported since Windows Vista).</summary>
    private static void WriteIcon(ImageSource logo, string file, int[] sizes)
    {
        var images = sizes.Select(size => Png(Draw(logo, size, size, 1.0))).ToList();
        using var stream = File.Create(file);
        using var writer = new BinaryWriter(stream);
        writer.Write((short)0);            // reserved
        writer.Write((short)1);            // icon
        writer.Write((short)images.Count);
        var offset = 6 + 16 * images.Count;
        for (var index = 0; index < images.Count; index++)
        {
            var size = sizes[index];
            writer.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);         // no palette
            writer.Write((byte)0);
            writer.Write((short)1);        // colour planes
            writer.Write((short)32);       // bits per pixel
            writer.Write(images[index].Length);
            writer.Write(offset);
            offset += images[index].Length;
        }
        foreach (var image in images)
            writer.Write(image);
    }

    /// <summary>Store screenshots: the window as it really draws, centred on a 1920×1080 canvas in its own colours.</summary>
    public static void WriteScreenshot(FrameworkElement window, string file)
    {
        // As large as fits the 1080-pixel height with a margin.
        var scale = Math.Min(1.25, 1000 / window.ActualHeight);
        var shot = new RenderTargetBitmap((int)(window.ActualWidth * scale), (int)(window.ActualHeight * scale), 96 * scale, 96 * scale,
            PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen())
            context.DrawRectangle((Brush)Application.Current.FindResource("Bg"), null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        shot.Render(backdrop);
        shot.Render(window);
        shot.Freeze();

        var canvas = new Grid { Width = 1920, Height = 1080, Background = new SolidColorBrush(Color.FromRgb(0x08, 0x09, 0x0C)) };
        canvas.Children.Add(new Border
        {
            Child = new Image { Source = shot, Width = shot.PixelWidth, Height = shot.PixelHeight },
            BorderBrush = (Brush)Application.Current.FindResource("LineStrong"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 0, Opacity = 0.6 },
        });
        canvas.Measure(new Size(1920, 1080));
        canvas.Arrange(new Rect(0, 0, 1920, 1080));
        canvas.UpdateLayout();
        var output = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
        output.Render(canvas);
        Save(output, file);
    }
}
