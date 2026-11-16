using PDFtoImage;
using SkiaSharp;

namespace Dib4t.Infrastructure.Tests.Pdf;

/// <summary>Renders PDFs with PDFium (PDFtoImage) and compares pages pixel by pixel.</summary>
internal static class PdfPixels
{
    /// <summary>Resolution of the comparison (PLAN.md §7.1: same renderer, same DPI).</summary>
    public const int Dpi = 100;

    /// <summary>A pixel differs when a channel differs by more than this (0: any difference counts).</summary>
    public const int ChannelTolerance = 0;

    /// <summary>Where the comparison images of the last run go (rendered pages and differences), for a look by eye.</summary>
    public static string OutputDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "pdf-visual-diff");

    public static List<SKBitmap> Render(byte[] pdf)
    {
#pragma warning disable CA1416 // PDFium has native builds for the test machines (macOS, Linux, Windows)
        return [.. Conversion.ToImages(pdf, options: new RenderOptions(Dpi: Dpi, WithAnnotations: false, WithFormFill: false, AntiAliasing: PdfAntiAliasing.All))];
#pragma warning restore CA1416
    }

    public static List<SKBitmap> Load(IEnumerable<string> pngFiles) => [.. pngFiles.Select(f => SKBitmap.Decode(f))];

    public static void Save(SKBitmap bitmap, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(file);
        data.SaveTo(stream);
    }

    /// <summary>The number of pixels that differ by more than <see cref="ChannelTolerance"/> in a channel; writes a difference image.</summary>
    public static int DifferentPixels(SKBitmap expected, SKBitmap actual, string? diffFile = null)
    {
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            return int.MaxValue;
        }

        using var diff = new SKBitmap(expected.Width, expected.Height);
        var count = 0;
        for (var y = 0; y < expected.Height; y++)
        {
            for (var x = 0; x < expected.Width; x++)
            {
                var e = expected.GetPixel(x, y);
                var a = actual.GetPixel(x, y);
                var delta = Math.Max(Math.Abs(e.Red - a.Red), Math.Max(Math.Abs(e.Green - a.Green), Math.Abs(e.Blue - a.Blue)));
                if (delta > ChannelTolerance)
                {
                    count++;
                    diff.SetPixel(x, y, SKColors.Red);
                }
                else
                {
                    diff.SetPixel(x, y, e.WithAlpha(60));
                }
            }
        }

        if (diffFile is not null)
        {
            Save(diff, diffFile);
        }

        return count;
    }
}
