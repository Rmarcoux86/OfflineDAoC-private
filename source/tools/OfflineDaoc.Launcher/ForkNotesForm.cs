using System.Globalization;

namespace OfflineDaoc.Launcher;

/// <summary>
/// Read-only, formatted fork release notes: the build history (newest first), the notes of an offered update or the
/// one-time "Updated to" notice. Plain RichTextBox formatting; no browser control and no network.
/// </summary>
internal sealed class ForkNotesForm : Form
{
    private readonly IReadOnlyList<ForkUpdater.ForkBuild>? _builds;
    private readonly string _missingText;
    private readonly string? _installedTag;
    private readonly RichTextBox _notes = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BorderStyle = BorderStyle.None,
        BackColor = DaocTheme.Panel,
        ForeColor = DaocTheme.Text,
        Font = new Font("Georgia", 10f),
        DetectUrls = false,
        ScrollBars = RichTextBoxScrollBars.Vertical,
        WordWrap = true,
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        ForeColor = DaocTheme.Muted,
        Font = new Font("Georgia", 8.5f, FontStyle.Italic),
        TextAlign = ContentAlignment.MiddleLeft,
    };
    private Font? _headingFont;
    private Font? _detailFont;

    /// <param name="builds">The builds to show, newest first; null when there are none to read.</param>
    /// <param name="missingText">Shown instead of the notes when <paramref name="builds"/> is null or empty.</param>
    public ForkNotesForm(string title, string intro, IReadOnlyList<ForkUpdater.ForkBuild>? builds, string missingText, string? installedTag)
    {
        _builds = builds;
        _missingText = missingText;
        _installedTag = installedTag;
        Text = title;
        Size = new Size(640, 560);
        MinimumSize = new Size(420, 320);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        BackColor = DaocTheme.Panel;
        ForeColor = DaocTheme.GoldLight;
        Font = new Font("Georgia", 9f);
        // Built in 96-DPI pixels and scaled once by UiScale (display scaling and text size).
        AutoScaleMode = AutoScaleMode.None;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = intro,
            ForeColor = DaocTheme.GoldLight,
            Font = new Font("Georgia", 9.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        var frame = new InsetPanel { Dock = DockStyle.Fill, Accent = DaocTheme.Gold, Padding = new Padding(14, 10, 4, 8) };
        frame.Controls.Add(_notes);
        layout.Controls.Add(frame, 0, 1);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var close = new RuneButton { Text = "CLOSE", Accent = DaocTheme.Gold, Anchor = AnchorStyles.Right, Margin = new Padding(4, 6, 2, 4) };
        close.Click += (_, _) => Close();
        bottom.Controls.Add(_status, 0, 0);
        bottom.Controls.Add(close, 1, 0);
        layout.Controls.Add(bottom, 0, 2);
        Controls.Add(layout);

        // Esc closes; the notes keep no caret focus. The text is written once the window (and its handle) exists.
        CancelButton = close;
        ActiveControl = close;
        Shown += (_, _) => ShowNotes();
        UiScale.Apply(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _headingFont?.Dispose();
            _detailFont?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ShowNotes()
    {
        if (_builds is null || _builds.Count == 0)
        {
            _notes.Clear();
            Append(_missingText, _notes.Font, DaocTheme.Muted, false);

            return;
        }

        _status.Text = _builds.Count == 1 ? string.Empty : $"{_builds.Count} builds, newest first.";
        ShowBuilds(_builds);
    }

    // Each build: a bold gold tag (and "(installed)"), its date, then its "What's new" items as bullets.
    private void ShowBuilds(IReadOnlyList<ForkUpdater.ForkBuild> builds)
    {
        // Derived from the already scaled notes font, so UiScale's text size applies to the headings too.
        Font body = _notes.Font;
        _headingFont ??= new Font(body.FontFamily, body.Size * 1.2f, FontStyle.Bold);
        _detailFont ??= new Font(body.FontFamily, body.Size * 0.85f, FontStyle.Italic);
        _notes.BulletIndent = UiScale.Px(_notes, 14);
        _notes.Clear();

        for (int index = 0; index < builds.Count; index++)
        {
            ForkUpdater.ForkBuild build = builds[index];

            if (index > 0)
            {
                Append("\n", body, DaocTheme.Text, false);
            }

            Append(build.Tag, _headingFont, DaocTheme.GoldLight, false);

            if (string.Equals(build.Tag, _installedTag, StringComparison.OrdinalIgnoreCase))
            {
                Append("  (installed)", _headingFont, DaocTheme.Success, false);
            }

            Append("\n", body, DaocTheme.Text, false);

            // A calendar day (the release's UTC day), written in the player's long date format.
            if (build.Date is { } date)
            {
                Append(date.ToString("D", CultureInfo.CurrentCulture) + "\n", _detailFont, DaocTheme.Muted, false);
            }

            if (build.Notes.Count == 0)
            {
                Append(ForkUpdater.NoNotesText + "\n", _detailFont, DaocTheme.Muted, false);

                continue;
            }

            foreach (string note in build.Notes)
            {
                Append(note + "\n", body, DaocTheme.Text, true);
            }
        }

        // The empty paragraph after the last item would otherwise keep its bullet.
        Append(string.Empty, body, DaocTheme.Text, false);
        _notes.SelectionStart = 0;
        _notes.SelectionLength = 0;
        _notes.ScrollToCaret();
    }

    // Appends at the end with this font, colour and bullet (the bullet applies to the paragraph the text starts).
    private void Append(string text, Font font, Color color, bool bullet)
    {
        _notes.SelectionStart = _notes.TextLength;
        _notes.SelectionLength = 0;
        _notes.SelectionFont = font;
        _notes.SelectionColor = color;
        _notes.SelectionBullet = bullet;
        _notes.AppendText(text);
    }
}
