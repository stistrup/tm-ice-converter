# TmSurfaceEditor

A tool to convert Trackmania 2020 maps to ice. It features a web based editor that allows a rule based conversion on a single or batch of maps. Conversions run either on a per map rule set configurable in the editor, or block by block. 

<img width="1453" height="1035" alt="image" src="https://github.com/user-attachments/assets/316c5c0c-e108-4762-a412-b40c0a1a084c" />

This is still under development with a bunch of bugs, missing features, assumptions that might not be true of the game engine etc. Hobby project and not sure where this will end up yet. Lots of ideas, but one thing at a time. 

**NOTE**: the viewer/editor tries to mimic what happens in game too, and is still quite often wrong. So even if something looks off, try a conversion and see what happens in game. The opposite can be true too, looks good in editor and game maybe the shadows looks way off. Let me know if you find these things.

AI has been used quite extensively, and a few features are made to work well with AI. Claude Code run at this repo can see your selections in the editor, take screenshots, run test conversions, inspect bugs etc. 

Blocks that have a native ice version are swapped; most others become custom ice blocks built from Nadeo's own meshes and embedded in the map. Blocks in vistas are made to "inherit" from a block the game accepts in that vista. What can't be a block yet becomes a custom ice item, and walls, concrete and pillars stay native with an ice surface laid on top. Terrain stays as it is. Working on a way to make terrain snow..

## Install

Download the package for your system from [Releases](https://github.com/stistrup/tm-surface-editor/releases) (Windows: `.zip`, Linux: `.tar.gz`), unpack it anywhere and start `TmSurfaceEditor.exe` (Linux: `./TmSurfaceEditor`). It opens in your browser with a setup page that finds your folders and walks you through the rest: Openplanet, NadeoImporter, Blender (Wine if you're on Linux), an extract plugin which runs and a one-time extraction of the game's blocks and models (around 6-7 GB).

I'm running on Linux and haven't tried on Windows, I'll just assume Windows could be suspicious of this program as it isn't signed, or seems normal. If it does, click More info › Run anyway, and let me know. Or if you have any other problems on Windows for that matter.

To update, just replace everything in old app folder (wherever you put it). Your settings, cache and extracted game files live elsewhere (`%LOCALAPPDATA%\TmSurfaceEditor` on Windows, `~/.local/share/TmSurfaceEditor` on Linux, and Openplanet's own folder)

## From source

.NET 10 and Node. In `web/`: `npm install`, then `npm run build`. Then run `tools/ice-convert` without arguments to start server. Open browser at http://localhost:5199 

## Use

Hopefully the interface is somewhat self-explanatory. If not, here is a short explanation:

If running first time, just go through the setup phase and follow instructions. All steps are needed to run the converter as intended. Then, at the gui, tick one or more maps in the map list, check the preview and the conversion options, then press Convert…, fill in options at popup and run the conversion. Each converted map is saved under your Maps folder (with subfolder from the popup) Its report, which says what happened to every block, is kept with the run in your user folder (`settings/runs`), not in Maps.

If new pieces were built, you need to restart the game to try it. Shadows are not computed. No idea how to do that programmatically. If you know, let me know. The extract plugin has a menu for it: with TM in developer mode, Plugins › Compute shadows for every map in… lets you pick a folder, and the plugin runs through the maps and calculates shadows map by map. (This acts as if you click open map, calculate shadows, save etc, only there to save clicks, you still need to let the game do its thing)

The first time you open a map, it takes a while to load and any ice pieces it wants to convert that don't exist yet are built in the background (you'll see "Building missing pieces 12 of 80…"). The preview updates when they're done. Huge maps naturally take much longer. Opening other maps stops the build, and it picks up where it left off next time you open that map. Pieces are shared between maps, so the more you've built, the less the next map has to build.

### Command line

The same options as the Convert popup:

```
TmSurfaceEditor convert <maps or folders> [--folder <dir>] [--name <name>] [--fake-author-time] [--me-as-author]
                       [--rebuild-blocks] [--overwrite] [--settings <from>] [--set <option>=true|false]
```

`--folder` is a folder under Maps or an absolute path (default: `ICE in progress`). An overwritten map keeps its UID; a new file name gets a UID of its own. `convert` without arguments lists every option. From the repo, `tools/ice-convert` takes the same arguments.

For working on the converter there are Blender tools: `view <map>` (a scene and renders of a map), `inspect <map>` (the same with a panel to look up and mark blocks) and `show <item>` (a built item). They're experimental and rough, but they somewhat work. I abandoned this route as soon as I started to work in the editor as that served the purpose as a debug tool. Blender preview could prove useful at some point, time will tell.

The map-wide options and their defaults are `ConvertOptions` (`Rules/ConvertOptions.cs`). A map's own choices are what the GUI saves for it, `settings/maps/<map UID>.json` in the user folder, with the edits not saved yet in `settings/working/`; a map without either converts with the defaults. Presets (`data/presets.jsonc`) say which pieces they touch and how; applying one turns its work into conversion targets, one per piece (`data/picks.jsonc` lists what each kind of piece can become). Block and item swaps live in `data/swaps.jsonc`, the theme (what roads and ground turn into, and each material's mapping) in `data/themes/ice/`.

