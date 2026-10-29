using System;
using System.Collections.Generic;
using DOL.Database;
using DOL.Database.Attributes;

namespace DOL.GS.Expedition;

/// <summary>
/// One character's Expedition run, saved in its own table. Keyed by the character's id and carrying its name, so the
/// record outlives the character: a run that ends in death deletes the character (hardcore), the record stays.
/// Times are ISO 8601 UTC strings ("o"); the SQLite layer has no nullable DateTime column, and EndedUtc is empty
/// while the run is going.
/// </summary>
[DataTable(TableName = "offline_expeditions")]
public class OfflineExpeditionRecord : DataObject
{
    private string m_characterId = string.Empty;
    private string m_characterName = string.Empty;
    private string m_accountName = string.Empty;
    private bool m_summaryShown;
    private int m_seed;
    private int m_act;
    private string m_status = string.Empty;
    private int m_kills;
    private int m_deaths;
    private int m_bossesKilled;
    private int m_lootCommon;
    private int m_lootMagic;
    private int m_lootRare;
    private int m_lootEpic;
    private int m_lootUnique;
    private string m_startedUtc = string.Empty;
    private string m_endedUtc = string.Empty;
    private string m_actJson = string.Empty;
    private string m_logJson = string.Empty;
    private long m_playedSecondsAtWin;

    public OfflineExpeditionRecord()
    {
        AllowAdd = true;
        AllowDelete = true;
    }

    [PrimaryKey]
    public string CharacterId
    {
        get { return m_characterId; }
        set
        {
            Dirty = true;
            m_characterId = value;
        }
    }

    [DataElement(AllowDbNull = false, Varchar = 64)]
    public string CharacterName
    {
        get { return m_characterName; }
        set
        {
            Dirty = true;
            m_characterName = value;
        }
    }

    /// <summary>The account's login name (DbAccount.Name), so a later login on any character can find the run.</summary>
    [DataElement(AllowDbNull = false, Varchar = 64, Index = true)]
    public string AccountName
    {
        get { return m_accountName; }
        set
        {
            Dirty = true;
            m_accountName = value;
        }
    }

    /// <summary>
    /// True once the end-of-run window has been seen. A death's window is sent just before the hardcore kick, so it
    /// is shown again at the account's next login (<see cref="ExpeditionManager"/>).
    /// </summary>
    [DataElement(AllowDbNull = false)]
    public bool SummaryShown
    {
        get { return m_summaryShown; }
        set
        {
            Dirty = true;
            m_summaryShown = value;
        }
    }

    [DataElement(AllowDbNull = false)]
    public int Seed
    {
        get { return m_seed; }
        set
        {
            Dirty = true;
            m_seed = value;
        }
    }

    [DataElement(AllowDbNull = false)]
    public int Act
    {
        get { return m_act; }
        set
        {
            Dirty = true;
            m_act = value;
        }
    }

    /// <summary>"active", "won", "dead" or "abandoned".</summary>
    [DataElement(AllowDbNull = false, Varchar = 16, Index = true)]
    public string Status
    {
        get { return m_status; }
        set
        {
            Dirty = true;
            m_status = value;
        }
    }

    [DataElement(AllowDbNull = false)]
    public int Kills
    {
        get { return m_kills; }
        set
        {
            Dirty = true;
            m_kills = value;
        }
    }

    [DataElement(AllowDbNull = false)]
    public int Deaths
    {
        get { return m_deaths; }
        set
        {
            Dirty = true;
            m_deaths = value;
        }
    }

    [DataElement(AllowDbNull = false)]
    public int BossesKilled
    {
        get { return m_bossesKilled; }
        set
        {
            Dirty = true;
            m_bossesKilled = value;
        }
    }

    /// <summary>Expedition loot found on the run, per rarity (<see cref="ExpeditionLoot"/>).</summary>
    [DataElement(AllowDbNull = false)]
    public int LootCommon
    {
        get { return m_lootCommon; }
        set
        {
            Dirty = true;
            m_lootCommon = value;
        }
    }

