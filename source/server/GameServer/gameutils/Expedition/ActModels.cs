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
