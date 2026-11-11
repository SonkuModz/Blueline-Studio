# Blueline Studio: status

Last updated 2026-11-07. Tested on one dump: NHL 19 PS4, CUSA11126, app version 1.60. **Retail PS4 has never been
tested; all in-game testing was under shadPS4: the fork in the reference folder and, for JSON settings, 0.21.1.**

## How far each proof goes

- **Tested on real files:** an automated test runs against the real game data and passes.
- **Loaded in game:** the emulator log shows the archive was opened, with no new errors or stall.
- **Seen in game:** a person saw the change on screen.

So far only one change has been **seen in game**: a built archive replacing `eng_us.db`, which turned the title
screen French. That archive was a probe made before this tool's builder existed, using the same archive format.

## Modules

| Tab | What works | Proof |
|---|---|---|
| Home | Choose game folder and `sx.exe`, create and open projects, recent projects | Used by the UI tour |
| Assets | Browse and search all 141,769 merged paths (146,832 with the current mods folder), see which archive wins and whether it came from the mods folder, export, jump to an editor. Previews textures, PNG and JPEG, video, fonts and text | UI tour |
| Textures | Preview channels, mips and faces. Export PNG and exact DDS. Import one image or a folder. The game's plain PNG and JPEG files preview, export and import too, staying in their own format and size. Every DDS the game ships previews, including greyscale, float, cubemaps and volumes. Greyscale DDS can be replaced. Float DDS, DDS cubemaps and DDS volumes are preview and export only | Tested on real files. A jersey font import was loaded in game. A greyscale DDS replacement has not been seen in game |
| Teams | Every team from the game's own table, by league, with logo, arena and colours. Thumbnails of its logos, center ice, uniforms, gear, pucks, arena, crowd, mascot, menu art and models. Replace or revert any picture, one image into all square logos, export, folder import, revert the whole team, warnings on files other teams share. 3D uniform: a kit's jersey, pants and socks on the game's own meshes, with jersey size, tuck and pant length, updating as you edit. On a jersey colour sheet, "Make normal and coeff maps" opens it in Jersey Maps with its maps made | Tested on real files. Export, import, revert and the 3D uniform run in the UI tour. None of the team art has been seen in game yet |
| Jersey Maps | Make a jersey's normal (`_nm`) and coeff (`_coeff`, the sm map) maps from its colour sheet or a picture. Starts from a plain fabric base made from the game's own jerseys, the jersey's original map, or a blank you choose. Sliders for depth, edge softness, grain, inner detail, clearing old marks and the coeff grey, with a stitched edge. Compare with the current maps, save both as one undo step in the game's formats, export PNGs, revert. Can be opened from a jersey in the Teams tab | Direction, depth and formats checked against the game's own sheets in tests. Save, revert and opening from Teams run in the UI tour. Not yet seen in game |
| Models | 3D preview with orbit, zoom and pan. Hide shapes. Export OBJ, singly or in batches | Tested on real files: every model parses, OBJ export tested. Read-only |
| Text | Search and edit strings in every language. CSV import and export | Tested on real files. A text edit was loaded in game, not yet seen |
| Database | All tables of all 13 databases, cell editing, meta names | Tested on real files: CRCs valid after an edit. Not yet tried in game |
| Audio | Browse samples. With `sx.exe`: listen, export WAV, replace in the same slot | Decode, encode and replace tested on a real bank. Not yet heard in game |
| Video | All 394 videos, `.mkv` and `.vp6`: play with sound, seek, view the matte, export the original or an MP4. Replace `.mkv` from any video, encoded to match. Replace `.vp6` from an AVI you encode as VP60, keeping its sound and matte. Needs FFmpeg, which you install | Tested on real files. Playback, matte and `.mkv` replace run in the UI tour. `.vp6` repack rebuilds real files byte for byte from their own frames. No replacement has been seen in game |
| Overlays | All 94 on-screen graphics, joined from definition to textures. Preview draws them with the game's textures, fonts, tints, blend modes and clipping on a 1920 by 1080 screen, with sample teams and scores. Movies play frame by frame. Edit widgets by dragging or by property, add, copy and delete them. Replace textures, edit settings, edit layouts, navigation and Lua as text, replace movies | Tested on real files and in the UI tour, and edits build and read back. The score clock looks right against its art, intros less so because their animation is not played. No edit has been seen in game, and files from `boot.big` and `overlays_2x` may not load (RESEARCH 6.9) |
| Build | Change list, per-file revert, build with read-back verification, install, uninstall. Add another mod (a Blueline project, a `.big`, or a folder of them): compares every file, shows conflicts and warnings, takes what you tick as one undo step | Tested on real files, and in a sandbox install. Loaded in game. A combined mod has not been tried in game |
| Play | Game art and details from `sce_sys`, installed mods, keyboard controls, launch and stop, live log, and shadPS4 settings with save, discard and restore, in either settings format | Launch and stop run through the real button. See below |

**Play tab settings.** The tab checks the chosen `shadPS4.exe` to see which file it reads (RESEARCH section 4.3).

