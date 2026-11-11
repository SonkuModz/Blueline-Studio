# Sportsnet scorebug reference

Measured on 2026-11-09 from the files in `references/sportsnet/`. Every one of the 170 frames of `goal.mp4` was looked at (contact sheets in
`references/sportsnet/frame-sheets/`, `s01.png` to `s17.png`, ten frames each, cropped to the bug) and the edges below were measured on
the full 1920 by 1080 frames with `scan.ps1`, `text.ps1` and `track.ps1` in the same folder.

**How exact this is.** `goal.mp4` is a blurry, roughly 3 times zoomed crop of a broadcast, so edges are good to about 3 video pixels
(1 native pixel) and colours are averaged over soft edges. Fonts are described, not identified. Nothing here has been tested in the
game. Where something was not on screen it is listed under "Not seen".

## 1. Sources and scale

| File | What it is |
| --- | --- |
| `goal.mp4` | 1920x1080, 30 fps, 170 frames (5.67 s), HEVC. ANA at CGY, Calgary scores. The bug is zoomed about 3.05 to 3.1 times. |
| `sm5psrclojsh1.jpg` | 2048x1170 full broadcast frame, MTL 0 TOR 1, 14:17 1ST. Bug is **472 wide and 89 high** in this image, so about **442 by 83 at 1920x1080**. |
| `d87e2rclojsh1.jpg` | Close crop of the same bug (MTL 0 TOR 1, 19:10 1ST). Same proportions, 5.2 to 1. |
| `pp.png` | Power play tab under the bug (ANA 1 CGY 2, POWER PLAY 1:36). |
| `stats.png` | Power play panel to the right of the clock (LAST 10 GAMES 9/24) and the tab (ANA 1 CGY 3, 9:32 2ND). |
| `power play goal specific.png` | Goal bar with the two line text POWER PLAY over GOAL. |
| `bug during replays.png` | During replays only the SN logo tile is left. |
| `Sportsnet_icon.png` | SN logo, 309x163, black "SN" with a red swoosh over a blue swoosh. |

Zoom: video bug width 1350 divided by 442 native gives 3.05, and video height 259 divided by 83 gives 3.1. **Native pixels below use 3.07**
(video px divided by 3.07). `H` means the bug's height, the unit that stayed constant across the stills.

**One thing that helps directly:** the video is 30 fps, which is the unit the game's own animation scripts use
(`_framesToMilliseconds(n) = n / 30 * 1000`, RESEARCH 6.9). Frame numbers below are the exported file numbers, `f1` to `f170`.
Time of frame n is `(n - 1) / 30` s. The animations below can be copied into `OverlayScript` frame for frame.

## 2. The resting bug

Video pixel coordinates from `f170` (x from the left of the 1920 frame, y from the top), then native size.

| Part | x | y | Native size |
| --- | --- | --- | --- |
| Whole bug | 147 to 1497 (1350) | 226 to 485 (259) | **440 x 84** |
| SN logo tile | 147 to 404 (257) | full height | 84 x 84, square |
| Away panel (orange here) | 406 to 797 (391) | 226 to 400 | 127 x 57 |
| Home panel (red here) | 797 to 1190 (393) | 226 to 400 | 128 x 57 |
| Clock cell | 1196 to 1497 (301) | 226 to 485 | 98 x 84 |
| Shots row | 404 to 1192 | 404 to 485 (81) | 26 high |
| Seam between rows | | about 400 to 404 | 1 px |

Shots row cells: away shots 404 to 619, SHOTS label 629 to 966, home shots 976 to 1192. Darker 8 to 10 px divider strips sit at 404 to 413,
619 to 629 and 966 to 976 (about 3 native px, a gradient not a line). The clock cell is not split by a row seam. Its time area runs from the top
to a thin divider at about y 367 to 372 (x about 1230 to 1460), and the period sits below it.

