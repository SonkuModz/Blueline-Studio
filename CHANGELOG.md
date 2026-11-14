# Changelog

## 0.10.0

Beta, same test setup as 0.9.0 (NHL 19 CUSA11126 1.60 under shadPS4).

### Uniform studio and gear colours
- Glove and helmet colours per kit, edited in the game database's team gear table. A kit that shares the fallback row can get a row of its own (seen working in game)
- The 3D uniform is now a uniform studio: the player large, with the glove and helmet editor beside it
- Choose a zone in the editor to light it up on the model and bring the camera to it; click the gloves or helmet to select the zone under the mouse
- Only the zones the shown glove or helmet model uses are listed first, with how much of it each covers
- Gloves and helmet drawn in their kit's colours; kit buttons and Front, Back, Gloves and Helmet views
- A roster save loaded in the game keeps its own copy of these colours and wins over the table

### Projects
- Open the mod Puck Peak Overtime installed as a Blueline project (start screen, menu, or `blueline puckpeak`). Its `boot.big` files cannot be carried by patch archives and are listed as warnings

### Build
- Each game database goes in its own patch archive: two in one archive hung the boot

### Docs
- docs/VISUALS.md: how NHL 19 draws a frame (materials, lighting, characters, arenas, effects, post-processing)

## 0.9.0 (first public release)

Beta. Tested on one NHL 19 PS4 dump (CUSA11126, 1.60) under shadPS4. Retail PS4 has not been tried.
See [docs/STATUS.md](docs/STATUS.md) for what is proven per feature.

### Editors
- Assets, Textures, Text, Database, Audio, Video, Teams (with 3D uniform), Jersey Maps, Models and Overlays
- Build with read-back verification, per-file revert, install and uninstall as separate `patch_*_99_*` archives
- Add another mod: compares every file, shows conflicts, takes what you tick as one undo step
- Play tab for shadPS4 settings and launch

### Overlays
- Score clock moments (goal, power play and more) previewed from the game's own animation rules
- Flat scorebug look (texture-only) with a one-click restore

### Interface
- Grouped tab bar with icons, a sliding indicator, and quick page transitions
- Start screen with recent projects and your own background picture
- Dark and light themes, reduce motion option
- Ctrl+K command palette for tabs, actions and game files
- Project chip with undo, redo and project actions; toasts for results

### Known limits
- Overlay layout files from `boot.big` do not load through patch archives; textures do
- Models are read-only; audio replacements must fit the original slot
