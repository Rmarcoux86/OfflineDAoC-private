# The Gauntlet: Expedition endless mode (execution plan)

Written 2026-10-25 for implementation by Claude Opus 5.5 agents. The owner (Ryan) approved the shape below in chat; the
decisions he made are marked **owner decision**. Anything not decided here is the implementing agent's call, and the
agent writes the call down in this file under "Decisions made during implementation".

## What it is

After a run wins act 5 (Preesh dies), the Keeper at the camp offers **the Gauntlet**: an opt-in, round-based mode in a
fixed arena where the player duels the five act bosses as real class characters, round after round, each harder than
the last. Depth reached is the score. It is a second game the run builds up to, not "act 6".

The five champions and their classes are the designed class bosses in `docs/EXPEDITION-CLASS-BOSSES.md`: Toothmouth
(Bard), Ios (Valewalker), Preesh (Blademaster), Drunkstains (Armsman), Gwyraan (Druid). Their looks are already built
(`gameutils/Expedition/ExpeditionBossLooks.cs`); their abilities are not.

## Owner decisions (fixed)

- **Arena:** the Master Level 10 arena, the one that looks like you are floating in space. Its region, zone and
  coordinates are NOT known in code (teleport destinations live in the runtime database's Teleport table, not in the
  repo). **First step of Phase 0: ask the owner to stand in that arena and send the output of `/loc`** (region id,
  zone, x, y, z), plus a second `/loc` from where the champion should walk in (the gate). Record them in
  `gameutils/Expedition/Gauntlet/GauntletArena.cs` as constants with the owner's words in a comment.
- **Death, for now:** no permadeath in the Gauntlet. A player who dies releases at the arena entrance, keeps the
  character, the champion is removed and the round resets to its start. This is so the owner can test. Permadeath may
  come later; build the death handling so the rule is one switch (`GauntletRules.DeathEndsRun`).
- **No Hall of Fame work in this plan.** The scoreboard is a separate task (groundwork exists: `PlayedSecondsAtWin`).
- **Champions as real class fighters, not dressed monsters** ("dueling some bot versions of the act bosses"). Approach
  A below is tried first; B is the fallback. The agent decides at the end of Phase 1 and records why.
- **Patch notes** (`fork-release/build-history.json`) must not name the champions or describe their signatures; the
  owner wants the bosses to be a surprise. "A new challenge after act 5" level of detail only.

## Standing rules for the implementing agents

- Work on `main`, push to the remote `private`. **Do not start a build**; the owner says "build" when he wants one.
  Builds are started with `gh workflow run fork-release.yml -R Rmarcoux86/OfflineDAoC-private --ref main -f version=fork-vNN`.
- Code style: braces on their own lines for every `if`/`else`/loop; a blank line before `return`, `if` and other
  control flow; inline 1–3 line logic rather than extracting helpers; verify every API in the code base before using
  it (grep it; do not guess from memory of other DoL forks); keep each file's existing line endings (many are CRLF;
  edit bytes, never rewrite a file through a tool that normalises endings).
- Tests live in `source/server/Tests/UnitTests/UT_Expedition*.cs` (NUnit, no world, no database). Every pure piece
  (round tables, champion rosters, escalation math, reward math) gets a test. The fork-tests count is reported in the
  build annotations; it must not go down.
- Loggers: `LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType)` and guard with `log.IsXEnabled` where a
  test host might run the code without a logger (see `ExpeditionArrival`).
- Everything Gauntlet goes under `source/server/GameServer/gameutils/Expedition/Gauntlet/` and the namespace
  `DOL.GS.Expedition.Gauntlet`, except the NPC classes, which follow the existing pattern in `gameobjects/CustomNPC/`
  (`ExpeditionBoss.cs`).

## Existing pieces to build on (read these first)

