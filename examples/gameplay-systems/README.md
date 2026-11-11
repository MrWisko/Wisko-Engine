# Gameplay Systems Showcase

One playable project that combines all 15 areas of Wisko's gameplay systems:
- navigation agents, dialogue and quests, the game clock and day/night, weather;
- crafting, building, sight and hearing, portals and checkpoints, a logic puzzle;
- a generated forest, equipment in an animated hand, a race;
- several cameras with a live security feed, curved paths, and saved games.

It is built only from Parts, three imported models (RobotExpressive, CesiumMan, Fox) and these scripts. Nothing here is
engine code. The scripts use only the public Lua API and the optional `Wisko.*` modules, which you can read, copy and
change. The API is documented in [docs/lua-gameplay-systems.md](../../docs/lua-gameplay-systems.md).

## Make the project

From the repository folder:

```
dotnet run --project Tests/Wisko.Tests -- --make-v5-demo "%USERPROFILE%\Documents\Wisko\Projects"
```

This creates the project **Gameplay Systems Showcase**:
- the scene, grouped in the World panel (Village, Guard Area, Vault, Sky Island, Race Course, Markers);
- the models, imported into `Assets/Models`;
- these scripts, in `Assets/Scripts`.

If a project with that name already exists, it refuses and changes nothing. Other projects are never touched. To
start over, delete the showcase from the Hub and run the command again.

## Play it

Open it from the Hub and press Play. A short flyover (a camera path) starts, then you are in the village.

| Key | Does |
|---|---|
| W A S D, Shift, Space | walk, run, jump |
| mouse, Tab | look; free / lock the mouse |
| E | talk to Mira, chop a tree / mine a rock, use the workbench, flip a switch, start the race at its post |
| 1–9 | choose in a conversation or the crafting menu (0 cancels a crafting job) |
| Q | equip / put away the axe (after crafting it) |
| B | build: a floor piece, then a wall piece, then off. R turns the piece, the left mouse button places it |
| T | next weather, in the order Clear, Cloudy, Rain, Clear, Fog, Clear, Storm, Clear, Snow (blended over 6 seconds) |
| M | show / hide the vault's security camera on the HUD |
| N | draw the guard's path, the navigation grid, the guard's view cone and the sky lift's path |
| C, Space | replay the flyover; skip it |
| F5, F9 | save, load (slot "Showcase") |
| H | show the controls |

## Things to try

- **Quests and dialogue.** Talk to **Mira** (by the well). Her options change with what you carry and what you have
  done, so they are conditional and written as plain tables.
  - "Firewood for Mira": bring her 5 wood.
  - "The Vault": open the vault and bring her the gem; it unlocks after Firewood.
  - "Builder" (build 3 pieces) and "Racer" (finish the race) are active from the start. The tracker at the top right
    shows the objectives.
- **Generation.** The **forest** east of the village is generated from a seed while you watch (a progress bar). It is
  the same forest every session. Chop trees for wood and mine rocks for stone; they grow back after a while.
- **Crafting.** At the **workbench**, turn wood into planks, then planks and stone into an axe. Crafting takes time.
  Cancelling gives the inputs back. "Sharpen axe" is an upgrade, not an item.
- **Equipment.** Press **Q** with the axe: it sits in the robot's right hand and moves with the walk and run
  animations. With the axe, a tree falls in one hit.
- **Navigation and perception.** The **guard** walks a route around the warehouse.
  - It sees you within its view cone if no wall is between you, and then chases you.
  - Running makes footstep noises: it hears them and comes to look.
  - Being caught sends you back to your last checkpoint. Press **N** to see its path and view.
  - The **fox** wanders around its home and runs away from you.
- **Logic puzzle.** The **vault** (west) opens while both red switches are on and the plate was pressed less than
  4 seconds ago. Flip both, step on the plate, then run in.
- **Camera feed.** Press **M** to watch the vault room through its security camera on the HUD, while you play.
- **Portals and checkpoints.** The purple pad on the plaza and the one on the **sky island** are linked. Arriving does
  not send you straight back. Touch the flags to set your respawn point; falling off the island brings you back.
- **Paths.** The yellow **sky lift** travels a curved path between the island and the ground and carries you.
- **Race.** Press E at the white post east of the village. After a 3 second countdown, run 2 laps through the three
  arches in order; a wrong arch is ignored and said. Your best time is kept in its own save slot across sessions.
- **Weather and time.** Press **T** for rain or snow: particles around the camera, fog and dimmer light. In a storm
  there are lightning flashes. The sun moves with the game clock: a game day lasts 4 minutes.
- **Saving.** Press **F5**, change things (move, craft, build, chop, advance quests), then press **F9**. Or press F5,
  stop Play, reopen the project, Play and press **F9**. The save is `Saves/Showcase.json` in the project folder.

## What is visible and what is not

- **Visible:** rain, snow, fog, the lightning flash (an exposure flash, with no bolt), the security feed (OpenGL),
  debug lines, and the sun and sky over the day.
- **Not drawn:** clouds ("Cloudy" only dims and greys the light), stars and the moon. Rain does not collide or splash.
- **Wind** slants the rain and pushes the crates in the village; nothing else feels it.

## Scripts

| Script | On | Shows |
|---|---|---|
| `Game.lua` | the invisible "Game" object | shared state, inventory table, quest log (`Wisko.Quests`), clock and day/night (`GameTime`, `Wisko.DayNight`), weather (`Wisko.Weather`), save/load (`GameSave`), HUD, security monitor (`Cameras` feed), navigation bake |
| `Player.lua` | "PlayerBody" | movement, interaction, equipment (`Wisko.Equipment`, bone `Palm2.R`), footstep noise (`Perception.EmitNoise`), building (`Wisko.Building`) |
| `Guard.lua` | "GuardBody" | patrol, chase, investigate (`Navigation` agent, `Perception` sensor and `OnNoise`), animation by speed |
| `Wanderer.lua` | the Fox model | wandering with a seeded `Random` and an agent in `"Transform"` mode |
| `Mira.lua` | "MiraSpot" | branching dialogue (`Wisko.Dialogue` with its presenter) |
| `Forest.lua` | "Forest" marker | seeded generation (`Random`, `Wisko.Generate` Poisson disc, a job with progress), harvesting, regrowth, save data |
| `Workbench.lua` | "Workbench" | recipes, station, timed jobs, cancel (`Wisko.Crafting`) |
| `Vault.lua` | "VaultDoor" | switches, plate, Hold, All, a Mover door (`Wisko.Logic`), the "VaultCam" camera |
| `Portals.lua` | "Portals" marker | linked pads and checkpoints (`Wisko.Teleport`, `object:Teleport`) |
| `Platform.lua` | "SkyLift" | a Catmull-Rom `Path` and `Path.Follow` carrying riders |
| `RaceCourse.lua` | "RaceStart" | `Wisko.Race`: countdown, laps, splits, wrong checkpoints, best times in a save slot |
| `Intro.lua` | "Intro" marker | a camera path cinematic with blends (`Cameras`) |
| `Crate.lua` | the village crates | the wind tag |
