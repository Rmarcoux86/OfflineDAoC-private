using System;
using System.Collections.Generic;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.PacketHandler;
using DOL.GS.Spells;

namespace DOL.GS.Expedition;

/// <summary>
/// The combat boons: what happens when the player lands a hit, kills a monster, or drops low on health. Hits are seen
/// before their damage is applied (WeaponAction.MakeAttack and SpellHandler.DamageTarget both notify the target first),
/// so <see cref="AttackData.Damage"/> can still be changed here, and the target's health is the pre-hit value.
/// </summary>
public static partial class BoonEffects
{
    private const int ExecutionerThresholdPercent = 15;
    private const int PhoenixTriggerPercent = 25;
    private const int PhoenixHealToPercent = 60;
    private const int SoulHarvestPercent = 8;
    private const int DesperationBelowPercent = 50;
    private const int DesperationDamagePercent = 30;
    private const int FrostbiteChancePercent = 25;
    private const long FrostbiteCooldownMs = 4000;
    private const int FrostbiteSnarePercent = 40;
    private const int FrostbiteSeconds = 8;
    private const int SiphonPercent = 3;
    private const int ScavengerChancePercent = 20;

    private const int FrostbiteSpellId = 1970010;
    private const int FrostbiteEffectGroup = 1970010;
    private const ushort FrostbiteIcon = 2616;

    #region Hits the player lands

