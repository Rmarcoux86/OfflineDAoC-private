using System;

namespace DOL.GS.Fishing;

/// <summary>
/// The visible side of a fishing session: the bobber floating at the cast point and the quiet cast and bite effects.
/// Only stock client content is used (objects.csv models and spells.csv effects of the original client), sent with
/// the existing packets, so nothing has to be installed on the client. FishingManager calls these under the
/// session's lock.
///
/// The ids below come from the client catalogs copied in source/tools/observer-model-inspection and have NOT been
/// seen in game yet. Each one is a single constant so it can be swapped after a look in game.
/// </summary>
public static class FishingBobber
{
    // objects.csv 4185 "Spring_NestEgg" -> items.csv 2224 Spring_NestEgg (Expansion Only 0): a small, round, pale
    // object with a real mesh. Most small round items (vials 526-532, orbs 523-525, bottle 554, egg 587) have NIF 1,
    // which the client draws as a loot bag on the ground, so they can't be used. Alternatives with a mesh:
    //   2084 "pos whitemarker" (items.csv 934 pos_whitemarker)
    //   1766 "Volcanus orb red" (items.csv 985 Volcanus_orb_red; may be large)
    public static readonly ushort BobberModel = 4185;

    // spells.csv 7461 "Thrusting Arrows": projectile 596 "arrow thrust01 travel" from the caster to the target,
    // strike 597, sound set 0. It is the only quiet, thin stock projectile; every projectile row has a caster
    // animation except the siege shots, and this one's (anims.csv slots 158-160) is the bow "spell version"
    // ready/aim/fire, which may play on the angler. Set to 0 to skip the cast effect. Alternatives:
    //   7462 "Slashing Arrows" (projectile 593, strike 594, sound set 0)
    //   7460 "Blunt Arrows" (projectile 599, strike 600, sound set 0)
    public static readonly ushort CastEffect = 7461;

    // spells.csv 5939 "frostspray hit/groundsplash": target effect 40 only (speffects.csv: rootnode, 3 s), no caster
    // animation, no sound set. Played with the bobber as both caster and target. Set to 0 to skip. Alternatives:
    //   6026 "Paladin Group Heal pulse" (target effect 127 "leaf and dewdrop with waterglob", rootnode, no sound)
    //   5953 "Water Blast w/o Sound" (target effect 362 "water blast", attached to Bip01 Pelvis, which a static
    //        object has no node for)
    public static readonly ushort BiteEffect = 5939;

    // Bolt spells travel at 850 units per second and send their travel time in tenths of a second
    // (BoltSpellHandler); the cast arc uses the same speed.
    private const int CastSpeed = 850;

    /// <summary>
    /// Puts a bobber on the water at <paramref name="point"/> (Z is the water surface). Returns null if it could not be
    /// added to the world.
    /// </summary>
    public static GameStaticItem Spawn(GamePlayer player, Point3D point)
    {
        GameStaticItem bobber = new()
        {
            CurrentRegion = player.CurrentRegion,
            X = point.X,
            Y = point.Y,
            Z = point.Z,
            Heading = player.Heading,
            Realm = player.Realm,
            Model = BobberModel,
            Name = $"{player.Name}'s bobber"
        };

        if (!bobber.AddToWorld())
        {
            return null;
        }

        return bobber;
    }

    public static void Remove(GameStaticItem bobber)
    {
        if (bobber == null)
        {
            return;
        }

        bobber.Delete();
    }

    /// <summary>The line flying out from the angler to the bobber.</summary>
    public static void PlayCast(GamePlayer player, GameStaticItem bobber)
    {
        if (CastEffect == 0 || bobber == null || bobber.ObjectState != GameObject.eObjectState.Active)
        {
            return;
        }

        int travelMs = Math.Min(player.GetDistanceTo(bobber), WorldMgr.VISIBILITY_DISTANCE) * 1000 / CastSpeed;
        ushort boltTime = (ushort) (1 + travelMs / 100);

        foreach (GamePlayer viewer in player.GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
        {
            viewer.Out.SendSpellEffectAnimation(player, bobber, CastEffect, boltTime, true, 1);
        }
    }

    /// <summary>A small splash on the bobber when a fish bites.</summary>
    public static void PlayBite(GameStaticItem bobber)
    {
        if (BiteEffect == 0 || bobber == null || bobber.ObjectState != GameObject.eObjectState.Active)
        {
            return;
        }

        foreach (GamePlayer viewer in bobber.GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
        {
            viewer.Out.SendSpellEffectAnimation(bobber, bobber, BiteEffect, 0, true, 1);
        }
    }
}
