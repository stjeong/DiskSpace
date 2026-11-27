using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DiskSpace;

/// <summary>Draws a nested, squarified treemap of a <see cref="DirNode"/>.</summary>
public sealed class TreemapView : Control
{
    // Sizes in 96-DPI pixels; scaled to the control's current DPI with S().
    // Rectangles narrower or shorter than MinDrawSize are not drawn; their parent's color shows through.
    private const float MinDrawSize = 10f;
    private const float Pad = 2f;
    private const float MinHeaderWidth = 24f;
    private const float MinLabelWidth = 30f;
    private const float HoverPenWidth = 2f;

    private static readonly Color RootHeaderColor = Color.FromArgb(255, 153, 0);
    private static readonly Color[] DirColors =
    [
        Color.FromArgb(222, 184, 135),
        Color.FromArgb(206, 164, 116),
        Color.FromArgb(230, 196, 154),
        Color.FromArgb(214, 174, 126),
    ];
    private static readonly Color PendingDirColor = Color.FromArgb(245, 232, 212);
    private static readonly Color FileColor = Color.FromArgb(138, 184, 233);
    private static readonly Color AggregateColor = Color.FromArgb(176, 204, 232);

    private readonly SolidBrush[] dirBrushes = DirColors.Select(c => new SolidBrush(c)).ToArray();
    private readonly SolidBrush rootBrush = new(RootHeaderColor);
    private readonly SolidBrush pendingBrush = new(PendingDirColor);
    private readonly SolidBrush fileBrush = new(FileColor);
    private readonly SolidBrush aggregateBrush = new(AggregateColor);
    private readonly SolidBrush hoverBrush = new(Color.FromArgb(70, Color.White));
    private readonly Pen dirPen = new(Color.FromArgb(150, 110, 70));
    private readonly Pen filePen = new(Color.FromArgb(70, 110, 160));
    private readonly Pen hoverPen = new(Color.Black, HoverPenWidth);
    private readonly HatchBrush pendingHatch = new(HatchStyle.BackwardDiagonal, Color.FromArgb(225, 205, 175), PendingDirColor);

    private readonly StringFormat headerFormat = new(StringFormatFlags.NoWrap)
    {
        Trimming = StringTrimming.EllipsisCharacter,
        LineAlignment = StringAlignment.Near,
    };
    private readonly StringFormat centerFormat = new()
    {
        Trimming = StringTrimming.EllipsisCharacter,
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
    };

    // Rectangles drawn during the last paint, parents before children (used for hit testing).
    private readonly List<(RectangleF Rect, Node Node)> hits = [];
    private DirNode? viewRoot;
    private Bitmap? cache;
    private bool dirty = true;
    private Node? hover;
    private RectangleF hoverRect;
    private Font? drawFont;

