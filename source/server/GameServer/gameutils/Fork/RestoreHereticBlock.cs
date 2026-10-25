using System;
using System.Reflection;
using DOL.Database;
using DOL.Events;
using DOL.GS.ServerProperties;
using DOL.Logging;

namespace DOL.GS;

/// <summary>
/// fork-v38 took the Heretic (33) out of disabled_classes at server start; it was reverted (owner, 2026-10-24: the
/// Heretic could not be created). When disabled_classes still holds exactly what v38 left from the setup default, the
/// setup default is put back, so the Heretic is blocked again at creation, trainers and Expedition promotion. Any other
/// value was set by hand and is kept.
/// </summary>
public static class RestoreHereticBlock
{
    public const string SetupDefault = "20;33;34;39;58-62";
    public const string LeftByV38 = "20;34;39;58-62";

    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        try
        {
            if (!string.Equals(Properties.DISABLED_CLASSES, LeftByV38, StringComparison.Ordinal))
            {
                return;
            }

            if (Properties.AllDomainProperties.TryGetValue("disabled_classes", out var entry))
            {
                DbServerProperty row = entry.Item3;

                if (row.IsPersisted)
                {
                    row.Value = SetupDefault;
                    GameServer.Database.SaveObject(row);
                }

                entry.Item2.SetValue(null, SetupDefault);
            }
            else
            {
                Properties.DISABLED_CLASSES = SetupDefault;
            }

            Log.Info($"[FORK] disabled_classes restored to \"{SetupDefault}\" (the Heretic is blocked again).");
        }
        catch (Exception ex)
        {
            Log.Error("[FORK] disabled_classes could not be restored.", ex);
        }
    }
}
