using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DOL.GS.Expedition.Gauntlet;
using NUnit.Framework;

namespace DOL.GS.Tests;

/// <summary>
/// Gauntlet spectators' pure parts: reading the spectators file the launcher's submissions build, the per-session pick,
/// the NPC body from a player model, and the ring of places around the arena. No server is needed.
/// </summary>
[TestFixture]
public class UT_ExpeditionGauntletSpectators
{
    private const string Aldric = """
        { "id": "char-1", "name": "Aldric", "classId": 2, "race": 1, "gender": 0, "realm": 1, "model": 4130, "hoodUp": true,
          "activeWeapon": 1, "player": "someone", "submittedUtc": "2026-10-26T09:00:00Z",
          "items": [
            { "slot": 25, "model": 1003, "color": 43, "effect": 0, "extension": 4 },
            { "slot": 12, "model": 932, "color": 0, "effect": 50, "extension": 0 },
            { "slot": 26, "model": 559, "color": 12 }
          ] }
        """;

    private static IReadOnlyList<SpectatorEntry> Parse(params string[] entries) =>
        GauntletSpectators.Parse("{ \"entries\": [" + string.Join(",", entries) + "] }");

    private static SpectatorEntry Entry(string id, int model = 4130) =>
        new(id, "Name", 2, 1, 0, 1, model, false, 0, new List<SpectatorItem>());

    [Test]
    public void Parse_ReadsEveryFieldOfASubmission()
    {
        IReadOnlyList<SpectatorEntry> entries = Parse(Aldric);

        Assert.That(entries, Has.Count.EqualTo(1));
        SpectatorEntry entry = entries[0];

        Assert.Multiple(() =>
        {
            Assert.That(entry.Id, Is.EqualTo("char-1"));
            Assert.That(entry.Name, Is.EqualTo("Aldric"));
            Assert.That(entry.ClassId, Is.EqualTo(2));
            Assert.That(entry.Race, Is.EqualTo(1));
            Assert.That(entry.Gender, Is.EqualTo(0));
            Assert.That(entry.Realm, Is.EqualTo(1));
            Assert.That(entry.Model, Is.EqualTo(4130));
            Assert.That(entry.HoodUp, Is.True);
            Assert.That(entry.ActiveWeapon, Is.EqualTo(1));
            Assert.That(entry.Items.Select(item => item.Slot), Is.EqualTo(new[] { 25, 12, 26 }));
            Assert.That(entry.Items[0], Is.EqualTo(new SpectatorItem(25, 1003, 43, 0, 4)));
            Assert.That(entry.Items[1].Effect, Is.EqualTo(50));
            Assert.That(entry.Items[2], Is.EqualTo(new SpectatorItem(26, 559, 12, 0, 0)), "missing look fields are 0");
        });
    }

    [Test]
    public void Parse_FillsDefaultsForMissingOptionalFields()
    {
        SpectatorEntry entry = Parse("""{ "id": " c2 ", "name": " Brynn ", "classId": 5, "model": 286 }""").Single();

        Assert.Multiple(() =>
        {
            Assert.That(entry.Id, Is.EqualTo("c2"));
            Assert.That(entry.Name, Is.EqualTo("Brynn"));
            Assert.That(entry.Gender, Is.EqualTo(0));
            Assert.That(entry.HoodUp, Is.False);
            Assert.That(entry.ActiveWeapon, Is.EqualTo(0));
            Assert.That(entry.Items, Is.Empty);
        });
    }

    [Test]
    public void Parse_SkipsEntriesWithoutIdNameOrBody_AndKeepsTheFirstOfARepeatedId()
    {
        IReadOnlyList<SpectatorEntry> entries = Parse(
            """{ "id": "a", "name": "First", "model": 286 }""",
            """{ "id": "a", "name": "Copy", "model": 286 }""",
            """{ "name": "NoId", "model": 286 }""",
            """{ "id": "b", "model": 286 }""",
            """{ "id": "c", "name": "<script>", "model": 286 }""",
            """{ "id": "d", "name": "NoBody" }""",
            """{ "id": "e", "name": "RaceOnly", "race": 9, "gender": 1 }""",
            """{ "id": "f", "name": "BadModel", "model": 70000, "race": 99 }""",
            "42",
            "null");

        Assert.That(entries.Select(entry => entry.Name), Is.EqualTo(new[] { "First", "RaceOnly" }));
        Assert.That(entries[1].Model, Is.EqualTo(0), "a race-only entry takes the race's body");
    }

