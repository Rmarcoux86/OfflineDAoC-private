using System.Diagnostics;

namespace OfflineDaoc.Launcher;

/// <summary>
/// The Hall of Fame beside the tabs on the front page (owner 2026-10-26: "should be on the front and not buried in a
/// tab"): the shared wins of every player plus this PC's wins not listed yet, newest first, with SHARE MY WIN, SUBMIT AS
/// SPECTATOR (a Gauntlet-cleared character's look, shown as a peaceful spectator at the Gauntlet arena) and a Refresh
/// link. Downloads never block the window; offline it shows the last saved list.
/// </summary>
internal sealed class HallOfFamePanel : Panel
{
    private readonly string _runtimeFolder;
    private readonly string _logsDirectory;
    private readonly Func<IReadOnlyList<HallOfFame.Entry>> _readLocalWins;
    private readonly Func<string, GauntletSpectator.Snapshot?> _readSpectator;
    private readonly HallOfFameView _view = new();
    private readonly Button _shareButton = new RuneButton { Text = "SHARE MY WIN", Accent = DaocTheme.Gold, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 4, 4) };
    private readonly LinkLabel _refreshLink = new()
    {
        Text = "Refresh",
        AutoSize = true,
        Anchor = AnchorStyles.Right,
        Font = new Font("Georgia", 8.5f),
        LinkColor = DaocTheme.GoldLight,
        ActiveLinkColor = Color.White,
        VisitedLinkColor = DaocTheme.GoldLight,
        LinkBehavior = LinkBehavior.AlwaysUnderline,
        BackColor = Color.Transparent,
        Margin = new Padding(4, 0, 2, 0),
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill,
        ForeColor = DaocTheme.Muted,
        Font = new Font("Georgia", 7.5f, FontStyle.Italic),
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
    };
    private readonly ContextMenuStrip _picker = new();
    private IReadOnlyList<HallOfFame.Entry> _shared = [];
    private IReadOnlyList<HallOfFame.Entry> _local = [];
    private bool _downloading;

    /// <param name="runtimeFolder">The server's runtime folder (cache, sent ids, repository override).</param>
    /// <param name="readLocalWins">Reads this PC's won expeditions; run off the UI thread, may throw.</param>
    /// <param name="readSpectator">Reads a character's spectator snapshot by character id (null when it is gone); run off the UI thread, may throw.</param>
    public HallOfFamePanel(string runtimeFolder, string logsDirectory, Func<IReadOnlyList<HallOfFame.Entry>> readLocalWins,
        Func<string, GauntletSpectator.Snapshot?> readSpectator)
    {
        _runtimeFolder = runtimeFolder;
        _logsDirectory = logsDirectory;
        _readLocalWins = readLocalWins;
        _readSpectator = readSpectator;
        Dock = DockStyle.Fill;
        BackColor = Color.Transparent;
        Margin = new Padding(4, 0, 0, 0);
        Padding = Padding.Empty;

        var frame = new InsetPanel { Dock = DockStyle.Fill, Accent = DaocTheme.Gold, Padding = new Padding(10, 6, 6, 8) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = Color.Transparent };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "HALL OF FAME",
            ForeColor = DaocTheme.Gold,
            Font = new Font("Georgia", 11f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
        }, 0, 0);
        header.Controls.Add(_refreshLink, 1, 0);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(_view, 0, 1);
        layout.Controls.Add(_status, 0, 2);
        layout.Controls.Add(_shareButton, 0, 3);
        frame.Controls.Add(layout);
        Controls.Add(frame);

        _shareButton.Enabled = false;
        _shareButton.Click += (_, _) => ShareClicked();
        _refreshLink.LinkClicked += async (_, _) => await RefreshAsync();
        _view.HandleCreated += (_, _) => Render();
        _status.Text = "Loading the shared list...";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _picker.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Re-reads this PC's wins and downloads the shared list (the saved copy shows meanwhile). Never throws.</summary>
    public async Task RefreshAsync()
    {
        if (_downloading)
        {
            return;
        }

        _downloading = true;
        _refreshLink.Enabled = false;

        try
        {
            if (_shared.Count == 0)
            {
                _shared = await Task.Run(() => HallOfFame.ReadCache(_runtimeFolder)) ?? [];
            }

            await RefreshLocalAsync();
            _status.Text = "Updating from GitHub...";
            string repository = ForkUpdater.SourceRepositoryFor(_runtimeFolder);
            IReadOnlyList<HallOfFame.Entry>? fetched = await HallOfFame.FetchAsync(repository, _runtimeFolder, _logsDirectory);

            if (fetched is not null)
            {
                _shared = fetched;
                _status.Text = $"Shared list updated {DateTime.Now:t}.";
            }
            else
            {
                _status.Text = _shared.Count > 0 ? "Offline: showing the last saved list." : "Could not reach GitHub. Try Refresh later.";
            }

            Render();
        }
        finally
        {
            _downloading = false;
            _refreshLink.Enabled = true;
        }
    }

    /// <summary>Re-reads this PC's wins only (no network). Never throws.</summary>
    public async Task RefreshLocalAsync()
    {
        try
        {
            _local = await Task.Run(_readLocalWins);
        }
        catch (Exception exception)
        {
            HallOfFame.Log(_logsDirectory, $"Hall of Fame: could not read this PC's wins: {exception.GetType().Name}: {exception.Message}");
        }

        Render();
    }

    private void Render()
    {
        HashSet<string> sent = HallOfFame.ReadSentIds(_runtimeFolder);
        _shareButton.Enabled = HallOfFame.ToShare(_local, _shared, sent, GauntletSpectator.ReadSubmittedIds(_runtimeFolder)).Count > 0;

        if (_view.IsHandleCreated)
        {
            _view.ShowRows(HallOfFame.Merge(_shared, _local, sent));
        }
    }

    // One unshared win is shared at once; several open a small picker (newest first).
    private void ShareClicked()
    {
        IReadOnlyList<HallOfFame.Entry> unshared = HallOfFame.ToShare(_local, _shared, HallOfFame.ReadSentIds(_runtimeFolder),
            GauntletSpectator.ReadSubmittedIds(_runtimeFolder));

        if (unshared.Count == 0)
        {
            return;
        }

        if (unshared.Count == 1)
        {
            _ = ShareAsync(unshared[0]);

            return;
        }

        _picker.Items.Clear();

        foreach (HallOfFame.Entry entry in unshared)
        {
            var row = new HallOfFame.Row(entry, HallOfFame.RowState.Local);
            _picker.Items.Add($"{entry.Character}, {HallOfFame.Summary(entry)} ({HallOfFame.Details(row)})", null, (_, _) => _ = ShareAsync(entry));
        }

        _picker.Show(_shareButton, new Point(0, _shareButton.Height));
    }

    /// <summary>
    /// Opens the pre-filled Hall of Fame issue for the win (owner 2026-10-27: one button): it carries the character's
    /// look too, so the workflow also adds it to the Gauntlet's spectators. The look is read from the database off the
    /// UI thread; without it, or when the link would be too long, the win goes alone. Never throws.
    /// </summary>
    private async Task ShareAsync(HallOfFame.Entry entry)
    {
        string characterId = GauntletSpectator.CharacterIdOf(entry);
        string repository = ForkUpdater.SourceRepositoryFor(_runtimeFolder);
        GauntletSpectator.Snapshot? snapshot = null;
        _shareButton.Enabled = false;

        try
        {
            snapshot = await Task.Run(() => _readSpectator(characterId));
        }
        catch (Exception exception)
        {
            HallOfFame.Log(_logsDirectory, $"Share: could not read {entry.Character}'s look: {exception.GetType().Name}: {exception.Message}");
        }

        string? spectatorJson = snapshot is null ? null : GauntletSpectator.SubmissionJson(snapshot);
        string url = HallOfFame.IssueUrlWithSpectator(repository, entry, spectatorJson);

        if (spectatorJson is not null && url.Length > GauntletSpectator.MaxIssueUrlLength)
        {
            HallOfFame.Log(_logsDirectory, $"Share: the link with {entry.Character}'s look is {url.Length} characters; the win is sent alone.");
            spectatorJson = null;
            url = HallOfFame.IssueUrlWithSpectator(repository, entry, null);
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception exception)
        {
            HallOfFame.Log(_logsDirectory, $"Hall of Fame: could not open the browser: {exception.GetType().Name}: {exception.Message}");
            Render();
            MessageBox.Show(this, "The browser could not be opened. Check the launcher's logs\\hall-of-fame.log.", "Share my win",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);

            return;
        }

        HallOfFame.RememberSent(_runtimeFolder, entry.Id, _logsDirectory);

        if (spectatorJson is not null)
        {
            GauntletSpectator.RememberSubmitted(_runtimeFolder, characterId, _logsDirectory);
        }

        _status.Text = spectatorJson is null
            ? "Submit the issue in your browser; the win is listed within a few minutes."
            : $"Submit the issue in your browser; the win is listed within minutes and {entry.Character} joins the Gauntlet's spectators.";
        Render();
    }
}