**Corners and edges.** Only the bottom right corner is clearly rounded, radius about 25 video px (**8 native**). The top left is square. The top
right is rounded by about 5 video px at most. There is a soft shadow under the whole bug, about 3 video px (1 native) of darker pixels
at y 484 to 487. The logo tile has a darker 10 px band on its right edge (404 to 413), a shadow that falls on the panels.

**Colours** (averages, home and away are team colours, so only the gradient shape matters):

| Part | Colours |
| --- | --- |
| Logo tile and clock cell, team row | top `#E7E2E6`, falling to `#D9DBDD` at the lower edge of the row |
| Shots row and below the clock divider | `#CFCFCF` to `#CDCACD` (a step darker than the team row) |
| Away panel, orange | top rim `#C3543B` (4 px), `#EC562A` (y 230 to 239), `#EB4C1C` (240 to 314), `#D64211` (315 to 387), bottom band `#A33514` (388 to 395) |
| Home panel, red | `#CB1C3B` (230 to 303), `#B4122C` (304 to 387), bottom band `#811021` (392 to 395); on the right `#E92849` falling to `#B41F35` |
| Text on panels | white `#FFFFFF` |
| Clock, period, shots numbers, SHOTS | dark slate, about `#1E2F3F` for the clock digits, `#2B3F52` for SHOTS |

So each team panel is two or three flat steps of one hue, lightest at the top, plus a darker band along its bottom edge about 8 video px
(2.6 native) high. The panel top has a 4 px darker rim line which may only be anti-aliasing.

**Text** (cap or digit heights in video px, then native; fonts are a condensed bold grotesque for names and numbers, a slightly lighter
cut for small text, none of it matched to a known font):

| Text | Box (video px) | Height | Native |
| --- | --- | --- | --- |
| Away abbreviation `ANA` | x 456 to 618, y 281 to 340 | 59 | 19 cap, about 27 px font |
| Away score `0` | x 678 to 746, y 262 to 361 | 100 | 32.5 high, 22 wide |
| Home abbreviation `CGY` | x 858 to 1011, y 282 to 341 | 59 | 19 |
| Home score `1` | x 1088 to 1124, y 262 to 360 | 99 | 32 |
| Clock `17:37` | x 1247 to 1456, y 273 to 340 | 67 | 22 |
| Period `1ST` | x 1295 to 1396, y 408 to 458 | about 46 | 15 |
| Shots away `0` | x 495 to 532, y 418 to 466 | 49 | 16 |
| `SHOTS` | x 692 to 900, y 417 to 466 | 49 | 16 |
| Shots home `1` | x 1075 to 1087, y 419 to 463 | 45 | 15 |
| SN logo | x 178 to 373, y 297 to 418 | 121 | 64 x 39 |

Placement rules visible in the numbers:

- The SN logo is centred in its tile (centre x 275.5 against tile 275.5, centre y 357.5 against 355.5).
- In each panel the abbreviation and score are a group with equal padding: away is 50 left, abbreviation, a gap of about 60, score, 51 right.
  Native: **16 padding, name, 20 gap, score, 16 padding**. Home has 61 left and 59 right padding with the narrower `1`, which fits a fixed
  score cell of about 70 video px (23 native) and the digit centred in it.
- Team row text is vertically centred in the team row (text centre 311, row centre 313).
- Shots row, clock and period are centred in their cells (shots numbers within 2 video px, SHOTS within 2, clock within 5, period within 1).

## 3. Goal sequence, frame by frame

Start of the clip: bug at rest with the old score (ANA 0, CGY 0, shots 0 and 1, 17:37 1ST). The goal bar is the **same height as the bug (259
video px)** and runs from the SN tile's right edge (x 404) to **x 1751**, so it is wider than the resting bug (1347 video px, 439 native, against
1093 for the panels plus clock). The SN tile never moves or changes.

Eased values below were fitted: `quadIn` is `t*t`, `quadOut` is `1-(1-t)^2`, matching the game's `Easing` (power 2).

