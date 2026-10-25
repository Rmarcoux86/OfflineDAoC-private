using System.Diagnostics;

namespace OfflineDaoc.Launcher;

// The fork's self-update: while a newer fork release exists and the server is stopped, START SERVER becomes GET UPDATE.
internal sealed partial class MainForm
{
    private static readonly Color StartServerAccent = Color.FromArgb(110, 128, 70);
    private static readonly Color GetUpdateAccent = Color.FromArgb(214, 162, 52);
    // The GET UPDATE tooltip lists at most this many "What's new" items; the What's new link shows them all.
    private const int UpdateTipNoteLimit = 5;

    // No timer of its own (the launcher keeps to its three timers): the one-second display clock checks this deadline.
    private DateTime _nextForkUpdateCheckUtc = DateTime.MaxValue;
    private ForkUpdater.ForkUpdateOffer? _forkUpdate;
    private string? _forkUpdateSkippedTag;
    private string _lastServerState = string.Empty;
    private bool _forkUpdateChecking;
    private bool _forkUpdateBusy;
    private bool _startButtonOffersUpdate;
    // Under the header buttons: the offered update's notes (only while it has some) and the installed build history.
    private readonly LinkLabel _whatsNewLink = NotesLink("What's new");

    // The playable folder: the parent of the server's runtime folder (in new-client mode that is ..\..\runtime).
    private string GameFolder => Path.GetFullPath(Path.Combine(_root, ".."));

    private void StartServerOrGetUpdate()
    {
        if (_startButtonOffersUpdate)
        {
            _ = GetUpdateAsync();

            return;
        }

        StartServer();
    }

    private static LinkLabel NotesLink(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Georgia", 9f),
        LinkColor = DaocTheme.GoldLight,
        ActiveLinkColor = Color.White,
        VisitedLinkColor = DaocTheme.GoldLight,
        LinkBehavior = LinkBehavior.AlwaysUnderline,
        BackColor = Color.Transparent,
        Margin = Padding.Empty,
    };

    // Beside the version label: "What's new" while an update with notes is offered (the history is the Build History tab).
    private Control BuildForkNotesLinks(Point location)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Location = location,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        _whatsNewLink.Visible = false;
        _whatsNewLink.LinkClicked += (_, _) => ShowOfferedUpdateNotes();
        row.Controls.Add(_whatsNewLink);

