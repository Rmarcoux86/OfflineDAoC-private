using System;
using System.Linq;
using System.Reflection;
using DOL.Database;
using DOL.Events;
using DOL.GS.Spells;
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
    private static ushort _potionIcon;

    /// <summary>The blessing's spell id (a fork-only id, never in the spell table).</summary>
    private const int BlessingSpellId = 1970050;

    /// <summary>Long enough for any session; it is cast again at every login and after every death.</summary>
    private const int BlessingDurationSeconds = 24 * 60 * 60;

    /// <summary>The buff icon when the database has no Nereid potion to borrow one from.</summary>
    private const ushort FallbackIcon = 1081;

    private const int BlessingDelayMilliseconds = 3000;

    /// <summary>The swim speed bonus (percent) when the database has no potion or spell to read it from.</summary>
    public const int FallbackValue = 100;

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
                    _potionIcon = (ushort) spell.Icon;
                    int value = (int) spell.Value;
                    Log.Info($"[FORK] Default swim speed: {value}% (from {potion.Name}).");

                    return value;
                }
            }

            // No Nereid potion in this database (owner 2026-10-25: swimming was still slow): the strongest water
            // breathing spell the database has, else a fixed value.
            double strongest = GameServer.Database.SelectObjects<DbSpell>(DB.Column("Type").IsEqualTo(eSpellType.WaterBreathing.ToString()))
                .Select(spell => spell.Value)
                .DefaultIfEmpty(0)
                .Max();

            if (strongest > 0)
            {
                Log.Info($"[FORK] Default swim speed: {(int) strongest}% (no Nereid potion found; the strongest water breathing spell).");

                return (int) strongest;
            }

            Log.Warn($"[FORK] No water breathing spell was found; default swim speed is {FallbackValue}%. Set player_default_swim_speed to change it.");

            return FallbackValue;
        }
        catch (Exception ex)
        {
            Log.Error("[FORK] The Nereid potion could not be read for the default swim speed.", ex);
        }

        return FallbackValue;
    }

    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        GameEventMgr.AddHandler(GamePlayerEvent.GameEntered, new DOLEventHandler(OnBlessingDue));
        GameEventMgr.AddHandler(GamePlayerEvent.Revive, new DOLEventHandler(OnBlessingDue));
    }

    [GameServerStoppedEvent]
    public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
    {
        GameEventMgr.RemoveHandler(GamePlayerEvent.GameEntered, new DOLEventHandler(OnBlessingDue));
        GameEventMgr.RemoveHandler(GamePlayerEvent.Revive, new DOLEventHandler(OnBlessingDue));
    }

    private static void OnBlessingDue(DOLEvent e, object sender, EventArgs args)
    {
        // A moment later: at login and at a revive the player is not settled in the world yet.
        if (sender is GamePlayer player)
        {
            new ECSGameTimer(player, _ =>
            {
                ApplyBlessing(player);

                return 0;
            }, BlessingDelayMilliseconds);
        }
    }

    /// <summary>
    /// Nereid's Blessing (owner 2026-10-25: "give everyone the permanent greater nereid buff"): a water breathing buff
    /// with the default swim speed, cast at login and after a death. It shows as a buff icon; a real potion running
    /// already (the same effect) is left alone.
    /// </summary>
    public static void ApplyBlessing(GamePlayer player)
    {
        int value = Value;

        if (player == null || !player.IsAlive || value <= 0 || EffectListService.GetEffectOnTarget(player, eEffect.WaterBreathing) != null)
        {
            return;
        }

        DbSpell spell = new()
        {
            AllowAdd = false,
            SpellID = BlessingSpellId,
            Name = "Nereid's Blessing",
            Description = $"You breathe under water and swim {value}% faster.",
            Icon = _potionIcon > 0 ? _potionIcon : FallbackIcon,
            ClientEffect = 0,
            Value = value,
            Duration = BlessingDurationSeconds,
            Range = 0,
            Target = eSpellTarget.SELF.ToString(),
            Type = eSpellType.WaterBreathing.ToString(),
            Uninterruptible = true,
            Message1 = "You feel the blessing of the Nereids: you can breathe water and swim swiftly."
        };

        try
        {
            ISpellHandler handler = ScriptMgr.CreateSpellHandler(player, new Spell(spell, Math.Max(1, (int) player.Level)), SkillBase.GetSpellLine(GlobalSpellsLines.Potions_Effects));
            handler?.StartSpell(player);
        }
        catch (Exception ex)
        {
            Log.Error($"[FORK] Nereid's Blessing could not be cast on {player.Name}.", ex);
        }
    }
}
