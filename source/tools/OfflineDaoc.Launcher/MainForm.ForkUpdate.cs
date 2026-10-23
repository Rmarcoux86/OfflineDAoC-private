using System.Diagnostics;

namespace OfflineDaoc.Launcher;

// The fork's self-update: while a newer fork release exists and the server is stopped, START SERVER becomes GET UPDATE.
internal sealed partial class MainForm
{
    private static readonly Color StartServerAccent = Color.FromArgb(110, 128, 70);
    private static readonly Color GetUpdateAccent = Color.FromArgb(214, 162, 52);

    // No timer of its own (the launcher keeps to its three timers): the one-second display clock checks this deadline.
    private DateTime _nextForkUpdateCheckUtc = DateTime.MaxValue;
    private ForkUpdater.ForkUpdateOffer? _forkUpdate;
    private string? _forkUpdateSkippedTag;
    private string _lastServerState = string.Empty;
    private bool _forkUpdateChecking;
    private bool _forkUpdateBusy;
    private bool _startButtonOffersUpdate;

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
            string installed = _forkUpdate!.InstalledTag ?? "an unversioned server";
            tip = $"{_forkUpdate.Release.Tag} available (you have {installed}). Downloads and installs it, then restarts the launcher.";
        }

        _helpTip.SetToolTip(_startButton, tip);
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
