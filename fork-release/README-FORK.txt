OFFLINE DAOC - FORK UPDATE: FISHING AND EXPEDITION MODE
=======================================================

This zip updates an existing Offline DAoC 0.37 game with two new features. It replaces one server
file (GameServer.dll). Your account, characters, bots, items and world are not changed.

INSTALL
  1. If you don't have the game yet, install Offline DAoC 0.37 first (see the README on the repo).
  2. Close the game and click STOP SERVER in the launcher.
  3. Double-click "INSTALL FORK UPDATE.cmd". If Windows warns, choose More info -> Run anyway.
     It finds or asks for your playable-v0.37 folder, backs up the stock files to fork-backups\,
     and installs the update.
  4. Start the game as usual (START OFFLINE DAOC.cmd -> START SERVER -> ENTER REALM).

UNDO
  Run "RESTORE STOCK SERVER.cmd". It puts the backed-up stock server back.

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
  brand-new character. Hibernia and Albion have full zone pools; Midgard uses Albion's for now.

  /run new           on a fresh level-1 character. Confirm, and you are taken to the act-1 camp.
                     The journal shows the zone, level band, 2-3 objectives and the boss.
  Objectives         clear a camp (kill N of a mob near a point), kill a named mob, reach a spot.
                     Kills by you, your pet and your companion bots count.
  Loot               most kills drop an item made for your class and level: Common, [Magic],
                     [Rare], [Epic] or a named Unique. You never go more than a few kills dry.
  The boss           appears when every objective is done. Kill it for three Rare-or-better
                     items; 15 seconds later you move to the next act. Act 5's boss wins the run.
  The mists          leaving the act's zone warns you, then returns you to camp after 10 seconds.
  /run journal       reopen the objective list.     /run status   one-line summary.
  /run seed          show the seed (/run new <seed> replays the same run).
  /run abandon       end a run without dying; the character survives.
  After a death, the run's summary is shown at your next login.

More commands: QUICK COMMANDS.txt and ALL SERVER COMMANDS.txt in this zip.
