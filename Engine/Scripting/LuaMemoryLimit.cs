using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Wisko.Engine.Scripting;

/// <summary>
/// Accounts for, and limits, the memory one Lua state allocates. It wraps the state's own allocator (lua_setallocf):
/// every block Lua allocates, grows, shrinks or frees passes through it, so <see cref="UsedBytes"/> is exactly the Lua
/// heap (tables, strings, closures, coroutines, Lua userdata). It does not see .NET objects the host creates for
/// scripts, native memory of the engine (meshes, textures, physics) or the GPU.
/// <para>
/// Growth beyond <see cref="LimitBytes"/> is refused only while <see cref="Enforcing"/> is set (script code running
/// inside the runtime's protected resume) and no host callback is on the stack (<see cref="EnterHost"/>): Lua then
/// collects garbage once (its emergency collection) and, still short, raises its own "not enough memory" error, which
/// unwinds Lua frames only. Outside script code, and inside a host callback, nothing is refused, because a memory error
/// there would unwind a .NET frame or reach Lua's panic handler; the host's own results are bounded (at most
/// <see cref="LuaScriptRuntime.MaxQueryResults"/> results, 16 MB for a game save), so the heap can exceed the limit by
/// at most such a result until the next allocation in script code is refused. Shrinking and freeing never fail, as
/// Lua requires.
/// </para>
/// </summary>
internal sealed unsafe class LuaMemoryLimit : IDisposable
{
    private const string LuaLibrary = "lua54";

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public IntPtr OriginalAllocator;
        public IntPtr OriginalData;
        public long Used;
        public long Peak;
        public long Limit;
        public long Refusals;
        public int Enforcing;
        public int HostDepth;
    }

    private State* _state;

    /// <param name="luaState">The lua_State* of the state to account for (its main thread).</param>
    /// <param name="limitBytes">Most bytes scripts may hold; <see cref="long.MaxValue"/> for no limit.</param>
    /// <param name="usedBytes">What the state already holds (lua_gc count), allocated before this was installed.</param>
    /// <param name="install">False only to measure the accounting's own cost: nothing is counted or limited.</param>
    public LuaMemoryLimit(IntPtr luaState, long limitBytes, long usedBytes, bool install = true)
    {
        _state = (State*)NativeMemory.AllocZeroed((nuint)sizeof(State));
        _state->OriginalAllocator = lua_getallocf(luaState, out _state->OriginalData);
        _state->Limit = limitBytes;
        _state->Used = usedBytes;
        _state->Peak = usedBytes;
        if (install)
        {
            lua_setallocf(luaState, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nuint, nuint, IntPtr>)&Allocate, (IntPtr)_state);
        }
    }

    public long LimitBytes => _state->Limit;

    /// <summary>Bytes the Lua heap holds now.</summary>
    public long UsedBytes => _state->Used;

    /// <summary>The most the Lua heap has held since this was installed (or since <see cref="ResetPeak"/>).</summary>
    public long PeakBytes => _state->Peak;

    /// <summary>How many allocations were refused so far.</summary>
    public long Refusals => _state->Refusals;

    /// <summary>Set while script code runs (the runtime's resume); refusals happen only then.</summary>
    public bool Enforcing
    {
        get => _state->Enforcing != 0;
        set => _state->Enforcing = value ? 1 : 0;
    }

    public void ResetPeak() => _state->Peak = _state->Used;

    /// <summary>A host callback started (nothing is refused until the matching <see cref="LeaveHost"/>).</summary>
    public void EnterHost() => _state->HostDepth++;

    public void LeaveHost() => _state->HostDepth--;

    /// <summary>Frees the accounting block; call only after the Lua state is closed (closing frees through it).</summary>
    public void Dispose()
    {
        if (_state is not null)
        {
            NativeMemory.Free(_state);
            _state = null;
        }
    }

    // lua_Alloc: (ud, ptr, osize, nsize). With ptr null, osize is the kind of object being created, not a size.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IntPtr Allocate(IntPtr data, IntPtr block, nuint oldSize, nuint newSize)
    {
        var state = (State*)data;
        var old = block == IntPtr.Zero ? 0L : (long)oldSize;
        var growth = (long)newSize - old;
        if (newSize != 0 && growth > 0 && state->Enforcing != 0 && state->HostDepth == 0 && state->Used + growth > state->Limit)
        {
            state->Refusals++;
            return IntPtr.Zero;
        }

        // Lua's own allocator (realloc / free). A normal unmanaged call: skipping its GC transition measured no clear gain.
        var original = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, nuint, nuint, IntPtr>)state->OriginalAllocator;
        var result = original(state->OriginalData, block, oldSize, newSize);
        if (newSize == 0)
        {
            state->Used -= old;
        }
        else if (result != IntPtr.Zero)
        {
            state->Used += growth;
            if (state->Used > state->Peak)
            {
                state->Peak = state->Used;
            }
        }

        return result;
    }

    [DllImport(LuaLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr lua_getallocf(IntPtr luaState, out IntPtr data);

    [DllImport(LuaLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern void lua_setallocf(IntPtr luaState, IntPtr allocator, IntPtr data);
}
