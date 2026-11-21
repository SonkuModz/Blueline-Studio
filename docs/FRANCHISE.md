# Franchise mode (Be a GM) in NHL 19

Research from 2026-11-18, then the first pieces were built (section 7). Nothing has been written to a real save or
installed. The game is CUSA11126 1.60
under shadPS4. Puck Peak Overtime is in `NHL 19/CUSA11126-mods`.

Each fact carries a label:

- **Verified**: read from the files or checked by a test.
- **Inferred**: follows from the data, but not tested in game.
- **Unknown**: not worked out yet.

Franchise mode is called "Be a GM" inside the game (`BeAGM`, `BE_A_GM`, `dynastyMode`) and "GM Connected" in the UI folders (`gmconnected`).

## 1. The save

### 1.1 Where it is

`%APPDATA%\shadPS4\home\1000\savedata\CUSA11126\`, next to `Profile0` and `RosterCustom0/1`:

| Folder | Size of DATA | Written | File info | |
|---|---|---|---|---|
| `BeAGM0` | 1,352,403 | 2026-11-18 17:27:03 | `"IsBackup":false` | The Franchise save |
| `BeAGM15` | 1,351,979 | 2026-11-18 17:26:46 | `"IsBackup":true` | A backup twin, written 17 s earlier |

**Verified.** Both saves have the same file info: `TeamFullName` "Buffalo Sabres®", `PlayerLastName` "", `CurrentSaveId` 0,
`SeasonYear` 20180911 and `UserName` "DynastyTest". Slot 15 seems to be where the game keeps the backup. **Inferred.**

### 1.2 Container: like the roster save, with three differences

The outer layer is the same as RosterCustom0. DATA is a run of `[u64 length][8-byte tag][payload]` records: the backup
flag (JSON), a version block ("1.60"), the file info (JSON) and the payload. The payload is `0x01`, then an LZ4 frame,
then a u32 of the unpacked size (1,625,518 for BeAGM0). INDEX entry 0 holds 16 + the payload length at +0x28, the
payload's offset at +0x30 and the DATA size at +0x38. **Verified.**

How it differs from the roster save (**Verified**):

1. **There is no `CRCValue`** in the file info. The roster checksum (RosterCrc) does not apply here.
2. **DATA has a gap in BeAGM0.** One stray byte (`0x7D`, a second `}`) sits at 0x11C, between the file info and the
   payload. INDEX's +0x30 points past it, to 0x11D. BeAGM15 has no gap. INDEX is the authority:
   `RosterSave.Read` walks the records one after another, so it would reject BeAGM0.
3. **The payload is packed twice.** The outer frame unpacks to four inner blocks, each `0x01` and an LZ4 frame with
   no size tail after it, followed by raw bytes:

| Block | Unpacked size | What it is | |
|---|---|---|---|
| 0 | 17,096 | Starts with u32 17,092 (its own size minus 4), then small u16 values | **Unknown** (league or calendar state?) |
| 1 | 475,339 | Header: u32 31,915, u32 488, u32 40. Then float records, then line names `$TEAMLINE1_ANA`, `$TEAMLINE2_ANA` and so on for every team, then leftover heap memory (pieces of UI layout JSON, `$SEATTLE`, `Kraken`) | **Unknown**: team and line data |
| 2 | 3,349,888 | u32 size, u32 count **4,379**, then **760-byte player records**, then 21,840 more bytes | **Inferred**: the player pool (see 1.4) |
| 3 | 1,923,236 | **A TDB** (`DB\0\x08`) with 34 `league*` tables. All 70 table checksums verify | **Verified** (section 1.3) |
| tail | 198,112 | Raw bytes, starting `00 00 ff ff ff ff …` | **Unknown**. BeAGM0 has one extra leading `00`. Once aligned, only 17 bytes differ from BeAGM15 |

Writing a save back is **not proven**. The first test is an unedited repack: unpack, then pack all five parts again
unchanged, and see whether the game loads it (section 5, step B0).

### 1.3 The TDB block: 34 tables

The field names come from `nhl_game-meta.xml`, which also names the empty `league*` templates in `nhl_game.db`. The
row counts below are from BeAGM0. The franchise was created and not played yet: no games, no stats and no tweets.

| Table | Rows | Fields and sample values |
|---|---|---|
| `leagueteams` | 126/160 | `teamname`, `cityname`, `abbrname`, `league` (0 NHL, 1 AHL, 11 OHL, 12 QMJHL, 13 WHL, 16 custom, 17), `ahlaffiliate`, `arenaid`, `prestige` (1 for every team), `avgattendance` (80 to 100), `rivalteam`/`rivalteam2`, `morale`, `moralechemistry`, `moralemeetingdate`/`result`, the fe/pres/overlay colours, `goalsongid`, `winsongid`, `powerplaysongid`, `intromusicid`, the mascot fields, goalie masks, `divisiongroup`, `conferencegroup`, `playoffstatus` |
| `leaguearena` | 126/280 | `arenaname`, `cityname`, `maxattendance` (Honda Center 17,174), `yearopened`, `goalhorn` and its parts, `periodhorn`, `announcer`, about 40 colour zones |
| `leaguegmdata` | 31/48 | One row per NHL club. `firstname`/`lastname` are the AI general managers' names (KASEY CARROLL, BRAD JONES and so on; the user's GM is "DynastyTest"). `salary` is the team payroll in dollars (79,515,000 to 103,260,000). Also `salarynextyear`, `cbaexpenseyear0..24`, `retainedtradesalaryyear0..24`, `numretainedsalariesyear0..24`, `misccapexpenses`, `stanleycups` (0), `standings1..3`, `mustrenewskaters`, `mustrenewgoalies`, `salarydatecalculated` |
| `leaguestates` | 627/800 | Key/value league state. `value` carries a flag in bit 31; take it off to read the number. Type 0 is the current date (43,352). **Types 25 to 29 are the cap rules in dollars: 850,000 / 20,800,000 / 1,025,000 / 76,900,000 / 104,000,000.** Read as minimum salary, maximum salary, a third limit, floor and cap; **Inferred** from the values |
| `leaguetasks` | 45/512 | The calendar of league events: `type`, `startdate`, `enddate` (from 2018-09-09 to 2019-09-11) |
| `leaguescheduletable` | 2,492/4,500 | `month`, `day`, `home`, `away`, `league`, `round`, `status`. NHL and AHL games |
| `leaguerostertable` | 1,528/6,400 | Player to team, `rosterstatus`, `captain`, `playerstyle`, and every line slot as a 1-bit flag (`l1c`, `pp1lw`, `pk4_1rd`, `ot3_2c`, `three_c` and so on) |
| `leaguefreeagentdata` | 1,232/8,000 | `age`, `overallrtg`, `potentialrtg`, `salary` (170 for most), `expectedcash`/`expectedcash2` (1,175, 950, 535), `expectedyears`, `importancefactors0..7`, `restrictedstatus`, `tradeagetier`, `tradequalitytier`, `adjtradeval`, `responsereviewdate`. The money **unit is Unknown** (perhaps $10,000) |
| `leaguefreeagentmiscdata` | 1 | Free agent counts by position (C 311, W 429, D 396, G 96) |
| `leaguedraftpick` | 1,302/1,400 | `year` (119 means 2019), `round`, `origteamid`, `owner`, `pickoverall` |
| `leaguetradeblock` | 5,902/30,000 | `teamid`, `itemindex`, `valuetype`, `value`: what each AI team wants and has spare |
| `leaguetradeoffers` | 0/10 | Up to 5 assets each way, `status`, `timeaccepted` |
| `leaguegmlogic` | 247/480 | `index` → `value`, pairs such as 834, 9. **Unknown** what they mean (AI GM state?) |
| `leagueplayerworth` | 48/50 | Value curves (999, 857, 826 …) |
| `leaguescoutmiscdata` | 1 | `draftquality0..24` (how strong each future draft class is, 0 to 2), `seasoncompletionpct` |
| `leaguestaffdata` | 0/48 | Scout and trainer grades, scouting assignments (`scouttarget0..29`) |
| `leaguetransactiontable` | 500/500 | `transaction`, `date`, `team`, `team2`, `data1..6`. A ring buffer, all type 18 so far |
| `leaguetweets` | 0/500 | The news feed: `tweettextid`, `authorid`, `hashtag`, `param1..10`, `createdate`, `score` |
| `leagueplayermoralelog` | 0/500 | `playerid`, `moraletype`, `reason`, `moraledelta`, `friendid` |
| `leaguevillaintable` | 0/250 | Grudges from injuries: hero and villain player and team, `injurytype`, `date` |
| `leagueawardstable` | 0/2,500 | Awards won in this franchise |
| `leagueretirementtable` | 0/700 | Retired players |
| `leagueteamstats` | 0/7,500 | Standings, streaks, home and away splits by year |
| `leaguegamestats` | 0/4,500 | Box score lines |
| `leagueplayerstreak` | 0/12,000 | Point and goal streaks |
| `leaguecontractoffers` | 0/6,000 | Offers: dollars, length, two-way, status |
| `leaguerfatable` | 0/100 | Offer sheets |
| `leagueplayerbuyouttable` | 0/100 | Buyouts |
| `leagueteamstrategies` | 126/160 | Coach sliders per team |
| `leagueteamequipmenttable`, `leagueteamjerseys`, `leagueteamcolorstable` | 117, 9, 0 | Gear, kits and colours of teams made in the franchise |
| `leaguegamemodesvars`, `leaguetempscheduletable` | 0 | |

Dates are day counts from 1899-12-30, as in spreadsheets: 43,352 is 2018-09-09. **Inferred**, because the dates agree
with `SeasonYear` and the season's span.

**Not in the TDB:** a player table, owner goals, finances (ticket prices, budget, income), marketing, facility levels,
and the news text itself. They are probably in blocks 0 to 2 or the tail. **Unknown.**

### 1.4 The player records (block 2)

There are 4,379 records of 760 bytes, starting at +8. The first is Laurent Brossoit:

| Offset in the record | |
|---|---|
| +0 | Birthplace, ASCII ("SURREY") |
| +40 | First name |
| +65 | Last name |
| about +0x18E to +0x230 | Rating runs, one byte per rating, around 70 to 90 ("ORRPKRN…") |

**Inferred.** Contract, age and potential are probably here too, but the layout is **Unknown**. The block also holds
modern prospects such as Mintyukov, so it comes from the Overtime roster.

### 1.5 What was made at creation time

- **The cap is baked in.** `leaguestates` holds 104.0M cap and 76.9M floor. Stock `capdefs.brk` says 79.5M and 58.8M.
  Puck Peak's `capdefs.brk` says 104.0M and 76.9M (section 2.1). So the franchise took the cap rules of the files in use
  when it was created. **Verified** for the values. Whether later seasons re-read `capdefs.brk` is **Unknown**.
- **AHL affiliations.** `leagueteams.ahlaffiliate` stores the farm team's row + 1, with 0 for none. **Inferred**: read
  that way, 29 of the 31 legacy clubs land on their real 2018-19 farm team (ANA San Diego, BOS Providence, BUF
  Rochester, CAR Charlotte, EDM Bakersfield, VGK Chicago, VAN Utica, STL San Antonio and so on). So these are the
  **2018-19 pairings, not today's**. The odd rows:
  - Row 22, SEA "Kraken", is a custom team (`teamtype` 1, `league` 16) with `ahlaffiliate` 1, which would be row 0,
    Anaheim. That is meaningless as an affiliation.
  - Row 1 is Winnipeg, in what was Arizona's slot, so Tucson (row 59) has no parent.
  - Puck Peak's Lua juggles team ids 22 (Utah) and 499 (Seattle) on the team select screens (section 2.4). Its
    roster also renames AHL abbreviations (Binghamton "ABB", San Antonio "HSK", Bridgeport "HAM", Stockton "CGY"),
    but the AHL team names are still from 2018.
- **Arenas.** The 2018 names are still there: Bell MTS Place, Pepsi Center, Staples Center, BB&T Center, Quicken
  Loans Arena. AHL arenas all have `yearopened` 2018. Seattle has CLIMATE PLEDGE ARENA (2021).

### 1.6 What the save wins over

As with the roster save, everything in a Franchise save is the game's state from then on. Edits to `nhl_game.db` or to
the roster save after creation do not reach an existing franchise. **Inferred**: the save holds its own copy of every
table, so nothing is left to read from the db. A franchise starts from:

- The active roster save (RosterCustom0 here): `exhibitionteams` (with `ahlaffiliate`), `exhibitionarena`, players and
  rosters, `nhlschedule`/`ahlschedule`/`echlschedule`, `stockteamsalaryextras`.
- `nhl_game.db` for what the roster save does not hold: tweet texts, authors and hashtags, award history
  (`stockawardstable`), career stats, retired numbers and `playernames` (generated names for prospects).
- `boot.big` for the `.brk` tuning (cap rules and so on).

## 2. The game side

Where each kind of file comes from. **Verified** in both mounts (stock, and with Puck Peak's mods folder):

| Files | Archive | Can a `patch_*_99` override it? |
|---|---|---|
| `gamemodes/league/resource_kernel/*.brk` | `boot.big` | **No** (RESEARCH 6.9). Only a whole new `boot.big` |
| `ui/features/gmconnected/**` (Lua, layouts, nav, dd) | `boot.big` | **No**, same reason |
| `gamemodes/service-defs/**.json` | `boot.big` | **No**, same reason |
| `kettle/loc/eng_us.db` (text) | `patch_inxp_56` | **Yes**, proven (the French title test) |
| `database/t3db/nhl_game.db` | `patch_inxp_56` | **Yes**, the same route. Puck Peak ships its own copy, so an override has to start from that one |
| `database/noa/nhl_gm_simulation.data` | `patch_inxp_56` | Yes |
| `creation_zone_csv/franchise*.csv` | `inxp_00` | Yes |

### 2.1 Tuning: `resource_kernel/*.brk`

There are 21 files. They start with `03 00` and end with `BRK`. They hold u16 and u8 tables with hashed ids and no
names. The eboot names each file.

| File | Size | Meaning, from the name |
|---|---|---|
| `capdefs.brk` | 170 | **Decoded**, see below |
| `franchisemode.brk` | 79,446 | Franchise rules, with about 97 tables |
| `gmbrain.brk` | 11,808 | AI general managers (trades, signings) |
| `worth2.brk` | 4,309 | How players are valued |
| `progression.brk` | 14,304 | Player growth and aging |
| `rookiegen.brk` | 4,159 | Generated draft classes |
| `playermorale.brk` | 23,912 | Morale |
| `injury.brk` | 3,458 | Injuries |
| `rfaufa.brk` | 935 | Free agency (RFA/UFA) |
| `scouting.brk`, `staffing.brk` | 56,028, 5,657 | Scouts and staff |
| `ownermode.brk` | 576 | Owner mode |
| `interviews.brk`, `tweets.brk` | 41,904, 25,119 | When interviews and tweets fire |
| `leagueinfo.brk`, `simengine.brk`, `linesmgr.brk`, `miscfeatures.brk`, `legacyrefactor.brk`, `onlineplayerclass.brk`, `techproto.brk` | | League setup, the sim engine, auto lines and others |

**capdefs.brk.** From offset 0x14 it is u16 values: four row offsets (2, 14, 26, 38), then floor and cap in
$100,000s, then 12 yearly floor rises, 12 yearly cap rises (both in $1,000s, years 0 to 11) and 24 yearly minimum
salaries in $1,000s:

| | Floor | Cap | Floor rises | Cap rises | Minimum salary, years 0 to 23 |
|---|---|---|---|---|---|
| Stock | 588 (58.8M) | 795 (79.5M) | 0, then 500 × 11 | 0, 1000, 1000, 1000, then 500 | 650, 700, 700, 750 … 1050 (the 2018 CBA) |
| Puck Peak | 769 (76.9M) | 1040 (104.0M) | 0, 7000, 10450, then 1500 | 0, 9500, 14000, then 1500 | 850, 900, 950, 1000 … 1300 |

**Verified** for the bytes. The meaning of the rows is **Inferred**: floor 76.9 + 7.0 = 83.9M and cap 104.0 + 9.5 =
113.5M match the real 2027-28 figures. **Verified in game, by a third party:** Puck Peak's edited `capdefs.brk` in its
own `boot.big` loaded, since the new franchise got 104.0M (section 1.5). No other `.brk` file is decoded: they are
located only. All other `.brk` files are byte-identical between stock and Puck Peak.

### 2.2 Databases

`nhl_game.db` holds the empty `league*` templates and:

- **News and tweets:**
  - `tweetstexts`: 968 rows linking tweet types to `TXT_TWEET_*` text keys.
  - `tweetsauthors`: 21 rows of **placeholder names**: "team", "analyst 1" to "analyst 10" and "player 1" to
    "player 10" for both `username` and `userhandle`.
  - `tweetshashtags`: 6 placeholders such as "teamHashTagSample".
  - `stocktweets`: 127 rows, the context rules for when a tweet fires.
- **History:** `stockawardstable` (427 rows), `stockcareerseasonplayerstats` (15,555), playoff and goalie career
  stats, `stockcareermiscstats` (3,857), `stockretirednumbertable` (357), `stockseasonteamstats`,
  `stockplayoffteamstats`.
- **Schedules:** `nhlschedule`, `nhl32schedule`, `nhlfutureschedule`, `ahlschedule`, `ahl32schedule`, `echlschedule`,
  CHL and European leagues.
- **Other:** `stockleagues` (29 leagues), `playernames` (4,925 generated first and last names),
  `stockteamsalaryextras` (31 rows of dead cap and retained salary per year).

Puck Peak's `nhl_game.db` changes the schedules, players and free agents. Its tweet, award and league tables match
stock. **Verified.** `roster/entry_draft.db` and `expansion_draft.db` are their own TDBs, not looked at yet.

`nhl_gm_simulation.data` is a RIFF `BDP0` file with reflection data. It is a dictionary of game variables
(`Team_IsOnSeasonTicketDrive`, `Arena_UpperBowlAttendance`, `Team_MerchLeagueSalesRank`, `Player_InjurySeverity` …),
not tuning. **Inferred.**

### 2.3 Text: `eng_us.db`

About 38,000 strings. The Franchise ones:

- **Tweets:** `TXT_TWEET_*`, 969 of them, in about 14 numbered variants per event. Events include FREE_AGENT_SIGNING,
  TRADE, PLAYER_SENT_DOWN, WAIVER_CLAIM, OFFER_SHEET_*, AWARDS, CLINCH_*, RECAP_*, PREVIEW_* and INJURY_RETURN.
- **Interviews:** `TXT_INT_QUEST_*` questions and `TXT_INT_RESP_*` answers from the player, teammates and others.
- **Owner messages:** `TXT_OWNER_*` and `TXT_TEAM_OWNER_MSG_*`, the owner's notes about goals, relocation and
  negotiation.
- **Morale, scouting, contracts, budget and concessions:** a few hundred strings each.

Placeholders look like `[<%1 pn>]` (player name), `[<%1 pp>]` (position), `[<%2 tnn>]` (team nickname), `[<%2 tf>]`,
`[<%3 tc>]` (team city), `[<%1 ta>]` (team abbreviation), `[<%4 mn>]` (money) and `[<%3 n>]` (number). Some lines are
weak, such as "Back to the minors for [<%1 pn>] #poorguy".

### 2.4 The UI: `nhl/ui/features/gmconnected` (you asked for notes)

The Franchise screens are **plain text, not Flash**: 94 `.nav` state machines, 126 `.vvm` bundles, 128 `.lua` view
models, 118 `.layout` JSON widget trees and 88 `.dd` data-binding files, all in `boot.big`. A `.vvm` lists its `.dd`
files, Lua and layouts. The Lua asks the game's service layer for data through `api("ServiceRequest")`. Those
services are described in `gamemodes/service-defs` (352 data, 235 job and 381 view definitions, with names, types and
Min/Max bounds), which makes a good map of what each screen can read and set.

There are 67 screen bundles in `vvms/`:

- **Hub:** `hubs`, `hubsmain`, `hubsupdates`, `subhubs`, `activityfeed` (the news feed), `leagueinfo`,
  `leagueoverview`, `statsandstandings`, `teamrankings`, `fullcalendar`.
- **Owner and money:** `ownergoals`, `owneroperationsbudget`, `marketingbudget`, `balancesheet`, `setprices`,
  `promotions`, `fanappreciationevents`, `upgradeandmaintain`, `relocatefranchise`, `negotiatewithcity`.
- **Roster and contracts:** `viewcontracts`, `listallcontracts`, `retainsalary`, `buyout`, `freeagents`,
  `freeagentsoffer`, `offersheetcompensation`, `protectionlist`, `captainsandjerseys`, `ahlteamselect`,
  `playermorale`, `injuryreport`, `progressreport`.
- **Trades:** `proposetrade`, `managetradeblock`, `browsetradeblocks`, `surplusandwants`, `editsurpluswants`.
- **Scouting and draft:** `scouting`, `scoutingscreen`, `assignment`, `scoutplayer`, `scoutedplayers`,
  `allprospects`, `drafting`, `draftsummary`, `editpicks`, `expansiondraft`, `fantasydraft`.
- **Setup:** `createnewgm`, `startcareer`, `teamselection`, `offlinecareersettings`, `modeselect`.

The code is readable. For example, `activityfeed.lua` sets `NUMBER_OF_ITEMS_ON_SCREEN = 6` and has a table of
leagues (NHL 0, AHL 1, CHL 2, OHL 3, QMJHL 4, WHL 5).

**Can it be edited?** Yes, but only through a whole `boot.big`. Puck Peak's `boot.big` changes 12 files against stock
(**Verified** by diff):

- `capdefs.brk`
- `homepage.lua` and its `.layout`, to hide the online modes
- `playnowmenu.lua` and its `.layout`
- `selectteam.lua` and `nhlselectteam.lua`, to swap Utah and Seattle on the Be a GM and Season team lists
- the Be a Pro creation flow
- three `league_creation/competition.json` service defs

These run in game for the user, so **edited Lua, layouts and service defs load when they come in a replaced
`boot.big`**. **Inferred**, strongly: the cap value proves their `boot.big` loads. A `patch_inxp_99` copy of a
`boot.big` path was ignored (RESEARCH 6.9).

Blueline's `BootArchive` already builds a whole `boot.big` from the one the game reads (Puck Peak's) with a project's
edits on top. A Blueline-built `boot.big` has not been seen in game yet. On the real install it would have to replace
Puck Peak's own `boot.big` in `CUSA11126-mods`, which is off limits and gets overwritten when Puck Peak reinstalls.
In the sandbox, it can go into a sandbox copy of the mods folder. That decision is yours.

The diffs are saved in the session notes folder, under `franchise/ppdiff`.

## 3. What is editable, and where

| Want to change | Existing franchise | New franchises |
|---|---|---|
| Affiliations, arenas, team names, prestige, attendance, AI GM names | Franchise save TDB (`leagueteams`, `leaguearena`, `leaguegmdata`). Needs a save writer, not proven | Roster save (`exhibitionteams.ahlaffiliate`, `exhibitionarena`). The roster sync route is proven |
| Schedule | `leaguescheduletable` (save) | `nhlschedule` and friends in the roster save |
| News text, interviews, owner messages | `eng_us.db`: the save stores text ids, so edits show in an existing franchise too (**Inferred**) | Same |
| Tweet authors and hashtags | `nhl_game.db` `tweetsauthors`/`tweetshashtags`. Whether a running franchise reads them live is **Unknown** | `nhl_game.db` |
| Award history, career stats, retired numbers | Existing: probably copied in at creation (**Unknown**) | `nhl_game.db` `stock*` tables |
| Cap, floor, minimum salary | `leaguestates` types 25 to 29 (save) | `capdefs.brk` (`boot.big`) |
| Progression, morale, injuries, trade AI, free agency | Not decoded | The `.brk` files (`boot.big`) |
| Screens | — | gmconnected Lua and layouts (`boot.big`) |
| Contracts, ages, potential of players | Block 2 of the save, not decoded | Roster save players |

## 4. Immersion ideas, ranked by how feasible they are

Ranked by delivery route first (proven routes first), then by payoff.

**Tier A: proven routes, low risk**

1. **News that reads like a real feed.**
   - Give the 21 `tweetsauthors` rows reporter and insider names and handles. They are "analyst 1" today.
   - Make the 6 hashtags real (#GoSabres-style per team, if the owner id allows it).
   - Rewrite weak `TXT_TWEET_*` lines.
   - *Touches:* `nhl_game.db` (built from Puck Peak's copy) and `eng_us.db`, in a `patch_inxp_99`.
   - *Test:* sandbox, a new franchise, sim a week, open the activity feed. Decode the re-saved save: `leaguetweets`
     shows which author and text ids fired.
2. **Better interview and owner text.** More variety, today's leagues and wording, fixed "AHL" mentions.
   - *Touches:* `eng_us.db` (`TXT_INT_*`, `TXT_OWNER_*`).
   - *Test:* a loaded franchise, an interview or the owner inbox.
3. **Real AHL affiliations (2025-26)** for new franchises, for example CAR Chicago, VGK Henderson, VAN Abbotsford,
   SEA Coachella Valley, UTA Tucson, NJD Utica, FLA Charlotte, STL Springfield.
   - *Touches:* `exhibitionteams.ahlaffiliate` in the roster save, through the existing roster sync after a backup
     and your OK. New AHL clubs need a renamed AHL slot, since all 31 are used.
   - *Test:* create a franchise in the sandbox. Decode `leagueteams.ahlaffiliate`, then look at the AHL team select
     and send-down screens.
4. **Today's arena names and capacities** (Canada Life Centre, Ball Arena, Crypto.com Arena, Amerant Bank Arena,
   Rocket Arena; AHL arena years).
   - *Touches:* `exhibitionarena` in the roster save, which a new franchise copies into `leaguearena`.
   - *Test:* the team info and game intro screens.
5. **Award history up to 2026.** Trophy winners and retired numbers, so the history and awards screens look current.
   - *Touches:* `nhl_game.db` `stockawardstable` and `stockretirednumbertable`.
   - *Test:* the awards screen in a new franchise. Whether existing ones pick it up is unknown.

**Tier B: needs a Franchise save writer (the format is decoded; writing it back is not proven)**

6. **Fix an existing franchise:** the affiliations, the Kraken's farm team, arena names, real AI GM names in
   `leaguegmdata` (today "KASEY CARROLL" and so on), varied `prestige`/`avgattendance` instead of 1 for everyone, and
   rivals.
   - *Touches:* the BeAGM0 TDB (block 3).
   - *Needs:* a writer that follows INDEX, keeps blocks 0 to 2 and the tail, and repacks inner and outer frames plus
     the u32. It would build on `RosterSave`'s LZ4 code.
   - *Test:* step B0, an unedited repack loaded in the sandbox, then one field.

**Tier C: needs a whole `boot.big` (works for Puck Peak; a Blueline build is not seen in game)**

7. **Franchise UI changes**, which you asked about. Examples: more items in the activity feed, labels, a cap summary on
   the hub, screens that hide things that do not work offline.
   - *Touches:* gmconnected Lua and layouts, through `BootArchive` built on Puck Peak's `boot.big`.
   - *Test:* the sandbox only, with a sandbox copy of the mods folder. The gate is a one-line test change (a label in
     `activityfeed`) seen in game.
8. **Cap rules.** Fine-tune the growth and minimum salary rows in `capdefs.brk`, which is decoded.
   - *Test:* a new franchise, then read `leaguestates` types 25 to 29.

**Tier D: research first**

9. **Progression, aging, injuries, morale, trade AI and free agency.** The `.brk` files are found but not decoded.
   Map them by changing one table and diffing saves after a sim. High effort.
10. **Contracts, ages and potential in an existing franchise.** Decode the 760-byte player record in block 2,
    starting with the fields the contract screens show.

## 5. How to test

- **Sandbox only.** Use the hardlinked emulator in a scratch folder with its own `user` folder and networking off (see
  the shadPS4 notes). Copy the Franchise save into the sandbox's savedata. Afterwards, check that the real savedata and
  config hashes have not changed.
- **You drive the menus.** No input is sent from here, and the game window is not captured. Each test therefore ends
  with you saving in game. Decoding the re-saved Franchise save is how a change is measured.
- **Step B0**, before any save edit: unpack BeAGM0 and pack it again unchanged with the same tags and the stray byte
  kept, then load it in the sandbox. If it loads, try one field (for example an arena name).

## 6. Tools used

A scratch probe in the session notes folder (`franchise/`):

- `tools/lz4f.py`: an LZ4 frame reader.
- `fprobe`: a .NET console app on Blueline.Formats and Core. It lists paths, extracts files with or without the mods
  folder, dumps a TDB with names, dumps loc strings and diffs `boot.big` against stock.
- `tdb_dumpall.txt`: the full dump of BeAGM0's TDB.

The save reader and writer are now in Blueline (section 7).

## 7. What was built (2026-11-18)

**Code** (not committed):

- `Blueline.Formats/Tdb/FranchiseSave.cs`: reads a BeAGM folder by INDEX, unpacks the outer frame and the inner blocks,
  and finds the league TDB. `WriteTo` writes a copy with a new league TDB. It keeps blocks 0 to 2, the tail and every
  byte before the payload (the stray byte too), and fixes the u32 size and INDEX +0x28/+0x38. An unedited repack
  unpacks to the same outer content as the original. **Verified** by test; **not seen in game**.
- `Blueline.Core/Franchise/FranchiseSync.cs`: copies team names, AHL affiliates (translated from exhibition index to
  league row + 1) and arena name, capacity and year from a project's database or a roster save into a Franchise save's
  `leagueteams` and `leaguearena`.
- `Blueline.Core/Franchise/FranchiseImmersion.cs`: real-world data for a project.
  - 63 arenas, 2025-26: name, hockey capacity, year opened (Wikipedia's lists of NHL and AHL arenas, read 2026-11-18).
    Matched by the club's current name, so a 2018 database misses the clubs renamed since. Seattle is not an exhibition
    club, so it is skipped.
  - The ten analyst rows of `tweetsauthors` become real insiders (14 bytes at most per field): E. Friedman
    @FriedgeHNIC, P. LeBrun @PierreVLeBrun, D. Dreger @DarrenDreger, C. Johnston @reporterchris, B. McKenzie
    @TSNBobMcKenzie, K. Weekes @KevinWeekes, E. Kaplan @emilymkaplan, G. Wyshynski @wyshynski, M. Russo @RussoHockey,
    NHL PR @PR_NHL. The team and player author rows are left alone.
  - `TWITTER_ANALYTIC_AUTHOR_NAME` "Hockey Analyst" becomes "NHL Insider". **Seen in game** 2026-11-18: the activity
    feed shows this text as every analyst's name ("NHL INSIDER"), not the `tweetsauthors` names. Team items show the
    team's short name ("PREDS"). So the per-insider names would need the feed's Lua to read the author row.
  - 670 `TXT_TWEET_*`, `TXT_INT_*` and `TXT_OWNER_*` lines are cleaned: double and trailing spaces, "apart of", "has a
    received", "]a Contract", "back to soon", "its ready", "leagues best". The "#poorguy" send-down line is rewritten.
    A test checks that every placeholder survives.
- CLI: `franchise-info`, `franchise-repack`, `franchise-sync`, `franchise-immersion <project> <game> [--write]`.
  Every save command writes to another folder, and refuses the same folder or a running shadPS4.
- `tests/Blueline.Tests/FranchiseTests.cs`: 6 tests. The real BeAGM0 is only read; everything is written to temp.

**Applied:**

- **NHL 19 Overhaul project:** `franchise-immersion --write` changed 61 arena cells, 20 author cells and 670 text lines
  as one undo step. The backup is `project.before-franchise.json`. It is **not rebuilt or installed**, so the game has
  none of it yet.

**Scratch outputs only** (the session scratchpad; real saves untouched):

- `franchise/out-repack`: BeAGM0 repacked unchanged. This is test B0.
- `franchise/out-sync-ovh2`: BeAGM0 with the Overhaul's names, affiliates and arenas. 144 cells; all 70 checksums
  verify. Affiliates: CAR to Chicago, FLA to Charlotte, NJD to Utica, STL to Springfield, VAN to Abbotsford, VGK to
  Henderson.
- Roster sync dry run on a copy of RosterCustom0: 61 cells left to write (146 already there), 0 skipped. These are the
  arena updates. A new franchise copies them from the roster save, so the real roster needs this write (backup first,
  your OK).
- `uitest/boot.big`: Puck Peak's `boot.big` with one line changed. The activity feed's author title gets " [BL]"
  (`activityfeed.lua` line 256, CRLF kept). It builds and verifies. It is the gate test for UI edits.

**Sandbox results (2026-11-18)**, with networking off, a private user folder and the game linked read only:

- **Test B0 passed, seen in game.** The unedited repack of BeAGM0 loaded (DynastyTest, 09/29/2018, activity feed with
  sims). The game then wrote its own BeAGM15 backup from it, which decodes with 70 of 70 checksums.
- **UI edit through a whole boot.big passed, seen in game.** The marker `boot.big` (built from Puck Peak's) booted, and
  every feed title read "... [BL]". Lua edits to the Franchise UI work this way.
- **Overhaul installed** and **RosterCustom0 synced** (61 arena cells; backup in `CUSA11126-backups`).

- **Affiliates in a new franchise come from the roster save, seen in game.** The new-career screen showed St. Louis
  with slot 55 as its farm team. That was San Antonio in 2018, now named "Henderson Silver Knights" by the database,
  with San Antonio's logo. `roster-sync` had not copied the affiliates, because it copies only what the project changed
  from the game's database, and Puck Peak's database already has the 2025-26 affiliates. The new `roster-league-sync`
  (`FranchiseSync.PlanRoster`) copies the NHL/AHL names and affiliates straight across. It wrote 22 cells into the real
  RosterCustom0 (backup `CUSA11126-backups/RosterCustom0-20261118-184006`). **Seen in game:** a new St. Louis career
  then showed Springfield Thunderbirds.
- The same gap held for arenas: the new-career screen showed Enterprise Center at 19,150, the roster's 2018 value.
  `roster-league-sync` now copies each club's arena name, capacity and year too. 54 more cells were written (backup
  `RosterCustom0-20261118-184224`), including 2018 NHL names such as Pepsi Center, Staples Center and Barclays Center.
- **The new-career capacity was still 19,150 after that, seen in game.** Franchise reads seating from
  `nhl/data/common/gamemodes/kernel_entity_csv/arena.csv` (in `inxp_00`). It has one row per NHL club: TeamId is the
  exhibitionteams index (-1 is a default), then seat shares and `MaxCapacity`. `franchise-immersion` now sets
  `MaxCapacity` from the project's arena table (20 clubs changed) and puts it in the Overhaul, which is reinstalled.
  **Seen in game:** St. Louis then showed 18,096. Arena age is worked out from the year opened.
- AHL slots renamed since 2018 still show the old club's logo (slot 55 Henderson shows San Antonio's art, id 152). The
  logos are team art, not data.

- **An edited existing franchise crashes on load, seen in game.** `out-sync-ovh2` (144 cells: names, affiliates and
  arenas in `leagueteams`/`leaguearena`) crashed with an access violation (0xc0000005 in "Loading Job 0") on Continue
  Career, while the unedited repack loads. So the writer is fine and the edit is not. Likely the other blocks (team and
  line data in block 1, or the raw tail) hold copies or derived data that must agree with the league tables. The
  likeliest culprits are the affiliate rows and the renamed AHL clubs. Not investigated further: the user starts a new
  franchise instead, which gets all of this from the roster save and the Overhaul. The real BeAGM0 was never written.
  To retry later, change one field at a time (an arena name first), not the whole sync.

**Earlier list of what needed your OK, in order:**

1. Rebuild and install the Overhaul (writes `patch_*_99` archives into the game's databigs).
2. Write the roster sync into the real RosterCustom0 (a backup is made beside it).
3. Sandbox: load `out-repack` (test B0). If it loads, load `out-sync-ovh2`. Only then write the real BeAGM0.
4. Sandbox: a copy of the mods folder with `uitest/boot.big` in it. Open the activity feed and look for " [BL]".

**Left out, and why:**

- **Award history to 2026:** the franchise calendar starts in 2018, and `stockawardstable` holds a five-year window
  (years 114 to 118). Later winners would sit in the franchise's future. For reference, the 2026 winners: Hart
  Kucherov, Vezina Vasilevskiy, Norris Werenski, Calder Schaefer, Conn Smythe Jordan Staal.
- **`.brk` decoding beyond `capdefs`,** and **the player block (block 2):** research tasks, see Tier D.
- **AI GM names:** these live in each franchise's `leaguegmdata`, so they need the save writer proven in game first.

## 8. Second round (2026-11-18 evening)

- **Real names on the feed (seen in game, sandbox):** the "NHL 19 Franchise UI" project changes `activityfeed.lua`. Each analyst item gets one of
  ten insiders (name and handle), picked from a hash of its text, so the same item always shows the same person. The
  service gives no author id (only AuthorName, AuthorType, AuthorArtId, Date and MessageText), and every analyst has
  asset id 1, so the face stays the same. `NUMBER_OF_ITEMS_ON_SCREEN` only switches the scrollbar on; the list already
  scrolls.
- **Hashtags (seen in game, sandbox: "#GoAvsGo #Smashville"):** `tweetshashtags` is never referenced. Lines build hashtags from placeholders (`#[<%n ta>]` 190 times,
  `#[<%n tnn>]` 72, `#[<%n pl>]` 110). The same Lua replaces NHL abbreviations with real team hashtags (#CHI to
  #Blackhawks, #NSH to #Smashville and so on) and joins two-word names (#Golden Knights to #GoldenKnights), on both
  feed tabs.
- **Text:** 39 more news lines rewritten (caps, "!!!", repeated words). Each rewrite must keep exactly the original's
  placeholders or it is skipped. `tweetstexts` is full (968 of 968), so lines can only be rewritten, not added.
- **Cap:** Puck Peak's `capdefs.brk` already matches 2026-27 (cap $104.0M, floor $76.9M, +$9.5M to $113.5M, minimum
  $850k), so it is unchanged.
- **Logos:** each renamed AHL slot (141 Abbotsford, 132 Hamilton, 152 Henderson, 161 Calgary Wranglers) has 17 logo
  files under `nhl/ui/kettle_assets/teamlogos/*/t<artid>.gnf` and the creation zone. `team-logos` fills them from one
  PNG. No source art yet.
- **Shipping UI edits:** the game reads `boot.big` from Puck Peak's mods folder. A Franchise UI edit reaches the game
  only by replacing that file (with a backup), and a Puck Peak update would put its own back. Built and tested in the
  sandbox only.

## 9. The calendar year (2026-11-18, research)

The user asked whether Franchise could run on a 2026-27 calendar instead of 2018-19.

- **Where the year lives.** A new franchise's league database (save block 3) has two state rows:
  - `leaguestates` type 0 is the current date, a day count from 1899-12-30 with bit 31 set (43352 = 2018-09-09).
  - Type 23 (key 138) is the season year, 2018 with bit 31 set.

  Other day counts in that database: `leaguetasks.startdate/enddate`, `leaguegmdata.salarydatecalculated`,
  `leaguefreeagentdata.responsereviewdate` and `leaguetradeblock.key`. `leaguedraftpick.year` counts years from 1900
  (119 to 122). The schedule (`leaguescheduletable`) stores month and day only, with no year. No data file, `.brk` or
  service definition sets the base year; it is in the game code.
- **Puck Peak already shifts the roster back 8 years** so it fits the 2018 calendar. In `exhibitionplayerbiotable`,
  Celebrini (born 2006, drafted 2024) is stored as born 1998 and drafted 2016. McDavid (born 1997) is stored as 1989.
  `yearufa` and `yearwaivereligible` follow the same rule. That is why ages look right in game.
- **So moving the real calendar** would mean shifting every year by +8 together: the two state rows and every day
  count in the league database, and the players' birth, draft, contract, UFA and waiver years. The players live in
  save block 2, which is not a TDB, so its format is unknown. Editing league tables of an existing save has already
  crashed the game on load once (section 7).
- **Display-only route (being tested in the sandbox).** The game prints dates through one formatter at vaddr
  0x4467ab0 (file offset = vaddr + 0x4000). It takes a packed date (year in bits 0 to 11, month at bit 12, day at
  bit 16) and prints `%d/%d/%d`, `%02d/%02d/%d`, `%02d.%02d.%d` or `%d-%02d-%02d` depending on the locale. Each path
  masks the year with `and r8d/r12d, 0xfff`. The patch replaces each of those masks with a call to a 12-byte stub in
  the alignment padding (0x4467e13 and 0x44684f1) that masks the year and then adds 8. Game logic never sees the
  shifted year.
  - Script: `scratchpad\calpatch\patch_eboot.py <in> <out> [shift]`. Only the sandbox copy of `eboot.bin` was replaced
    (its hardlink was removed first). The real eboot is unchanged: sha1 3f318040ac120a417627c008dce60a4df375d555.
  - Not covered yet: years the game passes as plain numbers. `TXT_SEASON_WITH_YEAR_RANGE` (the "2018-2019" labels,
    built with `%d` at 0x1d01bc3), `CurrentLeagueYear` (12 code sites), `SeasonYear`, `DraftPickYear` and
    `MonthAndYearString`.
  - **Result: did not work.** With the patched eboot the feed still showed 2018 dates, so the feed's date text comes
    from somewhere else. The sandbox eboot was put back to a hardlink of the stock one. Parked at the user's request.

## 10. UI look: Ice theme (2026-11-18, sandbox)

- **The theme is two global files in `boot.big`:** `nhl/ui/default.constants` (named colours, animation speeds,
  screen animations) and `nhl/ui/definition.styles` (font styles). Layouts use `$NAME` colours almost everywhere:
  about 1,600 named references against about 120 literal hex values in the UI. So one palette change restyles every
  screen, in every mode. A loud test (magenta) showed up on the hub straight away.
- **Not reachable by the palette:** the striped panel backgrounds and the red streaks behind the hub are textures. The
  orange and red lines under the matchup and over the calendar days are the two teams' colours.
- **Ice palette, picked by the user:** accent `ORANGE_PRIMARY` 0x5BC8F5, `ORANGE` 0x2E9BD6, `DARK_ORANGE` 0x1F6E9C, and
  navy greys (`BLACK` 0x101C2E, `BLACK_BG_PRIMARY_` 0x0E1A2B, `DARK_GRAY_BLUE` 0x1A2B42, `GRAY` 0x8FA3B8,
  `LIGHT_GRAY` 0xBCCAD8, `WHITE` 0xEEF3F8). Seen in game: the HUB tab, the focus bar, the calendar day, the standings
  highlight and the popup buttons all show ice blue.
- **Faster animations:** the screen and popup speed constants were cut by about a third, and all 40 literal durations
  in the `animations` section were scaled to 60%.
- **Roomier feed rows:** the row height in `activityfeed.layout` went from 105 to 130. In `newsitem.layout` the margins
  went from 15 to 22, the message from y 40 to 46, the insider name now uses the accent colour and the date is grey.
  Seen in game: insider and team names in ice blue, grey dates, taller rows, nothing clipped.
- **Files:** in the "NHL 19 Franchise UI" project, built into the sandbox `boot.big` only. Script:
  `scratchpad\ice\make_ice.py`; the project before this change is in `scratchpad\ice\project.before-ice.json`.
- **Installed 2026-11-18 21:21 at the user's request.** Puck Peak's `CUSA11126-mods/databigs/boot.big` was replaced by
  the sandbox-tested build (sha1 7d07bbb2…). This also brings the feed Lua from section 8 (real insiders and team
  hashtags) to the real game. Backup: `NHL 19/backups/ice-ui-20261118-212106/` holds `boot.big` (Puck Peak's,
  sha1 03787e98…) and the unchanged stock `eboot.bin`. To undo, copy that `boot.big` back. A Puck Peak update will put
  its own `boot.big` back; rebuild with `boot-build` from the "NHL 19 Franchise UI" project afterwards.
