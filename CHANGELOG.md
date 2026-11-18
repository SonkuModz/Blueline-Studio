# Changelog

## 0.11.0

Beta, same test setup as 0.10.0 (NHL 19 CUSA11126 1.60 under shadPS4).

### Important fix: patch archives with three or more files
- Every stock .big has a one-byte-per-file flag table after its rows. Blueline 0.10.0 and older left it out, so in any archive with three or more files, the third file and every one after it loaded black or not at all (ad boards, masks). Fixed and confirmed in game. **Rebuild and reinstall any archive built with 0.10.0 or older**

### Full-ice rinks
- Teams tab, Full ice: a whole-rink ice image per team from faceoff.net, for use with a full-ice rink model (`rink_ice_nhl.mcd`) that stretches the centre-ice logo over the whole rink. The image is fitted so its goal lines sit on the game's goal lines, and the nets drawn on the image are painted out
- Every NHL, AHL and ECHL team at once, with a shared plain rink for every other slot (the game has no fallback slot). Pages are fetched politely and cached
- CLI: `faceoff`, `full-ice`, `full-ice-set`, `full-ice-leagues`

### Jerseys
- Jersey Maps, Layout card: "Fit to this jersey" re-bakes a sheet drawn in the adidas layout onto the Reebok (rbk) jersey through the 3D models, so side stripes and under-arm panels line up (checked in game on converted NHL 27 kits). CLI `jersey-refit`
- New Font Maps tab: makes the normal and specular maps for jersey numbers and names from a font's colour map. CLI `font-maps`, `font-maps-all`

### Team details and roster saves
- Team details panel on the Teams tab, for a team's roster table fields. CLI `team`
- Build tab, Copy edits to roster save: puts the project's database edits into the game's custom roster save (RosterCustom0), which wins over the database in game. The save is backed up first. Checked in game with the score clock colour. CLI `roster-sync`
- Merge another project, a .big or a folder of .big files into the open project, reviewing every file first

### Score clock (work in progress)
- `boot-build` rebuilds boot.big for the mods folder, the only route by which score clock layouts load
- The Sportsnet goal animation is compiled into the score clock's own code (`sportsnet`, `goal-frames`). Seen in game in a test setup; the power play and stats drop-downs are not done yet

### League colours from TruColor
- Teams tab, League colours: a whole league's team colours from trucolor.net (NHL and alumni, AHL, ECHL, OHL, QMJHL, WHL), matched to the game's teams by full name and shown next to each team's current menu, presentation and score clock colours before anything is written. Click a site colour for primary, right-click for secondary; the score clock can be darkened like stock; home gloves and helmet are opt-in
- Pages are fetched politely (one at a time, 2 s apart, cached for 30 days). The site has no European leagues
- CLI: `trucolor` lists the site's leagues, previews a league (with an HTML page), and with `--project --write` writes into a project

### Goal horns
- Pick an arena's goal horn by name in Team details (Songs and sound): 31 club horns, 9 spare club slots, Generic (what every AHL arena plays) and 85 novelty horns
- Play a horn's sample and replace its sound from any audio FFmpeg reads; it is encoded to EA Opus to fit the sample's slot. The sample is filled in for every novelty horn (checked in game); club and Generic samples are marked likely
- EA Opus samples (most banks, all horns) now play, export and replace in the Audio tab through FFmpeg, with no sx.exe
- CLI: `horns`, `horn-export`, `horn-set`, and `horn-label`, a test bank where every horn sample says its number, to find which sample a horn plays

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
