# The editor

How to make and edit worlds in Wisko: projects, the camera, selecting and transforming Parts, the baseplate, the World
panel, and Play.

## Projects

The **Project Hub** opens first. It lists your projects (with a picture of each, taken when you last left it) and a
**History** of worlds you opened, including ones deleted through Wisko.

**A new world.** **New World** shows each template (Classic, Testing World) as a picture of the world it creates.

- A click picks a template; the arrow keys and Tab move between templates and Enter picks one; Escape goes back.
- Pointing at a card highlights it, as the keyboard focus does.
- In a small window the cards stack and shrink to fit.

**Where projects live.** `Documents\Wisko\Projects` in your user profile (macOS: `~/Documents/Wisko/Projects`). Start
Wisko with `--projects <folder>` to use another folder (portable use, tests); the History is then kept next to that
folder. A project is an ordinary folder: `Scene.json`, `Atmosphere.json`, `UI.json`, `Graphics.json` (when the project
has graphics settings), `project.json` and `Assets/` (scripts, models, textures). Game saves made by scripts go to its
`Saves/` folder.

**Saving and leaving.** Save Project (project menu) writes the project. Leaving with unsaved changes (Leave to Hub or
closing the window) asks **Save | Don't Save | Cancel**:

- Save asks for a name only for a world that was never saved.
- Don't Save leaves a saved project exactly as it was last saved, and a new world leaves nothing behind (no folder, no
  History entry).
- Cancel stays in the editor.

Leaving without changes asks nothing; nothing is saved without being asked. Every file is written safely: the new
contents go to a temporary file that replaces the old one only once it is complete on the disk, so a crash or a full
disk during a save never leaves a damaged file.

A project's files are also saved together, as one step: after a crash they are always all from the same save, never
a mix of two. The project's hidden `.wisko` folder holds what this needs: the save before the last one
(`.wisko/previous`) and, while a save is in progress, its new files. When a project opens after an interrupted save,
Wisko finishes that save if it had got far enough, or otherwise keeps the project as it was last saved. It never puts
an older file over a newer one, and when the files cannot be shown to belong to one save it changes nothing, keeps
every file and says so. A second Wisko window cannot save the same project at the same time. Copying a project does
not need its `.wisko` folder.

## The camera

The camera keys are physical key positions: on AZERTY, W A S D are the keys labelled Z Q S D.

| Key | Action |
|---|---|
| W / S | Move forward / backward |
| A / D | Move left / right |
| Space / C | Move up / down |
| Right mouse button + drag | Look around |
| Shift | Move faster |
| Mouse wheel | Fly forward / back |

## Selecting and transforming

The toolbar's tools are **Select**, **Move**, **Rotate** and **Resize**.

- A click selects a Part; Ctrl+click (on macOS also Command+click) and Shift+click in the World panel add rows; dragging
  on empty space draws a selection box. Locked Parts are looked through: a click selects what is behind them.
- **Move** shows arrows: dragging one moves the selection along that axis. A press on the body selects.
- **Rotate** shows rings; **Resize** shows handles on the faces (and corners for a single Part).
- With **Select**, **Rotate** or **Resize** active, dragging a Part's **body** moves it:
  - the handles keep priority (a press on a ring or handle rotates or resizes);
  - the Part keeps the point where it was grabbed, so it does not jump (a Part in the air keeps its height above the
    surface);
  - with no surface under the cursor (no baseplate, nothing below) the grab point follows the cursor on the level plane
    through it;
  - a move into another Part is refused; a multi-selection moves together;
  - the tool stays active, and the drag is one undo step. **Escape** during a drag puts everything back and records
    nothing.
- Holding **Shift** while dragging a rotate ring snaps the change to 15° steps. **R** and **Shift+R** (Rotate tool)
  turn the selection ±90° about Y.
- Ctrl+Z / Ctrl+Y undo and redo; Delete or Backspace deletes the selection; Ctrl+D duplicates it; Ctrl+S saves the
  project (as Save Project in the project menu; in a script tab it saves that script).
