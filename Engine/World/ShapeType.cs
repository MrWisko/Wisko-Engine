namespace Wisko.Engine.World;

/// <summary>
/// Built-in primitive shapes. Values are explicit and stable: existing scene files serialize the
/// enum member NAME (e.g. "Block", "Cylinder"), so these names must not change. New shapes are added
/// only at the end. The editor displays <see cref="Block"/> as "Cube"; the serialized name stays "Block".
/// </summary>
public enum ShapeType
{
    Block = 0,
    Cylinder = 1,
    Wedge = 2,
    Sphere = 3,
    Pyramid = 4
}