    [DataElement(AllowDbNull = false)]
    public int LootMagic
    {
        get { return m_lootMagic; }
        set
        {
            Dirty = true;
            m_lootMagic = value;
        }
    }

    [DataElement(AllowDbNull = false)]
    public int LootRare
    {
        get { return m_lootRare; }
        set
        {
            Dirty = true;
            m_lootRare = value;
        }
    }

    [DataElement(AllowDbNull = false)]
    public int LootEpic
    {
        get { return m_lootEpic; }
        set
        {
            Dirty = true;
            m_lootEpic = value;
        }
    }

    [DataElement(AllowDbNull = false)]
    public int LootUnique
    {
        get { return m_lootUnique; }
        set
        {
            Dirty = true;
            m_lootUnique = value;
        }
    }

    [DataElement(AllowDbNull = false, Varchar = 40)]
    public string StartedUtc
    {
        get { return m_startedUtc; }
        set
        {
            Dirty = true;
            m_startedUtc = value;
        }
    }

    /// <summary>Empty while the run is going.</summary>
    [DataElement(AllowDbNull = true, Varchar = 40)]
    public string EndedUtc
    {
        get { return m_endedUtc; }
        set
        {
            Dirty = true;
            m_endedUtc = value;
        }
    }

    /// <summary>The current <see cref="ActPlan"/> as JSON (Varchar = 0 makes a TEXT column).</summary>
    [DataElement(AllowDbNull = true)]
    public string ActJson
    {
        get { return m_actJson; }
        set
        {
            Dirty = true;
            m_actJson = value;
        }
    }

    /// <summary>The journal lines as a JSON string array (TEXT column).</summary>
    [DataElement(AllowDbNull = true)]
    public string LogJson
    {
        get { return m_logJson; }
        set
        {
            Dirty = true;
            m_logJson = value;
        }
    }

    private string m_boonsJson;
    private string m_pendingBoonOfferJson;
    private long m_realmPointsEarned;
    private int m_rerollsUsed;

    /// <summary>Boon offer rerolls used on this run (<see cref="ExpeditionBoons.RerollsPerRun"/>).</summary>
    [DataElement(AllowDbNull = false)]
    public int RerollsUsed
    {
        get { return m_rerollsUsed; }
        set
        {
            Dirty = true;
            m_rerollsUsed = value;
        }
    }

    /// <summary>Realm points the run has earned (<see cref="ExpeditionRealm"/>).</summary>
    [DataElement(AllowDbNull = false)]
    public long RealmPointsEarned
    {
        get { return m_realmPointsEarned; }
        set
        {
            Dirty = true;
            m_realmPointsEarned = value;
        }
    }

    /// <summary>
    /// The character's played time (seconds) when it won the expedition; 0 until then. The launcher's Hall of Fame
    /// shows it (owner 2026-10-25), since the character can be played on after its win.
    /// </summary>
    [DataElement(AllowDbNull = false)]
    public long PlayedSecondsAtWin
    {
        get { return m_playedSecondsAtWin; }
        set
        {
            Dirty = true;
            m_playedSecondsAtWin = value;
        }
    }

    private bool m_hard;

    /// <summary>True for a hard-mode run (ExpeditionHardMode, owner 2026-10-29): unlocked by beating the Gauntlet.</summary>
    [DataElement(AllowDbNull = false)]
    public bool Hard
    {
        get { return m_hard; }
        set
        {
            Dirty = true;
            m_hard = value;
        }
    }

    private string m_inheritedUnique;

    /// <summary>
    /// A hard run's unique taken from the account's Gauntlet winner with /run inherit (ExpeditionHardMode); empty until
    /// one is taken, and on every other run.
    /// </summary>
    [DataElement(AllowDbNull = true, Varchar = 64)]
    public string InheritedUnique
    {
        get { return m_inheritedUnique; }
        set
        {
            Dirty = true;
            m_inheritedUnique = value;
        }
    }

    private string m_gauntletJson;

