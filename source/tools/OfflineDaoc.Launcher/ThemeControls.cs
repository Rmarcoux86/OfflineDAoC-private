using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.ComponentModel;
using System.Reflection;

namespace OfflineDaoc.Launcher;

internal static class DaocTheme
{
    internal static readonly Color Void = Color.FromArgb(18, 15, 12);
    internal static readonly Color Stone = Color.FromArgb(47, 43, 36);
    internal static readonly Color StoneDark = Color.FromArgb(25, 24, 21);
    internal static readonly Color StoneLight = Color.FromArgb(79, 71, 56);
    internal static readonly Color Iron = Color.FromArgb(102, 100, 90);
    internal static readonly Color Gold = Color.FromArgb(194, 157, 77);
    internal static readonly Color GoldLight = Color.FromArgb(232, 205, 135);
    internal static readonly Color Parchment = Color.FromArgb(215, 199, 157);
    internal static readonly Color Ink = Color.FromArgb(43, 32, 22);
    internal static readonly Color Panel = Color.FromArgb(35, 31, 26);
    internal static readonly Color PanelAlt = Color.FromArgb(42, 37, 30);
    internal static readonly Color Text = Color.FromArgb(224, 215, 190);
    internal static readonly Color Muted = Color.FromArgb(166, 154, 128);
    internal static readonly Color Success = Color.FromArgb(139, 191, 104);
    internal static readonly Color Danger = Color.FromArgb(232, 95, 83);
    internal static readonly Color Albion = Color.FromArgb(171, 64, 58);
    internal static readonly Color Midgard = Color.FromArgb(62, 94, 144);
    internal static readonly Color Hibernia = Color.FromArgb(57, 117, 66);
    // DAoC Expedition accents: torchlight for the road ahead (Ember, EmberLight for text) and weathered bronze for map
    // lines and frames. They sit beside Gold, never in place of it.
    internal static readonly Color Ember = Color.FromArgb(204, 102, 40);
    internal static readonly Color EmberLight = Color.FromArgb(240, 158, 84);
    internal static readonly Color Bronze = Color.FromArgb(150, 108, 62);
}

internal sealed class StoneSurface : Panel
{
    public StoneSurface()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        Padding = new Padding(14);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var background = new LinearGradientBrush(ClientRectangle, Color.FromArgb(54, 50, 42), Color.FromArgb(25, 23, 20), 90f);
        e.Graphics.FillRectangle(background, ClientRectangle);

        // Subtle deterministic banding gives the surface the compressed stone texture
        // common to small fan launchers without shipping a large modern art asset.
        for (var y = 5; y < Height; y += 7)
        {
            var shade = 33 + ((y * 17) % 13);
            using var texture = new Pen(Color.FromArgb(65, shade + 12, shade + 8, shade));
            e.Graphics.DrawLine(texture, 2, y, Width - 3, y);
        }

        using var outer = new Pen(Color.FromArgb(12, 10, 8), 2f);
        using var middle = new Pen(DaocTheme.StoneLight);
        using var inner = new Pen(DaocTheme.Bronze);
        e.Graphics.DrawRectangle(outer, 1, 1, Math.Max(0, Width - 3), Math.Max(0, Height - 3));
        e.Graphics.DrawRectangle(middle, 3, 3, Math.Max(0, Width - 7), Math.Max(0, Height - 7));
        e.Graphics.DrawRectangle(inner, 6, 6, Math.Max(0, Width - 13), Math.Max(0, Height - 13));
    }
}

internal sealed class InsetPanel : Panel
{
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Accent { get; init; } = DaocTheme.Gold;

    public InsetPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = DaocTheme.Panel;
        Padding = new Padding(2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var shadow = new Pen(Color.FromArgb(10, 9, 7), 2f);
        using var edge = new Pen(DaocTheme.StoneLight);
        using var accent = new Pen(Accent);
        e.Graphics.DrawRectangle(shadow, 1, 1, Math.Max(0, Width - 3), Math.Max(0, Height - 3));
        e.Graphics.DrawLine(edge, 3, 3, Width - 4, 3);
        e.Graphics.DrawLine(accent, 3, Height - 4, Width - 4, Height - 4);

        // End caps on the accent line, like the scale bar of a map.
        e.Graphics.DrawLine(accent, 3, Height - 7, 3, Height - 4);
        e.Graphics.DrawLine(accent, Width - 4, Height - 7, Width - 4, Height - 4);
    }
}

internal sealed class RuneButton : Button
{
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowOrnaments { get; set; } = true;
    private bool _hovered;
    private bool _pressed;
    private Color _accent = DaocTheme.Gold;
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Accent
    {
        get => _accent;
        set
        {
            _accent = value;
            Invalidate();
        }
    }

