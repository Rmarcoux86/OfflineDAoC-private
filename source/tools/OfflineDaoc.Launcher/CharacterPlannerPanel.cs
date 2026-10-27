using System.Globalization;
using System.Runtime.InteropServices;

namespace OfflineDaoc.Launcher;

/// <summary>
/// The Character Planner tab: pick a realm and class, set the level and realm rank, spend spec points on the lines (left),
/// see what the selected line unlocks (middle: spells, styles and abilities, the ones already bought in the normal text
/// colour and the ones still to buy muted) and spend realm ability points (right, passive and active). RESET untrains
/// everything; COPY BUILD puts a one-line summary on the clipboard. The data source is created on a worker the first
/// time the tab is shown, since reading the game database takes a moment.
/// </summary>
internal sealed class CharacterPlannerPanel : Panel
{
    private static readonly string[] RealmOrder = ["Albion", "Midgard", "Hibernia"];

    private readonly Func<ICharacterPlannerSource> _createSource;
    private readonly CharacterPlannerState _state = new();
    private readonly ToolTip _tips = new() { AutomaticDelay = 250, AutoPopDelay = 15_000, ReshowDelay = 100, ShowAlways = true };
    private readonly Font _smallFont = new("Georgia", 7.75f);
    private readonly Font _headingFont = new("Georgia", 7.75f, FontStyle.Bold);
    private readonly Label _message = new()
    {
        Dock = DockStyle.Fill,
        Text = CharacterPlannerFormat.LoadingText,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = DaocTheme.Muted,
        Font = new Font("Georgia", 10f, FontStyle.Italic),
    };
    private readonly Panel _body = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent, Visible = false };
    private readonly ComboBox _realm = Selector(96);
    private readonly ComboBox _class = Selector(140);
    private readonly NumericUpDown _level = Number(1, CharacterPlannerRules.MaxLevel, CharacterPlannerRules.MaxLevel, 48);
    private readonly NumericUpDown _realmRank = Number(1, CharacterPlannerRules.MaxRealmRank, 1, 44);
    private readonly NumericUpDown _realmLevel = Number(0, CharacterPlannerState.MaxRealmLevelWithinRank, 0, 40);
    private readonly Label _specPoints = PointsLabel();
    private readonly Label _realmPoints = PointsLabel();
    private readonly RuneButton _reset = new() { Text = "RESET", Size = new Size(76, 26), Margin = new Padding(10, 2, 0, 2) };
    private readonly RuneButton _copy = new() { Text = "COPY BUILD", Size = new Size(110, 26), Margin = new Padding(4, 2, 0, 2) };
    private readonly Panel _specRows = RowsPanel();
    private readonly Panel _realmRows = RowsPanel();
    private readonly Label _unlockTitle = ColumnTitle("UNLOCKS");
    private readonly RuneButton _spellsSection = SectionButton("SPELLS");
    private readonly RuneButton _stylesSection = SectionButton("STYLES");
    private readonly RuneButton _abilitiesSection = SectionButton("ABILITIES");
    private readonly ListView _unlocks = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        BorderStyle = BorderStyle.None,
        BackColor = DaocTheme.Panel,
        ForeColor = DaocTheme.Text,
        OwnerDraw = true,
        ShowItemToolTips = true,
        Font = new Font("Georgia", 8.25f),
    };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 36,
        Text = CharacterPlannerFormat.HintText,
        ForeColor = DaocTheme.Muted,
        Font = new Font("Georgia", 8.25f, FontStyle.Italic),
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
        Padding = new Padding(4, 2, 4, 0),
    };
    private readonly List<SpecRow> _specRowList = [];
    private readonly List<RealmRow> _realmRowList = [];
    private ICharacterPlannerSource? _source;
    private IReadOnlyList<PlannerSpell> _spells = [];
    private IReadOnlyList<PlannerStyle> _styles = [];
    private IReadOnlyList<PlannerAbility> _abilities = [];
    private UnlockKind _unlockKind = UnlockKind.Spells;
    private string? _selectedSpec;
    private string? _selectedRealmAbility;
    private bool _loadStarted;
    private bool _rendering;

    private enum UnlockKind
    {
        Spells,
        Styles,
        Abilities,
    }

    private sealed record SpecRow(PlannerSpec Spec, TableLayoutPanel Row, Label Name, NumericUpDown Value, Button Lower, Button Raise, Label Cost);

    private sealed record RealmRow(PlannerRealmAbility Ability, TableLayoutPanel Row, Label Name, Label Level, Button Lower, Button Raise, Label Cost);

    // An unlock list row's level; AtCharacterLevel for baseline spells, which compare with the character level instead.
    private sealed record Unlock(int Level, bool AtCharacterLevel);

    /// <param name="createSource">Creates the data source; run once, off the UI thread, the first time the tab is shown. May throw.</param>
    public CharacterPlannerPanel(Func<ICharacterPlannerSource> createSource)
    {
        _createSource = createSource;
        Dock = DockStyle.Fill;
        BackColor = DaocTheme.Panel;
        ForeColor = DaocTheme.Text;
        Padding = new Padding(6, 4, 6, 2);

        // The top bar wraps onto a second line when the window is narrow.
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, 0, 4),
        };
        top.Controls.AddRange([Caption("Realm"), _realm, Caption("Class"), _class, Caption("Level"), _level, Caption("RR"), _realmRank,
            Caption("L"), _realmLevel, _specPoints, _realmPoints, _reset, _copy]);
        _class.DisplayMember = nameof(PlannerClass.Name);

        var columns = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Docked controls are laid out from the last added to the first, so each frame gets its filling part first.
        var specFrame = Frame();
        specFrame.Controls.Add(_specRows);
        specFrame.Controls.Add(ColumnTitle("SPECIALIZATIONS"));

        var sections = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 30,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, 0, 2),
        };
        sections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        sections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        sections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.4f));
        sections.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sections.Controls.Add(_spellsSection, 0, 0);
        sections.Controls.Add(_stylesSection, 1, 0);
        sections.Controls.Add(_abilitiesSection, 2, 0);
        var unlockFrame = Frame();
        unlockFrame.Controls.Add(_unlocks);
        unlockFrame.Controls.Add(sections);
        unlockFrame.Controls.Add(_unlockTitle);

        var realmFrame = Frame();
        realmFrame.Controls.Add(_realmRows);
        realmFrame.Controls.Add(ColumnTitle("REALM ABILITIES"));

        columns.Controls.Add(specFrame, 0, 0);
        columns.Controls.Add(unlockFrame, 1, 0);
        columns.Controls.Add(realmFrame, 2, 0);
        _body.Controls.Add(columns);
        _body.Controls.Add(_status);
        _body.Controls.Add(top);
        Controls.Add(_body);
        Controls.Add(_message);

        _realm.SelectedIndexChanged += (_, _) => RealmChanged();
        _class.SelectedIndexChanged += (_, _) => ClassChanged();
        _level.ValueChanged += (_, _) => LevelOrRankChanged();
        _realmRank.ValueChanged += (_, _) => LevelOrRankChanged();
        _realmLevel.ValueChanged += (_, _) => LevelOrRankChanged();
        _reset.Click += (_, _) =>
        {
            _state.Reset();
            _status.Text = "Every line and realm ability is untrained.";
            Render();
        };
        _copy.Click += (_, _) => CopyBuild();
        _spellsSection.Click += (_, _) => ShowSection(UnlockKind.Spells);
        _stylesSection.Click += (_, _) => ShowSection(UnlockKind.Styles);
        _abilitiesSection.Click += (_, _) => ShowSection(UnlockKind.Abilities);
        _unlocks.Resize += (_, _) => SizeUnlockColumns();
        _unlocks.DrawColumnHeader += (_, e) =>
        {
            using var fill = new SolidBrush(DaocTheme.StoneDark);
            using var edge = new Pen(DaocTheme.StoneLight);
            e.Graphics.FillRectangle(fill, e.Bounds);
            e.Graphics.DrawLine(edge, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty, _unlocks.Font, Rectangle.Inflate(e.Bounds, -4, 0), DaocTheme.GoldLight,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        _unlocks.DrawItem += (_, e) => e.DrawDefault = true;
        _unlocks.DrawSubItem += (_, e) => e.DrawDefault = true;
        _tips.SetToolTip(_copy, "Copies the build as one line, e.g. to paste into a chat or a note.");
        _tips.SetToolTip(_reset, "Untrains every specialization and realm ability (class, level and realm rank stay).");
        _tips.SetToolTip(_realmRank, "Realm rank (1-14).");
        _tips.SetToolTip(_realmLevel, "Realm level within the rank (0-9): RR5L3 is rank 5, level 3.");
        Render();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
            _smallFont.Dispose();
            _headingFont.Dispose();
            (_source as IDisposable)?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        StartLoading();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        StartLoading();
    }

    // Loads the data the first time the panel is on screen (the tab is opened), never while the launcher starts up hidden.
    private void StartLoading()
    {
        if (_loadStarted || !Visible || !IsHandleCreated)
        {
            return;
        }

        _loadStarted = true;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        ICharacterPlannerSource? source = null;
        string? error = null;

        try
        {
            // Classes is read on the worker as well, so a source that loads on first use does that off the UI thread.
            // The await resumes on the UI thread (the WinForms synchronization context), so no Invoke is needed after it.
            source = await Task.Run(() =>
            {
                ICharacterPlannerSource created = _createSource();
                _ = created.Classes.Count;

                return created;
            });
        }
        catch (Exception exception)
        {
            error = $"{CharacterPlannerFormat.EmptyText}\n({exception.GetType().Name}: {exception.Message})";
        }

        if (IsDisposed)
        {
            (source as IDisposable)?.Dispose();

            return;
        }

        // CharacterPlannerData never throws: without a readable database it still lists the classes (with no lines), so
        // its LoadError counts as "not found" too.
        if (source is CharacterPlannerData { LoadError: string loadError })
        {
            error = $"{CharacterPlannerFormat.EmptyText}\n({loadError})";
        }

        if (source is null || source.Classes.Count == 0 || error is not null)
        {
            (source as IDisposable)?.Dispose();
            _message.Text = error ?? CharacterPlannerFormat.EmptyText;

            return;
        }

        _source = source;
        IReadOnlyList<PlannerClass> classes = source.Classes;
        string[] realms = RealmOrder
            .Where(realm => classes.Any(playerClass => string.Equals(playerClass.Realm, realm, StringComparison.OrdinalIgnoreCase)))
            .Concat(classes.Select(playerClass => playerClass.Realm)
                .Where(realm => !RealmOrder.Contains(realm, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            .ToArray();
        _realm.Items.AddRange(realms);
        _message.Visible = false;
        _body.Visible = true;
        _realm.SelectedIndex = 0;
    }

    private void RealmChanged()
    {
        if (_source is null || _realm.SelectedItem is not string realm)
        {
            return;
        }

        object[] classes = _source.Classes
            .Where(playerClass => string.Equals(playerClass.Realm, realm, StringComparison.OrdinalIgnoreCase))
            .OrderBy(playerClass => playerClass.Name, StringComparer.OrdinalIgnoreCase)
            .Cast<object>()
            .ToArray();
        _class.BeginUpdate();
        _class.Items.Clear();
        _class.Items.AddRange(classes);
        _class.EndUpdate();

        if (_class.Items.Count > 0)
        {
            _class.SelectedIndex = 0;
        }
    }

    private void ClassChanged()
    {
        if (_source is null || _class.SelectedItem is not PlannerClass selected)
        {
            return;
        }

        try
        {
            _state.SelectClass(_source, selected);
        }
        catch (Exception exception)
        {
            _status.Text = $"{selected.Name} could not be read: {exception.GetType().Name}: {exception.Message}";

            return;
        }

        _selectedSpec = (_state.Specs.FirstOrDefault(spec => spec.Trainable) ?? _state.Specs.FirstOrDefault())?.KeyName;
        _selectedRealmAbility = null;
        _status.Text = CharacterPlannerFormat.HintText;
        BuildSpecRows();
        BuildRealmRows();
        LoadUnlocks();
        Render();
    }

    private void LevelOrRankChanged()
    {
        if (_rendering)
        {
            return;
        }

        _state.SetLevel((int)_level.Value);
        _state.SetRealmRank((int)_realmRank.Value, (int)_realmLevel.Value);
        Render();
    }

    private void CopyBuild()
    {
        string summary = _state.Summary();

        try
        {
            Clipboard.SetText(summary);
            _status.Text = $"Copied: {summary}";
        }
        catch (ExternalException)
        {
            _status.Text = "The clipboard is busy. Click COPY BUILD again.";
        }
    }

    private void SelectSpec(string specKey)
    {
        if (!string.Equals(specKey, _selectedSpec, StringComparison.OrdinalIgnoreCase))
        {
            _selectedSpec = specKey;
            LoadUnlocks();
        }

        Render();
    }

    private void SelectRealmAbility(string abilityKey)
    {
        _selectedRealmAbility = abilityKey;
        Render();
    }

    private void BuildSpecRows()
    {
        _specRowList.Clear();
        var rows = new List<Control>();

        foreach (PlannerSpec spec in _state.Specs)
        {
            SpecRow row = BuildSpecRow(spec);
            _specRowList.Add(row);
            rows.Add(row.Row);
        }

        if (rows.Count == 0)
        {
            rows.Add(EmptyRow("This class has no specialization lines."));
        }

        ReplaceRows(_specRows, rows);
    }

    private SpecRow BuildSpecRow(PlannerSpec spec)
    {
        string key = spec.KeyName;
        TableLayoutPanel row = Row([22, 44, 22, 50]);
        var name = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty };
        var value = new NumericUpDown
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Minimum = 0,
            Maximum = CharacterPlannerRules.MaxLevel,
            TextAlign = HorizontalAlignment.Center,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = DaocTheme.StoneDark,
            ForeColor = DaocTheme.Text,
            Margin = new Padding(1, 0, 1, 0),
            AccessibleName = spec.Name + " level",
        };
        RuneButton lower = StepButton("−");
        RuneButton raise = StepButton("+");
        var cost = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Font = _smallFont, Margin = Padding.Empty };
        row.Controls.Add(name, 0, 0);
        row.Controls.Add(lower, 1, 0);
        row.Controls.Add(value, 2, 0);
        row.Controls.Add(raise, 3, 0);
        row.Controls.Add(cost, 4, 0);

        row.Click += (_, _) => SelectSpec(key);
        name.Click += (_, _) => SelectSpec(key);
        cost.Click += (_, _) => SelectSpec(key);
        value.Enter += (_, _) => SelectSpec(key);
        lower.Click += (_, _) =>
        {
            _state.LowerSpec(key);
            SelectSpec(key);
        };
        raise.Click += (_, _) =>
        {
            _state.RaiseSpec(key);
            SelectSpec(key);
        };
        value.ValueChanged += (_, _) =>
        {
            if (_rendering)
            {
                return;
            }

            // Clamped to the floor, the character level and the points left; Render shows where it ended.
            _state.SetSpec(key, (int)value.Value);
            SelectSpec(key);
        };

        return new SpecRow(spec, row, name, value, lower, raise, cost);
    }

    private void BuildRealmRows()
    {
        _realmRowList.Clear();
        var rows = new List<Control>();

        foreach (bool passive in new[] { true, false })
        {
            List<PlannerRealmAbility> group = _state.RealmAbilities
                .Where(ability => ability.Passive == passive)
                .OrderBy(ability => ability.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (group.Count == 0)
            {
                continue;
            }

            rows.Add(new Label
            {
                Dock = DockStyle.Top,
                Height = 20,
                Text = passive ? "PASSIVE" : "ACTIVE",
                ForeColor = DaocTheme.Gold,
                Font = _headingFont,
                TextAlign = ContentAlignment.BottomLeft,
                Padding = new Padding(4, 0, 0, 1),
            });

            foreach (PlannerRealmAbility ability in group)
            {
                RealmRow row = BuildRealmRow(ability);
                _realmRowList.Add(row);
                rows.Add(row.Row);
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(EmptyRow("This class has no realm abilities."));
        }

        ReplaceRows(_realmRows, rows);
    }

    private RealmRow BuildRealmRow(PlannerRealmAbility ability)
    {
        string key = ability.KeyName;
        TableLayoutPanel row = Row([34, 22, 22, 46]);
        var name = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty };
        var level = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Margin = Padding.Empty };
        RuneButton lower = StepButton("−");
        RuneButton raise = StepButton("+");
        var cost = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Font = _smallFont, Margin = Padding.Empty };
        row.Controls.Add(name, 0, 0);
        row.Controls.Add(level, 1, 0);
        row.Controls.Add(lower, 2, 0);
        row.Controls.Add(raise, 3, 0);
        row.Controls.Add(cost, 4, 0);

        row.Click += (_, _) => SelectRealmAbility(key);
        name.Click += (_, _) => SelectRealmAbility(key);
        level.Click += (_, _) => SelectRealmAbility(key);
        cost.Click += (_, _) => SelectRealmAbility(key);
        lower.Click += (_, _) =>
        {
            _state.LowerRealmAbility(key);
            SelectRealmAbility(key);
        };
        raise.Click += (_, _) =>
        {
            _state.RaiseRealmAbility(key);
            SelectRealmAbility(key);
        };

        return new RealmRow(ability, row, name, level, lower, raise, cost);
    }

    // Rows are built after the window is shown, so they are scaled here (UiScale) once they are in the panel.
    private static void ReplaceRows(Panel panel, IReadOnlyList<Control> rows)
    {
        panel.SuspendLayout();

        foreach (Control old in panel.Controls.Cast<Control>().ToArray())
        {
            old.Dispose();
        }

        panel.Controls.Clear();

        // Top-docked controls stack from the last added, so the bottom row goes in first.
        for (int index = rows.Count - 1; index >= 0; index--)
        {
            panel.Controls.Add(rows[index]);
            UiScale.ApplyToNewControl(rows[index]);
        }

        panel.ResumeLayout();
        panel.AutoScrollPosition = Point.Empty;
    }

    // Reads what the selected line unlocks; the shown section stays unless it is empty and another is not.
    private void LoadUnlocks()
    {
        _spells = [];
        _styles = [];
        _abilities = [];

        if (_source is not null && _state.Class is not null && _selectedSpec is not null)
        {
            int classId = _state.Class.Id;

            try
            {
                _spells = _source.SpellsFor(classId, _selectedSpec).OrderBy(spell => spell.Level).ThenBy(spell => spell.Name, StringComparer.OrdinalIgnoreCase).ToList();
                _styles = _source.StylesFor(classId, _selectedSpec).OrderBy(style => style.Level).ThenBy(style => style.Name, StringComparer.OrdinalIgnoreCase).ToList();
                _abilities = _source.AbilitiesFor(classId, _selectedSpec).OrderBy(ability => ability.Level).ThenBy(ability => ability.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception exception)
            {
                _status.Text = $"Could not read what this line unlocks: {exception.GetType().Name}: {exception.Message}";
            }
        }

        int shown = _unlockKind switch
        {
            UnlockKind.Spells => _spells.Count,
            UnlockKind.Styles => _styles.Count,
            _ => _abilities.Count,
        };

        if (shown == 0)
        {
            _unlockKind = _spells.Count > 0 ? UnlockKind.Spells
                : _styles.Count > 0 ? UnlockKind.Styles
                : _abilities.Count > 0 ? UnlockKind.Abilities
                : _unlockKind;
        }

        ShowSection(_unlockKind);
    }

    private void ShowSection(UnlockKind kind)
    {
        _unlockKind = kind;
        _unlocks.BeginUpdate();
        _unlocks.Items.Clear();
        _unlocks.Columns.Clear();
        _unlocks.Columns.Add("Lvl");
        _unlocks.Columns.Add("Name");

        if (kind == UnlockKind.Spells)
        {
            _unlocks.Columns.Add("Kind");
            _unlocks.Columns.Add("Description");

            foreach (PlannerSpell spell in _spells)
            {
                bool baseline = CharacterPlannerFormat.UnlocksAtCharacterLevel(spell.LineName);
                var item = new ListViewItem(new[] { spell.Level.ToString(CultureInfo.InvariantCulture), spell.Name, spell.Kind, spell.Description })
                {
                    Tag = new Unlock(spell.Level, baseline),
                    ToolTipText = baseline ? $"{spell.LineName}: unlocks at character level {spell.Level}. {spell.Description}" : spell.Description,
                };
                _unlocks.Items.Add(item);
            }
        }
        else if (kind == UnlockKind.Styles)
        {
            _unlocks.Columns.Add("Opening");
            _unlocks.Columns.Add("Description");

            foreach (PlannerStyle style in _styles)
            {
                var item = new ListViewItem(new[] { style.Level.ToString(CultureInfo.InvariantCulture), style.Name, style.Opening, style.Description })
                {
                    Tag = new Unlock(style.Level, false),
                    ToolTipText = string.IsNullOrWhiteSpace(style.Opening) ? style.Description : $"{style.Opening}. {style.Description}",
                };
                _unlocks.Items.Add(item);
            }
        }
        else
        {
            foreach (PlannerAbility ability in _abilities)
            {
                _unlocks.Items.Add(new ListViewItem(new[] { ability.Level.ToString(CultureInfo.InvariantCulture), ability.Name }) { Tag = new Unlock(ability.Level, false) });
            }
        }

        if (_unlocks.Items.Count == 0)
        {
            _unlocks.Items.Add(new ListViewItem(new[] { string.Empty, _selectedSpec is null ? "Select a specialization." : "Nothing listed for this line." }));
        }

        _unlocks.EndUpdate();
        SizeUnlockColumns();

        void Section(RuneButton button, UnlockKind sectionKind, string label, int count)
        {
            bool active = sectionKind == kind;
            button.Text = $"{label} {count}";
            button.Accent = active ? DaocTheme.GoldLight : DaocTheme.Iron;
            button.ForeColor = active ? DaocTheme.GoldLight : DaocTheme.Muted;
        }

        Section(_spellsSection, UnlockKind.Spells, "SPELLS", _spells.Count);
        Section(_stylesSection, UnlockKind.Styles, "STYLES", _styles.Count);
        Section(_abilitiesSection, UnlockKind.Abilities, "ABILITIES", _abilities.Count);
        RenderUnlocks();
    }

    // Fixed widths for level, name and kind/opening; the last column takes the rest of the list's width.
    private void SizeUnlockColumns()
    {
        int count = _unlocks.Columns.Count;

        if (count == 0)
        {
            return;
        }

        int[] widths = _unlockKind == UnlockKind.Styles ? new[] { 34, 120, 110 } : new[] { 34, 130, 80 };
        int used = 0;

        for (int index = 0; index < count - 1; index++)
        {
            int width = UiScale.Px(_unlocks, widths[Math.Min(index, widths.Length - 1)]);
            _unlocks.Columns[index].Width = width;
            used += width;
        }

        _unlocks.Columns[count - 1].Width = Math.Max(UiScale.Px(_unlocks, 120), _unlocks.ClientSize.Width - used - 1);
    }

    // Unlocked rows in the text colour, the rest muted; the title says what the next point buys.
    private void RenderUnlocks()
    {
        PlannerSpec? spec = _state.Specs.FirstOrDefault(line => string.Equals(line.KeyName, _selectedSpec, StringComparison.OrdinalIgnoreCase));
        int trained = spec is null ? 0 : _state.SpecLevel(spec.KeyName);

        foreach (ListViewItem item in _unlocks.Items)
        {
            item.ForeColor = item.Tag is Unlock unlock
                ? CharacterPlannerFormat.UnlockColor(unlock.Level, unlock.AtCharacterLevel ? _state.Level : trained)
                : DaocTheme.Muted;
        }

        if (spec is null)
        {
            _unlockTitle.Text = "UNLOCKS";

            return;
        }

        // Baseline spells come with the character's level, so only the line's own unlocks count for its next point.
        int? next = _spells.Where(spell => !CharacterPlannerFormat.UnlocksAtCharacterLevel(spell.LineName))
            .Select(spell => spell.Level)
            .Concat(_styles.Select(style => style.Level))
            .Concat(_abilities.Select(ability => ability.Level))
            .Where(level => level > trained)
            .Select(level => (int?)level)
            .Min();
        string progress = next is int nextLevel ? $"next unlock at {nextLevel}" : "everything unlocked";
        _unlockTitle.Text = $"{spec.Name.ToUpperInvariant()} {trained}  ·  {progress}";
    }

    private void Render()
    {
        _rendering = true;

        try
        {
            _level.Value = _state.Level;
            _realmRank.Value = _state.RealmRank;
            _realmLevel.Value = _state.RealmLevel;
            _specPoints.Text = CharacterPlannerFormat.PointsText("Spec points", _state.SpecPointsSpent, _state.SpecPointsTotal);
            _specPoints.ForeColor = CharacterPlannerFormat.PointsColor(_state.SpecPointsSpent, _state.SpecPointsTotal);
            _realmPoints.Text = CharacterPlannerFormat.PointsText("RA points", _state.RealmPointsSpent, _state.RealmPointsTotal);
            _realmPoints.ForeColor = CharacterPlannerFormat.PointsColor(_state.RealmPointsSpent, _state.RealmPointsTotal);
            _reset.Enabled = _state.Class is not null;
            _copy.Enabled = _state.Class is not null;

            foreach (SpecRow row in _specRowList)
            {
                RenderSpecRow(row);
            }

            foreach (RealmRow row in _realmRowList)
            {
                RenderRealmRow(row);
            }

            RenderUnlocks();
            PlannerRealmAbility? selected = _state.RealmAbilities
                .FirstOrDefault(ability => string.Equals(ability.KeyName, _selectedRealmAbility, StringComparison.OrdinalIgnoreCase));

            if (selected is not null)
            {
                _status.Text = RealmAbilityDetails(selected);
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    private void RenderSpecRow(SpecRow row)
    {
        string key = row.Spec.KeyName;
        int level = _state.SpecLevel(key);
        int maximum = _state.SpecMaximum(key);
        int next = _state.SpecRaiseCost(key);
        bool acquired = _state.IsAcquired(key);
        bool selected = string.Equals(key, _selectedSpec, StringComparison.OrdinalIgnoreCase);
        int? floor = _state.AutotrainFloor(key);

        row.Row.BackColor = selected ? DaocTheme.StoneLight : DaocTheme.Panel;
        row.Name.Text = CharacterPlannerFormat.SpecNameText(row.Spec.Name, floor);
        row.Name.ForeColor = !acquired ? DaocTheme.Muted : selected ? DaocTheme.GoldLight : DaocTheme.Text;

        // Minimum to 0 first, so the new maximum and minimum never cross; NumericUpDown pulls Value inside the range itself.
        row.Value.Minimum = 0;
        row.Value.Maximum = maximum;
        row.Value.Minimum = _state.SpecMinimum(key);
        row.Value.Value = Math.Clamp(level, (int)row.Value.Minimum, (int)row.Value.Maximum);
        row.Value.Enabled = row.Spec.Trainable && acquired;
        row.Lower.Enabled = _state.CanLowerSpec(key);
        row.Raise.Enabled = _state.CanRaiseSpec(key);
        row.Cost.Text = CharacterPlannerFormat.SpecCostText(row.Spec.Trainable, acquired, row.Spec.LevelAcquired, level >= maximum, next);
        row.Cost.ForeColor = row.Spec.Trainable && acquired && level < maximum && next > _state.SpecPointsRemaining ? DaocTheme.Danger : DaocTheme.Muted;

        string tip;

        if (!acquired)
        {
            tip = $"{row.Spec.Name} is learned at level {row.Spec.LevelAcquired}.";
        }
        else if (!row.Spec.Trainable)
        {
            tip = $"{row.Spec.Name} follows the character level and costs no points.";
        }
        else
        {
            tip = $"{row.Spec.Name} {level}: {_state.SpecPointsIn(key)} points spent.";
            tip += level < maximum ? $" Level {level + 1} costs {next}." : " At the character level.";
            tip += floor is int free ? $" Autotrained: the first {free} levels are free." : string.Empty;
        }

        _tips.SetToolTip(row.Name, tip);
        _tips.SetToolTip(row.Cost, tip);
    }

    private void RenderRealmRow(RealmRow row)
    {
        string key = row.Ability.KeyName;
        int level = _state.RealmAbilityLevel(key);
        int next = _state.RealmAbilityRaiseCost(key);
        bool met = _state.PrerequisitesMet(key);
        bool selected = string.Equals(key, _selectedRealmAbility, StringComparison.OrdinalIgnoreCase);

        row.Row.BackColor = selected ? DaocTheme.StoneLight : DaocTheme.Panel;
        row.Name.Text = row.Ability.Name;
        row.Name.ForeColor = !met ? DaocTheme.Muted : level > 0 ? DaocTheme.GoldLight : DaocTheme.Text;
        row.Level.Text = CharacterPlannerFormat.RealmAbilityLevelText(level, row.Ability.MaxLevel);
        row.Level.ForeColor = level > 0 ? DaocTheme.GoldLight : DaocTheme.Muted;
        row.Lower.Enabled = _state.CanLowerRealmAbility(key);
        row.Raise.Enabled = _state.CanRaiseRealmAbility(key);
        row.Cost.Text = CharacterPlannerFormat.RealmAbilityCostText(level, row.Ability.MaxLevel, next);
        row.Cost.ForeColor = level < row.Ability.MaxLevel && next > _state.RealmPointsRemaining ? DaocTheme.Danger : DaocTheme.Muted;
        string details = RealmAbilityDetails(row.Ability);
        _tips.SetToolTip(row.Name, details);
        _tips.SetToolTip(row.Cost, details);
    }

    // "Purge (active, 1/1): Removes ... Requires X 2 (not met). Needed by Y."
    private string RealmAbilityDetails(PlannerRealmAbility ability)
    {
        int level = _state.RealmAbilityLevel(ability.KeyName);
        string text = $"{ability.Name} ({(ability.Passive ? "passive" : "active")}, {level}/{ability.MaxLevel}): {ability.Description}";
        string requires = CharacterPlannerFormat.PrerequisiteText(ability.Prerequisites, key =>
            _state.RealmAbilities.FirstOrDefault(other => string.Equals(other.KeyName, key, StringComparison.OrdinalIgnoreCase))?.Name ?? key);

        if (requires.Length > 0)
        {
            text += $"  {requires}{(_state.PrerequisitesMet(ability.KeyName) ? "." : " (not met).")}";
        }

        if (level > 0)
        {
            IReadOnlyList<PlannerRealmAbility> needing = _state.RealmAbilitiesNeeding(ability.KeyName, level - 1);

            if (needing.Count > 0)
            {
                text += $"  Needed by {string.Join(", ", needing.Select(other => other.Name))}.";
            }
        }

        return text;
    }

    private static TableLayoutPanel Row(int[] fixedWidths)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 28,
            ColumnCount = fixedWidths.Length + 1,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(4, 1, 2, 1),
            BackColor = DaocTheme.Panel,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (int width in fixedWidths)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        }

        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        return row;
    }

    private static Label EmptyRow(string text) => new()
    {
        Dock = DockStyle.Top,
        Height = 40,
        Text = text,
        ForeColor = DaocTheme.Muted,
        TextAlign = ContentAlignment.MiddleCenter,
    };

    private static InsetPanel Frame() => new() { Dock = DockStyle.Fill, Accent = DaocTheme.Gold, Padding = new Padding(6, 6, 6, 8), Margin = new Padding(2) };

    private static Label ColumnTitle(string text) => new()
    {
        Dock = DockStyle.Top,
        Height = 22,
        Text = text,
        ForeColor = DaocTheme.Gold,
        Font = new Font("Georgia", 9f, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
    };

    private static Panel RowsPanel() => new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = DaocTheme.Panel };

    private static RuneButton SectionButton(string text) => new() { Text = text, ShowOrnaments = false, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 2, 2) };

    private static RuneButton StepButton(string text) => new() { Text = text, ShowOrnaments = false, Dock = DockStyle.Fill, Margin = new Padding(1, 2, 1, 2), TabStop = false };

    private static Label Caption(string text) => new() { AutoSize = true, Text = text, ForeColor = DaocTheme.Muted, Margin = new Padding(6, 8, 3, 0) };

    private static Label PointsLabel() => new()
    {
        AutoSize = true,
        ForeColor = DaocTheme.GoldLight,
        Font = new Font("Georgia", 8.5f, FontStyle.Bold),
        Margin = new Padding(12, 8, 0, 0),
    };

    private static ComboBox Selector(int width) => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
        BackColor = DaocTheme.StoneDark,
        ForeColor = DaocTheme.Text,
        Width = width,
        Margin = new Padding(0, 4, 4, 0),
    };

    private static NumericUpDown Number(int minimum, int maximum, int value, int width) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Value = value,
        Width = width,
        TextAlign = HorizontalAlignment.Center,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = DaocTheme.StoneDark,
        ForeColor = DaocTheme.Text,
        Margin = new Padding(0, 4, 4, 0),
    };
}
