# NHL 19 (PS4, CUSA11126) research findings

Everything marked **verified** was reproduced from the bytes of the supplied dump
(`NHL 19/CUSA11126`, app version 1.60, build CL 3445259) by scripts written for this project, not taken
from earlier documents. **Observed** means consistently true but the mechanism is unconfirmed.
**Unknown** means exactly that. Nothing here is copied from another EA title's documentation as fact.

## 1. What was supplied

`references/` contains one note pointing at `Downloads\NHL 19 Mod Bench`, the previous project.

| Item | What it is | Verdict |
|---|---|---|
| `docs/*.md`, `AUDIO.md` | Format notes | **Written against NHL 21 (CUSA18182), not NHL 19.** Useful leads only. Several claims are false for NHL 19 (section 8) |
| `src/`, `tests/` | "Nhl19BigStudio": C#/WPF, CLI of ~90 KB, `MainWindow.xaml.cs` of 217 KB | Not reused. Monolithic, and writes into the game's own archives |
| `references/PS4 GNF Studio` | Separate GNF tool plus test fixtures | Shells out to Noesis / `orbis-image2gnf`. Not needed (section 4) |
| `references/PS4 GNF Studio/References/Bunch Of PS4 Tools` | Sony SDK binaries (`orbis-*`, `libSce*`) | **Not used or bundled.** Proprietary, licence unclear, unnecessary |
| `references/PS4 GNF Studio/References/Noesis` | Third-party viewer | Optional oracle for checking decoders only |
| `references/Soundtrack Editing Toolkit` | `sx.exe` (EA Sound eXchange) and DLLs, foobar2000, vgmstream | `sx.exe` is the only known EAOpus/EASpeex encoder here. EA-owned, so the user points the app at their copy; it is not bundled |
| `references/export and reimport quickbms scripts` | Generic EB `.bms` script | Confirms the container shape. Its "CRC" is not interpreted |
| `references/NHL21-Localization-Developer-Handoff` | Precise TDB localization spec + reference JS | Excellent spec; re-verified on NHL 19 where used |
| `references/NHL 21 Intel` | Audits of NHL 21 catalogs | NHL 21-specific; not used |
| `references/NhlEditor19` | PyQt editor over UTF-16 TSV exports of `exhibition*` tables | Works on text exports, not on game files. Useful column names |
| `ShadPS4/Shadlix-NHL-*` | shadPS4 fork build | **Used as the in-game test bench** |
| `publish/`, `scratch/`, `build-shadps4*` | Build output, experiments, emulator source (1.7 GB rar) | Scanned for leads only |

## 2. Platform

PS4 only. Evidence: `sce_sys`, `sce_module`, `.prx`, GNF textures, `titleinfo.ini` (`na_titleID=CUSA11126_00`),
`eboot.bin` source paths `E:/P4/NHL19/1.6.0/...`, build target `kettle` (EA's PS4 name, also in paths
such as `nhl/data/kettle`). `nhl/data/common` = shared across platforms, `kettle` = PS4-specific.

`eboot.bin` here is a **plain decrypted ELF** (`7F 45 4C 46` at offset 0, 100,369,766 bytes, first LOAD
at file offset `0x4000`, so `file = vaddr + 0x4000` for code/rodata). The old doc's "fake SELF, ELF at
`0x160`, offset `0x4160`" describes NHL 21 and is wrong here.

## 3. `.big` (EB) archives: verified on all 28 archives, 152,659 entries

All integers big-endian.

```
0x00 "EB"  0x02 u16 version=3  0x04 u32 fileCount  0x08 u32 flags
0x0C u32 nameTableOffset  0x10 u32 nameTableSize
0x14 u8 fileRecordLen  0x15 u8 folderRecordLen  0x16 u16 folderCount
0x18 u64 archiveSize (always equals file length)    0x20..0x2F zero
0x30 file table
```

File table row (20 bytes with flag `0x10000`, otherwise 16):

| Off | Field | Meaning |
|---|---|---|
| +0 | u32 | data offset in 16-byte blocks (64 if flag `0x200`; never set in NHL 19) |
| +4 | u32 | compressed size (0 in all databigs; used by `faceposer.big`, `proam_download.big`) |
| +8 | u32 | size on disk when +4 is 0 |
| +12 | u32 | **high 32 bits of the path hash** |
| +16 | u32 | **low 32 bits of the path hash** |

### The path hash: the key finding

```
h = 5381; for each byte c of the path: h = h * 33 + c   (64-bit wrap-around)
```

The path is `folder + "/" + name`, as stored (all lowercase in the databigs).
**152,659 of 152,659 entries match.** Rows are **sorted ascending by the u64 hash** in every databig
(`faceposer.big` with its 16-byte rows is the exception; it stores only 32 bits and is not sorted by them).

The old project called this field "not a CRC, a group id, a writer does not need to recompute it" and noticed
a "constant 1,185,921" in EXTRA. That constant is 33⁴: the hash of the last characters. Because of this a
**rename or new file requires a correct hash and a re-sort**, which is likely why old rename experiments
behaved unpredictably.

### Name and folder tables

At `nameTableOffset`: `fileCount` records of `fileRecordLen` = `u16 folderIndex` + NUL-padded name,
rounded up to 16, then `folderCount` records of `folderRecordLen`. In real archives
`fileRecordLen = longest name + 3` and `folderRecordLen = longest folder + 1`. File data follows; every file
ends on a 16-byte boundary.

### Flags

| Flags | Archives | Effect |
|---|---|---|
| `0x00110400` | All databigs, `gps.big` | 20-byte rows, hash present and sorted |
| `0x00200400` | `faceposer.big` | 16-byte rows, uses compressed-size field |
| `0x00100400` | `proam_download.big` | 16-byte rows |

Meaning of `0x400`, `0x100000`, `0x200000` individually: **unknown**.

### Deduplication

Base archives dedupe (`game_90-006`: 79,391 entries, 64,380 unique offsets); **patch archives never do**.
No archive has a partial overlap. Writing in place into a shared block corrupts siblings, one more reason not
to edit archives in place. Zero-length entries share the next file's offset; that is not an overlap.

### Payload wrapper: `chunkzip`

```
"chunkzip" u32 version(<=2) u32 fullSize u32 chunkSize(0x20000 on all GNF) u32 chunkCount + 16 reserved
each chunk: align so pos%16==8, u32 compressedSize, u32 flag (4 = stored), raw DEFLATE
```

Verified by decoding entries. All 131,820 non-empty `.gnf` entries use this form. Also seen, stored raw:
`.sbs`, `.sbr`, `.db` (patch versions), `.mkv`, `.gfx`, `.png`, many `.json`/`.lua`. Whether an entry is
wrapped varies per path and per archive, so a tool must detect it from the bytes.

## 4. Archive mounting: how mods can install (verified in code and in game)

Disassembly of `eboot.bin`:

1. `0x2AEE655`: mounts `<root>/databigs/inxp*.big`, then `0x2AEE68A` calls `0x37FDB30("patch_inxp")`.
2. `0x2B25625` etc: `game*.big`, then `patch_game` the same way (later, on `SimThread`).
3. `0x37FDB30` formats `"%s*%s"` with `.big`, lists the directory, copies names into a **fixed array of
   50 256-byte buffers (`0x32`)**, sorts them ascending with `strncmp`, and mounts in that order.
   **No bounds check was seen on the 50 limit**, so more than 50 matches may corrupt the stack: enforced as
   a hard limit by this tool. `boot.big` and `overlays_2x.big` are named explicitly.

**In-game test, 2026-11-06.** An archive `patch_inxp_99_probe.big` built from scratch mapped
`nhl/data/kettle/loc/eng_us.db` to the game's own French data, on a hardlinked install, under shadPS4.

