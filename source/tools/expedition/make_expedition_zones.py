"""Build source/server/GameServer/gameutils/Expedition/expedition-zones.json: the zones each realm's Expedition
acts may use.

    python make_expedition_zones.py [--repo <this repository>] [--out <json path>] [--world-db <opendaoc.sqlite3.db>]

Every zone id, name and region is checked against the server's Zones.xml
(source/server/GameServer/scripts/dbupdater/insert/Zones.xml), and the old frontier zones are refused (the same list
the server's AutonomousWorldBotController.IsFrontierZone uses). Zone names are written as Zones.xml spells them.

Camps and bosses are not typed here: every zone gets "camp": null (computed at generation) and an empty
"bossCandidates" list (computed), except the zones that already carried boss names checked against the CapnBry goal
catalog; those names are checked again.

The server decides at generation time which listed zones are usable: a zone needs at least 3 monster camps in the
act's level band, from the live world's Mob table (or from the CapnBry catalog when the world gives none). This script
prints the catalog's count per zone and, with --world-db, the world's count (same filters and clustering as
WorldCampSource.cs), so a zone that the world doesn't populate in band shows up before a release.
"""
from __future__ import annotations

import argparse
import json
import sqlite3
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

BANDS = {1: (1, 10), 2: (10, 20), 3: (20, 32), 4: (32, 44), 5: (44, 50)}
MIN_BAND_CAMPS = 3
CAMP_CELL_SIZE = 4200

# The old (pre-New Frontiers) frontier zones and the shared frontier dungeons: never expedition zones.
# Mirrors AutonomousWorldBotController.IsFrontierZone. Region 163 is New Frontiers.
FRONTIER_ZONES = {(1, 11), (1, 12), (1, 14), (1, 15), (100, 111), (100, 112), (100, 113), (100, 115),
                  (200, 210), (200, 211), (200, 212), (200, 214)}
FRONTIER_REGIONS = {163, 246, 248, 276, 277}

# (zone id, region id, boss candidates checked against the catalog). Classic level ranges decide the act; the server
# drops any zone the world doesn't populate in the act's band.
REALMS = {
    "Albion": {
        1: [(0, 1, ["bandit lieutenant", "grumoz demon", "convert guard"]), (2, 1, ["devout filidh"]),
            (9, 1, ["giant wolf", "bogman trapper", "shambler", "marsh scrag"]), (51, 51, [])],
        2: [(1, 1, []), (3, 1, ["forest chief"]), (8, 1, []), (51, 51, []), (7, 1, [])],
        3: [(6, 1, ["giant rooter"]), (52, 51, ["dread disciple", "drakoran thaumaturge", "sea hound", "caer caddug baille"]),
            (7, 1, []), (10, 1, []), (53, 51, [])],
        4: [(52, 51, ["caer caddug priest", "caer caddug knight"]),
            (19, 20, ["pendragon ardent", "pendrake", "spectral essence", "grave goblin"]),
            (249, 249, ["naburite drinker", "cursed necyomancer"]), (10, 1, []), (4, 1, []), (53, 51, []), (54, 51, []),
            (55, 51, [])],
        5: [(19, 20, ["stone sentinel", "spectral essence"]), (249, 249, []), (4, 1, []), (54, 51, []), (56, 51, []),
            (57, 51, [])],
    },
    "Midgard": {
        1: [(100, 100, []), (101, 100, []), (103, 100, []), (151, 151, [])],
        2: [(102, 100, []), (105, 100, []), (151, 151, []), (129, 129, [])],
        3: [(104, 100, []), (106, 100, []), (155, 151, []), (152, 151, []), (128, 128, [])],
        4: [(107, 100, []), (116, 100, []), (153, 151, []), (152, 151, []), (125, 125, []), (127, 127, [])],
        5: [(108, 100, []), (116, 100, []), (154, 151, []), (156, 151, []), (158, 151, []), (150, 150, []), (161, 161, [])],
    },
    # Hibernia's Shrouded Isles zones (region 181, Hy Brasil) were taken out on the owner's request (2026-10-24).
    "Hibernia": {
        1: [(200, 200, ["dergan fury", "dergan enchanter", "whistler", "anger sprite"]),
            (207, 200, ["large eirebug", "wild lucradan"])],
        2: [(200, 200, []), (221, 221, ["tomb dweller"]), (201, 200, ["walking rock", "silvermine badger"]), (204, 200, [])],
        3: [(201, 200, []), (202, 200, ["eriu waylayer"]), (222, 222, ["earth sprite"]), (204, 200, []), (208, 200, []),
            (206, 200, []), (216, 200, [])],
        4: [(249, 249, ["naburite drinker", "cursed necyomancer"]), (203, 200, []), (205, 200, []), (206, 200, []),
            (216, 200, [])],
        5: [(249, 249, []), (203, 200, []), (205, 200, [])],
    },
}