    [Test]
    public void Parse_DropsItemsOutsideTheVisibleSlotsWithoutAModelOrRepeatingASlot()
    {
        SpectatorEntry entry = Parse("""
            { "id": "a", "name": "Aldric", "model": 286, "activeWeapon": 7, "items": [
              { "slot": 10, "model": 903 }, { "slot": 10, "model": 904 }, { "slot": 24, "model": 101 }, { "slot": 36, "model": 5 },
              { "slot": 21, "model": 0 }, { "slot": 22, "model": 70000 }, { "slot": 23, "model": 382, "color": -4, "effect": -1, "extension": 900 },
              "junk", { "model": 12 } ] }
            """).Single();

        Assert.That(entry.Items, Is.EqualTo(new[] { new SpectatorItem(10, 903, 0, 0, 0), new SpectatorItem(23, 382, 0, 0, 255) }));
        Assert.That(entry.ActiveWeapon, Is.EqualTo(0), "an unknown weapon slot is one-hand");
    }

    [TestCase("")]
    [TestCase("not json")]
    [TestCase("[]")]
    [TestCase("""{ "entries": {} }""")]
    [TestCase("""{ }""")]
    public void Parse_GivesNullForTextThatIsNotASpectatorsFile(string json)
    {
        Assert.That(GauntletSpectators.Parse(json), Is.Null);
    }

    [Test]
    public void Parse_ReadsTheEmptyFileTheRepositoryStartsWith()
    {
        Assert.That(GauntletSpectators.Parse("{\n  \"entries\": []\n}\n"), Is.Empty);
    }

