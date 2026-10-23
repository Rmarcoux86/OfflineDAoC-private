using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DOL.GS.Commands;
using DOL.GS.PacketHandler;
using DOL.Logging;

namespace DOL.GS.Fishing;

/// <summary>A named place to fish that /fish go can take a player to.</summary>
public sealed class FishingSpot
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The realm whose territory the spot is in. Players may only travel to their own realm's spots.</summary>
    [JsonIgnore]
    public eRealm Realm { get; set; }

    /// <summary>The realm as a name ("Albion", "Midgard", "Hibernia") so the JSON file is easy to edit by hand.</summary>
    [JsonPropertyName("Realm")]
    public string RealmName
    {
        get => GlobalConstants.RealmToName(Realm);
        set => Realm = Enum.TryParse(value, true, out eRealm realm) ? realm : eRealm.None;
    }

    public ushort RegionID { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public ushort Heading { get; set; }
    public string Description { get; set; } = string.Empty;

    /// <summary>Where the coordinates came from (a data file and row, or the player who marked the spot).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>True for spots added with /fish mark. Only these can be removed with /fish unmark.</summary>
    public bool PlayerAdded { get; set; }
}

/// <summary>
/// The fishing spots of /fish spots and /fish go: the built-in defaults below, always first, then the spots players
/// marked. runtime/server/fishing-spots.json (next to bot-goals.json) holds only the marked spots and is rewritten
/// when a player marks or unmarks one. Built-in spots can't be unmarked, and names are unique across both lists.
/// </summary>
public static class FishingSpots
{
    private static readonly Logger Log = LoggerManager.Create(typeof(FishingSpots));

    public const string FileName = "fishing-spots.json";
    public const int MaxSpots = 100;
    public const int MaxNameLength = 40;

    // Walkable ground only: a spot whose stored point is a little off the mesh (or out in the water) lands on the shore.
    private static readonly EDtPolyFlags[] WalkFilters = [EDtPolyFlags.Walk, EDtPolyFlags.Swim | EDtPolyFlags.Disabled];