    /// <summary>
    /// A hit the player lands, before its damage is applied: Executioner, Frostbite, Siphon and
    /// Vampiric. <paramref name="ad"/>.Target still has its pre-hit health.
    /// </summary>
    private static void OnPlayerLandsHit(GamePlayer striker, AttackData ad)
    {
        AppliedBoons state = striker.TempProperties.GetProperty<AppliedBoons>(StateKey, null);

        if (state == null || !striker.IsAlive)
        {
            return;
        }

        // Damage-over-time ticks are not hits for Siphon. A style bleed is a small melee
        // side effect and rolls nothing (owner, 2026-10-24: Vampiric "procced on the bleed" for 1 health), but a DoT
        // spell is a caster's real damage, so Vampiric rolls on its ticks (owner, same day). Executioner sees both,
        // so a tick can finish a monster off.
        bool styleBleed = ad.SpellHandler?.Spell?.SpellType is eSpellType.StyleBleeding;
        bool dotTick = styleBleed || ad.SpellHandler?.Spell?.SpellType is eSpellType.DamageOverTime or eSpellType.HereticDamageOverTime;
        HashSet<string> boons;

        lock (state)
        {
            boons = new HashSet<string>(state.Ids);
        }

        GameLiving target = ad.Target;
        int dealt = ad.Damage + ad.CriticalDamage;

        // Executioner: a monster that is already below the threshold dies to this hit, whatever it rolled.
        if (boons.Contains(ExpeditionBoons.Executioner) && target is GameNPC victim && victim is not ExpeditionBoss && IsMonster(victim)
            && victim.IsAlive && victim.HealthPercent <= ExecutionerThresholdPercent && dealt < victim.Health)
        {
            ad.Damage = victim.Health;
            ad.CriticalDamage = 0;
            dealt = ad.Damage;
            striker.Out?.SendMessage($"Executioner: you finish off {victim.GetName(0, false)}!", eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
        }

        if (dealt <= 0 || styleBleed)
        {
            return;
        }

        // Frostbite: a melee hit may snare the target.
        if (boons.Contains(ExpeditionBoons.Frostbite) && ad.IsMeleeAttack && target is GameNPC snared && IsMonster(snared) && snared.IsAlive)
        {
            TryFrostbite(striker, state, snared);
        }

        // Siphon: power from every hit.
        if (boons.Contains(ExpeditionBoons.Siphon) && !dotTick && striker.Mana < striker.MaxMana)
        {
            striker.ChangeMana(striker, eManaChangeType.Spell, Math.Max(1, striker.MaxMana * SiphonPercent / 100));
        }

        // Vampiric: a chance to heal half the damage.
        if (boons.Contains(ExpeditionBoons.Vampiric) && striker.Health < striker.MaxHealth && Util.Chance(VampiricChancePercent))
        {
            int healed = striker.ChangeHealth(striker, eHealthChangeType.Spell, Math.Max(1, dealt / 2));

            if (healed > 0)
            {
                striker.Out?.SendMessage($"Vampiric: you drain {healed} health.", eChatType.CT_Spell, eChatLoc.CL_SystemWindow);
            }
        }
    }

    private static void TryFrostbite(GamePlayer striker, AppliedBoons state, GameNPC target)
    {
        long now = GameLoop.GameLoopTime;

        lock (state)
        {
            if (now < state.NextFrostbiteAt || !Util.Chance(FrostbiteChancePercent))
            {
                return;
            }

            state.NextFrostbiteAt = now + FrostbiteCooldownMs;
        }

        // The weapon-proc route (as ExpeditionElites.TryVenom): a spell handler started straight on the target.
        ISpellHandler handler = ScriptMgr.CreateSpellHandler(striker, CreateFrostbiteSpell(striker.Level), SkillBase.GetSpellLine(GlobalSpellsLines.Mob_Spells));
        handler?.StartSpell(target);
    }

    private static Spell CreateFrostbiteSpell(int level)
    {
        DbSpell spell = new()
        {
            AllowAdd = false,
            SpellID = FrostbiteSpellId,
            Name = "Frostbite",
            Description = "Slowed by biting cold.",
            Icon = FrostbiteIcon,
            ClientEffect = FrostbiteIcon,
            TooltipId = FrostbiteIcon,
            Value = FrostbiteSnarePercent,
            Duration = FrostbiteSeconds,
            Range = 0,
            Target = eSpellTarget.ENEMY.ToString(),
            Type = eSpellType.SpeedDecrease.ToString(),
            EffectGroup = FrostbiteEffectGroup,
            Uninterruptible = true,
            Message1 = "Biting cold slows you!",
            Message2 = "{0} is slowed by biting cold!",
            Message3 = "The cold fades.",
            Message4 = "{0} moves freely again."
        };

        return new Spell(spell, Math.Max(1, level));
    }

    #endregion

    #region Kills

    /// <summary>
    /// A monster the player killed (credited by <see cref="ObjectiveTracker"/>): Soul Harvest and Bounty
    /// Hunter's elite bonus drop.
    /// </summary>
    public static void OnKill(GamePlayer player, GameLiving target)
    {
        if (player == null || target is not GameNPC killed || !ExpeditionLoot.IsLootableKill(killed))
        {
            return;
        }

        try
        {
            if (HasBoon(player, ExpeditionBoons.SoulHarvest) && player.IsAlive)
            {
                player.ChangeHealth(player, eHealthChangeType.Spell, Math.Max(1, player.MaxHealth * SoulHarvestPercent / 100));
                player.ChangeMana(player, eManaChangeType.Spell, Math.Max(1, player.MaxMana * SoulHarvestPercent / 100));
            }

            if (HasBoon(player, ExpeditionBoons.BountyHunter) && ExpeditionElites.IsElite(killed))
            {
                player.Out?.SendMessage("Bounty Hunter: the elite drops an extra item.", eChatType.CT_Loot, eChatLoc.CL_SystemWindow);
                ExpeditionLoot.DropFor(player, killed, eLootRarity.Rare);
            }
        }
        catch (Exception)
        {
            // A boon must never break a kill.
        }
    }

    /// <summary>Bounty Hunter: one more boss drop. Called by ExpeditionLoot.BossDrops.</summary>
    public static int ExtraBossDrops(GamePlayer player)
    {
        return HasBoon(player, ExpeditionBoons.BountyHunter) ? 1 : 0;
    }

    /// <summary>Scavenger: whether this drop comes with a second one. Called by ExpeditionLoot.DropFor.</summary>
    public static bool RollsSecondDrop(GamePlayer player)
    {
        return HasBoon(player, ExpeditionBoons.Scavenger) && Util.Chance(ScavengerChancePercent);
    }

    #endregion

    #region Pulse: Phoenix Feather and Desperation

    /// <summary>Every half second: Phoenix Feather's rescue and Desperation's damage switch. Stops with the run or the boons.</summary>
    private static int PulseTick(GamePlayer player, AppliedBoons state)
    {
        ExpeditionRun run = ExpeditionManager.GetRun(player);
        bool phoenix;
        bool desperation;

        lock (state)
        {
            phoenix = state.Ids.Contains(ExpeditionBoons.PhoenixFeather);
            desperation = state.Ids.Contains(ExpeditionBoons.Desperation);

            if (run == null || state.PulseTimer == null || (!phoenix && !desperation))
            {
                state.PulseTimer = null;

                return 0;
            }
        }

        if (!player.IsAlive || player.CurrentRegion == null)
        {
            return PulseIntervalMilliseconds;
        }

        int act = run.Act;
        int healthPercent = player.HealthPercent;

        if (phoenix && healthPercent < PhoenixTriggerPercent)
        {
            bool rescue = false;

            lock (state)
            {
                if (state.PhoenixUsedAct != act)
                {
                    state.PhoenixUsedAct = act;
                    rescue = true;
                }
            }

            if (rescue)
            {
                int to = player.MaxHealth * PhoenixHealToPercent / 100;
                player.ChangeHealth(player, eHealthChangeType.Spell, Math.Max(0, to - player.Health));
                player.Out?.SendMessage("Phoenix Feather! Fire wraps you and your wounds close. It will not burn again this act.", eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
                player.Out?.SendMessage("Phoenix Feather: you are healed to 60% health.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                healthPercent = player.HealthPercent;
            }
        }

        if (desperation)
        {
            bool wanted = healthPercent < DesperationBelowPercent;
            bool changed = false;

            lock (state)
            {
                if (state.DesperationOn != wanted)
                {
                    SetDesperation(player, state, wanted);
                    changed = true;
                }
            }

            if (changed)
            {
                player.Out?.SendMessage(wanted ? "Desperation: you fight harder!" : "Desperation fades.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
                SendUpdates(player);
            }
        }

        return PulseIntervalMilliseconds;
    }

    /// <summary>Switches Desperation's damage bonus on or off. The caller holds the state lock.</summary>
    private static void SetDesperation(GamePlayer player, AppliedBoons state, bool on)
    {
        int sign = on ? 1 : -1;
        player.AbilityBonus[eProperty.MeleeDamage] += sign * DesperationDamagePercent;
        player.AbilityBonus[eProperty.SpellDamage] += sign * DesperationDamagePercent;
        state.DesperationOn = on;
    }

    #endregion

    /// <summary>A monster: not a pet, companion, bot or friendly NPC.</summary>
    private static bool IsMonster(GameNPC npc)
    {
        return npc != null && npc is not GameBot && npc.Brain is not IControlledBrain && npc.Realm == eRealm.None;
    }
}