    public TreemapView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.White;
        // Font is inherited from the form so WinForms rescales it on DPI changes; labels use a slightly smaller copy.
    }

    /// <summary>Converts a 96-DPI length to pixels at the current DPI (the form's: children always share it).</summary>
    private float S(float value) => Dpi.Scale(value, (TopLevelControl ?? this).DeviceDpi);

    /// <summary>Label font: 8/9 of the inherited (DPI-scaled) font, recreated whenever that font changes.</summary>
    private Font DrawFont => drawFont ??= new Font(Font.FontFamily, Font.Size * 8f / 9f, Font.Style, Font.Unit);

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        drawFont?.Dispose();
        drawFont = null;
        RefreshMap();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        SetHover(null, RectangleF.Empty);
        RefreshMap();
    }

    public event Action<Node?>? HoverChanged;
    public event Action<Node, MouseEventArgs>? NodeClicked;

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public DirNode? ViewRoot
    {
        get => viewRoot;
        set
        {
            viewRoot = value;
            SetHover(null, RectangleF.Empty);
            RefreshMap();
        }
    }

    public Node? HitTest(Point p)
    {
        for (int i = hits.Count - 1; i >= 0; i--)
        {
            if (hits[i].Rect.Contains(p))
                return hits[i].Node;
        }
        return null;
    }

    /// <summary>Re-renders the treemap from the (possibly changed) tree.</summary>
    public void RefreshMap()
    {
        dirty = true;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        dirty = true;
    }

    private void RenderCache()
    {
        int w = Math.Max(1, ClientSize.Width), h = Math.Max(1, ClientSize.Height);
        if (cache is null || cache.Width != w || cache.Height != h)
        {
            cache?.Dispose();
            cache = new Bitmap(w, h);
        }

        using Graphics g = Graphics.FromImage(cache);
        g.Clear(BackColor);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        hits.Clear();
        if (viewRoot is not null)
            DrawDirectory(g, viewRoot, new RectangleF(0, 0, w - 1, h - 1), 0);

        // Re-locate the hovered node in the new layout.
        if (hover is not null)
        {
            int idx = hits.FindIndex(x => ReferenceEquals(x.Node, hover));
            hoverRect = idx >= 0 ? hits[idx].Rect : RectangleF.Empty;
        }
        dirty = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (dirty || cache is null) RenderCache();
        Graphics g = e.Graphics;
        g.DrawImageUnscaled(cache!, 0, 0);

        if (!hoverRect.IsEmpty)
        {
            float pw = S(HoverPenWidth);
            hoverPen.Width = pw;
            g.FillRectangle(hoverBrush, hoverRect);
            g.DrawRectangle(hoverPen, hoverRect.X + pw / 2, hoverRect.Y + pw / 2, hoverRect.Width - pw, hoverRect.Height - pw);
        }
    }

    private float HeaderHeight => DrawFont.Height + S(2);

    private void DrawDirectory(Graphics g, DirNode dir, RectangleF rect, int depth)
    {
        hits.Add((rect, dir));

        bool scanned = dir.IsScanned;
        Brush fill = !scanned ? pendingHatch : dirBrushes[depth % dirBrushes.Length];
        g.FillRectangle(fill, rect);
        g.DrawRectangle(dirPen, rect.X, rect.Y, rect.Width, rect.Height);

        float hh = HeaderHeight;
        bool hasHeader = rect.Height >= hh * 2 && rect.Width >= S(MinHeaderWidth);
        if (hasHeader)
        {
            var header = new RectangleF(rect.X + 1, rect.Y + 1, rect.Width - 1, hh - 1);
            if (depth == 0)
                g.FillRectangle(rootBrush, header);
            string name = depth == 0 ? dir.FullPath : dir.Name;
            string label = $"{name} - {SizeFormat.Format(dir.Size)}";
            g.DrawString(label, DrawFont, Brushes.Black, new RectangleF(header.X + 1, header.Y, header.Width - 2, header.Height), headerFormat);
        }

        float pad = S(Pad), minSize = S(MinDrawSize);
        var inner = hasHeader
            ? new RectangleF(rect.X + pad, rect.Y + hh, rect.Width - 2 * pad, rect.Height - hh - pad)
            : RectangleF.Inflate(rect, -pad, -pad);
        if (inner.Width < minSize || inner.Height < minSize) return;

        Node[]? children = dir.Children;
        if (children is null || children.Length == 0) return;

        // Snapshot sizes (they change while scanning) and sort descending.
        var items = new List<(Node Node, long Size)>(children.Length);
        long total = 0;
        foreach (Node c in children)
        {
            long s = c.Size;
            if (s <= 0) continue;
            items.Add((c, s));
            total += s;
        }
        if (items.Count == 0) return;
        items.Sort((a, b) => b.Size.CompareTo(a.Size));

        RectangleF[] rects = Squarify(items, total, inner);
        for (int i = 0; i < items.Count; i++)
        {
            RectangleF r = rects[i];
            if (r.Width < minSize || r.Height < minSize) continue;
            if (items[i].Node is DirNode sub)
                DrawDirectory(g, sub, r, depth + 1);
            else
                DrawFile(g, (FileNode)items[i].Node, r);
        }
    }

    private void DrawFile(Graphics g, FileNode file, RectangleF rect)
    {
        hits.Add((rect, file));
        g.FillRectangle(file.IsAggregate ? aggregateBrush : fileBrush, rect);
        g.DrawRectangle(filePen, rect.X, rect.Y, rect.Width, rect.Height);

        Font font = DrawFont;
        if (rect.Width >= S(MinLabelWidth) && rect.Height >= font.Height + S(2))
        {
            string text = rect.Height >= font.Height * 2 + S(2)
                ? $"{file.Name}\n{SizeFormat.Format(file.Size)}"
                : file.Name;
            g.DrawString(text, font, Brushes.Black, RectangleF.Inflate(rect, -S(2), -S(1)), centerFormat);
        }
    }

    /// <summary>Squarified treemap layout (Bruls, Huizing, van Wijk). Items must be sorted descending.</summary>
    private static RectangleF[] Squarify(List<(Node Node, long Size)> items, long total, RectangleF rect)
    {
        int n = items.Count;
        var result = new RectangleF[n];
        if (total <= 0 || rect.Width <= 0 || rect.Height <= 0) return result;

        double scale = rect.Width * (double)rect.Height / total;
        var areas = new double[n];
        for (int i = 0; i < n; i++) areas[i] = items[i].Size * scale;

        double x = rect.X, y = rect.Y, w = rect.Width, h = rect.Height;
        int start = 0;
        while (start < n)
        {
            if (w <= 0 || h <= 0) break;
            double side = Math.Min(w, h);
            double rowSum = areas[start];
            double best = Worst(rowSum, areas[start], areas[start], side);
            int end = start + 1;
            while (end < n)
            {
                double newSum = rowSum + areas[end];
                double wr = Worst(newSum, areas[end], areas[start], side);
                if (wr > best) break;
                rowSum = newSum;
                best = wr;
                end++;
            }

            if (w >= h)
            {
                // Column along the left edge.
                double cw = rowSum / h;
                double yy = y;
                for (int k = start; k < end; k++)
                {
                    double kh = areas[k] / cw;
                    result[k] = new RectangleF((float)x, (float)yy, (float)cw, (float)kh);
                    yy += kh;
                }
                x += cw;
                w -= cw;
            }
            else
            {
                // Row along the top edge.
                double rh = rowSum / w;
                double xx = x;
                for (int k = start; k < end; k++)
                {
                    double kw = areas[k] / rh;
                    result[k] = new RectangleF((float)xx, (float)y, (float)kw, (float)rh);
                    xx += kw;
                }
                y += rh;
                h -= rh;
            }
            start = end;
        }
        return result;
    }

    private static double Worst(double sum, double min, double max, double side)
    {
        double s2 = sum * sum, w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }

    private void SetHover(Node? node, RectangleF rect)
    {
        if (ReferenceEquals(node, hover)) return;
        RectangleF old = hoverRect;
        hover = node;
        hoverRect = rect;
        float m = S(HoverPenWidth);
        if (!old.IsEmpty) Invalidate(Rectangle.Ceiling(RectangleF.Inflate(old, m, m)));
        if (!rect.IsEmpty) Invalidate(Rectangle.Ceiling(RectangleF.Inflate(rect, m, m)));
        HoverChanged?.Invoke(node);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        for (int i = hits.Count - 1; i >= 0; i--)
        {
            if (hits[i].Rect.Contains(e.Location))
            {
                SetHover(hits[i].Node, hits[i].Rect);
                return;
            }
        }
        SetHover(null, RectangleF.Empty);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(null, RectangleF.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        Node? node = HitTest(e.Location);
        if (node is not null)
            NodeClicked?.Invoke(node, e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var b in dirBrushes) b.Dispose();
            rootBrush.Dispose();
            pendingBrush.Dispose();
            fileBrush.Dispose();
            aggregateBrush.Dispose();
            hoverBrush.Dispose();
            dirPen.Dispose();
            filePen.Dispose();
            hoverPen.Dispose();
            pendingHatch.Dispose();
            cache?.Dispose();
            drawFont?.Dispose();
            headerFormat.Dispose();
            centerFormat.Dispose();
        }
        base.Dispose(disposing);
    }
}
