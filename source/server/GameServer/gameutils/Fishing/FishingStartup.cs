using System;
using DOL.Database;
using DOL.Events;
using DOL.Logging;

namespace DOL.GS.Fishing;

/// <summary>
/// Adds the fishing item templates (fish, junk and the pole) to the database when the server starts.
/// Existing rows are left alone, so edits made in the database are kept.
/// </summary>
public static class FishingStartup
{
    private static readonly Logger Log = LoggerManager.Create(typeof(FishingStartup));

    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        int added = 0;
        int failed = 0;

        foreach (DbItemTemplate template in FishingCatalog.AllTemplates())
        {
            DbItemTemplate existing = GameServer.Database.FindObjectByKey<DbItemTemplate>(template.Id_nb);

            if (existing != null)
            {
                // A template with our id but from another source (a stock quest item, say) would be handed out
                // in place of ours, so say so in the log rather than silently using it.
                if (existing.PackageID != FishingCatalog.PackageId)
                {
                    Log.Warn($"Fishing: item template '{template.Id_nb}' already exists from package '{existing.PackageID}' and is used as-is.");
                }

                continue;
            }

            if (GameServer.Database.AddObject(template))
            {
                added++;
            }
            else
            {
                failed++;
                Log.Error($"Fishing: could not add item template '{template.Id_nb}'.");
            }
        }

        if (Log.IsInfoEnabled)
        {
            Log.Info($"Fishing: added {added} item template(s) to the database ({failed} failed).");
        }
    }
}
