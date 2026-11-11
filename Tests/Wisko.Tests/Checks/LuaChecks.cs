using System.Text;
using Wisko.Engine.Editor;
using Wisko.Engine.Gui;
using Wisko.Engine.Math;
using Wisko.Engine.Scripting;
using Wisko.Engine.ScriptEditor;
using Wisko.Engine.World;
using static Wisko.Tests.TestSupport;

namespace Wisko.Tests.Checks;

/// <summary>The Lua runtime (stock Lua 5.4 through NLua/KeraLua), its sandbox, the API catalog and the syntax validator.</summary>
internal static class LuaChecks
{
    public static void Sandbox(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "Proj");
        WriteScript(root, "Util.lua", "local M = {}\nfunction M.add(a, b) return a + b end\nreturn M\n");
        WriteScript(root, "Sub/Mod.lua", "return { name = \"submod\" }\n");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Escape.lua"), "return { escaped = true }\n");
        WriteScript(root, "Sandbox.lua", ChkFunction + """
            function Start()
              chk("version_5_4", _VERSION == "Lua 5.4")
              chk("no_io", io == nil)
              chk("no_os_execute", os.execute == nil)
              chk("no_os_remove", os.remove == nil)
              chk("no_loadfile", loadfile == nil)
              chk("no_dofile", dofile == nil)
              chk("debug_traceback_only", type(debug.traceback) == "function" and debug.getinfo == nil and debug.sethook == nil)
              chk("os_time", type(os.time()) == "number")
              local co = coroutine.create(function(a) local b = coroutine.yield(a + 1); return b * 2 end)
              local ok1, r1 = coroutine.resume(co, 1)
              local ok2, r2 = coroutine.resume(co, 10)
              chk("coroutines", ok1 and r1 == 2 and ok2 and r2 == 20)
              local U = require("Util")
              chk("require", U ~= nil and U.add(2, 3) == 5)
              chk("require_cached", require("Util") == U)
              chk("require_nested", require("Sub.Mod").name == "submod")
              chk("require_escape_blocked", not pcall(require, "..Escape") and not pcall(require, "../Escape") and not pcall(require, "..\\Escape"))
              chk("require_absolute_blocked", not pcall(require, "C:\\Windows\\win"))
              local f = load("return 6 * 7")
              chk("load", f ~= nil and f() == 42)
              chk("string_lib", string.format("%03d", 7) == "007" and ("abc"):upper() == "ABC")
              chk("utf8_lib", utf8.len("h\u{e9}llo") == 5)
              chk("integer_math", math.type(3) == "integer" and 7 // 2 == 3)
              chk("metatables", getmetatable(setmetatable({}, { __index = function() return 1 end })) ~= nil)
              local ok3, msg = pcall(error, "boom")
              chk("pcall_error", not ok3 and tostring(msg):find("boom") ~= nil)
              SHARED_FROM_SANDBOX = true
            end
            """);
        WriteScript(root, "Isolation.lua", ChkFunction + """
            local reported = false
            function Update(dt)
              if not reported then
                reported = true
                chk("globals_isolated_between_scripts", rawget(_G, "SHARED_FROM_SANDBOX") == nil)
              end
            end
            """);

        var workspace = new Workspace();
        workspace.AddPart(MakePart("SandboxPart", new Vector3(0, 5, 0), new Vector3(2, 2, 2), true, "Assets/Scripts/Sandbox.lua"));
        workspace.AddPart(MakePart("OtherPart", new Vector3(5, 5, 0), new Vector3(2, 2, 2), true, "Assets/Scripts/Isolation.lua"));
        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = StartPlay(root, workspace, null, log, faults);
        Step(play, 3, faults);
        play.Stop(workspace);
        play.Dispose();

