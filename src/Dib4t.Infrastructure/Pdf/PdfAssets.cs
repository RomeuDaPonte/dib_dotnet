namespace Dib4t.Infrastructure.Pdf;

/// <summary>
/// The legacy inputs of the budget PDF, embedded in this assembly (<c>Pdf/Assets</c>, written by
/// <c>tools/legacy/pdf-golden.js</c> from the legacy sources byte for byte): pdfmake's Roboto fonts, the logo of
/// <c>helpers/logoForPdf.js</c> and the bundle's Unicode tables.
/// </summary>
internal static class PdfAssets
{
    public const string RobotoRegular = "Roboto-Regular.ttf";
    public const string RobotoMedium = "Roboto-Medium.ttf";
    public const string Logo = "logo.jpg";

    private const string Prefix = "Dib4t.Infrastructure.Pdf.Assets.";

    public static Stream Open(string name) =>
        typeof(PdfAssets).Assembly.GetManifestResourceStream(Prefix + name)
        ?? throw new InvalidOperationException($"Embedded PDF asset {name} is missing.");

    public static byte[] Read(string name)
    {
        using var stream = Open(name);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
