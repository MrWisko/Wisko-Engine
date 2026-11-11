-- Mira.lua (on "MiraSpot", the trigger in front of the Mira model): a branching conversation (Wisko.Dialogue, plain
-- tables) that gives and completes quests (Wisko.Quests, in Game.lua). Options appear only when their Condition holds,
-- texts can be functions of the game state, and Actions change the game. Press E near her; 1-9 or a click chooses.

local Dialogue = require("Wisko.Dialogue")

local talking
local presenter

local function game() return Shared.game end
local function wood() return game().inventory.Count("Wood") end
local function hasGem() return game().inventory.Count("Gem") > 0 end

local tree = {
  Start = "hello",
  Nodes = {
    hello = {
      Speaker = "Mira",
      Text = function()
        local log = game().quests
        if log:IsComplete("vault") then return "The village is safe and warm. Thank you, friend!" end
        if log:IsActive("vault") then return "Any luck with the vault? Two switches, and the plate by the door..." end
        if log:IsActive("firewood") then return "How is the firewood coming along? (" .. wood() .. "/5)" end
        return "Hello, traveller! Winter is coming and my stove is cold."
      end,
      Options = {
        { Text = "Can I help?", Next = "offer", Condition = function() return game().quests:GetState("firewood") == "Available" end },
        { Text = function() return "Here is your wood (" .. wood() .. ")" end, Next = "thanks",
          Condition = function() return game().quests:IsActive("firewood") and wood() >= 5 end },
        { Text = "Where do I find wood?", Next = "where", Condition = function() return game().quests:IsActive("firewood") end },
        { Text = "I found the gem!", Next = "gem", Condition = function() return game().quests:IsActive("vault") and hasGem() end },
        { Text = "How do I open the vault?", Next = "vaulthint", Condition = function() return game().quests:IsActive("vault") end },
        { Text = "What is there to do here?", Next = "tips" },
        { Text = "Goodbye." },
      },
    },
    offer = {
      Speaker = "Mira", Text = "Bring me 5 wood from the forest to the east. Press E at a tree; an axe makes it quicker.",
      Options = {
        { Text = "I'm on it.", Next = "accepted", Action = function() game().quests:Start("firewood") end },
        { Text = "Maybe later." },
      },
    },
    accepted = { Speaker = "Mira", Text = "Thank you! The workbench can turn wood into planks and tools, too." },
    where = { Speaker = "Mira", Text = "East of the village the forest grows. Each tree takes a few hits.", Next = "hello" },
    thanks = {
      Speaker = "Mira", Text = "Wonderful, the stove will burn tonight. Here are 20 coins. One more thing... the old vault.",
      Action = function()
        local g = game()
        g.inventory.Remove("Wood", 5)
        g.quests:SetProgress("firewood", "wood", 5)
        g.quests:Complete("firewood")
        g.inventory.Add("Coin", 20)
        g.quests:Start("vault")
      end,
      Next = "vaulthint",
    },
    vaulthint = {
      Speaker = "Mira", Text = "West of here. Flip both switches, then step on the plate: the door stays open a few seconds.",
    },
    gem = {
      Speaker = "Mira", Text = "My grandmother's gem! Take these 50 coins, you have earned them.",
      Action = function()
        local g = game()
        g.inventory.Remove("Gem", 1)
        g.quests:SetProgress("vault", "gem", 1)
        g.quests:Complete("vault")
        g.inventory.Add("Coin", 50)
      end,
    },
    tips = {
      Speaker = "Mira",
      Text = "The pads by the plaza take you to the sky island; a moving platform brings you down. There is a race course too.",
      Next = "hello",
    },
  },
}

function Start()
  presenter = Dialogue.Presenter()
  self:AddTag("Interactable")
  Shared.interactions = Shared.interactions or {}
  Shared.interactions[self.Id] = {
    Prompt = function() return talking and "" or "Talk to Mira" end,
    Use = function(player)
      if talking ~= nil and talking.Active then
        return
      end
      talking = Dialogue.Start(tree, { Presenter = presenter, OnEnd = function() talking = nil end })
      Scene.Find("Mira"):PlayAnimation(1)
    end,
  }
end

function Update()
  -- Walking away ends the conversation.
  if talking ~= nil and talking.Active and Shared.player ~= nil
    and Vector3.distance(Shared.player.Transform.Position, self.Transform.Position) > 10 then
    talking:End("walked away")
  end
end
