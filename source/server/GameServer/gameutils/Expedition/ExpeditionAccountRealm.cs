using System;
using System.Collections.Concurrent;
using System.Reflection;
using DOL.Database;
using DOL.Database.Attributes;
using DOL.GS.PacketHandler;
using DOL.Logging;

namespace DOL.GS.Expedition;

/// <summary>
/// The realm points an account has earned on Expedition runs. Keyed by the account's login name, so they outlive every
/// hardcore character the account loses.
/// </summary>
[DataTable(TableName = "offline_expedition_account")]
public class OfflineExpeditionAccountRecord : DataObject
{
    private string m_accountName = string.Empty;
    private long m_realmPoints;

    public OfflineExpeditionAccountRecord()
    {
        AllowAdd = true;
        AllowDelete = true;
    }

    [PrimaryKey]
    public string AccountName
    {
        get { return m_accountName; }
        set
        {
            Dirty = true;
            m_accountName = value;
        }
    }

    [DataElement(AllowDbNull = false)]
    public long RealmPoints
    {
        get { return m_realmPoints; }
        set
        {
            Dirty = true;
            m_realmPoints = value;
        }
    }
}

/// <summary>
/// Account realm points: every realm point a run earns is added to the account at once, and a new run's character
/// starts with the account's total, so realm ranks and realm abilities are never lost to a hardcore death.
/// </summary>
public static class ExpeditionAccountRealm
{
    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    private static readonly ConcurrentDictionary<string, OfflineExpeditionAccountRecord> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object WriteSync = new();

    private static OfflineExpeditionAccountRecord Get(string accountName)
    {
        // No server (unit tests): no account store.
        if (string.IsNullOrEmpty(accountName) || GameServer.Instance == null)
        {
            return null;
        }

        if (Cache.TryGetValue(accountName, out OfflineExpeditionAccountRecord cached))
        {
            return cached;
        }

        OfflineExpeditionAccountRecord record;

        try
        {
            record = GameServer.Database.FindObjectByKey<OfflineExpeditionAccountRecord>(accountName);
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] Could not read the realm points of account {accountName}.", ex);

            return null;
        }

        record ??= new OfflineExpeditionAccountRecord { AccountName = accountName };

        return Cache.GetOrAdd(accountName, record);
    }

    public static long Total(string accountName)
    {
        OfflineExpeditionAccountRecord record = Get(accountName);

        if (record == null)
        {
            return 0;
        }

        lock (WriteSync)
        {
            return record.RealmPoints;
        }
    }

    /// <summary>Adds realm points a run earned to its account and saves the row.</summary>
    public static void Add(string accountName, long points)
    {
        OfflineExpeditionAccountRecord record = points > 0 ? Get(accountName) : null;

        if (record == null)
        {
            return;
        }

        lock (WriteSync)
        {
            record.RealmPoints += points;

            try
            {
                if (record.IsPersisted)
                {
                    GameServer.Database.SaveObject(record);
                }
                else
                {
                    GameServer.Database.AddObject(record);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[EXPEDITION] Could not save the realm points of account {accountName}.", ex);
            }
        }
    }

    /// <summary>
    /// A new run's character (no realm points of its own; TryStartRun checks) is given the account's total. Not
    /// counted as earned by the run, so it is not added to the account again.
    /// </summary>
    public static void GiveToNewRun(GamePlayer player, ExpeditionRun run)
    {
        long total = Total(run?.AccountName);

        if (total <= 0 || player.RealmPoints > 0)
        {
            return;
        }

        player.GainRealmPoints(total, false, false);
        run.AddLog($"The account's {total} realm points carry over to {player.Name}.");
        player.Out.SendMessage($"Your account's {total} realm points carry over to this character. Spend realm ability points with /train.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
    }
}
