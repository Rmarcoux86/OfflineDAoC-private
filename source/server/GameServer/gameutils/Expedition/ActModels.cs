using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DOL.GS.Expedition;

public enum eObjectiveKind
{
    ClearCamp,
    KillNamed,
    ReachPoint
}

/// <summary>
/// One act objective. Coordinates are global (zone offset already applied).
/// Description is progress-free; DescribePlan appends "(Progress/Count)".
/// </summary>
public sealed class ActObjective
{
    public string Id = string.Empty;
    public eObjectiveKind Kind;
    public string MobName = string.Empty;
    public int Count;
    public int Progress;
    public int RegionId;
    public int X, Y, Z;
    public int Radius;
    public string Description = string.Empty;

    public bool Done => Progress >= Count;
}

/// <summary>
/// The act boss: a buffed copy of a catalog mob. Coordinates are global.
/// </summary>
public sealed class BossSpec
{
    public string BaseMobName = string.Empty;
    public int Level;
    public int RegionId;
    public int X, Y, Z;
    public string Title = string.Empty;
    public int HealthMultiplier;
}

/// <summary>
/// An elite pack: a world camp whose monsters <see cref="ExpeditionElites"/> buffs with an affix while the run's
/// player is near. Coordinates are global; Level is the generated pack level (the camp's top level + 2).
/// </summary>
public sealed class EliteCamp
{
    public string MobName = string.Empty;
    public string Affix = string.Empty;
    public int RegionId;
    public int X, Y, Z;
    public int Radius;
    public int Level;
    public string Description = string.Empty;

    /// <summary>
    /// The spawns (GameNPC.InternalID) of this camp's elites that have died this act. A later login still makes their
    /// respawns elite (owner 2026-10-28: farming them is wanted), but as repeat kills with lesser rewards
    /// (<see cref="ExpeditionElites.IsRepeat"/>).
    /// </summary>
    public List<string> Slain = new();

    /// <summary>
    /// Hard mode (owner 2026-10-29: "a hard-run elite rolls TWO different affixes"): the affixes after
    /// <see cref="Affix"/>. Empty in a normal run, and in plans saved before it existed (the initialiser is kept).
    /// </summary>
    public List<string> ExtraAffixes = new();
}

/// <summary>
/// A generated act. All members are public fields, so System.Text.Json only
/// (de)serializes them with IncludeFields = true: always serialize with
/// <see cref="ExpeditionJson.Options"/>.
/// </summary>
public sealed class ActPlan
{
    public int Act;
    public int MinLevel, MaxLevel;
    public string ZoneName = string.Empty;
    public int ZoneId;
    public int RegionId;
    public int CampX, CampY, CampZ;
    public ushort CampHeading;

    /// <summary>
    /// The camp-safety rules (<see cref="ExpeditionArrival.CampSafetyRules"/>) this camp was last checked under. Plans
    /// saved before the check existed have no member and read as 0, so they are checked once at their next use.
    /// </summary>
    public int CampSafetyVersion;

    public List<ActObjective> Objectives = new();
    public BossSpec Boss;
    public bool BossSpawned;
    public bool BossKilled;

    // Plans saved before elite packs existed have no "Elites" member; System.Text.Json then keeps this initialiser.
    public List<EliteCamp> Elites = new();

    // Plans saved before shrines existed have none (the initialiser is kept).
    public List<ShrineSpec> Shrines = new();

    /// <summary>
    /// Hard mode: every camp of the act was made an elite pack with two affixes (<see cref="ExpeditionHardMonsters.HardenPlan"/>).
    /// False in a normal run.
    /// </summary>
    public bool HardElites;

    /// <summary>
    /// The unique weapon exchanger's spot (ExpeditionExchanger), found in the world the first time the act is played
    /// and kept: in the zone's town when it has one, else beside the camp. Coordinates are global.
    /// </summary>
    public bool ExchangerPlaced;
    public bool ExchangerInTown;
    public int ExchangerX, ExchangerY, ExchangerZ;
}

/// <summary>
/// A wild shrine (<see cref="ExpeditionShrines"/>): found by walking near it, used by touching it, after which its buff
/// group lasts the rest of the act. Coordinates are global.
/// </summary>
public sealed class ShrineSpec
{
    public string Group = string.Empty;
    public int RegionId;
    public int X, Y, Z;
    public bool Guarded;
    public string Direction = string.Empty;
    public bool Discovered;
    public bool Used;

    /// <summary>
    /// Act 5 (owner 2026-10-27): the shrine stands just outside a monster camp, dormant until <see cref="KillsNeeded"/>
    /// of that camp's monster (<see cref="CampMob"/>, near <see cref="CampX"/>, <see cref="CampY"/>) are slain.
    /// KillsNeeded 0 is an ordinary shrine. <see cref="Placed"/> is set once the camp and the spot are chosen in the world.
    /// </summary>
    public bool Placed;
    public string CampMob = string.Empty;
    public int CampX, CampY, CampZ;
    public int KillsNeeded;
    public int Kills;

    /// <summary>Which placement rule placed the shrine (ExpeditionShrines.DormantPlacementVersion); an older one is placed again.</summary>
    public int PlacementVersion;
}

/// <summary>
/// The required System.Text.Json options for every expedition model.
/// IncludeFields serializes the public fields; IgnoreReadOnlyProperties skips the
/// computed ActObjective.Done; enums are written by name so reordering an enum
/// can't corrupt saved runs.
/// </summary>
public static class ExpeditionJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
