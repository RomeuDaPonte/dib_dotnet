namespace Dib4t.Infrastructure.Pdf.Layout;

/// <summary>A node of the document definition (the subset of pdfmake the budget PDF uses).</summary>
internal abstract record DocNode;

/// <summary><c>{ image, width, absolutePosition }</c>; the height follows the image's aspect ratio.</summary>
internal sealed record ImageNode(double X, double Y, double Width, int PixelWidth, int PixelHeight) : DocNode;

/// <summary><c>{ text, absolutePosition, id }</c>.</summary>
internal sealed record TextNode(double X, double Y, IReadOnlyList<TextRun> Runs, string? Id = null) : DocNode;

/// <summary><c>{ canvas: [...] }</c> (not absolutely positioned: drawn from the page margins).</summary>
internal sealed record CanvasNode(IReadOnlyList<Vector> Vectors) : DocNode;

internal abstract record Vector;

internal sealed record LineVector(double X1, double Y1, double X2, double Y2, double LineWidth, string LineColor) : Vector;

internal sealed record RectVector(double X, double Y, double W, double H, string Color) : Vector;

/// <summary>A laid-out page: its items in drawing order (pdfmake's <c>page.items</c>).</summary>
internal sealed class Page
{
    public List<PageItem> Items { get; } = [];
}

internal abstract record PageItem;

internal sealed record LineItem(Line Line) : PageItem;

internal sealed record VectorItem(Vector Vector) : PageItem;

internal sealed record ImageItem(double X, double Y, double Width, double Height) : PageItem;

/// <summary>
/// Port of the parts of pdfmake 0.1.37's <c>LayoutBuilder</c>, <c>DocumentContext</c>, <c>PageElementWriter</c> and
/// <c>ElementWriter</c> the budget PDF uses: absolutely positioned blocks, text lines flowing onto new pages, a
/// canvas, and the document's <c>pageBreakBefore</c> (the node with id <c>anchor</c> starts a new page when the
/// document has more than one page). A4, 40 pt margins.
/// </summary>
internal sealed class PdfmakeLayout
{
    public const double PageWidth = 595.28;
    public const double PageHeight = 841.89;
    public const double Margin = 40;

    private readonly List<Page> _pages = [];
    private readonly Stack<Snapshot> _snapshots = new();
    private readonly DocumentFonts _fonts;
    private int _page = -1;
    private double _x = Margin;
    private double _y;
    private double _availableWidth = PageWidth - Margin - Margin;
    private double _availableHeight;

    private PdfmakeLayout(DocumentFonts fonts)
    {
        _fonts = fonts;
        AddPage();
    }

    /// <summary>
    /// <c>layoutDocument</c>: lays the content out; when it takes more than one page, lays it out again with a page
    /// break before the <c>anchor</c> node (the legacy <c>pageBreakBefore</c>).
    /// </summary>
    public static IReadOnlyList<Page> Layout(IReadOnlyList<DocNode> content, DocumentFonts fonts)
    {
        var first = new PdfmakeLayout(fonts).Run(content, breakBeforeAnchor: false, out var anchorHasLines);
        return first.Count > 1 && anchorHasLines ? new PdfmakeLayout(fonts).Run(content, breakBeforeAnchor: true, out _) : first;
    }

    /// <summary>The number of lines of a text node at its position (the width pdfmake gives an absolute block).</summary>
    public static (int Lines, double LineHeight) Measure(TextNode node, DocumentFonts fonts)
    {
        var inlines = PdfmakeText.Measure(node.Runs, fonts);
        var lineHeight = inlines.Count == 0 ? 0 : inlines.Max(i => i.Height);
        var count = 0;
        while (PdfmakeText.BuildNextLine(inlines, PageWidth - node.X - Margin) is not null)
        {
            count++;
        }

        return (count, lineHeight);
    }

