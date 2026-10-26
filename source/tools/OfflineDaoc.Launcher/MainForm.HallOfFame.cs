using System.Data.SQLite;

namespace OfflineDaoc.Launcher;

// The Hall of Fame on the front page (owner 2026-10-26): beside the tabs, not in one.
internal sealed partial class MainForm
{
    private HallOfFamePanel? _hallOfFame;

    // Row 2 of the main layout: the tabs, and the Hall of Fame to their right (300 px at 96 DPI, scaled with the form).
    private Control BuildTabsAndHallOfFame()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _hallOfFame = new HallOfFamePanel(_root, _logsDirectory, ReadLocalExpeditionWins);
        row.Controls.Add(BuildTabs(), 0, 0);
        row.Controls.Add(_hallOfFame, 1, 0);

        return row;
    }

    /// <summary>
    /// This PC's won expeditions (offline_expeditions, Status 'won') with the character's class, race, realm and level.
    /// A win whose character no longer exists is left out (its class and realm are unknown). Columns added after the
    /// first expedition build are read only when the database has them.
    /// </summary>
    private IReadOnlyList<HallOfFame.Entry> ReadLocalExpeditionWins()
    {
        var wins = new List<HallOfFame.Entry>();

        if (!File.Exists(_database))
        {
            return wins;
        }

        using var connection = new SQLiteConnection($"Data Source={_database};Version=3;Read Only=True;Pooling=False;Default Timeout=5");
        connection.Open();

        if (!TableExists(connection, "offline_expeditions") || !TableExists(connection, "DOLCharacters"))
        {
            return wins;
        }

        string realmPoints = ColumnExists(connection, "offline_expeditions", "RealmPointsEarned") ? "COALESCE(e.RealmPointsEarned, 0)" : "0";
        string playedSeconds = ColumnExists(connection, "offline_expeditions", "PlayedSecondsAtWin") ? "COALESCE(e.PlayedSecondsAtWin, 0)" : "0";
        string gauntlet = ColumnExists(connection, "offline_expeditions", "GauntletJson") ? "COALESCE(e.GauntletJson, '')" : "''";
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT e.CharacterId, e.CharacterName, e.Seed, e.Kills, e.BossesKilled, e.LootUnique, e.EndedUtc,
                   {realmPoints}, {playedSeconds}, {gauntlet}, c.Class, c.Race, c.Realm, c.Level
            FROM offline_expeditions e
            JOIN DOLCharacters c ON c.DOLCharacters_ID = e.CharacterId
            WHERE LOWER(e.Status) = 'won' AND COALESCE(e.EndedUtc, '') <> ''
            """;
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            string characterId = Convert.ToString(reader.GetValue(0)) ?? string.Empty;
            string endedUtc = Convert.ToString(reader.GetValue(6)) ?? string.Empty;
            int classId = Convert.ToInt32(reader.GetValue(10));
            int realm = Convert.ToInt32(reader.GetValue(12));
            // ClassInfo has the classic classes only; 63 is the custom Hibernian Sluaghbinder (0.37b).
            (string classRealm, string className) = classId == 63 ? ("Hibernia", "Sluaghbinder") : ClassInfo(classId);
            string realmName = realm is >= 1 and <= 3 ? RealmName(realm) : classRealm;

            wins.Add(new HallOfFame.Entry(
                HallOfFame.EntryId(characterId, endedUtc),
                Convert.ToString(reader.GetValue(1)) ?? string.Empty,
                className,
                HallOfFame.RaceName(Convert.ToInt32(reader.GetValue(11))),
                realmName,
                Convert.ToInt32(reader.GetValue(13)),
                Convert.ToInt32(reader.GetValue(2)),
                Convert.ToInt64(reader.GetValue(8)),
                endedUtc,
                Convert.ToInt32(reader.GetValue(3)),
                Convert.ToInt32(reader.GetValue(4)),
                Convert.ToInt32(reader.GetValue(5)),
                Convert.ToInt64(reader.GetValue(7)),
                HallOfFame.GauntletCleared(Convert.ToString(reader.GetValue(9))),
                null,
                null));
        }

        return wins;
    }
}
