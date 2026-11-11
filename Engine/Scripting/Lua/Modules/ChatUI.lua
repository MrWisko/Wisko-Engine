-- Wisko.ChatUI: the ready-made chat window. Chat.EnableDefaultUI() turns it on; it is also an ordinary module:
--
--   local ChatUI = require("Wisko.ChatUI")
--   ChatUI.Enable({ Channel = "General", FontSize = 16, Width = 420, Height = 200, FocusKey = "Slash",
--     Placeholder = "Press / to chat", Position = { 0, 16, 1, -16 }, Anchor = { 0, 1 },
--     BackgroundColor = "#14161B", BackgroundOpacity = 0.7, InputColor = "#1E2026",
--     PlayerColor = "#F2F4F8", SystemColor = "#FFD166", NoticeColor = "#FF8A80" })
--   ChatUI.Disable()
--
-- A log of the channel's messages (newest at the bottom; long messages wrap, their next lines indented, so every
-- message starts at the left edge with its sender) and a text box under it: the focus key ("/" by default) or a click
-- starts typing, Enter sends (as the local player), Escape stops typing. Players' messages are white, the game's own
-- (Chat.Post: announcements, NPCs) gold, and local notices (sending too fast...) red; only this player sees notices. A
-- message whose Data.Color is "#RRGGBB" is shown in that colour. Every option is optional.
-- Ordinary Lua on Wisko's public API: copy it to Assets/Scripts/Wisko/ChatUI.lua to change it.

local ChatUI = {}

local Defaults = {
  Channel = false, FontSize = 16, Width = 420, Height = 200, FocusKey = "Slash", Placeholder = "Press / to chat",
  Position = { 0, 16, 1, -16 }, Anchor = { 0, 1 }, BackgroundColor = "#14161B", BackgroundOpacity = 0.7,
  InputColor = "#1E2026", PlayerColor = "#F2F4F8", SystemColor = "#FFD166", NoticeColor = "#FF8A80",
}
local MaxLines = 200

local state

