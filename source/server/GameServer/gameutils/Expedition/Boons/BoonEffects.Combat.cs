using System;
using System.Collections.Generic;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.PacketHandler;
using DOL.GS.Expedition.Gauntlet;
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

    /// <summary>Executioner against act bosses and Gauntlet champions (owner 2026-10-27): 10%, direct hits only.</summary>
    private const int ExecutionerBossThresholdPercent = 10;
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

    // The hard expedition boons (owner 2026-10-29).
    private const int AmbushDamagePercent = 50;
    private const int SilentDeathHealthPercent = 20;
    private const int SilentDeathEndurancePercent = 10;
    public const int PoisonerDurationPercent = 150;
    public const int PoisonerDamagePercent = 125;
    public const int DirtyFightingDurationPercent = 130;

    /// <summary>
    /// The pure casters (owner 2026-10-27): the list casters, the pet casters and the healers, and their caster base
    /// classes. Vampiric rolls on their damage-over-time ticks; a hybrid's DoTs (Reaver, Heretic, ...) roll nothing.
    /// </summary>
    private static readonly HashSet<eCharacterClass> PureCasters = new()
    {
        eCharacterClass.Wizard, eCharacterClass.Sorcerer, eCharacterClass.Theurgist, eCharacterClass.Cabalist, eCharacterClass.Necromancer, eCharacterClass.Cleric,
        eCharacterClass.Runemaster, eCharacterClass.Spiritmaster, eCharacterClass.Bonedancer, eCharacterClass.Warlock, eCharacterClass.Healer, eCharacterClass.Shaman,
        eCharacterClass.Eldritch, eCharacterClass.Enchanter, eCharacterClass.Mentalist, eCharacterClass.Animist, eCharacterClass.Sluaghbinder, eCharacterClass.Druid,
        eCharacterClass.Mage, eCharacterClass.Elementalist, eCharacterClass.Mystic, eCharacterClass.Seer, eCharacterClass.Magician, eCharacterClass.Forester
    };

    public static bool IsPureCaster(GamePlayer player)
    {
        return player?.CharacterClass != null && PureCasters.Contains((eCharacterClass) player.CharacterClass.ID);
    }

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
        // spell is a caster's real damage, so Vampiric rolls on its ticks (owner, same day), but only for a pure caster
        // (owner 2026-10-27: not on a Reaver's or other hybrid's DoTs; see PureCasters). Executioner sees both,
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

        // Ambush and Silent Death: is the monster still unaware of the fight? Asked before this hit puts it in combat
        // (GameLiving.OnAttackedByEnemy notifies first and sets the combat ticks after).
        if ((boons.Contains(ExpeditionBoons.Ambush) || boons.Contains(ExpeditionBoons.SilentDeath)) && target is GameNPC struck && IsMonster(struck))
        {
            bool unaware = IsUnaware(struck.InCombat, struck.attackComponent?.AttackState == true);

            lock (state)
            {
                state.LastHitMonster = struck;
                state.LastHitUnaware = unaware;
            }

            // Ambush: the opening hit on an unaware monster does more.
            if (unaware && boons.Contains(ExpeditionBoons.Ambush) && dealt > 0)
            {
                ad.Damage = ad.Damage * (100 + AmbushDamagePercent) / 100;
                ad.CriticalDamage = ad.CriticalDamage * (100 + AmbushDamagePercent) / 100;
                dealt = ad.Damage + ad.CriticalDamage;
                striker.Out?.SendMessage($"Ambush: you catch {struck.GetName(0, false)} unaware!", eChatType.CT_YouHit, eChatLoc.CL_SystemWindow);
            }
        }

        // Poisoner: the ticks of the player's weapon poisons (the Mundane Poisons line) hit harder.
        if (boons.Contains(ExpeditionBoons.Poisoner) && IsWeaponPoisonDot(ad.SpellHandler) && dealt > 0)
        {
            ad.Damage = ad.Damage * PoisonerDamagePercent / 100;
            ad.CriticalDamage = ad.CriticalDamage * PoisonerDamagePercent / 100;
            dealt = ad.Damage + ad.CriticalDamage;
        }

        // Marked Prey: elites, act bosses and Gauntlet champions take more from every hit.
        if (boons.Contains(ExpeditionBoons.MarkedPrey) && target is GameNPC marked && (IsBossLike(marked) || ExpeditionElites.IsElite(marked)) && dealt > 0)
        {
            ad.Damage = ad.Damage * (100 + MarkedPreyPercent) / 100;
            ad.CriticalDamage = ad.CriticalDamage * (100 + MarkedPreyPercent) / 100;
            dealt = ad.Damage + ad.CriticalDamage;
        }

        // Hunter's Instinct: a monster already below half health takes more from every hit.
        if (boons.Contains(ExpeditionBoons.HuntersInstinct) && target is GameNPC hunted && (IsMonster(hunted) || hunted is GauntletChampion) && hunted.IsAlive
            && hunted.HealthPercent < HuntersInstinctBelowPercent && dealt > 0)
        {
            ad.Damage = ad.Damage * (100 + HuntersInstinctDamagePercent) / 100;
            ad.CriticalDamage = ad.CriticalDamage * (100 + HuntersInstinctDamagePercent) / 100;
            dealt = ad.Damage + ad.CriticalDamage;
        }

        // Executioner: a monster that is already below the threshold dies to this hit, whatever it rolled. Act bosses and
        // Gauntlet champions have a lower threshold and only a direct hit finishes them (a DoT tick does not).
        if (boons.Contains(ExpeditionBoons.Executioner) && target is GameNPC victim && (IsMonster(victim) || victim is GauntletChampion)
            && victim.IsAlive && dealt < victim.Health
            && (IsBossLike(victim)
                ? !dotTick && victim.HealthPercent <= ExecutionerBossThresholdPercent
                : victim.HealthPercent <= ExecutionerThresholdPercent))
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
        if (boons.Contains(ExpeditionBoons.Vampiric) && (!dotTick || IsPureCaster(striker)) && striker.Health < striker.MaxHealth
            && Util.Chance(VampiricChancePercent))
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

            // Silent Death: the killing hit landed while the monster was still unaware (it never fought back).
            AppliedBoons state = player.TempProperties.GetProperty<AppliedBoons>(StateKey, null);
            bool silent = false;

            if (state != null)
            {
                lock (state)
                {
                    silent = state.Ids.Contains(ExpeditionBoons.SilentDeath) && ReferenceEquals(state.LastHitMonster, killed) && state.LastHitUnaware;
                    state.LastHitMonster = null;
                }
            }

            if (silent && player.IsAlive)
            {
                player.ChangeHealth(player, eHealthChangeType.Spell, Math.Max(1, player.MaxHealth * SilentDeathHealthPercent / 100));
                player.ChangeEndurance(player, eEnduranceChangeType.Spell, Math.Max(1, player.MaxEndurance * SilentDeathEndurancePercent / 100));
                player.Out?.SendMessage($"Silent Death: {killed.GetName(0, true)} never saw you coming.", eChatType.CT_Spell, eChatLoc.CL_SystemWindow);
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

            if (!HasBoonSource(player, run) || state.PulseTimer == null || (!phoenix && !desperation))
            {
                state.PulseTimer = null;

                return 0;
            }
        }

        if (!player.IsAlive || player.CurrentRegion == null)
        {
            return PulseIntervalMilliseconds;
        }

        // Phoenix Feather is never applied in the Gauntlet (NotInGauntlet), so a missing run means no act to track.
        int act = run?.Act ?? 0;
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

    #region Hard expedition boons: pure parts

    /// <summary>
    /// Ambush and Silent Death: a monster is unaware while it is not in combat (no attack given or taken lately, not
    /// crowd controlled: GameLiving.InCombat) and is not attacking anyone (its attack component's AttackState, set as
    /// soon as it aggroes, while it still runs at its target).
    /// </summary>
    public static bool IsUnaware(bool inCombat, bool attacking)
    {
        return !inCombat && !attacking;
    }

    /// <summary>
    /// Dirty Fighting: the attacker is behind the target when the target's angle to it (GameObject.GetAngle: 0 straight
    /// ahead, clockwise in degrees) is in its back half, from 90 to 270 degrees (a back style needs 150 to 210).
    /// </summary>
    public static bool IsBehind(float angleFromTarget)
    {
        return angleFromTarget > 90 && angleFromTarget < 270;
    }

    /// <summary>A duration (milliseconds) scaled by a percent, rounded down, at least 1.</summary>
    public static int ScaleDuration(int duration, int percent)
    {
        return Math.Max(1, (int) ((long) duration * percent / 100));
    }

    #endregion

    #region Hard expedition boons: spell handler hooks

    /// <summary>A damage-over-time spell of the Mundane Poisons line: a weapon poison (applied by GameLiving.CheckWeaponMagicalEffect).</summary>
    private static bool IsWeaponPoisonDot(ISpellHandler handler)
    {
        return handler?.Spell?.SpellType is eSpellType.DamageOverTime && handler.SpellLine?.KeyName == GlobalSpellsLines.Mundane_Poisons;
    }

    /// <summary>
    /// Poisoner: the duration (milliseconds) of a weapon poison's damage over time the caster applies, 50% longer with
    /// the boon. Called by DoTSpellHandler.CalculateEffectDuration.
    /// </summary>
    public static int PoisonDuration(ISpellHandler handler, int duration)
    {
        if (duration <= 0 || handler?.Caster is not GamePlayer player || !IsWeaponPoisonDot(handler) || !HasBoon(player, ExpeditionBoons.Poisoner))
        {
            return duration;
        }

        return ScaleDuration(duration, PoisonerDurationPercent);
    }

    /// <summary>
    /// Dirty Fighting: the duration (milliseconds) of a stun or snare the caster lands, 30% longer when the caster has the
    /// boon and stands behind the target (<see cref="IsBehind"/>). Called by StunSpellHandler and
    /// SpeedDecreaseSpellHandler.CalculateEffectDuration.
    /// </summary>
    public static int DirtyFightingDuration(GameLiving caster, GameLiving target, int duration)
    {
        if (duration <= 0 || caster is not GamePlayer player || target == null || target == caster || !HasBoon(player, ExpeditionBoons.DirtyFighting))
        {
            return duration;
        }

        if (!IsBehind(target.GetAngle(caster)))
        {
            return duration;
        }

        return ScaleDuration(duration, DirtyFightingDurationPercent);
    }

    #endregion

    /// <summary>An act boss or a Gauntlet champion.</summary>
    private static bool IsBossLike(GameNPC npc)
    {
        return npc is ExpeditionBoss or GauntletChampion;
    }

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