- **macOS** uses Command, as Mac apps do: Command+Z undo, Command+Shift+Z (or Command+Y) redo, Command+D duplicate,
  Command+G / Command+Shift+G group / ungroup, Command+S save. The letter is the one printed on the key (on AZERTY the
  key labelled Z is Command+Z). While Command or Control is held the fly keys do nothing, so Command+S, D or W never
  move the camera.

### Rotation in degrees

With Parts selected, the World panel shows **Rotation** fields X, Y and Z (degrees) above the Scripts section.

- They are the Part's `Rotation`: Euler angles about the **world** axes, applied in the order **Z, then X, then Y**.
  Lua's `Transform.Rotation` and `Scene.json` use the same numbers.
- The fields show the stored values, rounded to two decimals. With several Parts selected, an axis where they differ
  shows "—"; typing a value there sets that axis on all of them.
- Click a field (or Tab into it) and type: decimals, negative values, a leading `+`, a comma as the decimal point and a
  trailing `°` are accepted. What you type stays as typed until it is committed.
- **Enter**, **Tab**, **Shift+Tab** or a click elsewhere commits; **Escape** cancels. A commit is one undo step for all
  selected Parts.
- The value is stored normalized to 0 up to (but not including) 360: -45 is stored, and shown, as 315.
- Not a number, not finite, or larger than 1,000,000 in size: a short message appears. If a selected Part is locked,
  nothing changes and a message says why.
- The fields follow the rotate gizmo live, and a small readout near the gizmo shows the angle being dragged ("Y 45°").

## The baseplate

Every world starts with a baseplate: the 512 × 512 ground. It is part of the environment, not a Part, but a world may
delete it.

- **Select it:** click its row in the World panel, or Ctrl+click (Command+click on macOS) it in the 3D View. A plain click on the ground still
  clears the selection, and a drag still box-selects.
- **Delete it:** press **Delete** or **Backspace**, or choose **Delete** from the row's right-click menu. It is one undo
  step.
- **Without a baseplate:** nothing is drawn or listed there; in Play nothing collides with it (unanchored Parts fall);
  Lua raycasts that would hit it hit nothing; dragging no longer rests on it, and new Parts are no longer kept above it.
- The world is saved without it, and reopening or pressing Play never brings it back. `Scene.json` changes form only
  for such a world: `{"Environment": {"Baseplate": false}, "Parts": [...]}`; a world with its baseplate is saved as a
  plain list of Parts, as before. Wisko versions before 0.1.0 cannot open a world saved without a baseplate.

## The World panel

The World panel lists the world's Parts, models and lights (and the baseplate), and the selection's details: name,
lock, rotation and scripts.

**Groups.** Select rows (Ctrl+click, Shift+click), then right-click → **Group** or press **Ctrl+G** (macOS: Command
instead of Ctrl). Groups can be renamed, nested, opened and closed, dragged and ungrouped (**Ctrl+Shift+G**), with undo.
A name is typed with the keyboard's own layout (AZERTY, accents, other scripts and spaces). They only organise the list
and never move anything.

## ToolBench

The **ToolBench** menu switches the toolbar between its sections: **Main Tools** (the transform tools, anchor, lock,
colour, shapes and the World panel), **Scripting** (the Output panel), **Environment & Graphics** (atmosphere,
lighting, rendering, post-processing and materials; see [graphics.md](graphics.md)) and **UI** (the game GUI editor).
It also opens the **Physics** and **Animation** panels (see [animation.md](animation.md)). The project menu saves,
imports models, leaves to the Hub and deletes the project.

## Play

**Play** runs the world: physics starts, the scripts run (see [lua-api.md](lua-api.md)), and the game GUI appears.
**Stop** (or Escape) restores the world exactly as it was before Play. In Play, Escape first stops typing in a text box;
the next Escape stops Play.