    public RuneButton()
    {
        DoubleBuffered = true;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Font = new Font("Georgia", 8.25f, FontStyle.Bold);
        ForeColor = DaocTheme.GoldLight;
        Width = 104;
        Height = 31;
        Margin = new Padding(4, 0, 0, 0);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var top = _pressed ? Color.FromArgb(30, 27, 23) : _hovered ? Color.FromArgb(84, 68, 48) : Color.FromArgb(58, 52, 42);
        var bottom = _pressed ? Color.FromArgb(51, 45, 36) : Color.FromArgb(27, 25, 22);
        using var fill = new LinearGradientBrush(bounds, top, bottom, 90f);
        e.Graphics.FillRectangle(fill, bounds);
        using var outer = new Pen(Color.FromArgb(12, 10, 8));
        using var inner = new Pen(Enabled ? Accent : Color.FromArgb(86, 82, 72));
        using var shine = new Pen(Color.FromArgb(112, 102, 81));
        e.Graphics.DrawRectangle(outer, bounds);
        e.Graphics.DrawRectangle(inner, 2, 2, Width - 5, Height - 5);
        e.Graphics.DrawLine(shine, 3, 3, Width - 4, 3);

        if (_hovered && Enabled)
        {
            // Torchlight along the lower edge while the pointer is over the button.
            using var torch = new Pen(DaocTheme.Ember);
            e.Graphics.DrawLine(torch, 3, Height - 4, Width - 4, Height - 4);
        }

        using var ornament = new SolidBrush(Enabled ? Accent : Color.FromArgb(86, 82, 72));
        Point[] leftDiamond = [new(8, Height / 2), new(11, Height / 2 - 3), new(14, Height / 2), new(11, Height / 2 + 3)];
        Point[] rightDiamond = [new(Width - 9, Height / 2), new(Width - 12, Height / 2 - 3), new(Width - 15, Height / 2), new(Width - 12, Height / 2 + 3)];
        if (ShowOrnaments)
        {
            e.Graphics.FillPolygon(ornament, leftDiamond);
            e.Graphics.FillPolygon(ornament, rightDiamond);
        }

        var offset = _pressed ? 1 : 0;
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(offset, offset, Width, Height),
            Enabled ? ForeColor : Color.FromArgb(111, 106, 94), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class FantasyBanner : Panel
{
    private static readonly Lazy<Image?> Artwork = new(() =>
    {
        using Stream? stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("OfflineDaoc.Launcher.Assets.offline-daoc-header.png");
        if (stream == null)
            return null;
        using var loaded = new Bitmap(stream);
        return new Bitmap(loaded);
    });

    // The expedition route is drawn between these two controls (the title and the header buttons); see SetRoute.
    private Control? _routeStart;
    private Control? _routeEnd;

    public FantasyBanner()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        Padding = new Padding(4);
    }

    /// <summary>
    /// Draws the five-act route map in the free space right of <paramref name="start"/> and left of <paramref name="end"/>
    /// (both inside this banner). Their positions are read when painting, so the map follows layout, resizing and UiScale.
    /// </summary>
    internal void SetRoute(Control start, Control end)
    {
        _routeStart = start;
        _routeEnd = end;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.FromArgb(18, 19, 18));
        Image? artwork = Artwork.Value;
        if (artwork != null && Width > 0 && Height > 0)
        {
            float scale = Math.Max(Width / (float)artwork.Width, Height / (float)artwork.Height);
            float sourceWidth = Width / scale;
            float sourceHeight = Height / scale;
            float sourceX = Math.Max(0, (artwork.Width - sourceWidth) / 2f);
            float sourceY = Math.Max(0, (artwork.Height - sourceHeight) / 2f);
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(artwork, ClientRectangle,
                sourceX, sourceY, sourceWidth, sourceHeight, GraphicsUnit.Pixel);
        }

        using var shade = new LinearGradientBrush(ClientRectangle,
            Color.FromArgb(190, 10, 9, 8), Color.FromArgb(80, 10, 9, 8), 0f);
        e.Graphics.FillRectangle(shade, ClientRectangle);
        using var lowerShade = new LinearGradientBrush(ClientRectangle,
            Color.FromArgb(5, 0, 0, 0), Color.FromArgb(105, 0, 0, 0), 90f);
        e.Graphics.FillRectangle(lowerShade, ClientRectangle);

        if (Width > 0 && Height > 0)
        {
            // Torchlight rising from the lower edge.
            using var torchlight = new LinearGradientBrush(ClientRectangle,
                Color.FromArgb(0, DaocTheme.Ember), Color.FromArgb(42, DaocTheme.Ember), 90f);
            e.Graphics.FillRectangle(torchlight, ClientRectangle);
        }

        using var outer = new Pen(Color.FromArgb(8, 7, 5), 2f);
        using var bronze = new Pen(DaocTheme.Bronze);
        using var ember = new Pen(Color.FromArgb(150, DaocTheme.Ember));
        e.Graphics.DrawRectangle(outer, 1, 1, Math.Max(0, Width - 3), Math.Max(0, Height - 3));
        e.Graphics.DrawRectangle(bronze, 3, 3, Math.Max(0, Width - 7), Math.Max(0, Height - 7));
        e.Graphics.DrawLine(ember, 6, Height - 6, Width - 7, Height - 6);

        GraphicsState state = e.Graphics.Save();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float uiScale = UiScale.Factor(this);

        // Gold studs on the corners of the bronze frame.
        using var stud = new SolidBrush(DaocTheme.GoldLight);
        float size = 3f * uiScale;
        PointF[] corners = [new(3.5f, 3.5f), new(Width - 3.5f, 3.5f), new(3.5f, Height - 3.5f), new(Width - 3.5f, Height - 3.5f)];

        foreach (PointF corner in corners)
        {
            PointF[] diamond = [new(corner.X - size, corner.Y), new(corner.X, corner.Y - size), new(corner.X + size, corner.Y), new(corner.X, corner.Y + size)];
            e.Graphics.FillPolygon(stud, diamond);
        }

        DrawRoute(e.Graphics, uiScale);
        e.Graphics.Restore(state);
    }