| Piece | Where | Why it matters |
|---|---|---|
| Run state and the win | `ExpeditionManager.cs` (`StatusWon`, `OnBossKilled` near line 785, `OnPlayerDied` near 922) | The Gauntlet starts from a won run; death handling hooks here |
| Camp arrival and settling | `ExpeditionArrival.cs` (`MoveToCamp`, `EnsureSafeCamp`), `ExpeditionManager.WhenSettled` | Reuse for the arena teleport and the "player is loaded" callback |
| Boons | `ExpeditionBoons.cs` (`OfferAtActBreak`, `OfferLines`, `ApplyAll`) | Round-5 boon picks reuse the offer machinery |
| Shrines/buffs | `ExpeditionShrines.cs`, `ExpeditionBuffs.cs` | Rest-phase shrine |
| Elite affixes | `ExpeditionElites.cs` | Rounds 10+ put an affix on the champion |
| Loot and uniques | `ExpeditionLoot.cs` (`UtilityBudget`, boss drops), `ExpeditionUniques.cs` (`Pick`, `Build…`) | Round rewards |
| Boss NPC | `gameobjects/CustomNPC/ExpeditionBoss.cs`, `ExpeditionBossLooks.cs` | Approach B base; looks for A too |
| Bots | `bots/GameBot.cs` (`GameBot : GameNPC, IGamePlayer`), `bots/BotBrain.cs` (`IControlledBrain`), `bots/BotManager.cs` (`CreateBot(owner, …)`, `SpawnBot`), `bots/autonomous/*` (ownerless "world bots") | Approach A: a champion is a hostile bot |
| Attack rules | `serverrules/NormalServerRules.cs` `IsAllowedToAttack` (same-realm block near line 56; `GameBot { IsOnStableMasterRoute }` exclusions near line 24) | Hostility between a player and a bot of the same realm must be allowed inside the arena |
| Mob aggro and stealth | `ai/brain/StandardMob/StandardMobBrain.cs` (`CheckPlayerAggro`), `GamePlayer.UncoverStealthAction` | Champion must find a stealthed player (Ios vs an Infiltrator) |
| Commands | `commands/playercommands/recover.cs`, `Junk.cs`; `/run` handler (grep `"&run"`) | Pattern for `/gauntlet` |
| Server events | `[GameServerStartedEvent]` statics (e.g. `gameutils/Expedition/ExpeditionManager.cs`) | Registration |

## Phase 0: discovery (one agent, read-only, half a day)

Deliverable: a section "Phase 0 findings" appended to this file. No code.

1. Get the arena `/loc` values from the owner (see owner decisions). If he is not available, continue with a placeholder
   region/zone and leave `GauntletArena` constants marked `TODO owner /loc`.
2. **Hostile bot feasibility (Approach A).** Answer, with file:line evidence:
   - Can a `GameBot` exist with no owner? What breaks (`BotBrain.GetPlayerOwner()`, follow/travel logic, stable-master
     routes, the autonomous economy, death handling in `GameBot` and `BotManager`)? The autonomous "world bots" are
     ownerless and alive in the world already; what do they use instead of an owner?
   - Where would hostility be decided? `IsAllowedToAttack` blocks same-realm attacks; find the narrowest switch: an
     `IGauntletCombatant` marker interface checked first in `NormalServerRules.IsAllowedToAttack`, or giving the
     champion `Realm = eRealm.None` (check what `None` does to attack rules, con colours, name colour, and whether a
     `None` bot still buffs and uses styles).
   - Does `BotBrain` pick targets from an aggro list the way `StandardMobBrain` does, or from owner commands? Find the
     entry point to make a bot attack a named enemy and keep attacking (grep `Attack(`, `SetTarget`, `Engage`).
   - Does a bot use realm abilities, potions and styles on its own? Note the methods (so signatures can be curated).
   - Leash: how do bots or mobs return to a spot (`WalkToSpawn`, `MaxDistance`, `ReturnToSpawnPoint`)?
   - Death: what happens when a `GameBot` dies (respawn? deletion? `offline_world_bots` rows?). The champion must be a
     throwaway: no row in `offline_world_bots`, no persistence, removed on round end.
3. **Approach B feasibility** (`ExpeditionBoss` + styles + curated spells + custom brain): confirm the recipe in
   `EXPEDITION-CLASS-BOSSES.md` still matches the code (style lists, `Spells` after `Level`, `LeftHandSwingChance`).
4. **Player death without run end.** Trace `ExpeditionManager.OnPlayerDied` and `GamePlayer.ProcessDeath` for the
   hardcore path (`HCFlag`, `DeathCount`, deletion at next login in `HardCoreLogin`). Identify the exact point where a
   "Gauntlet death" can be intercepted so the character is kept, `DeathCount` is not incremented (or is reset), and
   the release goes to the arena entrance. Note: after the act-5 win `HCFlag` is already false and `HCCompleted` true
   (`ExpeditionManager` near line 790), so the hardcore deletion should not apply; verify.
