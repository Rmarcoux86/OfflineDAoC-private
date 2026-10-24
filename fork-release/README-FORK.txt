OFFLINE DAOC - FORK UPDATE: FISHING AND EXPEDITION MODE
=======================================================

This zip updates an existing Offline DAoC 0.37 game with two new features. It replaces the server
file (GameServer.dll) and the launcher (OfflineDAoC.exe and its files). Your account, characters,
bots, items and world are not changed.

INSTALL
  1. If you don't have the game yet, install Offline DAoC 0.37 first (see the README on the repo).
  2. Close the game, click STOP SERVER in the launcher and close the launcher.
  3. Double-click "INSTALL FORK UPDATE.cmd". If Windows warns, choose More info -> Run anyway.
     It finds or asks for your playable-v0.37 folder, backs up the stock files to fork-backups\,
     and installs the update.
  4. Start the game as usual (START OFFLINE DAOC.cmd -> START SERVER -> ENTER REALM).

UPDATES
  You only install by hand once. From then on the launcher checks for a newer fork update when it
  opens and every 30 minutes. When one is out and the server is stopped, START SERVER turns gold
  and reads GET UPDATE (hover it to see the versions). Click it: the launcher downloads the update,
  closes, installs it in a black window and opens again. If the download fails, the launcher says
  why and START SERVER works as usual. You can always install a newer zip by hand instead.

  Your installed version is in runtime\fork-version.txt (for example fork-v2).
  Developers: put the single word dev in runtime\fork-version.txt and the launcher never offers
  updates (installing a zip by hand replaces it with that zip's version).
  The launcher looks for updates on GitHub at Rmarcoux86/OfflineDAoC-private. To use another
  repository, put owner/repo on the first line of runtime\fork-update-source.txt.
  Problems with the check are logged to runtime\logs\fork-update.log.

UNDO
  Close the launcher, then run "RESTORE STOCK SERVER.cmd". It puts the stock server and launcher
  back from fork-backups\ (the files from before your first fork install) and removes
  runtime\fork-version.txt.

FISHING
  /fish pole         gives you a fishing pole; equip it in the two-handed slot.
  /fish              stand on a shore facing the water. You sit, a bobber lands, and you keep
                     casting after each catch until you move or type /fish stop.
  /fish active       reel in each bite yourself: type /fish within 4 seconds of a bite.
  /fish stats        your fishing skill and best catch.
  /fish spots        list shorelines; /fish go <number> travels there (your realm only).
  /fish mark <name>  save the shore you stand on; /fish unmark <number> removes it.
  While you hold the pole and are not fighting, monsters and guards leave you alone.

EXPEDITION MODE
  A seeded roguelite run: one character, five acts of zones and bosses from level 1 to 50.
  Dying ends the run AND DELETES THE CHARACTER (it uses the game's Hardcore flag), so use a
  brand-new character. Albion, Midgard and Hibernia each have their own zones for all five
  acts, and the acts use the monsters your world actually has.

  /run new           on a fresh level-1 character. Confirm, and you are taken to the act-1 camp.
                     The journal shows the zone, level band, 2-3 objectives and the boss.
  Objectives         clear a camp (kill N of a mob near a point), kill a named mob, reach a spot.
                     Kills by you, your pet and your companion bots count.
  Loot               most kills drop an item made for your class and level: [Common], [Magic],
                     [Rare], [Epic] or a Unique. You never go more than a few kills dry.
  Uniques            18 hand-made uniques with fixed names, lore and strong fixed bonuses, six
                     each for levels 1-19, 20-39 and 40+. A run hands out each unique once before
                     any repeats; armor and weapon uniques take your class's best armor or a
                     weapon you use.
  Elite packs        each act marks one or two camps (never an objective's or the boss's) as an
                     elite pack with a seeded affix: Swift, Venomous, Armoured, Stalwart,
                     Frenzied or Spectral. Elites are level +2, larger, double health (Stalwart
                     triple) and always drop Rare or better. The journal lists each pack and
                     where it lairs; you sense a pack when you approach.
  The boss           appears when every objective is done. Kill it for three Rare-or-better
                     items; 15 seconds later you move to the next act. Act 5's boss wins the run.
  Boons              each act boss you defeat offers three boons (from the run seed, never one
                     you already have, always a loot boon while any is left). Boons last the run
                     and are re-applied at login. Fortune (every drop one rarity better), Greed
                     (+25% drop chance, you take 15% more damage), Prospector (+10% drop chance),
                     Ironhide (+10% max health, 10% less physical damage), Warding (15% less
                     spell damage), Bloodlust (+15% melee crit), Hunter's Eye (named monsters
                     nearby are announced).
  /run boons         show the boon offer and the boons you have.  /run boon <1-3>  take one.
  The mists          leaving the act's zone warns you, then returns you to camp after 10 seconds.
  /run journal       reopen the objective list.     /run status   one-line summary.
  /run seed          show the seed (/run new <seed> replays the same run).
  /run abandon       end a run without dying; the character survives.
  /junk common|magic|rare  destroy Expedition items of that rarity and lower in your backpack
                     for the merchant price (asks first; Epic, Unique and non-Expedition items
                     are never touched).
  /junk auto off|common|magic  sell such drops on the spot (saved per character).
  /junk status       show the setting. Server setting: expedition_auto_junk.
  Your class         at run start you pick your advanced class and are promoted right away, even
                     at level 1 (asked again at login if you never answered); /train works
                     anywhere during a run.
  The sky            a run darkens your sky: rain, dark clouds and heavy fog over the whole
                     region, for you only; it clears when the run ends. Server settings:
                     expedition_weather_intensity (100, 0 = off, max 120),
                     expedition_weather_fog (16000, 16000-65535),
                     expedition_weather_speed (0 = still).
  After a death, the run's summary is shown at your next login.

More commands: QUICK COMMANDS.txt and ALL SERVER COMMANDS.txt in this zip.