        return row;
    }

    private void ShowOfferedUpdateNotes()
    {
        if (_forkUpdate is not { } offer || offer.Release.Notes.Count == 0)
        {
            return;
        }

        var build = new ForkUpdater.ForkBuild(offer.Release.Tag, null, offer.Release.Notes);
        string intro = $"Fork update {offer.Release.Tag} is available. Click GET UPDATE while the server is stopped to install it.";
        using var notes = new ForkNotesForm($"What's new in {offer.Release.Tag}", intro, [build], ForkUpdater.NoNotesText, offer.InstalledTag);
        notes.ShowDialog(this);
    }

    // The first tab (owner 2026-10-25): every fork build and what it changed, from runtime\build-history.json (installed
    // with every update; no network). Written again each time the tab is opened.
    private TabPage BuildHistoryTab()
    {
        var page = new TabPage("Build History") { BackColor = DaocTheme.Panel, ForeColor = DaocTheme.Text };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "FORK BUILDS, NEWEST FIRST  ·  what each update changed",
            ForeColor = DaocTheme.GoldLight,
            Font = new Font("Georgia", 9.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        var frame = new InsetPanel { Dock = DockStyle.Fill, Accent = DaocTheme.Gold, Padding = new Padding(14, 10, 4, 8) };
        var view = new ForkNotesView();
        frame.Controls.Add(view);
        layout.Controls.Add(frame, 0, 1);
        page.Controls.Add(layout);

        void Render()
        {
            view.ShowNotes(ForkUpdater.ReadBuildHistory(_root), "No build history is available. It comes with the next fork update.",
                ForkUpdater.ReadInstalledVersion(_root));
        }

        view.HandleCreated += (_, _) => Render();
        page.Enter += (_, _) => Render();

        return page;
    }

    // The first start on a newly installed fork build shows once what it changed (the tag is remembered in the runtime folder).
    private void ShowUpdatedNoticeOnce()
    {
        string? installed = ForkUpdater.ReadInstalledVersion(_root);
        ForkUpdater.UpdatedNotice notice = ForkUpdater.DecideUpdatedNotice(installed, ForkUpdater.ReadNoticeTag(_root));

        if (notice == ForkUpdater.UpdatedNotice.None)
        {
            return;
        }

        // An unwritable record would show the notice on every start: then it is not shown at all.
        if (!ForkUpdater.WriteNoticeTag(_root, installed!, _logsDirectory) || notice == ForkUpdater.UpdatedNotice.RecordOnly)
        {
            return;
        }

        ForkUpdater.ForkBuild build = ForkUpdater.ReadBuildHistory(_root)?
            .FirstOrDefault(entry => string.Equals(entry.Tag, installed, StringComparison.OrdinalIgnoreCase))
            ?? new ForkUpdater.ForkBuild(installed!, null, []);
        using var notes = new ForkNotesForm($"Updated to {installed}", $"Offline DAoC was updated to {installed}. What's new in this build:",
            [build], ForkUpdater.NoNotesText, installed);
        notes.ShowDialog(this);
    }

    private void StartForkUpdateChecks()
    {
        _displayClock.Tick += async (_, _) =>
        {
            if (DateTime.UtcNow < _nextForkUpdateCheckUtc)
            {
                return;
            }

            await CheckForForkUpdateAsync();
        };

        _ = CheckForForkUpdateAsync();
        ShowUpdatedNoticeOnce();
    }

    private async Task CheckForForkUpdateAsync()
    {
        if (_forkUpdateChecking || _forkUpdateBusy)
        {
            return;
        }

        _forkUpdateChecking = true;
        _nextForkUpdateCheckUtc = DateTime.UtcNow.AddMilliseconds(ForkUpdater.CheckIntervalMilliseconds);

        try
        {
            ForkUpdater.ForkUpdateOffer? offer = await ForkUpdater.CheckAsync(_root, _logsDirectory);

            if (IsDisposed)
            {
                return;
            }

            if (offer is not null && string.Equals(offer.Release.Tag, _forkUpdateSkippedTag, StringComparison.OrdinalIgnoreCase))
            {
                offer = null;
            }

            bool isNew = offer is not null && !string.Equals(offer.Release.Tag, _forkUpdate?.Release.Tag, StringComparison.OrdinalIgnoreCase);
            _forkUpdate = offer;
            ApplyStartButtonMode();

            if (isNew)
            {
                _footer.Text = $"Fork update {offer!.Release.Tag} is available. Click GET UPDATE while the server is stopped.";
            }
        }
        finally
        {
            _forkUpdateChecking = false;
        }
    }

    // Called after every dashboard refresh and update check; START SERVER is exactly as before when there is no update.
    private void ApplyStartButtonMode()
    {
        // While downloading, the button shows the progress and stays disabled.
        if (_forkUpdateBusy)
        {
            return;
        }

        bool offerUpdate = _forkUpdate is not null && !_stoppingServer && _lastServerState == "Stopped";
        _startButtonOffersUpdate = offerUpdate;
        _startButton.Text = offerUpdate ? "GET UPDATE" : "START SERVER";
        ((RuneButton)_startButton).Accent = offerUpdate ? GetUpdateAccent : StartServerAccent;

        string? tip = null;

        if (offerUpdate)
        {
            tip = UpdateTip(_forkUpdate!.Release.Tag, _forkUpdate.InstalledTag, _forkUpdate.Release.Notes);
        }

        _helpTip.SetToolTip(_startButton, tip);

        // Shown with any offered update that has notes, also while the server runs (GET UPDATE then waits for a stop).
        bool hasNotes = _forkUpdate is { Release.Notes.Count: > 0 };
        _whatsNewLink.Visible = hasNotes;
        _helpTip.SetToolTip(_whatsNewLink, hasNotes ? $"What {_forkUpdate!.Release.Tag} changes." : null);
    }

    /// <summary>The GET UPDATE tooltip: the offer, then up to <see cref="UpdateTipNoteLimit"/> of its "What's new" items.</summary>
    internal static string UpdateTip(string latestTag, string? installedTag, IReadOnlyList<string> notes)
    {
        string tip = $"{latestTag} available (you have {installedTag ?? "an unversioned server"}). Downloads and installs it, then restarts the launcher.";

        if (notes.Count == 0)
        {
            return tip;
        }

        tip += Environment.NewLine + Environment.NewLine + "What's new:";

        foreach (string note in notes.Take(UpdateTipNoteLimit))
        {
            tip += Environment.NewLine + "• " + (note.Length > 100 ? note[..99] + "…" : note);
        }

        if (notes.Count > UpdateTipNoteLimit)
        {
            tip += Environment.NewLine + $"…and {notes.Count - UpdateTipNoteLimit} more (click What's new).";
        }

        return tip;
    }

    private async Task GetUpdateAsync()
    {
        if (_forkUpdate is not { } offer || _forkUpdateBusy)
        {
            return;
        }

        using (Process? running = FindExactServerProcess())
        {
            if (_serverProcess is { HasExited: false } || IsServerRunning() || running is not null)
            {
                MessageBox.Show(this, "Stop the server before updating.", "Server is running", MessageBoxButtons.OK, MessageBoxIcon.Information);

                return;
            }
        }

        if (_botGoalsSettings?.HasUnsavedChanges == true)
        {
            MessageBox.Show(this, "Save or undo your edits in Bot Goals Setting before updating.", "Unsaved bot goals");

            return;
        }

        _forkUpdateBusy = true;
        _startButtonOffersUpdate = false;
        _startButton.Enabled = false;
        _startButton.Text = "DOWNLOADING…";
        _footer.Text = $"Downloading fork update {offer.Release.Tag}…";

        try
        {
            string folder = await ForkUpdater.DownloadAsync(offer.Release, percent => _startButton.Text = $"DOWNLOADING {percent}%");
            _startButton.Text = "INSTALLING…";
            _footer.Text = "Installing the update. The launcher closes now and reopens when the update is installed.";
            ForkUpdater.StartInstaller(folder, GameFolder, _newClient);
            ForkUpdater.Log(_logsDirectory, $"Started the installer for {offer.Release.Tag} from {folder}.");
            Application.Exit();
        }
        catch (Exception exception)
        {
            ForkUpdater.Log(_logsDirectory, $"Update {offer.Release.Tag} failed: {exception.GetType().Name}: {exception.Message}");

            // Don't offer the same release again this session, so the server can still be started.
            _forkUpdateSkippedTag = offer.Release.Tag;
            _forkUpdate = null;
            _forkUpdateBusy = false;
            ApplyStartButtonMode();
            _startButton.Enabled = !_resettingKeepsRelics && !_savingXpRates && !_stoppingServer && _lastServerState == "Stopped" && File.Exists(_serverExecutable);
            _footer.Text = _dashboardFooterSummary;
            MessageBox.Show(this,
                $"The update could not be installed:{Environment.NewLine}{exception.Message}{Environment.NewLine}{Environment.NewLine}" +
                "Nothing was changed. START SERVER plays the version you have; the update is offered again the next time the launcher starts.",
                "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
