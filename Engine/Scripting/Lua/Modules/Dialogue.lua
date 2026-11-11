-- Wisko.Dialogue: optional branching conversations described by plain Lua tables, with an optional GUI presenter.
--
--   local Dialogue = require("Wisko.Dialogue")
--   local tree = {
--     Start = "hello",
--     Nodes = {
--       hello = { Speaker = "Mira", Text = "Could you bring me 3 wood?", Options = {
--         { Text = "Of course.", Next = "thanks", Action = function(ctx) ctx.quests:Start("wood") end,
--           Condition = function(ctx) return not ctx.quests:IsActive("wood") end },
--         { Text = function(ctx) return "Here is your wood (" .. ctx.wood() .. "/3)" end, Next = "done",
--           Condition = function(ctx) return ctx.wood() >= 3 end },
--         { Text = "Goodbye." },                     -- no Next: the conversation ends
--       } },
--       thanks = { Speaker = "Mira", Text = "Thank you!", Next = "hello" },   -- no Options: Advance() goes on
--       done = { Speaker = "Mira", Text = "Wonderful!", Action = function(ctx) ctx.reward() end },
--     },
--   }
--   local talk = Dialogue.Start(tree, { Context = ctx, Presenter = Dialogue.Presenter() })
--
-- Text, Speaker and option Text may be strings or functions of the context (custom responses). Conditions hide options;
-- Actions run when an option is chosen (option.Action) or a node is reached (node.Action). Without a presenter the
-- script drives it: talk:GetOptions(), talk:Choose(i), talk:Advance(), talk:End(). Nothing is fixed about the format
-- beyond these names. Ordinary Lua on Wisko's public API: copy it to Assets/Scripts/Wisko/Dialogue.lua to change it.

local Signal = require("Wisko.Signal")

local Dialogue = {}

local Conversation = {}
Conversation.__index = Conversation

local function resolve(value, ctx, conversation)
  if type(value) == "function" then
    return value(ctx, conversation)
  end
  return value
end

-- Starts a conversation. Options: Context (any table, handed to every function), Presenter (Dialogue.Presenter(), or
-- nil), Start (a node id, instead of tree.Start), OnNode(f(node, id)), OnChoice(f(option)), OnEnd(f(reason)).
function Dialogue.Start(tree, options)
  if type(tree) ~= "table" or type(tree.Nodes) ~= "table" then
    error("Dialogue.Start expects a tree: { Start = id, Nodes = { id = { Text, Options, ... } } }", 2)
  end
  options = options or {}
  local conversation = setmetatable({
    Tree = tree,
    Context = options.Context or {},
    Presenter = options.Presenter,
    Active = true,
    History = {},
    _onNode = Signal.new(),
    _onChoice = Signal.new(),
    _onEnd = Signal.new(),
  }, Conversation)
  if options.OnNode then conversation._onNode:Connect(options.OnNode) end
  if options.OnChoice then conversation._onChoice:Connect(options.OnChoice) end
  if options.OnEnd then conversation._onEnd:Connect(options.OnEnd) end
  local start = options.Start or tree.Start
  if tree.Nodes[start] == nil then
    error("Dialogue.Start: there is no node \"" .. tostring(start) .. "\"", 2)
  end
  Events.Emit("DialogueStarted", { start = start })
  conversation:Goto(start)
  return conversation
end

