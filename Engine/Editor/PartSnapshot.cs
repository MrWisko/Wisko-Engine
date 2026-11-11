using Wisko.Engine.Animation;
using Wisko.Engine.Math;
using Wisko.Engine.World;

namespace Wisko.Engine.Editor;

public readonly struct PartSnapshot
{
    public string Name { get; init; }
    public Vector3 Position { get; init; }
    public Vector3 Size { get; init; }
    public Vector3 Color { get; init; }
    public Vector3 Rotation { get; init; }
    public bool Anchored { get; init; }
    public bool Locked { get; init; }
    public bool GravityEnabled { get; init; }
    public bool CanCollide { get; init; }
    public bool IsTrigger { get; init; }
    public float Friction { get; init; }
    public float? MassOverride { get; init; }
    public ShapeType ShapeType { get; init; }

    /// <summary>The attached script references, in order (a copy; the part's own list is not shared).</summary>
    public IReadOnlyList<ScriptReference> Scripts { get; init; }

    /// <summary>A model instance's saved animation settings (immutable, so shared safely); null for the defaults.</summary>
    public ModelAnimationSettings? Animation { get; init; }

    /// <summary>The surface material, model material overrides and light (immutable, so shared safely).</summary>
    public Graphics.SurfaceMaterial? Material { get; init; }

    public Graphics.MaterialOverrideSet? ModelMaterials { get; init; }

    public Graphics.PartLight? Light { get; init; }

    public static PartSnapshot FromPart(Part part) =>
        new()
        {
            Name = part.Name,
            Position = part.Position,
            Size = part.Size,
            Color = part.Color,
            Rotation = part.Rotation,
            Anchored = part.Anchored,
            Locked = part.Locked,
            GravityEnabled = part.GravityEnabled,
            CanCollide = part.CanCollide,
            IsTrigger = part.IsTrigger,
            Friction = part.Friction,
            MassOverride = part.MassOverride,
            ShapeType = part.ShapeType,
            Scripts = part.Scripts.Count == 0 ? [] : part.Scripts.ToArray(),
            Animation = part.Animation,
            Material = part.Material,
            ModelMaterials = part.ModelMaterials,
            Light = part.Light
        };

    public Part CreatePart()
    {
        var part = new Part(Name, Position, Size, Color, Rotation, Anchored, ShapeType) { Locked = Locked };
        ApplyPhysics(part);
        ApplyGraphics(part);
        return part;
    }

    /// <summary>The material and light settings only (material, model material overrides, light).</summary>
    public void ApplyGraphics(Part part)
    {
        part.Material = Material;
        part.ModelMaterials = ModelMaterials;
        part.Light = Light;
    }

    public bool SameGraphics(PartSnapshot other) =>
        Equals(Material, other.Material) && Graphics.MaterialOverrideSet.Same(ModelMaterials, other.ModelMaterials) && Equals(Light, other.Light);

    public void ApplyTransform(Part part)
    {
        part.Position = Position;
        part.Size = Size;
        part.Rotation = Rotation;
    }

    public void ApplyAll(Part part)
    {
        part.Name = Name;
        part.Position = Position;
        part.Size = Size;
        part.Color = Color;
        part.Rotation = Rotation;
        part.Anchored = Anchored;
        part.Locked = Locked;
        part.ShapeType = ShapeType;
        ApplyPhysics(part);
        ApplyScripts(part);
        part.Animation = Animation;
        ApplyGraphics(part);
    }

    /// <summary>The Physics panel settings only (Gravity, Can Collide, Is Trigger, Friction, Mass).</summary>
    public void ApplyPhysics(Part part)
    {
        part.GravityEnabled = GravityEnabled;
        part.CanCollide = CanCollide;
        part.IsTrigger = IsTrigger;
        part.Friction = Friction;
        part.MassOverride = MassOverride;
    }

    public bool SamePhysics(PartSnapshot other) =>
        GravityEnabled == other.GravityEnabled && CanCollide == other.CanCollide && IsTrigger == other.IsTrigger && Friction.Equals(other.Friction)
        && Nullable.Equals(MassOverride, other.MassOverride);

    public void ApplyScripts(Part part)
    {
        // A snapshot made without FromPart has no script list; it leaves the part's scripts alone.
        if (Scripts is null)
        {
            return;
        }

        part.Scripts.Clear();
        part.Scripts.AddRange(Scripts);
    }

    public bool SamePosition(Vector3 other) => NearlyEqual(Position, other);

    public static bool NearlyEqual(Vector3 a, Vector3 b) =>
        global::System.Math.Abs(a.X - b.X) < 1e-4f
        && global::System.Math.Abs(a.Y - b.Y) < 1e-4f
        && global::System.Math.Abs(a.Z - b.Z) < 1e-4f;

    public bool SameTransform(PartSnapshot other) =>
        SamePosition(other.Position)
        && global::System.Math.Abs(Size.X - other.Size.X) < 1e-4f
        && global::System.Math.Abs(Size.Y - other.Size.Y) < 1e-4f
        && global::System.Math.Abs(Size.Z - other.Size.Z) < 1e-4f
        && global::System.Math.Abs(Rotation.X - other.Rotation.X) < 1e-4f
        && global::System.Math.Abs(Rotation.Y - other.Rotation.Y) < 1e-4f
        && global::System.Math.Abs(Rotation.Z - other.Rotation.Z) < 1e-4f;
}
