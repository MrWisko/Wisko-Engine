-- Portals.lua (on the "Portals" marker): two linked teleport pads (Wisko.Teleport) between the village plaza and the
-- sky island, and checkpoints. Stepping on a pad sends you to the other one; arriving does not send you straight back
-- (you have to step off first), and each pad has a cooldown. Touching a flag makes it your respawn point: falling off
-- the island or being caught by the guard brings you back to the last flag you touched.

local Teleport = require("Wisko.Teleport")

function Start()
  local isPlayer = function(object) return object:HasTag("Player") end
  local plaza = Teleport.Pad(Scene.Find("PadPlaza"), { Filter = isPlayer, Offset = Vector3.new(0, 3, 0), Cooldown = 1 })
  local island = Teleport.Pad(Scene.Find("PadIsland"), { Filter = isPlayer, Offset = Vector3.new(0, 3, 0), Cooldown = 1 })
  Teleport.Link(plaza, island)
  local function arrived(where)
    return function(object)
      Shared.game.Hint("Teleported to " .. where, 2)
      Cameras.Shake(0.2, 0.3)
    end
  end
  plaza:OnTeleported(arrived("the sky island"))
  island:OnTeleported(arrived("the village"))

  local checkpoints = Teleport.Checkpoints({ Spawn = Scene.Find("Spawn").Transform.Position + Vector3.new(0, 2.5, 0), Filter = isPlayer })
  for i, name in ipairs({ "FlagVillage", "FlagIsland", "FlagRace" }) do
    checkpoints:Watch(Scene.Find(name), i)
  end
  checkpoints.AllowBacktrack = true
  checkpoints:OnActivated(function(who, name)
    Shared.game.Hint("Checkpoint: " .. name, 1.5)
  end)
  Shared.checkpoints = checkpoints
end