-- Goes to a node (its Action runs; a node with End = true ends the conversation after showing).
function Conversation:Goto(id)
  if not self.Active then
    return false
  end
  local node = self.Tree.Nodes[id]
  if node == nil then
    self:End("missing node " .. tostring(id))
    return false
  end
  self.Node = id
  self.History[#self.History + 1] = id
  if node.Action ~= nil then
    node.Action(self.Context, self)
  end
  if not self.Active then
    return true
  end
  self._onNode:Fire(node, id, self)
  if self.Presenter ~= nil then
    self.Presenter:Show(self)
  end
  return true
end

function Conversation:GetNode()
  return self.Active and self.Tree.Nodes[self.Node] or nil
end

function Conversation:GetText()
  local node = self:GetNode()
  return node and resolve(node.Text, self.Context, self) or nil
end

function Conversation:GetSpeaker()
  local node = self:GetNode()
  return node and resolve(node.Speaker, self.Context, self) or nil
end

-- The options shown now: { Index, Text, Option } for each whose Condition allows it (Index counts the shown ones).
function Conversation:GetOptions()
  local node = self:GetNode()
  local shown = {}
  if node == nil or node.Options == nil then
    return shown
  end
  for _, option in ipairs(node.Options) do
    if option.Condition == nil or option.Condition(self.Context, self) then
      shown[#shown + 1] = { Index = #shown + 1, Text = resolve(option.Text, self.Context, self), Option = option }
    end
  end
  return shown
end

-- Picks a shown option by its index: its Action runs, then its Next node (or the end). Returns true, or false and why.
function Conversation:Choose(index)
  if not self.Active then
    return false, "the conversation has ended"
  end
  local shown = self:GetOptions()
  local choice = shown[index]
  if choice == nil then
    return false, "there is no option " .. tostring(index) .. " (" .. #shown .. " shown)"
  end
  local option = choice.Option
  self._onChoice:Fire(option, choice.Text, self)
  if option.Action ~= nil then
    option.Action(self.Context, self)
  end
  if not self.Active then
    return true
  end
  local nextNode = resolve(option.Next, self.Context, self)
  if nextNode == nil then
    self:End("finished")
  else
    self:Goto(nextNode)
  end
  return true
end

-- A node without options goes on to its Next (or ends). With options shown it does nothing (choose one).
function Conversation:Advance()
  local node = self:GetNode()
  if node == nil then
    return false
  end
  if #self:GetOptions() > 0 then
    return false
  end
  if node.End or node.Next == nil then
    self:End("finished")
  else
    self:Goto(resolve(node.Next, self.Context, self))
  end
  return true
end

function Conversation:End(reason)
  if not self.Active then
    return
  end
  self.Active = false
  if self.Presenter ~= nil then
    self.Presenter:Hide()
  end
  self._onEnd:Fire(reason or "ended", self)
  Events.Emit("DialogueEnded", { node = self.Node, reason = reason or "ended" })
end

function Conversation:OnNode(f) return self._onNode:Connect(f) end
function Conversation:OnChoice(f) return self._onChoice:Connect(f) end
function Conversation:OnEnd(f) return self._onEnd:Connect(f) end

-- A GUI presenter: a panel at the bottom of the screen with the speaker, the text and a button per option (keys 1-9
-- choose too; Enter / Space or a click on "Continue" advances a node without options). Options: Width (720), Color,
-- TextColor, OptionColor, Keys (true). Replace it with your own: a presenter is anything with Show(conversation) and Hide().
function Dialogue.Presenter(options)
  options = options or {}
  local presenter = {}
  local width = options.Width or 720
  local panel, speaker, text
  local buttons = {}
  local current
  local keyConnection

  local function destroyButtons()
    for _, button in ipairs(buttons) do
      -- (A button the game destroyed itself is already gone.)
      pcall(function() button:Destroy() end)
    end
    buttons = {}
  end

  local function build()
    if panel ~= nil then
      return
    end
    panel = UI.Create("Frame", { Name = "DialoguePanel", Anchor = { 0.5, 1 }, Position = { 0.5, 0, 1, -24 }, Size = { 0, width, 0, 230 },
      Color = options.Color or "#1C2030", Opacity = 0.92, ZOrder = 50 })
    speaker = UI.Create("Text", { Name = "DialogueSpeaker", Parent = panel, Position = { 0, 18, 0, 10 }, Size = { 1, -36, 0, 26 },
      Text = "", FontSize = 18, TextColor = options.SpeakerColor or "#FFD479", HorizontalAlignment = "Left" })
    text = UI.Create("Text", { Name = "DialogueText", Parent = panel, Position = { 0, 18, 0, 38 }, Size = { 1, -36, 0, 48 },
      Text = "", FontSize = 18, TextColor = options.TextColor or "#F0F2F8", HorizontalAlignment = "Left" })
  end

  local function pressedNumber()
    for i = 1, 9 do
      if Input.IsKeyPressed(tostring(i)) then
        return i
      end
    end
    return nil
  end

  function presenter:Show(conversation)
    build()
    current = conversation
    panel.Visible = true
    speaker.Text = conversation:GetSpeaker() or ""
    text.Text = conversation:GetText() or ""
    destroyButtons()
    local shown = conversation:GetOptions()
    local rows = #shown > 0 and shown or { { Index = 0, Text = "Continue" } }
    for i, entry in ipairs(rows) do
      local label = entry.Index > 0 and (entry.Index .. ". " .. tostring(entry.Text)) or tostring(entry.Text)
      local button = UI.Create("Button", { Name = "DialogueOption" .. i, Parent = panel, Position = { 0, 18, 0, 92 + (i - 1) * 32 },
        Size = { 1, -36, 0, 28 }, Text = label, FontSize = 16, Color = options.OptionColor or "#2E3550", TextColor = "#F0F2F8" })
      local index = entry.Index
      button:OnClick(function()
        if current ~= conversation or not conversation.Active then
          return
        end
        if index == 0 then
          conversation:Advance()
        else
          conversation:Choose(index)
        end
      end)
      buttons[#buttons + 1] = button
    end
    if options.Keys ~= false and keyConnection == nil then
      keyConnection = Time.OnUpdate(function()
        if current == nil or not current.Active then
          return
        end
        local number = pressedNumber()
        if number ~= nil then
          current:Choose(number)
        elseif (Input.IsKeyPressed("Enter") or Input.IsKeyPressed("Space")) and #current:GetOptions() == 0 then
          current:Advance()
        end
      end)
    end
  end

  function presenter:Hide()
    current = nil
    destroyButtons()
    if panel ~= nil then
      panel.Visible = false
    end
    if keyConnection ~= nil then
      keyConnection:Disconnect()
      keyConnection = nil
    end
  end

  function presenter:Destroy()
    self:Hide()
    if panel ~= nil then
      panel:Destroy()
      panel = nil
    end
  end

  -- The presenter's GUI elements (for tests and custom styling): panel, speaker, text, buttons.
  function presenter:GetElements()
    return { Panel = panel, Speaker = speaker, Text = text, Buttons = buttons }
  end

  return presenter
end

return Dialogue
