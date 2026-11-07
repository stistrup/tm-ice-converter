# TmIceConverter

Turns Trackmania 2020 maps into ice versions. Blocks that have a native ice version are swapped; most others become custom ice blocks built from Nadeo's own meshes and embedded in the map, on Stadium and on the vistas. What can't be a block yet becomes a custom ice item, and walls, ground and concrete stay native with an ice surface laid on top.

## Setup

- .NET 10, Blender and the game's NadeoImporter. On Linux also Wine, to run the importer.
- Only tested on Linux, with the game running through Proton. Windows should work but hasn't been tried.
- For the web interface, Node. In `web/`: `npm install`, then `npm run build`.
- Copy `data/local.example.jsonc` to `data/local.jsonc` and fill in your folders.
- Copy `openplanet/IceConverterExtract` into `OpenplanetNext/Plugins/` with Openplanet dev mode on, and run its "Extract game files" once (6-7 GB; `dump-exclude.txt` lists what it skips). Run it again after a game update; files already on disk are skipped.

## Use

```
tools/ice-convert convert path/to/Map.Map.Gbx --folder "ICE in progress"
```

writes `Maps/ICE in progress/<map name> (ICE).Map.Gbx` and a `.report.md` next to it saying what happened to every block. The options are the GUI's Convert popup: `--name`, `--folder` (under Maps, or an absolute path), `--fake-author-time`, `--me-as-author`, `--rebuild-blocks`, `--overwrite` (an overwritten map keeps its UID; a new file name gets a UID of its own). `tools/ice-convert convert --help` lists them all; with no arguments it prints the help.

Restart the game if new items were built, then open the map in the editor.

```
tools/ice-convert convert <maps or folders> [options] --folder, --name, --fake-author-time, --me-as-author, --overwrite, …
tools/ice-convert gui                                  web interface: browse maps, look through them in 3D
tools/ice-convert view <map>                           Blender scene + renders of a map
tools/ice-convert inspect <map>                        same scene with a panel to look up and mark blocks
tools/ice-convert show <item>                          preview a built item
```

The Blender tools are buggy, experimental and janky, but they somewhat work: `view`, `inspect`, `show`, `tools/zfight.py` and `tools/render_at.py`.

The map-wide options and their defaults are `ConvertOptions` (`Rules/ConvertOptions.cs`). A map's own choices are what the GUI saves for it, `settings/maps/<map UID>.json`; a map without one converts with the defaults. Block and item swaps live in `data/swaps.jsonc`, material mappings in `data/ice-materials.jsonc`.

## The code

```
Cli/           the commands; ConvertCommand is the conversion workflow
Rules/         options and per-map settings
Game/          what the extracted game data says: blocks, clips, placement
Conversion/    what happens to a map; BlockRules holds the order every block goes through
Meshes/        a block's mesh pieces; ItemRecipe says what each kind of item takes
Packaging/     pieces to items and custom blocks: Blender, NadeoImporter, the block writer
Experimental/  still moving: snow terrain, hill and cliff terrain, skins, ramps, shadows
Inspect/       scene export, clip check
Gui/           local web server and the browser's scene format
web/           the page: Vue components, three.js viewer in src/viewer
```

A conversion goes through `MapConverter`: each block runs through the rule list in `BlockRules` until one deals with it, then come the passes over the whole map. New behaviour is usually one more rule in that list.

`tools/snapshot <maps folder> <dir>` converts a folder and compares the hashes of the output with the first run. Same hashes mean the same maps, items and custom blocks, so a refactor can be checked without driving. It doesn't rerun the steps after import unless an item's mesh data changed, and an item that really rebuilds shows as different even when nothing is wrong: the importer doesn't produce the same bytes twice.

For the page, run `tools/ice-convert gui --no-open` and `npm run dev` in `web/`; Vite forwards `/api` to it. A map's first open builds a cache in `Work/Items/IceConverter/Gui`. The view is for finding and identifying pieces: its lighting and materials are rough, so judge the look in game.

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
- Terrain in snow (`snowTerrain` in a map's settings) shows soft lighting steps where two cells meet, like native blocks on hills do.

Built on [GBX.NET](https://github.com/BigBang1112/gbx-net). Written with extensive help from Claude, steered and tested by hand on best effort.
