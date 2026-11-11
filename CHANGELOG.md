# Changelog

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