    private List<Page> Run(IReadOnlyList<DocNode> content, bool breakBeforeAnchor, out bool anchorHasLines)
    {
        // DocMeasure first (every text node, in order), then the layout.
        var measured = content.Select(node => node is TextNode text ? PdfmakeText.Measure(text.Runs, _fonts) : null).ToList();
        anchorHasLines = false;
        for (var i = 0; i < content.Count; i++)
        {
            switch (content[i])
            {
                case ImageNode image:
                    BeginDetachedBlock();
                    MoveTo(image.X, image.Y);
                    AddImage(image);
                    EndDetachedBlock();
                    break;
                case TextNode text:
                    if (text.Id == "anchor" && breakBeforeAnchor)
                    {
                        MoveToNextPage();
                    }

                    BeginDetachedBlock();
                    MoveTo(text.X, text.Y);
                    var lines = ProcessLeaf(measured[i]!);
                    EndDetachedBlock();
                    if (text.Id == "anchor")
                    {
                        anchorHasLines = lines > 0;
                    }

                    break;
                case CanvasNode canvas:
                    ProcessCanvas(canvas);
                    break;
                default:
                    throw new InvalidOperationException("Unknown document node " + content[i]);
            }
        }

        return _pages;
    }

    private int ProcessLeaf(List<Inline> inlines)
    {
        var count = 0;
        while (PdfmakeText.BuildNextLine(inlines, _availableWidth) is { } line)
        {
            // PageElementWriter.addLine (fitOnPage): on a new page when the line does not fit.
            if (!WriteLine(line))
            {
                MoveToNextPage();
                WriteLine(line);
            }

            count++;
        }

        return count;
    }

    /// <summary><c>ElementWriter.addLine</c>.</summary>
    private bool WriteLine(Line line)
    {
        var height = line.Height;
        if (_availableHeight < height)
        {
            return false;
        }

        line.X = _x + line.X;
        line.Y = _y + line.Y;
        PdfmakeText.AlignLine(line, _availableWidth);
        _pages[_page].Items.Add(new LineItem(line));
        MoveDown(height);
        return true;
    }

    private void AddImage(ImageNode image)
    {
        var height = image.PixelHeight * image.Width / image.PixelWidth;
        _pages[_page].Items.Add(new ImageItem(_x, _y, image.Width, height));
        MoveDown(height);
    }

    private void ProcessCanvas(CanvasNode canvas)
    {
        var height = canvas.Vectors.Max(v => v switch
        {
            LineVector l => Math.Max(l.Y1, l.Y2),
            RectVector r => r.Y + r.H,
            _ => 0,
        });
        if (_availableHeight < height)
        {
            MoveToNextPage();
        }

        foreach (var vector in canvas.Vectors)
        {
            _pages[_page].Items.Add(new VectorItem(vector switch
            {
                LineVector l => l with { X1 = l.X1 + _x, X2 = l.X2 + _x, Y1 = l.Y1 + _y, Y2 = l.Y2 + _y },
                RectVector r => r with { X = r.X + _x, Y = r.Y + _y },
                _ => vector,
            }));
        }
    }

    private void MoveTo(double x, double y)
    {
        _x = x;
        _availableWidth = PageWidth - _x - Margin;
        _y = y;
        _availableHeight = PageHeight - _y - Margin;
    }

    private void MoveDown(double offset)
    {
        _y += offset;
        _availableHeight -= offset;
    }

    private void MoveToNextPage()
    {
        var nextPageIndex = _page + 1;
        if (nextPageIndex >= _pages.Count)
        {
            var currentAvailableWidth = _availableWidth;
            AddPage();
            _availableWidth = currentAvailableWidth;
        }
        else
        {
            _page = nextPageIndex;
            InitializePage();
        }
    }

    private void AddPage()
    {
        _pages.Add(new Page());
        _page = _pages.Count - 1;
        InitializePage();
    }

    private void InitializePage()
    {
        _y = Margin;
        _availableHeight = PageHeight - Margin - Margin;
        // pageSnapshot().availableWidth: the outermost detached block's saved width when inside one.
        if (_snapshots.Count > 0)
        {
            _snapshots.Last().AvailableWidth = PageWidth - Margin - Margin;
        }
        else
        {
            _availableWidth = PageWidth - Margin - Margin;
        }
    }

    private void BeginDetachedBlock() => _snapshots.Push(new Snapshot(_x, _y, _availableHeight, _availableWidth, _page));

    private void EndDetachedBlock()
    {
        var saved = _snapshots.Pop();
        _x = saved.X;
        _y = saved.Y;
        _availableWidth = saved.AvailableWidth;
        _availableHeight = saved.AvailableHeight;
        _page = saved.Page;
    }

    private sealed class Snapshot(double x, double y, double availableHeight, double availableWidth, int page)
    {
        public double X { get; } = x;

        public double Y { get; } = y;

        public double AvailableHeight { get; } = availableHeight;

        public double AvailableWidth { get; set; } = availableWidth;

        public int Page { get; } = page;
    }
}