| Frames | Time | What happens |
| --- | --- | --- |
| f1 to f9 | 0 to 0.27 s | Bug at rest. |
| f10 to f16 | 0.30 to 0.50 s | **Blue bar** (`#015AB5`, 1,90,181) grows left to right over the whole panel and clock area, full height, flat. Front edge x: f10 435, f11 509, f12 635, f13 829, f14 1093, f15 1417, f16 1751. This is **`quadIn`, started at about f9.2, about 6.8 frames** (fit within 2 percent at every frame). It covers the panels, text and clock at once, so nothing needs to fade first. The bottom right of the bar is rounded like the bug. |
| f16 to f25 | 0.50 to 0.80 s | **White band** (`#EFECF1`) sweeps through the blue, left to right. Leading edge x: f17 748, f18 1066, f19 1319, f20 1489, f21 1596, f22 1665, f23 1710, f24 1737, f25 1752. About 8 frames of `quadOut`, start f16.0. |
| f18 to f28 | 0.57 to 0.90 s | The white band's **trailing edge** follows: f19 645, f20 933, f21 1180, f22 1375, f23 1512, f24 1600, f25 1666, f26 1700, f27 1724, f28 1745. Same `quadOut`, about 8 frames, started about 2.2 frames after the leading edge. Left of the trailing edge the blue is still there until the red arrives. |
| f22 to f28 | 0.70 to 0.90 s | **Red team bar** is revealed from the left where the blue ends: edge x f22 600, f23 1028, f24 1398, f25 1588, f26 1670, f27 1712, f28 1735 (blue gone). Again `quadOut`, about 8 frames, started about 3 frames after the white trailing edge. So three same-shaped 8 frame wipes in a row, at f16.0, f18.2 and f21.2. |
| f22 to f40 | 0.70 to 1.30 s | The new bar is dark red (`#AE0922` to `#CA1E35`) with a **tone on tone relief of the team name** repeated across it, letters as tall as the bar (cap height about 250 video px, 81 native), cut off at the ends, reading "S F L A C IES F" (the C slot is where the logo sits). It is texture only, there is no live text. |
| f23 to f31 | 0.73 to 1.0 s | **Team logo** (Calgary flaming C, cream with red C) is in the middle of the bar: f24 box x 1026 to 1151 (126 wide), y 292 to 394 (103 high). Centre about (1089, 343), which is 685 video px (223 native) from the bar's left edge, 11 right of the bar's centre and 12 above it. Growth is slow at first: widths f24 126, f27 127, f28 130, f30 136, f31 136, f32 140, f33 143, f34 150, f35 162, f36 175, f37 192. |
| f37 to f41 | 1.2 to 1.33 s | Logo **zooms through the camera**: f38 236 wide, f39 402 and clipped by the bar, f40 fills it (cream with a yellow outline, edges at the bar's top and bottom). Each frame scales about 1.1 and then 1.2, 1.7, 2 and more, so an accelerating zoom (ease in). At f41 the logo is a washed pale shape fading out and the word below is already coming in. |
| f41 to f57 | 1.33 to 1.87 s | **Team name `FLAMES`** in white, letter spaced, centred on the bar (centre x 1074 against bar centre 1077). Cap height **96 video px (31 native)**. **Tracking opens up**: text width f41 437, f42 487, f43 574, f44 650, f45 694, f46 720, f47 737, f48 750, f49 756, f50 761, f51 764 to f55 765. That is `quadOut` over about 10 frames (steps shrink 50, 87, 76, 44, 26, 17, 13, 6, 5, 3). Hold f51 to f55. The relief texture is gone by f41 and the bar is plain red with a faint swirl. |
| f56 to f58 | 1.83 to 1.9 s | **Glitch out**: the letters are cut into horizontal slices that shift sideways for two frames (f56 slightly, f57 heavily, 1325 white pixels left against 4900), and are gone at f58. |
| f58 to f64 | 1.9 to 2.1 s | Bar is blank: plain red with two **static decorations**, a large faint swirl shaped like the logo (x about 650 to 900, y 270 to 470) and a thin pale streak low on the right (x 1340 to 1550, y about 428 to 440). Both stay for the whole rest of the bar's life and are not animated. |
| f65 to f73 | 2.13 to 2.4 s | **`GOAL`** appears letter by letter in the order **G, L, A, O**, two frames apart, each fading in over two frames: G faint at f65 and solid at f66, L faint f67 and solid f68, A faint f69 and solid f70, O faint f71 and solid f72 to f73. Letters appear **already at their final places**, they do not move in. White, bold italic condensed, **cap height 204 video px (66 native), 79 percent of the bar**. First box: G at x 781 to 913. Text box f73: x 777 to 1390, y 250 to 453. |
| f73 to f125 | 2.4 to 4.13 s | **Hold**, about 1.7 s. The letters spread slowly (tracking grows): text box width f73 614, f80 626, f88 638, f96 653, f104 665, f112 678, f120 699 (left edge 777 to 733, right 1390 to 1431). That is about 1.6 video px per frame across the three gaps, linear, and the centre stays at 1082 (bar centre 1077.5, italic lean accounts for the 4). |
| f124 to f134 | 4.1 to 4.43 s | **Goal bar retracts to the left.** Right edge x: f123 1751, f124 1733, f125 about 1709, f126 about 1668, f127 about 1613, f128 about 1509, f129 1339, f130 1075, f131 803, f132 591, f133 527, f134 515. Slow start, fastest at f129 to f131 (about 265 video px per frame), then it stops at x about 515 (not at the logo tile edge, 404). |
| f125 to f131 | 4.13 to 4.33 s | `GOAL` **leaves while the edge passes**: the text spreads (width f122 709, f124 724, f126 750, f127 774, f128 809) and grows about 14 percent in height (204 to 239) while fading. Fade order is **O first (f126), then A (f127 to f128), L (gone by f129), G last (f129 to f131, cut by the edge)**. |
| f134 to f152 | 4.43 to 5.03 s | **New bug builds left to right, with the new score.** Right edge x: f134 515, f135 553, f136 600, f137 667, f138 761, f139 881, f140 1013, f141 1139, f142 1222, f143 1287, f144 1333, f145 1372, f146 1401, f147 1425, f148 1443, f149 1457, f150 1467, f151 1475, f152 1480, ending at 1497. Top and bottom rows grow together (within 5 px at each frame). The speed rises, peaks around f139 to f141 (about 130 px per frame) and then eases off, so an ease in out with a long tail, about 18 frames. |
| f135 to f153 | 4.47 to 5.07 s | Text fades in behind the growing edge, left to right, each element about 2 frames, staggered by the wipe: `ANA` f137 to f138, `CGY` f143 to f144, away score `0` f146 to f147, home score `1` f147 to f149 (**this is the new score**), clock `17:37` and `1ST` f149 to f150, shots row `0`, `SHOTS`, `1` f150 to f153. Text sits at its rest position when it fades in, with no slide. |
| f153 to f170 | 5.07 to 5.63 s | Bug at rest with the new score: ANA 0 CGY 1, shots 0 and 1, 17:37 1ST. |

**Totals.** Blue starts at f9.2 and the bug is whole again at f152, about **4.8 s**. If GOAL's hold is the only variable part, the rest
(wipe in about 0.8 s, logo and name 1.3 s, GOAL appear 0.3 s, retract and rebuild 0.9 s) is fixed at about 3.1 s.

**Background footage.** A red and yellow vertical stripe at x about 1500 to 1600 from f126 to f141 is the arena's goal light in the
video, not part of the graphic.

## 4. Other states (stills only, no timing)

**Power play tab** (`pp.png`, `stats.png`). Measured in units of the bug height `H` (84 native):

- The **scoring team's logo (Calgary C) is shown as a square tile between the home panel and the clock**, `1.0 H` wide and the full height,
  with no background of its own (the flames are cream with a gold outline, the C is red). It pushes the clock cell right by `1.0 H`.
- **Tab**: red (the team colour), joined to the bottom of the bug, **as wide as the home panel and the logo tile together (2.5 H, 208 native)**,
  **0.6 H high (51 native)**, starting under the left edge of the home panel. Contents: `POWER PLAY` left aligned about 6 native from the
  left edge in a smaller bold white condensed, cap height about 14 native, and the time `1:36` right aligned about 12 native from the right edge,
  larger, cap height about 18 native.
- Ratios from the still: logo tile 1.0 H, away panel 1.5 H, home panel 1.5 H, clock cell 1.18 H, tab 2.48 H by 0.61 H.

**Power play panel** (`stats.png`): on the right of the clock cell, a red panel the same height as the bug, **4.6 H wide (388 native)**, with a logo tile
(1.0 H square) at its left, then a two line label `POWER` over `PLAY` (cap height about 14 native, white), then `LAST 10 GAMES` above a
large `9/24` (cap height about 30 native) left aligned together. Rounded outer corners (top right and bottom right, about 8 native).
It sits 0 to 4 video px from the clock cell.

**Power play goal** (`power play goal specific.png`): the same goal bar as in the video, but the text is two lines, `POWER PLAY` in small italic caps
(cap height about 0.2 of the bar) above `GOAL` in the big italic letters, centred together. The same decorations (faint swirl, streak) show.

**Replays** (`bug during replays.png`): only the SN logo tile remains, in its usual place (about x 107 to 193, y 57 to 140 at 1920x1080).
The panels and clock are not shown.

**Away and home are the same animations with the colours swapped** (stated by the user, 2026-11-09). Where something sits on one side
(the logo tile) it mirrors: the tile goes on the **outer side of the team that has the situation**, as the next stills show.

**Final** (`Screenshot 2026-11-09 195613.png`, VAN 2 WPG 6): the clock cell holds only `FINAL`, centred both ways, dark slate bold, cap height about
0.14 H. Shots row unchanged (26, SHOTS, 29). A panel can be **light**: Vancouver's is a white to pale grey gradient with **dark navy text and score**,
while Winnipeg's is dark navy `#0B3C7C` with white text. So text colour flips with the panel's brightness.

**Empty net** (`...195653.png`, TOR 3 SEA 4, 1:36 3RD): the scoring side's **logo tile sits between the SN tile and the away panel**, 1.0 H square with a
background in the team's own colour (Toronto: the same dark blue as its panel) and the logo in white. Under it a tab in the same team colour,
**the width of the tile plus the away panel (2.5 H)**, about **0.37 H high here** (the power play tab above measured 0.61 H, so the heights differ by state
or the earlier crop included extra margin), text `EMPTY NET` in white bold, centred, cap height about 0.17 H. The tab's top edge is flush against the bottom of the tile and panel,
and the tab's left edge lines up with the tile's left edge.

**Four on four** (`...195833.png`, VAN 1 ANA 4, 10:45 3RD): a **second cell is added to the right of the clock cell**, the same width as the clock cell (1.2 H),
separated by a thin vertical divider. Top line `4-ON-4` (small, bold, dark slate), bottom line `1:10` (same size as the clock digits). The
cell's bottom right corner takes over the rounding. No tab appears for neutral strength.

**Goal scorer card** (`...195851.png`, GOAL 4TH THIS SEASON, BOESER 11:40): the SN tile stays. To its right a **team logo tile** (1.0 H, team colour background, logo in its
own colours), then a **player headshot** about 1.1 H wide, cut out and **rising above the bar by about 0.4 H**, then a text block in the team's colour (a mid blue
`#1F4F8F` here) with rounded top right and bottom right corners. Text is left aligned in two lines: `GOAL` large bold with `4TH THIS SEASON` smaller
beside it on the same baseline, and `BOESER` large with the time `11:40` smaller beside it. Whole bar is 1.0 H high. It is not known whether this follows the goal
bar or replaces it, or how it animates.

