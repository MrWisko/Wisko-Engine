using Wisko.Engine.World;

namespace Wisko.Engine.Editor;

internal static class PartDuplicator
{
    public static Part Duplicate(Part source, Workspace workspace)
    {
        // A model duplicate is another instance of the same asset (shared, not re-imported), named after its source.
        var name = source.Model is null
            ? PartSpawner.GenerateNextPartName(workspace)
            : PartSpawner.GenerateUniqueName(workspace, source.Name);
        var duplicate = new Part(
            name,
            source.Position,
            source.Size,
            source.Color,
            source.Rotation,
            source.Anchored,
            source.ShapeType)
        {
            Model = source.Model,
            GravityEnabled = source.GravityEnabled,
            CanCollide = source.CanCollide,
            IsTrigger = source.IsTrigger,
            Friction = source.Friction,
            MassOverride = source.MassOverride,
            Group = source.Group, // a duplicate stays in its source's World panel group
            Animation = source.Animation, // the same saved settings; its playback is its own
            Material = source.Material, // Material and light data is immutable: shared, never aliased state
            ModelMaterials = source.ModelMaterials,
            Light = source.Light
        };

        // Locked is not copied: a duplicate is made to be placed, so it starts unlocked (movable from the viewport).
        // Same script references, same order (the references are immutable; each object gets its own script instances).
        duplicate.Scripts.AddRange(source.Scripts);
        return duplicate;
    }
}
