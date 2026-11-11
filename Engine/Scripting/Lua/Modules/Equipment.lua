-- Wisko.Equipment: optional equipment slots on a character, on SetParent (with a model's bones) and attributes.
--
--   local Equipment = require("Wisko.Equipment")
--   local gear = Equipment.new(robot, {
--     Owner = player,                                   -- who owns what is equipped (optional; default: the character)
--     Slots = {
--       RightHand = { Bone = "Palm2.R", Offset = Vector3.new(0, 0.1, 0), Rotation = Vector3.new(0, 0, 90) },  -- robot:GetBones()
--       Back = { Offset = Vector3.new(0, 2, 0.6) },     -- no Bone: relative to the character itself
--     },
--   })
--   gear:Equip("RightHand", sword)
--   gear:OnEquipped(function(slot, item) print(item.Name .. " in " .. slot) end)
--   gear:Unequip("RightHand")                         -- dropped where it is (physics takes it again)
--
-- An equipped item follows its slot every frame (a bone follows the animation), never collides with the character
-- (one attachment tree), and gets the attributes "EquippedBy" (the character's Id), "EquippedSlot" and "Owner" (the
-- owner's Id). When the item is destroyed its slot empties; when the character is destroyed every item is dropped.
-- Slots are yours: any names, any number. Ordinary Lua on Wisko's public API.

local Signal = require("Wisko.Signal")

local Equipment = {}
Equipment.__index = Equipment

local function checkObject(object, usage)
  if type(object) ~= "table" or object.Id == nil then
    error(usage .. " expects a scene object, got a " .. type(object) .. " value", 3)
  end
end

-- Options: Slots = { name = { Bone (a bone of the character's model; see character:GetBones()), Offset, Rotation,
-- Accepts(item) -> bool } }, Owner (an object).
function Equipment.new(character, options)
  checkObject(character, "Equipment.new")
  options = options or {}
  local rig = setmetatable({
    Character = character,
    Owner = options.Owner or character,
    Slots = {},
    _items = {},          -- slot -> item
    _watch = {},          -- slot -> OnDestroyed connection
    _equipped = Signal.new(),
    _unequipped = Signal.new(),
  }, Equipment)
  for name, slot in pairs(options.Slots or {}) do
    rig:DefineSlot(name, slot)
  end
  rig._characterGone = character:OnDestroyed(function()
    for slot in pairs(rig._items) do
      rig:Unequip(slot)
    end
  end)
  return rig
end

-- Adds (or changes) a slot: { Bone, Offset, Rotation, Accepts }.
function Equipment:DefineSlot(name, slot)
  if type(name) ~= "string" then
    error("DefineSlot expects a slot name (a string)", 2)
  end
  slot = slot or {}
  if slot.Bone ~= nil and not self.Character:HasBone(slot.Bone) then
    error("slot " .. name .. ": " .. self.Character.Name .. " has no bone called \"" .. tostring(slot.Bone) .. "\" (see GetBones())", 2)
  end
  self.Slots[name] = { Bone = slot.Bone, Offset = slot.Offset or Vector3.new(0, 0, 0), Rotation = slot.Rotation or Vector3.new(0, 0, 0),
    Accepts = slot.Accepts }
end

-- Puts item in a slot (what was there is unequipped first). Returns true, or false and why.
function Equipment:Equip(slotName, item)
  checkObject(item, "equipment:Equip")
  local slot = self.Slots[slotName]
  if slot == nil then
    return false, "there is no slot called \"" .. tostring(slotName) .. "\""
  end
  if not item.Exists or not self.Character.Exists then
    return false, "the item or the character no longer exists"
  end
  if slot.Accepts ~= nil and not slot.Accepts(item) then
    return false, item.Name .. " does not fit in " .. slotName
  end
  -- An item is in one slot at a time.
  for name, current in pairs(self._items) do
    if current == item then
      self:Unequip(name)
    end
  end
  if self._items[slotName] ~= nil then
    self:Unequip(slotName)
  end
  item:SetParent(self.Character, slot.Offset, slot.Rotation, slot.Bone)
  item:SetAttribute("EquippedBy", self.Character.Id)
  item:SetAttribute("EquippedSlot", slotName)
  item:SetAttribute("Owner", self.Owner.Id)
  self._items[slotName] = item
  self._watch[slotName] = item:OnDestroyed(function()
    if self._items[slotName] == item then
      self._items[slotName] = nil
      self._watch[slotName] = nil
      self._unequipped:Fire(slotName, item, "destroyed")
    end
  end)
  self._equipped:Fire(slotName, item)
  Events.Emit("Equipped", { character = self.Character, slot = slotName, item = item })
  return true
end

-- Takes the item out of a slot; it stays where it is and physics owns it again. Returns the item (nil if empty).
function Equipment:Unequip(slotName)
  local item = self._items[slotName]
  if item == nil then
    return nil
  end
  self._items[slotName] = nil
  if self._watch[slotName] ~= nil then
    self._watch[slotName]:Disconnect()
    self._watch[slotName] = nil
  end
  if item.Exists then
    item:SetParent(nil)
    item:RemoveAttribute("EquippedBy")
    item:RemoveAttribute("EquippedSlot")
  end
  self._unequipped:Fire(slotName, item, "unequipped")
  Events.Emit("Unequipped", { character = self.Character, slot = slotName, item = item.Exists and item or nil })
  return item
end

function Equipment:Get(slotName)
  return self._items[slotName]
end

-- The slot an item is in (nil: not equipped here).
function Equipment:SlotOf(item)
  for name, current in pairs(self._items) do
    if current == item then
      return name
    end
  end
  return nil
end

-- Who owns an item: the owner's object (from its Owner attribute), or nil.
function Equipment.GetOwner(item)
  local id = item:GetAttribute("Owner")
  return id and Scene.FindById(id) or nil
end

-- Where a slot is in the world now (a bone's animated pose, or the character plus the offset).
function Equipment:GetSlotTransform(slotName)
  local slot = self.Slots[slotName]
  if slot == nil then
    return nil
  end
  if slot.Bone ~= nil then
    return self.Character:GetBoneTransform(slot.Bone)
  end
  return self.Character:PointToWorld(slot.Offset), self.Character.Transform.Rotation
end

function Equipment:OnEquipped(f) return self._equipped:Connect(f) end
function Equipment:OnUnequipped(f) return self._unequipped:Connect(f) end

return Equipment
