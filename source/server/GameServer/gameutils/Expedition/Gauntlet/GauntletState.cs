using System;
using System.Text.Json;

namespace DOL.GS.Expedition.Gauntlet;

/// <summary>
/// The saved part of a character's Gauntlet, as JSON in <see cref="OfflineExpeditionRecord.GauntletJson"/>: the round to
/// fight next and the depth (highest round beaten). Prototype: nothing else is saved.
/// </summary>
public sealed class GauntletSave
{
    public int Round = 1;
    public int Depth;

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, ExpeditionJson.Options);
    }

    /// <summary>The saved state, or a fresh one (round 1, depth 0) when the text is empty or unreadable.</summary>
    public static GauntletSave Parse(string json)
    {
        GauntletSave save = null;

        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                save = JsonSerializer.Deserialize<GauntletSave>(json, ExpeditionJson.Options);
            }
            catch (JsonException)
            {
                save = null;
            }
        }

        save ??= new GauntletSave();
        save.Round = Math.Max(1, save.Round);
        save.Depth = Math.Max(0, save.Depth);

        return save;
    }
}

/// <summary>
/// A character's Gauntlet while it is in the game (<see cref="GauntletManager"/> keeps them by character id). Changed
/// under a lock on the state itself.
/// </summary>
public sealed class GauntletState
{
    public string CharacterId;

    /// <summary>The won run's seed; it orders the champions (GauntletChampions.ForRound).</summary>
    public int Seed;

    /// <summary>The round being fought, or the next one during the rest; 1-based.</summary>
    public int Round = 1;

    /// <summary>The highest round beaten.</summary>
    public int Depth;

    /// <summary>The won run's row, loaded at /gauntlet start and saved with <see cref="ToSave"/>.</summary>
    internal OfflineExpeditionRecord Record;

    /// <summary>The champion of the round under way, or null.</summary>
    internal GauntletChampion Champion;

    /// <summary>The rest countdown between rounds, or null.</summary>
    internal ECSGameTimer RestTimer;

    /// <summary>When the rest ends (GameLoop time).</summary>
    internal long RestEndsAt;

    public bool ChampionOut => Champion != null && Champion.ObjectState == GameObject.eObjectState.Active && Champion.IsAlive;

    public bool IsResting => RestTimer != null && RestTimer.IsAlive;

    public GauntletSave ToSave()
    {
        return new GauntletSave { Round = Round, Depth = Depth };
    }
}
