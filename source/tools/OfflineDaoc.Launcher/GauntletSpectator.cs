using System.Text;
using System.Text.Json;

namespace OfflineDaoc.Launcher;

/// <summary>
/// Gauntlet spectators (owner 2026-10-26): a character that cleared the Gauntlet can be submitted as a spectator, a
/// peaceful NPC that watches from the edge of every player's Gauntlet arena. The snapshot is the character's look only:
/// name, class, race, gender, realm, body model, hood, and each worn visible item's model, color, glow (effect) and
/// extension. No stats. It goes out as a pre-filled "[Gauntlet Champion]" issue; the hall-of-fame workflow adds it to
/// gauntlet/guest-champions.json, which the fork update installs as runtime\server\gauntlet-spectators.json.
/// This file is the pure logic (snapshot, JSON, URL, candidates) plus the submitted-ids file.
/// </summary>
internal static class GauntletSpectator
{
    internal const string IssueTitlePrefix = "[Gauntlet Champion] ";
    // The character ids this PC already submitted, one per line, so SUBMIT AS SPECTATOR doesn't offer them again.
    internal const string SubmittedIdsFileName = "gauntlet-spectators-submitted.txt";
    // Browsers and GitHub accept pre-filled issue URLs up to about 8,000 characters.
    internal const int MaxIssueUrlLength = 8000;
    internal const int HeadSlot = 21;
    internal const int CloakSlot = 26;

    // The worn slots an NPC can show (eInventorySlot: hands, two-hand, ranged, head, gloves, boots, torso, cloak, legs, arms).
    internal static readonly int[] VisibleSlots = [10, 11, 12, 13, 21, 22, 23, 25, 26, 27, 28];

    internal sealed record Item(int Slot, int Model, int Color, int Effect, int Extension);

    /// <summary>
    /// What the server needs to dress a spectator. Gender is DOLCharacters.Gender (0 male, 1 female); Model is the
    /// character's model with its size bits; ActiveWeapon is eActiveWeaponSlot (0 one-hand, 1 two-hand, 2 ranged).
    /// </summary>
    internal sealed record Snapshot(
        string Id,
        string Name,
        int ClassId,
        int Race,
        int Gender,
        int Realm,
        int Model,
        bool HoodUp,
        int ActiveWeapon,
        IReadOnlyList<Item> Items);

    /// <summary>
    /// The snapshot from a DOLCharacters row and its worn Inventory rows: only visible slots with a model, the first item
    /// per slot, in slot order; a hidden helm or cloak is left out; CurrentModel, or CreationModel when it is 0.
    /// </summary>
    internal static Snapshot Build(
        string characterId,
        string name,
        int classId,
        int race,
        int gender,
        int realm,
        int currentModel,
        int creationModel,
        bool hoodUp,
        bool helmHidden,
        bool cloakHidden,
        int activeWeaponSlot,
        IEnumerable<Item> worn)
    {
        var items = new List<Item>();
        var slots = new HashSet<int>();

        foreach (Item item in worn.OrderBy(item => item.Slot))
        {
            bool hidden = (item.Slot == HeadSlot && helmHidden) || (item.Slot == CloakSlot && cloakHidden);

            if (hidden || item.Model <= 0 || !VisibleSlots.Contains(item.Slot) || !slots.Add(item.Slot))
            {
                continue;
            }

            items.Add(item with { Color = Math.Max(0, item.Color), Effect = Math.Max(0, item.Effect), Extension = Math.Clamp(item.Extension, 0, 255) });
        }

        // DOLCharacters.ActiveWeaponSlot is the weapon slot ORed with the quiver slot (high bits).
        int weapon = activeWeaponSlot & 0x0F;

        return new Snapshot(
            characterId.Trim(),
            name.Trim(),
            classId,
            race,
            gender == 1 ? 1 : 0,
            realm,
            currentModel > 0 ? currentModel : creationModel,
            hoodUp,
            weapon is >= 0 and <= 2 ? weapon : 0,
            items);
    }