5. **Instance or shared?** Decide whether the arena is used in the shared world region (simple; two players'
   Gauntlets would collide) or as an instance (`Region`/`BaseInstance`, grep `Instance` under `world/`). Recommend
   shared for the prototype with a single-occupancy lock (`GauntletArena.Occupant`), instance later if co-op needs it.

## Phase 1: prototype, one round (decides A vs B)

Deliverable: `/gauntlet start` for a player whose current run is `StatusWon`: teleport to the arena, fight **Ios**
(melee only, no pet, simplest), win or die, round resets. Pushed, not built; then tell the owner it is ready to build.

Files (new):
- `Gauntlet/GauntletArena.cs`: region, zone, centre, gate, entrance (release point), radius; `IsInside(GameObject)`.
- `Gauntlet/GauntletRules.cs`: `DeathEndsRun = false` (owner decision), rest seconds, leash radius, round timings.
- `Gauntlet/GauntletState.cs`: per-player state (round, champion alive, timers); in-memory dictionary keyed by
  character id; also persisted as a JSON column on `OfflineExpeditionRecord` (`GauntletJson`) so a relog resumes at
  the same round (prototype: round number and depth only).
- `Gauntlet/GauntletManager.cs`: `Start(player)`, `BeginRound`, `OnChampionDied`, `OnPlayerDied`, `Stop`. Registers
  on `GameServerStartedEvent`. Hooks: the Dying event the same way `ExpeditionManager` does; `GameLiving` death of the
  champion via its own `Die` override calling back.
- `Gauntlet/GauntletChampions.cs`: the roster (name, class id, race model, spec keys, signature id) as data; Phase 1
  uses only Ios.
- `gameobjects/CustomNPC/GauntletChampion.cs`: Approach A: a subclass of `GameBot` (or a wrapper around one) with no
  owner, hostility marker, leash, no persistence, `Die` override. Approach B: a subclass of `ExpeditionBoss`.
- `commands/playercommands/gauntlet.cs`: `/gauntlet start|leave|status`. Prototype only; Phase 4 adds the Keeper NPC.

Behaviour:
1. `/gauntlet start`: run must be `StatusWon` for this character (read `OfflineExpeditionRecord`), player alive, arena
   free. Teleport to the arena centre with `ExpeditionArrival`-style safety (`WhenSettled` before spawning anything).
2. Round 1 begins: a chat line and `SendCustomTextWindow("The Gauntlet", …)` announcing the champion; the champion
   spawns at the gate, walks to the centre (`WalkTo`/`PathTo`), buffs for 10 s if it has buffs, then attacks the
   player and only the player.
3. Champion death: message, a plain chest or ground loot (reuse `ExpeditionLoot` boss drop for one Rare), `Round++`,
   rest timer of `GauntletRules.RestSeconds` (90) shown as a countdown line every 30 s, then round 2 spawns Ios again
   (prototype) one level of escalation up (see Phase 2 table; the prototype applies only "+1 realm rank" if A, or
   "+5% stats" if B).
4. Player death: `DeathEndsRun == false` → the run is untouched, the character is kept, no `DeathCount` change, the
   champion is removed, release moves the player to the arena entrance, the round restarts on `/gauntlet start`
   (prototype) with the same round number.
5. `/gauntlet leave`: removes the champion, teleports to the act-5 camp (`ExpeditionArrival.MoveToCamp` with the won
   run's `CurrentAct`), keeps depth.
6. Logout mid-round: champion removed on `GamePlayerEvent.Quit`/region change; on login with Gauntlet state, the player
   is at the entrance and `/gauntlet start` resumes at the saved round.

Acceptance (the agent verifies by code reading and tests; the owner verifies in game):
- Ios attacks the player without the player attacking first, uses scythe styles, stays within the leash, and is removed
  when dead or when the player leaves.
- A stealthed player gets uncovered by Ios within the normal uncover range (rule in `GamePlayer.UncoverStealthAction`).
- Dying does not end the run or delete the character.
- No row appears in `offline_world_bots`, `DOLCharacters` or any bot table for the champion.

A vs B decision: record it under "Decisions made during implementation" with the blocking issue if A failed.

## Phase 2: the round loop and escalation

- Escalation table (data, unit-tested), applied to whatever Phase 1 chose:

| Rounds | Champion | Extras |
|---|---|---|
| 1–4 | Level 50, RR1, Magic-tier gear | styles only |
| 5–9 | RR3, Rare gear | realm abilities: Purge, Ignore Pain, Charge (A) / +10% stats (B) |
| 10–14 | RR5, Epic gear | potions (A); one Elite affix from `ExpeditionElites` |
| 15–19 | RR7, Unique-tier gear | one helper bot (healer or buffer) beside it |
| 20+ | RR10, +5% stats per round over 20 | two helpers; a Trial every round |

- Round order: seeded by the run's seed; no champion repeats until all five have been faced (same rule as the act
  bosses: `ActGenerator.BossTitle` skips faced bosses).