    // The expedition as a route on a map: a compass rose, then a dashed road through the act markers I to IV to the
    // ember-lit V (Darkness Falls). Left out when the space between the title and the buttons is too narrow.
    private void DrawRoute(Graphics graphics, float scale)
    {
        if (_routeStart is null || _routeEnd is null || !_routeStart.IsHandleCreated || !_routeEnd.IsHandleCreated)
        {
            return;
        }

        Rectangle start = RectangleToClient(_routeStart.RectangleToScreen(_routeStart.ClientRectangle));
        Rectangle end = RectangleToClient(_routeEnd.RectangleToScreen(_routeEnd.ClientRectangle));
        float compass = 13f * scale;
        float marker = 8f * scale;
        float left = start.Right + 18f * scale;
        float right = end.Left - 14f * scale;

        if (right - left < 160f * scale)
        {
            return;
        }

        // Compass rose: the four diagonals in bronze first, then the main points, each lit gold on one side.
        var centre = new PointF(left + compass, start.Top + start.Height / 2f);
        using var ring = new Pen(Color.FromArgb(170, DaocTheme.Bronze), Math.Max(1f, scale));
        using var bronze = new SolidBrush(DaocTheme.Bronze);
        using var gold = new SolidBrush(DaocTheme.GoldLight);
        graphics.DrawEllipse(ring, centre.X - compass * 0.7f, centre.Y - compass * 0.7f, compass * 1.4f, compass * 1.4f);

        foreach (int point in new[] { 1, 3, 5, 7, 0, 2, 4, 6 })
        {
            double angle = point * Math.PI / 4;
            float length = point % 2 == 0 ? compass : compass * 0.55f;
            float shoulder = compass * 0.2f;
            var tip = new PointF(centre.X + (float)(Math.Sin(angle) * length), centre.Y - (float)(Math.Cos(angle) * length));
            var leftShoulder = new PointF(centre.X + (float)(Math.Sin(angle - Math.PI / 4) * shoulder),
                centre.Y - (float)(Math.Cos(angle - Math.PI / 4) * shoulder));
            var rightShoulder = new PointF(centre.X + (float)(Math.Sin(angle + Math.PI / 4) * shoulder),
                centre.Y - (float)(Math.Cos(angle + Math.PI / 4) * shoulder));

            if (point % 2 == 1)
            {
                graphics.FillPolygon(bronze, new[] { leftShoulder, tip, rightShoulder, centre });
            }
            else
            {
                graphics.FillPolygon(gold, new[] { centre, leftShoulder, tip });
                graphics.FillPolygon(bronze, new[] { centre, tip, rightShoulder });
            }
        }

        // The road: from the compass through five evenly spaced acts, wandering a little like a path on a map.
        float[] drift = [-4f, 3f, -3f, 4f, 0f];
        float first = centre.X + compass + 3f * scale;
        float step = (right - marker * 1.25f - first) / 5f;
        var road = new PointF[6];
        road[0] = new PointF(first, centre.Y);

        for (int act = 1; act <= 5; act++)
        {
            road[act] = new PointF(first + step * act, centre.Y + drift[act - 1] * scale);
        }

        using (var dashes = new Pen(Color.FromArgb(210, DaocTheme.Parchment), 1.5f * scale) { DashPattern = [3f, 2f] })
        {
            graphics.DrawCurve(dashes, road, 0.5f);
        }

        // Acts I to IV on dark markers ringed in gold; V, Darkness Falls, is an ember marker with a glow.
        string[] acts = ["I", "II", "III", "IV", "V"];
        using var numeralFont = new Font("Georgia", 6.5f * UiScale.TextFactor, FontStyle.Bold);
        using var centred = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };
        using var markerFill = new SolidBrush(Color.FromArgb(235, DaocTheme.Void));
        using var markerRing = new Pen(DaocTheme.Gold, 1.2f * scale);
        using var numeral = new SolidBrush(DaocTheme.GoldLight);
        using var finalFill = new SolidBrush(DaocTheme.Ember);
        using var finalRing = new Pen(DaocTheme.GoldLight, 1.4f * scale);
        using var finalNumeral = new SolidBrush(DaocTheme.Void);
        using var glow = new SolidBrush(Color.FromArgb(70, DaocTheme.Ember));
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        for (int act = 0; act < acts.Length; act++)
        {
            bool darknessFalls = act == acts.Length - 1;
            float radius = darknessFalls ? marker * 1.25f : marker;
            PointF stop = road[act + 1];
            var bounds = new RectangleF(stop.X - radius, stop.Y - radius, radius * 2f, radius * 2f);

            if (darknessFalls)
            {
                graphics.FillEllipse(glow, RectangleF.Inflate(bounds, radius * 0.7f, radius * 0.7f));
            }

            graphics.FillEllipse(darknessFalls ? finalFill : markerFill, bounds);
            graphics.DrawEllipse(darknessFalls ? finalRing : markerRing, bounds);
            graphics.DrawString(acts[act], numeralFont, darknessFalls ? finalNumeral : numeral, bounds, centred);
        }
    }
}