## Code structure

```
Cli/           the commands; ConvertCommand is the conversion workflow
Rules/         options and per-map settings
Game/          what the extracted game data says: blocks, clips, placement
Conversion/    what happens to a map; BlockRules holds the order every block goes through
Cover/         the ground cover (snow): terrain cells, snowed columns, deco in the ground
Meshes/        a block's mesh pieces; ItemRecipe says what each kind of item takes
Packaging/     pieces to items and custom blocks: Blender, NadeoImporter, the block writer
Planning/      what a conversion would do, without building it: the preview, presets turned into conversion targets
Experimental/  stuff that doesn't feel solid enough yet: skin lift, ramps, shadows
Inspect/       a map as a 3D scene (GUI viewer, Blender tools), clip check
Gui/           local web server and the browser's scene format
web/           the frontend in Vue3: Vue components, three.js viewer etc
```

A conversion goes through `MapConverter`: each block runs through the rule list in `BlockRules` until one deals with it, then come the passes over the whole map. New behaviour is usually one more rule in that list.

`tools/snapshot <maps folder> <dir>` (needs your user ID set in the GUI, it converts with `--me-as-author`) converts a folder and compares the hashes of the output with the first run. Same hashes mean the same maps, items and custom blocks, mainly to verify that refactors and changes don't have unexpected side effects. It doesn't rerun the steps after import unless an item's mesh data changed, and an item that really rebuilds shows as different even when nothing is wrong: the importer doesn't produce the same bytes twice.

For the frontend, run `tools/ice-convert gui --no-open` and `npm run dev` in `web/`; Vite forwards `/api` to it. A map's first open builds a cache in the user folder (`Gui/`). The view is for finding and identifying pieces, previewing conversions and making choices: its lighting and materials are not necessarily true to the game, so judge the results in game.

## Working on it with AI

This is how it was mostly built. Although quite heavily steered to keep the code reasonable and not steer off to a vibe coded mess. Tooling exists to help development and debugging if you wanna fork and experiment yourself. (You can select items in the editor, ask eg. claude code about it and claude can query your selection, what your looking at etc).  

I try to keep the code at a reasonable standard, but have to say I'm kind of walking the tightrope of having AI do a lot for me in an area I know little of, as 3D modelling, game engines etc is way out of my comfort zone, and trying to keep it sensible and debuggable, try to avoid edge case rules and special handling to make it somewhat true to the game and avoid becoming a pile of strange bugs. And this being a hobby project, don't expect too much.

Built on [GBX.NET](https://github.com/BigBang1112/gbx-net). Licensed under the GPL-3.0 (see `LICENSE`), as GBX.NET.LZO is. Third-party licences in `THIRD-PARTY-NOTICES.txt`.