- Rest phase: 90 s; a Shrine of the player's choice (`/gauntlet shrine might|grace|warding|vigor`) every 5 rounds,
  applying the lesser buff through `ExpeditionBuffs` for the next 5 rounds.
- Depth: `GauntletState.Depth` (highest round beaten) saved in `GauntletJson`; `/gauntlet status` shows it.
- `/run journal` gains a "Gauntlet: round N, depth D" line while in the Gauntlet.

## Phase 3: the five champions and their signatures

Each champion gets its class kit (A: the bot's own class AI with a curated RA list; B: style list + curated spells +
small brain overrides) and one **signature** that forces a different answer from the player:

| Champion | Class | Signature |
|---|---|---|
| Ios | Valewalker | at 50% health: 20 s melee-speed and damage burst (a self-buff with an icon so it is readable) |
| Drunkstains | Armsman | polearm bleeds; Purge once; taunt that forces target for 3 s |
| Toothmouth | Bard | damage add and endurance regen on himself; mezzes a player who stays at range > 300 for 5 s |
| Gwyraan | Druid | a nature pet (`Fellwood`, monsters.csv 97) that heals him; nearsight on the player at round start |
| Preesh | Blademaster | dual wield, high evade; Vanish once per fight (stealth 5 s, reappears behind the player) |

Trials (every 5th round, named in the announcement; every round from 20): "Two at once" (two champions), "In the dark"
(nearsight on the player for the round), "Your own arms" (the champion gets the ToA percentages of the player's worn
uniques: read them with `ExpeditionUniques.ToaPropertiesFor`), "No potions" (the player's potion use is refused for
the round: hook the potion use path in `GamePlayer.UseSlot` with a Gauntlet check).

Looks: reuse `ExpeditionBossLooks.Apply(npc, name)` for all five. Preesh is in the doc but not yet in
`ExpeditionBossLooks`; add him there (models in the doc).

## Phase 4: rewards and the Keeper

- **Gauntlet Marks**: an int on `GauntletState`, +1 per round, +3 per Trial. Shown in `/gauntlet status`.
- Chest per round: one Rare at rounds 1–4, Epic 5+, and a **guaranteed late-tier unique every 3rd round** through
  `ExpeditionUniques.Pick(rng, eUniqueTier.Late, found)`.
- Every 5th round: a boon offer from the full list (not limited by earlier declines), via `OfferAtActBreak`-style call
  with a Gauntlet act number (e.g. 100 + round) so logs read clearly; shown with `OfferLines`.
- **The Keeper** NPC (`gameobjects/CustomNPC/GauntletKeeper.cs`): spawned at the act-5 camp when the run is won
  (`ExpeditionManager` win path), interact text explains the Gauntlet, `[Enter the Gauntlet]` starts it, and a shop
  (Marks → Mythirian upgrades once the Mythical-slot item exists; until then, Marks → a unique exchanger token and a
  boon reroll). The Keeper replaces `/gauntlet start` for normal play; the command stays for testing.
- Trophies (cosmetic): the champion's weapon model as a drop that `ExpeditionUniques`' look code can apply to the
  player's weapon (`/gauntlet trophy` to apply). Optional; do last.

## Phase 5: co-op (after the owner has played solo rounds)

- Group members at the Keeper enter together; rounds scale: 2 players → two champions from round 1, helpers one round
  earlier. All leave together; a dead member releases to the entrance and rejoins at the next round.
- Likely needs the arena as an instance if two groups can be in the Gauntlet at once; revisit the Phase 0 decision.

## Phase 6: guest champions (owner 2026-10-26)