    /// <summary>
    /// The Gauntlet after a won run (Gauntlet.GauntletSave as JSON: round and depth); empty until the character first
    /// enters it (TEXT column). Only GauntletManager writes it; ExpeditionManager.Save leaves it as loaded.
    /// </summary>
    [DataElement(AllowDbNull = true)]
    public string GauntletJson
    {
        get { return m_gauntletJson; }
        set
        {
            Dirty = true;
            m_gauntletJson = value;
        }
    }

    /// <summary>The boons taken on this run, as a JSON string array of boon ids (TEXT column).</summary>
    [DataElement(AllowDbNull = true)]
    public string BoonsJson
    {
        get { return m_boonsJson; }
        set
        {
            Dirty = true;
            m_boonsJson = value;
        }
    }

    /// <summary>The boon offer waiting for a choice, as a JSON string array of boon ids; empty when none (TEXT column).</summary>
    [DataElement(AllowDbNull = true)]
    public string PendingBoonOfferJson
    {
        get { return m_pendingBoonOfferJson; }
        set
        {
            Dirty = true;
            m_pendingBoonOfferJson = value;
        }
    }
}

/// <summary>
/// A run in memory. <see cref="ExpeditionManager"/> owns the cache and copies it to and from
/// <see cref="OfflineExpeditionRecord"/>.
/// </summary>
public sealed class ExpeditionRun
{
    public const int MaxLogLines = 100;

    public string CharacterId;
    public string CharacterName;
    public string AccountName;
    public bool SummaryShown;
    public int Seed;
    public int Act;
    public string Status;
    public ActPlan CurrentAct;
    public int Kills;
    public int Deaths;
    public int BossesKilled;

    /// <summary>Realm points earned on the run (ExpeditionRealm); updated under <see cref="KillSync"/>.</summary>
    public long RealmPointsEarned;

    /// <summary>The character's played time (seconds) at its win; 0 until then.</summary>
    public long PlayedSecondsAtWin;

    /// <summary>Boon offer rerolls used (ExpeditionBoons.TryReroll); changed under the Boons lock.</summary>
    public int RerollsUsed;

    /// <summary>Expedition loot found on the run, indexed by <see cref="eLootRarity"/> (Common..Unique).</summary>
    public int[] LootByRarity = new int[5];

    public DateTime StartedUtc;
    public DateTime? EndedUtc;
    public List<string> Log = new();

    /// <summary>True for a hard-mode run (ExpeditionHardMode).</summary>
    public bool Hard;

    /// <summary>The unique a hard run took from the account's Gauntlet winner (/run inherit); null until one is taken.</summary>
    public string InheritedUnique;

    /// <summary>Ids of the boons taken on this run, in the order taken (see ExpeditionBoons).</summary>
    public List<string> Boons = new();

    /// <summary>Ids of the boons offered at the last act break and not chosen yet; empty when no choice is pending.</summary>
    public List<string> PendingBoonOffer = new();

    /// <summary>The loaded or added row, so later saves update it instead of adding a second one.</summary>
    internal OfflineExpeditionRecord Record;

    /// <summary>
    /// The last kill counted in <see cref="Kills"/>. A group kill raises EnemyKilled on a group member more than once
    /// (GameLiving.ProcessDeath and GamePlayer.EnemyKilled both notify it), so one target is counted once.
    /// </summary>
    internal object LastKillTarget;
    internal long LastKillTick;
    internal readonly object KillSync = new();

    public bool IsActive => Status == ExpeditionManager.StatusActive;

    /// <summary>"Loot: 3 common, 2 magic, 1 rare, 0 epic, 0 unique" for /run status, the journal and the summary.</summary>
    public string LootSummary => $"Loot: {LootByRarity[0]} common, {LootByRarity[1]} magic, {LootByRarity[2]} rare, {LootByRarity[3]} epic, {LootByRarity[4]} unique";

    public void AddLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (Log)
        {
            Log.Add(line);

            if (Log.Count > MaxLogLines)
            {
                Log.RemoveRange(0, Log.Count - MaxLogLines);
            }
        }
    }
}
