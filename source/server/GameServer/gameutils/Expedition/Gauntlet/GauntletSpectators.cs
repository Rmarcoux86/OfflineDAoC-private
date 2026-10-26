using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using DOL.AI.Brain;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.GS.Realm;
using DOL.Logging;

namespace DOL.GS.Expedition.Gauntlet;

/// <summary>One worn item's look in a spectator snapshot (an eInventorySlot visible slot).</summary>
public sealed record SpectatorItem(int Slot, int Model, int Color, int Effect, int Extension);

/// <summary>
/// A spectator: the look of a character that cleared the Gauntlet, submitted from its player's launcher. Gender is
/// DOLCharacters.Gender (0 male, 1 female); Model is the player's model with its size bits (0 = take the race's);
/// ActiveWeapon is eActiveWeaponSlot (0 one-hand, 1 two-hand, 2 ranged).
/// </summary>
public sealed record SpectatorEntry(
    string Id,
    string Name,
    int ClassId,
    int Race,
    int Gender,
    int Realm,
    int Model,
    bool HoodUp,
    int ActiveWeapon,
    IReadOnlyList<SpectatorItem> Items);

/// <summary>Where a spectator stands and the heading that faces the arena centre.</summary>
public readonly record struct SpectatorSpot(int X, int Y, int Z, ushort Heading);

/// <summary>
/// Gauntlet spectators (owner 2026-10-26, replacing the guest round): players' Gauntlet champions, submitted from the
/// launcher (SUBMIT AS SPECTATOR) through the repository's gauntlet/guest-champions.json, which the fork update installs
/// as runtime/server/gauntlet-spectators.json. While a Gauntlet session is active up to <see cref="MaxSpectators"/> of
/// them stand on a ring around the arena centre, outside the fight, facing it: peaceful NPCs that cannot be attacked,
/// never fight, and show only a name, body and gear look. They go when the session stops. A missing or invalid file
/// means no spectators.
/// </summary>
public static class GauntletSpectators
{
    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>In the server's root directory (runtime/server), beside bot-goals.json.</summary>
    public const string FileName = "gauntlet-spectators.json";

    public const int MaxSpectators = 8;

    /// <summary>
    /// Distance from the arena centre. The player and gate spots are about 850 from the centre, so the ring stands just
    /// behind the fight; the champion's leash (2,500 from the gate) never matters as spectators can't be attacked.
    /// Assumption (not seen in game): Celestius' floor reaches this far around the centre at the same height as both of
    /// the owner's /loc readings (Z 15733); if a spot is off the platform the spectator stands in space.
    /// </summary>
    public const int RingRadius = 1200;

    public const string GuildName = "Hall of Champions";

    /// <summary>
    /// The ring's places, in degrees from the player-to-gate line, filled in this order: the four nearest the sides of
    /// the fight first (the best view), then the four nearer its ends. None is within 22.5 degrees of the line.
    /// </summary>
    private static readonly double[] SlotDegrees = [67.5, 112.5, 247.5, 292.5, 22.5, 157.5, 202.5, 337.5];

    // The worn slots an NPC shows (GameLivingInventory.VISIBLE_SLOTS).
    private static readonly int[] VisibleSlots = [10, 11, 12, 13, 21, 22, 23, 25, 26, 27, 28];

    // A DAoC character name: letters, an apostrophe, a space or a hyphen (the intake workflow's rule).
    private static readonly Regex NamePattern = new("^[A-Za-z][A-Za-z' -]{0,31}$", RegexOptions.CultureInvariant);

    private static readonly object Sync = new();
    private static IReadOnlyList<SpectatorEntry> _entries = [];
    private static readonly List<GameNPC> Spawned = new();

