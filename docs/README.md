# Wisko documentation

**Using Wisko**

- [The editor](editor.md): projects, the camera, selecting and transforming Parts, the baseplate, the World panel, Play.
- [Graphics](graphics.md): the Universal and Classic renderers, materials, lights, the Environment & Graphics panel.
- [Animation](animation.md): importing animated models, the Animation panel, playing clips from Lua.

**Scripting in Lua**

- [Lua scripting API](lua-api.md): the core API with examples.
- [Lua gameplay systems](lua-gameplay-systems.md): navigation, perception, game time, saved games, cameras, particles
  and the optional `Wisko.*` modules.
- [Text chat](chat.md): building a chat for your game, text boxes, the optional chat window.
- [Script limits](lua-limits.md): the instruction budget, the memory limit and other bounds.

**Platforms**

- [Windows](windows.md): the desktop app, frame pacing, anti-aliasing.
- [macOS](macos.md): status, how Wisko runs on a Mac, and the known differences from Windows.

**For developers and testers**

- [Building and testing](../README.md#building-from-source): the SDK, building, running and the test suite
  ([Tests/Wisko.Tests](../Tests/Wisko.Tests/README.md)).
- [Windows packaging](../packaging/windows/README.md): the installer and the portable zip, and how to test them.
- [Real-Mac checklist](macos-real-mac-checklist.md): getting the tested app and testing it by hand on a Mac.
- [Distribution and signing](distribution-and-signing.md): what unsigned builds look like to people, and what signing
  and notarization would change.
- [Contributing](../CONTRIBUTING.md) and [security](../SECURITY.md).

**Examples** (in [`examples/`](../examples)): the Gameplay Basics Demo, the Gameplay Systems Showcase, the Animation demo,
the Graphics Showcase, and a custom chat ([`examples/chat/CustomChat.lua`](../examples/chat/CustomChat.lua)).