    private static readonly eRealm[] RealmOrder = [eRealm.Albion, eRealm.Midgard, eRealm.Hibernia, eRealm.None];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
    };

    private static readonly object Lock = new();
    private static List<FishingSpot> _spots;

    // Set when fishing-spots.json exists but could not be read: only the defaults are used and the file is never
    // overwritten, so a hand edit with a typo doesn't cost the owner their marked spots.
    private static bool _fileUnreadable;

    /// <summary>
    /// The built-in spots. No world database or water map ships in the repository, so every coordinate is a
    /// shore-dwelling creature's camp (the centroid of its CapnBry sightings) from
    /// bots/autonomous/data/capnbry_classic_si_goals.json, or a starting-town location from
    /// scripts/dbupdater/insert/StartupLocation.xml. Zone-local CapnBry points were made global with the zone
    /// offsets in scripts/dbupdater/insert/Zones.xml (offset * 8192). The heading faces a lower or water-dwelling
    /// camp where one is known; otherwise it is 0 and the player turns to the water themselves.
    /// </summary>
    public static IReadOnlyList<FishingSpot> Defaults()
    {
        return
        [
            new FishingSpot
            {
                Name = "Camelot Hills Riverbank",
                Realm = eRealm.Albion,
                RegionID = 1,
                X = 560512,
                Y = 519035,
                Z = 2187,
                Heading = 3867,
                Description = "River spritelings flit over the slow water south of Cotswold; the trout here are lazy and fat.",
                Source = "capnbry_classic_si_goals.json capnbry:0:2:8:river spriteling (Camelot Hills), heading toward capnbry:0:3:10:river drake hatchling"
            },
            new FishingSpot
            {
                Name = "Black Mountains Tarn",
                Realm = eRealm.Albion,
                RegionID = 1,
                X = 516262,
                Y = 455483,
                Z = 1583,
                Heading = 3174,
                Description = "Goblin fishermen guard this dark mountain water jealously, and the eels below know why.",
                Source = "capnbry_classic_si_goals.json capnbry:2:7:5:goblin fisherman (Black Mtns. South), heading toward capnbry:2:9:5:eel (z 1500)"
            },
            new FishingSpot
            {
                Name = "Avalon Marsh Shallows",
                Realm = eRealm.Albion,
                RegionID = 1,
                X = 472871,
                Y = 633231,
                Z = 1730,
                Heading = 573,
                Description = "Carrion crabs pick along the brackish edge where the marsh meets the sea.",
                Source = "capnbry_classic_si_goals.json capnbry:9:13:4:carrion crab (Avalon Marsh), heading toward capnbry:9:11:5:river drake hatchling"
            },
            new FishingSpot
            {
                Name = "East Svealand Strand",
                Realm = eRealm.Midgard,
                RegionID = 100,
                X = 773433,
                Y = 752428,
                Z = 4432,
                Heading = 0,
                Description = "Soft-shelled crabs scuttle over cold shingle; turn to the grey water and cast.",
                Source = "capnbry_classic_si_goals.json capnbry:101:12:3:soft-shelled crab (East Svealand)"
            },
            new FishingSpot
            {
                Name = "Fort Atla",
                Realm = eRealm.Midgard,
                RegionID = 100,
                X = 748436,
                Y = 815916,
                Z = 4408,
                Heading = 3219,
                Description = "The fort sits barely above the waterline; walk the way you face to where the water striders skate.",
                Source = "StartupLocation.xml region 100 (748436, 815916, 4408) in Gotar, heading toward capnbry:103:8:3:water strider (z 4380)"
            },
            new FishingSpot
            {
                Name = "Myrkwood Frog Pond",
                Realm = eRealm.Midgard,
                RegionID = 100,
                X = 794736,
                Y = 895296,
                Z = 4731,
                Heading = 0,
                Description = "Giant bull frogs boom from the reeds of a still forest pond.",
                Source = "capnbry_classic_si_goals.json capnbry:105:9:6:giant bull frog (Myrkwood Forest)"
            },
            new FishingSpot
            {
                Name = "Lough Derg Shore",
                Realm = eRealm.Hibernia,
                RegionID = 200,
                X = 347777,
                Y = 496756,
                Z = 4965,
                Heading = 4089,
                Description = "Water beetle larvae stir the shallows of the great lough, a short walk from Mag Mell.",
                Source = "capnbry_classic_si_goals.json capnbry:200:6:3:water beetle larva (Lough Derg), heading toward capnbry:200:6:3:water beetle collector"
            },
            new FishingSpot
            {
                Name = "Shannon Estuary Sands",
                Realm = eRealm.Hibernia,
                RegionID = 200,
                X = 297239,
                Y = 644178,
                Z = 4832,
                Heading = 0,
                Description = "Sand crabs dig along the estuary flats where the river runs out to the sea.",
                Source = "capnbry_classic_si_goals.json capnbry:202:2:7:sand crab (Shannon Estuary)"
            },
            new FishingSpot
            {
                Name = "Connacht Frog Meadow",
                Realm = eRealm.Hibernia,
                RegionID = 200,
                X = 338423,
                Y = 474991,
                Z = 5311,
                Heading = 2060,
                Description = "Large frogs and beetle larvae crowd a reedy meadow pool in quiet Connacht.",
                Source = "capnbry_classic_si_goals.json capnbry:207:12:13:water beetle larva (Connacht), heading toward capnbry:207:12:9:water beetle collector"
            }
        ];
    }

    public static string Serialize(IEnumerable<FishingSpot> spots)
    {
        return JsonSerializer.Serialize(spots.ToList(), Json);
    }

    public static List<FishingSpot> Deserialize(string json)
    {
        List<FishingSpot> spots = JsonSerializer.Deserialize<List<FishingSpot>>(json, Json) ?? throw new InvalidDataException("The fishing spots file is empty.");
        spots.RemoveAll(spot => spot == null || string.IsNullOrWhiteSpace(spot.Name));

        return spots;
    }

    /// <summary>
    /// The full list: every default first, then each marked spot (flagged PlayerAdded) whose name no earlier spot uses.
    /// </summary>
    public static List<FishingSpot> Merge(IEnumerable<FishingSpot> defaults, IEnumerable<FishingSpot> marked)
    {
        List<FishingSpot> all = defaults.ToList();
        HashSet<string> names = new(all.Select(spot => spot.Name), StringComparer.OrdinalIgnoreCase);

        foreach (FishingSpot spot in marked)
        {
            if (!names.Add(spot.Name))
            {
                Log.Warn($"Fishing: the marked spot '{spot.Name}' in {FileName} has the name of another spot and is skipped.");

                continue;
            }

            spot.PlayerAdded = true;
            all.Add(spot);
        }

        return all;
    }

    /// <summary>The spots in the order /fish spots numbers them: the given realm first, then Albion, Midgard, Hibernia.</summary>
    public static List<FishingSpot> Ordered(IEnumerable<FishingSpot> spots, eRealm firstRealm)
    {
        List<FishingSpot> all = spots.ToList();
        List<FishingSpot> ordered = all.Where(spot => spot.Realm == firstRealm).ToList();

        foreach (eRealm realm in RealmOrder)
        {
            if (realm == firstRealm)
            {
                continue;
            }

            ordered.AddRange(all.Where(spot => spot.Realm == realm));
        }

        // Anything with a realm outside the usual four still gets a number.
        ordered.AddRange(all.Where(spot => !ordered.Contains(spot)));

        return ordered;
    }

    /// <summary>The text of the /fish spots window. Numbers run on across the realm groups.</summary>
    public static List<string> BuildListing(IEnumerable<FishingSpot> spots, eRealm playerRealm, bool canTravelAnywhere)
    {
        List<FishingSpot> ordered = Ordered(spots, playerRealm);
        List<string> lines = [];
        eRealm? currentRealm = null;

        for (int i = 0; i < ordered.Count; i++)
        {
            FishingSpot spot = ordered[i];

            if (currentRealm != spot.Realm)
            {
                if (currentRealm != null)
                {
                    lines.Add(string.Empty);
                }

                currentRealm = spot.Realm;
                string realmName = spot.Realm == eRealm.None ? "Anywhere" : GlobalConstants.RealmToName(spot.Realm);
                lines.Add(spot.Realm == playerRealm || canTravelAnywhere || spot.Realm == eRealm.None ? $"-- {realmName} --" : $"-- {realmName} (enemy territory) --");
            }

            string mine = spot.PlayerAdded ? " (marked)" : string.Empty;
            lines.Add($"{i + 1}. {spot.Name}{mine} - {spot.Description}");
        }

        if (ordered.Count == 0)
        {
            lines.Add("There are no fishing spots yet. Face open water and type /fish mark <name> to add one.");
        }

        lines.Add(string.Empty);
        lines.Add("Type /fish go <number> to travel there.");

        return lines;
    }

    /// <summary>A copy of the current spots, loading them on first use.</summary>
    public static List<FishingSpot> GetAll()
    {
        lock (Lock)
        {
            EnsureLoaded();

            return _spots.ToList();
        }
    }

    public static void ShowList(GamePlayer player)
    {
        List<string> lines = BuildListing(GetAll(), player.Realm, IsGm(player));
        player.Out.SendCustomTextWindow("Fishing Spots", lines);
    }

    /// <summary>/fish go: checks the player can travel, then moves them to the numbered spot.</summary>
    public static bool TryTravel(GamePlayer player, string numberText)
    {
        FishingSpot spot = FindByNumber(player, numberText);

        if (spot == null)
        {
            return false;
        }

        if (!player.IsAlive)
        {
            Tell(player, "You can't travel while dead.");

            return false;
        }

        if (player.InCombat)
        {
            Tell(player, "You can't travel while in combat.");

            return false;
        }

        if (player.Steed != null || player.IsOnHorse || AutonomousPlayerPilot.IsActive(player))
        {
            Tell(player, "You must be off a horse route before travelling to a fishing spot.");

            return false;
        }

        if (spot.Realm != eRealm.None && spot.Realm != player.Realm && !IsGm(player))
        {
            Tell(player, $"{spot.Name} is in {GlobalConstants.RealmToName(spot.Realm)}'s territory. You can only travel to your own realm's fishing spots.");

            return false;
        }

        Region region = WorldMgr.GetRegion(spot.RegionID);
        Zone zone = region?.GetZone(spot.X, spot.Y);

        if (zone == null)
        {
            Tell(player, $"{spot.Name} can't be reached right now.");

            return false;
        }

        int x = spot.X;
        int y = spot.Y;
        int z = spot.Z;
        IPathfindingMgr pathing = PathfindingProvider.Instance;

        // The built-in points are camp centres, so land on the nearest walkable ground rather than in the water or a hillside.
        if (pathing.HasNavmesh(zone))
        {
            Vector3? floor = pathing.GetClosestPoint(zone, new Vector3(x, y, z), 256, 256, 512, WalkFilters);

            if (floor.HasValue)
            {
                x = (int) floor.Value.X;
                y = (int) floor.Value.Y;
                z = (int) floor.Value.Z;
            }
        }

        // Stop fishing first (that removes the bobber and stands up a player fishing sat down), then stand up anyone
        // still sitting, then move: nobody arrives seated or leaves a bobber behind.
        if (FishingManager.IsFishing(player))
        {
            FishingManager.Stop(player, "You reel in your line.");
        }

        if (player.IsSitting)
        {
            player.Sit(false);
        }

        PlayerMobNavigator.Stop(player, string.Empty, false);

        if (PlayerCompanionGrind.IsActive(player))
        {
            PlayerCompanionGrind.Stop(player, "you travelled to a fishing spot");
        }

        if (!player.MoveTo(spot.RegionID, x, y, z, spot.Heading))
        {
            Tell(player, $"Could not travel to {spot.Name}.");

            return false;
        }

        Tell(player, $"You arrive at {spot.Name}. {spot.Description}");

        return true;
    }

    /// <summary>/fish mark: records where the player stands and faces as a new spot, if they face open water.</summary>
    public static bool TryMark(GamePlayer player, string name)
    {
        name = (name ?? string.Empty).Trim();

        if (name.Length == 0 || name.Length > MaxNameLength)
        {
            Tell(player, $"Give the spot a name of 1 to {MaxNameLength} letters: /fish mark <name>");

            return false;
        }

        if (!player.IsAlive || player.IsSwimming || player.Steed != null || player.IsOnHorse)
        {
            Tell(player, "Stand on the shore, facing the water, to mark a fishing spot.");

            return false;
        }

        if (!FishingManager.TryFindCastPoint(player, out _, false))
        {
            Tell(player, "You need to face open water to mark a fishing spot here.");

            return false;
        }

        lock (Lock)
        {
            EnsureLoaded();

            if (_fileUnreadable)
            {
                Tell(player, $"{FileName} could not be read, so new spots can't be saved. Fix or remove the file and restart the server.");

                return false;
            }

            if (_spots.Count(spot => spot.PlayerAdded) >= MaxSpots)
            {
                Tell(player, $"There are already {MaxSpots} marked fishing spots. Remove one with /fish unmark <number> first.");

                return false;
            }

            if (_spots.Any(spot => spot.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                Tell(player, $"There is already a fishing spot called {name}.");

                return false;
            }

            string place = player.CurrentZone?.Description ?? player.CurrentRegion?.Description ?? "the wilds";
            FishingSpot spot = new()
            {
                Name = name,
                Realm = player.Realm,
                RegionID = player.CurrentRegionID,
                X = player.X,
                Y = player.Y,
                Z = player.Z,
                Heading = player.Heading,
                Description = $"A spot {player.Name} found in {place}.",
                Source = $"/fish mark by {player.Name} on {DateTime.Now:yyyy-MM-dd}",
                PlayerAdded = true
            };

            _spots.Add(spot);

            if (!TrySave())
            {
                _spots.Remove(spot);
                Tell(player, "The fishing spot could not be saved.");

                return false;
            }
        }

        Tell(player, $"You mark this place as the fishing spot {name}. Type /fish spots to see it.");

        return true;
    }

    /// <summary>/fish unmark: removes a spot added with /fish mark. Built-in spots can't be removed.</summary>
    public static bool TryUnmark(GamePlayer player, string numberText)
    {
        FishingSpot spot = FindByNumber(player, numberText);

        if (spot == null)
        {
            return false;
        }

        if (!spot.PlayerAdded)
        {
            Tell(player, $"{spot.Name} is a built-in fishing spot and can't be removed.");

            return false;
        }

        lock (Lock)
        {
            if (_fileUnreadable)
            {
                Tell(player, $"{FileName} could not be read, so spots can't be changed. Fix or remove the file and restart the server.");

                return false;
            }

            int index = _spots.IndexOf(spot);

            if (index < 0)
            {
                Tell(player, "That fishing spot has already been removed.");

                return false;
            }

            _spots.RemoveAt(index);

            if (!TrySave())
            {
                _spots.Insert(index, spot);
                Tell(player, "The fishing spot could not be removed.");

                return false;
            }
        }

        Tell(player, $"You remove the fishing spot {spot.Name}.");

        return true;
    }

    // Resolves the number shown in this player's /fish spots window.
    private static FishingSpot FindByNumber(GamePlayer player, string numberText)
    {
        List<FishingSpot> ordered = Ordered(GetAll(), player.Realm);

        if (ordered.Count == 0)
        {
            Tell(player, "There are no fishing spots yet. Face open water and type /fish mark <name> to add one.");

            return null;
        }

        if (!int.TryParse(numberText, out int number) || number < 1 || number > ordered.Count)
        {
            Tell(player, $"Choose a spot number from 1 to {ordered.Count}. Type /fish spots to see the list.");

            return null;
        }

        return ordered[number - 1];
    }

    private static bool IsGm(GamePlayer player)
    {
        return player.Client?.Account != null && player.Client.Account.PrivLevel > (uint) ePrivLevel.Player;
    }

    private static void Tell(GamePlayer player, string message)
    {
        player.Out.SendMessage(message, eChatType.CT_System, eChatLoc.CL_SystemWindow);
    }

    // Caller holds Lock.
    private static void EnsureLoaded()
    {
        if (_spots != null)
        {
            return;
        }

        string path = FilePath();

        if (path == null || !File.Exists(path))
        {
            _spots = Defaults().ToList();

            return;
        }

        try
        {
            _spots = Merge(Defaults(), Deserialize(File.ReadAllText(path)));
        }
        catch (Exception e)
        {
            Log.Error($"Fishing: could not read {path}; using the built-in fishing spots and leaving the file untouched.", e);
            _spots = Defaults().ToList();
            _fileUnreadable = true;
        }
    }

    // Caller holds Lock. Writes through a temporary file so a crash mid-write never leaves half a file.
    private static bool TrySave()
    {
        string path = FilePath();

        if (path == null)
        {
            return false;
        }

        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllText(temporary, Serialize(_spots.Where(spot => spot.PlayerAdded)));
            File.Move(temporary, path, true);

            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Fishing: could not save {path}.", e);

            return false;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    // runtime/server, the same folder bot-goals.json is read from (GameServer.cs passes RootDirectory to AutonomousBotGoalPolicy).
    private static string FilePath()
    {
        string root = GameServer.Instance?.Configuration?.RootDirectory;

        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, FileName);
    }
}
