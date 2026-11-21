# Roster editor: field map

Research for the Studio roster editor (2026-11-20). It covers every player and roster field NHL 19 (CUSA11126 1.60) keeps in
`nhl_game.db` and in the custom roster save (`RosterCustom0`, which the game reads instead of the database once a roster is
active). See `docs/RESEARCH.md` and the memory notes on roster save sync for the save container.

**Evidence tags** used below:
- **CZ**: the game's own create-a-player definition (`creation_zone/create_player.json`) commits this field, so the game reads it.
- **CSV**: names and values come from `creation_zone_csv/*.csv`.
- **data**: inferred from real players in the stock db, the Puck Peak db and the Overtime roster save.
- **game**: seen in game.
- **?**: meaning not known; the editor still lets you edit it raw.

## 1. How a player is stored

A player is one `game_id`, with one row in each "per player" table and one roster row per team he belongs to.

| Table | Short | Key | Rows (save / db) | What it holds |
|---|---|---|---|---|
| exhibitionplayerbiotable | ubPc | game_id | 9051 / 9448 | Name, birth, body, contract, draft, team, appearance, personality |
| exhibitionskaterai | dSvy | game_id | 8121 / 8239 | Skater ratings, archetype, growth, traits, AI biases, celebration |
| exhibitiongoalieai | mHuy | game_id | 930 / 1209 | Goalie ratings, archetype, stance, growth, traits |
| exhibitionskaterequipment | xmja | game_id | 8121 / 8239 | Skater gear and colours |
| exhibitiongoalieequipment | fMVl | game_id | 930 / 1209 | Goalie gear and 15-zone colours |
| exhibitionplayers | ZBac | index | 10598 / 10915 | One membership: `index` (what roster rows point at), `playerid` = game_id, `table` (0 normal, 2 and 3 special rows from 56500 up; ?) |
| exhibitionrostertable | eGlu | key | 8625 / 8481 | One row per membership: team, lines, jersey, captain, status |
| exhibitionfreeagents | VoEQ | playerindex | 307 / 358 | Free agents (points at `exhibitionplayers.index`) |
| exhibitionresortskaterextras / goalieextras / playerextras | fuoG / WnSt / MgUe | game_id | 570 / 215 / 44 | Casual (World of Chel) clothes, emotes, glove and helmet colours, heckles, goal horn |
| playernames | BGwe | index | db only, 4925 | Commentary names (`audioid`); the "play-by-play name" picks one |
| stockplayervisorexempttable | zkcJ | game_id | | Players allowed to play without a visor |
| stockcareer*stats | tsSy, USfF, OAqO, nCDp, NPzU | | db | Career stats (not edited) |

All of the save's player tables count in CRCValue values 1 to 8 (VoEQ, mHuy, ubPc, ZBac, eGlu, dSvy, plus qiwI and xjgw).
`RosterCrc` already computes them.

**Memberships.** McDavid has six `exhibitionplayers` rows, so six roster rows:
- Edmonton (team 11)
- Pacific All-Stars (34)
- Canada (137)
- three "Circuit" teams (609, 629, 668, league 23: the NHL Threes circuit)

So "his team" is the membership whose team is an NHL or AHL club (league 0 or 1). The bio's `team` and `proteam` say the same club.

**The db and the save disagree on positions and keys.**
- Rows sit at different positions: McDavid's bio is row 2539 in the save and row 1940 in the db.
- `game_id` is stable between them: McDavid is 10647 in both.
- `exhibitionplayers.index` and the roster `key` are mostly the same, but not always. McDavid's Canada row is index 15152, key 5485 in the db, and index 3886, key 5509 in the save.
- So edits must be matched by game_id plus membership team, never by row or key (section 7).

**The save has players the db lacks, and the reverse.** It has 9051 bios against the db's 9448. The editor lists the save's
players when a roster save is in use, because that is what the game shows.

## 2. Value encodings (one place: `RosterFields`)

- **Fields whose range starts below 0 are stored shifted: stored = value − rangelow.** A −1..1022 team field stores 12 for team 11.
  - This is why bio `team`, `proteam`, `draftteam`, `juniorteam` and gear `team` look like "index + 1".
  - Same for `friendenemycompat*` (−256..255, idle 256 = 0), `growthmod` (−16..15, idle 16 = 0) and `yearwaivereligible`.
  - Stored 0 in a −1 field means −1: none, undrafted, no team. (data, consistent across every field checked)
