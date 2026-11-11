namespace Wisko.Engine.Scripting;

/// <summary>
/// The allow-list that defines what a Wisko script can see. The language is standard Lua (the runtime's Lua 5.4):
/// Wisko adds engine values and decides which host capabilities a script gets, it never changes the language. Scripts
/// never run with the host's global table: every script gets its own fresh environment built here, so nothing outside
/// this list exists for it.
/// <list type="bullet">
/// <item>Base: assert, error, ipairs, next, pairs, pcall, select, tonumber, tostring, type, xpcall, rawequal, rawget,
/// rawlen, rawset, getmetatable, setmetatable (refuses __gc/__close), print and warn (Wisko's Output), load (text chunks
/// only, in this script's environment unless given another), require (see below), collectgarbage ("count",
/// "isrunning", "collect", "step"), _G (the script's own environment), _VERSION.</item>
/// <item>Libraries, as per-script copies so one script cannot change another's: math (math.random draws from the Lua
/// state's one generator, so math.randomseed reseeds it for every script of the Play session), string (without dump:
/// binary chunks are never loaded), table, utf8, coroutine (the script's code runs as Lua's main thread would: yielding
/// outside a coroutine is an error), os (clock, date, difftime, time only), debug (traceback only). String methods
/// (<c>s:upper()</c>) come from the running script's own string library, as in standard Lua.</item>
/// <item>package / require: pure-Lua modules from the project's Assets/Scripts folder (package.path "./?.lua;./?/init.lua",
/// "." being that folder; package.loaded, preload, searchers, searchpath, config; cpath is empty and there is no loadlib:
/// no native modules). Each script has its own package.loaded, so a module runs once per script that requires it, in
/// that script's environment: scripts never share module state. A circular require is an error instead of a stack
/// overflow.</item>
/// <item>Engine API: Time (read-only deltaTime, elapsedTime, frameCount, fixedDeltaTime); Input (read-only:
/// IsKeyDown/IsKeyPressed/IsKeyReleased, IsMouseButtonDown/IsMouseButtonPressed/IsMouseButtonReleased by engine
/// key/button name, GetAxis, GetMousePosition, and mouseX, mouseY, mouseDeltaX, mouseDeltaY, scrollDelta; SetMouseLocked /
/// MouseLocked for mouse look); Shared (one plain table every script of the session sees); Scene (Find, FindAll,
/// FindById, GetAll, FindByType / FindAllByType, FindByTag / FindAllByTag, Count / CountByType / CountByTag: the running
/// scene's objects; Create: a new Part, positional or with an options table); UI (Find, FindAll, FindPath, FindById,
/// Root, Create, GetCanvasRect: the Play session's GUI copy); Timer (After, Every: callbacks in game time with extra
/// arguments, with handles: Active, Remaining, Paused, Cancel, Pause, Resume, Restart); Physics (Raycast, RaycastAll,
/// SphereCast, OverlapSphere, OverlapBox: each with an optional filter; Gravity, writable); Events (On, Emit: queued
/// messages between scripts, tables copied); Camera (the Play view: Position, Rotation, Forward, FieldOfView, LookAt,
/// WorldToScreen, ScreenPointToRay); Atmosphere (the session's
/// sky, sun, fog and image settings); Vector3 (vector values and math); and, for a script attached to a scene object,
/// self (the same kind of object wrapper Scene returns: Name, Id, Type, Exists, Color, Visible, Transform.Position /
/// Rotation / Scale with x, y, z, physics settings (Anchored and IsTrigger writable), IsSleeping, LinearVelocity /
/// AngularVelocity, ApplyImpulse / ApplyForce / ApplyTorque / ApplyImpulseAtPoint / ApplyAngularImpulse, Wake, tags,
/// attributes, GetBounds, Clone, AttachScript, Destroy, RotationLocked, IsGrounded, SetParent / Parent / GetChildren
/// (Play-only attachment), GetScript (another script's exports), OnDestroyed). Collision and trigger functions get the
/// contact as a second argument; LateUpdate(dt) runs after physics each frame.</item>
/// </list>
/// Objects and GUI elements are plain Lua tables with locked metatables, never .NET objects: there is no member, method
/// or type behind them for a script to reflect on. Each script gets its own wrappers (one per object or element); a
/// wrapper holds only an Id, and every read, write and method goes to the host (hostScene / hostGui), which looks the
/// Id up again, so every script sees the same live state.
/// Deliberately absent (host capabilities, not language): dofile, loadfile, io, the rest of os and debug, string.dump,
/// native modules (package.loadlib, cpath), collectgarbage's options that change the shared collector, and NLua's CLR
/// bridge (luanet, import, CLRPackage).
/// </summary>
internal static partial class LuaSandbox
{
    /// <summary>
    /// Trusted setup chunk, run once per runtime in the host state with the full global table. Arguments: the host
    /// log function, the print length cap, the engine's key and mouse button names (arrays), the host GUI, Scene,
    /// Timer, Physics, module and collector functions (see LuaScriptRuntime.HostGui / HostScene / HostTimer /
    /// HostPhysics / HostModule / HostGc) and the runner thread scripts run on. Returns newEnvironment(scriptName,
    /// selfId) (which returns the environment, its deliver(f, id): calls f with this script's wrapper of object id, or
    /// nil, and its string library), the host-written Time values, the host-written Input values, the strings'
    /// metatable (whose __index the host points at the running script's string library) and stopThread(thread).
    /// </summary>
    public const string Prelude = """
        local hostWrite, maxPrintLength, keyNames, buttonNames, hostGui, hostScene, hostTimer, hostPhysics, hostModule,
          hostGc, runner, hostEvents, hostView, fixedDeltaTime, hostGameplay, gameplaySetup = ...

        -- The gameplay systems (LuaSandbox.Gameplay): object methods and globals the gameplay prelude adds; set up below,
        -- before any environment exists.
        local gameplayObjectMethods, extendEnvironment

        local assert, error, ipairs, next, pairs, pcall, select = assert, error, ipairs, next, pairs, pcall, select
        local tonumber, tostring, type, xpcall = tonumber, tostring, type, xpcall
        local rawequal, rawget, rawlen, rawset = rawequal, rawget, rawlen, rawset
        local getmetatable, setmetatable = getmetatable, setmetatable
        local concat, sub, format, lower = table.concat, string.sub, string.format, string.lower
        local find, gmatch = string.find, string.gmatch
        local nativeLoad, nativeCollect = load, collectgarbage
        local coroutineRunning, coroutineYield, coroutineIsYieldable = coroutine.running, coroutine.yield, coroutine.isyieldable
        local traceback, sethook = debug.traceback, debug.sethook
        local osClock, osDate, osDifftime, osTime = os.clock, os.date, os.difftime, os.time
        local version = _VERSION
        local maxFloat = 3.4028234663852886e38
        local unpack, pack = table.unpack, table.pack
        local sqrt, mathType = math.sqrt, math.type

        -- Every string shares one metatable. Its methods ("s:upper()") come from the running script's own string
        -- library (scripts get copies): before every callback the host points __index at that script's library, so a
        -- script that adds string.trim can call s:trim(), as in standard Lua, without changing any other script's
        -- strings. The metatable itself stays locked: getmetatable("") never hands a script the shared table.
        local stringMetatable = getmetatable("")
        stringMetatable.__metatable = false
        -- Binary chunks are never loaded, so dumping one is of no use.
        string.dump = nil

        -- The instruction budget's last resort. Where a script cannot be paused (inside table.sort's comparison, a gsub
        -- replacement function, a __close handler, one of its own coroutines...), the host installs this as that
        -- thread's hook: from its next instruction on, every instruction raises an error, which unwinds the script.
        local function budgetError()
          error("instruction budget exceeded", 0)
        end

        local function stopThread(thread)
          sethook(thread, budgetError, "", 1)
        end

        local function copyLibrary(library, omit)
          local copy = {}
          for key, value in next, library do
            if key ~= omit then
              copy[key] = value
            end
          end
          return copy
        end

        -- Finalizers (__gc) run with debug hooks disabled, i.e. outside the instruction budget, and __close handlers
        -- can run while the host resets a stopped script; neither is offered to scripts.
        local function scriptSetmetatable(target, metatable)
          if type(metatable) == "table" and (rawget(metatable, "__gc") ~= nil or rawget(metatable, "__close") ~= nil) then
            error("__gc and __close metamethods are not available to scripts", 2)
          end
          -- Called through pcall so a failure carries no sandbox position; re-raised at the script's call site.
          local ok, result = pcall(setmetatable, target, metatable)
          if not ok then
            error(result, 2)
          end
          return result
        end

        -- frameCount: frames played so far (0 in Start); fixedDeltaTime: the physics step in seconds.
        -- unscaledDeltaTime (real seconds of the frame), timeScale (Time.SetTimeScale), realTime (seconds since Play began,
        -- high resolution); Time.SetTimeScale and Time.OnUpdate are added by the gameplay prelude.
        local timeValues = { deltaTime = 0, elapsedTime = 0, frameCount = 0, fixedDeltaTime = fixedDeltaTime,
          unscaledDeltaTime = 0, timeScale = 1, realTime = 0 }
        local timeMetatable = {
          __index = timeValues,
          __newindex = function() error("Time is read-only", 2) end,
          __metatable = "Time",
        }

        -- Unknown members are errors (typos fail loudly). Level 3: the script that indexed, behind the metamethod.
        local function invalidMember(owner, key)
          error(tostring(key) .. " is not a valid member of " .. owner, 3)
        end

        -- Input: one snapshot per frame, written by the host before the frame's callbacks and shared by every script.
        -- Key/button sets map an engine name to true; the host replaces them each frame.
        local inputValues = {
          mouseX = 0, mouseY = 0, mouseDeltaX = 0, mouseDeltaY = 0, scrollDelta = 0,
          keysDown = {}, keysPressed = {}, keysReleased = {},
          buttonsDown = {}, buttonsPressed = {}, buttonsReleased = {},
        }
        local inputNumbers = { mouseX = true, mouseY = true, mouseDeltaX = true, mouseDeltaY = true, scrollDelta = true }

        -- Names are matched case-insensitively ("w", "W", "space", "Space").
        local function nameLookup(names)
          local lookup = {}
          for i = 1, #names do
            lookup[lower(names[i])] = names[i]
          end
          return lookup
        end
        local knownKeys, knownButtons = nameLookup(keyNames), nameLookup(buttonNames)
        local maxInputWarnings = 10

        local function newInput(scriptName)
          local proxy
          local warned, warnings = {}, 0

          -- Unknown or malformed names are not errors: the query answers false and the script's log gets one warning
          -- per distinct name (capped, so a script cannot flood the log or grow this table without bound).
          local function resolve(functionName, name, known, what)
            if type(name) == "string" then
              local canonical = known[lower(name)]
              if canonical ~= nil then
                return canonical
              end
            end
            local warningKey = functionName .. "\0" .. (type(name) == "string" and name or type(name))
            if warnings < maxInputWarnings and not warned[warningKey] then
              warned[warningKey] = true
              warnings = warnings + 1
              local detail
              if rawequal(name, proxy) then
                detail = "call it with a dot: Input." .. functionName .. "(\"" .. (what == "key" and "W" or "Left") .. "\")"
              elseif type(name) == "string" then
                detail = format("unknown %s name %q", what, sub(name, 1, 40))
              else
                detail = format("expected a %s name, got a %s value", what, type(name))
              end
              hostWrite(scriptName, "Input." .. functionName .. ": " .. detail .. " (returns false)", true)
            end
            return nil
          end

          local function query(functionName, known, what, setName)
            return function(name)
              local canonical = resolve(functionName, name, known, what)
              return canonical ~= nil and inputValues[setName][canonical] == true
            end
          end

          local function keyDown(functionName, name)
            local canonical = resolve(functionName, name, knownKeys, "key")
            return canonical ~= nil and inputValues.keysDown[canonical] == true
          end

          local functions = {
            -- The cursor in window pixels from the top-left (as mouseX, mouseY).
            GetMousePosition = function()
              return inputValues.mouseX, inputValues.mouseY
            end,
            -- -1, 0 or 1 from two held keys: GetAxis("A", "D") is -1 while only A is down, 1 while only D is, else 0.
            GetAxis = function(negative, positive)
              return (keyDown("GetAxis", positive) and 1 or 0) - (keyDown("GetAxis", negative) and 1 or 0)
            end,
            -- Locks (true) or frees (false) the mouse: while locked the window holds and hides the cursor, mouseDeltaX /
            -- mouseDeltaY keep reporting movement (mouse look) and the editor's panels ignore the mouse. Stop always
            -- frees it (Escape stops Play). Returns whether it is locked now (false without a Play view).
            SetMouseLocked = function(locked)
              if rawequal(locked, proxy) then
                error("call it with a dot: Input.SetMouseLocked(true)", 2)
              end
              if type(locked) ~= "boolean" then
                error("Input.SetMouseLocked expects true or false, got a " .. type(locked) .. " value", 2)
              end
              if not hostView("mouselock", "", locked) then
                return false
              end
              local _, now = hostView("mouselocked")
              return now == true
            end,
            IsKeyDown = query("IsKeyDown", knownKeys, "key", "keysDown"),
            IsKeyPressed = query("IsKeyPressed", knownKeys, "key", "keysPressed"),
            IsKeyReleased = query("IsKeyReleased", knownKeys, "key", "keysReleased"),
            IsMouseButtonDown = query("IsMouseButtonDown", knownButtons, "mouse button", "buttonsDown"),
            IsMouseButtonPressed = query("IsMouseButtonPressed", knownButtons, "mouse button", "buttonsPressed"),
            IsMouseButtonReleased = query("IsMouseButtonReleased", knownButtons, "mouse button", "buttonsReleased"),
          }

          proxy = setmetatable({}, {
            __index = function(_, key)
              local member = functions[key]
              if member ~= nil then
                return member
              end
              if inputNumbers[key] then
                return inputValues[key]
              end
              if key == "MouseLocked" then
                local ok, locked = hostView("mouselocked")
                return ok == true and locked == true
              end
              if key == "IsTyping" then
                return hostGui("typing") == true
              end
              invalidMember("Input", key)
            end,
            __newindex = function() error("Input is read-only", 2) end,
            __tostring = function() return "Input" end,
            __metatable = "Input",
          })
          return proxy
        end

        local function isFinite(value)
          return type(value) == "number" and value == value and value <= maxFloat and value >= -maxFloat
        end

        -- Vectors. Engine vectors (a Transform's Position, a velocity, a raycast's Point...) are locked views with x, y,
        -- z; Vector3 values (Vector3.new) are plain tables {x, y, z} with arithmetic (their fields are ordinary fields:
        -- a value whose x, y or z is not a number is simply not a vector any more). Anything with numbers x, y and z
        -- counts as a vector wherever the engine takes one.
        local vectorValueMetatable

        local function vectorParts(value)
          if type(value) ~= "table" then
            return nil
          end
          local x, y, z
          if getmetatable(value) == "Vector3" then
            x, y, z = value.x, value.y, value.z
          else
            x, y, z = rawget(value, "x"), rawget(value, "y"), rawget(value, "z")
          end
          if type(x) == "number" and type(y) == "number" and type(z) == "number" then
            return x, y, z
          end
          return nil
        end

        local function newVector3(x, y, z)
          return setmetatable({ x = x, y = y, z = z }, vectorValueMetatable)
        end

        -- Level 3: the script line doing the arithmetic, behind the metamethod.
        local function operand(value, operation)
          local x, y, z = vectorParts(value)
          if x == nil then
            error("attempt to perform arithmetic (" .. operation .. ") on a Vector3 and a " .. type(value) .. " value", 3)
          end
          return x, y, z
        end

        -- Arithmetic every vector has: + and - (vectors), * and / (by a number, or per component), unary -, ==.
        local vectorArithmetic = {
          __add = function(a, b)
            local ax, ay, az = operand(a, "+")
            local bx, by, bz = operand(b, "+")
            return newVector3(ax + bx, ay + by, az + bz)
          end,
          __sub = function(a, b)
            local ax, ay, az = operand(a, "-")
            local bx, by, bz = operand(b, "-")
            return newVector3(ax - bx, ay - by, az - bz)
          end,
          __mul = function(a, b)
            if type(a) == "number" then
              local x, y, z = operand(b, "*")
              return newVector3(a * x, a * y, a * z)
            end
            local ax, ay, az = operand(a, "*")
            if type(b) == "number" then
              return newVector3(ax * b, ay * b, az * b)
            end
            local bx, by, bz = operand(b, "*")
            return newVector3(ax * bx, ay * by, az * bz)
          end,
          __div = function(a, b)
            local ax, ay, az = operand(a, "/")
            if type(b) == "number" then
              return newVector3(ax / b, ay / b, az / b)
            end
            local bx, by, bz = operand(b, "/")
            return newVector3(ax / bx, ay / by, az / bz)
          end,
          __unm = function(a)
            local x, y, z = operand(a, "-")
            return newVector3(-x, -y, -z)
          end,
          __eq = function(a, b)
            local ax, ay, az = vectorParts(a)
            local bx, by, bz = vectorParts(b)
            return ax ~= nil and bx ~= nil and ax == bx and ay == by and az == bz
          end,
        }

        -- A metatable with the vector arithmetic added to the given members.
        local function withArithmetic(metatable)
          for key, value in next, vectorArithmetic do
            metatable[key] = value
          end
          return metatable
        end

        local axes = { x = true, y = true, z = true }

        vectorValueMetatable = withArithmetic({
          __index = function(_, key)
            invalidMember("Vector3", key)
          end,
          __newindex = function(vector, key, value)
            if not axes[key] then
              invalidMember("Vector3", key)
            end
            if type(value) ~= "number" then
              error("Vector3." .. key .. " must be a number", 2)
            end
            rawset(vector, key, value)
          end,
          __tostring = function(vector)
            return format("Vector3(%g, %g, %g)", rawget(vector, "x"), rawget(vector, "y"), rawget(vector, "z"))
          end,
          __metatable = "Vector3",
        })

        -- Level 3: the script that called the Vector3 function.
        local function checkVector(functionName, position, value)
          local x, y, z = vectorParts(value)
          if x == nil then
            error(format("Vector3.%s expects a vector as argument #%d (numbers x, y, z), got a %s value", functionName, position, type(value)), 3)
          end
          return x, y, z
        end

        local function checkNumber(functionName, position, value)
          if type(value) ~= "number" then
            error(format("Vector3.%s expects a number as argument #%d, got a %s value", functionName, position, type(value)), 3)
          end
          return value
        end

        local vector3Functions = {
          new = function(...)
            local x, y, z = ...
            if select("#", ...) == 0 then
              return newVector3(0, 0, 0)
            end
            if type(x) ~= "number" or type(y) ~= "number" or type(z) ~= "number" then
              error("Vector3.new expects three numbers (x, y, z), or none for (0, 0, 0)", 2)
            end
            return newVector3(x, y, z)
          end,
          add = function(a, b)
            local ax, ay, az = checkVector("add", 1, a)
            local bx, by, bz = checkVector("add", 2, b)
            return newVector3(ax + bx, ay + by, az + bz)
          end,
          sub = function(a, b)
            local ax, ay, az = checkVector("sub", 1, a)
            local bx, by, bz = checkVector("sub", 2, b)
            return newVector3(ax - bx, ay - by, az - bz)
          end,
          mul = function(a, b)
            if type(a) == "number" then
              local x, y, z = checkVector("mul", 2, b)
              return newVector3(a * x, a * y, a * z)
            end
            local x, y, z = checkVector("mul", 1, a)
            local scale = checkNumber("mul", 2, b)
            return newVector3(x * scale, y * scale, z * scale)
          end,
          dot = function(a, b)
            local ax, ay, az = checkVector("dot", 1, a)
            local bx, by, bz = checkVector("dot", 2, b)
            return ax * bx + ay * by + az * bz
          end,
          cross = function(a, b)
            local ax, ay, az = checkVector("cross", 1, a)
            local bx, by, bz = checkVector("cross", 2, b)
            return newVector3(ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx)
          end,
          magnitude = function(a)
            local x, y, z = checkVector("magnitude", 1, a)
            return sqrt(x * x + y * y + z * z)
          end,
          -- Length 1 in the same direction; a zero vector stays (0, 0, 0).
          normalize = function(a)
            local x, y, z = checkVector("normalize", 1, a)
            local length = sqrt(x * x + y * y + z * z)
            if length == 0 or length ~= length then
              return newVector3(0, 0, 0)
            end
            return newVector3(x / length, y / length, z / length)
          end,
          distance = function(a, b)
            local ax, ay, az = checkVector("distance", 1, a)
            local bx, by, bz = checkVector("distance", 2, b)
            local dx, dy, dz = ax - bx, ay - by, az - bz
            return sqrt(dx * dx + dy * dy + dz * dz)
          end,
          -- a + (b - a) * t (t is not clamped).
          lerp = function(a, b, t)
            local ax, ay, az = checkVector("lerp", 1, a)
            local bx, by, bz = checkVector("lerp", 2, b)
            t = checkNumber("lerp", 3, t)
            return newVector3(ax + (bx - ax) * t, ay + (by - ay) * t, az + (bz - az) * t)
          end,
          -- The same direction, at most maxLength long (a speed limit).
          clampMagnitude = function(v, maxLength)
            local x, y, z = checkVector("clampMagnitude", 1, v)
            maxLength = checkNumber("clampMagnitude", 2, maxLength)
            local length = sqrt(x * x + y * y + z * z)
            if length <= maxLength or length == 0 then
              return newVector3(x, y, z)
            end
            local scale = math.max(maxLength, 0) / length
            return newVector3(x * scale, y * scale, z * scale)
          end,
          -- From a toward b by at most maxDelta (reaches b exactly, never overshoots): steady acceleration.
          moveTowards = function(a, b, maxDelta)
            local ax, ay, az = checkVector("moveTowards", 1, a)
            local bx, by, bz = checkVector("moveTowards", 2, b)
            maxDelta = checkNumber("moveTowards", 3, maxDelta)
            local dx, dy, dz = bx - ax, by - ay, bz - az
            local distance = sqrt(dx * dx + dy * dy + dz * dz)
            if distance <= maxDelta or distance == 0 then
              return newVector3(bx, by, bz)
            end
            local scale = maxDelta / distance
            return newVector3(ax + dx * scale, ay + dy * scale, az + dz * scale)
          end,
          -- The part of v along onto (zero when onto is zero).
          project = function(v, onto)
            local x, y, z = checkVector("project", 1, v)
            local ox, oy, oz = checkVector("project", 2, onto)
            local lengthSquared = ox * ox + oy * oy + oz * oz
            if lengthSquared == 0 then
              return newVector3(0, 0, 0)
            end
            local scale = (x * ox + y * oy + z * oz) / lengthSquared
            return newVector3(ox * scale, oy * scale, oz * scale)
          end,
          -- The angle between two vectors in degrees (0 to 180; 0 when either is zero).
          angle = function(a, b)
            local ax, ay, az = checkVector("angle", 1, a)
            local bx, by, bz = checkVector("angle", 2, b)
            local lengths = sqrt((ax * ax + ay * ay + az * az) * (bx * bx + by * by + bz * bz))
            if lengths == 0 then
              return 0
            end
            local cosine = (ax * bx + ay * by + az * bz) / lengths
            return math.deg(math.acos(math.max(-1, math.min(1, cosine))))
          end,
        }

        -- math, as standard Lua has it, plus four helpers every game needs (each script gets its own copy).
        local function newMath()
          local library = copyLibrary(math)
          local function checkNumberArgument(functionName, position, value)
            if type(value) ~= "number" then
              error(format("math.%s expects a number as argument #%d, got a %s value", functionName, position, type(value)), 3)
            end
          end
          -- x kept within min..max.
          library.clamp = function(x, min, max)
            checkNumberArgument("clamp", 1, x)
            checkNumberArgument("clamp", 2, min)
            checkNumberArgument("clamp", 3, max)
            if min > max then
              error("math.clamp expects min to be at most max", 2)
            end
            return x < min and min or (x > max and max or x)
          end
          -- a + (b - a) * t (t is not clamped).
          library.lerp = function(a, b, t)
            checkNumberArgument("lerp", 1, a)
            checkNumberArgument("lerp", 2, b)
            checkNumberArgument("lerp", 3, t)
            return a + (b - a) * t
          end
          -- -1, 0 or 1.
          library.sign = function(x)
            checkNumberArgument("sign", 1, x)
            return x > 0 and 1 or (x < 0 and -1 or 0)
          end
          -- The nearest whole number (halves away from zero), as an integer when it fits.
          library.round = function(x)
            checkNumberArgument("round", 1, x)
            local rounded = x >= 0 and math.floor(x + 0.5) or -math.floor(-x + 0.5)
            return math.tointeger(rounded) or rounded
          end
          return library
        end

        -- Exactly `count` numbers from the arguments from `first` on: a number counts as one, a vector as its x, y, z.
        -- Returns them in a table and the index of the first argument left, or nil when they do not fit.
        local function takeNumbers(count, first, ...)
          local numbers, taken, index, total = {}, 0, first, select("#", ...)
          while taken < count and index <= total do
            local value = (select(index, ...))
            if type(value) == "number" then
              taken = taken + 1
              numbers[taken] = value
            else
              local x, y, z = vectorParts(value)
              if x == nil or taken + 3 > count then
                return nil
              end
              numbers[taken + 1], numbers[taken + 2], numbers[taken + 3] = x, y, z
              taken = taken + 3
            end
            index = index + 1
          end
          if taken ~= count then
            return nil
          end
          return numbers, index
        end

        -- Three numbers, or one vector in their place.
        local function threeNumbers(x, y, z)
          if type(x) == "table" and y == nil and z == nil then
            return vectorParts(x)
          end
          return x, y, z
        end

        -- Scene objects (self, and what Scene finds): each script has its own wrappers, one per object. A wrapper holds
        -- the object's Id (and, for messages, the name it had when found), never the object: every read, write and method
        -- asks the host (hostScene), which looks the Id up again, so all scripts see one state. Plain tables with
        -- locked metatables: no member, method or type behind them for a script to reflect on.
        local objectProperties = { Id = true, Name = true, Type = true, Exists = true,
          Anchored = true, GravityEnabled = true, CanCollide = true, IsTrigger = true, Friction = true, Mass = true,
          Color = true, Visible = true, IsSleeping = true, RotationLocked = true,
          GravityScale = true, LinearDamping = true, AngularDamping = true, CollisionGroup = true }
        local objectReadOnly = { Id = true, Type = true, Exists = true, IsSleeping = true }
        local objectMethods = { ApplyImpulse = true, Destroy = true, ApplyForce = true, ApplyTorque = true,
          ApplyImpulseAtPoint = true, ApplyAngularImpulse = true, Wake = true, AddTag = true, RemoveTag = true, HasTag = true,
          GetTags = true, SetAttribute = true, GetAttribute = true, HasAttribute = true, RemoveAttribute = true,
          GetAttributes = true, GetBounds = true, Clone = true, AttachScript = true,
          IsGrounded = true, SetParent = true, GetChildren = true, GetScript = true, OnDestroyed = true,
          ApplyForceAtPoint = true, GetVelocityAtPoint = true, GetMass = true, Rotate = true, RotateAround = true, LookAt = true,
          PointToWorld = true, PointToLocal = true, VectorToWorld = true, VectorToLocal = true, IgnoreCollisionWith = true,
          OnAttributeChanged = true,
          HasAnimations = true, GetAnimations = true, PlayAnimation = true, CrossFadeAnimation = true, StopAnimation = true,
          PauseAnimation = true, ResumeAnimation = true, RestartAnimation = true, SetAnimationSpeed = true, SetAnimationLooping = true,
          GetAnimationTime = true, SetAnimationTime = true, GetAnimationLength = true, GetCurrentAnimation = true,
          IsAnimationPlaying = true, OnAnimationFinished = true,
          Teleport = true, GetBones = true, HasBone = true, GetBoneTransform = true, OnTriggerEntered = true, OnTriggerExited = true,
          AddLight = true, RemoveLight = true }
        -- Its local axes in the world now (length 1): Forward is local -Z, Right local +X, Up local +Y. Read-only values.
        local objectDirections = { Forward = true, Right = true, Up = true }

        -- An object's Material and Light members (host-checked: types, names and ranges).
        local materialKeys = { Preset = true, Shading = true, Tint = true, Metallic = true, Roughness = true, Opacity = true,
          AlphaMode = true, AlphaCutoff = true, EmissiveColor = true, EmissiveIntensity = true, NormalStrength = true,
          OcclusionStrength = true, ToonBands = true, Outline = true, OutlineColor = true, OutlineThickness = true,
          FlatShading = true, DoubleSided = true, CastShadows = true, ReceiveShadows = true, BaseColorTexture = true,
          NormalTexture = true, MetallicRoughnessTexture = true, OcclusionTexture = true, EmissiveTexture = true,
          UvMode = true, UvScaleX = true, UvScaleY = true }
        local lightKeys = { Type = true, Enabled = true, Color = true, Intensity = true, Range = true, Angle = true,
          InnerAngle = true, CastShadows = true }


        -- Every script's object wrappers -> their object Id, for the host-side API that takes an object (e.g. Raycast's
        -- ignoreObject). Weak keys: it never keeps a wrapper alive, and scripts cannot reach it.
        local objectIds = setmetatable({}, { __mode = "k" })

        -- Two scripts' wrappers of one object are equal (==), though they are different tables (as table keys, use .Id).
        local function sameObject(a, b)
          local id = objectIds[a]
          return id ~= nil and id == objectIds[b]
        end

        -- Defined further down (Connections with the GUI's, RaycastResults with Physics); objects use them too.
        local newConnection, raycastResult

        -- How far below its feet an object still counts as standing (IsGrounded's default), in studs.
        local defaultGroundTolerance = 0.15

        -- A runtime-attached script's handle (object:AttachScript): Active and Path ask the host, Detach tells it.
        local function newScriptHandle(handleId)
          local handle
          local function detach(self)
            if not rawequal(self, handle) then
              error("call it with a colon: script:Detach()", 2)
            end
            hostScene("detach", handleId)
          end
          handle = setmetatable({}, {
            __index = function(_, key)
              if key == "Active" then
                local active = hostScene("scriptinfo", handleId)
                return active == true
              end
              if key == "Path" then
                local _, path = hostScene("scriptinfo", handleId)
                return path
              end
              if key == "Detach" then
                return detach
              end
              invalidMember("ScriptHandle", key)
            end,
            __newindex = function(_, key)
              if key == "Active" or key == "Path" then
                error(key .. " is read-only", 2)
              end
              if key == "Detach" then
                error("Detach is a method and cannot be replaced", 2)
              end
              invalidMember("ScriptHandle", key)
            end,
            __tostring = function()
              local active, path = hostScene("scriptinfo", handleId)
              return "ScriptHandle(" .. tostring(path) .. (active and ", active" or ", inactive") .. ")"
            end,
            __metatable = "ScriptHandle",
          })
          return handle
        end

        local function newObjects()
          local wrappers = setmetatable({}, { __mode = "v" })
          local wrap

          wrap = function(id)
            if id == nil then
              return nil
            end
            local object = wrappers[id]
            if object ~= nil then
              return object
            end

            local foundAs = hostScene("name", id) or "?"
            local function currentName()
              return hostScene("name", id) or foundAs
            end
            -- Levels count from here: 3 = the script, behind the metamethod or method that called this.
            local function fail(message)
              error(message or ("ScriptObject '" .. foundAs .. "' (#" .. id .. ") no longer exists"), 3)
            end

            -- A whole vector written at once (Transform.Position = v, LinearVelocity = v): one change, not three. Anything
            -- that is not a vector cannot replace the live view.
            local function setVector(key, label, value)
              local x, y, z = vectorParts(value)
              if x == nil then
                error(label .. " cannot be replaced; set its components instead (e.g. " .. label .. ".x)", 3)
              end
              if not (isFinite(x) and isFinite(y) and isFinite(z)) then
                error(key .. " must be finite", 3)
              end
              local ok, message = hostScene("vsetall", id, key, x, y, z)
              if not ok then
                error(message or ("ScriptObject '" .. foundAs .. "' (#" .. id .. ") no longer exists"), 3)
              end
            end

            -- One Vector3 view onto three values of the object (a Transform vector or a velocity).
            local function newVector(key, label)
              return setmetatable({}, withArithmetic({
                __index = function(_, axis)
                  if not axes[axis] then
                    invalidMember(label, axis)
                  end
                  local ok, x, y, z = hostScene("vget", id, key)
                  if not ok then
                    fail(x)
                  end
                  if axis == "x" then
                    return x
                  elseif axis == "y" then
                    return y
                  end
                  return z
                end,
                __newindex = function(_, axis, value)
                  if not axes[axis] then
                    invalidMember(label, axis)
                  end
                  if not isFinite(value) then
                    error(label .. "." .. axis .. " must be a finite number", 2)
                  end
                  local ok, message = hostScene("vset", id, key, axis, value)
                  if not ok then
                    fail(message)
                  end
                end,
                __tostring = function()
                  local ok, x, y, z = hostScene("vget", id, key)
                  if not ok then
                    return label .. "(?)"
                  end
                  return format("%s(%g, %g, %g)", label, x, y, z)
                end,
                __metatable = "Vector3",
              }))
            end

            -- Its Transform, velocity views and methods are made when first used: a wrapper that is only compared or
            -- passed on (a raycast's Object, a contact's other) stays cheap to make.
            local transform, velocities, methods

            local function getTransform()
              if transform ~= nil then
                return transform
              end
              local transformMembers = {
                Position = newVector("Position", "Position"),
                Rotation = newVector("Rotation", "Rotation"),
                Scale = newVector("Scale", "Scale"),
              }
              transform = setmetatable({}, {
                __index = function(_, key)
                  local member = transformMembers[key]
                  if member == nil then
                    invalidMember("Transform", key)
                  end
                  return member
                end,
                __newindex = function(_, key, value)
                  if transformMembers[key] ~= nil then
                    setVector(key, "Transform." .. key, value)
                    return
                  end
                  invalidMember("Transform", key)
                end,
                __tostring = function()
                  return "Transform(" .. currentName() .. ")"
                end,
                __metatable = "Transform",
              })
              return transform
            end

            local function getVelocity(key)
              velocities = velocities or {}
              local velocity = velocities[key]
              if velocity == nil then
                velocity = newVector(key, key)
                velocities[key] = velocity
              end
              return velocity
            end

            -- Its Material (every object has one) and its Light (nil until it has one), views onto the
            -- host's values like Transform: each read asks the host, each write is checked and changed there.
            local material, light
            local function getMaterial()
              if material ~= nil then
                return material
              end
              local function applyPreset(self, name)
                if not rawequal(self, material) then
                  error("call it with a colon: object.Material:ApplyPreset(\"Metal\")", 2)
                end
                if type(name) ~= "string" then
                  error("ApplyPreset expects a preset name (a string), got a " .. type(name) .. " value", 2)
                end
                local ok, message = hostScene("matpreset", id, name)
                if not ok then
                  error(message or ("ScriptObject '" .. foundAs .. "' (#" .. id .. ") no longer exists"), 2)
                end
              end
              local function reset(self)
                if not rawequal(self, material) then
                  error("call it with a colon: object.Material:Reset()", 2)
                end
                local ok, message = hostScene("matreset", id)
                if not ok then
                  error(message or ("ScriptObject '" .. foundAs .. "' (#" .. id .. ") no longer exists"), 2)
                end
              end
              local materialMethods = { ApplyPreset = applyPreset, Reset = reset }
              material = setmetatable({}, {
                __index = function(_, key)
                  if materialMethods[key] then
                    return materialMethods[key]
                  end
                  if not materialKeys[key] then
                    invalidMember("Material", key)
                  end
                  local ok, value = hostScene("matget", id, key)
                  if not ok then
                    fail(value)
                  end
                  return value
                end,
                __newindex = function(_, key, value)
                  if materialMethods[key] then
                    error(key .. " is a method and cannot be replaced", 2)
                  end
                  if not materialKeys[key] then
                    invalidMember("Material", key)
                  end
                  if type(value) == "number" and not isFinite(value) then
                    error("Material." .. key .. " must be a finite number", 2)
                  end
                  local ok, message = hostScene("matset", id, key, value)
                  if not ok then
                    error(message or ("ScriptObject '" .. foundAs .. "' (#" .. id .. ") no longer exists"), 2)
                  end
                end,
                __tostring = function()
                  return "Material(" .. currentName() .. ")"
                end,
                __metatable = "Material",
              })
              return material
            end
            local function getLight()
              local ok, has = hostScene("lighthas", id)
              if not ok then
                fail(has)
              end
              if not has then
                return nil
              end
              if light ~= nil then
                return light
              end
              light = setmetatable({}, {
                __index = function(_, key)
                  if not lightKeys[key] then
                    invalidMember("Light", key)
                  end
                  local found, value = hostScene("lightget", id, key)
                  if not found then
                    fail(value)
                  end
                  return value
                end,
                __newindex = function(_, key, value)
                  if not lightKeys[key] then
                    invalidMember("Light", key)
                  end
                  if type(value) == "number" and not isFinite(value) then
                    error("Light." .. key .. " must be a finite number", 2)
                  end
                  local done, message = hostScene("lightset", id, key, value)
                  if not done then
                    error(message or ("ScriptObject '" .. foundAs .. "' (#" .. id .. ") no longer exists"), 2)
                  end
                end,
                __tostring = function()
                  return "Light(" .. currentName() .. ")"
                end,
                __metatable = "Light",
              })
              return light
            end
            local function getMethods()
              if methods ~= nil then
                return methods
              end
              local function checkSelf(self, usage)
                if not rawequal(self, object) then
                  error("call it with a colon: object:" .. usage, 3)
                end
              end

              -- Level 3: the script, behind the method that asked.
              local function request(...)
                local ok, message = hostScene(...)
                if not ok then
                  error(message or ("ScriptObject '" .. foundAs .. "' (#" .. id .. ") no longer exists"), 3)
                end
              end

              -- (x, y, z[, space]) or (vector[, space]): the three numbers and whether they are in the object's own axes
              -- ("Local"; "World", the default, as before). Level 4: the script, behind the method that asked.
              local function vectorInSpace(method, x, y, z, space)
                if type(x) == "table" then
                  space = y
                  x, y, z = vectorParts(x)
                end
                if not (isFinite(x) and isFinite(y) and isFinite(z)) then
                  error(method .. " expects three finite numbers (x, y, z) or a vector", 4)
                end
                if space ~= nil and space ~= "World" and space ~= "Local" then
                  error(method .. ": the space must be \"World\" or \"Local\", got " .. tostring(space), 4)
                end
                if space == "Local" then
                  local _, wx, wy, wz = hostScene("space", id, "vectorworld", x, y, z)
                  return wx, wy, wz
                end
                return x, y, z
              end

              local function applyImpulse(self, x, y, z, space)
                checkSelf(self, "ApplyImpulse(x, y, z)")
                x, y, z = vectorInSpace("ApplyImpulse", x, y, z, space)
                request("impulse", id, x, y, z)
              end

              -- ApplyForce / ApplyTorque / ApplyAngularImpulse(x, y, z or a vector[, "Local"]).
              local function push(kind, method)
                return function(self, x, y, z, space)
                  checkSelf(self, method .. "(x, y, z)")
                  x, y, z = vectorInSpace(method, x, y, z, space)
                  request("push", id, kind, x, y, z)
                end
              end

              -- A force at a world point for this frame (off the centre of mass it also turns the object).
              local function applyForceAtPoint(self, ...)
                checkSelf(self, "ApplyForceAtPoint(force, point)")
                local numbers = takeNumbers(6, 1, ...)
                if numbers == nil or not (isFinite(numbers[1]) and isFinite(numbers[2]) and isFinite(numbers[3])
                  and isFinite(numbers[4]) and isFinite(numbers[5]) and isFinite(numbers[6])) then
                  error("ApplyForceAtPoint expects a force and a world point: two vectors or six finite numbers", 2)
                end
                request("push", id, "forceat", unpack(numbers, 1, 6))
              end

              -- How fast a world point fixed to the object moves now (linear velocity plus spin), as a Vector3.
              local function getVelocityAtPoint(self, ...)
                checkSelf(self, "GetVelocityAtPoint(point)")
                local point = takeNumbers(3, 1, ...)
                if point == nil or not (isFinite(point[1]) and isFinite(point[2]) and isFinite(point[3])) then
                  error("GetVelocityAtPoint expects a world point: a vector or three finite numbers", 2)
                end
                local ok, vx, vy, vz = hostScene("pointvel", id, point[1], point[2], point[3])
                if not ok then
                  fail(vx)
                end
                return newVector3(vx, vy, vz)
              end

              -- The mass physics uses now (Mass, or density x volume on Auto); nil when physics does not simulate it.
              local function getMass(self)
                checkSelf(self, "GetMass()")
                local ok, mass = hostScene("mass", id)
                if not ok then
                  fail(mass)
                end
                return mass
              end

              -- PointToWorld / PointToLocal / VectorToWorld / VectorToLocal(vector or x, y, z) -> Vector3.
              local function convert(method, mode)
                return function(self, ...)
                  checkSelf(self, method .. "(vector)")
                  local value = takeNumbers(3, 1, ...)
                  if value == nil or not (isFinite(value[1]) and isFinite(value[2]) and isFinite(value[3])) then
                    error(method .. " expects a vector or three finite numbers", 2)
                  end
                  local ok, x, y, z = hostScene("space", id, mode, value[1], value[2], value[3])
                  if not ok then
                    fail(x)
                  end
                  return newVector3(x, y, z)
                end
              end

              -- Turns it by Euler degrees: Rotate(x, y, z[, "World"]) or Rotate(vector[, "World"]); its own axes by default.
              local function rotate(self, x, y, z, space)
                checkSelf(self, "Rotate(x, y, z)")
                if type(x) == "table" then
                  space = y
                  x, y, z = vectorParts(x)
                end
                if not (isFinite(x) and isFinite(y) and isFinite(z)) then
                  error("Rotate expects three finite numbers of degrees (x, y, z) or a vector", 2)
                end
                if space ~= nil and space ~= "World" and space ~= "Local" then
                  error("Rotate: the space must be \"Local\" (default) or \"World\", got " .. tostring(space), 2)
                end
                request("rotate", id, space == "World" and "world" or "local", x, y, z)
              end

              -- Swings it around a world point and axis by some degrees (position and orientation).
              local function rotateAround(self, pivot, axis, degrees)
                checkSelf(self, "RotateAround(pivot, axis, degrees)")
                local px, py, pz = vectorParts(pivot)
                local ax, ay, az = vectorParts(axis)
                if px == nil or ax == nil or not (isFinite(px) and isFinite(py) and isFinite(pz) and isFinite(ax) and isFinite(ay)
                  and isFinite(az)) or not isFinite(degrees) then
                  error("RotateAround expects a pivot (vector), an axis (vector) and degrees (a number)", 2)
                end
                if ax == 0 and ay == 0 and az == 0 then
                  error("RotateAround expects an axis that is not zero", 2)
                end
                request("rotatearound", id, "", px, py, pz, ax, ay, az, degrees)
              end

              -- Turns it so its Forward points at a world point (up: optional vector, default world up).
              local function lookAt(self, point, up)
                checkSelf(self, "LookAt(point[, up])")
                local x, y, z = vectorParts(point)
                local ux, uy, uz = 0, 1, 0
                if up ~= nil then
                  ux, uy, uz = vectorParts(up)
                end
                if x == nil or ux == nil or not (isFinite(x) and isFinite(y) and isFinite(z) and isFinite(ux) and isFinite(uy) and isFinite(uz)) then
                  error("LookAt expects a point (vector) and optionally an up direction (vector)", 2)
                end
                request("lookat", id, "", x, y, z, ux, uy, uz)
              end

              -- It and another object never touch (nor trigger each other) while ignore is true (default true).
              local function ignoreCollisionWith(self, other, ignore)
                checkSelf(self, "IgnoreCollisionWith(other[, ignore])")
                local otherId = objectIds[other]
                if otherId == nil then
                  error("IgnoreCollisionWith expects a scene object, got a " .. type(other) .. " value", 2)
                end
                if ignore ~= nil and type(ignore) ~= "boolean" then
                  error("IgnoreCollisionWith expects ignore to be true, false or nil", 2)
                end
                request("ignorecol", id, otherId, ignore ~= false)
              end

              -- f(value, object) at the start of the frame after the attribute changes (set to another value, or removed:
              -- nil). Returns a Connection; it goes with this script.
              local function onAttributeChanged(self, name, callback)
                checkSelf(self, "OnAttributeChanged(\"Name\", function)")
                if type(name) ~= "string" or name == "" then
                  error("OnAttributeChanged expects an attribute name (a string), got a " .. type(name) .. " value", 2)
                end
                if type(callback) ~= "function" then
                  error("OnAttributeChanged expects a function, got a " .. type(callback) .. " value", 2)
                end
                local handlerId, message = hostScene("onattr", id, name, callback)
                if not handlerId then
                  fail(message)
                end
                return newConnection(hostEvents, handlerId)
              end

              local function applyImpulseAtPoint(self, ...)
                checkSelf(self, "ApplyImpulseAtPoint(impulse, point)")
                local numbers = takeNumbers(6, 1, ...)
                if numbers == nil or not (isFinite(numbers[1]) and isFinite(numbers[2]) and isFinite(numbers[3])
                  and isFinite(numbers[4]) and isFinite(numbers[5]) and isFinite(numbers[6])) then
                  error("ApplyImpulseAtPoint expects an impulse and a world point: two vectors or six finite numbers", 2)
                end
                request("push", id, "impulseat", unpack(numbers, 1, 6))
              end

              -- Removes the object from the running scene at once (Stop brings an authored one back).
              local function destroy(self)
                checkSelf(self, "Destroy()")
                request("destroy", id)
              end

              local function wake(self)
                checkSelf(self, "Wake()")
                request("wake", id)
              end

              -- Tags and attributes: the Part's own (every script sees the same), gone at Stop.
              local function tagMethod(method, op)
                return function(self, tag)
                  checkSelf(self, method .. "(\"Tag\")")
                  if type(tag) ~= "string" then
                    error(method .. " expects a tag (a string), got a " .. type(tag) .. " value", 2)
                  end
                  local ok, value = hostScene("tag", id, op, tag)
                  if not ok then
                    fail(value)
                  end
                  if op == "has" then
                    return value
                  end
                end
              end

              local function getTags(self)
                checkSelf(self, "GetTags()")
                local ok, tags = hostScene("tag", id, "list")
                if not ok then
                  fail(tags)
                end
                return tags
              end

              local function attributeMethod(method, op)
                return function(self, name, ...)
                  checkSelf(self, method .. "(\"Name\"" .. (op == "set" and ", value)" or ")"))
                  if type(name) ~= "string" then
                    error(method .. " expects an attribute name (a string), got a " .. type(name) .. " value", 2)
                  end
                  if op == "set" then
                    local value = ...
                    local kind = type(value)
                    if kind ~= "nil" and kind ~= "boolean" and kind ~= "number" and kind ~= "string" then
                      error("SetAttribute: attributes hold numbers, strings or booleans (nil removes one), not a " .. kind .. " value", 2)
                    end
                  end
                  local ok, value = hostScene("attr", id, op, name, (...))
                  if not ok then
                    fail(value)
                  end
                  if op == "get" or op == "has" then
                    return value
                  end
                end
              end

              local function getAttributes(self)
                checkSelf(self, "GetAttributes()")
                local ok, attributes = hostScene("attr", id, "all")
                if not ok then
                  fail(attributes)
                end
                return attributes
              end

              -- The smallest world box around the object's exact shape: its min and max corners.
              local function getBounds(self)
                checkSelf(self, "GetBounds()")
                local ok, minX, minY, minZ, maxX, maxY, maxZ = hostScene("bounds", id)
                if not ok then
                  fail(minX)
                end
                return newVector3(minX, minY, minZ), newVector3(maxX, maxY, maxZ)
              end

              local function clone(self, name)
                checkSelf(self, "Clone([name])")
                if name ~= nil and type(name) ~= "string" then
                  error("Clone expects a name (a string) or nothing, got a " .. type(name) .. " value", 2)
                end
                local cloneId, message = hostScene("clone", id, name)
                if not cloneId then
                  fail(message)
                end
                return wrap(cloneId)
              end

              local function attachScript(self, path)
                checkSelf(self, "AttachScript(\"Script.lua\")")
                if type(path) ~= "string" then
                  error("AttachScript expects a script path (a string, e.g. \"Spin.lua\"), got a " .. type(path) .. " value", 2)
                end
                local handleId, message = hostScene("attach", id, path)
                if not handleId then
                  fail(message)
                end
                return newScriptHandle(handleId)
              end

              -- Standing on something: whether the object's underside is within maxDistance (default 0.15) of a
              -- surface below it (its own attachments and triggers never count), and that surface as a RaycastResult
              -- (Object: nil for the environment; Distance: the gap left below the object; Point; Normal).
              local function isGrounded(self, maxDistance)
                checkSelf(self, "IsGrounded([maxDistance])")
                if maxDistance == nil then
                  maxDistance = defaultGroundTolerance
                elseif not isFinite(maxDistance) or maxDistance < 0 then
                  error("IsGrounded expects maxDistance to be a finite number of at least 0 (or nothing)", 2)
                end
                local ok, grounded, objectId, gap, px, py, pz, nx, ny, nz = hostScene("ground", id, maxDistance)
                if not ok then
                  fail(grounded)
                end
                if not grounded then
                  return false, nil
                end
                return true, raycastResult(wrap, objectId, gap, px, py, pz, nx, ny, nz)
              end

              -- One vector argument or nothing (an attachment offset).
              local function offsetOf(value, what)
                if value == nil then
                  return false, 0, 0, 0
                end
                local x, y, z = vectorParts(value)
                if x == nil or not (isFinite(x) and isFinite(y) and isFinite(z)) then
                  error("SetParent expects " .. what .. " to be a vector (finite x, y, z) or nil", 3)
                end
                return true, x, y, z
              end

              -- Attaches the object to another for the rest of Play (nil lets it go): from then on it follows that
              -- parent every frame, after physics, at the offset given (position in the parent's space, rotation in
              -- Euler degrees), or where it is now relative to the parent when no offset is given. An attached object
              -- is moved by its parent, not by physics (it is anchored while attached; letting go restores it) and never
              -- collides with its own parent, children or siblings. Nothing about it is saved.
              local function setParent(self, parent, offsetPosition, offsetRotation, bone)
                checkSelf(self, "SetParent(parent[, offsetPosition[, offsetRotation[, bone]]])")
                if bone ~= nil and (type(bone) ~= "string" or bone == "") then
                  error("SetParent expects bone to be a bone name (a string, see parent:GetBones()) or nil, got a " .. type(bone) .. " value", 2)
                end
                local parentId = 0
                if parent ~= nil then
                  parentId = objectIds[parent]
                  if parentId == nil then
                    error("SetParent expects a scene object or nil (to let go), got a " .. type(parent) .. " value", 2)
                  end
                end
                local hasPosition, px, py, pz = offsetOf(offsetPosition, "offsetPosition")
                local hasRotation, rx, ry, rz = offsetOf(offsetRotation, "offsetRotation")
                request("setparent", id, parentId, hasPosition, px, py, pz, hasRotation, rx, ry, rz, bone)
              end

              local function getChildren(self)
                checkSelf(self, "GetChildren()")
                local ok, ids = hostScene("children", id)
                if not ok then
                  fail(ids)
                end
                local result = {}
                for i = 1, #ids do
                  result[i] = wrap(ids[i])
                end
                return result
              end

              -- What one of the object's running scripts exports: the table its file returned (return M), shared with
              -- that script (its functions run as that script wrote them). nil and the reason when there is none.
              local function getScript(self, name)
                checkSelf(self, "GetScript(\"Name\")")
                if type(name) ~= "string" or name == "" then
                  error("GetScript expects a script name (a string, e.g. \"Health\" or \"Health.lua\"), got a " .. type(name) .. " value", 2)
                end
                local ok, exports, reason = hostScene("getscript", id, name)
                if not ok then
                  fail(exports)
                end
                return exports, reason
              end

              -- f(object) once the object is destroyed (at the start of the next frame; the object no longer exists
              -- then). Returns a Connection; it goes with this script.
              local function onDestroyed(self, callback)
                checkSelf(self, "OnDestroyed(function)")
                if type(callback) ~= "function" then
                  error("OnDestroyed expects a function, got a " .. type(callback) .. " value", 2)
                end
                local handlerId, message = hostScene("ondestroyed", id, callback)
                if not handlerId then
                  fail(message)
                end
                return newConnection(hostEvents, handlerId)
              end

              -- Animation (imported models): a clip is named by its name or its number (1 = the first, as GetAnimations
              -- lists them). Level 3: the script, behind the method that asked.
              local function clipArgument(method, clip)
                if type(clip) == "string" then
                  return clip, false
                end
                if type(clip) == "number" and clip == math.floor(clip) then
                  return clip, true
                end
                error(method .. " expects an animation name (a string) or number (1 = the first), got a " .. type(clip) .. " value", 3)
              end

              local playOptionNames = { Loop = true, Speed = true, FadeTime = true, StartTime = true, Restart = true }

              -- { Loop = boolean, Speed = number, FadeTime = seconds (Play only), StartTime = seconds, Restart = boolean }.
              local function playOptions(method, options, allowFade)
                if options == nil then
                  return nil, nil, nil, nil, false
                end
                if type(options) ~= "table" then
                  error(method .. " expects options to be a table (e.g. { Loop = false, Speed = 1.5 }) or nil, got a " .. type(options) .. " value", 3)
                end
                for key in pairs(options) do
                  if not playOptionNames[key] or (key == "FadeTime" and not allowFade) then
                    error(method .. ": unknown option " .. tostring(key) .. " (use Loop, Speed, " .. (allowFade and "FadeTime, " or "") .. "StartTime or Restart)", 3)
                  end
                end
                local loop, speed, fade, start, restart = options.Loop, options.Speed, options.FadeTime, options.StartTime, options.Restart
                if loop ~= nil and type(loop) ~= "boolean" then
                  error(method .. ": Loop must be true or false", 3)
                end
                if speed ~= nil and not isFinite(speed) then
                  error(method .. ": Speed must be a finite number (1 = as authored, negative plays backwards)", 3)
                end
                if fade ~= nil and not (isFinite(fade) and fade >= 0) then
                  error(method .. ": FadeTime must be a number of seconds, 0 or more", 3)
                end
                if start ~= nil and not isFinite(start) then
                  error(method .. ": StartTime must be a finite number of seconds", 3)
                end
                if restart ~= nil and type(restart) ~= "boolean" then
                  error(method .. ": Restart must be true or false", 3)
                end
                return loop, speed, fade, start, restart == true
              end

              -- Plays a clip at once (or fading in over FadeTime): true, or false and why (an unknown name is not an error).
              local function playAnimation(self, clip, options)
                checkSelf(self, "PlayAnimation(\"Name\"[, options])")
                local value, byNumber = clipArgument("PlayAnimation", clip)
                local loop, speed, fade, start, restart = playOptions("PlayAnimation", options, true)
                local ok, played, reason = hostScene("anim", id, "play", value, byNumber, loop, speed, fade or 0, start, restart)
                if not ok then
                  fail(played)
                end
                return played, reason
              end

              -- Crossfades from what shows now to a clip over fadeTime seconds: true, or false and why.
              local function crossFadeAnimation(self, clip, fadeTime, options)
                checkSelf(self, "CrossFadeAnimation(\"Name\", fadeTime[, options])")
                local value, byNumber = clipArgument("CrossFadeAnimation", clip)
                if not (isFinite(fadeTime) and fadeTime >= 0) then
                  error("CrossFadeAnimation expects fadeTime: a number of seconds, 0 or more", 2)
                end
                local loop, speed, _, start, restart = playOptions("CrossFadeAnimation", options, false)
                local ok, played, reason = hostScene("anim", id, "play", value, byNumber, loop, speed, fadeTime, start, restart)
                if not ok then
                  fail(played)
                end
                return played, reason
              end

              -- An animation request answering true[, value].
              local function animationValue(op, ...)
                local ok, value = hostScene("anim", id, op, ...)
                if not ok then
                  fail(value)
                end
                return value
              end

              local function noArguments(method, op)
                return function(self)
                  checkSelf(self, method .. "()")
                  return animationValue(op)
                end
              end

              -- An optional clip argument (nil: the current clip).
              local function optionalClip(method, op)
                return function(self, clip)
                  checkSelf(self, method .. "([\"Name\"])")
                  if clip == nil then
                    return animationValue(op)
                  end
                  local value, byNumber = clipArgument(method, clip)
                  return animationValue(op, value, byNumber)
                end
              end

              local function stopAnimation(self, fadeTime)
                checkSelf(self, "StopAnimation([fadeTime])")
                if fadeTime ~= nil and not (isFinite(fadeTime) and fadeTime >= 0) then
                  error("StopAnimation expects fadeTime to be a number of seconds, 0 or more (or nothing)", 2)
                end
                animationValue("stop", fadeTime or 0)
              end

              local function setAnimationSpeed(self, speed)
                checkSelf(self, "SetAnimationSpeed(speed)")
                if not isFinite(speed) then
                  error("SetAnimationSpeed expects a finite number (1 = as authored, 0 holds, negative plays backwards)", 2)
                end
                animationValue("speed", speed)
              end

              local function setAnimationLooping(self, loop)
                checkSelf(self, "SetAnimationLooping(loop)")
                if type(loop) ~= "boolean" then
                  error("SetAnimationLooping expects true or false, got a " .. type(loop) .. " value", 2)
                end
                animationValue("loop", loop)
              end

              local function setAnimationTime(self, seconds)
                checkSelf(self, "SetAnimationTime(seconds)")
                if not isFinite(seconds) then
                  error("SetAnimationTime expects a finite number of seconds", 2)
                end
                animationValue("settime", seconds)
              end

              -- f(name, object) at the start of the frame after the current clip, playing once, reaches its end.
              local function onAnimationFinished(self, callback)
                checkSelf(self, "OnAnimationFinished(function)")
                if type(callback) ~= "function" then
                  error("OnAnimationFinished expects a function, got a " .. type(callback) .. " value", 2)
                end
                local handlerId, message = hostScene("onanim", id, callback)
                if not handlerId then
                  fail(message)
                end
                return newConnection(hostEvents, handlerId)
              end

              -- Gives it a light ("Point" by default, "Spot" or "Directional"; replaces one it had) and
              -- answers object.Light; RemoveLight takes it away (object.Light is then nil).
              local function addLight(self, lightType)
                checkSelf(self, "AddLight([\"Point\" | \"Spot\" | \"Directional\"])")
                if lightType ~= nil and type(lightType) ~= "string" then
                  error("AddLight expects a light type (\"Point\", \"Spot\" or \"Directional\") or nothing, got a " .. type(lightType) .. " value", 2)
                end
                request("lightadd", id, lightType)
                return getLight()
              end
              local function removeLight(self)
                checkSelf(self, "RemoveLight()")
                request("lightremove", id)
              end

              methods = {
                ApplyImpulse = applyImpulse,
                ApplyForce = push("force", "ApplyForce"),
                ApplyTorque = push("torque", "ApplyTorque"),
                ApplyAngularImpulse = push("angular", "ApplyAngularImpulse"),
                ApplyImpulseAtPoint = applyImpulseAtPoint,
                Destroy = destroy,
                AddLight = addLight,
                RemoveLight = removeLight,
                Wake = wake,
                AddTag = tagMethod("AddTag", "add"),
                RemoveTag = tagMethod("RemoveTag", "remove"),
                HasTag = tagMethod("HasTag", "has"),
                GetTags = getTags,
                SetAttribute = attributeMethod("SetAttribute", "set"),
                GetAttribute = attributeMethod("GetAttribute", "get"),
                HasAttribute = attributeMethod("HasAttribute", "has"),
                RemoveAttribute = attributeMethod("RemoveAttribute", "remove"),
                GetAttributes = getAttributes,
                GetBounds = getBounds,
                Clone = clone,
                AttachScript = attachScript,
                IsGrounded = isGrounded,
                SetParent = setParent,
                GetChildren = getChildren,
                GetScript = getScript,
                OnDestroyed = onDestroyed,
                ApplyForceAtPoint = applyForceAtPoint,
                GetVelocityAtPoint = getVelocityAtPoint,
                GetMass = getMass,
                Rotate = rotate,
                RotateAround = rotateAround,
                LookAt = lookAt,
                PointToWorld = convert("PointToWorld", "pointworld"),
                PointToLocal = convert("PointToLocal", "pointlocal"),
                VectorToWorld = convert("VectorToWorld", "vectorworld"),
                VectorToLocal = convert("VectorToLocal", "vectorlocal"),
                IgnoreCollisionWith = ignoreCollisionWith,
                OnAttributeChanged = onAttributeChanged,
                HasAnimations = noArguments("HasAnimations", "has"),
                GetAnimations = noArguments("GetAnimations", "list"),
                PlayAnimation = playAnimation,
                CrossFadeAnimation = crossFadeAnimation,
                StopAnimation = stopAnimation,
                PauseAnimation = noArguments("PauseAnimation", "pause"),
                ResumeAnimation = noArguments("ResumeAnimation", "resume"),
                RestartAnimation = noArguments("RestartAnimation", "restart"),
                SetAnimationSpeed = setAnimationSpeed,
                SetAnimationLooping = setAnimationLooping,
                GetAnimationTime = noArguments("GetAnimationTime", "gettime"),
                SetAnimationTime = setAnimationTime,
                GetAnimationLength = optionalClip("GetAnimationLength", "length"),
                GetCurrentAnimation = noArguments("GetCurrentAnimation", "current"),
                IsAnimationPlaying = optionalClip("IsAnimationPlaying", "playing"),
                OnAnimationFinished = onAnimationFinished,
              }
              -- The gameplay object methods (teleporting, bones, trigger connections).
              for name, method in next, gameplayObjectMethods(id, object, fail, checkSelf, request) do
                methods[name] = method
              end
              return methods
            end

            -- The object it follows (SetParent), or nil.
            local function getParent()
              local ok, parentId = hostScene("parentof", id)
              if not ok then
                fail(parentId)
              end
              return wrap(parentId)
            end

            object = setmetatable({}, {
              __index = function(_, key)
                if key == "Transform" then
                  return getTransform()
                end
                if key == "Material" then
                  return getMaterial()
                end
                if key == "Light" then
                  return getLight()
                end
                if key == "LinearVelocity" or key == "AngularVelocity" or key == "SurfaceVelocity" then
                  return getVelocity(key)
                end
                if objectMethods[key] then
                  return getMethods()[key]
                end
                if key == "Parent" then
                  return getParent()
                end
                if objectDirections[key] then
                  local ok, x, y, z = hostScene("vget", id, key)
                  if not ok then
                    fail(x)
                  end
                  return newVector3(x, y, z)
                end
                if key == "Id" then
                  return id
                end
                if not objectProperties[key] then
                  invalidMember("ScriptObject", key)
                end
                local ok, value = hostScene("get", id, key)
                if not ok then
                  if key == "Name" and value == nil then
                    -- Gone: its last name (OnDestroyed, OnCollisionExit and messages can still say what it was).
                    return currentName()
                  end
                  fail(value)
                end
                return value
              end,
              __newindex = function(_, key, value)
                if key == "Material" or key == "Light" then
                  error(key .. " cannot be replaced; set its members instead (e.g. object." .. key .. ".Color)", 2)
                end
                if key == "Transform" or objectReadOnly[key] then
                  error(key .. " is read-only", 2)
                end
                if key == "LinearVelocity" or key == "AngularVelocity" or key == "SurfaceVelocity" then
                  setVector(key, key, value)
                  return
                end
                if objectDirections[key] then
                  error(key .. " is read-only (turn the object with Rotate, LookAt or Transform.Rotation)", 2)
                end
                if key == "Parent" then
                  -- As SetParent(value) without offsets: it keeps where it is relative to the new parent.
                  local parentId = 0
                  if value ~= nil then
                    parentId = objectIds[value]
                    if parentId == nil then
                      error("Parent must be a scene object or nil, got a " .. type(value) .. " value", 2)
                    end
                  end
                  local ok, message = hostScene("setparent", id, parentId, false, 0, 0, 0, false, 0, 0, 0)
                  if not ok then
                    error(message or ("ScriptObject '" .. foundAs .. "' (#" .. id .. ") no longer exists"), 2)
                  end
                  return
                end
                if objectMethods[key] then
                  error(key .. " is a method and cannot be replaced", 2)
                end
                if not objectProperties[key] then
                  invalidMember("ScriptObject", key)
                end
                local ok, message = hostScene("set", id, key, value)
                if not ok then
                  fail(message)
                end
              end,
              __tostring = function()
                return "ScriptObject(" .. currentName() .. ")"
              end,
              __eq = sameObject,
              __metatable = "ScriptObject",
            })
            wrappers[id] = object
            objectIds[object] = id
            return object
          end
          return wrap
        end

        -- Scene: the running scene's objects by name or Id (exact, case-sensitive names; scene order; names need not be
        -- unique). Nothing found is nil (FindAll: an empty table), never an error.
        local function newScene(wrap)
          local scene
          local function checkName(functionName, name)
            if type(name) ~= "string" then
              if rawequal(name, scene) then
                error("call it with a dot: Scene." .. functionName .. "(\"Name\")", 3)
              end
              error("Scene." .. functionName .. " expects a name (a string), got a " .. type(name) .. " value", 3)
            end
          end

          local function optional(value, expected, what)
            if value ~= nil and type(value) ~= expected then
              error("Scene.Create expects " .. what .. ", got a " .. type(value) .. " value", 3)
            end
          end

          -- Type and tag searches take a non-empty string.
          local function checkText(functionName, value, what, example)
            if type(value) ~= "string" then
              if rawequal(value, scene) then
                error("call it with a dot: Scene." .. functionName .. "(\"" .. example .. "\")", 3)
              end
              error("Scene." .. functionName .. " expects " .. what .. " (a string), got a " .. type(value) .. " value", 3)
            end
          end

          local function wrapAll(ids)
            local result = {}
            for i = 1, #ids do
              result[i] = wrap(ids[i])
            end
            return result
          end

          -- Scene.Create(type, options): every option is checked before the Part is made; it is made with Name,
          -- Anchored and IsTrigger, then gets the rest as if a script set them (one failure removes it again).
          local createOptions = {
            Name = "string", Anchored = "boolean", IsTrigger = "boolean", CanCollide = "boolean", GravityEnabled = "boolean",
            Visible = "boolean", Color = "string", Friction = "number", Mass = "number", RotationLocked = "boolean",
            Position = "vector", Rotation = "vector", Size = "vector", LinearVelocity = "vector", Tags = "table", Attributes = "table",
            GravityScale = "number", CollisionGroup = "string",
          }
          local optionNames = "Name, Anchored, IsTrigger, Position, Rotation, Size, Color, CanCollide, GravityEnabled, Friction, "
            .. "Mass, Visible, RotationLocked, LinearVelocity, GravityScale, CollisionGroup, Tags, Attributes"

          local function createWithOptions(typeName, options)
            for key, value in next, options do
              local kind = createOptions[key]
              if kind == nil then
                error("Scene.Create: " .. tostring(key) .. " is not an option (" .. optionNames .. ")", 3)
              end
              if kind == "vector" then
                local x, y, z = vectorParts(value)
                if x == nil or not (isFinite(x) and isFinite(y) and isFinite(z)) then
                  error("Scene.Create: " .. key .. " must be a vector (finite numbers x, y, z)", 3)
                end
              elseif type(value) ~= kind then
                error("Scene.Create: " .. key .. " must be a " .. kind .. ", got a " .. type(value) .. " value", 3)
              end
            end
            if options.Color ~= nil and not find(options.Color, "^#?%x%x%x%x%x%x$") then
              error("Scene.Create: Color must be \"#RRGGBB\" (six hex digits)", 3)
            end
            if options.Friction ~= nil and not isFinite(options.Friction) then
              error("Scene.Create: Friction must be a finite number (0 to 1)", 3)
            end
            if options.Mass ~= nil and not (isFinite(options.Mass) and options.Mass > 0) then
              error("Scene.Create: Mass must be a positive number", 3)
            end
            local tags = options.Tags
            if tags ~= nil then
              for i = 1, rawlen(tags) do
                if type(rawget(tags, i)) ~= "string" then
                  error("Scene.Create: Tags must be a list of strings", 3)
                end
              end
            end
            local attributes = options.Attributes
            if attributes ~= nil then
              for key, value in next, attributes do
                local kind = type(value)
                if type(key) ~= "string" or (kind ~= "boolean" and kind ~= "number" and kind ~= "string") then
                  error("Scene.Create: Attributes must map names (strings) to numbers, strings or booleans", 3)
                end
              end
            end

            local id, message = hostScene("create", typeName, options.Name, options.Anchored ~= false, options.IsTrigger == true)
            if not id then
              error(message, 3)
            end
            local function apply(ok, failure)
              if not ok then
                hostScene("destroy", id)
                error("Scene.Create: " .. tostring(failure), 4)
              end
            end
            for _, key in ipairs({ "Size", "Rotation", "Position" }) do
              local value = options[key]
              if value ~= nil then
                local x, y, z = vectorParts(value)
                apply(hostScene("vsetall", id, key == "Size" and "Scale" or key, x, y, z))
              end
            end
            for _, key in ipairs({ "Color", "Visible", "CanCollide", "GravityEnabled", "Friction", "Mass", "RotationLocked", "GravityScale", "CollisionGroup" }) do
              local value = options[key]
              if value ~= nil then
                apply(hostScene("set", id, key, value))
              end
            end
            -- Last: a launched projectile leaves with its velocity once its mass and shape are set.
            if options.LinearVelocity ~= nil then
              apply(hostScene("vsetall", id, "LinearVelocity", vectorParts(options.LinearVelocity)))
            end
            if tags ~= nil then
              for i = 1, rawlen(tags) do
                apply(hostScene("tag", id, "add", rawget(tags, i)))
              end
            end
            if attributes ~= nil then
              for key, value in next, attributes do
                apply(hostScene("attr", id, "set", key, value))
              end
            end
            return wrap(id)
          end

          local members = {
            Find = function(name)
              checkName("Find", name)
              return wrap(hostScene("find", name))
            end,
            -- A new Part in the running scene (gone at Stop): its type, then optionally its name (default: the type),
            -- whether it is anchored (default true) and whether it is a trigger (default false); or its type and an
            -- options table (see createWithOptions).
            Create = function(typeName, name, anchored, isTrigger)
              if type(typeName) ~= "string" then
                if rawequal(typeName, scene) then
                  error("call it with a dot: Scene.Create(\"Block\")", 2)
                end
                error("Scene.Create expects a type name (\"Block\", \"Cylinder\", \"Wedge\", \"Sphere\" or \"Pyramid\"), got a "
                  .. type(typeName) .. " value", 2)
              end
              if type(name) == "table" then
                if anchored ~= nil or isTrigger ~= nil then
                  error("Scene.Create expects a type and an options table, or a type, name, anchored and isTrigger", 2)
                end
                local created = createWithOptions(typeName, name)
                return created
              end
              optional(name, "string", "a name (a string) or nil")
              optional(anchored, "boolean", "anchored to be true, false or nil")
              optional(isTrigger, "boolean", "isTrigger to be true, false or nil")
              local id, message = hostScene("create", typeName, name, anchored ~= false, isTrigger == true)
              if not id then
                error(message, 2)
              end
              return wrap(id)
            end,
            FindAll = function(name)
              checkName("FindAll", name)
              local ids = hostScene("findall", name)
              local result = {}
              for i = 1, #ids do
                result[i] = wrap(ids[i])
              end
              return result
            end,
            FindById = function(id)
              if type(id) ~= "number" then
                if rawequal(id, scene) then
                  error("call it with a dot: Scene.FindById(1)", 2)
                end
                error("Scene.FindById expects a number, got a " .. type(id) .. " value", 2)
              end
              return wrap(hostScene("findid", id))
            end,
            -- Every object, in scene order (a new table).
            GetAll = function()
              return wrapAll(hostScene("all"))
            end,
            -- By Type ("Block", "Sphere", ..., "Model") or by tag: the first in scene order (nil when none), or all.
            FindByType = function(typeName)
              checkText("FindByType", typeName, "a type name", "Block")
              return wrap(hostScene("bytype", typeName, true))
            end,
            FindAllByType = function(typeName)
              checkText("FindAllByType", typeName, "a type name", "Block")
              return wrapAll(hostScene("bytype", typeName, false))
            end,
            FindByTag = function(tag)
              checkText("FindByTag", tag, "a tag", "Coin")
              return wrap(hostScene("bytag", tag, true))
            end,
            FindAllByTag = function(tag)
              checkText("FindAllByTag", tag, "a tag", "Coin")
              return wrapAll(hostScene("bytag", tag, false))
            end,
            Count = function()
              return hostScene("count")
            end,
            CountByType = function(typeName)
              checkText("CountByType", typeName, "a type name", "Block")
              return hostScene("countbytype", typeName)
            end,
            CountByTag = function(tag)
              checkText("CountByTag", tag, "a tag", "Coin")
              return hostScene("countbytag", tag)
            end,
          }
          scene = setmetatable({}, {
            __index = function(_, key)
              local member = members[key]
              if member == nil then
                invalidMember("Scene", key)
              end
              return member
            end,
            __newindex = function() error("Scene is read-only", 2) end,
            __tostring = function() return "Scene" end,
            __metatable = "Scene",
          })
          return scene
        end

        -- A connection (OnClick and the other button events, Events.On, OnDestroyed): Connected asks the host, Disconnect
        -- tells it.
        newConnection = function(host, handlerId)
          local connection
          local function disconnect(self)
            if not rawequal(self, connection) then
              error("call it with a colon: connection:Disconnect()", 2)
            end
            host(host == hostGui and "disconnect" or "off", handlerId)
          end
          connection = setmetatable({}, {
            __index = function(_, key)
              if key == "Connected" then
                return host("connected", handlerId)
              end
              if key == "Disconnect" then
                return disconnect
              end
              invalidMember("Connection", key)
            end,
            __newindex = function(_, key)
              if key == "Connected" then
                error("Connected is read-only", 2)
              end
              if key == "Disconnect" then
                error("Disconnect is a method and cannot be replaced", 2)
              end
              invalidMember("Connection", key)
            end,
            __tostring = function()
              return "Connection(" .. (host("connected", handlerId) and "connected" or "disconnected") .. ")"
            end,
            __metatable = "Connection",
          })
          return connection
        end

        -- UI: the Play session's own copy of the authored GUI, reached through hostGui. Each script has its own UI and its
        -- own element wrappers. A wrapper holds an element's Id (and, for messages, its type and name), never the
        -- element: every read, write and method asks the host, which looks the Id up again. An element's type never
        -- changes, so which members exist is decided here; values and existence are the host's.
        local guiProperties = {
          Id = "all", Name = "all", Type = "all", Visible = "all", ZOrder = "all", Parent = "all",
          Text = "label", FontSize = "label", Enabled = "Button", Color = "filled", Opacity = "Frame",
          TextColor = "label", HorizontalAlignment = "Text", VerticalAlignment = "Text", Image = "Image",
          PlaceholderText = "TextBox", MaxLength = "TextBox", ClearOnSubmit = "TextBox", Focused = "TextBox",
        }
        local guiReadOnly = { Id = true, Name = true, Type = true, Focused = true }
        local guiMethods = {
          SetPosition = "all", SetSize = "all", GetPosition = "all", GetSize = "all", GetAnchor = "all", SetAnchor = "all",
          Destroy = "all", SetParent = "all", GetChildren = "all", FindChild = "all",
          OnClick = "Button", OnPressed = "Button", OnReleased = "Button", OnHoverEnter = "Button", OnHoverExit = "Button",
          Focus = "TextBox", Release = "TextBox", OnSubmit = "TextBox", OnFocusLost = "TextBox", OnFocused = "TextBox",
        }
        local buttonEvents = { OnPressed = "Pressed", OnReleased = "Released", OnHoverEnter = "HoverEnter", OnHoverExit = "HoverExit" }
        local textBoxEvents = { OnSubmit = "Submit", OnFocusLost = "FocusLost", OnFocused = "Focused" }

        -- "label": Text, Button and TextBox (they draw text); "filled": Frame, Button and TextBox (a background Color).
        local function hasMember(kind, elementType)
          return kind == "all" or kind == elementType
            or (kind == "label" and (elementType == "Text" or elementType == "Button" or elementType == "TextBox"))
            or (kind == "filled" and (elementType == "Frame" or elementType == "Button" or elementType == "TextBox"))
        end

        -- Every script's GUI wrappers -> function giving their Id, type and name (Events.Emit carries elements by Id).
        local guiInfo = setmetatable({}, { __mode = "k" })

        local function newUI()
          local ui, root
          local wrappers = setmetatable({}, { __mode = "v" })
          local wrap

          -- The Id of a parent given to UI.Create / SetParent: UI.Root (0) or an element wrapper.
          local function parentId(value, functionName)
            if rawequal(value, root) then
              return 0
            end
            local info = guiInfo[value]
            if info == nil then
              error(functionName .. " expects a parent: UI.Root or a GUI element, got a " .. type(value) .. " value", 3)
            end
            return (info())
          end

          -- Children of the Canvas (0) or an element, as this script's wrappers, in order.
          local function children(id)
            local found = hostGui("children", id)
            local result = {}
            if found ~= nil then
              for i = 1, #found, 3 do
                result[#result + 1] = wrap(found[i], found[i + 1], found[i + 2])
              end
            end
            return result
          end

          wrap = function(id, elementType, name)
            if id == nil then
              return nil
            end
            if id == 0 then
              return root
            end
            local element = wrappers[id]
            if element ~= nil then
              return element
            end

            local label = elementType .. " '" .. name .. "'"
            -- Levels count from here: 3 = the script, behind the metamethod or method that called this.
            local function fail(message)
              error(message or ("GUI element '" .. name .. "' (#" .. id .. ") no longer exists"), 3)
            end
            local function noMember(key, what)
              error("GUI element '" .. name .. "' is a " .. elementType .. " and has no " .. key .. " " .. what, 3)
            end
            local function checkSelf(self, usage)
              if not rawequal(self, element) then
                error("call it with a colon: element:" .. usage, 3)
              end
            end

            local function rect(method)
              return function(self, xScale, xOffset, yScale, yOffset)
                if not rawequal(self, element) then
                  error("call it with a colon: element:" .. method .. "(xScale, xOffset, yScale, yOffset)", 2)
                end
                if not (isFinite(xScale) and isFinite(xOffset) and isFinite(yScale) and isFinite(yOffset)) then
                  error(method .. " expects four finite numbers (xScale, xOffset, yScale, yOffset)", 2)
                end
                local ok, message = hostGui("rect", id, method, xScale, xOffset, yScale, yOffset)
                if not ok then
                  fail(message)
                end
              end
            end

            -- GetPosition / GetSize: the four numbers SetPosition / SetSize take.
            local function readRect(method, which)
              return function(self)
                if not rawequal(self, element) then
                  error("call it with a colon: element:" .. method .. "()", 2)
                end
                local ok, a, b, c, d = hostGui("rectget", id, which)
                if not ok then
                  fail(a)
                end
                return a, b, c, d
              end
            end

            -- OnClick, OnPressed, OnReleased, OnHoverEnter, OnHoverExit: f runs (no arguments) as this script, at the
            -- start of the frame after it happened. Answers a connection.
            local function buttonEvent(method)
              return function(self, callback)
                if not rawequal(self, element) then
                  error("call it with a colon: element:" .. method .. "(function() ... end)", 2)
                end
                if type(callback) ~= "function" then
                  error(method .. " expects a function, got a " .. type(callback) .. " value", 2)
                end
                local handlerId, message
                if method == "OnClick" then
                  handlerId, message = hostGui("click", id, callback)
                else
                  handlerId, message = hostGui("event", id, buttonEvents[method], callback)
                end
                if not handlerId then
                  fail(message)
                end
                return newConnection(hostGui, handlerId)
              end
            end

            -- OnSubmit(f(text)), OnFocusLost(f(submitted)), OnFocused(f()): f runs as this script at the start of the
            -- next frame. Answers a connection.
            local function textBoxEvent(method)
              return function(self, callback)
                if not rawequal(self, element) then
                  error("call it with a colon: textBox:" .. method .. "(function(...) ... end)", 2)
                end
                if type(callback) ~= "function" then
                  error(method .. " expects a function, got a " .. type(callback) .. " value", 2)
                end
                local handlerId, message = hostGui("textevent", id, textBoxEvents[method], callback)
                if not handlerId then
                  fail(message)
                end
                return newConnection(hostEvents, handlerId)
              end
            end

            local methods = {
              SetPosition = rect("SetPosition"),
              SetSize = rect("SetSize"),
              GetPosition = readRect("GetPosition", "Position"),
              GetSize = readRect("GetSize", "Size"),
              GetAnchor = function(self)
                if not rawequal(self, element) then
                  error("call it with a colon: element:GetAnchor()", 2)
                end
                local ok, x, y = hostGui("anchorget", id)
                if not ok then
                  fail(x)
                end
                return x, y
              end,
              SetAnchor = function(self, x, y)
                if not rawequal(self, element) then
                  error("call it with a colon: element:SetAnchor(x, y)", 2)
                end
                if not (isFinite(x) and isFinite(y)) then
                  error("SetAnchor expects two finite numbers (x, y), each 0 to 1", 2)
                end
                local ok, message = hostGui("anchor", id, x, y)
                if not ok then
                  fail(message)
                end
              end,
              -- Removes the element and everything inside it (this session only; authored elements come back at Stop).
              Destroy = function(self)
                checkSelf(self, "Destroy()")
                local ok, message = hostGui("destroy", id)
                if not ok then
                  fail(message)
                end
              end,
              SetParent = function(self, parent)
                checkSelf(self, "SetParent(UI.Root or element)")
                local ok, message = hostGui("setparent", id, parentId(parent, "SetParent"))
                if not ok then
                  fail(message)
                end
              end,
              GetChildren = function(self)
                checkSelf(self, "GetChildren()")
                return children(id)
              end,
              FindChild = function(self, childName)
                checkSelf(self, "FindChild(\"Name\")")
                if type(childName) ~= "string" then
                  error("FindChild expects a name (a string), got a " .. type(childName) .. " value", 2)
                end
                return wrap(hostGui("findchild", id, childName))
              end,
              OnClick = buttonEvent("OnClick"),
              OnPressed = buttonEvent("OnPressed"),
              OnReleased = buttonEvent("OnReleased"),
              OnHoverEnter = buttonEvent("OnHoverEnter"),
              OnHoverExit = buttonEvent("OnHoverExit"),
              -- Gives the TextBox the keyboard: true, or false and why (it must be visible; not just after the player
              -- pressed Escape to leave a text box).
              Focus = function(self)
                checkSelf(self, "Focus()")
                local ok, message = hostGui("focus", id)
                if ok then
                  return true
                end
                if message == nil then
                  fail()
                end
                return false, message
              end,
              -- The TextBox lets go of the keyboard (nothing happens when it does not have it).
              Release = function(self)
                checkSelf(self, "Release()")
                local ok, message = hostGui("release", id)
                if not ok then
                  fail(message)
                end
              end,
              OnSubmit = textBoxEvent("OnSubmit"),
              OnFocusLost = textBoxEvent("OnFocusLost"),
              OnFocused = textBoxEvent("OnFocused"),
            }

            element = setmetatable({}, {
              __index = function(_, key)
                local method = guiMethods[key]
                if method ~= nil then
                  if not hasMember(method, elementType) then
                    noMember(key, "method")
                  end
                  return methods[key]
                end
                local property = guiProperties[key]
                if property == nil then
                  invalidMember(label, key)
                end
                if not hasMember(property, elementType) then
                  noMember(key, "property")
                end
                if key == "Parent" then
                  local ok, parent, parentType, parentName = hostGui("parent", id)
                  if not ok then
                    fail(parent)
                  end
                  return wrap(parent, parentType, parentName)
                end
                local ok, value = hostGui("get", id, key)
                if not ok then
                  fail(value)
                end
                return value
              end,
              __newindex = function(_, key, value)
                if guiMethods[key] ~= nil then
                  error(key .. " is a method and cannot be replaced", 2)
                end
                local property = guiProperties[key]
                if property == nil then
                  invalidMember(label, key)
                end
                if not hasMember(property, elementType) then
                  noMember(key, "property")
                end
                if guiReadOnly[key] then
                  error(key .. " is read-only", 2)
                end
                if key == "Parent" then
                  local ok, message = hostGui("setparent", id, parentId(value, "Parent"))
                  if not ok then
                    fail(message)
                  end
                  return
                end
                if key == "Text" and type(value) == "number" then
                  value = tostring(value)
                end
                local ok, message = hostGui("set", id, key, value)
                if not ok then
                  fail(message)
                end
              end,
              __tostring = function()
                return elementType .. "(" .. name .. ")"
              end,
              __metatable = "GuiElement",
            })
            wrappers[id] = element
            guiInfo[element] = function()
              return id, elementType, name
            end
            return element
          end

          -- UI.Root: the Canvas every top-level element is inside (Id 0). It cannot be changed or destroyed.
          local rootMembers = {
            GetChildren = function(self)
              if not rawequal(self, root) then
                error("call it with a colon: UI.Root:GetChildren()", 2)
              end
              return children(0)
            end,
            FindChild = function(self, childName)
              if not rawequal(self, root) then
                error("call it with a colon: UI.Root:FindChild(\"Name\")", 2)
              end
              if type(childName) ~= "string" then
                error("FindChild expects a name (a string), got a " .. type(childName) .. " value", 2)
              end
              return wrap(hostGui("findchild", 0, childName))
            end,
          }
          root = setmetatable({}, {
            __index = function(_, key)
              if key == "Id" then
                return 0
              elseif key == "Name" or key == "Type" then
                return "Canvas"
              elseif key == "Parent" then
                return nil
              end
              local member = rootMembers[key]
              if member == nil then
                invalidMember("UI.Root", key)
              end
              return member
            end,
            __newindex = function() error("UI.Root is read-only", 2) end,
            __tostring = function() return "Canvas" end,
            __metatable = "GuiElement",
          })

          -- Names are not unique: Find answers the first match in tree order (parents before children, siblings in
          -- their saved order). Nothing found is nil, never an error.
          local function find(name)
            if type(name) ~= "string" then
              if rawequal(name, ui) then
                error("call it with a dot: UI.Find(\"Name\")", 2)
              end
              error("UI.Find expects a name (a string), got a " .. type(name) .. " value", 2)
            end
            return wrap(hostGui("find", name))
          end

          local function findById(id)
            if type(id) ~= "number" then
              if rawequal(id, ui) then
                error("call it with a dot: UI.FindById(12)", 2)
              end
              error("UI.FindById expects a number, got a " .. type(id) .. " value", 2)
            end
            return wrap(hostGui("findid", id))
          end

          -- Every match in tree order, as a new 1-based table (empty when none).
          local function findAll(name)
            if type(name) ~= "string" then
              if rawequal(name, ui) then
                error("call it with a dot: UI.FindAll(\"Name\")", 2)
              end
              error("UI.FindAll expects a name (a string), got a " .. type(name) .. " value", 2)
            end
            local found = hostGui("findall", name)
            local result = {}
            for i = 1, #found, 3 do
              result[#result + 1] = wrap(found[i], found[i + 1], found[i + 2])
            end
            return result
          end

          -- "HUD/Menu/Play": names of direct children from the Canvas down (a leading "/" is allowed); among siblings
          -- with the same name the first in saved order. Hidden and disabled elements are found; ZOrder plays no part.
          local function findPath(path)
            if type(path) ~= "string" then
              if rawequal(path, ui) then
                error("call it with a dot: UI.FindPath(\"HUD/Button\")", 2)
              end
              error("UI.FindPath expects a path (a string), got a " .. type(path) .. " value", 2)
            end
            return wrap(hostGui("findpath", path))
          end

          -- UI.Create(type, options): the element is made with Name and Parent, then gets every other option as if the
          -- script set it (Position / Size: {xScale, xOffset, yScale, yOffset}; Anchor: {x, y}); one failure removes it.
          local createOptions = { Name = true, Parent = true, Position = true, Size = true, Anchor = true, Text = true, TextColor = true,
            Color = true, FontSize = true, Visible = true, ZOrder = true, Opacity = true, Image = true, Enabled = true,
            HorizontalAlignment = true, VerticalAlignment = true, PlaceholderText = true, MaxLength = true, ClearOnSubmit = true }
          local createOptionNames = "Name, Parent, Position, Size, Anchor, Text, TextColor, Color, FontSize, Visible, ZOrder, Opacity, Image, "
            .. "Enabled, HorizontalAlignment, VerticalAlignment, PlaceholderText, MaxLength, ClearOnSubmit"

          local function numbersOption(options, key, count)
            local value = options[key]
            if type(value) ~= "table" then
              error("UI.Create: " .. key .. " must be a table of " .. count .. " numbers", 4)
            end
            for i = 1, count do
              if not isFinite(rawget(value, i)) then
                error("UI.Create: " .. key .. " must be a table of " .. count .. " finite numbers", 4)
              end
            end
            return unpack(value, 1, count)
          end

          -- UI.Create("Frame" | "Text" | "Button" | "Image" | "TextBox" [, name [, parent]]) or UI.Create(type, options): a new
          -- element of this session's GUI, with its type's defaults, last among its parent's children (default:
          -- UI.Root); gone at Stop.
          local function create(elementType, name, parent)
            if type(elementType) ~= "string" then
              if rawequal(elementType, ui) then
                error("call it with a dot: UI.Create(\"Frame\")", 2)
              end
              error("UI.Create expects a type (\"Frame\", \"Text\", \"Button\", \"Image\" or \"TextBox\"), got a " .. type(elementType) .. " value", 2)
            end
            local options
            if type(name) == "table" then
              if parent ~= nil then
                error("UI.Create expects a type and an options table, or a type, name and parent", 2)
              end
              options = name
              for key in next, options do
                if not createOptions[key] then
                  error("UI.Create: " .. tostring(key) .. " is not an option (" .. createOptionNames .. ")", 2)
                end
              end
              name, parent = options.Name, options.Parent
            end
            if name ~= nil and type(name) ~= "string" then
              error("UI.Create expects a name (a string) or nil, got a " .. type(name) .. " value", 2)
            end
            local parentOf = 0
            if parent ~= nil then
              parentOf = parentId(parent, "UI.Create")
            end
            local id, createdType, createdName = hostGui("create", elementType, name, parentOf)
            if not id then
              error(createdType, 2)
            end
            local element = wrap(id, createdType, createdName)
            if options == nil then
              return element
            end

            -- Every option as if the script set it; a failure takes the half-made element away again.
            local ok, message = pcall(function()
              if options.Position ~= nil then
                element:SetPosition(numbersOption(options, "Position", 4))
              end
              if options.Size ~= nil then
                element:SetSize(numbersOption(options, "Size", 4))
              end
              if options.Anchor ~= nil then
                element:SetAnchor(numbersOption(options, "Anchor", 2))
              end
              for _, key in ipairs({ "MaxLength", "Text", "TextColor", "Color", "FontSize", "Visible", "ZOrder", "Opacity", "Image", "Enabled",
                "HorizontalAlignment", "VerticalAlignment", "PlaceholderText", "ClearOnSubmit" }) do
                if options[key] ~= nil then
                  element[key] = options[key]
                end
              end
            end)
            if not ok then
              hostGui("destroy", id)
              error("UI.Create: " .. tostring(message):gsub("^[^:]*:%d+: ", ""), 2)
            end
            return element
          end

          -- The game view the GUI is laid out in, in window pixels (as Input.mouseX / Camera.WorldToScreen): x, y,
          -- width, height. An element at Offset (sx - x, sy - y) from the Canvas sits at window point (sx, sy).
          local function getCanvasRect()
            return hostGui("canvas")
          end

          -- How wide text is at a font size (8 to 72) in the game GUI's font, and one line's height, in pixels: for
          -- wrapping and fitting text (the default chat wraps its lines with it).
          local function measureText(text, fontSize)
            if rawequal(text, ui) then
              error("call it with a dot: UI.MeasureText(\"Hello\", 20)", 2)
            end
            if type(text) == "number" then
              text = tostring(text)
            end
            if type(text) ~= "string" then
              error("UI.MeasureText expects text (a string), got a " .. type(text) .. " value", 2)
            end
            if fontSize == nil then
              fontSize = 20
            elseif not isFinite(fontSize) then
              error("UI.MeasureText expects a font size (a number, 8 to 72), got a " .. type(fontSize) .. " value", 2)
            end
            local width, height = hostGui("measure", text, fontSize)
            if width == nil then
              return 0, 0
            end
            return width, height
          end

          local members = { Find = find, FindById = findById, FindAll = findAll, FindPath = findPath, Create = create, GetCanvasRect = getCanvasRect,
            MeasureText = measureText }
          ui = setmetatable({}, {
            __index = function(_, key)
              if key == "Root" then
                return root
              end
              local member = members[key]
              if member == nil then
                invalidMember("UI", key)
              end
              return member
            end,
            __newindex = function() error("UI is read-only", 2) end,
            __tostring = function() return "UI" end,
            __metatable = "UI",
          })
          return ui, wrap
        end

        -- Timer: callbacks in game time (Time.elapsedTime), run by the host between frames (never inside the call that
        -- made them), owned by this script. A handle holds only its timer's number: Active, Remaining and Paused ask the
        -- host; Cancel, Pause, Resume and Restart tell it. Arguments after the callback are passed to it each time it runs.
        local function newTimer()
          local timer

          local function newHandle(id)
            local handle
            local function method(name, request)
              return function(self)
                if not rawequal(self, handle) then
                  error("call it with a colon: handle:" .. name .. "()", 2)
                end
                return (hostTimer(request, id))
              end
            end
            local methods = {
              Cancel = method("Cancel", "cancel"),
              Pause = method("Pause", "pause"),
              Resume = method("Resume", "resume"),
              -- A full delay (After) or interval (Every) from now, running again; false when the timer has ended.
              Restart = method("Restart", "restart"),
            }
            local values = { Active = "active", Remaining = "remaining", Paused = "paused" }
            handle = setmetatable({}, {
              __index = function(_, key)
                local request = values[key]
                if request ~= nil then
                  return hostTimer(request, id)
                end
                local member = methods[key]
                if member ~= nil then
                  return member
                end
                invalidMember("TimerHandle", key)
              end,
              __newindex = function(_, key)
                if values[key] ~= nil then
                  error(key .. " is read-only", 2)
                end
                if methods[key] ~= nil then
                  error(key .. " is a method and cannot be replaced", 2)
                end
                invalidMember("TimerHandle", key)
              end,
              __tostring = function()
                return "TimerHandle(" .. (hostTimer("active", id) and (hostTimer("paused", id) and "paused" or "active") or "inactive") .. ")"
              end,
              __metatable = "TimerHandle",
            })
            return handle
          end

          local function start(kind, functionName, seconds, callback, ...)
            if rawequal(seconds, timer) then
              error("call it with a dot: Timer." .. functionName .. "(1, function() ... end)", 3)
            end
            if kind == "after" then
              if not isFinite(seconds) or seconds < 0 then
                error("Timer.After expects a delay in seconds (a finite number, 0 or more)", 3)
              end
            elseif not isFinite(seconds) or seconds <= 0 then
              error("Timer.Every expects an interval in seconds (a finite number greater than 0)", 3)
            end
            if type(callback) ~= "function" then
              error("Timer." .. functionName .. " expects a function, got a " .. type(callback) .. " value", 3)
            end
            local run = callback
            if select("#", ...) > 0 then
              -- The extra arguments, kept as they are now; a tail call keeps the callback's errors its own.
              local arguments = pack(...)
              run = function()
                return callback(unpack(arguments, 1, arguments.n))
              end
            end
            local id, message = hostTimer(kind, seconds, run)
            if not id then
              error(message, 3)
            end
            return newHandle(id)
          end

          -- Not tail calls: start's errors point at the script's line (level 3).
          local members = {
            After = function(seconds, callback, ...)
              local handle = start("after", "After", seconds, callback, ...)
              return handle
            end,
            Every = function(seconds, callback, ...)
              local handle = start("every", "Every", seconds, callback, ...)
              return handle
            end,
          }
          timer = setmetatable({}, {
            __index = function(_, key)
              local member = members[key]
              if member == nil then
                invalidMember("Timer", key)
              end
              return member
            end,
            __newindex = function() error("Timer is read-only", 2) end,
            __tostring = function() return "Timer" end,
            __metatable = "Timer",
          })
          return timer
        end

        -- Physics: questions about the running physics world. A result is a locked table of plain values: an empty
        -- proxy whose values live in a weak-keyed side table only this chunk can reach, behind shared metatables (a
        -- raycast that hits allocates a few small tables, no closures).
        local raycastMembers = { Object = true, IsEnvironment = true, Distance = true, Point = true, Normal = true }
        local resultValues = setmetatable({}, { __mode = "k" })
        local vectorValues = setmetatable({}, { __mode = "k" })

        local readOnlyVectorMetatable = withArithmetic({
          __index = function(proxy, axis)
            local values = vectorValues[proxy]
            if axis == "x" then
              return values[2]
            elseif axis == "y" then
              return values[3]
            elseif axis == "z" then
              return values[4]
            end
            invalidMember(values[1], axis)
          end,
          __newindex = function(proxy) error(vectorValues[proxy][1] .. " is read-only", 2) end,
          __tostring = function(proxy)
            local values = vectorValues[proxy]
            return format("%s(%g, %g, %g)", values[1], values[2], values[3], values[4])
          end,
          __metatable = "Vector3",
        })

        local function readOnlyVector(label, x, y, z)
          local proxy = setmetatable({}, readOnlyVectorMetatable)
          vectorValues[proxy] = { label, x, y, z }
          return proxy
        end

        -- A result keeps its numbers; its Point and Normal vectors are made when first read (most scripts only look at
        -- Object or Distance, and every table a raycast makes is garbage to collect).
        local raycastResultMetatable = {
          __index = function(proxy, key)
            if not raycastMembers[key] then
              invalidMember("RaycastResult", key)
            end
            local values = resultValues[proxy]
            local value = values[key]
            if value == nil and (key == "Point" or key == "Normal") then
              local offset = key == "Point" and 0 or 3
              value = readOnlyVector(key, values[offset + 1], values[offset + 2], values[offset + 3])
              values[key] = value
            end
            return value
          end,
          __newindex = function(_, key)
            if raycastMembers[key] then
              error("RaycastResult is read-only", 2)
            end
            invalidMember("RaycastResult", key)
          end,
          __tostring = function(proxy)
            local values = resultValues[proxy]
            return format("RaycastResult(%s, %g)", values.Object ~= nil and tostring(values.Object) or "environment", values.Distance)
          end,
          __metatable = "RaycastResult",
        }

        raycastResult = function(wrap, objectId, distance, px, py, pz, nx, ny, nz)
          local result = setmetatable({}, raycastResultMetatable)
          resultValues[result] = {
            px, py, pz, nx, ny, nz,
            Object = objectId ~= 0 and wrap(objectId) or nil,
            IsEnvironment = objectId == 0,
            Distance = distance,
          }
          return result
        end

        -- A collision / trigger function's second argument: where the contact is, the normal toward the receiving object
        -- (nil for a trigger: an overlap has no surface), the receiver's velocity relative to the other side there, and
        -- whether the other side is the environment. Read-only; its vectors are made when first read.
        local contactMembers = { Point = true, Normal = true, RelativeVelocity = true, IsEnvironment = true }
        local contactMetatable = {
          __index = function(proxy, key)
            if not contactMembers[key] then
              invalidMember("Contact", key)
            end
            local values = resultValues[proxy]
            if key == "IsEnvironment" then
              return values[10]
            end
            local cached = values[key]
            if cached ~= nil then
              return cached
            end
            local offset = key == "Point" and 0 or key == "Normal" and 3 or 6
            if values[offset + 1] == nil then
              return nil
            end
            cached = readOnlyVector(key, values[offset + 1], values[offset + 2], values[offset + 3])
            values[key] = cached
            return cached
          end,
          __newindex = function(_, key)
            if contactMembers[key] then
              error("Contact is read-only", 2)
            end
            invalidMember("Contact", key)
          end,
          __tostring = function(proxy)
            local values = resultValues[proxy]
            return format("Contact(%g, %g, %g)", values[1], values[2], values[3])
          end,
          __metatable = "Contact",
        }

        local function newContact(px, py, pz, nx, ny, nz, vx, vy, vz, isEnvironment)
          local contact = setmetatable({}, contactMetatable)
          resultValues[contact] = { px, py, pz, nx, ny, nz, vx, vy, vz, isEnvironment }
          return contact
        end

        local function newPhysics(wrap)
          local physics

          -- A query's last argument: nil; a scene object to ignore; or an options table: Ignore (an object or a list of
          -- them), IgnoreTags / OnlyTags (a tag or a list of tags: skip objects with any of them / only objects with one
          -- of them), IgnoreTriggers, IgnoreEnvironment (booleans). Objects listed directly ({a, b}) are ignored too.
          local filterOptions = { Ignore = true, IgnoreTags = true, OnlyTags = true, IgnoreTriggers = true, IgnoreEnvironment = true,
            CollisionGroup = true }
          local filterOptionNames = "Ignore, IgnoreTags, OnlyTags, IgnoreTriggers, IgnoreEnvironment, CollisionGroup"

          local function isFilter(value)
            return objectIds[value] ~= nil
              or (type(value) == "table" and getmetatable(value) == nil and vectorParts(value) == nil)
          end

          local function filterOf(functionName, value)
            if value == nil then
              return nil
            end
            local single = objectIds[value]
            if single ~= nil then
              return single
            end
            if type(value) ~= "table" or getmetatable(value) ~= nil then
              error(functionName .. " expects its last argument to be a scene object to ignore or an options table "
                .. "(" .. filterOptionNames .. "), got a " .. type(value) .. " value", 3)
            end
            local ids = {}
            local function addObject(object, where)
              local id = objectIds[object]
              if id == nil then
                error(functionName .. ": " .. where .. " must be a scene object, got a " .. type(object) .. " value", 4)
              end
              ids[#ids + 1] = id
            end
            local function tagList(key, tags)
              if tags == nil then
                return nil
              end
              if type(tags) == "string" then
                return { tags }
              end
              if type(tags) ~= "table" then
                error(functionName .. ": " .. key .. " must be a tag (a string) or a list of tags", 4)
              end
              local list = {}
              for i = 1, rawlen(tags) do
                local tag = rawget(tags, i)
                if type(tag) ~= "string" then
                  error(functionName .. ": " .. key .. " must be a list of strings", 4)
                end
                list[i] = tag
              end
              return list
            end
            for key, item in next, value do
              if mathType(key) == "integer" then
                addObject(item, "Ignore list entry " .. key)
              elseif not filterOptions[key] then
                error(functionName .. ": " .. tostring(key) .. " is not an option (" .. filterOptionNames .. ")", 3)
              elseif (key == "IgnoreTriggers" or key == "IgnoreEnvironment") and type(item) ~= "boolean" then
                error(functionName .. ": " .. key .. " must be true or false, got a " .. type(item) .. " value", 3)
              elseif key == "CollisionGroup" and (type(item) ~= "string" or item == "") then
                error(functionName .. ": CollisionGroup must be a group name (a string), got a " .. type(item) .. " value", 3)
              end
            end
            local ignore = value.Ignore
            if ignore ~= nil then
              if objectIds[ignore] ~= nil then
                addObject(ignore, "Ignore")
              elseif type(ignore) == "table" and getmetatable(ignore) == nil then
                for i = 1, rawlen(ignore) do
                  addObject(rawget(ignore, i), "Ignore[" .. i .. "]")
                end
              else
                error(functionName .. ": Ignore must be a scene object or a list of them, got a " .. type(ignore) .. " value", 3)
              end
            end
            return {
              ids = ids,
              ignoreTags = tagList("IgnoreTags", value.IgnoreTags),
              onlyTags = tagList("OnlyTags", value.OnlyTags),
              ignoreTriggers = value.IgnoreTriggers == true,
              ignoreEnvironment = value.IgnoreEnvironment == true,
              collisionGroup = value.CollisionGroup,
            }
          end

          local function checkGroup(functionName, name, position)
            if type(name) ~= "string" or name == "" or #name > 64 then
              error("Physics." .. functionName .. " expects a collision group name (a string of 1 to 64 characters) as argument #"
                .. position .. ", got a " .. type(name) .. " value", 3)
            end
          end

          -- Whether objects of two collision groups touch (and trigger each other). Every group touches every group
          -- until told otherwise; a name used for the first time makes the group (at most 32, Default included).
          local function setGroupsCollide(a, b, collide)
            if rawequal(a, physics) then
              error("call it with a dot: Physics.SetGroupsCollide(\"A\", \"B\", false)", 2)
            end
            checkGroup("SetGroupsCollide", a, 1)
            checkGroup("SetGroupsCollide", b, 2)
            if type(collide) ~= "boolean" then
              error("Physics.SetGroupsCollide expects collide to be true or false, got a " .. type(collide) .. " value", 2)
            end
            local ok, message = hostPhysics("groupset", a, b, collide)
            if not ok then
              error(message, 2)
            end
          end

          local function groupsCollide(a, b)
            if rawequal(a, physics) then
              error("call it with a dot: Physics.GroupsCollide(\"A\", \"B\")", 2)
            end
            checkGroup("GroupsCollide", a, 1)
            checkGroup("GroupsCollide", b, 2)
            return hostPhysics("groupget", a, b)
          end

          local function getCollisionGroups()
            return hostPhysics("groups")
          end

          -- Joints: Physics.CreateWeld(a, b), CreateHinge(a, b, point, axis), CreateBallSocket(a, b, point); b may be
          -- nil (a fixed point in the world). The Joint wrapper holds its number; every read and write asks the host.
          local jointProperties = { Type = true, Active = true, Destroyed = true, Enabled = true, CollideConnected = true,
            PartA = true, PartB = true, Angle = true, MotorEnabled = true, MotorSpeed = true, MotorMaxTorque = true,
            LimitsEnabled = true, LowerAngle = true, UpperAngle = true }

          local function newJoint(jointId)
            local joint
            local function destroy(self)
              if not rawequal(self, joint) then
                error("call it with a colon: joint:Destroy()", 2)
              end
              hostPhysics("jdestroy", jointId)
            end
            joint = setmetatable({}, {
              __index = function(_, key)
                if key == "Destroy" then
                  return destroy
                end
                if not jointProperties[key] then
                  invalidMember("Joint", key)
                end
                local ok, value = hostPhysics("jget", jointId, key)
                if not ok then
                  error(value, 2)
                end
                if (key == "PartA" or key == "PartB") and value ~= nil then
                  return wrap(value)
                end
                return value
              end,
              __newindex = function(_, key, value)
                if key == "Destroy" then
                  error("Destroy is a method and cannot be replaced", 2)
                end
                if not jointProperties[key] then
                  invalidMember("Joint", key)
                end
                if type(value) == "number" and not isFinite(value) then
                  error("Joint." .. key .. " must be a finite number", 2)
                end
                local ok, message = hostPhysics("jset", jointId, key, value)
                if not ok then
                  error(message, 2)
                end
              end,
              __tostring = function()
                local ok, kind = hostPhysics("jget", jointId, "Type")
                return "Joint(" .. (ok and kind or "?") .. ")"
              end,
              __metatable = "Joint",
            })
            return joint
          end

          local function jointSide(functionName, value, position, optional)
            if value == nil and optional then
              return 0
            end
            local sideId = objectIds[value]
            if sideId == nil then
              error("Physics." .. functionName .. " expects a scene object" .. (optional and " or nil (the world)" or "")
                .. " as argument #" .. position .. ", got a " .. type(value) .. " value", 3)
            end
            return sideId
          end

          local function vectorArgument(functionName, value, what)
            local x, y, z = vectorParts(value)
            if x == nil or not (isFinite(x) and isFinite(y) and isFinite(z)) then
              error("Physics." .. functionName .. " expects " .. what .. " (a vector with finite x, y, z)", 3)
            end
            return x, y, z
          end

          local function makeJoint(kind, a, b, px, py, pz, ax, ay, az)
            local jointId, message = hostPhysics("joint", kind, a, b, px, py, pz, ax, ay, az)
            if not jointId then
              error(message, 3)
            end
            return newJoint(jointId)
          end

          local function createWeld(a, b)
            if rawequal(a, physics) then
              error("call it with a dot: Physics.CreateWeld(a, b)", 2)
            end
            local aId, bId = jointSide("CreateWeld", a, 1, false), jointSide("CreateWeld", b, 2, true)
            return makeJoint("Weld", aId, bId, 0, 0, 0, 0, 1, 0)
          end

          local function createHinge(a, b, point, axis)
            if rawequal(a, physics) then
              error("call it with a dot: Physics.CreateHinge(a, b, point, axis)", 2)
            end
            local aId, bId = jointSide("CreateHinge", a, 1, false), jointSide("CreateHinge", b, 2, true)
            local px, py, pz = vectorArgument("CreateHinge", point, "a world point")
            local ax, ay, az = vectorArgument("CreateHinge", axis, "an axis")
            if ax == 0 and ay == 0 and az == 0 then
              error("Physics.CreateHinge expects an axis that is not zero", 2)
            end
            return makeJoint("Hinge", aId, bId, px, py, pz, ax, ay, az)
          end

          local function createBallSocket(a, b, point)
            if rawequal(a, physics) then
              error("call it with a dot: Physics.CreateBallSocket(a, b, point)", 2)
            end
            local aId, bId = jointSide("CreateBallSocket", a, 1, false), jointSide("CreateBallSocket", b, 2, true)
            local px, py, pz = vectorArgument("CreateBallSocket", point, "a world point")
            return makeJoint("BallSocket", aId, bId, px, py, pz, 0, 1, 0)
          end

          -- An origin and a direction: six numbers as they are (the common case, kept cheap), or vectors in their place
          -- spread into their x, y, z; the arguments after them follow.
          local function spreadLine(...)
            local ox, oy, oz, dx, dy, dz = ...
            if type(ox) == "number" and type(oy) == "number" and type(oz) == "number" and type(dx) == "number"
              and type(dy) == "number" and type(dz) == "number" then
              return ...
            end
            local numbers, nextIndex = takeNumbers(6, 1, ...)
            if numbers == nil then
              return ...
            end
            local count = 6
            for i = nextIndex, select("#", ...) do
              count = count + 1
              numbers[count] = (select(i, ...))
            end
            return unpack(numbers, 1, count)
          end

          local function checkLine(functionName, usage, ox, oy, oz, dx, dy, dz)
            if rawequal(ox, physics) then
              error("call it with a dot: Physics." .. functionName .. usage, 3)
            end
            if not (isFinite(ox) and isFinite(oy) and isFinite(oz) and isFinite(dx) and isFinite(dy) and isFinite(dz)) then
              error("Physics." .. functionName .. " expects an origin (x, y, z) and a direction (dx, dy, dz): six finite numbers", 3)
            end
            if dx == 0 and dy == 0 and dz == 0 then
              error("Physics." .. functionName .. " expects a direction that is not zero", 3)
            end
          end

          local function maxDistanceOf(functionName, maxDistance)
            if not isFinite(maxDistance) or maxDistance <= 0 then
              error("Physics." .. functionName .. " expects maxDistance to be a finite number greater than 0", 3)
            end
          end

          local function raycast(...)
            local ox, oy, oz, dx, dy, dz, maxDistance, ignore = spreadLine(...)
            checkLine("Raycast", "(x, y, z, dx, dy, dz, maxDistance)", ox, oy, oz, dx, dy, dz)
            maxDistanceOf("Raycast", maxDistance)
            local ok, objectId, distance, px, py, pz, nx, ny, nz = hostPhysics("raycast", ox, oy, oz, dx, dy, dz, maxDistance,
              filterOf("Physics.Raycast", ignore))
            if ok == nil then
              error(objectId, 2)
            end
            if not ok then
              return nil
            end
            return raycastResult(wrap, objectId, distance, px, py, pz, nx, ny, nz)
          end

          -- Every hit along the ray, nearest first (each object once; at most 1000), as RaycastResults.
          local function raycastAll(...)
            local ox, oy, oz, dx, dy, dz, maxDistance, ignore = spreadLine(...)
            checkLine("RaycastAll", "(x, y, z, dx, dy, dz, maxDistance)", ox, oy, oz, dx, dy, dz)
            maxDistanceOf("RaycastAll", maxDistance)
            local hits, message = hostPhysics("raycastall", ox, oy, oz, dx, dy, dz, maxDistance, filterOf("Physics.RaycastAll", ignore))
            if hits == nil then
              error(message, 2)
            end
            local results = {}
            for i = 1, #hits, 8 do
              results[#results + 1] = raycastResult(wrap, hits[i], hits[i + 1], hits[i + 2], hits[i + 3], hits[i + 4],
                hits[i + 5], hits[i + 6], hits[i + 7])
            end
            return results
          end

          -- The first thing a sphere meets moving along the line (Distance: how far its centre moved; Point: where it
          -- touches; Normal: the surface there). Something it already overlaps at the start is not reported.
          local function sphereCast(...)
            local ox, oy, oz, dx, dy, dz, radius, maxDistance, ignore = spreadLine(...)
            checkLine("SphereCast", "(x, y, z, dx, dy, dz, radius, maxDistance)", ox, oy, oz, dx, dy, dz)
            if not isFinite(radius) or radius <= 0 then
              error("Physics.SphereCast expects radius to be a finite number greater than 0", 2)
            end
            maxDistanceOf("SphereCast", maxDistance)
            local ok, objectId, distance, px, py, pz, nx, ny, nz = hostPhysics("spherecast", ox, oy, oz, dx, dy, dz, radius, maxDistance,
              filterOf("Physics.SphereCast", ignore))
            if ok == nil then
              error(objectId, 2)
            end
            if not ok then
              return nil
            end
            return raycastResult(wrap, objectId, distance, px, py, pz, nx, ny, nz)
          end

          local function overlapResult(ids, environment)
            local result = {}
            for i = 1, #ids do
              result[i] = wrap(ids[i])
            end
            return result, environment
          end

          -- The objects a sphere overlaps (their exact shapes; scene order; at most 1000), and whether it reaches the
          -- environment (the baseplate).
          local function overlapSphere(...)
            if rawequal((...), physics) then
              error("call it with a dot: Physics.OverlapSphere(x, y, z, radius)", 2)
            end
            local numbers, nextIndex = takeNumbers(3, 1, ...)
            local radius, ignore = select(nextIndex or 1, ...)
            if numbers == nil or not (isFinite(numbers[1]) and isFinite(numbers[2]) and isFinite(numbers[3])) then
              error("Physics.OverlapSphere expects a centre (x, y, z) and a radius", 2)
            end
            if not isFinite(radius) or radius <= 0 then
              error("Physics.OverlapSphere expects radius to be a finite number greater than 0", 2)
            end
            return overlapResult(hostPhysics("overlapsphere", numbers[1], numbers[2], numbers[3], radius, filterOf("Physics.OverlapSphere", ignore)))
          end

          -- The same for a box: centre, size (full lengths), then optionally its rotation (Euler degrees, as a Part's)
          -- and ignoreObject.
          local function overlapBox(...)
            if rawequal((...), physics) then
              error("call it with a dot: Physics.OverlapBox(x, y, z, sizeX, sizeY, sizeZ)", 2)
            end
            local numbers, nextIndex = takeNumbers(6, 1, ...)
            if numbers == nil then
              error("Physics.OverlapBox expects a centre (x, y, z) and a size (x, y, z)", 2)
            end
            local rx, ry, rz, ignore = 0, 0, 0, nil
            local after = (select(nextIndex, ...))
            if after ~= nil and not isFilter(after) then
              local rotation, afterRotation = takeNumbers(3, nextIndex, ...)
              if rotation == nil then
                error("Physics.OverlapBox expects a rotation (x, y, z degrees) or ignoreObject after the size", 2)
              end
              rx, ry, rz = rotation[1], rotation[2], rotation[3]
              ignore = (select(afterRotation, ...))
            else
              ignore = after
            end
            for i = 1, 6 do
              if not isFinite(numbers[i]) or (i > 3 and numbers[i] <= 0) then
                error("Physics.OverlapBox expects a finite centre and a size greater than 0 on every axis", 2)
              end
            end
            if not (isFinite(rx) and isFinite(ry) and isFinite(rz)) then
              error("Physics.OverlapBox expects a finite rotation", 2)
            end
            return overlapResult(hostPhysics("overlapbox", numbers[1], numbers[2], numbers[3], numbers[4], numbers[5], numbers[6],
              rx, ry, rz, filterOf("Physics.OverlapBox", ignore)))
          end

          local members = { Raycast = raycast, RaycastAll = raycastAll, SphereCast = sphereCast, OverlapSphere = overlapSphere, OverlapBox = overlapBox,
            SetGroupsCollide = setGroupsCollide, GroupsCollide = groupsCollide, GetCollisionGroups = getCollisionGroups,
            CreateWeld = createWeld, CreateHinge = createHinge, CreateBallSocket = createBallSocket }
          physics = setmetatable({}, {
            __index = function(_, key)
              if key == "Gravity" then
                return readOnlyVector("Gravity", hostPhysics("gravity"))
              end
              local member = members[key]
              if member == nil then
                invalidMember("Physics", key)
              end
              return member
            end,
            __newindex = function(_, key, value)
              if key == "Gravity" then
                -- The session's gravity (studs per second squared, every axis -1000 to 1000); Stop puts the project's back.
                local x, y, z = vectorParts(value)
                if x == nil or not (isFinite(x) and isFinite(y) and isFinite(z)) then
                  error("Physics.Gravity must be a vector (finite x, y, z), got a " .. type(value) .. " value", 2)
                end
                local ok, message = hostPhysics("setgravity", x, y, z)
                if not ok then
                  error(message, 2)
                end
                return
              end
              if members[key] ~= nil then
                error(key .. " is a function and cannot be replaced", 2)
              end
              error("Physics." .. tostring(key) .. " cannot be set (only Gravity can)", 2)
            end,
            __tostring = function() return "Physics" end,
            __metatable = "Physics",
          })
          return physics
        end

        -- coroutine: the standard library. A script's code itself runs on the host's runner coroutine, which plays the
        -- part of Lua's main thread: coroutine.running says so, it is not yieldable, and yielding there is the standard
        -- error. A script's coroutines run under the same instruction budget as the rest of it.
        local function newCoroutine()
          local library = copyLibrary(coroutine)
          library.running = function()
            local thread = coroutineRunning()
            return thread, thread == runner
          end
          library.isyieldable = function(...)
            local thread = ...
            if select("#", ...) == 0 then
              thread = coroutineRunning()
            end
            if thread == runner then
              return false
            end
            return coroutineIsYieldable(...)
          end
          library.yield = function(...)
            if coroutineRunning() == runner then
              error("attempt to yield from outside a coroutine", 2)
            end
            return coroutineYield(...)
          end
          return library
        end

        -- Standard "bad argument" errors, at the script's line: level 3 when raised by the library function itself.
        local function argumentError(position, functionName, expected, value, given, level)
          error(format("bad argument #%d to '%s' (%s expected, got %s)", position, functionName, expected,
            given and type(value) or "no value"), level or 3)
        end

        -- load: standard load for text chunks (binary chunks are refused, as with mode "t"). The chunk's environment is
        -- this script's, unless another table is given. A reader function is called until it returns nil or "" (in Lua,
        -- so the instruction budget covers it); its pieces are then compiled together.
        local function newLoad(env)
          return function(chunk, chunkname, mode, ...)
            if mode ~= nil then
              if type(mode) ~= "string" then
                argumentError(3, "load", "string", mode, true)
              end
              if not find(mode, "t", 1, true) then
                return nil, format("attempt to load a text chunk (mode is '%s')", mode)
              end
            end
            if chunkname ~= nil and type(chunkname) ~= "string" and type(chunkname) ~= "number" then
              argumentError(2, "load", "string", chunkname, true)
            end
            if type(chunk) == "function" then
              local reader, parts = chunk, {}
              local ok, failure = pcall(function()
                while true do
                  local piece = reader()
                  if piece == nil or piece == "" then
                    return
                  end
                  if type(piece) ~= "string" then
                    error("reader function must return a string", 0)
                  end
                  parts[#parts + 1] = piece
                end
              end)
              if not ok then
                return nil, failure
              end
              chunk = concat(parts)
              if chunkname == nil then
                chunkname = "=(load)"
              end
            elseif type(chunk) ~= "string" and type(chunk) ~= "number" then
              argumentError(1, "load", "string", chunk, chunk ~= nil)
            end
            if select("#", ...) > 0 then
              return nativeLoad(chunk, chunkname, "t", (...))
            end
            return nativeLoad(chunk, chunkname, "t", env)
          end
        end

        -- warn: standard Lua warnings (off until warn("@on")), shown in the Output as warnings.
        local function newWarn(scriptName)
          local enabled = false
          return function(...)
            local count = select("#", ...)
            if count == 0 then
              argumentError(1, "warn", "string", nil, false)
            end
            local parts = {}
            for i = 1, count do
              local piece = (select(i, ...))
              if type(piece) ~= "string" and type(piece) ~= "number" then
                argumentError(i, "warn", "string", piece, true)
              end
              parts[i] = tostring(piece)
            end
            if count == 1 and sub(parts[1], 1, 1) == "@" then
              if parts[1] == "@on" then
                enabled = true
              elseif parts[1] == "@off" then
                enabled = false
              end
              return
            end
            if enabled then
              local message = concat(parts)
              if #message > maxPrintLength then
                message = sub(message, 1, maxPrintLength) .. " ...[truncated]"
              end
              hostWrite(scriptName, "Lua warning: " .. message, true)
            end
          end
        end

        -- collectgarbage: reading the collector, and full or step collections (charged to the instruction budget by the
        -- host). The options that would change the collector every script and the engine share are refused.
        local function scriptCollectgarbage(option, argument)
          if option == nil then
            option = "collect"
          end
          if type(option) ~= "string" then
            argumentError(1, "collectgarbage", "string", option, true)
          end
          if option == "count" or option == "isrunning" then
            return nativeCollect(option)
          end
          if option == "collect" or option == "step" then
            if argument ~= nil and type(argument) ~= "number" then
              argumentError(2, "collectgarbage", "number", argument, true)
            end
            return hostGc(option, argument)
          end
          if option == "stop" or option == "restart" or option == "incremental" or option == "generational"
            or option == "setpause" or option == "setstepmul" then
            error(format("collectgarbage(\"%s\") is not available to scripts: the collector is shared by the whole game", option), 2)
          end
          error(format("bad argument #1 to 'collectgarbage' (invalid option '%s')", option), 2)
        end

        -- package / require: standard Lua's package system for pure-Lua modules, looked up in the project's
        -- Assets/Scripts folder ("." in package.path). Files are read by the host (hostModule), which only ever looks at
        -- .lua files inside that folder. Native modules are not offered (cpath is empty, there is no loadlib).
        local function replaceAll(text, old, new)
          if old == "" then
            return text
          end
          local parts, position = {}, 1
          while true do
            local first, last = find(text, old, position, true)
            if first == nil then
              break
            end
            parts[#parts + 1] = sub(text, position, first - 1)
            parts[#parts + 1] = new
            position = last + 1
          end
          parts[#parts + 1] = sub(text, position)
          return concat(parts)
        end

        -- A string argument (numbers convert, as in standard Lua), checked for the library function that called this.
        local function checkString(position, functionName, value, given)
          if type(value) == "number" then
            return tostring(value)
          end
          if type(value) ~= "string" then
            argumentError(position, functionName, "string", value, given, 4)
          end
          return value
        end

        local function searchpath(...)
          local count = select("#", ...)
          local name, path, separator, replacement = ...
          name = checkString(1, "searchpath", name, count >= 1)
          path = checkString(2, "searchpath", path, count >= 2)
          separator = separator == nil and "." or checkString(3, "searchpath", separator, true)
          replacement = replacement == nil and "/" or checkString(4, "searchpath", replacement, true)
          if separator ~= "" and find(name, separator, 1, true) then
            name = replaceAll(name, separator, replacement)
          end
          local candidates = replaceAll(path, "?", name)
          for filename in gmatch(candidates, "[^;]+") do
            if hostModule("exists", filename) then
              return filename
            end
          end
          return nil, "no file '" .. replaceAll(candidates, ";", "'\n\tno file '") .. "'"
        end

        -- Names the file searcher accepts: segments separated by "." or "/", none of them empty (so no "..", leading or
        -- trailing separator), and no backslash, drive colon or NUL. Anything else could only point outside the folder.
        local function isModuleName(name)
          if name == "" or find(name, "[\\:\0]") then
            return false
          end
          for segment in gmatch(name .. ".", "([^./]*)[./]") do
            if segment == "" then
              return false
            end
          end
          return true
        end

        local function newPackage(env)
          local loaded, preload, loading = {}, {}, {}
          local package = {
            loaded = loaded, preload = preload, path = "./?.lua;./?/init.lua", cpath = "",
            config = "/\n;\n?\n!\n-\n", searchpath = searchpath,
          }

          local function searchPreload(name)
            local found = preload[name]
            if found == nil then
              return "no field package.preload['" .. name .. "']"
            end
            return found, ":preload:"
          end

          local function searchLua(name)
            local path = package.path
            if type(path) ~= "string" then
              error("'package.path' must be a string", 0)
            end
            if not isModuleName(name) then
              return "no file for '" .. name .. "': modules are files inside Assets/Scripts, named like 'Utils' or "
                .. "'Combat.Damage' (no '..', absolute paths or drive letters)"
            end
            local filename, notFound = searchpath(name, path)
            if filename == nil then
              return notFound
            end
            local source, where = hostModule("read", filename)
            if source == nil then
              error(format("error loading module '%s' from file '%s':\n\t%s", name, filename, where), 0)
            end
            local chunk, problem = nativeLoad(source, "=" .. where, "t", env)
            if chunk == nil then
              error(format("error loading module '%s' from file '%s':\n\t%s", name, filename, problem), 0)
            end
            return chunk, filename
          end

          -- The optional utilities: require("Wisko.Dialogue") and the others come with the engine. A project file of
          -- the same name (Assets/Scripts/Wisko/Dialogue.lua) is found first, so a project can use its own copy.
          local function searchBuiltin(name)
            if sub(name, 1, 6) ~= "Wisko." then
              return "no built-in module '" .. name .. "' (Wisko's own modules are named 'Wisko.Something')"
            end
            local source, chunkName = hostModule("builtin", name)
            if source == nil then
              return "no built-in module '" .. name .. "' (Wisko has Wisko.Dialogue, Wisko.Quests, Wisko.Crafting, Wisko.Building, "
                .. "Wisko.Logic, Wisko.Race, Wisko.Equipment, Wisko.Weather, Wisko.DayNight, Wisko.Teleport, Wisko.Generate and Wisko.Signal)"
            end
            local chunk, problem = nativeLoad(source, "=" .. chunkName, "t", env)
            if chunk == nil then
              error(format("error loading built-in module '%s':\n\t%s", name, problem), 0)
            end
            return chunk, ":builtin:"
          end

          package.searchers = { searchPreload, searchLua, searchBuiltin }

          -- As standard require: the cached value when it is there (not false or nil); otherwise the first searcher
          -- that finds a loader, which runs with the name and the searcher's extra value; its result (true for nil) is
          -- cached. A module that requires a module still loading is an error rather than endless recursion.
          local function require(...)
            local name = checkString(1, "require", (...), select("#", ...) >= 1)
            local value = loaded[name]
            if value then
              return value
            end
            if loading[name] then
              error(format("circular require: module '%s' is still being loaded (it requires itself through another module)", name), 2)
            end
            local searchers = package.searchers
            if type(searchers) ~= "table" then
              error("'package.searchers' must be a table", 2)
            end
            local messages, loader, extra = {}, nil, nil
            local index = 1
            while true do
              local searcher = rawget(searchers, index)
              if searcher == nil then
                error(format("module '%s' not found:%s", name, concat(messages)), 2)
              end
              local found, data = searcher(name)
              if type(found) == "function" then
                loader, extra = found, data
                break
              elseif type(found) == "string" or type(found) == "number" then
                messages[#messages + 1] = "\n\t" .. found
              end
              index = index + 1
            end
            loading[name] = true
            local ok, result = pcall(loader, name, extra)
            loading[name] = nil
            if not ok then
              error(result, 0)
            end
            if result ~= nil then
              loaded[name] = result
            end
            if loaded[name] == nil then
              loaded[name] = true
            end
            return loaded[name], extra
          end

          return package, require
        end

        -- Events: named messages between scripts. Events.On(name, f) connects f (owned by this script, gone with it);
        -- Events.Emit(name, ...) queues the event: every connection of that name gets it at the start of the next frame
        -- (in emit order, then connection order), never inside the call that emitted it. Values travel as copies: nil,
        -- booleans, numbers, strings (at most 4096 characters), scene objects and GUI elements (each receiver gets its
        -- own wrapper), vectors, and plain tables of those (copied when emitted and again for every receiver: no two
        -- scripts ever share one; at most 16 levels deep and 10000 values, no cycles). Never functions or tables with
        -- metatables, which would let one script reach into another.
        local maxEventArguments, maxEventName, maxEventString = 16, 64, 4096
        local maxEventDepth, maxEventValues = 16, 10000

        -- Inside an event's table snapshot, a scene object, GUI element or vector is a box with this metatable (private
        -- to the prelude): { "o", id }, { "g", id, type, name } or { "v", x, y, z }. Every receiver unboxes its own.
        local eventBox = {}

        local function eventKeyLabel(key)
          if type(key) == "string" and find(key, "^[%a_][%w_]*$") then
            return "." .. key
          end
          if type(key) == "string" then
            return "[" .. format("%q", sub(key, 1, 40)) .. "]"
          end
          return "[" .. tostring(key) .. "]"
        end

        -- The plain-data copy of one value of an emitted table: the copy, or nil, where (a path from that value) and
        -- what is wrong. counter[1] counts every value copied for one Emit.
        local function snapshotEventValue(value, counter, open, depth)
          local kind = type(value)
          if kind == "boolean" or kind == "number" then
            return value
          end
          if kind == "string" then
            if #value > maxEventString then
              return nil, "", "is a string longer than " .. maxEventString .. " characters"
            end
            return value
          end
          if kind ~= "table" then
            return nil, "", "is a " .. kind .. " value (events carry nil, booleans, numbers, strings, vectors, scene objects, "
              .. "GUI elements and plain tables of those)"
          end
          local id = objectIds[value]
          if id ~= nil then
            return setmetatable({ "o", id }, eventBox)
          end
          local info = guiInfo[value]
          if info ~= nil then
            local elementId, elementType, elementName = info()
            return setmetatable({ "g", elementId, elementType, elementName }, eventBox)
          end
          local metatable = getmetatable(value)
          if metatable == "Vector3" then
            local x, y, z = vectorParts(value)
            return setmetatable({ "v", x, y, z }, eventBox)
          end
          if metatable ~= nil then
            return nil, "", "is a table with a metatable (" .. tostring(metatable) .. "); only plain tables travel"
          end
          if open[value] then
            return nil, "", "contains itself (a table cycle cannot travel)"
          end
          if depth >= maxEventDepth then
            return nil, "", "nests tables more than " .. maxEventDepth .. " levels deep"
          end
          open[value] = true
          local copy = {}
          for key, item in next, value do
            counter[1] = counter[1] + 1
            if counter[1] > maxEventValues then
              return nil, "", "holds more than " .. maxEventValues .. " values"
            end
            local keyKind = type(key)
            if keyKind == "string" then
              if #key > maxEventString then
                return nil, "", "has a key longer than " .. maxEventString .. " characters"
              end
            elseif keyKind ~= "number" and keyKind ~= "boolean" then
              return nil, "", "has a " .. keyKind .. " key (keys must be strings, numbers or booleans)"
            end
            local copied, path, problem = snapshotEventValue(item, counter, open, depth + 1)
            if copied == nil then
              return nil, eventKeyLabel(key) .. path, problem
            end
            copy[key] = copied
          end
          open[value] = nil
          return copy
        end

        local function newEvents()
          local events

          local function checkName(functionName, name)
            if type(name) ~= "string" then
              if rawequal(name, events) then
                error("call it with a dot: Events." .. functionName .. "(\"Name\", ...)", 3)
              end
              error("Events." .. functionName .. " expects an event name (a string), got a " .. type(name) .. " value", 3)
            end
            if #name == 0 or #name > maxEventName then
              error("Events." .. functionName .. " expects an event name of 1 to " .. maxEventName .. " characters", 3)
            end
          end

          local function on(name, callback)
            checkName("On", name)
            if type(callback) ~= "function" then
              error("Events.On expects a function, got a " .. type(callback) .. " value", 2)
            end
            local handlerId, message = hostEvents("on", name, callback)
            if not handlerId then
              error(message, 2)
            end
            return newConnection(hostEvents, handlerId)
          end

          local function emit(name, ...)
            checkName("Emit", name)
            local count = select("#", ...)
            if count > maxEventArguments then
              error("Events.Emit carries at most " .. maxEventArguments .. " values", 2)
            end
            -- Counted, not appended: a nil value keeps its place. counter: the values copied from tables so far.
            local tags, values, total, counter = {}, {}, 0, { 0 }
            for i = 1, count do
              local value = (select(i, ...))
              local kind = type(value)
              if kind == "nil" or kind == "boolean" or kind == "number" then
                tags[i] = "v"
                total = total + 1
                values[total] = value
              elseif kind == "string" then
                if #value > maxEventString then
                  error("Events.Emit: string values can be at most " .. maxEventString .. " characters (argument #" .. (i + 1) .. ")", 2)
                end
                tags[i] = "v"
                total = total + 1
                values[total] = value
              elseif objectIds[value] ~= nil then
                tags[i] = "o"
                total = total + 1
                values[total] = objectIds[value]
              elseif guiInfo[value] ~= nil then
                local id, elementType, elementName = guiInfo[value]()
                tags[i] = "g"
                values[total + 1], values[total + 2], values[total + 3] = id, elementType, elementName
                total = total + 3
              elseif kind == "table" then
                -- A vector or a plain table: its snapshot now (later changes by the sender do not travel).
                local snapshot, path, problem = snapshotEventValue(value, counter, {}, 0)
                if snapshot == nil then
                  error("Events.Emit: argument #" .. (i + 1) .. path .. " " .. problem, 2)
                end
                tags[i] = "t"
                total = total + 1
                values[total] = snapshot
              else
                error("Events.Emit can carry nil, booleans, numbers, strings, vectors, scene objects, GUI elements and plain "
                  .. "tables, not a " .. kind .. " value (argument #" .. (i + 1) .. ")", 2)
              end
            end
            local ok, message = hostEvents("emit", name, concat(tags), unpack(values, 1, total))
            if not ok then
              error(message, 2)
            end
          end

          local members = { On = on, Emit = emit }
          events = setmetatable({}, {
            __index = function(_, key)
              local member = members[key]
              if member == nil then
                invalidMember("Events", key)
              end
              return member
            end,
            __newindex = function() error("Events is read-only", 2) end,
            __tostring = function() return "Events" end,
            __metatable = "Events",
          })
          return events
        end

        -- Camera: the Play view's camera. Position (x, y, z) and Rotation (x: pitch -89 to 89, positive looks up; y: yaw in
        -- degrees, 0 looks along +X, 90 along +Z; z: always 0, the camera does not roll) are live vectors like a
        -- Transform's; FieldOfView is vertical degrees (10 to 120). Placing the camera (Position, Rotation, LookAt) takes it
        -- from the editor's fly controls until Stop, which puts it back. Screen points are window pixels from the
        -- top-left, as Input.mouseX / mouseY.
        local function newCamera()
          local camera

          local function cameraVector(key)
            return setmetatable({}, withArithmetic({
              __index = function(_, axis)
                if not axes[axis] then
                  invalidMember("Camera." .. key, axis)
                end
                local ok, x, y, z = hostView("cam", key)
                if not ok then
                  error(x, 2)
                end
                if axis == "x" then
                  return x
                elseif axis == "y" then
                  return y
                end
                return z
              end,
              __newindex = function(_, axis, value)
                if not axes[axis] then
                  invalidMember("Camera." .. key, axis)
                end
                if not isFinite(value) then
                  error("Camera." .. key .. "." .. axis .. " must be a finite number", 2)
                end
                local ok, x, y, z = hostView("cam", key)
                if not ok then
                  error(x, 2)
                end
                if axis == "x" then
                  x = value
                elseif axis == "y" then
                  y = value
                else
                  z = value
                end
                local done, message = hostView("camset", key, x, y, z)
                if not done then
                  error(message, 2)
                end
              end,
              __tostring = function()
                local ok, x, y, z = hostView("cam", key)
                if not ok then
                  return "Camera." .. key .. "(?)"
                end
                return format("%s(%g, %g, %g)", key, x, y, z)
              end,
              __metatable = "Vector3",
            }))
          end

          local views = { Position = cameraVector("Position"), Rotation = cameraVector("Rotation") }
          local readOnlyVectors = { Forward = true, Right = true, Up = true }
          local numbers = { FieldOfView = true, ViewportWidth = true, ViewportHeight = true }

          local function checkDot(functionName, first, usage)
            if rawequal(first, camera) then
              error("call it with a dot: Camera." .. functionName .. usage, 3)
            end
          end

          local members = {
            -- Turns the camera to look at a point (x, y, z or a vector).
            LookAt = function(...)
              checkDot("LookAt", (...), "(x, y, z)")
              local point = takeNumbers(3, 1, ...)
              if point == nil or not (isFinite(point[1]) and isFinite(point[2]) and isFinite(point[3])) then
                error("Camera.LookAt expects a point: three finite numbers or a vector", 2)
              end
              local ok, message = hostView("lookat", point[1], point[2], point[3])
              if not ok then
                error(message, 2)
              end
            end,
            -- Where a world point is drawn: x, y (window pixels) and whether it is on the screen (in front of the
            -- camera and inside the view). Behind the camera: 0, 0, false.
            WorldToScreen = function(...)
              checkDot("WorldToScreen", (...), "(x, y, z)")
              local point = takeNumbers(3, 1, ...)
              if point == nil or not (isFinite(point[1]) and isFinite(point[2]) and isFinite(point[3])) then
                error("Camera.WorldToScreen expects a point: three finite numbers or a vector", 2)
              end
              local ok, x, y, onScreen = hostView("screen", point[1], point[2], point[3])
              if not ok then
                error(x, 2)
              end
              return x, y, onScreen
            end,
            -- The ray from the camera through a screen point: its origin and its direction (length 1), as Vector3
            -- values (e.g. Physics.Raycast(origin, direction, 500)).
            ScreenPointToRay = function(x, y)
              checkDot("ScreenPointToRay", x, "(x, y)")
              if not (isFinite(x) and isFinite(y)) then
                error("Camera.ScreenPointToRay expects a screen point: two finite numbers (x, y)", 2)
              end
              local ok, ox, oy, oz, dx, dy, dz = hostView("ray", x, y)
              if not ok then
                error(ox, 2)
              end
              return newVector3(ox, oy, oz), newVector3(dx, dy, dz)
            end,
          }

          camera = setmetatable({}, {
            __index = function(_, key)
              local view = views[key]
              if view ~= nil then
                return view
              end
              local member = members[key]
              if member ~= nil then
                return member
              end
              if not (readOnlyVectors[key] or numbers[key]) then
                invalidMember("Camera", key)
              end
              local ok, x, y, z = hostView("cam", key)
              if not ok then
                error(x, 2)
              end
              if readOnlyVectors[key] then
                return readOnlyVector(key, x, y, z)
              end
              return x
            end,
            __newindex = function(_, key, value)
              if views[key] ~= nil then
                local x, y, z = vectorParts(value)
                if x == nil then
                  error("Camera." .. key .. " cannot be replaced by a " .. type(value) .. " value; give a vector or set its components", 2)
                end
                if not (isFinite(x) and isFinite(y) and isFinite(z)) then
                  error("Camera." .. key .. " must be finite", 2)
                end
                local ok, message = hostView("camset", key, x, y, z)
                if not ok then
                  error(message, 2)
                end
                return
              end
              if key == "Forward" then
                -- Turns the camera to look along a direction (as LookAt(Position + direction)).
                local x, y, z = vectorParts(value)
                if x == nil or not (isFinite(x) and isFinite(y) and isFinite(z)) or (x == 0 and y == 0 and z == 0) then
                  error("Camera.Forward must be a direction: a vector (finite x, y, z) that is not zero", 2)
                end
                local ok, message = hostView("camset", key, x, y, z)
                if not ok then
                  error(message, 2)
                end
                return
              end
              if key == "FieldOfView" then
                if not isFinite(value) then
                  error("Camera.FieldOfView must be a finite number (10 to 120 degrees)", 2)
                end
                local ok, message = hostView("camset", key, value, 0, 0)
                if not ok then
                  error(message, 2)
                end
                return
              end
              if readOnlyVectors[key] or numbers[key] then
                error("Camera." .. key .. " is read-only", 2)
              end
              if members[key] ~= nil then
                error(key .. " is a method and cannot be replaced", 2)
              end
              invalidMember("Camera", key)
            end,
            __tostring = function() return "Camera" end,
            __metatable = "Camera",
          })
          return camera
        end

        -- Atmosphere: the Play session's sky, sun, ambient light, fog and image settings (the editor's Atmosphere), read
        -- and written as the panel keeps them (numbers clamped to its ranges and steps). Stop puts the project's back.
        local atmosphereKinds = {
          Enabled = "boolean", FogEnabled = "boolean",
          SkyColor = "color", HorizonColor = "color", SunColor = "color", AmbientColor = "color", FogColor = "color",
          SkyIntensity = "number", SunIntensity = "number", SunElevation = "number", SunHeading = "number", SunDiscSize = "number",
          AmbientIntensity = "number", FogDensity = "number", FogStart = "number", FogEnd = "number", Haze = "number",
          Exposure = "number", Saturation = "number", Contrast = "number",
          GroundColor = "color", AmbientMode = "string", ReflectionIntensity = "number",
        }

        local function newAtmosphere()
          return setmetatable({}, {
            __index = function(_, key)
              if atmosphereKinds[key] == nil then
                invalidMember("Atmosphere", key)
              end
              local ok, value = hostView("atmo", key)
              if not ok then
                error(value, 2)
              end
              return value
            end,
            __newindex = function(_, key, value)
              local kind = atmosphereKinds[key]
              if kind == nil then
                invalidMember("Atmosphere", key)
              end
              if kind == "number" and not isFinite(value) then
                error("Atmosphere." .. key .. " must be a finite number", 2)
              end
              local ok, message = hostView("atmoset", key, value)
              if not ok then
                error(message, 2)
              end
            end,
            __tostring = function() return "Atmosphere" end,
            __metatable = "Atmosphere",
          })
        end


        -- Graphics: the Play session's rendering settings (the editor's Graphics settings): tone mapping, bloom, vignette, ambient
        -- occlusion, white balance, shadows and the quality level. Renderer is read-only. Stop puts the project's back.
        local graphicsKinds = {
          Renderer = "string", Quality = "string", ToneMapping = "string",
          Bloom = "boolean", BloomThreshold = "number", BloomIntensity = "number", BloomRadius = "number",
          Vignette = "boolean", VignetteIntensity = "number", AmbientOcclusion = "boolean", AmbientOcclusionIntensity = "number",
          Temperature = "number", Tint = "number", Shadows = "boolean", ShadowDistance = "number",
        }

        local function newGraphics()
          return setmetatable({}, {
            __index = function(_, key)
              if graphicsKinds[key] == nil then
                invalidMember("Graphics", key)
              end
              local ok, value = hostView("gfx", key)
              if not ok then
                error(value, 2)
              end
              return value
            end,
            __newindex = function(_, key, value)
              local kind = graphicsKinds[key]
              if kind == nil then
                invalidMember("Graphics", key)
              end
              if kind == "number" and not isFinite(value) then
                error("Graphics." .. key .. " must be a finite number", 2)
              end
              local ok, message = hostView("gfxset", key, value)
              if not ok then
                error(message, 2)
              end
            end,
            __tostring = function() return "Graphics" end,
            __metatable = "Graphics",
          })
        end

        -- Shared: one plain table for every script of the Play session (each starts with the same one; Stop clears it, as
        -- every Play session gets a new runtime). Deliberate shared state: a script's own globals stay its own, and only
        -- what a script puts in Shared is seen by the others, as it is (tables and functions included, not copies).
        local sharedTable = {}

        -- The gameplay prelude (LuaSandbox.Gameplay) gets these helpers once and gives back the extra
        -- object methods and the function that adds its globals (Navigation, Perception, Random, Noise, Path, GameTime,
        -- GameSave, Cameras, Particles, DebugDraw; Time.SetTimeScale and Time.OnUpdate) to every environment.
        gameplayObjectMethods, extendEnvironment = gameplaySetup({
          hostGameplay = hostGameplay, hostScene = hostScene, hostEvents = hostEvents,
          newVector3 = newVector3, vectorParts = vectorParts, isFinite = isFinite, invalidMember = invalidMember,
          objectIds = objectIds, newConnection = newConnection, timeValues = timeValues,
        })

        local function newEnvironment(scriptName, selfId)
          local stringLibrary = copyLibrary(string)
          local env = {
            assert = assert, error = error, ipairs = ipairs, next = next, pairs = pairs, pcall = pcall,
            select = select, tonumber = tonumber, tostring = tostring, type = type, xpcall = xpcall,
            rawequal = rawequal, rawget = rawget, rawlen = rawlen, rawset = rawset,
            getmetatable = getmetatable, setmetatable = scriptSetmetatable,
            collectgarbage = scriptCollectgarbage,
            _VERSION = version,
            math = newMath(),
            string = stringLibrary,
            table = copyLibrary(table),
            utf8 = copyLibrary(utf8),
            coroutine = newCoroutine(),
            os = { clock = osClock, date = osDate, difftime = osDifftime, time = osTime },
            debug = { traceback = traceback },
            Time = setmetatable({}, timeMetatable),
            Input = newInput(scriptName),
            Vector3 = copyLibrary(vector3Functions),
            Events = newEvents(),
            Camera = newCamera(),
            Atmosphere = newAtmosphere(),
            Graphics = newGraphics(),
            Shared = sharedTable,
          }
          local wrapElement
          env.UI, wrapElement = newUI()
          env.load = newLoad(env)
          env.warn = newWarn(scriptName)
          env.package, env.require = newPackage(env)

          -- All arguments, tab-separated like standard print, capped in length, handed to the engine log as one string.
          env.print = function(...)
            local count = select("#", ...)
            local parts = {}
            for i = 1, count do
              parts[i] = tostring((select(i, ...)))
            end
            local message = concat(parts, "\t")
            if #message > maxPrintLength then
              message = sub(message, 1, maxPrintLength) .. " ...[truncated]"
            end
            hostWrite(scriptName, message)
          end

          local wrapObject = newObjects()
          env.Scene = newScene(wrapObject)
          env.Timer = newTimer()
          env.Physics = newPhysics(wrapObject)
          extendEnvironment(env, wrapObject)
          if selfId ~= nil then
            env.self = wrapObject(selfId)
          end

          env._G = env

          -- What require("string") etc. give, as in standard Lua.
          local loaded = env.package.loaded
          loaded._G, loaded.package, loaded.coroutine, loaded.table = env, env.package, env.coroutine, env.table
          loaded.string, loaded.math, loaded.utf8, loaded.os, loaded.debug = stringLibrary, env.math, env.utf8, env.os, env.debug

          -- The host calls a script's event functions through this: f(other[, contact]), other being this script's own
          -- wrapper of object id (nil for none) and contact the contact's values when there is one. Tail calls, so f's
          -- errors and tracebacks are the script's own.
          local function deliver(f, id, px, py, pz, nx, ny, nz, vx, vy, vz, isEnvironment)
            if px == nil then
              return f(wrapObject(id))
            end
            return f(wrapObject(id), newContact(px, py, pz, nx, ny, nz, vx, vy, vz, isEnvironment))
          end

          -- An emitted table's snapshot, copied for this script: its own tables, vectors and wrappers.
          local function unboxEvent(value)
            if type(value) ~= "table" then
              return value
            end
            if rawequal(getmetatable(value), eventBox) then
              local kind = value[1]
              if kind == "o" then
                return wrapObject(value[2])
              elseif kind == "g" then
                return wrapElement(value[2], value[3], value[4])
              end
              return newVector3(value[2], value[3], value[4])
            end
            local copy = {}
            for key, item in next, value do
              copy[key] = unboxEvent(item)
            end
            return copy
          end

          -- Events.On handlers get their arguments through this: tags says what each one is ('v' a value, 'o' a scene
          -- object Id, 'g' a GUI element's Id, type and name, 't' a table's snapshot), made into this script's own
          -- wrappers and tables.
          local function deliverEvent(f, tags, ...)
            local arguments, index = {}, 1
            for i = 1, #tags do
              local tag = sub(tags, i, i)
              if tag == "o" then
                arguments[i] = wrapObject((select(index, ...)))
                index = index + 1
              elseif tag == "t" then
                arguments[i] = unboxEvent((select(index, ...)))
                index = index + 1
              elseif tag == "g" then
                local id, elementType, elementName = select(index, ...)
                arguments[i] = wrapElement(id, elementType, elementName)
                index = index + 3
              else
                arguments[i] = (select(index, ...))
                index = index + 1
              end
            end
            return f(unpack(arguments, 1, #tags))
          end

          return env, deliver, stringLibrary, deliverEvent
        end

        return newEnvironment, timeValues, inputValues, stringMetatable, stopThread
        """;
}
