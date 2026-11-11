-- A complete custom chat, written only with the public API: UI.Create("TextBox") for typing and the Chat library for
-- messages. It does not use the ready-made window (Chat.EnableDefaultUI), so it can look and behave however the game
-- wants. Attach it to any Part and press Play.
--
--   T          start typing (Escape stops typing; a second Escape stops Play)
--   Enter      send
--   /me waves  an emote ("* Player waves")
--
-- Every message carries Data the game chooses: the sender's team and colour. The engine keeps Data apart from what it
-- vouches for (the sender and the kind), so a message can be styled freely but never pretend to be someone else.

local Rows = 8
local RowHeight = 22
local Width = 380
local TeamColours = { Blue = "#5AA9FF", Red = "#FF6B6B", Game = "#FFD166" }

local rows = {}
local lines = {}
local box

-- The newest lines at the bottom.
local function redraw()
  local first = math.max(1, #lines - Rows + 1)
  for i = 1, Rows do
    local line = lines[first + i - 1]
    rows[i].Text = line and line.Text or ""
    rows[i].TextColor = line and line.Colour or "#FFFFFF"
  end
end

local function show(message)
  local colour = TeamColours[message.Data.Team] or TeamColours.Game
  local text
  if message.Data.Emote then
    text = "* " .. message.Sender.Name .. " " .. message.Text
  elseif message.Kind == "System" and message.Sender.Name == "System" then
    text = message.Text
  else
    text = message.Sender.Name .. ": " .. message.Text
  end
  lines[#lines + 1] = { Text = text, Colour = colour }
  if #lines > 100 then
    table.remove(lines, 1)
  end
  redraw()
end

function Start()
  -- The panel: top right, a log above a text box.
  local panel = UI.Create("Frame", {
    Name = "CustomChat", Anchor = { 1, 0 }, Position = { 1, -16, 0, 16 }, Size = { 0, Width, 0, Rows * RowHeight + 52 },
    Color = "#101418", Opacity = 0.8,
  })
  for i = 1, Rows do
    rows[i] = UI.Create("Text", {
      Name = "Row" .. i, Parent = panel, Text = "", FontSize = 15, HorizontalAlignment = "Left",
      Position = { 0, 10, 0, 6 + (i - 1) * RowHeight }, Size = { 1, -20, 0, RowHeight },
    })
  end
  box = UI.Create("TextBox", {
    Name = "CustomChatInput", Parent = panel, PlaceholderText = "Press T to chat", MaxLength = 120, ClearOnSubmit = true,
    Position = { 0, 8, 1, -40 }, Size = { 1, -16, 0, 32 }, FontSize = 15,
  })

  -- Sending: "/me" makes an emote; everything goes out with the player's team in Data.
  box:OnSubmit(function(text)
    local emote = string.match(text, "^/me%s+(.+)$")
    local id, reason = Chat.Send(emote or text, { Data = { Team = "Blue", Emote = emote ~= nil } })
    if not id then
      lines[#lines + 1] = { Text = "(" .. reason .. ")", Colour = "#999999" }
      redraw()
    end
  end)

  -- Receiving: what was said before this script started, then every new message.
  for _, message in ipairs(Chat.GetHistory()) do
    show(message)
  end
  Chat.OnMessage(show)

  -- A guard on the red team answers greetings.
  Chat.OnMessage(function(message)
    if message.Kind == "Player" and string.find(string.lower(message.Text), "hello", 1, true) then
      Chat.Post("Halt! Who goes there?", { SenderName = "Red Guard", Data = { Team = "Red" } })
    end
  end)

  Chat.Post("Custom chat ready. Press T to talk.", { Data = { Team = "Game" } })
end

function Update()
  if not Input.IsTyping and Input.IsKeyPressed("T") then
    box:Focus()
  end
end
