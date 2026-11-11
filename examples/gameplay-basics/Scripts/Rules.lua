-- Rules.lua: the playground's collision rules, set once at Start.
--   SolidWall  an ordinary wall: nothing gets through.
--   GhostWall  CanCollide off (set in the editor's Physics panel): everything walks through it.
--   PlayerDoor in the "PlayerDoor" collision group, which does not touch the "Player" group: the player walks through,
--              crates and dummies are stopped.
--   TriggerZone IsTrigger (Zone.lua reports who enters and leaves).
function Start()
  Physics.SetGroupsCollide("Player", "PlayerDoor", false)
  Scene.Find("PlayerDoor").CollisionGroup = "PlayerDoor"
  Scene.Find("Player").CollisionGroup = "Player"
end
