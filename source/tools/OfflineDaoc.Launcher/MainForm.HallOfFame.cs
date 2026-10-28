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
        _hallOfFame = new HallOfFamePanel(_root, _logsDirectory, ReadLocalExpeditionWins, ReadSpectatorSnapshot);
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
        string boons = ColumnExists(connection, "offline_expeditions", "BoonsJson") ? "COALESCE(e.BoonsJson, '')" : "''";
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT e.CharacterId, e.CharacterName, e.Seed, e.Kills, e.BossesKilled, e.LootUnique, e.EndedUtc,
                   {realmPoints}, {playedSeconds}, {gauntlet}, c.Class, c.Race, c.Realm, c.Level, {boons}
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
                null,
                HallOfFame.BoonNamesFromJson(Convert.ToString(reader.GetValue(14)))));
        }

        return wins;
    }

    /// <summary>
    /// A Gauntlet spectator's look (owner 2026-10-26): the character's DOLCharacters row and the models, colors, glows
    /// and extensions of its worn visible items (Inventory with ItemUnique for uniques, else ItemTemplate). Null when
    /// the character is not in the database.
    /// </summary>
    private GauntletSpectator.Snapshot? ReadSpectatorSnapshot(string characterId)
    {
        if (!File.Exists(_database))
        {
            return null;
        }

        using var connection = new SQLiteConnection($"Data Source={_database};Version=3;Read Only=True;Pooling=False;Default Timeout=5");
        connection.Open();

        if (!TableExists(connection, "DOLCharacters"))
        {
            return null;
        }

        // Booleans may come back as a bool or as 0/1.
        static bool Flag(object value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) is "1" or "True" or "true";
        string hoodUp = ColumnExists(connection, "DOLCharacters", "IsCloakHoodUp") ? "COALESCE(IsCloakHoodUp, 0)" : "0";
        string helmHidden = ColumnExists(connection, "DOLCharacters", "IsHelmInvisible") ? "COALESCE(IsHelmInvisible, 0)" : "0";
        string cloakHidden = ColumnExists(connection, "DOLCharacters", "IsCloakInvisible") ? "COALESCE(IsCloakInvisible, 0)" : "0";
        string name;
        int classId, race, gender, realm, currentModel, creationModel, activeWeaponSlot;
        bool isHoodUp, isHelmHidden, isCloakHidden;

        using (var character = connection.CreateCommand())
        {
            character.CommandText = $"""
                SELECT Name, Class, Race, Gender, Realm, CurrentModel, CreationModel, {hoodUp}, {helmHidden}, {cloakHidden}, ActiveWeaponSlot
                FROM DOLCharacters
                WHERE DOLCharacters_ID = @id
                """;
            character.Parameters.AddWithValue("@id", characterId);
            using var reader = character.ExecuteReader();

            if (!reader.Read())
            {
                return null;
            }

            name = Convert.ToString(reader.GetValue(0)) ?? string.Empty;
            classId = Convert.ToInt32(reader.GetValue(1));
            race = Convert.ToInt32(reader.GetValue(2));
            gender = Convert.ToInt32(reader.GetValue(3));
            realm = Convert.ToInt32(reader.GetValue(4));
            currentModel = Convert.ToInt32(reader.GetValue(5));
            creationModel = Convert.ToInt32(reader.GetValue(6));
            isHoodUp = Flag(reader.GetValue(7));
            isHelmHidden = Flag(reader.GetValue(8));
            isCloakHidden = Flag(reader.GetValue(9));
            activeWeaponSlot = Convert.ToInt32(reader.GetValue(10));
        }

        var worn = new List<GauntletSpectator.Item>();

        if (TableExists(connection, "Inventory") && TableExists(connection, "ItemTemplate") && TableExists(connection, "ItemUnique"))
        {
            using var items = connection.CreateCommand();

            // An item uses its ItemUnique row when UTemplate_Id is set (DbInventoryItem.Template), else its ItemTemplate row.
            items.CommandText = $"""
                SELECT i.SlotPosition,
                       CASE WHEN COALESCE(i.UTemplate_Id, '') <> '' THEN COALESCE(u.Model, 0) ELSE COALESCE(t.Model, 0) END,
                       COALESCE(i.Color, 0),
                       CASE WHEN COALESCE(i.UTemplate_Id, '') <> '' THEN COALESCE(u.Effect, 0) ELSE COALESCE(t.Effect, 0) END,
                       COALESCE(i.Extension, 0)
                FROM Inventory i
                LEFT JOIN ItemTemplate t ON t.Id_nb = i.ITemplate_Id
                LEFT JOIN ItemUnique u ON u.Id_nb = i.UTemplate_Id
                WHERE i.OwnerID = @id AND i.SlotPosition IN ({string.Join(", ", GauntletSpectator.VisibleSlots)})
                """;
            items.Parameters.AddWithValue("@id", characterId);
            using var reader = items.ExecuteReader();

            while (reader.Read())
            {
                worn.Add(new GauntletSpectator.Item(
                    Convert.ToInt32(reader.GetValue(0)),
                    Convert.ToInt32(reader.GetValue(1)),
                    Convert.ToInt32(reader.GetValue(2)),
                    Convert.ToInt32(reader.GetValue(3)),
                    Convert.ToInt32(reader.GetValue(4))));
            }
        }

        return GauntletSpectator.Build(characterId, name, classId, race, gender, realm, currentModel, creationModel, isHoodUp, isHelmHidden,
            isCloakHidden, activeWeaponSlot, worn);
    }
}
