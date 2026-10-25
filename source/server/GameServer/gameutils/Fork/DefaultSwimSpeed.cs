using System;
using System.Linq;
using System.Reflection;
using DOL.Database;
using DOL.Logging;

namespace DOL.GS;

/// <summary>
/// Offline fork: every player swims as if a Greater Nereid potion were running (owner, 2026-10-24). The potion's own
/// water speed is read from the database the first time it is needed (its item's spell value), so it always matches
/// the game's potion; server setting player_default_swim_speed overrides it (-1: the potion's value, 0: off).
/// <see cref="WaterSpeedCalculator"/> takes the higher of this and the player's own water speed, so a real potion or
/// realm ability never stacks on top of it.
/// </summary>
public static class DefaultSwimSpeed
{
    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly object Sync = new();
    private static int? _potionValue;

    public static int Value
    {
        get
        {
            int setting = ServerProperties.Properties.PLAYER_DEFAULT_SWIM_SPEED;

            if (setting >= 0)
            {
                return setting;
            }

            lock (Sync)
            {
                _potionValue ??= ReadPotionValue();

                return _potionValue.Value;
            }
        }
    }

    private static int ReadPotionValue()
    {
        try
        {
            var potions = GameServer.Database.SelectObjects<DbItemTemplate>(DB.Column("Name").IsLike("%Nereid%"))
                .Where(item => item.SpellID > 0)
                .OrderByDescending(item => item.Name.Contains("Greater", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (DbItemTemplate potion in potions)
            {
                DbSpell spell = GameServer.Database.SelectObject<DbSpell>(DB.Column("SpellID").IsEqualTo(potion.SpellID));

                if (spell != null && spell.Value > 0 && string.Equals(spell.Type, eSpellType.WaterBreathing.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    int value = (int) spell.Value;
                    Log.Info($"[FORK] Default swim speed: {value}% (from {potion.Name}).");

                    return value;
                }
            }

            Log.Warn("[FORK] No Nereid potion with a water breathing spell was found; default swim speed is off. Set player_default_swim_speed to turn it on.");
        }
        catch (Exception ex)
        {
            Log.Error("[FORK] The Nereid potion could not be read for the default swim speed.", ex);
        }

        return 0;
    }
}
