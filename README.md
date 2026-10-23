> **Disclaimer.** Dark Age of Camelot is the intellectual property of Electronic Arts and Broadsword
> Online Games. Offline DAoC is not affiliated with, endorsed by, or connected to either company. The
> game client, textures, models and other assets it uses belong to them, and this project is built on
> top of that copyrighted work.
>
> This is a free, non-commercial fan project. It is not a product and it is not for sale. No donations
> are accepted, and the project owner has not made, and will not make, any money from it. The client
> files are the same publicly available ones every free shard uses; the work original to this
> repository is the bot system, the server customizations and the new client's code. Because the code is open source, the
> owner cannot control what forks or other people do with it.
>
> This "time capsule" of the 2002-2003 game was never meant to compete with the current retail Dark Age
> of Camelot, which is a very different, modern experience. If that interests you, please visit the
> official site: [darkageofcamelot.com](https://www.darkageofcamelot.com/).

# Offline DAoC — single-player Dark Age of Camelot with bots

Offline DAoC is a local, single-player Dark Age of Camelot (Classic + Shrouded Isles, 1.65 rules)
that runs entirely on your own PC. The world is filled with autonomous gamebots that level,
group, trade and fight on their own, and you can recruit companion bots to adventure with you.

It is a community project built on the open-source [OpenDAoC](https://github.com/OpenDAoC/OpenDAoC-Core)
server. The customizations were developed with AI tools (Codex, then Claude) and directed and
play-tested by the project owner. It is not an official DAoC product and is not for sale.

## This fork: fishing and Expedition mode

This is Ryan's fork of Offline DAoC. On top of the 0.37 game it adds two things, both server-side so they work in
the original client and the new client:

- **Fishing** — sit at any shoreline with a pole and relax. `/fish`.
- **Expedition mode** — a seeded, roguelite campaign from level 1 to a final boss, with permadeath and
  Diablo-style loot. `/run new`.

Everything below walks through getting the game, building this fork's server, and playing both features.

### Step 1 — Install the game (one time, ~20 GB)

The playable game is the stock 0.37 download; this fork only replaces one server file on top of it.

1. **Check your PC:** 64-bit Windows 10 or 11, a CPU with AVX2, 16 GB RAM recommended, ~20 GB free disk.
2. **Turn on .NET Framework 3.5** (the original client needs it): Start → type *Turn Windows features on or off* →
   tick **.NET Framework 3.5** → OK.
3. **Download the installer:** open the
   [v0.37 release page](https://github.com/shadowofze/OfflineDAoC/releases/tag/v0.37) and download
   `Download-OfflineDAoC-v0.37.zip`.
4. **Extract it** into a folder with a short path and no spaces, for example `C:\Dev\OfflineDAoC-Game`
   (right-click the zip → **Extract All…**).
5. **Run `GET-OFFLINE-DAOC-v0.37.cmd`** from the extracted folder. If Windows warns, click
   **More info → Run anyway**. When it asks, type **2** (new install) and press Enter. It downloads the game in
   checked parts into a `playable-v0.37` folder next to it. If it stops, run it again; it continues.
6. **Play it once stock:** open `playable-v0.37` and double-click **START OFFLINE DAOC.cmd** →
   **START SERVER** → wait for **RUNNING** → **ENTER REALM**. Your local account is created automatically.
   Make a character and enter the world, then quit and click **STOP SERVER**. This proves the base game works
   before anything is changed.

Your **playable folder** from here on is `...\Download-OfflineDAoC-v0.37\playable-v0.37`.

### Step 2 — Build this fork's server (one time setup, then a few minutes per update)

1. **Install the tools:**
   - [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the *SDK*, x64). Check with
     `dotnet --list-sdks` in a terminal; it should list a `10.x` entry.
   - [Git for Windows](https://git-scm.com/download/win).
2. **Get the source** (use plain *Windows PowerShell* or *Terminal*, not PowerShell ISE):
   ```
   cd C:\Dev
   git clone https://github.com/Rmarcoux86/OfflineDAoC-private.git OfflineDAoC
   cd OfflineDAoC
   git checkout dev
   copy source\server\CoreServer\config\serverconfig.example.xml source\server\CoreServer\config\serverconfig.xml
   ```
   `dev` is the branch with every feature merged. The `copy` line is a one-time step; the build only needs the
   file to exist.
3. **Build:**
   ```
   dotnet build "source\server\Dawn of Light.sln" -c Release
   ```
   The first build restores packages and takes a few minutes. Git prints its progress in red in PowerShell; that
   is normal. Only `error` or `fatal` lines mean something failed.
4. **Optional — run the tests** for the new features:
   ```
   dotnet test source\server\Tests\Tests.csproj -c Release --filter "UT_Fishing|UT_Expedition"
   ```

### Step 3 — Put the built server into the game

The build produces one file that matters, `GameServer.dll`, which has to replace the stock copy in three places.

1. Close the game, click **STOP SERVER** in the launcher, and close the launcher.
2. In PowerShell, with your own two paths filled in:
   ```
   $src="C:\Dev\OfflineDAoC\source\server\Release\lib"
   $dst="C:\Dev\OfflineDAoC-Game\Download-OfflineDAoC-v0.37\playable-v0.37\runtime\server"
   foreach($d in $dst,"$dst\lib","$dst\win-x64"){ Copy-Item "$src\GameServer.dll","$src\GameServer.pdb" $d -Force }
   ```
   No output means it worked. (By hand: copy `GameServer.dll` and `GameServer.pdb` from `source\server\Release\lib`
   into `runtime\server`, `runtime\server\lib` and `runtime\server\win-x64` inside the playable folder.)
3. Start the launcher → **START SERVER** → **ENTER REALM**. The first start adds the fishing items and the
   expedition table to your world database; your characters and bots are untouched.

**Every later update is just:** `git pull`, step 2.3, step 3. You never redownload the game. If you want to protect
your saves while testing, copy the whole `playable-v0.37` folder first and deploy into the copy (only one server can
run at a time).

### Expedition mode — how to play

A run is one character, one seed, five acts of zones and bosses from level 1 to 50. Death ends the run **and
deletes the character** (it uses the game's Hardcore flag), so always use a fresh character.

1. Create a **new level-1 character** (Hibernia and Albion have full zone pools; Midgard uses Albion's for now)
   and enter the world.
2. Type **`/run new`** and accept the confirmation. You are teleported to the act-1 camp and the **journal** opens:
   the act's zone, level band, 2–3 objectives, and the boss.
3. **Do the objectives** — clear a camp (kill N of a mob near a point), kill a named mob, or reach a spot. Kills by
   you, your pet and your companion bots all count. Progress shows in chat; `/run journal` reopens the list,
   `/run status` is the one-liner.
4. **Loot drops on most kills** during a run: Common, `[Magic]`, `[Rare]`, `[Epic]` and named **Unique** items,
   generated for your class and level. Grey mobs drop less; you never go more than a few kills without something.
5. **When every objective is done, the act boss appears** at its lair (announced in chat). It is a named mob of
   the zone at a higher level with much more health. Kill it: it drops three Rare-or-better items. Fifteen seconds
   later you are moved to the next act's camp.
6. **Stay in the act's zone.** Leaving it gives a warning, then pulls you back to camp after 10 seconds.
7. **Act 5's boss wins the run.** The Hardcore flag comes off and the character keeps everything.
8. If you die, the run is over and the character is deleted; a summary of the run is shown at your next login.

Other commands: `/run seed` shows the seed (`/run new 12345` replays an exact run); `/run abandon` ends a run
without dying (the character survives, Hardcore flag off). `/run` alone prints the usage.

Tuning lives in the server's `serverproperty` table, category `expedition` (`expedition_loot_chance`,
`expedition_pity_guarantee`, `expedition_boss_drops`, …); `expedition_loot_outside_runs` turns the same loot on for
every character everywhere.

### Fishing — how to play

1. `/fish pole` gives you a fishing pole; equip it in the two-handed slot.
2. Stand on a shore facing the water and type `/fish`. You sit down, a bobber lands on the water, and you keep
   casting after each catch until you move or type `/fish stop`.
3. `/fish active` makes you reel in each bite yourself (type `/fish` within 4 seconds). `/fish stats` shows your
   skill and best catch.
4. `/fish spots` lists shorelines; `/fish go <number>` travels to one in your realm. `/fish mark <name>` saves the
   shore you stand on as a new spot, `/fish unmark <number>` removes it.
5. While you hold the pole and are not fighting, monsters and guards leave you alone.

Everything else below is the upstream project's README.

## Current release: 0.37

0.37 carries on from 0.36: you can form your own guild and bring gamebots into it, bot armies attack together and
raise their fallen at sieges, groups travel as one pack, raids get further in Caer Sidi and Galladoria, there is far
less lag on a busy server, and the new client gets many fixes. Read [what's new in 0.37](docs/RELEASE-0.37.md), or
every change in the [changelog](CHANGELOG.md).

**0.37 is an update.** If you already play 0.36, 0.36b or 0.36c, you only download the update (much smaller than the
game) and your account, characters, bots and settings stay as they are. If you are new, the same file installs the
complete game for you.

There are three editions. They are the same game on the same server:

| Edition | What it is | Download |
|---|---|---|
| **0.37** (recommended) | The original DAoC client with the classic Classic + SI class list only. There are no Sluaghbinder players or bots. | [v0.37 release](https://github.com/shadowofze/OfflineDAoC/releases/tag/v0.37) |
| **0.37b** | The original DAoC client, with the custom Hibernian class, the **Sluaghbinder** (a pet-summoning undead binder), for players and bots. | [v0.37b release](https://github.com/shadowofze/OfflineDAoC/releases/tag/v0.37b) |
| **0.37c** (BETA) | The game with **two clients**: the original one and a **new client** built in Godot 4. Both play the same account, characters, bots and world, so you can switch whenever you like. It works with 0.37 and with 0.37b. | [v0.37c release](https://github.com/shadowofze/OfflineDAoC/releases/tag/v0.37c) |

**Handy commands:** [QUICK COMMANDS.txt](QUICK%20COMMANDS.txt) lists the in-game commands and launcher shortcuts: teleport to a bot (`/tele`), find monsters of a level (`/mobs`), add 10 or 100 bots at once (Shift or Ctrl + click), and more. Every command is in [ALL SERVER COMMANDS.txt](ALL%20SERVER%20COMMANDS.txt), and both files are in your game folder too.

## Download and play (no Git or AI needed)

1. **Check the requirements:**
   - A 64-bit Windows 10 or 11 PC with a CPU that supports AVX2.
   - 16 GB RAM recommended.
   - Free disk space for a new install: about **20 GB** for 0.37 or 0.37b, about **60 GB** while 0.37c installs.
   - For the original client: the Windows **.NET Framework 3.5** feature. [How to turn it on](docs/PLAY.md#before-you-start).
   - For the new client (0.37c): a graphics card that supports Vulkan or DirectX 12, with current drivers.
2. **Get the downloader:** open the release page of your edition (links above) and download its one zip file:
   `Download-OfflineDAoC-v0.37.zip`, `Download-OfflineDAoC-v0.37b.zip` or `Download-OfflineDAoC-v0.37c.zip`.
   Put it in a folder with a short path, for example `C:\Games\OfflineDAoC`.
3. **Unzip it:** right-click the zip, choose **Extract All…**, and open the extracted folder.
4. **Run it:** double-click `GET-OFFLINE-DAOC-v0.37.cmd` (or `-v0.37b` / `-v0.37c`). If Windows asks, choose
   **Run** (or **More info → Run anyway**). It asks one question:
   - **1 = Update my game** (you have 0.36, 0.36b or 0.36c): close the game, choose your game folder, and only the
     update is downloaded and applied. Everything it replaces is backed up first. If you have the new client, it is
     updated too.
   - **2 = New install** (you don't have the game yet): it downloads the complete game in checked parts into a new
     `playable-v0.37…` folder next to it, then updates it. If it stops, run it again: it continues.
5. **Play:** open your game folder and double-click **START OFFLINE DAOC.cmd** (in 0.37c,
   **START OFFLINE DAOC - NEW CLIENT.cmd** plays with the new client). The launcher shows **VERSION 0.37** (or 0.37b /
   0.37c).
   1. Click **START SERVER** and wait until it says **RUNNING**.
   2. Click **ENTER REALM**.
   3. Your own local account is created automatically the first time.
6. **Create your bots** with the bot buttons in the launcher. Every new install starts with an empty world and
   default settings. [QUICK COMMANDS.txt](QUICK%20COMMANDS.txt) has the shortcuts and in-game commands.

**Want the new client too?** Run `GET-OFFLINE-DAOC-v0.37c.cmd` from the v0.37c release and choose **1 = Update my
game**: if your game doesn't have the new client yet, it offers to add it. Your saves stay as they are, and both
clients play them.

The full walkthrough, with troubleshooting, is in [docs/PLAY.md](docs/PLAY.md). The new client has its own guide:
[docs/NEW-CLIENT.md](docs/NEW-CLIENT.md).

> **Code > Download ZIP** on this page gives you the *source code*, not the playable game. Use the
> downloader from a release page to get the game.

## Already playing an older version?

**0.36, 0.36b or 0.36c:** just update (step 4, choice 1). Nothing else is needed.

**0.35 or older:** install 0.37 (choice 2), then bring your characters, account, items, money, houses **and bots**
over with **IMPORT PROGRESS FROM OLD OFFLINE DAOC.cmd** in the new folder:
1. Close the old and the new game.
2. Run the import tool from the new folder.
3. Pick your old folder.

The tool never changes your old folder, and it backs up the new one first. It works with v0.3,
v0.31, v0.31b, v0.32, v0.32b, v0.33, v0.33b, v0.34, v0.34b, v0.35, v0.35b, v0.36, v0.36b and the "new class test"
builds. In 0.37c, both clients then have your progress. See [docs/TRANSFER-PROGRESS.md](docs/TRANSFER-PROGRESS.md).

## Older versions

Every earlier release is still available and unchanged:
- [v0.36](https://github.com/shadowofze/OfflineDAoC/releases/tag/v0.36),
  [v0.36b](https://github.com/shadowofze/OfflineDAoC/releases/tag/v0.36b) and
  [v0.36c](https://github.com/shadowofze/OfflineDAoC/releases/tag/v0.36c): the full downloads 0.37 updates.
- [v0.35](https://github.com/shadowofze/OfflineDAoC/releases/tag/v0.35) and
  [v0.35b](https://github.com/shadowofze/OfflineDAoC/releases/tag/v0.35b).
- [v0.34](https://github.com/shadowofze/OfflineDAoC/releases/tag/v0.34) and
  [v0.34b](https://github.com/shadowofze/OfflineDAoC/releases/tag/v0.34b).
- [v0.33](https://github.com/shadowofze/OfflineDAoC/releases/tag/v0.33) and
  [v0.33b](https://github.com/shadowofze/OfflineDAoC/releases/tag/v0.33b).
- [v0.32](https://github.com/shadowofze/OfflineDAoC/releases/tag/v0.32) and
  [v0.32b](https://github.com/shadowofze/OfflineDAoC/releases/tag/v0.32b): Darkness Falls beta.
- [v0.31](https://github.com/shadowofze/OfflineDAoC/releases/tag/v0.31) and
  [v0.31b](https://github.com/shadowofze/OfflineDAoC/releases/tag/v0.31b).
- [v0.3](https://github.com/shadowofze/OfflineDAoC/releases/tag/v0.3).

Their download helpers are in [older-versions/](older-versions/), and their notes are in
[docs/history/](docs/history/). Each release's source code stays on its own branch and tag (for
example `release/v0.36` and `v0.36b`), so older code is always there to compare.

## For developers and AI assistants

- **Where to start:** [AGENTS.md](AGENTS.md) (also read by Claude as [CLAUDE.md](CLAUDE.md)) has
  the project rules.
- **Guides:** [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) covers building and testing, and
  [docs/LLM-QUICKSTART.md](docs/LLM-QUICKSTART.md) covers customizing with your own AI.
- **Where the code is:**
  - `source/server`: the game server and all bot AI.
  - `source/tools`: the launcher, the progress importer and the release tools.
  - `source/new-client`: the new client (Godot 4 project and its content pipeline). See its
    [README](source/new-client/README.md).
  - `tools/pet-art`: the art pipeline used for the custom pets, weapons and spell effects.
- **Forking:** you don't need permission to fork this repository and make it your own. Keep the
  existing licenses and credits ([THIRD_PARTY.md](THIRD_PARTY.md)).

## Good to know

- **What the download includes:**
  - the full game client (0.36c: both clients)
  - clean world data and navigation meshes
  - the launcher and server
  - the progress import tool
  - the bundled .NET runtime
  - the source code

  It does **not** include anyone's account, characters, bots or settings. Every install makes its
  own.
- **Raids are still being tuned.** Bot raids have finished Darkness Falls, Summoner's Hall and
  Tuscaran Glacier, but not yet every epic dungeon to its last boss, and no relic has been captured
  by a bot army yet. See [Known issues](https://github.com/shadowofze/OfflineDAoC/discussions/4).
- **AI-written code can have bugs.** Keep a backup of your save before installing a new build or
  mod. There's no guarantee against regressions.
- **Licensing:** the original game client is a binary dependency. This repository has the server,
  tools, customization source and the new client's source, not the original client's source code.
  The new client's content is converted from the original client's files and ships only in the 0.36c
  download. The server's license doesn't
  relicense third-party client assets or dependencies. See [THIRD_PARTY.md](THIRD_PARTY.md).
- **Local only:** the default setup is for one PC. Running a public multiplayer server would need
  its own security work.

## Videos

[![Optional Hibernian Sluaghbinder class](https://i.ytimg.com/vi/EowrCcjigBY/hqdefault.jpg)](https://www.youtube.com/watch?v=EowrCcjigBY)
[![Raids and realm events](https://i.ytimg.com/vi/zmh7YkajRx0/hqdefault.jpg)](https://www.youtube.com/watch?v=zmh7YkajRx0)

## Changes

See the [changelog](CHANGELOG.md).