    [Test]
    public void Load_MissingOrInvalidFileMeansNoSpectators_AndAValidFileIsRead()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ut-spectators-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, GauntletSpectators.FileName);

        try
        {
            File.WriteAllText(path, "{ \"entries\": [" + Aldric + "] }");
            GauntletSpectators.Load(path);
            Assert.That(GauntletSpectators.Entries.Select(entry => entry.Name), Is.EqualTo(new[] { "Aldric" }));

            File.WriteAllText(path, "{ broken");
            GauntletSpectators.Load(path);
            Assert.That(GauntletSpectators.Entries, Is.Empty);

            File.Delete(path);
            GauntletSpectators.Load(path);
            Assert.That(GauntletSpectators.Entries, Is.Empty);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Test]
    public void Body_SplitsThePlayerModelIntoAModelAndASize_AndFallsBackToTheRace()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GauntletSpectators.Body(Entry("a", 0x1000 | 34)), Is.EqualTo(((ushort) 34, (byte) 50)));
            Assert.That(GauntletSpectators.Body(Entry("a", 0x800 | 302)), Is.EqualTo(((ushort) 302, (byte) 45)));
            Assert.That(GauntletSpectators.Body(Entry("a", 0x1800 | 310)), Is.EqualTo(((ushort) 310, (byte) 55)));
            Assert.That(GauntletSpectators.Body(Entry("a", 286)), Is.EqualTo(((ushort) 286, (byte) 50)));

            SpectatorEntry celtFemale = new("b", "Name", 0, (int) eRace.Celt, 1, 3, 0, false, 0, new List<SpectatorItem>());
            Assert.That(GauntletSpectators.Body(celtFemale).Model, Is.EqualTo((ushort) eLivingModel.CeltFemale));
            Assert.That(GauntletSpectators.RaceModel((int) eRace.Troll, 0), Is.EqualTo((ushort) eLivingModel.TrollMale));
            Assert.That(GauntletSpectators.RaceModel(999, 0), Is.EqualTo((ushort) 0));
        });
    }

    [Test]
    public void Pick_IsCappedSeededAndLeavesOutTheChallenger()
    {
        List<SpectatorEntry> entries = Enumerable.Range(1, 20).Select(i => Entry("c" + i)).ToList();

        List<SpectatorEntry> first = GauntletSpectators.Pick(entries, "c3", 42, 100);
        List<SpectatorEntry> again = GauntletSpectators.Pick(entries, "c3", 42, 100);
        bool seedMatters = Enumerable.Range(0, 20).Any(seed =>
            !GauntletSpectators.Pick(entries, null, seed, 8).Select(e => e.Id).SequenceEqual(GauntletSpectators.Pick(entries, null, seed + 1, 8).Select(e => e.Id)));

        Assert.Multiple(() =>
        {
            Assert.That(first, Has.Count.EqualTo(GauntletSpectators.MaxSpectators));
            Assert.That(first.Select(entry => entry.Id), Is.Unique);
            Assert.That(first.Select(entry => entry.Id), Does.Not.Contain("c3"));
            Assert.That(again.Select(entry => entry.Id), Is.EqualTo(first.Select(entry => entry.Id)));
            Assert.That(seedMatters, Is.True);
            Assert.That(GauntletSpectators.Pick(entries.Take(3).ToList(), "c1", 1, 8).Select(entry => entry.Id), Is.EquivalentTo(new[] { "c2", "c3" }));
            Assert.That(GauntletSpectators.Pick(new List<SpectatorEntry>(), null, 1, 8), Is.Empty);
            Assert.That(GauntletSpectators.Pick(null, null, 1, 8), Is.Empty);
        });
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(3)]
    [TestCase(8)]
    [TestCase(30)]
    public void Ring_PlacesUpToEightSpotsOnTheRingFacingTheCentre(int count)
    {
        List<SpectatorSpot> spots = GauntletSpectators.Ring(count, GauntletArena.CentreX, GauntletArena.CentreY, GauntletArena.CentreZ,
            GauntletSpectators.RingRadius, GauntletArena.GateX, GauntletArena.GateY);

        Assert.That(spots, Has.Count.EqualTo(Math.Min(count, GauntletSpectators.MaxSpectators)));
        Assert.That(spots.Select(spot => (spot.X, spot.Y)), Is.Unique);

        foreach (SpectatorSpot spot in spots)
        {
            double dx = GauntletArena.CentreX - spot.X;
            double dy = GauntletArena.CentreY - spot.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);

            // Point2D's heading: 0 faces +Y, clockwise; its direction is (-sin, cos).
            double radians = spot.Heading * Point2D.HEADING_TO_RADIAN;
            double facingX = -Math.Sin(radians);
            double facingY = Math.Cos(radians);

            Assert.Multiple(() =>
            {
                Assert.That(distance, Is.EqualTo(GauntletSpectators.RingRadius).Within(1.5));
                Assert.That(spot.Z, Is.EqualTo(GauntletArena.CentreZ));
                Assert.That(spot.Heading, Is.LessThan(4096));
                Assert.That(facingX * dx / distance + facingY * dy / distance, Is.GreaterThan(0.999), "faces the centre");
                Assert.That(spot.Heading, Is.EqualTo(new Point2D(spot.X, spot.Y).GetHeading(GauntletArena.CentreX, GauntletArena.CentreY)).Within(1));
            });
        }
    }

    [Test]
    public void Ring_StaysOffTheLineBetweenThePlayerAndTheGate()
    {
        List<SpectatorSpot> spots = GauntletSpectators.Ring(8, GauntletArena.CentreX, GauntletArena.CentreY, GauntletArena.CentreZ,
            GauntletSpectators.RingRadius, GauntletArena.GateX, GauntletArena.GateY);
        double axis = Math.Atan2(GauntletArena.GateY - GauntletArena.CentreY, GauntletArena.GateX - GauntletArena.CentreX);

        foreach (SpectatorSpot spot in spots)
        {
            double angle = Math.Atan2(spot.Y - GauntletArena.CentreY, spot.X - GauntletArena.CentreX) - axis;
            double fromLine = Math.Abs(Math.Asin(Math.Sin(angle))) * 180 / Math.PI;

            Assert.That(fromLine, Is.GreaterThan(20), $"spot {spot.X},{spot.Y} is {fromLine:0.#} degrees from the fight line");
            Assert.That(Distance(spot, GauntletArena.PlayerX, GauntletArena.PlayerY), Is.GreaterThan(300));
            Assert.That(Distance(spot, GauntletArena.GateX, GauntletArena.GateY), Is.GreaterThan(300));
            Assert.That(Distance(spot, GauntletArena.CentreX, GauntletArena.CentreY), Is.LessThan(GauntletArena.Radius));
        }

        // The first four (shown when there are few spectators) stand at the sides of the fight.
        foreach (SpectatorSpot spot in spots.Take(4))
        {
            double angle = Math.Atan2(spot.Y - GauntletArena.CentreY, spot.X - GauntletArena.CentreX) - axis;

            Assert.That(Math.Abs(Math.Asin(Math.Sin(angle))) * 180 / Math.PI, Is.GreaterThan(60));
        }
    }

    private static double Distance(SpectatorSpot spot, int x, int y) => Math.Sqrt(Math.Pow(spot.X - x, 2) + Math.Pow(spot.Y - y, 2));
}
