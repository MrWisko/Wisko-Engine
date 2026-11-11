-- CrateSpawner.lua: drops a crate onto the first belt every EVERY seconds, keeping at most MAX crates. A crate that
-- has been around for LIFETIME seconds is removed again, so the line never fills the scene.
local EVERY, MAX, LIFETIME = 3, 6, 40
local crates = {}

function Start()
  Timer.Every(EVERY, function()
    -- Forget crates that are gone (destroyed by their lifetime or by another script).
    for i = #crates, 1, -1 do
      if not crates[i].Exists then
        table.remove(crates, i)
      end
    end
    if #crates >= MAX then
      return
    end
    local crate = Scene.Create("Block", {
      Name = "Crate", Anchored = false, Size = Vector3.new(1.6, 1.6, 1.6), Color = "#B8864B", Mass = 2,
      Position = self.Transform.Position + Vector3.new(0, 1.5, 0), Tags = { "Crate" },
    })
    crates[#crates + 1] = crate
    Timer.After(LIFETIME, function()
      if crate.Exists then
        crate:Destroy()
      end
    end)
  end)
end
