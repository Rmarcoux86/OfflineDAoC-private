using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using DOL.Database;
using DOL.Events;
using DOL.GS.ServerProperties;
using DOL.Logging;

namespace DOL.GS;

/// <summary>
/// Offline fork: classes the Classic + SI setup blocks (disabled_classes) but the owner wants playable (2026-10-24: the
/// Heretic). At every server start, while allow_heretic is on, the Heretic is taken out of disabled_classes, both in the
/// saved setting and in memory, so character creation and trainers offer it. Turning allow_heretic off leaves the list
/// as it is; put 33 back in disabled_classes to block it again.
/// </summary>
public static class ForkClassUnlocks
{
    private const string DisabledClassesKey = "disabled_classes";

    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        if (!Properties.ALLOW_HERETIC)
        {
            return;
        }

        try
        {
            Unblock((int) eCharacterClass.Heretic);
        }
        catch (Exception ex)
        {
            Log.Error("[FORK] The Heretic could not be unblocked.", ex);
        }
    }

    private static void Unblock(int classId)
    {
        string current = Properties.DISABLED_CLASSES ?? string.Empty;
        string updated = RemoveClassId(current, classId);

        if (updated == current)
        {
            return;
        }

        if (Properties.AllDomainProperties.TryGetValue(DisabledClassesKey, out var entry))
        {
            DbServerProperty row = entry.Item3;

            if (row.IsPersisted)
            {
                row.Value = updated;
                GameServer.Database.SaveObject(row);
            }

            entry.Item2.SetValue(null, updated);
        }
        else
        {
            Properties.DISABLED_CLASSES = updated;
        }

        Log.Info($"[FORK] Class {classId} unblocked: disabled_classes is now \"{updated}\" (was \"{current}\").");
    }

    /// <summary>
    /// The disabled-classes list without <paramref name="classId"/>: "20;33;34;39;58-62" without 33 is
    /// "20;34;39;58-62"; a range around it is split ("30-35" without 33 is "30-32;34-35"). Separators ";" or ",".
    /// </summary>
    public static string RemoveClassId(string list, int classId)
    {
        if (string.IsNullOrWhiteSpace(list))
        {
            return list ?? string.Empty;
        }

        var kept = new List<string>();
        bool removed = false;

        foreach (string raw in list.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] range = raw.Split('-');

            if (range.Length == 2 && int.TryParse(range[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int low)
                && int.TryParse(range[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int high) && low <= classId && classId <= high)
            {
                removed = true;
                AddRange(kept, low, classId - 1);
                AddRange(kept, classId + 1, high);

                continue;
            }

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int single) && single == classId)
            {
                removed = true;

                continue;
            }

            kept.Add(raw);
        }

        return removed ? string.Join(";", kept) : list;
    }

    private static void AddRange(List<string> kept, int low, int high)
    {
        if (low > high)
        {
            return;
        }

        kept.Add(low == high ? low.ToString(CultureInfo.InvariantCulture) : $"{low}-{high}");
    }
}
