# OpenSimsStudio³ UI design

The target look and the agreed UI decisions. The boards in `mockups/` are the reference layouts:

| Board | What |
|---|---|
| `mockups/Main.dc.html` | Start screen and the game-item browser |
| `mockups/Editor.dc.html` | The editor: one screen for everything about one item |
| `mockups/Export.dc.html` | The export window |

The boards are HTML with a small template layer (`{{…}}`, `<sc-for>`, `<sc-if>`), made for a design canvas. Read them for
layout, sizes, colours and wording; they do not render as plain web pages. `canvas.json` gives their order and sizes.
They hold placeholders (`[EA skirt]`, `[CASP key]`, `[Random TS3 loading screen]`) where the app shows game data.

**No screenshots of the running app go in this repo:** the app shows EA textures, meshes and loading screens. Compare
against the boards, or against screenshots shared outside the repo.

## Visual style
- **Palette:**
  - Ink `#11161E` (page), Rail `#161C25`, Surface `#202833`, inputs `#1C2430`, Line `#33404E`.
  - Text `#F2F5F8`, Muted `#ACBAC8`, disabled `#8996A5`.
  - One accent, Coral `#FF9379`, with dark `#11161E` text on primary buttons (hover `#FFAF99`, pressed `#EB816A`).
  - Success `#A8DBB8`, Warn `#F2C46D`, destructive `#FF8F8F` on `#6B3A40`.
- **Shapes and type:**
  - Fonts: Inter; JetBrains Mono for keys and hashes.
  - 6 px corners; buttons 44 px high.
  - The page's main action is a 60 px coral button with 18 px semibold text.
  - Section labels are 10 px semibold uppercase in Muted.
  - Selection: a coral border on `#252E3A`, or a 3 px coral marker on rail items.
- **Start screen layout:**
  - A 224 px left rail: the OSS3 icon on top, then Recent projects and Open project…; Settings, Report a bug and About at the bottom.
  - A 68 px page header.
  - A footer bar with Target and Project name on the left and the coral Create project button on the right.
  - **Hero:** one of the game's loading screens at random, read from the player's install at run time.
  - **Found strip:** the game with an icon per installed pack, OpenTS3 (text chip) and Blender with GEOM Tools.
- The editor and export window keep their working layouts (with a menu bar) in the same palette.
- The app's style sheet is `src/OpenSimsStudio3.App/Styles/Oss3Styles.axaml`. Its values are approximations, so bring them in line with this palette.

## Decisions
- **Starting a project:** you choose what to do first: clone a game item, clone an existing package, or make a default replacement. Then you pick the item.
  - There is no up-front flag checklist and no general "Open a package": OSS3 is not a resource editor. It opens its own projects, and outside packages come in only through Clone.
  - There is no "import my own mesh" start either. Your own mesh comes in afterwards, per mesh group, in the editor.
- **One editor screen:** mesh, textures and recolours, LODs, catalog and thumbnails share one screen, with no wizard steps. All in-game flags are edited there. Export is a separate window.
- **Recolours:** picked from a dropdown in a block at the top of the right panel, with the recolour's colours, patterns, textures and its thumbnail (Render, Replace). The left outline has one Textures entry for the picked recolour.
- **Textures:**
  - Slots show only their resolution, read from the DDS; an empty slot shows nothing.
  - Clicking a slot opens a texture window: a large preview, R/G/B/A view toggles, Import…, Export…, Bake (where it applies) and Clear, plus **Same in all recolours**.
  - A shared slot shows a link icon. There are no texture buttons under the list.
- **Under the preview:** Catalog (name in CAS, creator tag) and then Item properties, laid out in columns.
  - CAS items have no description field: CAS never shows one.
  - The properties use TSRW's full lists: all 9 part types; every clothing type, grouped; outfit categories filtered by species; flags (maternity, random, hat, revealing, hidden in CAS, own texture space with a tooltip; handedness).
  - Advanced… holds sort priority, footstep sound, hair links, the key and the launcher thumbnail.
- **Left sidebar:**
  - Recolours.
  - Mesh: LODs, each expanding to its mesh groups. A group has its own mesh (Replace, Edit in Blender, Export, Delete), Fit to body, UV warnings, material and shader, and bones.
  - Morphs: fat, fit, thin, special, each checked when present.
  - Warnings: every current warning; each one jumps to where it applies.
- **Preview outfit:** an Outfit… button on the preview. It is preview-only, not item data.
- **The material shader is editable:** a shader dropdown and editable parameters, with an add action.
- **A mesh's source shows only the model name.**
- **Project target:** Retail, Both or OpenTS3 only.
  - It is picked on the start screen and shown in the editor title bar, and it can be changed later.
  - Controls that only apply to OpenTS3 are disabled with an "OpenTS3 only" note under other targets.
  - Leaving OpenTS3 only lists the settings that will be removed and asks first.
- **No UV repacking:** OSS3 only warns about UV problems and shows them; the artist fixes them.
- **Export:**
  - It can write .package and .sims3pack together (checkboxes).
  - Checks are one-line labels with a Show link on problems.
  - It offers a copy to a test profile and clears that profile's CAS caches.
  - Merged export of several projects into one package is planned.
- **Wording:** no explanatory text in the UI. Labels and controls speak for themselves; explanations go in tooltips or docs. Button labels are short (Bake, Replace…, Export…, Show UVs, Add…), except Edit in Blender, which keeps its full label.

## Built vs mockup (2026-11-01)
- **Built:**
  - Start screen, browser and clone/default-replacement flow.
  - Editor layout, item properties, recolour block (colours, textures, live preview).
  - LOD and group outline, Edit in Blender, morph sliders, camera presets, Whole Sim, Settings page.
- **Not built:**
  - The texture window, thumbnail Render/Replace, Patterns…, and the 3/4-channel toggle.
  - Fit to body, UV warnings, shader parameters, the bones list, LOD regenerate.
  - The CAS camera, pose and animation, Outfit….
  - The target picker in the editor and the Export window.
  - Report a bug, About.