- The emulator log shows it opened immediately after `patch_inxp_56.big`, by wildcard alone.
- The title screen read **"APPUYEZ SUR ⊗"** in place of English (user's screenshot).
- No original file changed.

So: **last mounted wins, and a mod can ship as a new archive.** This is the foundation of the application.

**Open:** the user reported the game "frozen" on that screen. The logs show no overlay-caused stall, but input
was never tested (see section 4.1).

### 4.1 Stock and overlay comparison, 2026-11-06

Two runs of 150 seconds each under shadPS4 on the hardlinked install, compared by log only: one with no overlay,
one with archives built by `blueline install` (one text edit, one jersey texture). No input was sent and the
game window was not captured.

| | Stock | Overlay |
|---|---|---|
| Log lines | 7,288 | 7,513 |
| Error lines | 388 | 389 |
| Files named in the log (unique) | 73 | 75, the extra two being the overlay archives |
| Last file named | `icon_saving_bootflow.gnf` | Same |
| Image sync counter at end | 131,072 syncs | 131,072 syncs |
| Audio decoding | Still running at end | Still running at end |

Both overlay archives opened after the last stock archive of their family. The only differing error lines are
different counts of one stubbed network call (`sceNpWebApiCreateContextA`), plus one `sceNpWebApiCreateRequest`
in the overlay run. So **the overlay did not stop the engine, and did not change what it loaded next.**

Not established: whether the title screen responds to a button, in either run, and whether the edited text and
texture appeared on screen in this run. The earlier French test is the only on-screen confirmation so far.

Also seen in the eboot: `chunkdefinition_patch.json` (chunk IDs for `PTH1`..`PTH6`), read at `boot.big` level.
Whether a retail PS4's PlayGo chunk layer would accept a new file is **unknown**; only the emulator was tested.

Priority in the 6,863 paths present in more than one archive confirms the model; for instance
`generic_player_60120.mcd` is in five archives and `eng_us.db` in seven with different sizes each.

### 4.2 This shadPS4 build

**Verified by test:** a `user` folder in the working directory takes priority over `%APPDATA%\shadPS4`, as the build's
README says. Without one, as now, the emulator uses `%APPDATA%\shadPS4`. On exit it wrote new `windowPosX` and
`windowPosY` values to `config.toml`; nothing else differed. Keyboard ⊗ is `kp2` or `n` in `input_config/default.ini`,
and `global.ini` holds only hotkeys. Enter is Options, not ⊗.

`param.sfo` declares its title as UTF-8, but NHL 19's holds a bare Latin-1 `®`.

### 4.3 JSON settings in shadPS4 0.21.1, 2026-11-07

Tested on the build in `%APPDATA%\OvertimeRoster\boost`, hardlinked into a scratch folder with its own `user` folder
and networking off. Its real settings files kept the same hashes.

**How to tell the builds apart:** both can share one settings folder, which here holds both `config.toml` and
`config.json`, so the folder does not say which is in use. The 0.21.1 program contains the string `readbacks_mode` and
the Shadlix program does not, while `readbackSpeedMode` is the other way round.

**How the files combine, verified by test:**

- A `user` folder in the working directory takes priority here too.
- A per-game file can be partial. One holding two keys gave exactly two lines under `Game-specific overrides applied`.
- Each override is logged as `key ( shared -> game )`, and only when it differs from the shared value.
- The emulator did not rewrite either file during a 25-second run that was then ended by force. A clean exit was not tried.
- Internal resolution and `show_fps_counter` produced no line when changed, so this build ignores them per game.
- `Log.filter` set to `*:error` removed every `open: path` line from both stdout and `shad_log.txt`. `*:info` showed 147
  in the same time. Those lines are `Kernel.Fs` at Info level.

**Unknown:** what the three `readbacks_mode` levels are called. Neither program holds names for them, so the form says
Level 0, 1 and 2. `userfaultfd` was not offered, because its test value matched the shared value and proved nothing.

## 5. Textures: `.gnf`

Survey of **all 131,822 `.gnf` entries**: 130,305 are GNF, **1,515 are actually DDS with a `.gnf` extension**
(for example `czjersey/czjersey_25_name.gnf`), 2 are empty. Never trust the extension. Those DDS files are all
uncompressed BGRA8 (red mask `0xFF0000`), such as the jersey name plate `name_rbk_chicago_away.gnf` at 256x256.

The files named `.dds` vary more. The merged game has 1,431 DDS textures under `.dds` and `.gnf` names, and a survey
on 2026-11-08 found these beyond BGRA8 and BC blocks: 7 are 8-bit luminance (`postfx/burn_*`, `hair_*`, `noise_64`),
and `rendering/brdfggxlut.dds` is RGBA 32-bit float (DXGI 2), with red and green as the lookup and blue around 1, so it
looks magenta. Six are cubemaps (`indoorsky.dds` and the five BC6H `brdfdefault*` and `skyhdr` files), with each face
holding a full mip chain. Two are volumes: `colourcube_gamma.dds` (32 slices) and `loading_stadium.dds` (120 slices).
File sizes match these layouts exactly.

Number and name fonts (`font_*_cm.gnf`) keep their shapes in alpha over a flat colour, so a preview without
alpha is a blank sheet.

GNF layout (little-endian): `"GNF "`, u32 contentsSize=248, u8 version=2, u8 count=1, u8 align=8, u8 0,
u32 stream size, 32-byte Gnm texture descriptor at `0x10`; pixel data starts at **`0x100`**.

| Descriptor | Bits | |
|---|---|---|
| word1 | 20..25 / 26..29 | data format / number format |
| word2 | 0..13 / 14..27 | width-1 / height-1 |
| word3 | 12..15, 16..19 / 20..24 / 28..31 | base, last mip / tile mode / type |
| word7 | | total pixel data size |

| Format (dfmt, nfmt) | Count | Use |
|---|---|---|
| BC1 sRGB (35,9) | 63,214 | colour |
| BC7 UNorm (41,0) | 33,024 | UI, portraits |
| BC7 sRGB (41,9) | 7,442 | UI, portraits |
| BC5 SNorm (39,1) | 7,431 | normal maps |
| BC3 sRGB (37,9) | 7,258 | colour + alpha |
| BC1 UNorm (35,0) | 6,241 | data maps |
| BC3 UNorm (37,0) | 2,352 | |
| BC6H (40,0) | 2,077 | lightmaps, cubemaps |
| BC4 UNorm (38,0) | 1,257 | |
| RGBA8 (10,*) | 9 | |

Types: 2D 130,198; cube 90; 2D array 17. **Tile mode is 13 (`Thin_1dThin`) on all 130,305.**
No 2D macro-tiling exists in this game, so no GPU address library is required.

**Layout, verified:** elements (4x4 blocks) are tiled in 8x8 micro-tiles, Morton order inside each tile, tiles
in raster order. Every mip is padded to multiples of 8 elements, largest mip first, with faces/slices
consecutive. Predicted size equals `word7` for BC1 2048² 12 mips (2,798,592), BC7 332x464 (168,960), BC7 512x340
(180,224), BC6H cube 128² (98,304). A St. Louis jersey detiled with this rule renders perfectly.

**Team kit naming, observed:** `characters/player/{jersey,pant,sock,font,name,helmetlogo}_<brand>_<team>_<variant>_{cm,nm,sm,coeff}.gnf`,
brand `nineties`/`rbk`/`adidas`, variant `home`,`away`,`two`,`three`... UI: `nhl/ui/kettle_assets/teamlogos`,
`teamjerseys`, `playerportraits`. Arena: `rendering/stadiums/<arena>/`, lightmaps `lm_*`.

## 6. Other formats (survey of the databigs)

| Type | Count | MB | State |
|---|---|---|---|
| `.gnf` | 131,822 | 30,436 | Decoded, section 5 |
| `.mkv` | 344 | 7,272 | WebM-typed Matroska, VP8 and Vorbis, section 6.8 |
| `.sbs` / `.sbr` | 63 / 83 | 3,841 / 659 | Audio, section 6.1 |
| `.mcd` | 7,416 | 1,963 | Models, section 6.3. Geometry reads; writing untested |
| `.cba` | 1,981 | 752 | Animation. **Unknown** |
| `.vp6` | 59 | 218 | `MVhd` EA video with a matte stream, section 6.8 |
| `.db` | 61 | 210 | TDB, section 6.2 |
| `.gfx` / `.swf` | 1,024 / 50 | 12 | Scaleform (`CFX`, `CWS`), zlib body |
| `.lua`, `.json`, `.xml`, `.csv`, `.layout`, `.menu`, `.vvm`, `.lreq`, `.nav`, `.dd` | ~6,000 | | Plain text; the whole UI is scriptable |
| `.vlt` / `.bin` | 213 / 193 | | Attribute vault, keys unsolved in old work |
| `.ply`, `.rsf`, `.nav`, `.crowd`, `.skel`, `.shst` | | | **Unknown** |
| `.ttf` | 26 | 1.5 | Fonts |
| nested `.big` | 58 | 20 | EB archives inside archives (`music_ptc.big`) |

Loose files: `nhl/` on disk mirrors `inxp_00.big` (2,329 of 2,334 paths). The game's boot log also opens some
loose config directly. Precedence of loose vs archive for other files is **unknown**; do not rely on it.

### 6.1 Audio

**No `.abk`** in NHL 19. Banks are `.sbr` (`SBle`, index) + optional `.sbs` (`data`, samples), stored raw.
First sample header in the 58 `.sbs` files: `1C` (EAOpus) in 46, **`19` (EASpeex) in 12**. So the old doc's
"all EAOpus" is not true here. The nordic bank starts `48 00 00 0C 1C 04 BB 80 40 A2 7E 36`, matching its
block layout (`0x48` header, `0x44` data, `0x45` end).

**Verified:** walking those blocks accounts for 99.8 to 100% of the bytes in the 8 largest banks, for example
`pbp_samples_inxp` at 715 MB with 36,677 samples. A real sample from `fesfxbank.sbs` decoded through `sx.exe`
(22.61 s to a 4.3 MB WAV), re-encoded at quality 75 to fit its slot (547,376 of 655,980 bytes), and went back into
the bank. Same-slot replacement has not been heard in game. How the game seeks inside a bank is unknown, so a
sample cannot grow.

### 6.2 TDB databases

`44 42 00 08`, little-endian. Verified on NHL 19 with the MSB-first CRC-32 (poly `0x04C11DB7`, init `0xFFFFFFFF`, no
final XOR):

- `eng_us.db`: file header, directory, table header and body CRCs **all match**. 38,001 rows, not NHL 21's 55,766.
- `nhl_game.db`: 111 tables; header CRC and directory CRC (`[24, 24+8n)`) match.

Schema is `nhl_game-meta.xml` in `inxp_00.big`, which names all 111 tables. Its attributes are lowercase
(`shortname`) and the file starts with a BOM. Bit fields are read least significant bit first.

**Verified:** per-table CRCs for the multi-table layout hold on all 13 databases, including all 224 in
`nhl_game.db`, and stay valid after a cell edit through this tool. Localization key hashes match all 38,001 rows.

### 6.3 Models: `.mcd`

Header `RSF\0`, chunks with reversed tags (`TIEW`). **Verified:** the parser reads all 5,726 distinct models found,
81,459 shapes, with shape binding records in every file and no need for the fallback that pairs buffers by order.
Attribute encodings seen: `2f16`, `4f16`, `4s16n`, `3f32`, `4u8n`, `4f32`, `2s16`.

**Verified, 2026-11-07: vertices can be split across streams.** The third field of a format token is a stream slot:
in `t0:00:01:...:2f16`, texcoord 0 sits at offset 0 of the shape's second stream. A binding record lists all of a
shape's streams, and for pants, socks, gloves and skates the first holds positions (12 bytes) and the second the rest.
The format a binding names usually fits its streams slot by slot. Helmet visors and player heads name one that does
not, while exactly one other format in the file fits, and that one is used. With this, every one of 81,459 shapes has
strides that add up, 81,439 have UVs, and of the 3,049 streams that match no format by themselves, 20 are unexplained.
Positions were never affected: they are always first in the first stream.

An `.mcd` holds no material data, and what links a model to its textures in the game is unknown. Some models that
look incomplete are real data, such as `m_avg_hs2_anaheim_duck_hat_lod0.mcd`, a head, hat and two hands with no body.

### 6.6 Uniforms on their meshes, 2026-11-07

Uniform meshes are named for the maker, like the sheets: `jersey_skater_{adidas|rbk|nineties}_{notuck|lefttuck|
righttuck|fulltuck}_{large|small}_high.mcd`, `jerseytie_skater_{maker}_high.mcd` (the lace patch, on the jersey
sheet), `pant_player_{long|short}_high.mcd` and `sock_player_{notuck|fronttuck|backtuck|fulltuck}_high.mcd`. Pants and
socks are shared by all makers. All sit in one pose and fit together as they are, with the jersey over the pants and
the socks inside them. Their UVs cover the whole of `jersey_`, `pant_` and `sock_{maker}_{club}_{kit}_cm.gnf`.

**Seen by eye** in Studio, not in game: Toronto home and away and the modded Kraken alternate draw correctly, crest on
the chest and stripes on sleeves and socks, and a test pattern wraps the jersey sheet without seams out of place.
**Assumed:** that the game pairs them the same way. Numbers and names come from `font_` and `name_` sheets placed by
something not yet found, perhaps the jersey's second UV set (`t8`, 4 bytes); they are not drawn. Which tuck the game
pairs with which sock or skate is a guess. All 31 clubs have pant and sock sheets for every kit, 379 kits in all.

### 6.7 Jersey normal and coeff maps, 2026-11-07

Every jersey colour sheet `jersey_{maker}_{club}_{kit}_cm.gnf` has two partners in the same folder, `_nm` and `_coeff`
(the "sm" map in older modding talk).

**Measured on the game's sheets:**

- **`_nm`:** BC5 SNorm, 2048 square, 12 mips. Decoded, red and green hold the surface direction (128 is flat) and blue
  is 0. A raised crest edge reads as red = 128 − k·dh/dx and green = 128 + k·dh/dy, with y running down the sheet.
  This was fitted on the template sheet and Rochester's crest.
- **`_coeff`:** BC1 sRGB, 2048. Plain fabric is about (16–27, 16–27, 0). The game's crests are one flat grey in all
  three channels: 79 on the template and 46 on Rochester. Blue is non-zero only on the crest.
- **Shared adidas maps:** most adidas kits share one nm and one coeff sheet that still carries the adidas mark, so a
  new crest drawn over it shows the mark behind.
- **Plain base:** a per-pixel median of 15 of a maker's sheets (one per stored size, the template left out) drops the
  team crests and keeps the folds, seams and weave. On Rochester's crest area, detail falls from 4,699 to 540.
- **Weave:** repeats every 6 px at 2048.

**How Blueline makes them** (`Teams/JerseyMaps`):

- **Raised areas:** taken from the colour sheet, as everything not the main colour, or from a picture.
- **Normal:** the raised areas are pressed in with a one-pass bevel, a dashed stitch about 2 px outside the edge and
  fine grain inside. The depth is scaled so the strongest edges match the template's: 102 against the game's 120 in
  the test.
- **Clearing:** around the design, the base is smoothed and an averaged weave is put back. This takes the adidas mark
  from 3.56 to 0.75 in the test, while the weave's strength around it stays at 1,815 against 1,765.
- **Coeff:** raised areas get one grey.
- **Saving:** both maps are saved in the stock files' own formats.

**Not known:**

- What each coeff channel does in the game's lighting.
- Whether a grey other than the game's own 46–79 looks right.
- Whether any made map looks right in game. None has been loaded yet.
- A faint line of the old adidas wordmark can remain at the Toronto collar. A blank base made by hand avoids it.

### 6.8 Video, 2026-11-08

**Measured on samples from each folder, with FFmpeg 8.1:**

- **`.mkv`:** EBML doctype `webm`, muxed by `rwmovieencoder`. VP8 video and Vorbis audio at 48 kHz, in SimpleBlocks. No
  Cues and no SeekHead, and a new cluster only about every 32 s, when 16-bit block times would run out. Frame rate
  about 29.97. `press_start_reveal` has 7.1 sound, the training videos stereo, and commentary mono; scenic videos have
  none.
- **Commentary mattes:** `context_*` files have a second VP8 track, language tag `EAA`, holding a black and white
  matte of the commentators, with the same frame count as the picture.
- **Seeking:** every frame decodes cleanly from the start of a file. Starting FFmpeg at a seek point gave decode
  errors, so previews always read from the start.
- **`.vp6`:** chunks of a 4-byte tag and a little-endian size that includes the 8 header bytes, every size a multiple
  of 4. `MVhd`, then `ALhd`, then `SCHl`, then frames as `MV0K`/`MV0F` and `AL0K`/`AL0F` with `SCDl` sound chunks
  between them. All 4 samples have the `AL` matte stream. `MVhd` holds `vp60`, width, height, frame count, then rate
  and scale. A key frame has the top bit of its first byte clear.
- **FFmpeg and VP6:** it reads only the `MV` stream and does not expose the `SCHl` sound. Relabelling the `AL` chunks
  as `MV` makes it decode the matte, which lines up with the picture by eye. It has VP6 decoders but no VP6 encoder.

**How Blueline replaces them:**

- **`.mkv`:** FFmpeg encodes VP8 and Vorbis in WebM at the original's size, rate, tracks, channels and about its
  bitrate, with clusters of up to 32 s. The result is read back and refused if it differs. FFmpeg still writes Cues
  and a SeekHead, which the game's files do not have. A file with a matte gets one from the source's transparency, or
  solid white. The game's mattes use the full 0-255 rather than video range, so Blueline stretches them to match.
- **`.vp6`:** frames come from an AVI encoded as VP60 with the On2 VP6 VFW codec, for example in VirtualDub. They go
  into a copy of the game's file, keeping the headers, sound and, unless a second AVI is given, the matte. Frame count
  and size must match. Passing a game file's own frames through an AVI rebuilds it byte for byte, on 6 files.

**Not known:**

- Whether the game plays any replacement. None has been loaded yet.
- Whether the game needs the Cues and SeekHead to be absent.
- Whether an AVI from the VFW codec itself is accepted. The repack was tested with the game's own frames, because the
  codec has not been installed on this PC. `references/Programs` has it, and has not been run.
- What the game does if a replacement's length differs. Studio notes a difference of over half a second.

### 6.9 Overlays, 2026-11-08

The chain, from the game's own files. Everything but the movies and textures is plain JSON or Lua.

| File | Holds |
|---|---|
| `nhl/ui/definition.overlays` | 69 overlays: name, show and hide actions, lifetime (-1 means until hidden), visibility, and data entries |
| `nhl/ui/features/overlays/*.nav` | 86 state machines. `["loadVVM",["/features/overlays/vvms/.../x.vvm"]]` names a bundle |
| `.vvm` | `dds` data files, `vms` Lua scripts, `views` layouts |
| `.layout` | Widget trees. `commonName` is the widget type |
| `nhl/ui/definition.kettle.assetpaths` | Asset names to paths. `#` is a number the game fills in, `img://X/` a resize rule, `[loc X]` and `[sku]` variants |
| `nhl/ui/generated_widget.manifest` | Custom widgets to their layouts or ActionScript classes |
| `nhl/ui/default.constants` | Colour names such as `$COBALT_BLUE` |
| `nhl/ui/fonts/definition.fonts` | Font names used by layouts to the game's `.ttf` files |

Some layouts repeat a key inside one object, and one layout is empty. `JsonEdit` keeps the last value of a repeated key.

Layout keys the preview follows: `x`, `y`, `width`, `height` (`AUTO` means from the content), `alignH`, `alignV`, margins,
`scaleX`, `scaleY`, `alpha`, `visible`, `rotation`, `color`, `fillColor`, `fontFace`, `fontSize`, `fontColor`, `textAlignH`,
`textAlignV`, `forceCaps`, `leading` (between written lines only), `resizeType` (`COMPRESS` squeezes text to fit), `clipToBounds`, `mask` (the name of another widget, whose rectangle cuts this
one), `blendMode` (`ADD`, `MULTIPLY`, `SCREEN`). Children draw in file order, first at the bottom. Most `designSize` values
are 1920 by 1080, and the score clock's own `x` and `y` place it at the broadcast corner, so the preview uses that screen.

**Styles.** A widget's `styles` array names entries in the layout's own `style-definitions` or in `nhl/ui/definition.styles`
(238 shared). They set any property, mostly `fontFace`, `fontSize` and `fontColor`. 4,160 labels use them. The preview takes the
widget's own value first, then the last style in the list that sets the key. That order is inferred: where a widget and its style
disagree (99 of 105 cases) the widget's value looks hand-picked, such as 150 over a style's 125. The manifest's Label defaults are
`fontSize` 20, `textAlignH` LEFT, `textAlignV` TOP, `resizeType` NONE.

**Where text sits.** WPF places a line using a font's Windows ascent (0.964 of the size for NBCSN_Frank), while the game's position
fits the `hhea` ascent (0.750) plus Flash's 2 pixel gutter: baseline = box top + 2 + ascent x size. Drawing with WPF's own value put
scorebug text 8 to 11 pixels low. Compared with the in-game score clock (one screenshot, where one row is about 2 layout pixels), the
score, abbreviation and period now land within about 2 layout pixels of the game. Top alignment is what that shot covers. `CENTER`
and `BOTTOM` follow the same rules (block height = (ascent + descent) x size) and have not been compared with the game, nor has any
font other than NBCSN_Frank. The horizontal 2 pixel gutter is taken from Flash and not measured. `tracking` is not drawn, and text does not wrap.

What the preview assumes, because the code that sets it is compiled ActionScript in the game and is not run:

- A group with `authoringData.visible` false stays off until asked for. The score clock's drop-downs and goal animation are
  marked this way. Labels marked this way are drawn, because the clock's period and time are, and they do show in play.
- Empty labels get sample text chosen from their name. Panels named `tint` under a `home` or `away` group get the team colour,
  and `bottomGlow` the same colour.
- Measured from one in-game screenshot of the score clock (Calgary at Vancouver), and so approximate: panels are about 0.52
  of the brightness in the team table.
- `animationScripts` are not played, so an overlay that animates in appears at its authored starting state. Intros
  (TeamIntro and similar) look further from the game than fixed graphics such as the score clock. The score clock's real
  animations are in code, see below, and the preview plays those.

**How the score clock moves.** The layout's `animationScripts` only hold a 125 ms fade. Everything else is ActionScript in class
`nhl.overlays.scoreclock::ScoreClockOverlay`, inside `nhl/ui/lib/nhluidata.swf` in `boot.big`, on the engine `zinc.ui.anim` and
`zinc.utils.Easing` from `nhl/ui/lib/external/zinc.swf`. The other clocks (Proam, Threes, Gauntlet) are separate classes and are
not previewed. Times are written in 30 per second frames. `OverlayScript` copies the engine's rules: an animation starts at a
cursor without moving it, a delay moves it, a wait moves it to the end of everything so far, and a sub-script extends the total.
An animation takes its start value from the widget at the moment it starts. `ScoreClockScene` holds the transcribed moments:

| Moment | Summary |
| --- | --- |
| Clock in | 0.75 speed. Slides from 380 left, cap and mask open, panels and text follow in a stagger |
| Goal | Team's bar fades in over 30 frames, banner opens in 10, streaks, text tracks from 10 to 25 and logo scales up over 82, then an end sweep across the team's panel. About 5.4 seconds |
| Power play | Bar fades in 10 frames, bar and tab drop 35 in 5, logo mask grows from 55 to 71, labels slide in. "POWER PLAY", or "5 ON 3" style when strengths differ by more than one or are equal |
| Short drop-down | Same shape, 35 tall, or 60 with a second line. Delayed penalty uses fixed colours 0xEDB21C and 0xC8A316 with black text |
| Long message | The short shape, then both bar and panel widen to 742 over 7 frames, and it holds 150 frames |
| Clock message, stats, shootout | Under the clock, across 495 and 45 tall with 86 wide bars, and 494 wide at 0.8 speed |

Each team's small logo uses tables in the same class (`_logoScale`, `_logoXPosition`, `_logoYPosition`, copied to
`ScoreClockLogos`), with defaults 0.57, -8, -9. The code sets the drop-down tint and `logoFader` to the team's primary colour, and
the `blackGradientCover` darkens it.

