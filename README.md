# TmIceConverter

Turns Trackmania 2020 maps into ice versions. Blocks that have a native ice version are swapped, everything else that should be ice becomes a custom item built from Nadeo's own meshes and embedded in the map.

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
tools/ice-convert convert <maps or folders> [-o dir]   --final, --testrun, -t <tag>, --set opt=value
tools/ice-convert view <map>                           Blender scene + renders of a map
tools/ice-convert inspect <map>                        same scene with a panel to look up and mark blocks
tools/ice-convert show <item>                          preview a built item
```

The Blender tools are buggy, experimental and janky, but they somewhat work: `view`, `inspect`, `show`, `tools/zfight.py` and `tools/render_at.py`.

Options and per-map exceptions live in `data/translations.jsonc`, material mappings in `data/ice-materials.jsonc`.

## Working on it with an AI

This is how it was mostly built. Drive the converted map, and when something looks wrong, either in code or the result you can:

1. Ask the assistant to open `tools/ice-convert inspect <map>`.
2. In Blender, the Ice inspector panel is in the sidebar's Tool tab, press N if the sidebar is hidden. Click the broken block or item, write a short note and press Mark selected.
3. Close Blender and tell the assistant. Ive marked the item with comment, and it will get the coordinates and info it needs to know what item is talked about

An in-game screenshot of the spot helps a lot, since the Blender scene isn't exactly what the game draws.

## What we've figured out so far

Current understanding after a lot of trial and error, not documentation. Some of it may well be wrong.

- Which end caps, borders and pillar poles a block shows depends on its neighbours. Replacing a block with an item changes its neighbours too, so those get rebuilt as well, see `ClipResolver`.
- Some clips are shared between mirrored blocks and the data doesn't say how they're turned; the converter picks the best fit.
- Items can't carry gameplay effects, so turbo/reset/slow-motion blocks stay native or get a gate.
- Blocks missing from an environment make the game refuse the map; items are fine anywhere.
- A map without shadows still has to declare a lightmap or clubs reject it.
- NadeoImporter drops collision surfaces and waypoint info, which get patched in afterwards.

`tools/zfight.py` finds flickering faces and `tools/render_at.py` renders from any camera, handy for checking a converted map.

Built on [GBX.NET](https://github.com/BigBang1112/gbx-net). Written with extensive help from Claude, steered and tested by hand on best effort.
