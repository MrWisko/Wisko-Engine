using Wisko.Engine.Gameplay.Navigation;
using Wisko.Engine.Math;

namespace Wisko.Engine.Scripting;

/// <summary>What Navigation.Build asks for (checked in Lua): the area (null: around the scene) and the agent's size and abilities.</summary>
internal sealed record ScriptNavBuild(Vector3? Min, Vector3? Max, float CellSize, float AgentRadius, float AgentHeight, float MaxClimb, float MaxSlope,
    bool UseHighestSurface, bool IncludeUnanchored, IReadOnlyList<string> IgnoreTags);

/// <summary>
/// The gameplay requests that need the running scene (its objects, physics and animation) besides the session's services:
/// navigation (grid and agents), teleporting, bones, trigger watching. Implemented by the scene runtime; every answer
/// is a value or a short reason for the script (null: done).
/// </summary>
internal interface IScriptGameplayScene
{
    /// <summary>Bakes the navigation grid now (replacing any earlier one).</summary>
    string? BuildNavigation(ScriptNavBuild request, out NavGrid? grid, out double milliseconds);

    /// <summary>The grid, baked with the defaults on first use; null and why without physics.</summary>
    NavGrid? Navigation(out string? error);

    /// <summary>Re-bakes the cells in a box (null when done).</summary>
    string? RebakeNavigation(Vector3 center, Vector3 size);

    /// <summary>Where an object stands: the middle of its bottom (its feet), for path ends and agents.</summary>
    Vector3? FeetOf(IScriptObject target);

    string? CreateAgent(IScriptObject target, out NavAgent? agent);

    NavAgent? FindAgent(int id);

    string? FollowWithAgent(NavAgent agent, IScriptObject target, float distance);

    string? SetAgentDestination(NavAgent agent, Vector3 destination);

    /// <summary>One frame of an agent: its decision, applied to its object as its Move mode says.</summary>
    NavAgentStep? StepAgent(NavAgent agent, double seconds, out string? error);

    void DestroyAgent(NavAgent agent);

    /// <summary>Moves an object at once (position, optionally rotation), its physics body with it; velocity cleared unless kept.</summary>
    string? Teleport(IScriptObject target, Vector3 position, Vector3? rotation, bool keepVelocity, bool rotateVelocity);

    /// <summary>A model's bone names in skeleton order (null and why when it has none).</summary>
    IReadOnlyList<string>? BonesOf(IScriptObject target, out string? error);

    /// <summary>A bone's (or other model node's) world position and Euler rotation in its current pose.</summary>
    string? BoneTransform(IScriptObject target, string bone, out Vector3 position, out Vector3 rotation);

    /// <summary>A script connected to this object's trigger events: physics must report them.</summary>
    void WatchTriggers(IScriptObject target);
}