*What a custom scorebug must keep.* The code finds parts with `getNodeByName`, so names and nesting have to stay. On load it reads
these from the layout: `scoreClock.x`, `capLeft.x`, `capRight.x`, `contentsMask` x and width, `contents.x`, `home`, `away` and `clock`
x, `goal.width`, and `logoHolder` x and scale. These are written in code and will not follow a moved layout: team panel start x
-247, clock start -249, scoreClock start 380 left of its place and hide 257 right, capRight start 6, capLeft hide 746, mask hide 753,
goal bar widths 743, 248 and 247 with x 248, power play label x 157, 177, 10 and 0 with widths 125 and 250, drop-down heights 35 and
60, logo mask heights 55 and 71, long drop-down 742 wide with text at 238, stat bars 86 wide at 408, shootout positions, width 494.

*Not settled, none of it compared with the running game.* Only the in-play clock has an in-game screenshot to compare with. Player
names and messages are sample text. The two line drop-down's tint stays 35 tall in a 60 tall tab because nothing found resizes it,
so its lower half may look different in game. Shootout totals sit left of centre in their boxes because the code places their auto
sized labels at fixed x values, unproven. Sponsor ads, the top message and out-of-town scores are not played. In a game the code
also queues and combines these moments (a goal during a power play, for example), and the preview plays each alone. The streak
shift the game randomises by up to 20 pixels is left at zero.

