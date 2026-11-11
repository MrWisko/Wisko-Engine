using System.Text;
using Wisko.Engine.Math;
using Wisko.Engine.Projects;
using Wisko.Engine.World;

namespace Wisko.Tests.Checks;

/// <summary>
/// The first public release's showcase ("Editor and Chat Showcase"): crates and a platform to drag with any tool,
/// Parts with typed rotations, and a chat host whose script turns on the default chat window with a guide NPC, a
/// moderation filter and a News channel. The baseplate is there so it can be deleted by hand.
/// <c>Wisko.Tests --make-release-demo &lt;projects folder&gt;</c> writes it as an ordinary project and never replaces one.
/// </summary>
internal static class ReleaseShowcase
{
    public const string ProjectName = "Editor and Chat Showcase";

    public const string ChatScript = """
        -- Wisko release showcase: text chat. Local only: messages never leave this computer.
        -- Press / to type, Enter to send, Escape to stop typing (a second Escape stops Play).

        local function guide(text)
          Timer.After(0.6, function()
            Chat.Post(text, { SenderName = "Guide" })
          end)
        end

        function Start()
          Chat.EnableDefaultUI({ Placeholder = "Press / to chat (try hello, help or darn)" })

          -- A moderation hook: one word is rewritten, long shouting is held back.
          Chat.SetFilter(function(message)
            if message.Kind == "Player" and #message.Text >= 12 and message.Text == string.upper(message.Text)
              and string.find(message.Text, "%a") then
              return false, "too much shouting"
            end
            local cleaned, count = string.gsub(message.Text, "[Dd][Aa][Rr][Nn]", "****")
            if count > 0 then
              return cleaned
            end
          end)

          local news = Chat.CreateChannel("News", { PlayersCanSend = false })
          news:Post("Release showcase loaded.")
          Chat.Post("Welcome! This chat is local: messages never leave this computer.")
          Chat.Post("Press / to type, Enter to send, Escape to stop typing.")

          Chat.OnMessage(function(message)
            if message.Kind ~= "Player" then
              return
            end
            local text = string.lower(message.Text)
            if string.find(text, "hello", 1, true) or string.find(text, "^hi") then
              guide("Hello, " .. message.Sender.Name .. "! Stop Play and drag a crate with the Rotate tool.")
            elseif string.find(text, "help", 1, true) then
              guide("Delete the baseplate from the World panel, type rotations under it, or drag Parts with any tool.")
            end
          end)
        end
        """;

    /// <summary>Creates the showcase in <paramref name="projectsRoot"/>; refuses when a project of that name exists.</summary>
    public static int Make(string projectsRoot)
    {
        Directory.CreateDirectory(projectsRoot);
        var store = new ProjectStore(projectsRoot);
        if (store.ListProjects().Any(p => string.Equals(p.Name, ProjectName, StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"refused: a project named \"{ProjectName}\" already exists in {projectsRoot} (nothing changed)");
            return 1;
        }

        var workspace = new Workspace();
        foreach (var part in Build())
        {
            workspace.AddPart(part);
        }

        if (!store.TryCreateProject(ProjectName, WorldTemplate.Classic, workspace, out var project, out var error))
        {
            Console.Error.WriteLine($"could not create the project: {error}");
            return 1;
        }

        // As a world made in the editor: the Universal renderer at its defaults.
        if (!Engine.Graphics.GraphicsFile.TrySave(project.GraphicsPath, Engine.Graphics.GraphicsSettings.Default, out error))
        {
            Console.Error.WriteLine($"could not save the project's graphics settings: {error}");
            return 1;
        }

        var scripts = Path.Combine(project.FolderPath, "Assets", "Scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "Chat.lua"), ChatScript, new UTF8Encoding(false));
        Console.WriteLine($"created \"{project.Name}\" at {project.FolderPath}: {workspace.Parts.Count} parts");
        return 0;
    }

    public static List<Part> Build()
    {
        var parts = new List<Part>();
        Part Add(string name, PartGroup group, Vector3 position, Vector3 size, Vector3 color, Vector3? rotation = null, ShapeType shape = ShapeType.Block)
        {
            var part = new Part(name, position, size, color, rotation, anchored: true, shapeType: shape) { Group = group };
            parts.Add(part);
            return part;
        }

        // 1. Drag with any tool: crates on the ground, one on a platform, a ball in the air.
        var drag = new PartGroup("01 Drag with any tool");
        Add("Crate (drag me with Rotate active)", drag, new Vector3(-12f, 1f, 0f), new Vector3(2f, 2f, 2f), new Vector3(0.80f, 0.55f, 0.30f));
        Add("Crate (drag me with Resize active)", drag, new Vector3(-8f, 1.5f, 0f), new Vector3(3f, 3f, 3f), new Vector3(0.70f, 0.45f, 0.25f));
        Add("Platform", drag, new Vector3(-10f, 1f, -8f), new Vector3(8f, 2f, 6f), new Vector3(0.55f, 0.58f, 0.62f));
        Add("Crate on the platform", drag, new Vector3(-10f, 3f, -8f), new Vector3(2f, 2f, 2f), new Vector3(0.85f, 0.65f, 0.35f));
        Add("Ball in the air", drag, new Vector3(-14f, 6f, -4f), new Vector3(2f, 2f, 2f), new Vector3(0.95f, 0.35f, 0.35f), shape: ShapeType.Sphere);

        // 2. Typed rotation: values as typed in the World panel (Z, then X, then Y, about the world axes).
        var rotation = new PartGroup("02 Typed rotation");
        Add("Y 22.5", rotation, new Vector3(2f, 1f, 0f), new Vector3(2f, 2f, 4f), new Vector3(0.30f, 0.60f, 0.95f), new Vector3(0f, 22.5f, 0f));
        Add("Y -45 (shown as 315)", rotation, new Vector3(7f, 1f, 0f), new Vector3(2f, 2f, 4f), new Vector3(0.30f, 0.75f, 0.55f), new Vector3(0f, 315f, 0f));
        Add("X 30 Y 45 Z 10", rotation, new Vector3(12f, 2.5f, 0f), new Vector3(2f, 2f, 4f), new Vector3(0.85f, 0.75f, 0.30f), new Vector3(30f, 45f, 10f));
        Add("Wedge Z 90", rotation, new Vector3(17f, 1f, 0f), new Vector3(2f, 2f, 4f), new Vector3(0.65f, 0.40f, 0.85f), new Vector3(0f, 0f, 90f), ShapeType.Wedge);

        // 3. Chat: the host runs the chat script (default window, guide NPC, filter, News channel).
        var chat = new PartGroup("03 Chat");
        var host = Add("Chat Host", chat, new Vector3(0f, 1f, 10f), new Vector3(2f, 2f, 2f), new Vector3(0.25f, 0.55f, 0.95f));
        ScriptReference.TryCreate("Assets/Scripts/Chat.lua", out var reference);
        host.Scripts.Add(reference!);
        Add("Guide", chat, new Vector3(4f, 2f, 10f), new Vector3(2f, 4f, 2f), new Vector3(0.95f, 0.80f, 0.30f), shape: ShapeType.Cylinder);
        return parts;
    }
}
