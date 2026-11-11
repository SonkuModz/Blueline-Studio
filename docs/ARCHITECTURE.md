# Architecture

```
Blueline.App (WPF)   Blueline.Cli
        \                /
         Blueline.Core            game install, merged file view, projects, build, install
              |
         Blueline.Formats         file formats only: no UI, no knowledge of projects
```

## Blueline.Formats

Each format stands alone and works on bytes or streams.

| Folder | Contents |
|---|---|
| `Eb/` | `.big` reader, writer, path hash, chunkzip, audit |
| `Textures/` | GNF, Thin 1D tiling, block codecs, DDS, PNG, the import pipeline |
| `Detect/` | Format detection from bytes |
| `Tdb/` | Databases, CRCs, localization, CSV |
| `Audio/` | Sound bank walk and same-slot replace |
| `Models/` | `.mcd` parsing and OBJ export |
| `Scaleform/` | `.gfx` movies: `GfxMovie` lists their textures, `GfxTimeline` lays out any frame |

`Video/Vp6File` reads EA's `.vp6` chunks, pulls out the matte and swaps in new frames. `Video/AviVideo` reads the
frames of a compressed AVI.

## Blueline.Core

| Type | Job |
|---|---|
| `UniformKits` | Groups a team's jersey, pant and sock sheets into kits and names the meshes they go on |
| `ModMerge` | Compares another mod with a project and the game, then stores the chosen files in the project |
| `GameInstall` | Finds archives, overlays the `CUSA11126-mods` folder if there is one, and gives the mount order |
| `VirtualFileSystem` | Merges archives by that order. Answers "what would the game load for this path?" without loading archives into memory |
| `ModProject` | A folder holding a manifest and content-addressed changed files. Set, revert, undo, redo |
| `Workspace` | Game plus project. `ReadCurrent(path)` gives the edited version if there is one |
| `OverlayBuilder` | Writes overlay archives into the project and reads every file back to verify it |
| `ModInstaller` | Copies built archives into `databigs` and tracks them, so uninstall only removes what it installed |
| `BatchTextureImport` | Matches a folder of images to game textures |
| `Teams/TeamCatalog` | Joins the game's team table to its files, filed by category. See RESEARCH section 6.4 |
| `Teams/JerseyMaps` | Makes a jersey's `_nm` and `_coeff` maps from its colour sheet or a picture, on a plain base made from the game's own sheets. See RESEARCH section 6.7 |
| `Teams/TeamLogoImport` | One picture into all of a team's square logos, as one undo step |
| `SxTool` | Runs the user's `sx.exe` for audio |
| `FfmpegTool` | Runs the user's FFmpeg: reads videos, makes MP4s, encodes replacements to match the game's. See RESEARCH section 6.8 |
| `VideoPreviewCache` | Keeps MP4 previews on disk by content, so a video is converted once |
| `Overlays/OverlayCatalog` | Joins definitions, navigation, bundles, layouts, textures, fonts and movies into overlays. See RESEARCH section 6.9 |
| `Overlays/LayoutDocument`, `JsonEdit` | Editable layouts and JSON that keeps numbers as written and survives the game's repeated keys |
| `Overlays/LayoutPlacer` | Works out where each widget lands, with clipping, masks, tints and blends |
| `Overlays/OverlayCompositor` | Draws layers over a backdrop the way the game's interface blends them |
| `Overlays/OverlaySample` | Sample teams, scores and text for the preview |
| `SessionLog`, `AppSettings` | Logging and settings |

## Blueline.App

- `Shell/` holds the main window, the background job runner (progress, cancel, error banner), shared services and
  the capture tour.
- `Controls/` holds what modules share: `AssetBrowser`, `TexturePreview`, `VideoPlayer`, `FontPreview`,
  `ModelViewport`, `ConfirmDialog`, `DropTarget`, `OverlayCanvas`, `JsonPropertyEditor`.
- `Modules/` has one folder pair per tab. Each implements `IEditorModule` and is registered in one list in
  `MainWindow`. Modules talk only through `AppServices`, so adding a tab touches no other tab.
- `Theme/Theme.xaml` holds all colours and control styles.

## The rules that keep game files safe

1. Only `ModInstaller` writes into the game folder, and only archive names matching `patch_(inxp|game)_99_*`.
2. A build reads back every file before reporting success.
3. Each change goes in the overlay for the family that holds the current winning copy, so it mounts at the right
   time, and keeps the original's chunkzip or raw wrapper.
4. Heavy work runs through `JobRunner`, so the UI stays responsive and errors reach the banner and log.