**Movies.** `.gfx` is Flash with Scaleform additions. `CFX` and `CWS` bodies are zlib. Tags are `u16 code<<6|len`, with 63 meaning
a `u32` length follows. Tag 1009 is an external image: `u32 id, u16 format, u16 width, u16 height`, then a name and a file name,
each with a length byte. All 2,019 such tags across 742 interface movies resolve to a `.gnf` beside the movie, ignoring case.
A shape's first fill is often a dummy bitmap with id 65535, and the real one follows. `GfxTimeline` reads shapes, sprites and
place and remove tags and can draw any frame, but runs no ActionScript and draws no text.

**Does an edit load?** Not yet seen in game. The evidence so far:

- Overlay textures under `kettle_assets` come from `inxp_10-002.big`, and stock `patch_inxp_*` files already override more than
  1,900 of that archive's paths, so edited textures use the proven route.
- Layouts, definitions, Lua and fonts come from `boot.big`. Movies and their textures come from `overlays_20/21/22.big`.
- **No stock archive overrides any path of `boot.big` or `overlays_2x`** (checked 2026-11-08, zero duplicates), so EA's own
  patches give no proof that an override works for those. The eboot names `overlays_2x` in a separate table, and where they
  mount has not been found. A `patch_inxp_99` override of those files may or may not take effect. `OverlayBuilder` warns.
