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
    public List<ActObjective> Objectives = new();
    public BossSpec Boss;
    public bool BossSpawned;
    public bool BossKilled;

    // Plans saved before elite packs existed have no "Elites" member; System.Text.Json then keeps this initialiser.
    public List<EliteCamp> Elites = new();

    // Plans saved before shrines existed have none (the initialiser is kept).
    public List<ShrineSpec> Shrines = new();
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