    /// <summary>The spectators read at server start.</summary>
    public static IReadOnlyList<SpectatorEntry> Entries
    {
        get
        {
            lock (Sync)
            {
                return _entries;
            }
        }
    }

    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        string root = GameServer.Instance?.Configuration?.RootDirectory;

        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        Load(Path.Combine(root, FileName));
    }

    [GameServerStoppedEvent]
    public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
    {
        Hide();
    }

    /// <summary>Reads the spectators file; a missing or invalid file leaves none (logged).</summary>
    public static void Load(string path)
    {
        IReadOnlyList<SpectatorEntry> entries = null;

        try
        {
            if (File.Exists(path))
            {
                entries = Parse(File.ReadAllText(path));

                if (entries == null && Log.IsWarnEnabled)
                {
                    Log.Warn($"[GAUNTLET] {path} is not a spectators file ({{\"entries\": [...]}}); the Gauntlet has no spectators.");
                }
            }
            else if (Log.IsInfoEnabled)
            {
                Log.Info($"[GAUNTLET] No {path}; the Gauntlet has no spectators.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Log.IsWarnEnabled)
            {
                Log.Warn($"[GAUNTLET] {path} could not be read; the Gauntlet has no spectators.", ex);
            }
        }

        lock (Sync)
        {
            _entries = entries ?? [];
        }

        if (entries != null && Log.IsInfoEnabled)
        {
            Log.Info($"[GAUNTLET] {entries.Count} spectator(s) loaded from {path}.");
        }
    }

    /// <summary>
    /// The spectators in a gauntlet/guest-champions.json ({ "entries": [ ... ] }). An entry without an id, a valid
    /// character name, or a body (a model, or a known race) is skipped; a repeated id keeps its first entry; items
    /// outside the visible slots, without a model, or repeating a slot are dropped; unknown fields are ignored. Null when
    /// the text is not such an object.
    /// </summary>
    public static IReadOnlyList<SpectatorEntry> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entries", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            List<SpectatorEntry> result = new();
            HashSet<string> ids = new(StringComparer.Ordinal);

            foreach (JsonElement element in entries.EnumerateArray())
            {
                SpectatorEntry entry = element.ValueKind == JsonValueKind.Object ? ReadEntry(element) : null;

                if (entry != null && ids.Add(entry.Id))
                {
                    result.Add(entry);
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SpectatorEntry ReadEntry(JsonElement element)
    {
        string id = Text(element, "id")?.Trim();
        string name = Text(element, "name")?.Trim();

        if (string.IsNullOrEmpty(id) || name == null || !NamePattern.IsMatch(name))
        {
            return null;
        }

        int race = Number(element, "race");
        int gender = Number(element, "gender") == 1 ? 1 : 0;
        int model = Number(element, "model");

        if ((model <= 0 || model > ushort.MaxValue) && RaceModel(race, gender) == 0)
        {
            return null;
        }

        List<SpectatorItem> items = new();

        if (element.TryGetProperty("items", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                int slot = Number(item, "slot");
                int itemModel = Number(item, "model");

                if (!VisibleSlots.Contains(slot) || itemModel <= 0 || itemModel > ushort.MaxValue || items.Any(added => added.Slot == slot))
                {
                    continue;
                }

                items.Add(new SpectatorItem(slot, itemModel, Math.Max(0, Number(item, "color")), Math.Max(0, Number(item, "effect")), Math.Clamp(Number(item, "extension"), 0, 255)));
            }
        }

        int weapon = Number(element, "activeWeapon");

        return new SpectatorEntry(
            id,
            name,
            Number(element, "classId"),
            race,
            gender,
            Number(element, "realm"),
            model is > 0 and <= ushort.MaxValue ? model : 0,
            element.TryGetProperty("hoodUp", out JsonElement hood) && hood.ValueKind == JsonValueKind.True,
            weapon is >= 0 and <= 2 ? weapon : 0,
            items);
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Whole numbers that fit an int; anything else is 0.
    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : 0;

    /// <summary>The race's model for the gender (0 male, 1 female), or 0 when the race is not a player race.</summary>
    public static ushort RaceModel(int race, int gender)
    {
        PlayerRace playerRace = PlayerRace.AllRaces.FirstOrDefault(candidate => (int) candidate.ID == race);

        return playerRace == null ? (ushort) 0 : (ushort) playerRace.GetModel(gender == 1 ? eGender.Female : eGender.Male);
    }

    /// <summary>
    /// The NPC model and size for a spectator: a player's model carries its size in bits 11-12 (GamePlayer.Size: 0x800
    /// short, 0x1800 tall), an NPC has a separate size; no model takes the race's.
    /// </summary>
    public static (ushort Model, byte Size) Body(SpectatorEntry entry)
    {
        if (entry.Model <= 0)
        {
            return (RaceModel(entry.Race, entry.Gender), 50);
        }

        int size = entry.Model & 0x1800;
        byte npcSize = size == 0x800 ? (byte) 45 : size == 0x1800 ? (byte) 55 : (byte) 50;

        return ((ushort) (entry.Model & 0x7FF), npcSize);
    }

    /// <summary>
    /// Up to <paramref name="count"/> spectators in a random order fixed by <paramref name="seed"/>, leaving out the
    /// character now in the Gauntlet (it would watch itself).
    /// </summary>
    public static List<SpectatorEntry> Pick(IReadOnlyList<SpectatorEntry> entries, string excludedId, int seed, int count)
    {
        List<SpectatorEntry> pool = (entries ?? []).Where(entry => entry != null && !string.Equals(entry.Id, excludedId, StringComparison.Ordinal)).ToList();
        Random random = new(seed);

        // Fisher-Yates.
        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        return pool.Take(Math.Clamp(count, 0, MaxSpectators)).ToList();
    }

    /// <summary>
    /// <paramref name="count"/> spots (at most <see cref="MaxSpectators"/>) on a ring of <paramref name="radius"/> around
    /// the centre, placed by <see cref="SlotDegrees"/> from the line through the centre towards
    /// (<paramref name="axisX"/>, <paramref name="axisY"/>), each with the heading that faces the centre.
    /// </summary>
    public static List<SpectatorSpot> Ring(int count, int centreX, int centreY, int z, int radius, int axisX, int axisY)
    {
        List<SpectatorSpot> spots = new();
        double axis = Math.Atan2(axisY - centreY, axisX - centreX);

        for (int i = 0; i < Math.Min(count, Math.Min(MaxSpectators, SlotDegrees.Length)); i++)
        {
            double angle = axis + SlotDegrees[i] * Math.PI / 180.0;
            int x = centreX + (int) Math.Round(radius * Math.Cos(angle));
            int y = centreY + (int) Math.Round(radius * Math.Sin(angle));

            // Point2D.GetHeading: 0 faces +Y and the heading grows clockwise (4096 a turn).
            double heading = Math.Atan2(-(centreX - x), centreY - y) * Point2D.RADIAN_TO_HEADING;

            if (heading < 0)
            {
                heading += 4096;
            }

            spots.Add(new SpectatorSpot(x, y, z, (ushort) ((int) Math.Round(heading) % 4096)));
        }

        return spots;
    }

    /// <summary>
    /// Shows the spectators for a Gauntlet session (GauntletManager.TryStart): a random pick per session, placed on the
    /// ring. Does nothing while they are already out (one arena, one session at a time).
    /// </summary>
    public static void Show(GamePlayer challenger)
    {
        lock (Sync)
        {
            if (Spawned.Count > 0 || _entries.Count == 0 || !GauntletArena.IsConfigured)
            {
                return;
            }

            int seed = Random.Shared.Next();
            List<SpectatorEntry> picked = Pick(_entries, challenger?.ObjectId, seed, MaxSpectators);
            List<SpectatorSpot> spots = Ring(picked.Count, GauntletArena.CentreX, GauntletArena.CentreY, GauntletArena.CentreZ, RingRadius,
                GauntletArena.GateX, GauntletArena.GateY);

            for (int i = 0; i < spots.Count; i++)
            {
                GameNPC spectator = null;

                try
                {
                    spectator = Create(picked[i], spots[i]);

                    if (spectator.AddToWorld())
                    {
                        Spawned.Add(spectator);
                    }
                    else
                    {
                        spectator.Delete();
                    }
                }
                catch (Exception ex)
                {
                    spectator?.Delete();

                    if (Log.IsErrorEnabled)
                    {
                        Log.Error($"[GAUNTLET] Spectator {picked[i].Name} ({picked[i].Id}) could not be placed.", ex);
                    }
                }
            }

            if (Log.IsInfoEnabled)
            {
                Log.Info($"[GAUNTLET] {Spawned.Count} spectator(s) watch {challenger?.Name}'s Gauntlet (seed {seed}).");
            }
        }
    }

    /// <summary>Removes the spectators (GauntletManager.Stop, server stop).</summary>
    public static void Hide()
    {
        List<GameNPC> spectators;

        lock (Sync)
        {
            spectators = new List<GameNPC>(Spawned);
            Spawned.Clear();
        }

        foreach (GameNPC spectator in spectators)
        {
            spectator.Delete();
        }
    }

    /// <summary>A champion fell (GauntletManager.OnChampionDied): one spectator cheers.</summary>
    public static void Cheer()
    {
        GameNPC spectator;

        lock (Sync)
        {
            if (Spawned.Count == 0)
            {
                return;
            }

            spectator = Spawned[Random.Shared.Next(Spawned.Count)];
        }

        foreach (GamePlayer watcher in spectator.GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
        {
            watcher.Out.SendEmoteAnimation(spectator, eEmote.Cheer);
        }
    }

    /// <summary>
    /// The spectator NPC: no brain (it never thinks, aggroes or moves), PEACE (AbstractServerRules.IsAllowedToAttack
    /// refuses any attack by or on it, and the client shows it as friendly), no realm, no respawn, no loot; dressed with
    /// the snapshot's item looks.
    /// </summary>
    private static GameNPC Create(SpectatorEntry entry, SpectatorSpot spot)
    {
        (ushort model, byte size) = Body(entry);
        GameNPC spectator = new(new BlankBrain())
        {
            Name = entry.Name,
            GuildName = GuildName,
            Realm = eRealm.None,
            Level = 50,
            Model = model,
            Size = size,
            Flags = GameNPC.eFlags.PEACE,
            RespawnInterval = -1,
            X = spot.X,
            Y = spot.Y,
            Z = spot.Z,
            Heading = spot.Heading,
            CurrentRegionID = GauntletArena.RegionId
        };

        if (entry.Items.Count > 0)
        {
            GameNpcInventoryTemplate template = new();

            foreach (SpectatorItem item in entry.Items)
            {
                template.AddNPCEquipment((eInventorySlot) item.Slot, item.Model, item.Color, item.Effect, item.Extension);
            }

            spectator.Inventory = template.CloseTemplate();
            spectator.SwitchWeapon((eActiveWeaponSlot) entry.ActiveWeapon);
        }

        spectator.IsCloakHoodUp = entry.HoodUp;

        return spectator;
    }
}
