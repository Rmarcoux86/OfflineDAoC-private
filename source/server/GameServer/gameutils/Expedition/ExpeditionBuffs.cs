using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DOL.Database;
using DOL.GS.Spells;
using DOL.Logging;

namespace DOL.GS.Expedition;

/// <summary>
/// The lesser buffs of a run: from wild shrines (for the rest of their act) and from the buff boons (for the rest of the
/// run). They are real buff spells of the classes' own types, cast by the player on itself, so the engine's stacking
/// rules apply: a buff class's stronger spell of the same kind takes over and ours waits disabled underneath it.
/// Strength is 60% of the player buff cap for the level (StatCalculator, ArmorFactorCalculator: 1.875 x level for spec
/// buffs), and endurance regeneration half of the strongest endurance regeneration buff in the spell table.
/// <see cref="Maintain"/> runs on the objective tracker's tick (in the world, never mid-load): it casts what is missing,
/// recasts after a level-up and ends what the run no longer grants.
/// </summary>
public static class ExpeditionBuffs
{
    public const string StrengthConstitution = "might";
    public const string DexterityQuickness = "grace";
    public const string Armor = "warding";
    public const string Endurance = "vigor";

    /// <summary>The shrine buff groups, in a fixed order (shrine draws index into it).</summary>
    public static readonly IReadOnlyList<string> Groups = [StrengthConstitution, DexterityQuickness, Armor, Endurance];

    /// <summary>Percent of the spec buff cap (1.875 x level) a lesser buff gives.</summary>
    public const int StrengthPercent = 60;

    /// <summary>Long enough for any act; the tracker ends them when the act or run does.</summary>
    private const int DurationSeconds = 4 * 60 * 60;

    private const int SpellIdBase = 1970020;

    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>The level each player's buffs were last cast at, so a level-up recasts them stronger.</summary>
    private static readonly ConcurrentDictionary<string, int> CastLevel = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<eSpellType, int> EffectGroups = new();
    private static int? _enduranceValue;

    public static string NameOf(string group)
    {
        return group switch
        {
            StrengthConstitution => "Might (strength and constitution)",
            DexterityQuickness => "Grace (dexterity and quickness)",
            Armor => "Warding (armor)",
            Endurance => "Vigor (endurance regeneration)",
            _ => group
        };
    }

    public static string ShrineName(string group)
    {
        return group switch
        {
            StrengthConstitution => "Shrine of Might",
            DexterityQuickness => "Shrine of Grace",
            Armor => "Shrine of Warding",
            Endurance => "Shrine of Vigor",
            _ => "Shrine"
        };
    }

    /// <summary>The value of a lesser stat or armor buff at <paramref name="level"/>: 60% of 1.875 x level.</summary>
    public static int StatValue(int level)
    {
        return Math.Max(1, (int) Math.Round(Math.Max(1, level) * 1.875 * StrengthPercent / 100.0));
    }

    /// <summary>The buff groups the run grants right now: its buff boons, and the used shrines of the current act.</summary>
    public static HashSet<string> Wanted(ExpeditionRun run)
    {
        var groups = new HashSet<string>(StringComparer.Ordinal);

        if (run == null)
        {
            return groups;
        }

        List<string> boons;

        lock (run.Boons)
        {
            boons = run.Boons.ToList();
        }

        if (boons.Contains(ExpeditionBoons.BlessedArms))
        {
            groups.Add(StrengthConstitution);
            groups.Add(DexterityQuickness);
        }

        if (boons.Contains(ExpeditionBoons.SecondWind))
        {
            groups.Add(Endurance);
        }

        if (boons.Contains(ExpeditionBoons.WardedHide))
        {
            groups.Add(Armor);
        }

        foreach (ShrineSpec shrine in run.CurrentAct?.Shrines ?? [])
        {
            if (shrine != null && shrine.Used && Groups.Contains(shrine.Group))
            {
                groups.Add(shrine.Group);
            }
        }

        return groups;
    }

