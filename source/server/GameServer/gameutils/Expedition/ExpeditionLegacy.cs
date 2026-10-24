using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DOL.Database;
using DOL.Database.Attributes;
using DOL.GS.ServerProperties;
using DOL.Logging;

namespace DOL.GS.Expedition;

/// <summary>
/// An account's Legacy: the Renown banked from its finished runs and the permanent perks bought with it. Keyed by the
/// account's login name, so it outlives every hardcore character the account loses.
/// </summary>
[DataTable(TableName = "offline_expedition_legacy")]
public class OfflineExpeditionLegacyRecord : DataObject
{
    private string m_accountName = string.Empty;
    private int m_renown;
    private int m_lifetimeRenown;
    private string m_perks = string.Empty;

    public OfflineExpeditionLegacyRecord()
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

    /// <summary>Renown not spent yet.</summary>
    [DataElement(AllowDbNull = false)]
    public int Renown
    {
        get { return m_renown; }
        set
        {
            Dirty = true;
            m_renown = value;
        }
    }

    /// <summary>All Renown ever banked.</summary>
    [DataElement(AllowDbNull = false)]
    public int LifetimeRenown
    {
        get { return m_lifetimeRenown; }
        set
        {
            Dirty = true;
            m_lifetimeRenown = value;
        }
    }

    /// <summary>Bought perk ranks, "seasoned:2,lucky:1" (<see cref="ExpeditionLegacy.FormatPerks"/>).</summary>
    [DataElement(AllowDbNull = false, Varchar = 255)]
    public string Perks
    {
        get { return m_perks; }
        set
        {
            Dirty = true;
            m_perks = value;
        }
    }
}

public sealed record LegacyPerk(string Id, string Name, string Effect, int[] Costs)
{
    public int MaxRank => Costs.Length;
}

/// <summary>
/// Legacy: a share of the realm points each run earns is banked as Renown on the account when the run ends in death or
/// victory (abandoning banks nothing, since the character and its realm ranks are kept). Renown buys permanent perks
/// that every later run of the account starts with.
/// </summary>
public static class ExpeditionLegacy
{
    public const string Seasoned = "seasoned";
    public const string Renowned = "renowned";
    public const string Lucky = "lucky";
    public const string Outfitted = "outfitted";
    public const string BlessedStart = "blessed";
    public const string Discerning = "discerning";

    /// <summary>Realm points per point of Renown, after the death or victory share.</summary>
    public const int RealmPointsPerRenown = 100;

    /// <summary>Renown for each act boss slain on the run, on top of the realm point share.</summary>
    public const int RenownPerBoss = 10;

    public const int SeasonedXpPercentPerRank = 10;
    public const int RenownedRpPercentPerRank = 10;
    public const int LuckyChancePerRank = 5;

    /// <summary>The catalog, in display order: /legacy buy takes the 1-based position.</summary>
    public static readonly IReadOnlyList<LegacyPerk> Catalog = new List<LegacyPerk>
    {
        new(Seasoned, "Seasoned", $"+{SeasonedXpPercentPerRank}% experience on runs per rank.", new[] { 40, 100, 200 }),
        new(Renowned, "Renowned", $"+{RenownedRpPercentPerRank}% realm points on runs per rank.", new[] { 40, 100, 200 }),
        new(Lucky, "Lucky", $"+{LuckyChancePerRank}% Expedition drop chance per kill per rank.", new[] { 50, 120, 240 }),
        new(Outfitted, "Outfitted", "Start every run with a Rare (or better) item for your class.", new[] { 80 }),
        new(BlessedStart, "Blessed Start", "Start every run with a random combat boon.", new[] { 150 }),
        new(Discerning, "Discerning", "Boon offers show 4 choices instead of 3.", new[] { 200 })
    };

    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>Loaded Legacy rows by account name; the row is the cache (rows are tiny and few).</summary>
    private static readonly ConcurrentDictionary<string, OfflineExpeditionLegacyRecord> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object WriteSync = new();

    public static LegacyPerk Find(string id)
    {
        return Catalog.FirstOrDefault(perk => string.Equals(perk.Id, id, StringComparison.Ordinal));
    }

    #region Pure rules

    /// <summary>The share of the run's realm points banked: a death banks less than a victory, abandoning nothing.</summary>
    public static int SharePercent(string status)
    {
        if (status == ExpeditionManager.StatusWon)
        {
            return Math.Clamp(Properties.EXPEDITION_LEGACY_WIN_SHARE, 0, 100);
        }

        if (status == ExpeditionManager.StatusDead)
        {
            return Math.Clamp(Properties.EXPEDITION_LEGACY_DEATH_SHARE, 0, 100);
        }

        return 0;
    }

