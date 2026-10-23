using System;
using System.Collections.Generic;
using System.Linq;
using DOL.GS.Fishing;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public class UT_FishingSpots
{
    private static FishingSpot Spot(string name, eRealm realm, bool playerAdded = false)
    {
        return new FishingSpot
        {
            Name = name,
            Realm = realm,
            RegionID = 1,
            X = 1000,
            Y = 2000,
            Z = 300,
            Heading = 1024,
            Description = name + " water",
            Source = "test",
            PlayerAdded = playerAdded
        };
    }

    [Test]
    public void Defaults_HaveUniqueNamesValidRealmsCoordinatesAndSources()
    {
        IReadOnlyList<FishingSpot> spots = FishingSpots.Defaults();
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        Assert.That(spots, Is.Not.Empty);

        foreach (FishingSpot spot in spots)
        {
            Assert.That(names.Add(spot.Name), Is.True, $"duplicate name {spot.Name}");
            Assert.That(spot.Name.Length, Is.InRange(1, FishingSpots.MaxNameLength), spot.Name);
            Assert.That(spot.Realm, Is.AnyOf(eRealm.Albion, eRealm.Midgard, eRealm.Hibernia), spot.Name);
            Assert.That(spot.RegionID, Is.GreaterThan(0), spot.Name);
            Assert.That(spot.X, Is.GreaterThan(0), spot.Name);
            Assert.That(spot.Y, Is.GreaterThan(0), spot.Name);
            Assert.That(spot.Z, Is.GreaterThan(0), spot.Name);
            Assert.That(spot.Heading, Is.LessThan(4096), spot.Name);
            Assert.That(spot.Description, Is.Not.Null.And.Not.Empty, spot.Name);
            Assert.That(spot.Source, Is.Not.Null.And.Not.Empty, spot.Name);
            Assert.That(spot.PlayerAdded, Is.False, spot.Name);
        }

        foreach (eRealm realm in new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia })
        {
            Assert.That(spots.Count(spot => spot.Realm == realm), Is.GreaterThanOrEqualTo(2), realm.ToString());
        }
    }

    [Test]
    public void Json_RoundTripKeepsEveryField()
    {
        List<FishingSpot> original = FishingSpots.Defaults().ToList();
        original.Add(Spot("My Pond", eRealm.Midgard, true));

        string json = FishingSpots.Serialize(original);
        List<FishingSpot> copy = FishingSpots.Deserialize(json);

        Assert.That(json, Does.Contain("\"Realm\": \"Midgard\""));
        Assert.That(copy, Has.Count.EqualTo(original.Count));

        for (int i = 0; i < original.Count; i++)
        {
            Assert.That(copy[i].Name, Is.EqualTo(original[i].Name));
            Assert.That(copy[i].Realm, Is.EqualTo(original[i].Realm));
            Assert.That(copy[i].RegionID, Is.EqualTo(original[i].RegionID));
            Assert.That(copy[i].X, Is.EqualTo(original[i].X));
            Assert.That(copy[i].Y, Is.EqualTo(original[i].Y));
            Assert.That(copy[i].Z, Is.EqualTo(original[i].Z));
            Assert.That(copy[i].Heading, Is.EqualTo(original[i].Heading));
            Assert.That(copy[i].Description, Is.EqualTo(original[i].Description));
            Assert.That(copy[i].Source, Is.EqualTo(original[i].Source));
            Assert.That(copy[i].PlayerAdded, Is.EqualTo(original[i].PlayerAdded));
        }
    }

    [Test]
    public void Json_ReadsHandWrittenRealmNamesInAnyCase()
    {
        List<FishingSpot> spots = FishingSpots.Deserialize("[{\"Name\":\"Edited\",\"Realm\":\"hibernia\",\"RegionID\":200,\"X\":1,\"Y\":2,\"Z\":3}]");

        Assert.That(spots, Has.Count.EqualTo(1));
        Assert.That(spots[0].Realm, Is.EqualTo(eRealm.Hibernia));
    }

    [Test]
    public void Merge_KeepsDefaultsFirstFlagsMarkedSpotsAndSkipsTakenNames()
    {
        List<FishingSpot> defaults = [Spot("Lake", eRealm.Albion), Spot("River", eRealm.Midgard)];
        List<FishingSpot> marked = [Spot("lake", eRealm.Hibernia), Spot("Pond", eRealm.Hibernia), Spot("POND", eRealm.Albion)];

        List<FishingSpot> all = FishingSpots.Merge(defaults, marked);

        Assert.That(all.Select(spot => spot.Name), Is.EqualTo(new[] { "Lake", "River", "Pond" }));
        Assert.That(all.Select(spot => spot.PlayerAdded), Is.EqualTo(new[] { false, false, true }));
        Assert.That(all[0].Realm, Is.EqualTo(eRealm.Albion));
    }

    [Test]
    public void Merge_WithNoMarkedSpotsIsTheDefaults()
    {
        List<FishingSpot> all = FishingSpots.Merge(FishingSpots.Defaults(), []);

        Assert.That(all.Select(spot => spot.Name), Is.EqualTo(FishingSpots.Defaults().Select(spot => spot.Name)));
        Assert.That(all.Any(spot => spot.PlayerAdded), Is.False);
    }

    [Test]
    public void Listing_PutsThePlayersRealmFirstAndNumbersContinuously()
    {
        List<FishingSpot> spots =
        [
            Spot("Alb One", eRealm.Albion),
            Spot("Mid One", eRealm.Midgard),
            Spot("Hib One", eRealm.Hibernia),
            Spot("Alb Two", eRealm.Albion),
            Spot("Hib Two", eRealm.Hibernia, true)
        ];

        List<FishingSpot> ordered = FishingSpots.Ordered(spots, eRealm.Hibernia);
        List<string> lines = FishingSpots.BuildListing(spots, eRealm.Hibernia, false);
        List<string> numbered = lines.Where(line => line.Length > 0 && char.IsDigit(line[0])).ToList();

        Assert.That(ordered.Select(spot => spot.Name), Is.EqualTo(new[] { "Hib One", "Hib Two", "Alb One", "Alb Two", "Mid One" }));
        Assert.That(lines[0], Is.EqualTo("-- Hibernia --"));
        Assert.That(numbered, Has.Count.EqualTo(5));

        for (int i = 0; i < numbered.Count; i++)
        {
            Assert.That(numbered[i], Does.StartWith($"{i + 1}. {ordered[i].Name}"));
        }

        Assert.That(numbered[1], Is.EqualTo("2. Hib Two (marked) - Hib Two water"));
        Assert.That(lines, Does.Contain("-- Albion (enemy territory) --"));
        Assert.That(lines[^1], Is.EqualTo("Type /fish go <number> to travel there."));
    }
}
