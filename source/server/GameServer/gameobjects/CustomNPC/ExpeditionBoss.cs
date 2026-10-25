using System;
using System.Reflection;
using DOL.AI.Brain;
using DOL.Database;
using DOL.Logging;

namespace DOL.GS.Expedition;

/// <summary>
/// An Expedition act boss: a buffed, script-spawned copy of a catalog mob, owned by one run's player. It is never
/// saved to the database and never respawns on its own; <see cref="ObjectiveTracker"/> spawns it, removes it when the
/// player leaves and respawns it when needed. Its death is reported back to the tracker for credit and reward.
/// </summary>
public sealed class ExpeditionBoss : GameNPC
{
    /// <summary>The model used when the base mob is not in the Mob table: GameNPC's own default model (GameNPC.cs:4220).</summary>
    public const ushort FallbackModel = 408;
    public const byte FallbackSize = 60;
    public const int BossAggroLevel = 100;
    public const int BossAggroRange = 1500;

    private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    // Field initialisers run before the GameNPC constructor, which already reads MaxHealth (and so this factor).
    private double _healthMultiplier = 1.0;
    private GamePlayer _owner;

    public ExpeditionBoss(GamePlayer owner, StandardMobBrain brain) : base(brain)
    {
        _owner = owner;
        OwnerKey = owner?.ObjectId;
    }

    /// <summary>The run's character id (GamePlayer.ObjectId), the tracker's key.</summary>
    public string OwnerKey { get; private set; }

    /// <summary>
    /// The health lever: MaxHealthCalculator multiplies an NPC's level/constitution health by this factor
    /// (MaxHealthCalculator.cs:147), so the boss keeps the normal level curve times the spec's multiplier.
    /// </summary>
    public override double MaxHealthScalingFactor => _healthMultiplier;

    /// <summary>Detaches the boss from its run, so its death or removal no longer reports to the tracker.</summary>
    public void Release()
    {
        _owner = null;
        OwnerKey = null;
    }

    public static ExpeditionBoss Spawn(GamePlayer owner, BossSpec spec)
    {
        if (owner == null || spec == null)
        {
            return null;
        }

        StandardMobBrain brain = new()
        {
            AggroLevel = BossAggroLevel,
            AggroRange = BossAggroRange
        };

        ExpeditionBoss boss = new(owner, brain);
        DbMob template = FindTemplate(spec);

        if (template != null)
        {
            boss.Model = template.Model;
            boss.Size = template.Size;
            boss.Race = (short) template.Race;
            boss.BodyType = (ushort) template.BodyType;

            if (template.MeleeDamageType > 0)
            {
                boss.MeleeDamageType = (eDamageType) template.MeleeDamageType;
            }

            if (template.FactionID > 0)
            {
                boss.Faction = FactionMgr.GetFactionByID(template.FactionID);
            }

            boss.LoadEquipmentTemplateFromDatabase(template.EquipmentTemplateID);
        }
        else
        {
            log.Warn($"[EXPEDITION] Boss base mob '{spec.BaseMobName}' is not in the Mob table; using fallback model {FallbackModel}.");
            boss.Model = FallbackModel;
            boss.Size = FallbackSize;
        }

        boss.Name = string.IsNullOrWhiteSpace(spec.Title) ? spec.BaseMobName : spec.Title;

        // The named act bosses look like their race and class (docs/EXPEDITION-CLASS-BOSSES.md); still plain monsters.
        ExpeditionBossLooks.Apply(boss, spec.Title);
        boss.Realm = eRealm.None;
        boss.RespawnInterval = -1;
        boss.RoamingRange = 0;
        boss._healthMultiplier = Math.Max(1, spec.HealthMultiplier);

        // GameNPC.SetStats only auto-sets a stat that is below 1 (GameNPC.cs:235), and the constructor already set
        // them for level 1; clear them so the Level setter auto-sets them for the boss's level.
        boss.Strength = 0;
        boss.Constitution = 0;
        boss.Dexterity = 0;
        boss.Quickness = 0;
        boss.Intelligence = 0;
        boss.Empathy = 0;
        boss.Piety = 0;
        boss.Charisma = 0;
        boss.Level = (byte) Math.Clamp(spec.Level, 1, 255);
        boss.Health = boss.MaxHealth;

        boss.CurrentRegionID = (ushort) spec.RegionId;
        boss.X = spec.X;
        boss.Y = spec.Y;
        boss.Z = spec.Z;
        boss.Heading = 0;

        if (boss.CurrentRegion == null || !boss.AddToWorld())
        {
            log.Warn($"[EXPEDITION] Boss '{boss.Name}' could not be added to region {spec.RegionId} at ({spec.X}, {spec.Y}, {spec.Z}).");

            return null;
        }

        return boss;
    }

    /// <summary>The base mob row: one in the boss's region first, then any row with that name.</summary>
    private static DbMob FindTemplate(BossSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.BaseMobName))
        {
            return null;
        }

        try
        {
            DbMob mob = GameServer.Database.SelectObject<DbMob>(DB.Column("Name").IsEqualTo(spec.BaseMobName).And(DB.Column("Region").IsEqualTo(spec.RegionId)));

            return mob ?? GameServer.Database.SelectObject<DbMob>(DB.Column("Name").IsEqualTo(spec.BaseMobName));
        }
        catch (Exception ex)
        {
            log.Error($"[EXPEDITION] Mob table lookup failed for boss base mob '{spec.BaseMobName}'.", ex);

            return null;
        }
    }

    public override void ProcessDeath(GameObject killer)
    {
        // Credit is decided before the base call, which clears the attacker list (GameLiving.cs:2050).
        GamePlayer owner = _owner;
        bool credited = false;

        if (owner != null)
        {
            credited = ObjectiveTracker.IsCreditedTo(owner, killer);

            if (!credited)
            {
                foreach (GameLiving attacker in attackComponent.AttackerTracker.Attackers)
                {
                    if (ObjectiveTracker.IsCreditedTo(owner, attacker))
                    {
                        credited = true;
                        break;
                    }
                }
            }
        }

        base.ProcessDeath(killer);

        if (owner == null)
        {
            return;
        }

        try
        {
            ObjectiveTracker.OnBossDied(this, credited);
        }
        catch (Exception ex)
        {
            log.Error($"[EXPEDITION] Boss death handling failed for {owner.Name}.", ex);
        }
    }

    #region Phase 3 extension point

    // Boss abilities go here (phase 3): override GameNPC hooks such as StartAttack / TakeDamage, or give the boss a
    // brain derived from StandardMobBrain (ExpeditionBossBrain next to this file) and pass it to the constructor in
    // Spawn. Nothing is custom yet: the boss fights with the standard mob AI.

    #endregion
}