- **Tested 2026-11-09, does not work:** a `patch_inxp_99` archive holding an edited `scoreclockoverlay.layout` and an edited scoreclock
  texture. In game the texture changed and the layout did not (the stock clock showed the new picture where it uses it). So a
  `boot.big` path in a `patch_*_99` archive is **not** picked up here, while a texture under `kettle_assets` is. The mods folder's own
  `boot.big` was in place at the time, which may or may not matter.
- Puck Peak's mods folder holds a whole replacement `boot.big`, which shows that file can be swapped under shadPS4 if the
  overlay route fails.

### 6.4 Teams and their art, 2026-11-07

**Verified by looking at the pictures** for Toronto (art id 27), Boston (2), Bakersfield (163) and Mannheim (180), and
by test for every NHL club. Nothing here has been confirmed in game: which picture the game draws on which screen is
inferred from names.

The team table is `exhibitionteams` in `nhl/data/common/gamemodes/database/t3db/nhl_game.db`, 423 rows, with names
from `nhl_game-meta.xml`. Fields used: `artid`, `teamname`, `cityname`, `artabbr`, `league`, `arenaid`, `centerline`,
and `feprimarycolor` and friends, which hold `0xRRGGBB`. `arenaid` is the `index` of `exhibitionarena`, which has the
name, capacity and opening year. `centericelogo` is always 1 and is not an id.

Numbered art uses `artid` (written N):

| Art | Path |
|---|---|
| Logos | `nhl/ui/kettle_assets/teamlogos/{large,medium,mediumglossy,smallglossy,extrasmall,largemono,extralargemono,extrasmallmono,hutcard*}/tN.gnf`, and `hutcardrare/t_<tier>_N.gnf` |
| Other logos | `creationzone(_xl)/logos/N.gnf`, `ultimateteam/collectionfolderlogos/tN.gnf` |
| Center ice | `nhl/data/kettle/rendering/stadiums/common/ice_centerlogo_N_cm.gnf`, 1024 square, with the arena's name in it |
| Whole rink | `stadiums/common/icesurface_N_bm.gnf`, mainly NHL |
| Center line | `stadiums/common/ice_cline_<centerline>_cm.gnf`, 64x4096, shared by many teams, with menu pictures in `creationzone(_xl)/arena/centericeline/<centerline>.gnf` |
| Menu art | `bgs/teambackground/tN`, `widgets/mainmenu/seasontile/tN`, `widgets/mainmenu/eashltile/tN` |
| Menu jerseys | `teamjerseys(xl)/jersey_<n>_N_<i>.gnf` |

Not team art: `ice_hotspot_x_y` (sponsor ads) and `centreice_nhl_master` (Winter Classic).

Named art uses a name found in `font_{adidas|rbk|nineties}_<name>_{home|away|two|three}_cm`, 343 of them. The table
does not hold this name, so it is matched from the team's: normalized exact, then prefix, then city, then close
spelling. These needed fixing by hand: San Jose Barracuda is `barracuda`, Cleveland Monsters `lake_erie`,
Wilkes-Barre/Scranton `scranton`, KalPa `kalpa_hockey_oy`, Vaasan Sport `sport_vaasan`, Nurnberg
`thomas_sabo_ice_tigers`, Plzen `hc_skoda_plzen`, SCL Tigers `sc_langnau`, North Bay `brampton_battalion`, EC KAC
`ec_kac_klagenfurt`. Nordiques, Whalers and North Stars alumni have no named art of their own. Sides in the EASHL,
3v3 and Pro-Am leagues are not matched by name, because names like "Notes" would match real teams by accident.
Alumni share their parent's art id, so they inherit its name.

A file belongs to the longest run of whole words in its name that is one of these names, so `calgary_hitmen` never
counts as `calgary`. Those files live in `characters/{player,crowd,mascot,trainer,common}`, `stadiums/common` and
`customlogos`. Arena folders are `stadiums/<name>/` and `stadiums/custom_<name>/`, except `nyislanders`, `nyrangers`,
`tampa`, and the Swiss `euroswissdavos`, `euroswissresega`, `euroswissscbern`, `euroswisszurich`, `euroswissbossard`.

Suffixes `_nm`, `_norm`, `_normal`, `_sm`, `_coeff`, `_spec`, `_am` and `_ab` mark technical maps; `_cm` is colour.

**Mods keep the slots and change the names.** With the mods folder below, the table has Utah Mammoth on art id 22,
still wearing the `arizona` files, and Kraken on art id 10499, in the NHL league, wearing `rbk_pre_order`. Looked at:
those jerseys are the Mammoth's and the Kraken's. So NHL clubs get their file name from their art id, never their name.

### 6.5 The mods folder, 2026-11-07