    /// <summary>Renown for a run: its realm points times the share, per <see cref="RealmPointsPerRenown"/>, plus the bosses.</summary>
    public static int RenownFor(long realmPoints, int bossesKilled, int sharePercent)
    {
        if (sharePercent <= 0)
        {
            return 0;
        }

        long fromPoints = Math.Max(0, realmPoints) * sharePercent / 100 / RealmPointsPerRenown;

        return (int) Math.Min(int.MaxValue, fromPoints + Math.Max(0, bossesKilled) * RenownPerBoss);
    }

    public static Dictionary<string, int> ParsePerks(string text)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(text))
        {
            return ranks;
        }

        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] pair = part.Split(':');

            if (pair.Length == 2 && Find(pair[0]) is LegacyPerk perk && int.TryParse(pair[1], out int rank) && rank > 0)
            {
                ranks[perk.Id] = Math.Min(rank, perk.MaxRank);
            }
        }

        return ranks;
    }

    public static string FormatPerks(IReadOnlyDictionary<string, int> ranks)
    {
        return string.Join(",", Catalog.Where(perk => ranks.TryGetValue(perk.Id, out int rank) && rank > 0).Select(perk => $"{perk.Id}:{ranks[perk.Id]}"));
    }

    #endregion

    #region Rows

    private static OfflineExpeditionLegacyRecord Get(string accountName)
    {
        // No server (unit tests): no Legacy.
        if (string.IsNullOrEmpty(accountName) || GameServer.Instance == null)
        {
            return null;
        }

        if (Cache.TryGetValue(accountName, out OfflineExpeditionLegacyRecord cached))
        {
            return cached;
        }

        OfflineExpeditionLegacyRecord record = null;

        try
        {
            record = GameServer.Database.FindObjectByKey<OfflineExpeditionLegacyRecord>(accountName);
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] Could not read the Legacy of account {accountName}.", ex);

            return null;
        }

        record ??= new OfflineExpeditionLegacyRecord { AccountName = accountName };

        return Cache.GetOrAdd(accountName, record);
    }

    private static void Save(OfflineExpeditionLegacyRecord record)
    {
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
            Log.Error($"[EXPEDITION] Could not save the Legacy of account {record.AccountName}.", ex);
        }
    }

    private static string AccountOf(GamePlayer player)
    {
        return player?.Client?.Account?.Name;
    }

    public static int Rank(string accountName, string perkId)
    {
        OfflineExpeditionLegacyRecord record = Get(accountName);

        if (record == null)
        {
            return 0;
        }

        lock (WriteSync)
        {
            return ParsePerks(record.Perks).TryGetValue(perkId, out int rank) ? rank : 0;
        }
    }

    public static int Rank(GamePlayer player, string perkId)
    {
        return Rank(AccountOf(player), perkId);
    }

    #endregion

    #region Perk effects

    public static double XpFactor(GamePlayer player)
    {
        return 1.0 + Rank(player, Seasoned) * SeasonedXpPercentPerRank / 100.0;
    }

    public static double RealmPointFactor(GamePlayer player)
    {
        return 1.0 + Rank(player, Renowned) * RenownedRpPercentPerRank / 100.0;
    }

    public static int LootChanceBonus(GamePlayer player)
    {
        return Rank(player, Lucky) * LuckyChancePerRank;
    }

    public static int OfferSize(string accountName)
    {
        return ExpeditionBoons.OfferSize + (Rank(accountName, Discerning) > 0 ? 1 : 0);
    }

    /// <summary>
    /// Blessed Start: a combat (non-loot) boon added to a new run, the same one for the same seed. Called by
    /// <see cref="ExpeditionManager.TryStartRun"/> before the run is first saved; the caller applies boon effects.
    /// </summary>
    public static string GrantStartingBoon(GamePlayer player, ExpeditionRun run)
    {
        if (run == null || Rank(player, BlessedStart) <= 0)
        {
            return null;
        }

        List<BoonDefinition> pool = ExpeditionBoons.Catalog.Where(boon => !boon.IsLoot).ToList();

        if (pool.Count == 0)
        {
            return null;
        }

        BoonDefinition pick = pool[new Random(unchecked(run.Seed * 31 + 7)).Next(pool.Count)];

        lock (run.Boons)
        {
            if (!run.Boons.Contains(pick.Id))
            {
                run.Boons.Add(pick.Id);
            }
        }

        run.AddLog($"Legacy: the run begins with the boon {pick.Name}.");

        return pick.Name;
    }

    /// <summary>Outfitted: one Rare-or-better Expedition item for the player's class. Called once the player has settled in the camp.</summary>
    public static void GrantStartingItem(GamePlayer player)
    {
        if (Rank(player, Outfitted) <= 0 || ExpeditionManager.GetRun(player) == null)
        {
            return;
        }

        ExpeditionLoot.DropFor(player, null, eLootRarity.Rare);
    }

    #endregion

    #region Banking and buying

    /// <summary>
    /// Banks the run's Renown on its account when it ends in death or victory. Once per run: the run's
    /// <see cref="ExpeditionRun.RenownBanked"/> marks it. The caller saves the run.
    /// </summary>
    public static int BankRun(ExpeditionRun run)
    {
        if (run == null || run.RenownBanked > 0 || string.IsNullOrEmpty(run.AccountName))
        {
            return 0;
        }

        int renown = RenownFor(run.RealmPointsEarned, run.BossesKilled, SharePercent(run.Status));

        if (renown <= 0)
        {
            return 0;
        }

        OfflineExpeditionLegacyRecord record = Get(run.AccountName);

        if (record == null)
        {
            return 0;
        }

        lock (WriteSync)
        {
            record.Renown += renown;
            record.LifetimeRenown += renown;
            Save(record);
        }

        run.RenownBanked = renown;
        run.AddLog($"Legacy: {renown} Renown banked (total {record.Renown}).");

        return renown;
    }

    /// <summary>Buys the next rank of perk number <paramref name="choice"/> (1-based, catalog order).</summary>
    public static bool TryBuy(GamePlayer player, int choice, out string message)
    {
        string accountName = AccountOf(player);
        OfflineExpeditionLegacyRecord record = Get(accountName);

        if (record == null)
        {
            message = "Your Legacy could not be read.";

            return false;
        }

        if (choice < 1 || choice > Catalog.Count)
        {
            message = $"Choose a perk from 1 to {Catalog.Count}. Type /legacy to see them.";

            return false;
        }

        LegacyPerk perk = Catalog[choice - 1];

        lock (WriteSync)
        {
            Dictionary<string, int> ranks = ParsePerks(record.Perks);
            int rank = ranks.TryGetValue(perk.Id, out int current) ? current : 0;

            if (rank >= perk.MaxRank)
            {
                message = $"{perk.Name} is already at its highest rank.";

                return false;
            }

            int cost = perk.Costs[rank];

            if (record.Renown < cost)
            {
                message = $"{perk.Name} rank {rank + 1} costs {cost} Renown; you have {record.Renown}.";

                return false;
            }

            record.Renown -= cost;
            ranks[perk.Id] = rank + 1;
            record.Perks = FormatPerks(ranks);
            Save(record);

            message = perk.MaxRank > 1
                ? $"Legacy perk {perk.Name} is now rank {rank + 1} of {perk.MaxRank}. {record.Renown} Renown left."
                : $"Legacy perk {perk.Name} unlocked. {record.Renown} Renown left.";
        }

        return true;
    }

    /// <summary>The /legacy window.</summary>
    public static List<string> DescribeLines(GamePlayer player)
    {
        OfflineExpeditionLegacyRecord record = Get(AccountOf(player));
        var lines = new List<string>();

        if (record == null)
        {
            lines.Add("Your Legacy could not be read.");

            return lines;
        }

        Dictionary<string, int> ranks;

        lock (WriteSync)
        {
            ranks = ParsePerks(record.Perks);
            lines.Add($"Renown: {record.Renown}   (lifetime {record.LifetimeRenown})");
        }

        lines.Add(string.Empty);
        lines.Add($"A run that ends in death banks {SharePercent(ExpeditionManager.StatusDead)}% of its realm points, a won run {SharePercent(ExpeditionManager.StatusWon)}%,");
        lines.Add($"at 1 Renown per {RealmPointsPerRenown} realm points, plus {RenownPerBoss} Renown per act boss slain. Abandoning banks nothing.");
        lines.Add("Perks are permanent and apply to every run on this account.");
        lines.Add(string.Empty);

        for (int i = 0; i < Catalog.Count; i++)
        {
            LegacyPerk perk = Catalog[i];
            int rank = ranks.TryGetValue(perk.Id, out int current) ? current : 0;
            string next = rank >= perk.MaxRank ? "maxed" : $"next rank {perk.Costs[rank]} Renown";
            string rankText = perk.MaxRank > 1 ? $"rank {rank}/{perk.MaxRank}" : rank > 0 ? "owned" : "not owned";

            lines.Add($"{i + 1}. {perk.Name} ({rankText}, {next}): {perk.Effect}");
        }

        lines.Add(string.Empty);
        lines.Add("Type /legacy buy <number> to buy a perk or its next rank.");

        return lines;
    }

    #endregion
}