Owner decision: a player who beats the Gauntlet can submit their character to become an opponent for everyone. A
submitted champion appears as a **guest round**: each run, one random SmuG member (never Preesh, who stays last) is
replaced by a random submitted champion. The guest keeps the player's **exact gear, realm abilities and boons**.

- Snapshot (`/gauntlet submit`, offered in the victory window): name, class, race, gender, spec points, realm rank,
  every equipped item in full (all bonus lines incl. ToA, DPS/AF/SPD, procs and charges, models, glows, colors), trained
  realm abilities with levels, and the run's boons. Versioned JSON.
- Sharing: the launcher opens a pre-filled GitHub issue; a workflow validates the JSON and appends it to a guest roster
  file in the repo that ships with every update (the same pipeline the Hall of Fame needs). A full gear snapshot is a few
  thousand characters; if it exceeds the pre-filled issue URL limit (~8,000), `/gauntlet submit` writes a file the
  player attaches to the issue instead.
- Spawning: a GauntletChampion built from the snapshot (class/race/gender/spec via the ownerless constructor with a
  snapshot BotSpec; inventory rebuilt item by item instead of generated; no capped template, the gear is the gear).
- Realm abilities: passives are granted as-is. Bot AI only uses Purge, Ignore Pain, Second Wind and First Aid today
  (AutonomousBotRealmAbilities); other actives are present but unused until the bot AI learns them.
- Boons: port each boon to work on a GameBot where it matters in a duel (stat/combat boons first); boons tied to the
  player (looting, shrines, the act tracker) are skipped with a note.
- Guest champions use the class's generic bot AI (no hand-made signature).

## Open questions for the owner (ask when relevant, do not block on them)

1. Exact `/loc` of the arena centre, gate and entrance (blocks Phase 1 only).
2. Should Gauntlet rounds grant realm points like boss kills do (`ExpeditionRealm.OnBossKilled`)? Default: yes, one
   boss kill's worth per round, doubled on Trials.
3. When permadeath comes back on, does a Gauntlet death delete the character (as in the acts) or only end the Gauntlet?

## Decisions made during implementation

- 2026-10-25: **Approach A (hostile GameBot)** chosen from the Phase 0 findings: an ownerless GameBot with
  `Realm = eRealm.None` is attackable by and aggressive to any player with no server-rule changes, and can be made a
  throwaway. Arena is a shared region with a single-occupancy lock for the prototype. The arena /loc is not known yet;
  `GauntletArena` uses placeholders marked `TODO owner /loc`.
