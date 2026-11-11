# Script limits

Scripts run in a sandbox with limits, so a mistake in a script (or a project from someone else) stops that script with
a clear message instead of freezing or crashing the editor. The limits are generous: normal games stay far below them.

## Instruction budget

Each callback (a script's top-level chunk, `Start`, one `Update`, one event or timer) may run at most 50,000,000 Lua VM
instructions. A runaway loop reaches that in well under a second and is stopped: the Output panel shows
"stopped after exceeding the instruction budget", and the script faults. Its coroutines run under the same budget, and
catching the error does not escape it.

## Memory limit

All scripts of a Play session share one Lua heap, and it has a limit: **256 MB** by default.

- **What it counts:** every block the Lua VM allocates (tables, strings, closures, coroutines, Lua userdata), exactly,
  from Lua's own allocator. It does not count the engine's .NET objects, native memory (meshes, textures, physics) or
  GPU memory, which scripts reach only through the engine's bounded APIs.
- **What happens at the limit:** an allocation that would go past it is refused. Lua first collects garbage once; if
  that is not enough, the running callback stops with "stopped: the scripts' Lua memory would have gone past the limit
  of 256 MB", the script faults, and what only that script held is freed at once so the other scripts carry on. A
  script that catches the error with `pcall` is stopped too.
- **When it applies:** while script code runs. Inside the engine's own functions nothing is refused (a memory error
  there could not be handled safely), so the heap can briefly exceed the limit by one engine result, such as a list of
  at most 1,000 query results or a game save of at most 16 MB, before the next allocation in script code is refused.
- **Why 256 MB:** every example project was measured over 20 seconds of Play. The largest Lua heap, the Gameplay
  Systems Showcase, peaked at 5.8 MB (Gameplay Basics Demo 4.6 MB, Graphics Showcase 1.7 MB, Animation Demo 1.6 MB,
  Editor and Chat Showcase 0.8 MB). 256 MB is about 44 times the largest, and small enough that a runaway script is
  stopped long before it can endanger the editor on an 8 GB computer.
- **Cost:** counting adds a few nanoseconds to each Lua allocation: about 5-25% on loops that do nothing but allocate,
  nothing measurable on code that does not allocate, and no measurable difference on a whole game (the Gameplay
  Systems Showcase, 600 frames).

**Changing it.** The limit belongs to the computer, not to a project, so a project made by someone else cannot raise
it. It is kept in `editor-scripts.json` next to the editor preferences (`%LOCALAPPDATA%\Wisko` on Windows,
`~/Library/Application Support/Wisko` on macOS):

```json
{
  "FormatVersion": 1,
  "MemoryLimitMegabytes": 512
}
```

Values from 32 to 4096 are accepted (others are clamped); a missing or unreadable file means 256. Wisko reads it at
start-up and logs `[Scripts] Lua memory limit: ... MB per Play session`.

## Other bounds

| Bound | Limit |
|---|---|
| Timers per script | 10,000 |
| `Events.On` connections and GUI callbacks per script | 1,000 |
| `Events.Emit` calls waiting at once | 10,000 |
| Values in one `Events.Emit`; strings in it | 16; 4,096 characters |
| Results of one query (`RaycastAll`, overlaps, Scene searches) | 1,000 |
| GUI elements | 10,000 |
| `print()` message | 4,096 bytes |
| Module file for `require` | 16 MB |
| Game save | 16 MB, 1,000 slots |
| Chat | see [chat.md](chat.md#safety) |

## What the limits do not do

They keep a script's mistakes from taking the editor down; they do not make an untrusted project safe to open. A
project's models and images are read by native and unsafe decoders, and its scripts can still use everything the API
offers. Open projects from people you trust. See [SECURITY.md](../SECURITY.md).