- **JSON builds (0.21.1):** the form edits `custom_configs/CUSA11126.json` by default, and can switch to the shared
  `config.json`. It shows the value in effect: the game file's, else the shared file's. Saving writes only the changed
  keys to the chosen file, and creates the game file if needed. Internal resolution and the FPS counter are greyed out
  for the game file, because this build ignores them there.
- **TOML builds (the Shadlix fork):** the form edits the shared `config.toml`, which applies to every game.

Saves change only the values asked for, read the result back first, and keep one `<file>.blueline-original` from before
the first change. Restore puts it back, or removes a game file that Blueline created. Saving is locked while a game
runs, because the TOML build writes to its file when it exits.

Proof, each with a throwaway `user` folder and networking off:

- **JSON, end to end:** the form changed vblank frequency, Play saved it, one line of the game file changed, and the
  emulator logged `vblank_frequency ( 60 -> 144 )` and `Game-specific config used: true`. Play has not been pressed
  with the 0.21.1 build on the real settings, which have networking on.
- **JSON, override seen in the log:** window size, present mode, vblank, FSR, RCAS, HDR, readbacks mode, memory, copy
  GPU buffers, inline fetch shader, fast shader math, patch shaders, archive cache, PS4 Pro, language, volume, log
  detail, ○ confirm, background controller, plus linear images, direct memory, cache and network in the older log.
  This shows the value was taken, not that it changed anything on screen. Display mode was left out of the test so it
  could not take over the screen, and was not exercised.
- **TOML, confirmed read:** PS4 Pro mode, readback speed, readback linear images, direct memory access, vblank
  frequency, copy GPU buffers, FPS limiter and limit.
- **TOML, written and kept by the emulator, effect not confirmed:** every other setting, including window size,
  internal resolution, FSR and console language. The log does not report them, and the game window was not captured.

Overtime Roster also manages `CUSA11126.json`: its `gamesetup.json` records a hash of the file and the network values
it replaced. The file had already changed from that hash before this tab existed. How Overtime reacts to a changed
file has not been tested, so it may rewrite Blueline's edits.

The JSON file's log filter is `*:error`, which hides file opens, so at that level the tab says it cannot count opened
mods rather than reporting none.

Undo and redo work across project edits. The Log panel and a per-session file in `%LOCALAPPDATA%\Blueline\logs`
record operations and errors.

The `blueline` command line (`src/Blueline.Cli`) covers audit, find, extract, project creation, file replacement,
text edits, folder texture import, build, install and uninstall. All have been run against the real game; `set` is
the exception, covered only by the same `ModProject.Set` call that the other commands use. `texture-png`, `db-tables`,
`db-show`, `team-list` and `team-assets` read the game and were used for the team survey. `team-logos` was run once
against a throwaway project.

## Tests

130 test cases in `tests/Blueline.Tests`, all passing on 2026-11-09, in about 2 minutes. They run against the real dump,
stock unless a test is about the mods folder.

- Archive audit and path hash on every entry
- Texture decode, chunkzip, import quality (BC1 59.5 dB, BC3 54.6, BC4 72, BC5 66, BC7 43.3, RGBA8 lossless)
- BGRA8 name plate round trip
- All database CRCs, a database cell edit, localization edit
- Sound bank walk on the largest banks, `sx.exe` decode, encode and same-slot replace
- All models parse, OBJ export, vertex fields split across streams resolve on every shape
- Uniforms: every kit option has its meshes, every NHL club has full kits, UVs on the sheet, pants fit between
  jersey and socks
- Overlay build, sandbox install and uninstall
- Score clock moments: animation script rules and curves, all moments find their parts, power play height, goal length,
  moves follow the layout, per-team logo table
- `param.sfo`, TOML and JSON config editing, format detection on the real builds, per-game layering, value checks,
  backup and restore, play time and key bindings. The real custom config must survive a read and write byte for byte
- Team filing: all 31 NHL clubs have their core art, name matches that needed exceptions, no team collecting another
  from its city, shared files, and the logo set on real textures including its single undo step
- Jersey maps: sheet names resolve, a pressed crest leans the game's way at the game's depth, the plain base drops
  crests, clearing removes a shared mark but keeps the weave, the weave period, and both maps import in the game's
  formats
- Video, using FFmpeg: reading the game's videos and mattes, previews keep every frame and are cached, `.vp6` mattes
  decode, `.vp6` files rebuild byte for byte through an AVI, ill-fitting AVIs are refused, encoded replacements match
  the game's tracks, channels and matte white
- Mods folder: every file its manifest lists resolves to the mods folder, and it can be switched off
- Adding a mod: conflicts stay yours unless chosen, unchanged files are left out, one undo step, later archive wins in
  a folder, built archives round trip, a source that changed after comparing is refused

The UI has a scripted tour rather than unit tests:

```bash
src/Blueline.App/bin/Debug/net10.0-windows/BluelineStudio.exe --game "NHL 19/CUSA11126" --capture <absolute folder>
```

It drives the real controls with real assets, writes PNGs and `tour.log`, and is how `docs/screenshots` was made.

## Limits

