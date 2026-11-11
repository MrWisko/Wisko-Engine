using Wisko.Engine.Animation;
using Wisko.Engine.Math;

namespace Wisko.Engine.World;

/// <summary>
/// One object in a world: a primitive shape or an imported model's instance. Data only; drawing is the renderers' job.
/// </summary>
public sealed class Part
{
    /// <summary>Friction a Part has unless set otherwise (and the environment's, e.g. the baseplate).</summary>
    public const float DefaultFriction = 0.6f;

    /// <summary>Smallest and largest manual mass (<see cref="MassOverride"/>).</summary>
    public const float MinMass = 0.001f;

    public const float MaxMass = 1_000_000f;

    /// <summary>Size and colour a new primitive Part gets (the editor's new Part, and a script's Scene.Create).</summary>
    public static readonly Vector3 DefaultSize = new(4f, 4f, 4f);

    public static readonly Vector3 DefaultColor = new(0.78f, 0.78f, 0.82f);

    private float _friction = DefaultFriction;
    private float? _massOverride;

    public Part(
        string name,
        Vector3 position,
        Vector3 size,
        Vector3 color,
        Vector3? rotation = null,
        bool anchored = true,
        ShapeType shapeType = ShapeType.Block)
    {
        Name = name;
        Position = position;
        Size = size;
        Color = color;
        Rotation = rotation ?? Vector3.Zero;
        Anchored = anchored;
        ShapeType = shapeType;
    }

    public string Name { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 Size { get; set; }
    public Vector3 Color { get; set; }
    public Vector3 Rotation { get; set; }
    public bool Anchored { get; set; }

    /// <summary>
    /// Editor protection, not physics: a locked Part is skipped by viewport click and box selection and refuses to be
    /// moved, resized, rotated or deleted (World Explorer can still select it, so it can be unlocked).
    /// </summary>
    public bool Locked { get; set; }

    /// <summary>Physics: gravity pulls this Part while it is dynamic (not anchored). No effect while anchored.</summary>
    public bool GravityEnabled { get; set; } = true;

    /// <summary>Physics: this Part makes contacts with other Parts and the environment. Off: everything passes through it.</summary>
    public bool CanCollide { get; set; } = true;

    /// <summary>
    /// Physics: a trigger detects overlaps with other Parts (scripts' OnTriggerEnter / Stay / Exit) but never pushes
    /// or stops anything, whatever <see cref="CanCollide"/> says. It never reports the environment (the baseplate).
    /// </summary>
    public bool IsTrigger { get; set; }

    /// <summary>Physics: surface friction from 0 (slippery) to 1 (grippy); values outside are clamped, NaN is the default.</summary>
    public float Friction
    {
        get => _friction;
        set => _friction = float.IsNaN(value) ? DefaultFriction : global::System.Math.Clamp(value, 0f, 1f);
    }

    /// <summary>
    /// Physics: the mass a dynamic body uses whatever its size, or null for Auto (density x the collider's volume, so it
    /// follows the Part's Size). A value that is not finite or not positive means Auto; others are clamped to
    /// <see cref="MinMass"/>..<see cref="MaxMass"/>.
    /// </summary>
    public float? MassOverride
    {
        get => _massOverride;
        set => _massOverride = value is not { } mass || !float.IsFinite(mass) || mass <= 0f
            ? null
            : global::System.Math.Clamp(mass, MinMass, MaxMass);
    }

    public ShapeType ShapeType { get; set; }

    /// <summary>
    /// Imported model drawn instead of <see cref="ShapeType"/>; null for primitive parts. For a model,
    /// <see cref="Position"/>/<see cref="Size"/> describe its bounds box (see <see cref="ModelTransform"/>) and
    /// <see cref="Color"/> is a per-instance tint over the imported colours (white = unchanged).
    /// </summary>
    public ModelReference? Model { get; init; }

    /// <summary>
    /// Drawn in the 3D view. Play only: a script can hide a Part while playing (it still collides, is found and can be
    /// hit by rays); it is never saved, the editor never changes it, and Stop shows every Part again.
    /// </summary>
    public bool Visible { get; set; } = true;

    /// <summary>
    /// Lua scripts attached to this object, in execution order (primitive parts and model instances alike). Removing
    /// a reference or deleting the object never deletes the script file.
    /// </summary>
    public List<ScriptReference> Scripts { get; } = [];

    /// <summary>
    /// The World panel group it is organised in (null: directly in the Workspace). Editor organisation only: it changes
    /// nothing about the Part's transform, physics or scripts. Saved as the group's path in Scene.json.
    /// </summary>
    public PartGroup? Group { get; set; }

    /// <summary>
    /// A model instance's saved animation settings (which clip plays by itself when Play starts, loop, speed); null means
    /// the defaults (<see cref="ModelAnimationSettings.Default"/>). Saved in Scene.json.
    /// </summary>
    public ModelAnimationSettings? Animation { get; set; }

    /// <summary>
    /// Runtime animation playback of a model instance (the editor's Animation preview, or Play): the pose it is drawn,
    /// picked and outlined in. Never saved and never in a snapshot; null shows the rest pose. Each instance has its own.
    /// </summary>
    public ModelAnimator? Animator { get; set; }

    /// <summary>
    /// A primitive Part's surface material (shading mode, metallic, roughness, textures, emission,
    /// transparency...); null means <see cref="Graphics.SurfaceMaterial.Default"/>. Its colour stays <see cref="Color"/>.
    /// Immutable, so it is shared safely with snapshots and copies. Saved in Scene.json.
    /// </summary>
    public Graphics.SurfaceMaterial? Material { get; set; }

    /// <summary>
    /// A model instance's own versions of some of its imported materials (by material index); null or
    /// empty draws every material as imported. Saved in Scene.json.
    /// </summary>
    public Graphics.MaterialOverrideSet? ModelMaterials { get; set; }

    /// <summary>
    /// The light this Part carries (point, spot or directional, from its position and Forward); null for
    /// none. Saved in Scene.json.
    /// </summary>
    public Graphics.PartLight? Light { get; set; }
}
