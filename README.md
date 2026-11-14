# TmSurfaceEditor

A tool to convert Trackmania 2020 maps to ice. It features a web based editor to swap surfaces on blocks. It previews your edits, can edit in bulk, per preset or block by block. 

<img width="1453" height="1035" alt="image" src="https://github.com/user-attachments/assets/316c5c0c-e108-4762-a412-b40c0a1a084c" />

This is still under development with a bunch of bugs, missing features, assumptions that might not be true of the game engine etc. Hobby project and not sure where this will end up yet. Lots of ideas, but one thing at a time. AI is used quite heavily and tooling exists to assist with development. I try to keep reasonable code standards, choose flows, rules, UI interactions with care but some vibe coded messes here and there is to be expected.

**NOTE**: the viewer/editor tries to mimic what happens in game too, and is still quite often wrong. So even if something looks off, try a conversion and see what happens in game. The opposite can be true too, looks good in editor and game maybe the shadows looks way off. Let me know if you find these things.

I'm trying to make this grow to handle more materials, but currently ice/snow is the focus. I'll fill in more info on how stuff works but this changes so often so i'll just leave this like this for now.

## Install

Only pre-releases so far. Download the newest package for your system from [Releases](https://github.com/stistrup/tm-surface-editor/releases) (Windows: `.zip`, Linux: `.tar.gz`), unpack it anywhere and start `TmSurfaceEditor.exe` (Linux: `./TmSurfaceEditor`). It opens in your browser (should at least, if it doesn't browse to http://localhost:5199) with a setup page that finds your folders and walks you through the rest: Openplanet, NadeoImporter, Blender (and Wine on Linux), and an extract plugin that runs in the game to make a one-time copy of the game's blocks and models (around 6–7 GB).

I'm running on Linux and haven't tried on Windows, I'll just assume Windows could be suspicious of this program as it isn't signed, or seems normal. If it does, click More info › Run anyway, and let me know. Or if you have any other problems on Windows for that matter.

To update, just replace everything in old app folder (wherever you put it). Your settings, cache and extracted game files live elsewhere (`%LOCALAPPDATA%\TmSurfaceEditor` on Windows, `~/.local/share/TmSurfaceEditor` on Linux, and Openplanet's own folder)

## From source

.NET 10 and Node. In `web/`: `npm install`, then `npm run build`. Then run `tools/ice-convert` without arguments to start server. Open browser at http://localhost:5199 

#### Dev mode 
.NET 10 and Node. In `web/`: `npm install`. Then run `tools/gui-dev` from the repo root: it starts the page on Vite http://localhost:5173 and the API on :5199. After changing C# code, run `tools/gui-dev restart`: the API builds and starts again, probably reload browser.

## Use

Hopefully the UI is somewhat self-explanatory. If not, sorry stuff changes so much and can't be bothered to keep this up to date and will wait until UX stuff settles.
Safe to click around, test conversions etc, this app does not modify any original item, map or file. The only things that touch the game are installing the Openplanet plugin to extract game files and turning on dev mode in Openplanet (not without asking). And of course it creates maps, blocks and items.

The converted items end up in `Documents/Trackmania/Items/SurfaceEditor/` and working files in `Documents/Trackmania/Work/Items/SurfaceEditor/` that nadeo importer needs, extracted files in `OpenplanetNext/Extract/`, and converted maps in `Documents/Trackmania/Maps/` as new files. Other maps, items etc are never touch. 

### Command line

The same options as the Convert popup:

```
TmSurfaceEditor convert <maps or folders> (--settings <from> | --preset <name>) [--folder <dir>] [--name <name>]
                       [--fake-author-time] [--me-as-author] [--rebuild-blocks] [--overwrite] [--set <option>=true|false]
```

Say what a map converts by: `--settings current` takes what you set up in the GUI (the working copy, else the saved settings), `--settings saved`, a settings file or a run id; `--preset <name>` applies a preset (on top of `--settings` when both are given). `--folder` is a folder under Maps or an absolute path (default: `ICE in progress`). An overwritten map keeps its UID; a new file name gets a UID of its own. `convert` without arguments lists every option. From the repo, `tools/ice-convert` takes the same arguments.

For working on the converter there are Blender tools: `view <map>` (a scene and renders of a map), `inspect <map>` (the same with a panel to look up and mark blocks) and `show <item>` (a built item). They're experimental and rough, but they somewhat work. I abandoned this route as soon as I started to work in the editor as that served the purpose as a debug tool. Blender preview could prove useful at some point, time will tell.

The map-wide options and their defaults are `ConvertOptions` (`Rules/ConvertOptions.cs`). A map's own choices are what the GUI saves for it, `settings/maps/<map UID>.json` in the user folder, with the edits not saved yet in `settings/working/`; a map without either converts with the defaults. Presets (`data/presets.jsonc`) say which pieces they touch and how; applying one turns its work into conversion targets, one per piece (`data/picks.jsonc` lists what each kind of piece can become). Block and item swaps live in `data/swaps.jsonc`, the theme (what roads and ground turn into, and each material's mapping) in `data/themes/ice/`.


Built on [GBX.NET](https://github.com/BigBang1112/gbx-net). Licensed under the GPL-3.0 (see `LICENSE`), as GBX.NET.LZO is. Third-party licences in `THIRD-PARTY-NOTICES.txt`.