**Position on screen** (full frame still, scaled to 1920 by 1080): bug left edge about **106**, top about **68**, so it sits in the upper left
with a margin of roughly 106 across and 68 down.

## 5. What this means for a custom scorebug in this engine

Compared with what is already known about the NHL 19 score clock (RESEARCH 6.9):

- **Same timing unit and easing.** 30 fps frames and quadIn, quadOut and quadInOut are exactly what the engine has. The goal, wipe and
  rebuild above can be written as `OverlayScript` steps, including the delays between the three 8 frame wipes.
- **Different structure.** The stock clock slides the whole bug in from the left and shows drop-downs under it. The Sportsnet bug stays put,
  shows the goal as a wipe over its own panels, has no slide in, and puts the scoring team's logo into a tile on the bug during a power play.
  The stock layout has no equivalent of the tile, the relief texture, the glitch slices, or letter by letter text, so those are new
  layout parts, and the **text effects (tracking, glitch slices, per letter fade) would need either one label per letter or textures**,
  because the game's `Label` has `tracking` but nothing in the stock scripts animates it per letter except the goal text's own tracking (10 to 25).
- **Hard coded numbers in the stock code** (RESEARCH 6.9) still apply if the stock `ScoreClockOverlay` class is reused: it expects the node names
  and writes its own start positions, widths and heights. A custom bug with these proportions would need either its own widths matched
  to those constants or a new code path, which is not possible without replacing `nhluidata.swf`.