-- options with the defaults filled in; an unknown option or a wrong kind of value is an error (at the caller's line).
local function settle(options)
  if options ~= nil and type(options) ~= "table" then
    error("Wisko.ChatUI: options must be a table, got a " .. type(options) .. " value", 3)
  end
  options = options or {}
  local settled = {}
  for key, value in pairs(options) do
    local default = Defaults[key]
    if default == nil then
      local names = {}
      for name in pairs(Defaults) do
        names[#names + 1] = name
      end
      table.sort(names)
      error("Wisko.ChatUI: unknown option " .. tostring(key) .. " (options: " .. table.concat(names, ", ") .. ")", 3)
    end
    if key == "Channel" and type(value) ~= "string" then
      error("Wisko.ChatUI: Channel must be a string (a channel name), got a " .. type(value) .. " value", 3)
    end
    if default ~= false and type(value) ~= type(default) then
      error("Wisko.ChatUI: " .. key .. " must be a " .. type(default) .. ", got a " .. type(value) .. " value", 3)
    end
    settled[key] = value
  end
  for key, default in pairs(Defaults) do
    if settled[key] == nil and default ~= false then
      settled[key] = default
    end
  end
  return settled
end

-- A message's own colour (Data.Color = "#RRGGBB"), else the colour of its kind.
local function colourOf(message, fallback)
  local colour = message.Data and message.Data.Color
  if type(colour) == "string" and string.find(colour, "^#%x%x%x%x%x%x$") then
    return colour
  end
  return fallback
end

local function measure(text, fontSize)
  return (UI.MeasureText(text, fontSize))
end

-- A long word cut into pieces that fit width (by characters).
local function splitWord(word, width, fontSize, firstWidth)
  local pieces, piece, limit = {}, "", firstWidth
  for _, code in utf8.codes(word) do
    local char = utf8.char(code)
    if piece ~= "" and measure(piece .. char, fontSize) > limit then
      pieces[#pieces + 1] = piece
      piece, limit = "", width
    end
    piece = piece .. char
  end
  if piece ~= "" then
    pieces[#pieces + 1] = piece
  end
  return pieces
end

-- The text in lines no wider than width (the lines after the first: width - indent).
local function wrap(text, width, indent, fontSize)
  local lines, line = {}, ""
  local function limit()
    return #lines == 0 and width or width - indent
  end
  for word in string.gmatch(text, "%S+") do
    local candidate = line == "" and word or line .. " " .. word
    if measure(candidate, fontSize) <= limit() then
      line = candidate
    else
      if line ~= "" then
        lines[#lines + 1] = line
        line = ""
      end
      if measure(word, fontSize) <= limit() then
        line = word
      else
        local pieces = splitWord(word, width - indent, fontSize, limit())
        for i = 1, #pieces - 1 do
          lines[#lines + 1] = pieces[i]
        end
        line = pieces[#pieces] or ""
      end
    end
  end
  if line ~= "" or #lines == 0 then
    lines[#lines + 1] = line
  end
  return lines
end

-- The rows show the newest lines, oldest at the top.
local function refresh()
  local rows, lines = state.rows, state.lines
  local count = math.min(#rows, #lines)
  local empty = #rows - count
  for i = 1, #rows do
    local row = rows[i]
    if i > empty then
      local line = lines[#lines - count + i - empty]
      local indent = line.Indent and state.indent or 0
      row.Text = line.Text
      row.TextColor = line.Color
      row:SetPosition(0, state.padding + indent, 0, state.padding + (i - 1) * state.lineHeight)
      row:SetSize(1, -state.padding * 2 - indent, 0, state.lineHeight)
      row.Visible = true
    else
      row.Visible = false
    end
  end
end

local function addLines(text, color)
  local width = state.logWidth - state.padding * 2
  local lines = wrap(text, width, state.indent, state.fontSize)
  for i = 1, #lines do
    state.lines[#state.lines + 1] = { Text = lines[i], Color = color, Indent = i > 1 }
  end
  while #state.lines > MaxLines do
    table.remove(state.lines, 1)
  end
end

local function show(message)
  if message.Kind == "System" then
    local name = message.Sender.Name
    addLines((name == "System" and "" or name .. ": ") .. message.Text, colourOf(message, state.options.SystemColor))
  else
    addLines(message.Sender.Name .. ": " .. message.Text, colourOf(message, state.options.PlayerColor))
  end
end

local function notice(text)
  addLines(text, state.options.NoticeColor)
  refresh()
end

-- Turns the chat window on (again, with these options, when it is already on).
function ChatUI.Enable(options)
  options = settle(options)
  if state ~= nil then
    ChatUI.Disable()
  end

  local channel = options.Channel or Chat.DefaultChannel
  local handle = Chat.GetChannel(channel)
  if handle == nil then
    error("Wisko.ChatUI: there is no chat channel \"" .. tostring(channel) .. "\" (Chat.CreateChannel makes one)", 2)
  end

  local fontSize = options.FontSize
  local width = options.Width
  local logHeight = options.Height
  local inputHeight = math.floor(fontSize * 2 + 4)
  local _, lineHeight = UI.MeasureText("Ag", fontSize)
  lineHeight = math.max(lineHeight, fontSize)
  local padding = 8

  state = {
    channel = channel, fontSize = fontSize, lineHeight = lineHeight, padding = padding, indent = 14,
    logWidth = width, lines = {}, rows = {}, connections = {}, options = options,
  }

  local root = UI.Create("Frame", {
    Name = "WiskoChat", Opacity = 0, ZOrder = 100, Anchor = options.Anchor,
    Position = options.Position, Size = { 0, width, 0, logHeight + 6 + inputHeight },
  })
  state.root = root
  UI.Create("Frame", {
    Name = "Log", Parent = root, Color = options.BackgroundColor, Opacity = options.BackgroundOpacity, Position = { 0, 0, 0, 0 }, Size = { 1, 0, 0, logHeight },
  })
  local rowCount = math.max(1, math.floor((logHeight - padding * 2) / lineHeight))
  for i = 1, rowCount do
    state.rows[i] = UI.Create("Text", {
      Name = "Line" .. i, Parent = root, Text = "", FontSize = fontSize, TextColor = options.PlayerColor,
      HorizontalAlignment = "Left", VerticalAlignment = "Middle", Visible = false,
      Position = { 0, padding, 0, padding + (i - 1) * lineHeight }, Size = { 1, -padding * 2, 0, lineHeight },
    })
  end
  local box = UI.Create("TextBox", {
    Name = "Input", Parent = root, FontSize = fontSize, Color = options.InputColor, TextColor = options.PlayerColor,
    PlaceholderText = options.Placeholder, MaxLength = handle.MaxLength, ClearOnSubmit = true,
    Position = { 0, 0, 0, logHeight + 6 }, Size = { 1, 0, 0, inputHeight },
  })
  state.box = box

  for _, message in ipairs(Chat.GetHistory(channel, rowCount)) do
    show(message)
  end
  refresh()

  local connections = state.connections
  connections[#connections + 1] = Chat.OnMessage(function(message)
    if state ~= nil then
      show(message)
      refresh()
    end
  end, channel)
  connections[#connections + 1] = box:OnSubmit(function(text)
    if state == nil or not string.find(text, "%S") then
      return
    end
    local id, reason = Chat.Send(text, channel)
    if not id then
      notice(reason)
    end
  end)
  local focusKey = options.FocusKey
  connections[#connections + 1] = Time.OnUpdate(function()
    if state ~= nil and not Input.IsTyping and Input.IsKeyPressed(focusKey) then
      state.box:Focus()
    end
  end)
end

-- Turns the chat window off (its GUI goes; the chat itself goes on).
function ChatUI.Disable()
  if state == nil then
    return
  end
  for _, connection in ipairs(state.connections) do
    connection:Disconnect()
  end
  pcall(function() state.root:Destroy() end)
  state = nil
end

function ChatUI.IsEnabled()
  return state ~= nil
end

-- Shows a line only this player sees (not a chat message), in the notice colour.
function ChatUI.Notice(text)
  if state ~= nil then
    notice(tostring(text))
  end
end

return ChatUI
