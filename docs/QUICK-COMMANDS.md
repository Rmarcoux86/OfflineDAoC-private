# Offline DAoC quick commands

These are the everyday Offline DAoC commands, not the exhaustive list of every
inherited game/server command. They are registered for normal players in this
release; some still have level, target, party or state requirements.

## Launcher bot generation

Click **+ Lv.1** or **+ Lv.50** under a faction to create bots for that faction.
Hold **Ctrl** for **+100 bots** per click, or **Shift** for **+10**. Without a
modifier, create one bot. Start small and increase population for your PC's capacity.

## Travel and finding mobs

| Command | What it does |
|---|---|
| `/tele X` | Teleport to a gamebot; replace X with its name. You can also right-click bot names in the launcher to teleport. |
| `/mobs X` | List mob names at a level; replace X with the level number. |
| `/tele mob X` | Teleport to a mob spawn; use the mob's exact name for X. Dungeon targets use the configured entrance approach where applicable. |
| `/tc` | Teleport to your realm's Realm Exchange NPC. |
| `/stables` | List every stablemaster horse route in your realm, one page at a time, including the neutral dragonfly, gryphon and wyvern routes. `/stables X` shows page X. |
| `/stables classic` | Jump to your realm's Classic stable routes. |
| `/stables si` | Jump to your realm's Shrouded Isles stable routes. |

## Companion groups, grinding, and raids

| Command | What it does |
|---|---|
| `/fish` | Face open water with a fishing pole and cast your line; you keep casting after each catch until you move or type `/fish stop`. `/fish active` makes you reel in each bite yourself (type `/fish` within 4 seconds), `/fish stats` shows your skill and best catch, `/fish pole` gives you a pole if you have none. `/fish spots` lists fishing spots and `/fish go <number>` travels to one in your realm; `/fish mark <name>` saves the shore you stand on (facing water) as a new spot and `/fish unmark <number>` removes one you marked. |
| `/grind` | Start automated grinding with a companion-bot group, including for AFK use. |
| `/pull` | Order your companion group and pets to engage your selected enemy. When possible, a tank makes first contact before the rest of the group joins the fight. |
| `/train <line> <level>` | Train a specialization to the chosen level using your available specialization points. Select a valid trainer for your class first. |
| `/spawn` | Open the menu of valid companion bots to summon. |
| `/spawn X` | Summon a companion by class name instead of using the menu; useful for macros. |
| `/raid 40` | Enable a 40-member companion raid. Use **before** `/spawn`. Requires level 50; the total includes you. |
| `/raid 80` | Enable an 80-member companion raid. Use **before** `/spawn`. Requires level 50; the total includes you. |
| `/aggressive` | Companions automatically engage what you attack, while retaining defensive reactions. |
| `/defensive` | Companions stay near you and engage nearby threats, letting you pull mobs back to the group; useful for harder content. |

`/spawn 40` and `/spawn 80` are **not** the raid-size commands. Use `/raid` first.
The aggressive/defensive commands control your companions, not autonomous gamebots.
The `/raid` command does not implement Darkness Falls raid AI. In the v0.32/
v0.32b Darkness Falls Beta, Legion, the hardest level-70+ encounters,
unreachable flying targets, and unverified routes are excluded from ordinary
autonomous bot goals.

## Expedition mode

| Command | What it does |
|---|---|
| `/run new [seed]` | Start a seeded roguelite run on a level-1 character with no realm points. Your Hardcore flag goes **on** (death ends the run and deletes the character) and you are taken to act 1's camp. The same seed gives the same expedition. |
| `/run status` | One line: act, level band, zone, objectives done and the act boss. |
| `/run journal` | Open the journal: seed, act, zone, level band, objectives and the latest entries. |
| `/run seed` | Show this run's seed, to share or replay it. |
| `/run abandon` | Give up the run after a confirmation; your Hardcore flag goes off. |
| `/run boons` | Show the three boons offered after an act boss, and the boons you have. |
| `/run reroll` | Swap the waiting boon offer for 3 different boons. 2 rerolls per run. |
| `/run boon <1-3>` | Take one boon of the offer; it lasts the whole run and is re-applied at login. |
| `/junk common`, `/junk magic`, `/junk rare` | Destroy Expedition items of that rarity and lower in your backpack for the merchant price (asks first; Epic, Unique and non-Expedition items are never touched). |
| `/junk auto off`, `/junk auto common`, `/junk auto magic` | Sell such drops on the spot instead of bagging them (saved per character; server setting `expedition_auto_junk`). |
| `/junk status` | Show the auto-junk setting and what each `/junk` would sell. |

For the complete advanced reference, see **ALL SERVER COMMANDS.txt** inside the
download. That file separates normal-player, GM and administrator registrations.