/// <summary>
/// The Hall of Fame list, formatted like the build notes (ForkNotesView): each win its character name in gold (and a
/// Gauntlet star), "level N Race Class, Realm", then who, played time, seed and date.
/// </summary>
internal sealed class HallOfFameView : RichTextBox
{
    private Font? _nameFont;
    private Font? _detailFont;

    public HallOfFameView()
    {
        Dock = DockStyle.Fill;
        ReadOnly = true;
        BorderStyle = BorderStyle.None;
        BackColor = DaocTheme.Panel;
        ForeColor = DaocTheme.Text;
        Font = new Font("Georgia", 9f);
        DetectUrls = false;
        ScrollBars = RichTextBoxScrollBars.Vertical;
        WordWrap = true;
        TabStop = false;
        Margin = Padding.Empty;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _nameFont?.Dispose();
            _detailFont?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Writes the rows (or the empty text). Needs the control's handle.</summary>
    public void ShowRows(IReadOnlyList<HallOfFame.Row> rows)
    {
        // Derived from the already scaled font, so UiScale's text size applies to the names too.
        Font body = Font;
        _nameFont ??= new Font(body.FontFamily, body.Size * 1.15f, FontStyle.Bold);
        _detailFont ??= new Font(body.FontFamily, body.Size * 0.9f, FontStyle.Italic);
        Clear();

        if (rows.Count == 0)
        {
            Append(HallOfFame.EmptyText, body, DaocTheme.Muted);

            return;
        }

        for (int index = 0; index < rows.Count; index++)
        {
            HallOfFame.Row row = rows[index];

            if (index > 0)
            {
                Append("\n", body, DaocTheme.Text);
            }

            Append(row.Entry.Character, _nameFont, DaocTheme.GoldLight);

            if (row.Entry.GauntletCleared)
            {
                Append("  ★ Gauntlet", _nameFont, DaocTheme.Gold);
            }

            Append("\n" + HallOfFame.Summary(row.Entry) + "\n", body, DaocTheme.Text);
            Append(HallOfFame.Details(row) + "\n", _detailFont, row.State == HallOfFame.RowState.Shared ? DaocTheme.Muted : DaocTheme.Success);

            if (HallOfFame.BoonsLine(row.Entry) is string boons)
            {
                Append(boons + "\n", _detailFont, DaocTheme.Muted);
            }
        }

        SelectionStart = 0;
        SelectionLength = 0;
        ScrollToCaret();
    }

    private void Append(string text, Font font, Color color)
    {
        SelectionStart = TextLength;
        SelectionLength = 0;
        SelectionFont = font;
        SelectionColor = color;
        AppendText(text);
    }
}