        AssertNoFaults(report, "Lua sandbox", faults);
        AssertLuaChecks(report, "Lua sandbox", log,
        [
            "version_5_4", "no_io", "no_os_execute", "no_os_remove", "no_loadfile", "no_dofile", "debug_traceback_only", "os_time",
            "coroutines", "require", "require_cached", "require_nested", "require_escape_blocked", "require_absolute_blocked", "load",
            "string_lib", "utf8_lib", "integer_math", "metatables", "pcall_error", "globals_isolated_between_scripts"
        ]);
    }

    /// <summary>
    /// Every catalog global exists in a live script environment; every member of the catalog's tables is there
    /// (functions as functions), reached through the global or a representative instance. Keeps the editor's
    /// completion/help (ScriptApiCatalog) and the sandbox from drifting apart.
    /// </summary>
    public static void Catalog(TestReport report, string dir)
    {
        var root = Path.Combine(dir, "Proj");
        var lua = new StringBuilder(ChkFunction);
        lua.AppendLine("local function get(f) local ok, v = pcall(f) if ok then return v end return nil end");
        lua.AppendLine("function Start()");
        var expected = new List<string>();
        foreach (var global in ScriptApiCatalog.Globals)
        {
            var key = $"global.{global.Name}";
            expected.Add(key);
            lua.AppendLine($"  chk(\"{key}\", get(function() return {global.Name} end) ~= nil)");
        }

        // Global tables and the instances that expose the other member tables.
        var reach = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Scene"] = "Scene", ["ScriptObject"] = "self", ["Transform"] = "self.Transform", ["Vector3Library"] = "Vector3",
            ["Time"] = "Time", ["Input"] = "Input", ["UI"] = "UI", ["GuiRoot"] = "UI.Root", ["Timer"] = "Timer",
            ["Physics"] = "Physics", ["Events"] = "Events", ["Camera"] = "Camera", ["Atmosphere"] = "Atmosphere",
            ["math"] = "math", ["string"] = "string", ["table"] = "table", ["utf8"] = "utf8", ["coroutine"] = "coroutine",
            ["package"] = "package", ["os"] = "os", ["debug"] = "debug",
            ["TimerHandle"] = "HANDLE", ["Connection"] = "CONN", ["GuiElement"] = "ELEM_ANY", ["ScriptHandle"] = "SCRIPTH",
            ["Vector3"] = "Vector3.new(1, 2, 3)", ["Joint"] = "JOINT",
            // Lua V5.
            ["Navigation"] = "Navigation", ["NavAgent"] = "AGENT", ["Perception"] = "Perception", ["Sensor"] = "SENSOR",
            ["RandomLibrary"] = "Random", ["Random"] = "Random.new(1)", ["Noise"] = "Noise", ["PathLibrary"] = "Path", ["Path"] = "PATH",
            ["PathFollower"] = "FOLLOWER", ["GameTime"] = "GameTime", ["GameSave"] = "GameSave", ["Cameras"] = "Cameras", ["GameCamera"] = "CAM",
            ["CameraPlayback"] = "PLAYBACK", ["Particles"] = "Particles", ["ParticleEmitter"] = "EMITTER", ["DebugDraw"] = "DebugDraw",
            // First public release: chat.
            ["Chat"] = "Chat", ["ChatChannel"] = "Chat.GetChannel(\"General\")", ["ChatSender"] = "Chat.LocalPlayer",
        };
        lua.AppendLine("  AGENT = Navigation.CreateAgent(self)");
        lua.AppendLine("  SENSOR = Perception.CreateSensor(self)");
        lua.AppendLine("  PATH = Path.new({ Vector3.new(0, 0, 0), Vector3.new(1, 0, 0) })");
        lua.AppendLine("  FOLLOWER = Path.Follow(self, PATH)");
        lua.AppendLine("  CAM = Cameras.Create(\"CatalogCam\")");
        lua.AppendLine("  PLAYBACK = CAM:PlayPath(PATH)");
        lua.AppendLine("  EMITTER = Particles.CreateEmitter()");
        lua.AppendLine("  HANDLE = Timer.After(100, function() end)");
        lua.AppendLine("  CONN = Events.On(\"x\", function() end)");
        lua.AppendLine("  JOINT = Physics.CreateHinge(self, nil, self.Transform.Position, Vector3.new(0, 1, 0))");

        // GuiElement members are type-specific (Opacity: Frame; alignments: Text): each must exist on some element type.
        lua.AppendLine("  local elems = { UI.Create(\"Button\", \"CatalogButton\"), UI.Create(\"Text\", \"CatalogText\"), UI.Create(\"Frame\", \"CatalogFrame\"), UI.Create(\"Image\", \"CatalogImage\"), UI.Create(\"TextBox\", \"CatalogTextBox\") }");
        lua.AppendLine("  ELEM_ANY = setmetatable({}, { __index = function(_, k) for _, e in ipairs(elems) do local v = get(function() return e[k] end) if v ~= nil then return v end end return nil end })");
        lua.AppendLine("  SCRIPTH = self:AttachScript(\"Empty.lua\")");
        var unreached = new List<string>();
        foreach (var table in ScriptApiCatalog.MemberTables)
        {
            if (!reach.TryGetValue(table, out var expression))
            {
                unreached.Add(table);
                continue;
            }

            foreach (var member in ScriptApiCatalog.GetMembers(table))
            {
                var key = $"{table}.{member.Name}";
                expected.Add(key);
                // Mass (nil: Auto), Parent (nil: none), Light (nil: no light) and a world joint's PartB may be nil: those must read
                // without an error.
                lua.AppendLine(member.Kind == ScriptApiKind.Function
                    ? $"  chk(\"{key}\", type(get(function() return {expression}.{member.Name} end)) == \"function\")"
                    : key is "ScriptObject.Mass" or "ScriptObject.Parent" or "ScriptObject.Light" or "Joint.PartB" or "NavAgent.Destination" or "NavAgent.Target" or "NavAgent.FailureReason"
                        ? $"  chk(\"{key}\", (pcall(function() return {expression}.{member.Name} end)))"
                        : $"  chk(\"{key}\", get(function() return {expression}.{member.Name} end) ~= nil)");
            }
        }

        lua.AppendLine("end");
        WriteScript(root, "Catalog.lua", lua.ToString());
        WriteScript(root, "Empty.lua", "function Start() end\n");
        TestReport.Info($"catalog tables only reachable from a live event (not checked here): {string.Join(", ", unreached)}");

        var workspace = new Workspace();
        workspace.AddPart(MakePart("CatalogPart", new Vector3(0, 5, 0), new Vector3(2, 2, 2), false, "Assets/Scripts/Catalog.lua"));
        var log = new OutputLog();
        var faults = new List<PlayScriptFault>();
        var play = StartPlay(root, workspace, new GuiDocument(), log, faults);
        Step(play, 2, faults);
        play.Stop(workspace);
        play.Dispose();
        AssertNoFaults(report, "Catalog", faults);

        var seen = LuaReports(log);
        var missing = expected.Where(name => !seen.TryGetValue(name, out var v) || v != "true").ToList();
        report.Check($"Catalog: all {expected.Count} documented globals/members exist in the live sandbox", missing.Count == 0,
            missing.Count == 0 ? null : "missing/mismatched: " + string.Join(", ", missing));
    }

    public static void SyntaxValidator(TestReport report)
    {
        using var validator = new LuaSyntaxValidator();
        report.Check("Syntax validator: valid code passes", validator.Validate("local x = 1\nreturn x\n") is null);
        var line2 = validator.Validate("local a = 1\nlocal b = = 2\n");
        report.Check("Syntax validator: error reported on the second line (Line is zero-based by design)", line2 is { Line: 1 }, line2?.ToString() ?? "no diagnostic");
    }
}