- **Years** (`year`, `draftyear`, `yearprodebut`, `yearfirstsigned`, `yearufa`, `yearwaivereligible`, `exhibitiondraftpick.year`) are years since 1900.
  - The stock db has Crosby at 87 (1987).
  - **The Overtime / Puck Peak roster subtracts 8 from every year of active players**, so ages come out right on NHL 19's 2018 calendar. Crosby shows 79, McDavid's draft 107 (2015 → 2007 in game terms). Retired, untouched rows such as Chara keep real years.
  - The editor shows the real year with a "years shifted by 8 (Overtime roster)" switch, found by comparing the save with the stock db, and writes the stored value.
- **Birth month and day count from 0**: Crosby, 7 Aug, is month 7, day 6. (CSV `birthmonths`, `birthdays`; data)
- **Height = inches − 54** (5 bits: 54 to 85 in). Checked against stock: McDavid 19 = 73 in (6'1"), Chara 27 = 81 in (6'9"), Crosby 17 = 71 in. (data)
- **Weight = pounds − 120** (8 bits: 120 to 375). McDavid 72 = 192 lb, Chara 130 = 250 lb. The CSV `weights` also starts at 120. (data)
- **Money is in $5,000 steps.** `contractdollars` 2500 = $12.5M (McDavid), 1740 = $8.7M (Crosby). From CSV `salaries`: "$550000" = 110. (CSV, data)
- **Colours** are 24-bit RRGGBB, as elsewhere.

## 3. Bio: `exhibitionplayerbiotable`

| Field | Meaning | Values | Tag |
|---|---|---|---|
| firstname, lastname | Name, 24 bytes each | text | CZ |
| nickname | Nickname | text | ? (empty on stock players) |
| audioid | Commentary id: the player's own id, seen equal to `artid` for McDavid (9857). Not a `playernames.audioid` (McDavid's there is 32419), so which recording it picks is not mapped | number | data |
| position | Position | 0 C, 1 LW, 2 RW, 3 D, 4 G | CZ (Choices) |
| handedness | Shoots / catches | 0 left, 1 right | CZ |
| jersey | Preferred number | 0 to 99 | CZ; the roster row has its own `jersey` too |
| height, weight | Body | see section 2 | CZ (custom), data |
| year, month, day | Birth date | see section 2 | CZ, data |
| birthplace | Birth city, 30 bytes | free text; CSV `cities` (535 names) as suggestions | CZ |
| state | Born in (province, state or country) | CSV `origins` index (0 Canada, 1-13 provinces, 14 USA, 15+ states, then countries) | data |
| intlcountry | Nationality | CSV `origins` `country` (Canada 0, USA 14, others = their own index) | CZ ("Origin") |
| team | Current club | exhibitionteams index (shifted) | data; matches the club roster row |
| proteam | Pro team (creation zone "Pro team") | exhibitionteams index (shifted), CSV `proteams` | CZ |
| draftteam, draftyear, draftround, draftposition | Draft | team index (shifted, −1 undrafted), year, round 1-7, pick overall | CZ, data |
| isdraftable | In the draft pool | flag | ? |
| contractdollars, contractlength, contractis2way, contractismultiyear | Contract | $5k steps, years 0-15, 1-way/2-way | CZ |
| extensiondollars, extensionlength, extensionis2way | Signed extension | same | CZ |
| contractstatus | Contract status | −1 to 6 (? RFA/UFA/ELC kinds) | ? |
| contractneedsentrylevel | Needs entry-level deal | flag | ? |
| yearfirstsigned, yearprodebut, yearufa, yearwaivereligible | Career years | years, section 2 | data |
| rookie, rookieahl, rookiechl | Rookie flags | flags | data |
| retainedsalarytrades | Times traded with salary retained | 0-3 | ? |
| nhlgamesplayed* (career, last season, two and three seasons ago), nhlcareergamesplayedregularseason, nhlcareergoalsregularseason, nhlgamesmissedinjurylastseason, progamesplayedcareer, nhlcareermilestoneaward | Career counters | numbers | data |
| juniorleague, juniorteam | Junior league and club | −1 = none | ? |
| playerclassid, expansiondraftstatus | Franchise logic | ? | ? |
| personalitytype, personalitysubtype, personalitytrait0-6, swagger | Personality (Franchise morale) | small enums | ? |
| moralesub0-4, moraletradedemandstatus | Morale | ? | ? |
| friendenemy0-6, friendenemycompat0-6 | Relationships: another player's game_id and how well they get on | game_id / −256..255 | ? |
| active | Active player | flag | data (retired = 0) |
| ismascot, mascotheadid, mascotbodyid | Mascot players | | data |
| **artid** | Art id: picks the menu portrait `playerportraits/p<artid>.gnf` | number | data (McDavid 9857 → p9857.gnf, in the Puck Peak mods folder) |
| hasportrait | Has a portrait | flag | data |
| headid, headtexture | Head model and its texture (scanned players use their own id; created players use CSV `playerheads` 60000+) | number | CZ |
| hairid, haircolorcode, eyecolorcode, eyebrowid, eyebrowcolorcode, skincolor | Appearance | CSV `appearancehairstyles`, `appearancehaircolors`, `appearanceeyecolors`, `appearanceeyebrow*` | CZ |
| playoffbeardtype, playoffbeardcolor, playoffbeardstart, playoffbeardmax, playoffbeardgrowthrate | Beard and how it grows in the playoffs | CSV `appearancefacialhair*` | CZ |
| casualgear, casualgearfk | Casual-clothes link | | ? |
| introsongid | Intro song | CSV `songs` | ? |
| playercomp | Comparable player | game_id | ? |