# WorldCampSource's filters: realm 0, not PEACE (0x10) or CANTTARGET (0x08), no service or epic class type or guild.
SERVICE_WORDS = ["merchant", "trainer", "guard", "teleporter", "keep", "herald", "master", "banker", "healer", "vault",
                 "stable", "siege", "boat", "quest", "commander", "lord", "epic"]
EXCLUDED_FLAGS = 0x10 | 0x08
# Also skipped: classic quest NPCs and the Darkness Falls raid/event spawns (AutonomousDarknessFallsGoalScope).
CLASSIC_QUEST_PACKAGE = "ClassicQuest"


def load_zones(path: Path) -> dict[int, dict]:
    zones = {}

    for node in ET.parse(path).getroot():
        values = {child.tag: child.text for child in node}
        zone_id = int(values["ZoneID"])
        zones[zone_id] = {
            "id": zone_id,
            "region": int(values["RegionID"]),
            "name": values["Name"],
            "x": int(values["OffsetX"]) * 8192,
            "y": int(values["OffsetY"]) * 8192,
            "w": int(values["Width"]) * 8192,
            "h": int(values["Height"]) * 8192,
        }

    return zones


def in_band(levels: list[int], band: tuple[int, int]) -> bool:
    return min(levels) <= band[1] and max(levels) >= band[0]


