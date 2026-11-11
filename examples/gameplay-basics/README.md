# Gameplay Basics Demo

A playground that shows Wisko's core gameplay scripting. It is built only from placeholder Parts and these scripts: none of
it is engine code, and every system here (flying, the car, survival stats, inventory, harvesting, conveyors, status
effects) is ordinary Lua you can read, copy and change.

## Make the project

From the repository folder:

```
dotnet run --project Tests/Wisko.Tests -- --make-demo "%USERPROFILE%\Documents\Wisko\Projects"
```

This creates the project **Gameplay Basics Demo** (the scene, grouped in the World panel, and these scripts in
`Assets/Scripts`). If a project with that name already exists, it refuses and changes nothing. Existing projects are
never touched. To start over, delete the demo project from the Hub and run the command again.

## Play it

| Key | Does |
|---|---|
| W A S D, Space, LeftShift | walk (camera-relative), jump, sprint |
| mouse, wheel, Tab | look, zoom, free / lock the mouse |
| E | use what is in reach: chop the tree, mine the rock, pick up an apple, drink from the well, feed the goat, drive the car (E again gets out), switch the conveyors at the lever |
| Q | eat an apple from the inventory |
| I | show / hide the inventory |
| G or the Fly button | fly / land (touching the cyan orb also starts flight); Space up, LeftControl down |
| F | shove what is in front of you (knockback) |
| V | blast enemies you can see |
| X | "X-ray" blast: enemies within range, through walls, nothing else |

In the car: W/S drive and reverse, A/D steer, Space brakes; the camera chases the car.

Things to try: walk through the coins (the objective advances), chop the tree and watch the wood fly to you, wait for
it to grow back, ride the purple spinning platform, put crates on the conveyors and switch the lever, step into the
lava, mud and heal pads, run into the pink bumper, walk through the ghost wall and the purple player-only door (crates
cannot), blast the dummy behind the solid wall with X.

## Scripts

| Script | On | What it does |
|---|---|---|
| `Player.lua` | Player | third-person movement and camera; steps aside while flying or driving; slowed by `SpeedMultiplier` |
| `Flight.lua` | Player | flying: gravity scale 0, steered velocity, Fly button |
| `FlightOrb.lua` | FlightOrb | trigger that starts flight |
| `Interact.lua` | Player | E uses the nearest interactable (any object that opts in); Q eats |
| `Inventory.lua` | Player | items in a Lua table; `InventoryChanged` events |
| `Collector.lua` | Player | dropped items fly to you and go into the inventory |
| `Survival.lua` | Player, Goat | Health / Hunger / Thirst attributes that fall over time; `Eat` |
| `FoodDefs.lua` | (module) | what each food restores |
| `Status.lua` | Player, dummies | damage, healing, knockback, timed effects (Burn, Slow, Regen) |
| `Pulse.lua` | Player | shove, line-of-sight blast, X-ray blast |
| `Objectives.lua` | Player | a list of goals |
| `HUD.lua` | Player | the whole HUD, made with `UI.Create` |
| `Car.lua`, `CarSeat.lua` | Car | raycast car; getting in and out |
| `ResourceNode.lua` | Tree, Rock | harvesting, drops, regrowth |
| `Food.lua` | Apples, Well | pick up / drink |
| `Animal.lua` | Goat | feeding |
| `Coin.lua` | Coins | a trigger coin adding to the `Coins` attribute |
| `Spinner.lua` | Spinner, Fan | anchored Parts spinning on their own |
| `Conveyor.lua`, `Lever.lua`, `CrateSpawner.lua` | belts, lever, chute | conveyor line |
| `Rules.lua` | GameRules | collision groups (the player-only door) |
| `Zone.lua` | TriggerZone | reports who enters and leaves |
| `Hazard.lua` | Lava, Mud, HealPad | timed effects for whoever stands in them |
| `Bumper.lua` | Bumper | knockback |
| `Dummy.lua` | dummies | an enemy target that heals 3 s after dropping to 0 |

The API they use is documented in [docs/lua-api.md](../../docs/lua-api.md). The automated tests play these scripts
(`Tests/Wisko.Tests/Checks/GameplaySystemsV4Checks.cs`).