    /// <summary>The character id of a Hall of Fame entry ("characterId:endedUtc"; character ids have no colon).</summary>
    internal static string CharacterIdOf(HallOfFame.Entry entry)
    {
        int colon = entry.Id.IndexOf(':');

        return colon > 0 ? entry.Id[..colon] : entry.Id;
    }

    /// <summary>
    /// The local wins whose character cleared the Gauntlet and was not submitted from this PC yet, one per character
    /// (its newest win), newest first.
    /// </summary>
    internal static IReadOnlyList<HallOfFame.Entry> Candidates(IReadOnlyList<HallOfFame.Entry> local, ISet<string> submittedIds) =>
        local
            .Where(entry => entry.GauntletCleared && !submittedIds.Contains(CharacterIdOf(entry)))
            .OrderByDescending(entry => HallOfFame.EndedAt(entry.EndedUtc))
            .GroupBy(CharacterIdOf, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

    /// <summary>The JSON object the workflow reads from the issue, compact so the pre-filled URL stays short.</summary>
    internal static string SubmissionJson(Snapshot snapshot)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", snapshot.Id);
            writer.WriteString("name", snapshot.Name);
            writer.WriteNumber("classId", snapshot.ClassId);
            writer.WriteNumber("race", snapshot.Race);
            writer.WriteNumber("gender", snapshot.Gender);
            writer.WriteNumber("realm", snapshot.Realm);
            writer.WriteNumber("model", snapshot.Model);
            writer.WriteBoolean("hoodUp", snapshot.HoodUp);
            writer.WriteNumber("activeWeapon", snapshot.ActiveWeapon);
            writer.WriteStartArray("items");

            foreach (Item item in snapshot.Items)
            {
                writer.WriteStartObject();
                writer.WriteNumber("slot", item.Slot);
                writer.WriteNumber("model", item.Model);
                writer.WriteNumber("color", item.Color);
                writer.WriteNumber("effect", item.Effect);
                writer.WriteNumber("extension", item.Extension);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static string IssueTitle(Snapshot snapshot) => IssueTitlePrefix + snapshot.Name;

    internal static string IssueBody(Snapshot snapshot) =>
        "My Offline DAoC Gauntlet champion, to stand as a spectator at the edge of the Gauntlet arena (looks only: name, "
        + "body and gear appearance). Submit this issue as it is: a bot checks the block below, adds the champion to the "
        + "shared list and closes the issue.\n\n```json\n" + SubmissionJson(snapshot) + "\n```\n";

    /// <summary>The "new issue" page of the repository, pre-filled with the spectator.</summary>
    internal static string IssueUrl(string repository, Snapshot snapshot) =>
        $"https://github.com/{repository}/issues/new?title={Uri.EscapeDataString(IssueTitle(snapshot))}&body={Uri.EscapeDataString(IssueBody(snapshot))}";

    /// <summary>The character ids in runtime\gauntlet-spectators-submitted.txt (empty when missing or unreadable).</summary>
    internal static HashSet<string> ReadSubmittedIds(string runtimeFolder)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string file = Path.Combine(runtimeFolder, SubmittedIdsFileName);

        try
        {
            if (File.Exists(file))
            {
                ids.UnionWith(HallOfFame.ParseSentIds(File.ReadAllText(file)));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing remembered: the button may offer the character again, which is harmless (the workflow dedupes).
        }

        return ids;
    }

    /// <summary>Adds the character id to runtime\gauntlet-spectators-submitted.txt; failures are logged.</summary>
    internal static void RememberSubmitted(string runtimeFolder, string characterId, string logsDirectory)
    {
        try
        {
            File.AppendAllText(Path.Combine(runtimeFolder, SubmittedIdsFileName), characterId + Environment.NewLine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            HallOfFame.Log(logsDirectory, $"Could not remember the submitted spectator {characterId}: {exception.Message}");
        }
    }

    /// <summary>"Aldric (level 50 Celt Hero)" for the picker.</summary>
    internal static string PickerText(HallOfFame.Entry entry)
    {
        string race = entry.Race is null ? string.Empty : entry.Race + " ";

        return $"{entry.Character} (level {entry.Level} {race}{entry.ClassName})";
    }
}
