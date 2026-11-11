using Wisko.Engine.Math;

namespace Wisko.Engine.World;

public static class PartMover
{
    public const float MoveSpeed = 8f;
    public const float FastMultiplier = 3f;

    public static void TryMove(Part? part, Vector3 direction, float deltaTime, bool fast, Workspace workspace)
    {
        if (part is null || deltaTime <= 0f || direction.Length < 1e-6f)
        {
            return;
        }

        var speed = MoveSpeed * (fast ? FastMultiplier : 1f);
        var candidate = part.Position + direction.Normalized() * (speed * deltaTime);

        var previous = part.Position;

        if (!PartCollision.TryApplyPosition(part, candidate, workspace, out var resolved))
        {
            part.Position = previous;
            return;
        }

        part.Position = resolved;
    }
}
