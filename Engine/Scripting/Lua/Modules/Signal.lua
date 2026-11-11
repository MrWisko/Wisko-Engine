-- Wisko.Signal: a list of listeners with connections, used by the other Wisko modules (and free for any script).
--
--   local Signal = require("Wisko.Signal")
--   local changed = Signal.new()
--   local connection = changed:Connect(function(value) print(value) end)
--   changed:Fire(42)
--   connection:Disconnect()
--
-- Listeners run in the order they connected, inside Fire (no delay). A listener may disconnect itself or others while
-- the signal fires. This is ordinary Lua on Wisko's public API: copy it to Assets/Scripts/Wisko/Signal.lua to change it.

local Signal = {}
Signal.__index = Signal

function Signal.new()
  return setmetatable({ _listeners = {} }, Signal)
end

local Connection = {}
Connection.__index = Connection

function Connection:Disconnect()
  if not self.Connected then
    return
  end
  self.Connected = false
  local listeners = self._signal._listeners
  for i = #listeners, 1, -1 do
    if listeners[i] == self then
      table.remove(listeners, i)
    end
  end
end

-- f(...) every time the signal fires; returns a connection (Disconnect, Connected).
function Signal:Connect(f)
  if type(f) ~= "function" then
    error("Signal:Connect expects a function, got a " .. type(f) .. " value", 2)
  end
  local connection = setmetatable({ Connected = true, _signal = self, _f = f }, Connection)
  self._listeners[#self._listeners + 1] = connection
  return connection
end

function Signal:Fire(...)
  local listeners = self._listeners
  if #listeners == 0 then
    return
  end
  local copy = table.move(listeners, 1, #listeners, 1, {})
  for i = 1, #copy do
    if copy[i].Connected then
      copy[i]._f(...)
    end
  end
end

function Signal:Count()
  return #self._listeners
end

function Signal:DisconnectAll()
  for i = #self._listeners, 1, -1 do
    self._listeners[i].Connected = false
    self._listeners[i] = nil
  end
end

return Signal