## 4. Ratings and play: `exhibitionskaterai` and `exhibitiongoalieai`

**Ratings.** Ratings are 0 to 99. The creation zone commits each slider to these `c_` fields (CZ):

| Skater | Goalie |
|---|---|
| c_speed, c_acceleration, c_agility, c_balance, c_endurance, c_durability | c_speed, c_agility, c_endurance, c_durability |
| c_puckcontrol, c_deking, c_handeye, c_passing, c_poise | c_passing, c_poise, c_aggression |
| c_wristshotpower, c_wristaccuracy, c_slapshotpower, c_slapaccuracy | c_gsl (glove low), c_gsh (glove high), c_ssl (stick low), c_ssh (stick high), c_5hole |
| c_offawareness, c_defawareness, c_shotblocking, c_stickchecking, c_bodychecking, c_strength | c_positioning ("Angles"), c_breakaway, c_reboundcontrol, c_shotrecover ("Recover"), c_pokecheck |
| c_faceoffs, c_aggression, c_discipline, c_fightingskill | c_consistency, c_vision, c_puckplayfrequency |

**What else is in these tables:**
- `c_fighting` (2 bits): how often he fights, 0 never, 1 rarely, 2 sometimes, 3 often. (CZ)
- **`skill_overall` is the displayed overall** (the creation zone's "Overall" global). Category ratings:
  - skater: `skill_skate`, `skill_shoot`, `skill_puck`, `skill_sense`, `skill_defense`, `skill_physical`
  - goalie: `skill_reflex`, `skill_control`, `skill_athletics`
  - Whether the game recomputes them from `c_` ratings is to be seen in game. The editor recomputes nothing and writes what you set; the in-game test decides.
- `c_overall` and `c_potential` are not committed by the creation zone, and McDavid has 74 and 42. Shown raw. (?)
- `c_backskating` is 0 on every player checked. (?)
- **`playerstyle` is the archetype** (CZ Choices):
  - skater: 1 Defensive D, 2 Offensive D, 3 Enforcer D, 4 Two-Way D, 5 Grinder, 6 Playmaker, 7 Sniper, 8 Power Forward, 9 Two-Way Forward, 10 Enforcer
  - goalie: 0 Stand-up, 1 Hybrid, 2 Butterfly
  - The roster row has its own copy; write both.
- **Potential and development.** NHL 19 shows potential as a tier (Elite, Top 6, ...) and a likelihood.
  - The growth fields: `growthtier` −1..14, `growthletter` −1..6 (the likelihood letter), `growthspeed` −1..14, `growthage` 0..63, `growthmod` −16..15 (stored shifted).
  - McDavid has tier 1, letter 2, speed 2. How the tier and letter are labelled is to be seen in game. (data, ?)
- `trait0`-`trait31`: the player's traits as trait ids, from CSV `playertraits`. 1 Net Presence, 16 Dangler, 23 Speedster, 25 Setup Man, 26 Perimeter Sniper, 27 Big Shooter, and so on, 0 = none.
  - McDavid: 3, 4, 7, 11, 12, 13, 16, 23, 24, 25, 26 and on.
  - Goalie traits are 3 bits (0-7). (CSV, data)
- `off_defbias`, `pass_carrybias`, `shoot_passbias` (0-15): AI tendencies, as named in CSV `playertraits` "bias". (CSV)
- `skatingstyle`: CSV `skaterskatingstyles` (3 styles). (CZ)
- `goalcelebration`: CSV `gamecelebrations` animation id. (CZ)
- `goalsongid`: CSV `songs`, −1 none. (CZ)
- `animpack` is ?
- Goalie only: `stance` (CSV `goaliestances`, 41 stances), `warmup` 0-15, `c_paddledown` flag.

## 5. Gear: `exhibitionskaterequipment` and `exhibitiongoalieequipment`

**Skater gear:**
- helmetbrand (CSV `skaterhelmetstyle`), gloves (`skaterglovestyle`), stick (`stickdisplayinfo`), skatebrand (`skatedisplayinfo`)
- visortype and visorcolor (`skatervisortypes`, `skatervisortint`), earguard, mouthguard (+ anim, material, colour)
- shoulderpads (`skatershoulderpads`), jerseytuck (`skaterjerseytuck`), pantsbrand, pantstypecode (`playerpantlength`)
- skate colours and rocker, guard
- socks: tuck, tape style, colour, alpha, transparency
- stick: curve, flex, blade and shaft tape type, style and three colour zones

**Goalie gear:**
- mask (maskid, masktype), neckprotector
- blocker, trapper and pads, each with 15 zone colours
- stick with 6 zone colours, tape colour
- `showcustomcolors` and `showcustomstick` (else the team's gear colours), `keepcustomiftraded`, `keepcustomstickiftraded`

Every gear field is CZ. `stockplayervisorexempttable` lists players allowed no visor.

## 6. Roster row: `exhibitionrostertable` (one per membership)

| Field | Meaning |
|---|---|
| key | Row key (unique) |
| playerindex | `exhibitionplayers.index` of this membership |
| team | exhibitionteams index (not shifted: range 0..1023) |
| jersey, playerstyle | Copies of the number and archetype for this team |
| captain | 0 none, 1 captain, 2 alternate (237 and 796 rows) |
| rosterstatus | 1 dressed / on the active roster, 0 not (1825 rows; scratches, minors?) |
| l1-l4 lw/c/rw | Even-strength forward lines 1-4 |
| l1-l3 ld/rd | Defence pairs 1-3 |
| pp1, pp2 lw/c/rw/ld/rd | Power play units (5 on 4) |
| pp4_1, pp4_2 lw/c/ld/rd | Power play 4 on 3 |
| pk4_1, pk4_2 lw/c/ld/rd | Penalty kill 4 men |
| pk3_1, pk3_2 c/ld/rd | Penalty kill 3 men |
| ot_1-ot_3 lw/c/ld/rd | 4-on-4 overtime units |
| ot3_1-ot3_3 c/ld/rd | 3-on-3 overtime units |
| three_c, three_w, three_d, three_g1, three_g2 | NHL Threes roster |
| s1-s5 | Shootout order |
| g1, g2 | Starting and backup goalie |
| x1, x2 | Extra attacker 1 and 2 (?) |

Each slot is one flag on the player's row, so a line is "whoever has `l1c` set on this team". A player can hold many slots
(McDavid holds l1c, pp1c, pk4_1c, pk3_1c, ot_1c, ot3_1c, three_c, s1 and x1 on Edmonton). The editor shows a team's line
card and moves flags between players; it never leaves a slot with two players or none it had before.

**Moving a player to another team:**
- Change his club membership's `team` in place and clear its line flags.
- Set bio `team` (and `proteam` for NHL clubs).
- Then give him the new team's open slots, or leave him as a scratch (`rosterstatus` 0) for you to place.
- Rows are not removed or added. Whether the game fixes lines itself after a trade is to be seen in game.

**Free agents.** Free agents are listed in `exhibitionfreeagents` by membership index. Releasing a player moves his club
membership there.

## 7. Saving: project database plus roster save

Edits go into the project's `nhl_game.db` (one undo step per edit, through the project). `RosterSync` (Build tab button,
`roster-sync` CLI) then copies them to `RosterCustom0`. The generic sync matches rows by `index`, which does not work for the
player tables, so those go through `Roster.PlayerSync`:
- **Per-player tables** (bio, AI, gear, resort extras): rows that differ between the game's db and the project's are found in
  the save by `game_id`, since the save keeps players at other rows.
- **Roster rows**: found by game_id plus the team the game's db has the membership on. `key` and `playerindex` are never copied.
  A move changes that row's `team`.
- **Line slots are placed by slot, not copied by cell.** The save keeps the user's own lines, which differ from the db's
  (seen: Draisaitl at Line 2 C in the save but not the db). So:
  1. a moved player loses every slot and the captaincy the save gave him on his old team;
  2. on each team whose lines the project changed, each changed slot goes to the project's holder and is cleared from everyone
     else the save has on that team;
  3. a slot the project emptied is taken only from the player the db had there.
  A cell-by-cell copy left two players on Line 2 C, and the game kept the save's own one (seen in game 2026-11-20).
- Added membership rows and added players are reported as skipped, never guessed.
- Writing keeps the timestamped backup and read-back check `RosterSync.Write` already has.

## 8. Portraits and visuals

- **Menu portrait** is `nhl/ui/kettle_assets/playerportraits/p<artid>.gnf`; there are 12,912 of them, and Puck Peak puts newer ones in its mods folder (McDavid p9857 in `patch_game_97.big`).
  - The editor shows it, and "Replace portrait" imports a PNG through the existing texture import into the project at that path.
  - A player with `hasportrait` 0 shows the silhouette.
  - To be seen in game: whether a new `artid` with a new portrait file draws, or whether the portrait list is gated like kit names were (if so, borrow an unused portrait id).
- **Country flags** are in `kettle_assets/countryflags/` for the nationality dropdown.
- **Team logos** come from the Teams tab's logo lookup.
- **Head, hair and face** are model ids, not pictures. The editor gives dropdowns with the CSV names; previewing the 3D head is out of scope.

## 9. Adding and removing players

**Adding.** Adding clones a whole row set from a template player:
- bio, AI, equipment, one `exhibitionplayers` row and one roster row
- each with a new game_id, membership index and key above the highest in both the db and the save
- every table has spare allocated rows

The sync will then have to add the new rows to the save (not built).

**Removing.** Removing is "release" (free agent) or "retire" (`active` 0, no club membership). Rows are never deleted:
- the save's CRC skips records whose last byte has 0x80 set, which looks like a deleted-row mark the game uses
- `TdbTable.RemoveRow` shifts rows, which would break every index that points at them

Both, adding especially, wait until the edit path is seen working in game.

## 10. Seen in game, and still to see

Seen in a sandbox copy of the user's roster save (2026-11-20):
1. An edited name shows (McDavid as "Testy Blueline").
2. An edited `skill_overall` shows (Draisaitl at 50).
3. A moved player shows on the new team (Matthews TOR → EDM), and on the slot he was given (Line 2 C) once slots were placed
   by slot.

Still to see:
1. Whether the overall recomputes from lowered `c_` ratings when `skill_overall` is left alone.
2. What `growthtier` and `growthletter` show as potential.
3. Whether a new portrait id draws.

## 11. The Studio tab (Create → Roster)

`src/Blueline.App/Modules/RosterModule.cs`, built on `RosterBook`:
- **List**: every active player (tick Retired for all), filtered by team (or free agents), position group and a search on
  name, number or game_id. Sorted by overall. Shift/Ctrl-click picks several.
- **One player**: the portrait (from `PortraitPath(artid)`) with Replace or drop a picture, name, number, position, team,
  overall and archetype at the top; then Move to another club; then every field in collapsible groups: sliders for 0-99
  ratings, lists for choices and teams, ticks for flags, hex boxes with a swatch for colours, text for the rest (heights take
  6'2", money takes 12.5M). "Find a field" filters by label or database name.
- **Several players**: ratings up or down (overall optional), one field set for all, move all to a club.
- **Lines**: with a team chosen, one dropdown per slot, grouped (even strength, power play, penalty kill, overtime, shootout and
  extra attacker, threes). `SetSlot` swaps when the incoming player held a slot of the same kind.
- **Saving**: edits change a working copy at once and are kept as a list; Save replays them onto the project's current
  database (so other tabs' saved edits stay) as one undo step. Discard throws them away. Portraits save straight to the
  project as their own undo step. "Real birth years" shows and takes years without the roster's year shift.
- The screenshot tour has steps `05z1`-`05z5` (`BLUELINE_TOUR_ONLY=05z`).

Not yet: dragging players between line slots (dropdowns for now), adding new players, and a portrait for a new art id seen in
game (the file is new to the game, so it relies on the build adding new files).