shadPS4 has a folder beside the game, `CUSA11126-mods`. Both emulator builds here name a `-mods` folder, the fork
logs `Auto-detected mods folder:`, and 0.21.1 logs `Files found in game mods folder`. Puck Peak installs there, with
`overtime-patch.json` listing what it did. The one seen on 2026-11-07 held `boot`, `inxp_00` and `patch_inxp_56`
(its copies of those archives) and new `patch_inxp_57` and `patch_game_97`, with 10,156 files listed.

**Assumed from that, not shown by a log:** a mods copy of an archive replaces the install's copy under the same name,
and new archives join the usual mount order. The log filter hid file opens in the runs available.
**Not tested:** whether the game still loads Blueline's `patch_*_99` from the install's own `databigs` when a mods
folder exists, and what happens to an edit when the mod tool reinstalls. Studio reads the folder only at mount, and
holds its archives open while running, so another tool may be unable to replace them until Studio closes.

**Seen in a log, 2026-11-13:** with a mods folder present, shadPS4 0.21.1 still opens every `patch_inxp_99_*` and
`patch_game_99_*` in the install's own `databigs`, in name order, after the stock and mods archives. Whether they win
over the mods folder's copies is shown only by an on-screen marker (6.10).

### 6.10 Glove and helmet colours per kit, 2026-11-13

**The question.** Editing glove or helmet colours in `nhl_game.db` (Database tab, `blueline db-show`) does nothing in
game for some kits. Where does the game get them?

**Short answer, verified in game 2026-11-14 (sandbox, Toronto).** The colours are data in
`stockteamequipmenttable` in `nhl_game.db`:

1. **Gloves are `equipment` 5 and helmets are `equipment` 8**, one row per kit (`era` + `variant`). The
   equipment labels from `create_team.json` (4 gloves, 7 helmet) do not apply to this table.
2. **Gear `variant` 1 is Home, 2 is Away, 3 and up match the jersey variant, and 0 is the fallback** for a kit
   with no row of its own. Toronto and St. Louis have no Home or Away glove row, so both kits use the variant 0
   glove row and can't differ. That is why only specific kits seemed uneditable. Re-pointing an unused row's `variant`
   gives the kit its own colour (tested).
3. **A roster save blocks all of it.** `RosterCustom0` carries its own copy of the table. With the user's save
   loaded, no edit showed; without it, every edit did. `initial_experience.db` is not read for this.

Neither textures nor the attribute vaults hold these colours. Points below are marked **Read** (from files),
**Verified** (tested) or **Assumed**.

**Every database (Read).** There are 13 `.db` paths, all in the inxp family. None are in `game_*`, and the loose
`nhl/` copies are byte-identical to stock `inxp_00`.

| Path | Copies (mount order) |
|---|---|
| `kettle/loc/*.db` (7 languages) | `inxp_00`, `patch_inxp_51`-`56`; mods `inxp_00`, `patch_inxp_56` |
| `gamemodes/database/t3db/nhl_game.db` (111 tables) | `inxp_00`, `patch_inxp_51`-`56`; mods `inxp_00`, `patch_inxp_56` (winner) |
| `gamemodes/roster/initial_experience.db` (41 tables) | `inxp_00` only; mods `inxp_00` (winner). **Never patched by EA** |
| `gamemodes/roster/{entry_draft,expansion_draft,proam,threes_campaign}.db` | `inxp_00` only; mods `inxp_00` |

The user's saves add two more. `RosterCustom0` and `RosterOnline0` in `savedata/CUSA11126/` each hold one
LZ4-framed TDB. The TDB starts at the frame magic `04 22 4D 18`, about 0x130 bytes in after a JSON header, and is
followed by an 18-byte trailer. The TDB's size field is exact, and all CRCs match.

- `RosterOnline0`: 41 tables, 6,239,164 bytes. This is byte-for-byte the size of `initial_experience.db`, so it is
  that file's schema.
- `RosterCustom0`: 54 tables, adding the `custom*` tables.

The profile save (`Profile0`, LZ4 JSON) says `"ActiveRosterType":"Custom"`, `"ActiveRosterSaveSlot":0`. So the
game boots on `RosterCustom0`.

**Tables that hold gear colour (Read).**

| Table | In | Holds |
|---|---|---|
| `stockteamequipmenttable` (5,188 rows) | `nhl_game.db`, `initial_experience.db`, both roster saves | `team`, `equipment`, `era`, `variant`, `brand`, `zone1`-`zone15` as `0xRRGGBB` |
| `customteamequipmenttable`, `leagueteamequipmenttable` | `nhl_game.db`, `RosterCustom0` | the same, for created teams and franchise leagues (league one empty) |
| `stockteamjerseys` (1,574) | the same four files | per kit: `teamid`, `style` (0 adidas, 1 rbk, 2 nineties, from the kit sheets), `variant` (0 home, 1 away, 2 `two` ...), `helmetlogo`, `islightcolor` |
| `exhibitionskaterequipment` | every roster-shaped file | per player `gloves` and `helmetbrand`: **brands only, no colour** |
| `exhibitionresortskaterextras` | `nhl_game.db`, `initial_experience.db`, saves | `glovezone1`-`15color`, `helmetzone1`-`3color` per art id, light and dark. All `1B1B1B` placeholders; World of CHEL casual gear |

The schema puts the team tables in `savegroups="allloaded,rosters,rosterupdate"`. They are roster data, written
whole into a roster save and read back from it.

**What `equipment` means (Read).** `creation_zone/create_team.json` writes `CustomTeamEquipmentTable` with
explicit keys:

| Value | Gear |
|---|---|
| 0 | trapper |
| 1 | blocker |
| 2 | pads |
| 3 | goalie stick |
| **4** | **gloves** |
| 5 | socks |
| 6 | pants |
| **7** | **helmet** |
| 8 | jersey |
| 9 | jersey font |
| 10-12 | logo colours |

It also writes `variant` 0, 1, 2 for home, away and alternate. Gloves have 12 mask zones and helmets 3, matching
the program's render inputs `Skater_Render_GloveColorZone1..12` and `HelmetColorZone1..3`. **Assumed:** stock rows
use the same numbering, since it is one schema.

**What the stock table actually contains (Read, same in every copy).** For the 31 NHL clubs (`team` 0 to 30):

- **Helmet (7): no rows.** Pants (6) and font (9) have none either.
- **Gloves (4): one row per era** (`era` 0, 1, 2), always `variant` 0. Nothing changes them per kit.
- **Socks (5) and jersey (8): rows per `era` and `variant`.** Many jersey rows use only zones 1 to 3, which is the
  helmet's zone count.
- **Only the nine template teams 391 to 400 have helmet rows and per-kit glove rows.** These are EASHL, HUT, Drop
  In, and the mod's Kraken on 399, at `variant` 1 to 3.

So for NHL kits the helmet colour cannot come from a helmet row. **Assumed, by elimination:** it comes from the
kit's jersey (8) row, or from the club's era glove row. The probe below answers that.