    /// <summary>
    /// Casts every wanted buff the player lacks (or all of them after a level-up) and ends ours that are no longer
    /// wanted. Called by the objective tracker's tick while the player is alive and in the world.
    /// </summary>
    public static void Maintain(GamePlayer player, ExpeditionRun run)
    {
        if (player?.effectListComponent == null || !player.IsAlive)
        {
            return;
        }

        try
        {
            HashSet<string> wanted = Wanted(run);
            Dictionary<string, ECSGameEffect> ours = OurEffects(player);
            bool levelChanged = CastLevel.TryGetValue(player.ObjectId, out int castAt) && castAt != player.Level;

            foreach ((string group, ECSGameEffect effect) in ours)
            {
                if (!wanted.Contains(group))
                {
                    effect.End();
                }
            }

            foreach (string group in wanted)
            {
                // A Vampiir cannot hold stat buffs (DualStatBuff refuses them with a chat line, and no effect is made),
                // so casting would repeat on every tick.
                if (!CanHold(player, group))
                {
                    continue;
                }

                if (!ours.ContainsKey(group) || levelChanged)
                {
                    Cast(player, group);
                }
            }

            CastLevel[player.ObjectId] = player.Level;
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] Lesser buffs could not be kept up for {player.Name}.", ex);
        }
    }

    /// <summary>False for a buff the engine refuses the player outright (Vampiir stat abilities, DualStatBuff.ApplyEffectOnTarget).</summary>
    private static bool CanHold(GamePlayer player, string group)
    {
        return group switch
        {
            StrengthConstitution => !player.HasAbility(Abilities.VampiirStrength) && !player.HasAbility(Abilities.VampiirConstitution),
            DexterityQuickness => !player.HasAbility(Abilities.VampiirDexterity) && !player.HasAbility(Abilities.VampiirQuickness),
            _ => true
        };
    }

    /// <summary>
    /// The effect bar icon (and cast effect) of each group, the ones the stock buff merchant's spells use
    /// (BuffMerchant.cs: 1517 strength/constitution, 1526 dexterity/quickness, 1506 spec armor factor) and spells.csv
    /// 1081 "Chant of Endurance" for endurance regeneration. Without an icon the buff is invisible in the effect bar.
    /// </summary>
    private static ushort IconOf(string group)
    {
        return group switch
        {
            StrengthConstitution => 1517,
            DexterityQuickness => 1526,
            Armor => 1506,
            _ => 1081
        };
    }

    /// <summary>Ends every lesser buff on the player (the run is over).</summary>
    public static void CancelAll(GamePlayer player)
    {
        if (player?.effectListComponent == null)
        {
            return;
        }

        try
        {
            foreach (ECSGameEffect effect in OurEffects(player).Values)
            {
                effect.End();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] Lesser buffs could not be removed from {player.Name}.", ex);
        }

        CastLevel.TryRemove(player.ObjectId, out _);
    }

    /// <summary>Our effects on the player (active or disabled under a stronger buff), by group.</summary>
    private static Dictionary<string, ECSGameEffect> OurEffects(GamePlayer player)
    {
        var ours = new Dictionary<string, ECSGameEffect>(StringComparer.Ordinal);

        foreach (ECSGameEffect effect in player.effectListComponent.GetEffects())
        {
            int id = effect.SpellHandler?.Spell?.ID ?? 0;
            int index = id - SpellIdBase;

            if (index >= 0 && index < Groups.Count)
            {
                ours[Groups[index]] = effect;
            }
        }

        return ours;
    }

    private static void Cast(GamePlayer player, string group)
    {
        int index = Groups.ToList().IndexOf(group);

        if (index < 0)
        {
            return;
        }

        (eSpellType type, double value) = group switch
        {
            StrengthConstitution => (eSpellType.StrengthConstitutionBuff, StatValue(player.Level)),
            DexterityQuickness => (eSpellType.DexterityQuicknessBuff, StatValue(player.Level)),
            Armor => (eSpellType.SpecArmorFactorBuff, StatValue(player.Level)),
            _ => (eSpellType.EnduranceRegenBuff, EnduranceValue())
        };

        DbSpell spell = new()
        {
            AllowAdd = false,
            SpellID = SpellIdBase + index,
            Name = $"Lesser {NameOf(group).Split(' ')[0]}",
            Description = $"A lesser {NameOf(group).ToLowerInvariant()} buff from the expedition.",
            Icon = IconOf(group),
            ClientEffect = IconOf(group),
            Value = value,
            Duration = DurationSeconds,
            Range = 0,
            Target = eSpellTarget.SELF.ToString(),
            Type = type.ToString(),
            EffectGroup = EffectGroupOf(type),
            Uninterruptible = true
        };

        // The potion line: stat buffs cast by a player scale with the caster's spec in the spell's line
        // (SingleStatBuff.CalculateBuffDebuffEffectiveness), down to 75% for a line the player has no spec in; potion,
        // item and style lines always count at full strength.
        ISpellHandler handler = ScriptMgr.CreateSpellHandler(player, new Spell(spell, Math.Max(1, (int) player.Level)), SkillBase.GetSpellLine(GlobalSpellsLines.Potions_Effects));
        handler?.StartSpell(player);
    }

    /// <summary>
    /// The effect group the class buffs of this type use in the spell table, so ours conflicts with theirs and the
    /// stronger one wins (SpellHandler.HasConflictingEffectWith). 0 (match by spell type) when the table has none.
    /// </summary>
    private static int EffectGroupOf(eSpellType type)
    {
        return EffectGroups.GetOrAdd(type, t =>
        {
            try
            {
                return GameServer.Database.SelectObjects<DbSpell>(DB.Column("Type").IsEqualTo(t.ToString()))
                    .Where(s => s.EffectGroup != 0)
                    .GroupBy(s => s.EffectGroup)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                Log.Error($"[EXPEDITION] Could not read the effect group of {t} buffs.", ex);

                return 0;
            }
        });
    }

    /// <summary>Half the strongest endurance regeneration buff in the spell table (at least 1).</summary>
    private static int EnduranceValue()
    {
        if (_enduranceValue.HasValue)
        {
            return _enduranceValue.Value;
        }

        int value = 1;

        try
        {
            double strongest = GameServer.Database.SelectObjects<DbSpell>(DB.Column("Type").IsEqualTo(eSpellType.EnduranceRegenBuff.ToString()))
                .Select(s => s.Value)
                .DefaultIfEmpty(2)
                .Max();
            value = Math.Max(1, (int) Math.Round(strongest / 2));
        }
        catch (Exception ex)
        {
            Log.Error("[EXPEDITION] Could not read the endurance regeneration buffs.", ex);
        }

        _enduranceValue = value;

        return value;
    }
}