## 5b. The "Sportsnet look" preset (built 2026-11-09)

Overlays, ScoreClock, **Sportsnet look** button (`SportsnetBug.cs`). One undo step for the layout and one for the logo texture, and Revert layout
restores the original. Designed at 1.953 times size because the code's 248 pixel panels are fixed, then `scoreClock` is scaled by 0.51.
Compared against `f170` and the stills with `references/sportsnet/compare.ps1` style side by sides.

**Matches closely (resting bug):** overall size 436 by 84, SN tile with the logo (stock `scoreclock_score_divider` texture is replaced with the SN
logo, so the other clocks' score dividers become that logo too), panel and clock widths, text sizes and positions, light flat tile, clock
cell and shots strip, the clock divider, tracking tightened on the clock digits.

**Does not match, and why:**

- **Shots numbers.** The row shows `SHOTS` and dividers only. The numbers are live, nothing in a layout can fill them.
- **Panel colour.** The game tints panels darker than the team table. A glow layer coloured with the team colour lifts them, but this is calibrated to one
  in-game screenshot and not checked in game with this layout.
- **Goal bar.** The game's code sets the goal text size (60) and runs its own animation: no blue or white wipe, no relief texture, no logo zoom,
  no `FLAMES`, no per letter reveal, no italic, no ghost swirl. Only the bar's size and colour follow the real one.
- **Power play, empty net, penalties.** The tab is the code's 35 high strip under one panel, not the 2.5 H wide tab, and there is no logo tile.
- **Not built at all:** 4-on-4 extra cell, `FINAL` layout check, scorer card with headshot, rounded bottom right corner.
- The remaining items need the ActionScript changed (RESEARCH 6.9 lists the hard coded numbers). Patching literal numbers is possible, new logic
  could not be tested without the game.

## 6. Not seen

- Anything other than the right hand team scoring; the away side's goal wipe (does it run from the right or reuse the left to right sweep?).
- What the tab looks like when the **away** team has the power play, or where its logo tile goes.
- Delayed penalty, empty net, penalty details, the overtime and shootout looks, intermission and final.
- Any text font file. Sportsnet's own font could not be matched, so type would need a close stock substitute (the game ships
  `NBCSN_Frank` in Bold, Medium and Regular).
- Whether the swirl behind GOAL is the team logo or a fixed shape. It looks like a faint copy of the Calgary C, so it is probably the logo.
- Exact colours of the white band and the blue beyond the averages above; the blue is a fixed colour, not a team colour.
