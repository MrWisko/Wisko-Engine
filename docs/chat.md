# Text chat for your game (Lua)

Wisko gives scripts the building blocks of a text chat: channels, messages, filters, history, mute and block, and a
text box GUI element. You build the chat your game needs with them. A ready-made chat window exists too, but only for
games that ask for it: nothing about chat appears in a game unless a script turns it on.

**Chat is local only.** Wisko has no networking: messages never leave the computer, and "the player" means the person
playing. The design leaves room for a networked transport later (see [Multiplayer readiness](#multiplayer-readiness)).

## Build your own chat

A chat interface is a text box to type in and something that shows messages. This is the core of
[examples/chat/CustomChat.lua](../examples/chat/CustomChat.lua), a complete chat with team colours and `/me` emotes:

```lua
local box, log

function Start()
  log = UI.Create("Text", { Name = "Log", Position = { 1, -400, 0, 16 }, Size = { 0, 380, 0, 22 }, Text = "" })
  box = UI.Create("TextBox", {
    PlaceholderText = "Press T to chat", MaxLength = 120, ClearOnSubmit = true,
    Position = { 1, -400, 0, 44 }, Size = { 0, 380, 0, 32 },
  })

  -- Typing: Enter sends the box's text as the local player, with the game's own values in Data.
  box:OnSubmit(function(text)
    local id, reason = Chat.Send(text, { Data = { Team = "Blue" } })
    if not id then
      log.Text = "(" .. reason .. ")"   -- too fast, empty...
    end
  end)

  -- Showing: every message, at the start of the frame after it was sent.
  Chat.OnMessage(function(message)
    log.Text = message.Sender.Name .. ": " .. message.Text
  end)
end

function Update()
  if not Input.IsTyping and Input.IsKeyPressed("T") then
    box:Focus()
  end
end
```

- The game decides how messages look: `message.Data` carries the values it attached (a team, a colour, an icon...).
- The game decides which keys open the chat, where it sits, how many lines it keeps and what commands it understands.
- Escape always lets go of the text box first; the next Escape stops Play.

## Messages

A message is a plain table, a fresh copy for every receiver:

| Field | |
|---|---|
| `Id` | its number, unique in the session |
| `Text` | one line of cleaned text |
| `Channel` | the channel's name |
| `Sender` | `{ Id, Name }`: `"local"` for the player, `"system"` for the game, `"npc:Name"` for an NPC line |
| `Kind` | `"Player"` (typed by the player) or `"System"` (the game's own `Chat.Post`) |
| `Time` | seconds since Play started |
| `Timestamp` | Unix seconds, UTC |
| `Data` | the game's own values (an empty table when there are none) |

Messages sent in a frame are delivered at the start of the next frame: first the filters, then the channel's history,
then the `OnMessage` listeners.

### Data

`Data` is for the game's own styling and logic. It is a table of names to strings, numbers or booleans:

```lua
Chat.Post("Halt!", { SenderName = "Red Guard", Data = { Team = "Red", Color = "#FF6B6B" } })
```

- At most 16 values. A key is a name: a letter, then letters, digits or `_`, at most 32 characters.
- Strings are cleaned like message text and cut to 200 characters; numbers must be finite.
- Anything else is refused with an error naming the key.
- `Data` never changes who sent a message or its kind: `Data.Kind` or `Data.Sender` are just the game's own values.

## API

**Sending and listening**

| | |
|---|---|
| `Chat.Send(text, options?)` | The local player says text. `options`: a channel name (default "General"), or `{ Channel, Data }`. Returns the message's id, or nil and why (too fast, empty, players cannot send there). No option changes the sender or the kind. |
| `Chat.Post(text, options?)` | The game's own message, `Kind = "System"`. `options`: a channel name, or `{ Channel, SenderName, Data }` (with `SenderName`, an NPC line). Returns the id, or nil and why. |
| `Chat.OnMessage(f, channel?)` | `f(message)` for every delivered message (of one channel when given). Returns a Connection. |
| `Chat.SetFilter(f)` | Moderation hook: `f(message)` sees each message first. `nil`/`true` keeps it, `false` (and a reason) withholds it, a string replaces its text (cleaned again). Filters run in the order they were set. Returns a Connection. |

**Channels and history**

| | |
|---|---|
| `Chat.CreateChannel(name, options?)` | A channel. `options`: `MaxLength` (1-1000, default 200), `HistorySize` (1-500, default 100), `PlayersCanSend` (default true). Returns a channel handle. |
| `Chat.GetChannel(name)`, `Chat.GetChannels()` | Channel handles ("General" always exists). |
| `Chat.GetHistory(channel?, count?)` | Recent messages, oldest first (with their Data). |

A channel handle has the properties `Name`, `MaxLength`, `HistorySize`, `PlayersCanSend` and `Count`, and the methods
`channel:Send(text, { Data }?)`, `channel:Post(text, { SenderName, Data }?)`, `channel:OnMessage(f)` and
`channel:GetHistory(count?)`.

**Players and limits**

| | |
|---|---|
| `Chat.Mute(id)`, `Unmute`, `IsMuted` | Hide a sender's messages from the local player (a message's `Sender` table works too). |
| `Chat.Block(id)`, `Unblock`, `IsBlocked` | As Mute; the transport is also told (a networked one would stop them seeing yours). |
| `Chat.SetPlayerName(name)` | The local player's chat name, 1-32 visible characters, not a reserved name ("System", "Admin", "Server", "Moderator", "Wisko"...). Returns true, or false and why. |
| `Chat.SetRateLimit(burst, perSecond)` | How fast the player may send (default 5 at once, then 1 per second). |
| `Chat.LocalPlayer`, `Chat.Transport`, `Chat.IsNetworked`, `Chat.DefaultChannel` | `{ Id = "local", Name }`, `"Local"`, `false`, `"General"` (read-only). |

## Text boxes

`UI.Create("TextBox", options)` makes a one-line text box any script can use.

| Property | |
|---|---|
| `Text` | One line: line breaks become spaces and control characters go. |
| `PlaceholderText` | Shown dimmed while the box is empty. |
| `MaxLength` | 1 to 1000 (default 200). |
| `ClearOnSubmit` | Whether Enter empties the box (default false). |
| `FontSize`, `TextColor`, `Color` | Its look. |
| `Focused` | Whether it has the keyboard (read-only). |

| Method | |
|---|---|
| `Focus()` | Gives the box the keyboard: true, or false and why. |
| `Release()` | The box lets go of the keyboard. |
| `OnSubmit(f(text))` | Enter was pressed. |
| `OnFocusLost(f(submitted))` | The box let go of the keyboard. |
| `OnFocused(f())` | The box got the keyboard. |

Callbacks run at the start of the next frame.

- **Editing keys:** typing, Left/Right, Home/End, Backspace/Delete, and Ctrl+V (Cmd+V on macOS) to paste one line.
- **While a box has the keyboard,** scripts see no keys (`Input.IsKeyDown` and the others answer false, and
  `Input.IsTyping` is true), and the editor's camera keys and shortcuts are off. The mouse still works.
- **Letting go:** a click elsewhere, Escape, or hiding the box. After Escape, scripts cannot take the keyboard back for a
  second, so a script can never keep Escape from stopping Play.
- `UI.MeasureText(text, fontSize)` returns the width and line height in pixels in the game GUI's font, for wrapping
  and fitting text.

## The ready-made chat window (optional)

```lua
Chat.EnableDefaultUI()   -- "/" to type, Enter to send, Escape to stop typing
```

It shows a log of one channel (newest at the bottom; long messages wrap, their next lines indented) and a text box under
it. Players' messages are white, the game's own gold, and local notices (such as "sending too quickly") red. A message
whose `Data.Color` is `"#RRGGBB"` is shown in that colour.

`Chat.EnableDefaultUI(options)` options (all optional):

| Option | Default |
|---|---|
| `Channel` | `"General"` |
| `FontSize`, `Width`, `Height` | 16, 420, 200 |
| `FocusKey` | `"Slash"` |
| `Placeholder` | `"Press / to chat"` |
| `Position`, `Anchor` | `{ 0, 16, 1, -16 }`, `{ 0, 1 }` (bottom left) |
| `BackgroundColor`, `BackgroundOpacity`, `InputColor` | `"#14161B"`, 0.7, `"#1E2026"` |
| `PlayerColor`, `SystemColor`, `NoticeColor` | `"#F2F4F8"`, `"#FFD166"`, `"#FF8A80"` |

An unknown option or a wrong kind of value is an error. `Chat.DisableDefaultUI()` removes the window. One script at a
time has it, and it goes when that script ends. The window is the built-in module `Wisko.ChatUI`, ordinary Lua on the
public API: `require("Wisko.ChatUI")` gives `Enable(options)`, `Disable()`, `IsEnabled()` and `Notice(text)`; copy it to
`Assets/Scripts/Wisko/ChatUI.lua` in a project to change it.

## Safety

- **Cleaning:** every message is cleaned before anyone sees it: line breaks and tabs become spaces; control characters,
  bidirectional overrides, zero-width and other invisible characters, tag characters, noncharacters and unpaired
  surrogates are removed; piles of combining marks are cut to three per letter; text is cut to the channel's
  `MaxLength`.
- **No impersonation:** only `Chat.Post` makes System messages. Nothing a player can reach (the text box, `Chat.Send`)
  chooses the kind, the sender id or the sender name. Player names cannot be reserved names, however they are spaced or
  decorated. A message arriving from a transport claiming to be a player with the game's sender ids, or a reserved name,
  is dropped; its Data is checked again.
- **Rate limit:** a burst of 5 player messages, then 1 per second, adjustable with `Chat.SetRateLimit`.
- **Filters fail closed:** if a filter raises an error, that message and every later one in the session are withheld,
  and the Output panel says so once.
- **Bounds:** at most 32 channels; each keeps at most its `HistorySize` messages; at most 1,000 messages wait for a
  frame (more are refused); at most 1,000 muted or blocked senders.
- **Cleanup:** history, channels, mutes, blocks and waiting messages all go at Stop. Every Play starts with an empty
  chat.

## Local-only limitations

- Nothing is sent anywhere: there are no other players, servers, accounts or persistence.
- Mute and Block only matter for messages a transport delivers from other senders; locally there are none.
- The protections are local safety measures, not security: the game's own scripts are trusted (they can `Post`
  anything as the game), and a modified client could bypass everything on its own machine.
- Chat is not saved with the project or in game saves.
- The text box has no text selection or undo, no mouse placement of the caret, and no IME composition window.

## Multiplayer readiness

`Engine/Chat/IChatTransport.cs` is the seam a networked transport would fill: `Send(message)` carries a message this
session accepted, the transport delivers others' messages back, and `SetBlocked` passes blocks on. The service cleans
every delivered message again and drops forged player messages. A real multiplayer transport must also be
server-authoritative: the server decides who a message is from and whether it is a System message, applies the length,
rate and cleaning rules again (a client can be modified), and enforces blocks. None of this exists yet.
