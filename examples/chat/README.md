# Custom chat example

[CustomChat.lua](CustomChat.lua) is a complete chat built only from the public Lua API: a `TextBox` to type in, the
`Chat` library for messages, and its own panel at the top right. It does not use the ready-made chat window.

**Try it:** make a project, copy `CustomChat.lua` into its `Assets/Scripts` folder, attach it to any Part (World panel →
Scripts → Attach) and press Play.

- **T** starts typing, **Enter** sends, **Escape** stops typing (a second Escape stops Play).
- `/me waves` sends an emote.
- Messages carry the sender's team in `Data`, and the panel colours each line by team.
- Say "hello" and the Red Guard answers.

The automated tests play this file exactly as it is here (`Chat: the custom chat example` in `Tests/Wisko.Tests`). How
chat works: [docs/chat.md](../../docs/chat.md).