// Inspired by the classic Camelot knot (the game client's effects/camknot.tga): the dragon
// in the triquetra with the Hibernia tree, Midgard hammer and Albion grail, in gold, red and
// the realm colours.
internal sealed class RealmShieldPicture : PictureBox
{
    private static readonly Lazy<Image?> Shield = new(() =>
    {
        using Stream? stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("OfflineDaoc.Launcher.Assets.offline-daoc-realm-emblem.png");
        if (stream == null)
            return null;
        using var loaded = new Bitmap(stream);
        return new Bitmap(loaded);
    });

    public RealmShieldPicture()
    {
        BackColor = Color.Transparent;
        Image = Shield.Value;
        SizeMode = PictureBoxSizeMode.Zoom;
        TabStop = false;
    }
}

internal sealed class DaocTabControl : TabControl
{
    public DaocTabControl()
    {
        DrawMode = TabDrawMode.OwnerDrawFixed;
        SizeMode = TabSizeMode.Fixed;
        ItemSize = new Size(172, 29);
        Padding = new Point(8, 3);
        Font = new Font("Georgia", 9f, FontStyle.Bold);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        var selected = e.Index == SelectedIndex;
        var rectangle = GetTabRect(e.Index);
        rectangle.Inflate(-1, 0);
        using var fill = new LinearGradientBrush(rectangle,
            selected ? Color.FromArgb(79, 65, 42) : Color.FromArgb(47, 43, 36),
            selected ? Color.FromArgb(39, 32, 23) : Color.FromArgb(25, 24, 21), 90f);
        e.Graphics.FillRectangle(fill, rectangle);
        using var border = new Pen(selected ? DaocTheme.Gold : DaocTheme.Iron);
        e.Graphics.DrawRectangle(border, rectangle.X, rectangle.Y, rectangle.Width - 1, rectangle.Height - 1);

        if (selected)
        {
            // The open tab carries a torchlit strip along its top edge.
            using var torch = new SolidBrush(DaocTheme.Ember);
            e.Graphics.FillRectangle(torch, rectangle.X + 1, rectangle.Y + 1, Math.Max(0, rectangle.Width - 2), 2);
        }

        TextRenderer.DrawText(e.Graphics, TabPages[e.Index].Text.ToUpperInvariant(), Font, rectangle,
            selected ? DaocTheme.GoldLight : DaocTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);

        if (message.Msg != 0x000F || !IsHandleCreated || TabCount == 0)
        {
            return;
        }

        using var graphics = Graphics.FromHwnd(Handle);
        var lastTab = GetTabRect(TabCount - 1);
        var headerHeight = Math.Max(lastTab.Bottom + 2, DisplayRectangle.Top);
        using var fill = new SolidBrush(DaocTheme.StoneDark);
        graphics.FillRectangle(fill, lastTab.Right, 0, Math.Max(0, Width - lastTab.Right), headerHeight);
        using var edge = new Pen(DaocTheme.Bronze);
        graphics.DrawLine(edge, lastTab.Right, headerHeight - 1, Width, headerHeight - 1);
    }
}