- 2026-10-25, Phase 1 code (not built, not tested in game):
  - `GameBot` gained a protected ownerless constructor `(classId, name, raceId, genderId, level, spec = None)`: the
    owner constructor's setup minus the owner, Quit hook, DB and registrations. `GauntletChampion` sets
    `Realm = None` after it (gear is already generated).
  - The arena constants are placeholders with `RegionId = 0`; `GauntletArena.IsConfigured` is false and
    `/gauntlet start` refuses with "The Gauntlet arena is not set up yet." until the owner's `/loc` is filled in.
  - Prototype champion is always Ios (`GauntletManager.PrototypeIosOnly`); flip it to use
    `GauntletChampions.ForRound(seed, round)`. Escalation applied: realm rank (`RealmLevel = (RR-1)*10`) and the
    round-21+ stat bonus (base stats). Gear tier, RAs, potions, affixes and helpers are Phase 2.
  - `ExpeditionBossLooks.Apply` replaces an NPC's inventory with a model-only template, which would strip a bot's real
    gear (weapon type drives styles). The champion keeps its generated `BotInventory` and only copies the template's
    models onto its items (each item gets its own template copy so cached templates are not changed).
  - `GameNPC.AddToWorld` resets `SpawnPoint` to where the NPC appears, so the manager sets the spawn point (leash
    centre) to the arena centre after `AddToWorld` at the gate. The brain walks in (20 s cap), lets the idle state cast
    buffs for `ChampionBuffSeconds`, then fights; a hit from the challenger's side engages at once.
  - A stealthed challenger is hunted: the champion walks to the player's position (half speed, re-plotted every 3 s) so
    `UncoverStealthAction`'s normal NPC rule can reveal them; it is not given aggro on a hidden player.
  - Release hook placed after `Release`'s switch (overrides the computed point) rather than before it, to keep the edit
    to one block. Auto-release is a timer started from the Dying handler calling `Release(eReleaseType.Normal, true)`.
  - `GauntletRules.DeathEndsRun = true` ends the Gauntlet session at death and lets the normal death rules apply
    (open question 3 decides more).
  - Session ends (champion removed, round saved, arena freed) on quit, linkdeath, leaving the arena's region and
    `/gauntlet leave`. A login inside the arena is moved to the entrance. `/gauntlet leave` uses
    `ExpeditionArrival.MoveToCamp(player, null, plan)` with the won run's saved act and writes a moved camp back.
  - A champion kill gives one `ExpeditionLoot.DropFor(player, champion, Rare)` (Rare or better). Normal kill rewards
    stay, so the player may also get experience and realm points for the bot kill (open question 2).
  - `GauntletChampion.cs` is LF like `ExpeditionBoss.cs` (the fork's own files in that folder are LF; the stock ones CRLF).
  - Unit tests for `GauntletSave.Parse`/`ToJson` are not written (the test files belong to the data agent).
- 2026-10-25, first three signatures (owner's design, not built, not tested in game; `GauntletChampion.cs`, one hook in
  `BotMeleeStylePolicy.Select`, tests in `UT_ExpeditionGauntletSignatures.cs`):
  - Signatures are data (`ChampionSignature`: priority, behind opener, side chain, polearm chain, shield style) and
    `PriorityStyles` is what the brain wants now. `Select` checks the champion's priority before a queued style and
    lets those styles (only those) through the positional exclusion; `StyleProcessor.CanUseStyle` still decides.
  - Fixed builds: Ios = stock Valewalker variant 3 (50 Scythe/34 Arboreal/26 Parry), Drunkstains = stock Armsman
    TwoHandHybrid without its 3 Crossbow; a missing polearm, slashing one-hander or shield is generated with
    `BotEquipment.SetWeaponROG`.
  - Sprint: `GameBot.Sprint`'s effect does not speed up an NPC (`MaxSpeedCalculator` applies the 1.3 only to
    `GamePlayer`), so the champion's sprint is `MaxSpeedBase` × 1.3 for at most 4 s per step, no endurance cost.
  - The stuck simulation shows a challenger on a perfect /stick is never hittable from behind; with a heading update
    every U ms the window after running through is under U (worst 90–220 ms for U = 100–500 ms, average 0–155 ms per
    run-through), a few percent of a 4–5 s swing. So Ios goes behind only when the challenger is not facing her (and
    not stunned, stun-immune or mezzed), every 20 s; a stun (hers or any) sends her, and Preesh, to the side chain
    instead. Stun immunity is `CCSpellHandler.CheckSpellResist`'s test: the `eEffect.StunImmunity` effect (a style
    stun leaves 5× its duration on a player) or the StunImmunity ability.
  - Superseded the same day (owner: "make sure in tests you can get that stun off"): against a challenger facing her
    (within 30°), Ios times a run through to her swing. Per loop tick (30/s) the server applies player packets first,
    then brains, then swings, then NPC movement, so a swing sees her where the previous tick moved her and the
    challenger's heading as last sent. The brain thinks every tick while waiting and starts the walk (75–100 % of
    sprint speed) on the tick that puts her half a tick's travel past the challenger on the tick before the swing
    (`GauntletPositioning.RunThroughSpeed`), stopping 20 units past. It fails only if the client sends a heading in
    that last tick: simulated success 65 % (heading every 100 ms), 77 % (150), 83 % (200–210, the ~210 ms position
    cadence `PlayerMovementMonitor` assumes), 87 % (250), 89 % (300), 93 % (500); 100 % with no heading during the run.
    A challenger facing elsewhere still gets the walk to the spot behind.
  - Drunkstains holds the shield and takes up the polearm when the swing is due within a think interval + 150 ms
    (the next swing time is the last swinging weapon's interval and survives `SwitchWeapon`); Slam replaces the
    polearm swing within 3 s of a block when `CanUseStyle` accepts it.

## Phase 0 findings (2026-10-25)

**Hostile bot (A).** `new GameBot(owner, …)` throws on a null owner (bots/GameBot.cs:2262) and takes level/Quit hook
from the owner; BotManager.CreateBot/SpawnBot need an owner and write DB rows. Never use the autonomous-world-bot
constructor (GameBot.cs:2328; registry, travel, DB saves). The DB-free precedent is `/spawn`
(commands/playercommands/TemporaryGroupCommands.cs:232-266): new GameBot, coordinates, AddToWorld; a plain
`new BotInventory()` never saves. Changes for A:
1. GameBot.cs: protected ownerless constructor copying :2263-2313 with SetRaceAndRealm(null), Level 50, optional spec,
   EquipBot(50), no Quit handler. Set `Realm = eRealm.None` AFTER gear is generated (gear is picked by realm).
2. Realm None needs no rule change: the owner swap in NormalServerRules.cs:33-38 is skipped without an owner, None vs a
   player realm is "different realm" (:57), self-buffs still land (:104), BotBrain.CanAggroTarget allows it (:386).
3. Targeting: BotBrain has an aggro list (:150, AddToAggroList :186, Attack(target) :38-53); AGGRO drops after 6 s
   without combat (:2906-2918), so the champion brain re-adds the challenger every Think and filters AddToAggroList to
   the challenger and their pets.
4. Kit: styles via BotMeleeStylePolicy (BotBrain.cs:3494), spells CheckSpells/Offensive/Defensive/Heals
   (:3792/:4138/:4278/:4755) built by SetBotSpells (GameBot.cs:3647), pets via AutonomousPetSupport.Maintain (:1465).
   Realm abilities only through AutonomousBotRealmAbilities' private Grant (autonomous-only, :180/:253); add a public
   GrantFixed. No bot potion code exists. The constructor picks a random spec (GameBot.cs:2284), so take a spec param.
5. Leash: GameNPC SpawnPoint/TetherRange/IsOutOfTetherRange/ReturnToSpawnPoint (GameNPC.cs:718-908); BotBrain ignores
   TetherRange, so do it in the brain subclass (precedent: SluaghbinderBotBrain : BotBrain; SetOwnBrain is public).
6. Death: GameBot.ProcessDeath (:941) revives after 20 s unless RetainCorpseForResurrection is false (override it;
   GameNPC.ProcessDeath then deletes the body, RespawnInterval -1). Kill rewards stay normal.
7. Risks to check in game: the con/name colour of a Realm-None bot; AGGRO 6 s dropout stutter. Mezzed players are not
   hit by bots (BotPvpCrowdControl.cs:23).

**Death without run end.** A won run is out of ExpeditionManager.Runs (:802), so OnDying returns before OnPlayerDied;
HCFlag is false after the win, so no deletion. But a Realm-None killer takes the normal monster-death path
(GamePlayer.cs:6064): DeathCount++, xp loss, con loss, gravestone. DeathCount is dangerous later (hardcore.cs:35 and
/run new refuse or delete with DeathCount > 0). Two hooks in GamePlayer: (1) in ProcessDeath's else branch at :6064,
skip the xp/DeathCount/con block when `GauntletManager.IsGauntletDeath(this)`; (2) in Release just before
`switch (m_releaseType)` (:1521), use the Gauntlet's release point when it supplies one. Release(eReleaseType, bool) is
public (:1425) for an auto-release timer. Keep the entrance in the arena's own region (cross-region release sets
RELEASING_PROPERTY and skips Revive/Released notifications, :1748-1751). The Gauntlet must read "won" from
OfflineExpeditionRecord (GetRun is null for won runs) and refuse if HCFlag is on.

**Arena.** Instances exist (WorldMgr.CreateInstance; BaseInstance) but get new zone ids (no navmesh: nav loads by
zone id), and login inside one moves the player to bind. Prototype: shared region + `GauntletArena.Occupant` lock.
Safe teleport: ExpeditionArrival.MoveToCamp pattern and ExpeditionManager.WhenSettled (internal, :642).

**Approach B fallback** still matches the code (ExpeditionBoss is sealed: unseal or sibling; SkillBase.GetStyleList
:2722; GAMENPC_CHANCES_TO_STYLE 20; Spells after Level; in-combat heals need a StandardMobBrain subclass overriding
AttackMostWanted to CheckSpells(Defensive) first).

**Commands.** Model on commands/playercommands/Run.cs ([CmdAttribute("&run", …)], AbstractCommandHandler,
ICommandHandler); registration is automatic (ScriptMgr.cs:178-189). Run.cs and Junk.cs are LF.
