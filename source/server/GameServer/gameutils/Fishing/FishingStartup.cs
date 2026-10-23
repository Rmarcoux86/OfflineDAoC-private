using System;
using DOL.Database;
using DOL.Events;
using DOL.Logging;

namespace DOL.GS.Fishing;

/// <summary>
/// Adds the fishing item templates (fish, junk and the pole) to the database when the server starts.
/// Existing rows are left alone, so edits made in the database are kept, except the model of our own rows:
/// a model changed in FishingCatalog (the pole's look, say) is copied onto the existing row.
/// </summary>
public static class FishingStartup
{
    private static readonly Logger Log = LoggerManager.Create(typeof(FishingStartup));

    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        int added = 0;
        int updated = 0;
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

                    continue;
                }

                // The client draws an item (hand mesh and bag icon) from its model alone, and inventory items read
                // the model through their template, so updating the cached row changes every existing pole too.
                if (existing.Model != template.Model)
                {
                    int oldModel = existing.Model;

                    // DbItemTemplate ignores Dirty unless AllowUpdate is set, and SaveObject skips clean rows while
                    // still returning true, so without it the new model would never reach the database.
                    existing.AllowUpdate = true;
                    existing.Model = template.Model;
                    bool saved = GameServer.Database.SaveObject(existing);
                    existing.AllowUpdate = false;

                    if (saved)
                    {
                        updated++;
                        Log.Info($"Fishing: item template '{template.Id_nb}' model changed from {oldModel} to {template.Model}.");
                    }
                    else
                    {
                        failed++;
                        Log.Error($"Fishing: could not save the new model of item template '{template.Id_nb}'.");
                    }
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
            Log.Info($"Fishing: added {added} and updated {updated} item template(s) in the database ({failed} failed).");
        }
    }
}
