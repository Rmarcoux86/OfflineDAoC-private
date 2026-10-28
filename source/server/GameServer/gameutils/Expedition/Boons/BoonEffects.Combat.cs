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
    private const int MarkedPreyPercent = 20;
    private const double EvasionChance = 0.10;
    private const double SwiftfootFactor = 1.2;
    private const int PhoenixTriggerPercent = 25;
    private const int PhoenixHealToPercent = 60;
    private const int SoulHarvestPercent = 8;
    private const int DesperationBelowPercent = 50;
    private const int DesperationDamagePercent = 30;
    private const int SiphonPercent = 3;
    private const int HuntersInstinctBelowPercent = 50;
    private const int HuntersInstinctDamagePercent = 15;
    private const int BerserkerHealCapPercent = 70;
    private const double BeastBondHealthFactor = 1.2;
    private const int BeastBondDamagePercent = 15;

    #region Hits the player lands

    /// <summary>
    /// A hit the player lands, before its damage is applied: Executioner, Siphon and
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

        // Marked Prey: elites and act bosses take more from every hit.
        if (boons.Contains(ExpeditionBoons.MarkedPrey) && target is GameNPC marked && (marked is ExpeditionBoss || ExpeditionElites.IsElite(marked)) && dealt > 0)
        {
            ad.Damage = ad.Damage * (100 + MarkedPreyPercent) / 100;
            ad.CriticalDamage = ad.CriticalDamage * (100 + MarkedPreyPercent) / 100;
            dealt = ad.Damage + ad.CriticalDamage;
        }

        // Hunter's Instinct: a monster already below half health takes more from every hit.
        if (boons.Contains(ExpeditionBoons.HuntersInstinct) && target is GameNPC hunted && IsMonster(hunted) && hunted.IsAlive
            && hunted.HealthPercent < HuntersInstinctBelowPercent && dealt > 0)
        {
            ad.Damage = ad.Damage * (100 + HuntersInstinctDamagePercent) / 100;
            ad.CriticalDamage = ad.CriticalDamage * (100 + HuntersInstinctDamagePercent) / 100;
            dealt = ad.Damage + ad.CriticalDamage;
        }

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

    /// <summary>
    /// Berserker: the player cannot be healed above 70% health. Any health gain (spell, potion, regeneration, a boon)
    /// is cut to what reaches that line; losses pass through. Called by GamePlayer.ChangeHealth.
    /// </summary>
    public static int CapHealthGain(GamePlayer player, int changeAmount)
    {
        if (changeAmount <= 0 || player == null || !HasBoon(player, ExpeditionBoons.Berserker))
        {
            return changeAmount;
        }

        int cap = player.MaxHealth * BerserkerHealCapPercent / 100;

        return Math.Max(0, Math.Min(changeAmount, cap - player.Health));
    }

    /// <summary>The player whose pet this is, when it is a player's pet (a controlled brain with a player owner).</summary>
    private static GamePlayer PetOwner(GameLiving living)
    {
        return living is GameNPC { Brain: IControlledBrain brain } ? brain.GetPlayerOwner() : null;
    }

    /// <summary>Beast Bond: the max health factor of a player's pet (1 without the boon). Called by MaxHealthCalculator.</summary>
    public static double PetHealthFactor(GameLiving pet)
    {
        GamePlayer owner = PetOwner(pet);

        return owner != null && HasBoon(owner, ExpeditionBoons.BeastBond) ? BeastBondHealthFactor : 1.0;
    }

    /// <summary>Beast Bond: a hit the player's pet lands on a monster, before its damage is applied.</summary>
    private static void OnPetLandsHit(GamePlayer owner, AttackData ad)
    {
        if (ad.Target is GameNPC monster && IsMonster(monster) && HasBoon(owner, ExpeditionBoons.BeastBond))
        {
            ad.Damage = ad.Damage * (100 + BeastBondDamagePercent) / 100;
            ad.CriticalDamage = ad.CriticalDamage * (100 + BeastBondDamagePercent) / 100;
        }
    }

    /// <summary>Evasion: the evade chance (0-1) a player with the boon adds against a melee attack. Called by GameLiving.TryEvade.</summary>
    public static double EvasionBonus(GameLiving defender, AttackData ad)
    {
        if (defender is not GamePlayer player || ad == null || !ad.IsMeleeAttack || !HasBoon(player, ExpeditionBoons.Evasion))
        {
            return 0;
        }

        return EvasionChance;
    }

    /// <summary>Swiftfoot: the run speed factor out of combat (1 without the boon). Called by MaxSpeedCalculator.</summary>
    public static double SwiftfootSpeedFactor(GamePlayer player)
    {
        if (player == null || player.InCombat || !HasBoon(player, ExpeditionBoons.Swiftfoot))
        {
            return 1.0;
        }

        return SwiftfootFactor;
    }
}
