# TmIceConverter

Turns Trackmania 2020 maps into ice versions. Blocks that have a native ice version are swapped; most others become custom ice blocks built from Nadeo's own meshes and embedded in the map, on Stadium and on the vistas. What can't be a block yet becomes a custom ice item, and walls, ground and concrete stay native with an ice surface laid on top.

## Setup

- .NET 10, Blender and the game's NadeoImporter. On Linux also Wine, to run the importer.
- Only tested on Linux, with the game running through Proton. Windows should work but hasn't been tried.
- Copy `data/local.example.jsonc` to `data/local.jsonc` and fill in your folders.
- Copy `openplanet/IceConverterExtract` into `OpenplanetNext/Plugins/` with Openplanet dev mode on, and run its "Extract block meshes". Expect to run it many times: conversions and `view` list the files they still miss, and the next run fetches them usually.

## Use

```
tools/ice-convert convert path/to/Map.Map.Gbx -o out/folder
```

writes `out/folder/Map (ICE).Map.Gbx` and a `.report.md` next to it saying what happened to every block. Without `-o` it goes to `Maps/ICE in progress/`, with `--final` to `Maps/ICE finished/` or whatever `finishedFolder` says in `data/local.jsonc`. Running `tools/ice-convert` with no arguments converts every map in `Maps/To Ice/` that changed since its last conversion.

Restart the game if new items were built, then open the map in the editor.

```
tools/ice-convert convert <maps or folders> [-o dir]   --final, --testrun, --release, -t <tag>, --set opt=value
tools/ice-convert view <map>                           Blender scene + renders of a map
tools/ice-convert inspect <map>                        same scene with a panel to look up and mark blocks
tools/ice-convert show <item>                          preview a built item
```

The Blender tools are buggy, experimental and janky, but they somewhat work: `view`, `inspect`, `show`, `tools/zfight.py` and `tools/render_at.py`.

Options and per-map exceptions live in `data/maps.jsonc`, block and item swaps in `data/swaps.jsonc`, material mappings in `data/ice-materials.jsonc`.

## Working on it with an AI

This is how it was mostly built. Drive the converted map, and when something looks wrong, either in code or the result you can:

1. Ask the assistant to open `tools/ice-convert inspect <map>`.
2. In Blender, the Ice inspector panel is in the sidebar's Tool tab, press N if the sidebar is hidden. Click the broken block or item, write a short note and press Mark selected.
3. Close Blender and tell the assistant. Ive marked the item with comment, and it will get the coordinates and info it needs to know what item is talked about

An in-game screenshot of the spot helps a lot, since the Blender scene isn't exactly what the game draws.

`tools/zfight.py` finds flickering faces and `tools/render_at.py` renders from any camera, handy for checking a converted map.

## Known issues

- Free-placed blocks, gameplay pads, water walls and some ground deco still become items.
- Where native ice meets an item, the native block draws its border across the road.
- Item undersides and pillar joints are lighter than native ones, and item lighting can look blotchy. Compute shadows at High.
- Some end caps, borders and pillar poles differ from what the game would show, mostly vertical clips next to pillars.
- Large maps go over the room limit of 7 MB (likely 7 MiB, 7,340,032 bytes), and the game's shadow save makes them bigger still. Campaigns and solo play have no limit.
- Frozen trees have other shapes than the originals.
- The rink markings of the ice texture show on ice laid over roads.
- Terrain in snow (`snowTerrain` in `data/maps.jsonc`) shows soft lighting steps where two cells meet, like native blocks on hills do.

Built on [GBX.NET](https://github.com/BigBang1112/gbx-net). Written with extensive help from Claude, steered and tested by hand on best effort.