**Team numbering: the gear table is off by one (Read; confirmed for St. Louis by the user's own edit).**
`stockteamequipmenttable.team` is **not** the `exhibitionteams` index that `stockteamcolorstable` and
`stockteamjerseys` use. For NHL clubs it reads as index + 1.

| Gear `team` | Colours in its rows | Club | `exhibitionteams` index |
|---|---|---|---|
| 1 | orange | Anaheim | 0 |
| 3 | gold | Boston | 2 |
| 11 | red | Detroit | 10 |
| 12 | orange, navy | Edmonton | 11 |
| 22 | orange | Philadelphia | 21 |
| 25 | navy, gold, royal blue | St. Louis | 24 |
| 26 | teal `12678C` | San Jose | 25 |

The same `+1` matches `exhibitionteams.mascotbody`. Near the end of the list it is unclear: gear 31 has
Washington-like colours, and Vegas (index 30) has mascot 33, so check those clubs by colour first.

**Anyone editing this table by team index lands on the neighbouring club.** For example, team 24 is Pittsburgh's
gear, not St. Louis's. The Database tab and `db-show` show the raw number with no name, so this alone can make
"specific teams' gloves and helmets" look uneditable.

**Duplicate keys (Read).** Three glove keys exist twice with different colours. Editing the copy the game skips
would do nothing.

| Team | `era`, `variant` | Rows |
|---|---|---|
| Winnipeg (gear `team` 2) | era 1, variant 0 | `index` 76 and 77 |
| 78 | era 0, variant 0 | |
| 309 | era 1, variant 0 | |

**Why the obvious values are ignored.**

1. **The save wins (Read; replacement Assumed, not yet seen in game).** The game boots on `RosterCustom0`, which
   carries its own full `stockteamequipmenttable`, `stockteamjerseys` and `exhibitionteams`. The game reads the
   loaded roster's copy, so an edit to `nhl_game.db`, the file the Database tab opens first, never reaches the
   screen while a roster save is active.

   **Concrete case: St. Louis Blues, the table's `team` 25, rows keyed `era` 0, `variant` 0** (which kit uses them
   is not confirmed). The user changed St. Louis's gloves and helmet themselves, and the game wrote that edit as
   `team` 25 (see "Team numbering" below).

   | Rows | Mods `nhl_game.db` | `RosterCustom0` |
   |---|---|---|
   | Gloves (4), every era | zones 4-9 `202020` | zones 4-9 `0072CE` |
   | Jersey (8), the 3-zone helmet-like set | `12192E` / `202020` / `E7E7E7` | `0072CE` everywhere |
   | Socks (5) | `12192E` | `0072CE` |

   The save changes 28 St. Louis rows across all eras, goalie gear (1 to 3) included. `RosterOnline0` (Overtime's roster) changes 34 rows of
   teams 38, 65 and 398.
2. **Possibly the wrong database even without a save.** **Assumed** from the size match above: new rosters are
   seeded from `initial_experience.db`, not `nhl_game.db`. Toronto's two colour sets in the probe test this.
   **Read:** in stock that file is still the launch-day table (6,688 rows): EA's patches 51 to 56 updated only
   `nhl_game.db` (5,188 rows), and 2,614 keys differ between the two. Puck Peak's mods folder ships both with the
   same table.
3. **No row exists to edit** for an NHL club's helmet, or for a glove that should change by kit, as shown above.

**Not the source (Read).**

- **Textures.** Gloves are one sheet set per model (`glove_{left,right}_<brand>_<model>_{cm,nm,coeff,am,dm,tm1-4}`).
  Helmets are per model (`helmet_<model>` and `helmet_current_<model>_{nhl,ahl,generic}`). Both are recoloured at
  run time through `tm` zone masks (VISUALS 8.4). Only the logo decal is per kit
  (`helmetlogo_<maker>_<club>_<kit>_0`). No team colours are baked in.
- **Attribute vaults.** `gccollections` names the render inputs and has `nhl_skaterglovestexturepack`,
  `nhl_skaterhelmettexturepack` and `nhl_skatercolorpack` classes. No team- or kit-named colour collection exists
  among its strings.
- **`isfixtexture`.** In `creation_zone_csv` this applies to skates and sticks, not gloves or helmets.

**Probe patch and sandbox (Verified to boot; colours not yet seen).** The first build used team-list numbers (27
and 2), so it hit Tampa Bay and Winnipeg. It was rebuilt on 2026-11-13 with gear `team` 28 (Toronto) and 3
(Boston). The rebuilt probe colour-codes those two clubs. Toronto's rows are set differently in each database, so the colour on screen names the winning file:

| Toronto rows | In `nhl_game.db` | In `initial_experience.db` |
|---|---|---|
| Gloves (4) | red `FF0000` | yellow `FFFF00` |
| Socks (5) | green `00FF00` | magenta `FF00FF` |
| Jersey (8) | blue `0000FF` | cyan `00FFFF` |

Boston's rows get one colour per row, the same in both databases:

| Boston glove row (4) | Colour |
|---|---|
| era 0 | orange `FF8000` |
| era 1 | pink `FF66CC` |
| era 2 | purple `7F00FF` |

| Boston jersey row (8) | Colour |
|---|---|
| era 0, variant 0 | cyan |
| era 0, variant 2 | yellow |
| era 0, variant 3 | magenta |
| era 0, variant 4 | white |
| era 1, variant 0 | brown |
| era 1, variant 2 | orange |
| era 1, variant 3 | grey |
| era 1, variant 4 | mint `00FF80` |
| era 3, variant 0 | green |
| era 3, variant 2 | red |
| era 3, variant 4 | blue |

The full row-by-row list is in the probe project's `legend.tsv`. Boston has no duplicate glove keys; the duplicates
listed above belong to Winnipeg (gear `team` 2) and teams 78 and 309.

The title-screen `PressStart` text ("Press") should read `GEAR PROBE`, which shows the eng_us edit loaded. The sandbox is
hardlinked and includes the mods folder, with networking off. The game's EA telemetry posts are logged as
`OFFLINE POST` / `TRANSPORT FAIL`.

**Several databases in one archive break the boot (seen 2026-11-13/14).** A check that waits only for
`HideSplashScreen` misses this. Wait until the log opens `nhl/ui/lua/saveload/rostersaveloadutility.lreq`: that is
the front end loading. On a good boot it appears about 25 s after launch.

| `patch_inxp_99` archives in the sandbox | Result |
|---|---|
| none of the probe archives | menus in 25 s |
| `nhl_game.db`, `initial_experience.db`, `eng_us.db` in one archive | crash `0xc0000005` in "Loading Job 3", twice |
| `nhl_game.db` + `eng_us.db` in one archive (edited), with or without the ie archive | **hangs** after `HideSplashScreen`: the log stops after loading `libSceJson2`, the `ant`/`DIME` threads and audio never start, no menus after 6 min |
| `nhl_game.db` unchanged copy, alone | menus in 25 s |
| edited `nhl_game.db` alone | menus in 25 s |
| edited `eng_us.db` alone | menus in 25 s |
| edited `nhl_game.db`, edited `eng_us.db`, edited `initial_experience.db`, three archives | menus in 25 s |

So the database edits themselves are fine; the trouble is two of these databases sharing one archive. It looks like
the colour-cube freeze (an XML and its cube freeze together, fine apart). Why it happens is unknown. Until it is
understood, give each `.db` its own archive. Studio's `OverlayBuilder` puts every inxp change into one
`patch_inxp_99_<mod>.big`, so a Studio mod that edits two databases will hit this.

The sandbox's other mounts (`/data`, `/temp0`, `/download0`) and its log all point into the sandbox `user` folder,
and the user's real saves and configs were unchanged afterwards. Runs A (user's saves) and B (`Profile0` only) differ
only once the game reads the saves, after a button is pressed at the title screen.

**To change a kit's glove or helmet colour, as far as is known.**

- Edit `stockteamequipmenttable` in **both** `nhl_game.db` and `initial_experience.db`, each `.db` in its own archive.
- Make sure the game is not on a roster save that carries its own copy. The other route is to write the roster save
  itself, which Studio cannot do yet. That would need LZ4 re-framing, the header size fields and the `CRCValue`,
  whose algorithm is unknown.
- Which row a given kit reads is unconfirmed until someone looks at the probe in game.