def world_camps(db_path: Path, zones: dict[int, dict], regions: set[int], raid_ids: set[str]) -> dict[tuple[int, int], list[list[int]]]:
    """(region, zone) -> each camp's levels, clustered like WorldCampSource.BuildCamps."""
    by_region = defaultdict(list)

    for zone in zones.values():
        by_region[zone["region"]].append(zone)

    clusters = defaultdict(set)
    connection = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)

    try:
        marks = ",".join("?" * len(regions))
        rows = connection.execute(
            f"SELECT Mob_ID, PackageID, Name, Region, X, Y, Level, Realm, Flags, ClassType, Guild FROM Mob WHERE Region IN ({marks})",
            sorted(regions))

        for mob_id, package_id, name, region, x, y, level, realm, flags, class_type, guild in rows:
            if package_id == CLASSIC_QUEST_PACKAGE or mob_id in raid_ids:
                continue

            if not name or not name.strip() or (level or 0) < 1 or (realm or 0) != 0 or (flags or 0) & EXCLUDED_FLAGS:
                continue

            service_text = f"{class_type or ''} {guild or ''}".lower()

            if any(word in service_text for word in SERVICE_WORDS):
                continue

            zone = next((z for z in by_region[region] if z["x"] <= x < z["x"] + z["w"] and z["y"] <= y < z["y"] + z["h"]), None)

            if zone is None:
                continue

            local_x = x - zone["x"]
            local_y = y - zone["y"]
            clusters[(region, zone["id"], name, local_x // CAMP_CELL_SIZE, local_y // CAMP_CELL_SIZE)].add(level)
    finally:
        connection.close()

    camps = defaultdict(list)

    for (region, zone_id, _name, _cx, _cy), levels in clusters.items():
        camps[(region, zone_id)].append(sorted(levels))

    return camps


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument("--out", type=Path)
    parser.add_argument("--world-db", type=Path)
    args = parser.parse_args()

    server = args.repo / "source" / "server" / "GameServer"
    zones = load_zones(server / "scripts" / "dbupdater" / "insert" / "Zones.xml")
    catalog = json.loads((server / "bots" / "autonomous" / "data" / "capnbry_classic_si_goals.json").read_text(encoding="utf-8"))
    out = args.out or server / "gameutils" / "Expedition" / "expedition-zones.json"

    catalog_camps = defaultdict(list)

    for goal in catalog["goals"]:
        catalog_camps[(goal["region_id"], goal["zone_id"])].append(goal)

    world = None

    if args.world_db:
        regions = {region for acts in REALMS.values() for zone_list in acts.values() for _zone, region, _bosses in zone_list}
        raid_manifest = json.loads((server / "bots" / "autonomous" / "data" / "darkness_falls_raid_event_mobs.json").read_text(encoding="utf-8"))
        world = world_camps(args.world_db, zones, regions, {row["id"] for row in raid_manifest["rows"]})

    errors = []
    doc = {
        "schema": 1,
        "_comment": ("Expedition act zones per realm, made by source/tools/expedition/make_expedition_zones.py. Zone ids, "
                     "names and regions are from Zones.xml; old frontier zones are excluded. At generation a zone needs "
                     ">= 3 monster camps in the act's band (world Mob table, else the CapnBry catalog). camp null = "
                     "computed (median of the zone's lowest-level camps). bossCandidates are catalog mob names checked "
                     "for that zone; empty = computed (a rare named mob at or above the band, else the band's most "
                     "common top mob)."),
        "realms": [],
    }

    for realm, acts in REALMS.items():
        if sorted(acts) != sorted(BANDS):
            errors.append(f"{realm}: acts {sorted(acts)} are not 1-5")

        realm_entry = {"realm": realm, "acts": []}

        for act in sorted(acts):
            band = BANDS[act]
            act_entry = {"act": act, "zones": []}
            usable = 0

            for zone_id, region_id, bosses in acts[act]:
                zone = zones.get(zone_id)

                if zone is None:
                    errors.append(f"{realm} act {act}: zone {zone_id} is not in Zones.xml")
                    continue

                if zone["region"] != region_id:
                    errors.append(f"{realm} act {act}: zone {zone_id} ({zone['name']}) is in region {zone['region']}, not {region_id}")

                if (region_id, zone_id) in FRONTIER_ZONES or region_id in FRONTIER_REGIONS:
                    errors.append(f"{realm} act {act}: zone {zone_id} ({zone['name']}) is a frontier zone")

                goals = catalog_camps.get((region_id, zone_id), [])
                names = {goal["name"] for goal in goals}

                for boss in bosses:
                    if boss not in names:
                        errors.append(f"{realm} act {act}: boss '{boss}' is not a catalog mob of {zone['name']}")

                catalog_count = sum(1 for goal in goals if in_band(goal["levels"], band))
                world_count = None if world is None else sum(1 for levels in world.get((region_id, zone_id), []) if in_band(levels, band))
                # As WorldThenCatalogCampSource: the world's camps when it has any in the zone, else the catalog's.
                count = world_count if world and world.get((region_id, zone_id)) else catalog_count
                usable += 1 if count >= MIN_BAND_CAMPS else 0
                world_text = "" if world is None else f", world {world_count}"
                print(f"{realm:8} act {act} {zone_id:3} {zone['name']:24} catalog {catalog_count:3}{world_text}"
                      + ("" if count >= MIN_BAND_CAMPS else "   (not usable in band)"))
                act_entry["zones"].append({
                    "zoneId": zone_id,
                    "zoneName": zone["name"],
                    "regionId": region_id,
                    "minLevel": band[0],
                    "maxLevel": band[1],
                    "camp": None,
                    "bossCandidates": bosses,
                })

            if usable == 0:
                print(f"{realm:8} act {act}: no listed zone is usable; the server falls back to the realm's best zone")

            realm_entry["acts"].append(act_entry)

        doc["realms"].append(realm_entry)

    if errors:
        for error in errors:
            print("ERROR:", error, file=sys.stderr)

        return 1

    with open(out, "w", newline="\n", encoding="utf-8") as handle:
        json.dump(doc, handle, indent=2, ensure_ascii=False)
        handle.write("\n")

    print(f"wrote {out}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
