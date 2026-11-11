-- Vault.lua (on the "Vault" marker): a puzzle built from Wisko.Logic signals. The door opens while both switches are
-- on AND the pressure plate is pressed or was pressed less than 4 seconds ago (Logic.Hold): flip the switches, step on
-- the plate, then run in. Nothing about the puzzle is built into the engine; change the signals to make another.
-- The gem inside completes "The Vault" quest's first objective. A security camera ("VaultCam") watches the room; M shows
-- its feed on the HUD (Game.lua).

local Logic = require("Wisko.Logic")

local HOLD = 4
local switchA, switchB, plate, held, open, door
local gem
local label

local function lampColor(object, on)
  object.Color = on and "#4CD964" or "#D64545"
end

local function registerSwitch(object, switch)
  object:AddTag("Interactable")
  Shared.interactions[object.Id] = {
    Prompt = function() return switch.Value and "Turn the switch off" or "Turn the switch on" end,
    Use = function(player) switch:Use(player) end,
  }
  switch:Bind(function(on) lampColor(object, on) end)
end

function Start()
  Shared.interactions = Shared.interactions or {}
  switchA = Logic.Switch(Scene.Find("SwitchA"))
  switchB = Logic.Switch(Scene.Find("SwitchB"))
  registerSwitch(Scene.Find("SwitchA"), switchA)
  registerSwitch(Scene.Find("SwitchB"), switchB)
  plate = Logic.PressurePlate(Scene.Find("Plate"), { Filter = function(other) return other:HasTag("Player") or other:HasTag("Heavy") end })
  held = Logic.Hold(plate, HOLD)
  open = Logic.All(switchA, switchB, held)
  door = Logic.Mover(Scene.Find("VaultDoor"), { Offset = Vector3.new(0, 5.5, 0), Duration = 0.8 })
  label = UI.Create("Text", { Name = "VaultStatus", Position = { 0, 16, 0, 70 }, Size = { 0, 420, 0, 22 },
    FontSize = 15, TextColor = "#C9D1E0", HorizontalAlignment = "Left", Text = "" })
  open:OnChanged(function(isOpen)
    door:Set(isOpen)
    if isOpen then
      Shared.vaultOpen = true
      if Shared.game.quests:IsActive("vault") then
        Shared.game.quests:SetProgress("vault", "open", 1)
      end
      Shared.game.Hint("The vault door opens!", 2)
      Cameras.Shake(0.15, 0.5)
    end
  end)
  plate:Bind(function(pressed) Scene.Find("Plate").Color = pressed and "#FFD479" or "#8C6D3A" end)

  -- The gem: a trigger inside the vault.
  gem = Scene.Find("Gem")
  gem:OnTriggerEntered(function(other)
    if other ~= nil and other == Shared.player and gem.Visible then
      gem.Visible = false
      Shared.game.inventory.Add("Gem", 1)
      Shared.game.Hint("You found the gem! Bring it to Mira", 3)
    end
  end)

  -- The security camera, up in a corner of the room, looking at the gem.
  local vault = Scene.Find("VaultFloor").Transform.Position
  Cameras.Create("VaultCam", { Position = vault + Vector3.new(5.5, 4.6, 5.5), LookAt = Scene.Find("Gem").Transform.Position, FieldOfView = 80 })

  -- A loaded game that had the vault open opens it again (without the puzzle).
  Shared.openVault = function()
    switchA:Set(true)
    switchB:Set(true)
    door:Set(true)
    Shared.vaultOpen = true
  end
end

function Update()
  local remaining = held:GetRemaining()
  local parts = {
    "Switches " .. (switchA.Value and "ON" or "off") .. " / " .. (switchB.Value and "ON" or "off"),
    plate.Value and "plate pressed" or (remaining > 0 and string.format("plate %.1f s", remaining) or "plate up"),
  }
  label.Text = "Vault: " .. table.concat(parts, ", ")
end