**Probe result, run A (user's saves, 2026-11-14).** Play Now team select, Toronto home preview: gloves and jersey
are stock Toronto blue. Neither the `nhl_game.db` colours (red gloves, `0000FF` jersey) nor the
`initial_experience.db` colours (yellow gloves, cyan jersey) show. So with the user's roster save active, both
database copies are ignored for this kit. Run B (no roster save) decides whether the save is the reason.

**Probe result, run B (`Profile0` only, no roster save, 2026-11-14).** Toronto's gloves are bright green
`00FF00` on every kit (Home and "The Digital 6"). Green was the colour set on Toronto's **equipment 5** rows in
`nhl_game.db`. The equipment 4 rows were set to red, which shows nowhere. So:

- **Gloves are `equipment` 5 in `stockteamequipmenttable`**, not 4. The `create_team.json` enum does not match this
  table, so treat the equipment labels above as unconfirmed. Stock Toronto equipment 5 zone colours are navy
  `052E6C`, which fits gloves.
- Equipment 5 has one row per kit (`era` + `variant`), 14 rows for Toronto, so **per-kit glove colours are
  possible**. All kits turned green only because the probe painted every row.
- **The game reads `nhl_game.db`**; the `initial_experience.db` colour (`FF00FF` on equipment 5) did not show.
- **The user's roster save was what blocked the edit:** the same archives showed nothing in run A.
- The helmet looked a brighter blue than in run A, possibly the probe's `0000FF` on equipment 8. Unconfirmed.

A follow-up probe (`kitmap`) gives each Toronto equipment 5 row its own colour and sets every equipment 8 row orange.
Its legend is below.

| Colour | Row (`index`) | era | variant |
|---|---|---|---|
| red | 991 | 3 | 0 |
| yellow | 992 | 3 | 3 |
| cyan | 993 | 3 | 4 |
| magenta | 994 | 3 | 5 |
| white | 995 | 3 | 6 |
| black | 996 | 0 | 0 |
| green | 997 | 0 | 0 (brand 59) |
| pink | 998 | 0 | 11 |
| purple | 999 | 0 | 7 |
| brown | 1000 | 0 | 10 |
| grey | 1001 | 1 | 0 |
| teal | 1002 | 1 | 4 |
| dark red | 1003 | 1 | 5 |
| tan | 1004 | 1 | 6 |

**Kit-map result (run B, 2026-11-14), Play Now team select, Toronto.** Two runs: the kit map as legended above, then
the same with row 995 (era 3 / variant 6, white) re-pointed to `variant` 1.

| Kit | Jersey (`style` 0) variant | Run 1 gloves | Run 2 gloves | Gear row it reads |
|---|---|---|---|---|
| Home | 0 | red | **white** | gear variant **1**; with no such row, falls back to variant 0 |
| Away | 1 | red | red | gear variant **0** (row 991) |
| Alternate (black) | 3 | yellow | | gear variant 3 (row 992) |
| 2018-19 Alternate (St. Pats) | 5 | magenta | | gear variant 5 (row 994) |

- Home read gear variant 1 once such a row existed. Away kept reading variant 0 here, but the St. Louis test below
  shows Away reads **variant 2** when it exists; variant 0 is a fallback, not Away. Variants 3 and 5 matched the
  jersey variant.
- **A kit with no row of its own uses the gear variant 0 row.** Stock Toronto has no variant 1 glove row,
  so Home and Away shared gloves, and neither could be changed alone. This is the "specific uniforms" problem.
- **Fix that works:** re-point a row nobody uses (here era 3 / variant 6; no adidas jersey has variant 6) to the
  missing variant and colour it. Home got its own gloves; Away kept its own. Adding a new row was not needed.
- Gear `era` 3 goes with jersey `style` 0 (adidas). How eras 0 and 1 map to `style` 1 and 2 is not yet seen.
- **The helmet was orange on every kit, so `equipment` 8 sets the helmet colour.** The jerseys did not change
  (they are textures). Toronto has equipment 8 rows for era 3 variants 0, 1, 3, 5 and 6, so per-kit helmets should
  work without re-pointing; not yet tested kit by kit.
- All of this only shows when no roster save with its own table is loaded (run A, with the user's `RosterCustom0`,
  showed none of it).

**Working edit: St. Louis (gear `team` 25), verified in the sandbox 2026-11-14.** One `nhl_game.db` archive,
`patch_inxp_99_stl-kit-colours.big`. Each row's main colour (the zone 1 value) was replaced in every zone holding it;
the other zones were kept.

| Row | Equipment, era / variant | Change | Seen on |
|---|---|---|---|
| 900 | gloves, 3 / 0 (fallback) | main `0072CE`, zone 7 `FFB81C` | Home and Away gloves; zone 7 is the CCM logo |
| 907 | helmet, 3 / 1 | `0072CE` | Home helmet |
| 908 | helmet, 3 / 2 | `FFFFFF` | Away helmet (**proves gear variant 2 = Away**) |
| 901 | gloves, 3 / 3 | `041E42` (zone 6 cream, zone 8 gold kept) | Alternate gloves; the logo showed cream, so zone 6 is the logo on this row |
| 909 | helmet, 3 / 3 | `041E42` | Alternate helmet |

## 7. Requirements that follow

1. **Never modify game files.** Install by adding `patch_inxp_99_*.big` / `patch_game_99_*.big`; uninstall by
   deleting them. Backups of 33 GB are unnecessary (the user's drive has about 45 GB free).
2. Route each edit by family: a path whose current winner is an `inxp`-family archive goes in the inxp overlay,
   a `game`-family one in the game overlay. `game` mounts later, so a boot-time read would miss it.
3. Maintain a virtual file system with the game's real mount order, so previews show what the game would load.
4. Detect formats from bytes. Preserve the original's wrapper (chunkzip or raw).
5. Handle 13 GB archives without loading them: stream, and read only headers on open.
6. Refuse replacement textures that change a descriptor unless the user confirms it (dimensions, format).

## 8. Old claims tested against NHL 19

| Old claim (from NHL 21 work) | Result on NHL 19 |
|---|---|
| Header layout, chunkzip layout | **True** |
| "CRC field is a group id, not needed by a writer" | **False.** High half of the path hash, sort key |
| "EXTRA has a constant step, purpose unknown" | **False.** Low half of the path hash |
| `game_90.big`, 92,322 files, 17 GB | NHL 19: `game_90-006.big`, 79,391 files, 12.6 GB, plus patches |
| eboot is a fake SELF | **False here.** Plain ELF |
| Loc tables have 55,766 rows | NHL 19: 38,001 |
| All audio is EAOpus | **False.** 12 banks are EASpeex |
| Audio in `game_80` + `inxp_40` only | **False.** Also patched in `patch_game_91/92/93` and `patch_inxp_51..` |
| Same-format texture replace needs Noesis/`orbis-image2gnf` | **False.** Native decode and encode, measured in `tests/Blueline.Tests/RealGameTests.cs` (BC7 43 dB, BC1 59 dB) |
| 5-6 MB archive growth ceiling, `pant_nm` cannot move | Only tested in-place on NHL 21. Irrelevant to overlays; recheck if ever seen |
| Equipment colour tables ignored at render | **Explained** (6.10): gloves are equipment 5 and helmets 8; gear variant 1 Home, 2 Away, 0 fallback; a loaded roster save overrides the table. Seen in game |
| Texpack vault gating for new jersey slots | Not retested on NHL 19 |

## 9. Still unknown

- **Whether the title screen takes input.** The "frozen" report is not reproduced as a stall in the logs (section
  4.1), but nobody has pressed a button in both a stock and an overlay run. Until then, in-game proof stops at
  "loaded and overrode".
- Retail PS4 behaviour of an extra archive (PlayGo chunks, `sce_discmap`). Emulator only so far.
- `.vlt`, `.cba`, `.ply`, `.rsf`, `.crowd` formats, and what links a model to its colour textures.
- `.sbr` seek mechanism, so **growing** an audio sample stays out of scope.
- Where `overlays_2x.big` sits in the mount order. It is named explicitly, but its position is not established.
- Three nested archives (`anim/faceposer.big`, `gamedata/gps.big`, `gamemodes/proam/proam_download.big`) are not
  opened by this tool's file system, so their contents do not appear in it.
- Roster saves: seen in game (6.10) that a loaded `RosterCustom0` overrides `stockteamequipmenttable` edits. Whether
  every roster table behaves the same is not tested.
- Gear `era` 0 and 1 against jersey `style` 1 and 2, and per-kit helmet rows, are not yet seen in game (6.10).
- Save `CRCValue` (54-character text in each roster save's JSON header): algorithm unknown, so saves are read-only
  for now.