- **Loose files cannot be replaced.** The builder refuses them, because their priority against archives is unknown.
- **Overlays:** edits to `boot.big` and `overlays_2x` files have a load order that is not proven (RESEARCH 6.9). The preview does not
  run the game's ActionScript or `animationScripts`, so text, timing and animated states are sample or authored values, except the
  main score clock, whose moments were copied by hand from its code (not compared with the running game, RESEARCH 6.9). A movie's
  internal animation cannot be edited in Studio, only exported and replaced. Lua is not syntax checked. Editing by form rewrites
  a file's JSON spacing and collapses repeated keys, keeping the meaning.
- **Textures:** cubemaps, texture arrays, BC6H and BC2 preview where decodable but cannot be imported.
- **Audio:** a replacement must fit the original sample's slot. Needs the user's `sx.exe`, which is not bundled.
- **Models:** no writing back. The Models tab is grey because an `.mcd` holds no material data; only uniforms are
  textured, by name (RESEARCH section 6.6). Previews stop at 1.5 million triangles and say so. 20 vertex streams are
  still unexplained (RESEARCH section 6.3).
- **3D uniform:** no numbers, names, normal maps or shine, and gloves, skates and the body are grey or absent. It is a
  check of the sheets on the game's meshes, not a picture of the game.
- **Jersey maps:** none have been seen in game. What each coeff channel does in the lighting is unknown, so raised
  areas get one grey, as the game's crests do. Clearing can leave a faint line of a mark the base shares, such as the
  adidas wordmark at the Toronto collar. A hand-made blank avoids it. Making a pair takes about 4 seconds.
- **UI files:** `.gfx` and `.swf` have no preview. Export and replace work at the file level only.
- **Video:** needs the user's FFmpeg, with `ffprobe.exe` beside it. The player shows a converted copy at up to 1280
  wide, and `.vp6` sound cannot be previewed. FFmpeg has no VP6 encoder, so a `.vp6` replacement needs an AVI encoded
  as VP60 with the On2 VFW codec, at the original's exact size and frame count. That route is tested only with the
  game's own frames. No replacement has been played in game (RESEARCH section 6.8).
- **No editor at all:** `.cba`, `.vlt`, `.ply`, `.rsf`, `.crowd`, `.skel`, `.shst`. Whole-file export and replacement
  only.
- **Team filing:** logos and center ice come from the team table's art id. NHL clubs' uniforms, gear and arena come
  from a file name looked up by art id, so renamed clubs keep their files. Other teams match by name, with a short list
  of hand-checked exceptions (RESEARCH section 6.4). A club on a reused slot also lists that slot's stock files:
  Kraken shows the stock `adidas_pre_order` kits below its modded ones. Anything named
  differently appears under Assets only. Which picture the game draws on which screen has not been checked in game.
- **Replace logo set** leaves alone non-square files, special HUT cards (rare, playoffs, free agency, heritage,
  stadium) and custom logo layers, and writes mono logos only from a picture with transparency.
- **Mods folder (`CUSA11126-mods`):** Studio reads it over the game, so what you see and edit starts from the mod,
  and "original" means the mod's version. Blueline still installs into the game's own `databigs`; whether the game
  loads that alongside a mods folder has not been tested (RESEARCH section 6.5). Close Studio before reinstalling the
  other tool's mods, then reopen it to see them.
- **Combining mods:** works per file, never inside one. If two mods changed the same database or language file, one
  whole file wins and the other's edits to it are lost; the review says so. The mods folder cannot be added, because
  Studio already reads it under every project.
- **New paths:** the builder warns about them, because whether the game looks a given new name up is unproven.
- **Nested archives** (`faceposer.big`, `gps.big`, `proam_download.big`) are not opened by the file system.
- **Rosters:** saves may override database edits, as seen in old NHL 21 work. Not tested on NHL 19.
- **Install limit:** the game copies at most 50 names per wildcard, with no bounds check seen, so the tool refuses
  to go past it.

## Decisions

| Decision | Why |
|---|---|
| New .NET 10 and WPF project, nothing reused from the old one | Old code was one huge window and edited game archives in place |
| Mods are new `patch_*_99_*` archives | Originals are never written, so uninstalling is deleting one file, with no 33 GB backup |
| Projects store changed files by content hash | Undo, revert and rebuild without touching the game |
| No Sony SDK tools, and EA's `sx.exe` is user-supplied | Licensing. Texture work is fully native |
| FFmpeg is found on the user's PC, not bundled | Same reason as `sx.exe`. Windows cannot be relied on to play VP8 or VP6, so previews are converted to H.264 |
| Models read-only for now | Writing unproven geometry would be claiming support that has not been tested |

## Remaining

1. A person presses a button at the title screen, stock and with an overlay, to settle the "frozen" report.
2. See the tool-built text and texture edits on screen, and try a database edit and an audio replacement in game.
3. Try an install on a retail PS4 (PlayGo and `sce_discmap` behaviour are unknown).
4. See a center ice and a team logo change in game, to confirm the filing matches what the game draws.
5. Choose the next format to research. Texture links for models and Scaleform `.gfx` previews look most useful.
