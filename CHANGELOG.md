# Changelog

## 0.12.0

Beta, same test setup as 0.11.0 (NHL 19 CUSA11126 1.60 under shadPS4). A big release: mod packs, a full roster editor, uniform slots and a new look.

### Mod packs and the Mods tab
- New `.blpack` mod pack format (docs/MOD_PACKS.md): one file that carries a project to someone else's game. It holds only the files the mod changes or adds, never whole game archives, plus optional pack art and an optional custom roster save
- Build tab, Share card: **Export mod pack**, with name, author, version, description and art
- New **Mods** tab (Ship group): open or drop in a pack, see what it changes, check the game version and the stock files it expects, see conflicts with other packs, and set load order (top wins)
- Install builds the pack's `patch_*_99_pk-*` archives on the player's own machine, with every archive read back. A pack's roster goes into a free custom roster slot (never RosterCustom0), after asking, with a backup. Enable, disable and uninstall return the game to stock
- CLI `pack-export`, `pack-info`, `pack-check`, `pack-install`, `pack-list`, `pack-order`, `pack-slots`, `pack-enable`, `pack-disable`, `pack-uninstall`
- Tested in a sandbox round trip that booted to the home menu with a pack installed. Not yet played in game with a shared pack

### Roster editor
- New **Roster** tab (Create group), with every player field from the game database and the roster save (field map in docs/ROSTER_EDITOR.md)
- Player list filtered by team, free agents, position and search. One player: portrait (replace or drop a picture), name, number, position, team, overall, archetype, then every field in groups (sliders for ratings, lists for choices, colour swatches, heights as 6'2", money as 12.5M) and "Find a field"
- Several players: ratings up or down, set one field for all, move all to a club
- Move a player to another club; set every line slot (even strength, power play, penalty kill, overtime, shootout, threes)
- Edits are saved to the project as one undo step and reach the game through roster save sync
- Seen in game: an edited name, an edited overall, and a moved player on his new team and line
- CLI `players`, `player`, `players-set`, `ratings`, `archetype`, `move`, `lines`, `jersey`, `roster-league-sync`
- Not yet: adding new players, dragging between line slots

### Uniform slots
- Teams tab, More, **Uniform slots**: a team's kits as tiles in jersey menu order. Rename, reorder, set default and light, replace the jersey sheet, remove a kit
- **Add a slot**: gives a team an extra kit using a kit name the game already knows and no team uses (about 360 free), with its own menu name and gear colours (docs/UNIFORM_SLOTS.md)
- Seen in game: a new Toronto kit listed in jersey select and worn, with its own glove and helmet colours and menu name
- CLI `slots`, `slot-add`, `slot-set`, `slot-remove`

### Franchise mode
- Research in docs/FRANCHISE.md
- CLI `franchise-info`, `franchise-repack`, `franchise-sync` (team names, AHL affiliates, arenas into a Franchise save) and `franchise-immersion` (2025-26 arenas and real insiders for a project). New franchises pick up 2025-26 affiliates and arenas from the roster save. A repacked existing Franchise save has not been loaded in game

### DLSS
- Play tab: a **DLSS (DLAA)** switch when the emulator is the DLSS build (docs/UPSCALING.md). That build is separate and not included. At 1080p it runs as DLAA, NVIDIA's anti-aliasing, for about 1.5 ms a frame

### Interface
- Slim left rail instead of the top tab bar, and an Ice colour palette
- Pinned projects on the start screen, plus start screen fixes
- Buttons are greyed out when they can't be used; the Teams tab's extra panels are in a More menu

### Other
- CLI `texture-import <project> <game> <path> <png>`: a PNG over any game texture, encoded in that texture's own format and size (used for menu backgrounds)

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
