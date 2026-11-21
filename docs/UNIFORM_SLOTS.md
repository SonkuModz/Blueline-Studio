# Uniform slots: how NHL 19 defines a team's kits

Researched 2026-11-20 on CUSA11126 1.60 with Puck Peak's mods folder overlaid. Points are marked **Read** (from files),
**Seen** (tested in game) or **Assumed**.

## Short answer

- A team's kits are rows of `stockteamjerseys` in `nhl_game.db` (and the copy in a loaded roster save). One row is one
  kit slot. **Read.**
- The textures a row wears are found **by name**: `<brand>_<slug>_<word>`, where the brand comes from the row's
  `style`, the slug from the row's **`teamartid`** (not the team), and the word from `variant`. **Read** (see the
  Whalers rows below).
- Every kit name must also exist as a **texture pack in the attribute vault** (`gccollections`), for example
  `jersey_adidas_toronto_five_texpack`, `pant_..._texpack`, `socks_..._texpack`. The vault's value records are not
  decoded, so Blueline cannot add new texture packs yet. **Read.**
- So a new slot is made by adding a row that points at a kit name the vault already has and no row uses yet, and
  writing the new art into that kit's files. About 360 such names exist (list below). **Read**; in-game result in
  "Probes".

## The jersey table

`stockteamjerseys`: 1,574 rows, **2,037 allocated** (463 spare). Stride 20 bytes. The roster save carries its own
copy, which wins over the database in game (same as the gear table, `roster-save-sync`).

| Field | Bits | Meaning |
|---|---|---|
| `teamid` | 10 | `exhibitionteams` index of the team that lists the kit |
| `teamartid` | 15 | art id the kit's files are named after (slug) and the menu picture's number |
| `style` | 2 | **0 = nineties, 1 = rbk, 2 = adidas** (from the sheets: Buffalo has 2 nineties, 5 rbk and 3 adidas sheets, and 2, 5 and 3 rows) |
| `variant` | 5 | the kit word: 0 home, 1 away, 2 two, 3 three ... 12 twelve |
| `fevariant` | 5 | front-end variant; equals `variant` in every stock row |
| `variantleadingscorer` | 5 | equals `variant` in stock rows (the top-scorer kits use it) |
| `jerseyindex` | 5 | 0-based number of the kit within its team, unique per team. **5 bits: a team holds at most 32 kits** |
| `orderid` | 5 | position in the jersey menu (0 first) |
| `isdefault` | 2 | 1 on the current Home and Away (adidas variant 0 and 1) |
| `locstring` | 4 | kit type label: 0 Home, 1 Away, 2 Third, 3-5 retro home/away/third, 6 Alternate, 8-9 top scorer (`jersey_type` service lists types 0-7) |
| `islightcolor` | 1 | light kit (Away is light) |
| `iscasual` | 1 | 1 on adidas rows |
| `hastie`, `font`, `jerseyback`, `sockstyle`, `pantsstyle`, `helmetlogo`, `*logotype`, `yearstart/end`, `active`, `islockable` | | copied from a template row |

Example, Toronto (team 27, art 27), 21 rows: adidas variants 0-5 (`jerseyindex` 15-20, order 0-5), rbk 0, 1, 3, 4,
6-10, nineties 0-5. The jersey menu lists adidas Home and Away first.

**The kit word is not purely the number.** Variants 10 and 11 on European clubs are their top-scorer kits
(`hometopscorer`, `awaytopscorer`, `locstring` 8 and 9), while Toronto's rbk variant 10 is `rbk_toronto_ten`.
Blueline only offers words 0-12 (`home` ... `twelve`).

**`teamartid` picks the files (Read).** Hartford Whalers Alumni (team 309, art 349) has two rows with `teamartid` 5,
style 0, variants 3 and 4. Those are `nineties_carolina_three` and `nineties_carolina_four`, the Whalers sheets. The
Kraken (team 399) has rows with `teamartid` 900 wearing `rbk_pre_order_*`. The menu pictures follow the same number:
`teamjerseys/jersey_2_900_0..12.gnf` exist for art 900.

## Files per kit

For kit `<brand>_<slug>_<word>` in `nhl/data/kettle/rendering/characters/player/`:

| File | Notes |
|---|---|
| `jersey_<kit>_cm`, `_nm`, `_coeff` | jersey sheet (docs/RESEARCH.md 6.6, 6.7) |
| `pant_<kit>_cm`, `_nm`, `_coeff` | not every kit has its own |
| `sock_<kit>_cm` | |
| `font_<kit>_cm`, `_nm`, `_sm` | numbers |
| `name_<kit>` | name bar |
| `helmetlogo_<kit>_0` | helmet decal |

UI: `nhl/ui/kettle_assets/teamjerseys/jersey_<style>_<teamartid>_<variant>.gnf` and the same in `teamjerseysxl`.

Vault (`nhl/data/common/database/attrib/gccollections.bin`, string pool, **Read**): one collection per kit and part,
`jersey_<kit>_texpack`, `pant_<kit>_texpack`, `socks_<kit>_texpack`, `merged_sock_<kit>_texpack` (rbk and nineties),
plus each texture's path (`rendering\characters\player\jersey_<kit>_cm` and so on). 2,523 `_texpack` names in all.
Puck Peak does not change `gccollections`. The NHL 27 kit work only replaced files under existing names, which is why it
never hit this limit.

## Gear rows per kit

`stockteamequipmenttable` (docs/RESEARCH.md 6.10), corrected here:

- **Gear `variant` = adidas jersey `variant` + 1, and 0 is the fallback.** This fits every NHL club: Toronto's adidas
  0-5 have glove rows 3-6 and helmet rows 1, 3, 5, 6; St. Louis adidas 0-2 has helmets 1-3. It also explains the
  in-game results: St. Louis gear 3 showed on its `two` kit, Buffalo `two` wore gear 3, and Toronto's "unused" row 995
  (variant 6) was really its `five` kit's row. **Read**, matches what was seen.
- Gear `era` 3 goes with style 2 (adidas). `GearColours` assumed style 0 was adidas; Blueline fixes that.
- A new kit gets rows with the next gear variant (its jersey variant + 1) for gloves (5) and the helmet (8), copied
  from the team's Home rows.

## Free kit names (donor kits)

A name is free when the vault has it and no row's `teamartid`/`style`/`variant` reaches it. Counted 2026-11-20:

- **`adidas_pre_order` (art 900):** words `home` to `twelve`, 13 kits. Only the Hockey Ultimate Team side (team 394)
  wears 0-11, and Puck Peak hides HUT. `twelve` is used by nobody. This is the best source for new adidas kits.
- **Orphan kits of real clubs:** `rbk_toronto_five`, `nineties_arizona_three/four`, many CHL `rbk_*_three`, Swiss and
  Finnish `nineties_*_two/three`, `rbk_sandiego_two/three`, `rbk_texas_stars_two/three` and others. They have sheets and
  packs but no row.
- **Event sides:** `rbk_east/west/legends/threes_circuit_*`, `rbk_wc_*`, `nineties_spengler_*`, `nineties_champions_*`.
  Some may be used by sides whose art id has no known slug (the All-Star divisions, art 345 and 348), so Blueline offers
  only names it can prove are unused.

Each free name can be used by one new slot. Using it means writing the new art into that name's files.

## Limits

- 32 kits per team (`jerseyindex`, `orderid` and `variant` are 5 bits).
- 463 spare jersey rows and 5,500 spare gear rows in `nhl_game.db`. The roster save's copy has its own spare rows.
- New kit names need new vault texture packs, which are not possible yet (see Probes C).
- One `.db` per archive (RESEARCH 6.10).

## Probes (2026-11-20, sandbox, Toronto, no roster save)

Archives `patch_*_99_slotprobe-abc`: three new Toronto rows after Home and Away in the menu.

| Probe | Row | Files | Result |
|---|---|---|---|
| A | rbk variant 5, art 27 (`rbk_toronto_five`, pack and sheets exist, no row) | none | **Seen:** listed in the jersey select and worn in full (the rbk Leafs kit with striped socks) |
| B | adidas variant 12, art 900 (`adidas_pre_order_twelve`) | magenta jersey cm, magenta menu picture | **Seen:** listed and worn. The magenta jersey shows, with the donor's own pants (black) and socks (gold), since only the jersey sheet was written. So a borrowed slot must write every part: jersey, pant, sock, font, name, helmet logo |
| C | adidas variant 6, art 27 (`adidas_toronto_six`, **no pack**) | new lime jersey cm, copies of the rest, lime menu picture | **Seen:** listed, but **the jersey is not drawn at all** (the body shows through). The new files under a name the vault does not know are ignored. Confirms the vault gate |

All three rows appeared in the menu, so a new row with a fresh `jerseyindex` and `orderid` is enough for the menu to
list it (**Seen**). Gloves and helmet on B and C used the team's fallback gear rows (no rows were added for them).

**Conclusion:** "add slot" = new row + a free vault name (own orphan first, else a donor such as `adidas_pre_order_*`)
+ every part's files written under that name + gear rows. New kit names need vault writing, which is future work.

## Studio tools (2026-11-20)

Code: `src/Blueline.Core/Teams/UniformSlots.cs` (table, free names, add/move/set/remove) and `UniformSlotArt.cs`
(the files a slot writes). Tests: `tests/Blueline.Tests/UniformSlotTests.cs`.

- **Teams tab › More › Uniform slots.** The team's slots in menu order with their menu pictures. Each slot can be
  relabelled, moved earlier or later, made the default, marked light, given a new jersey sheet, or removed. "Add a
  slot" picks a free kit name (own unused kits first), a slot of the same brand to copy the other parts from, an
  optional jersey sheet (normal and coeff maps are made from it) and menu picture, a label and a menu position. Every
  change goes into the project at once as one undo step. Reordering is by buttons, not drag and drop.
- **CLI:** `blueline slots`, `slot-set`, `slot-add`, `slot-remove` (see `blueline` usage).
- **What "add" writes:** the jersey row (`jerseyindex` = highest + 1, `orderid` at the chosen position, other fields
  copied from the team's same-brand Home), for adidas kits of NHL clubs a gloves row and a helmet row (gear variant =
  variant + 1, copied from what the new kit read before: the team's fallback rows), and every file the name has: jersey cm/nm/coeff, pant cm/nm/coeff, sock
  cm, font cm/nm/sm, name, helmet logo, both menu pictures. Files the name does not have are skipped.
- **Remove** deletes the row, closes the gap in `jerseyindex` and `orderid`, and leaves the files and gear rows alone.
  The name becomes free again.
- **Roster save:** RosterSync matches jersey rows by `teamid`, `teamartid`, `style`, `variant`, so a new slot is
  added to the roster save as a new row by "Copy edits to roster save". Untested in game.

**Studio-built slot, sandbox 2026-11-20:** `slot-add` on Toronto, `adidas_pre_order_twelve`, a green recolour of
the Leafs home sheet, `copy=1` (Home), green menu picture, menu position 4. Builds to 3 archives (art, inxp, db).
**Seen in game:** listed in Toronto's jersey select and worn, the green jersey with Leafs pants, socks and numbers
copied from Home (no donor parts left). Gloves navy and helmet white, as the new gear rows copied the fallback rows.

**Second pass, seen in game:** orange gloves (new row 5188) and magenta helmet (new row 5189) on gear variant 13, and
green recoloured pant and sock sheets written under `adidas_pre_order_twelve`, all four worn. So an added slot takes
its own gear rows and its own pants and socks.

## Kit names (2026-11-20)

The jersey select shows a kit's own name in place of its type label when the language table has the key
`VINTAGE_<style>_<art>_<variant>`. The format string `VINTAGE_%d_%d_%d` sits in `eboot.bin` beside the type keys
(`ALTERNATEJERSEY`, `OLD_HOME`, `OLD_AWAY`, `OLD_2NDHOME`). `eng_us.db` has 276 of these keys, spelled in mixed case
(`vintage_2_27_5`, `VINTAGE_1_27_9`). The key hash ignores case, so the game finds them either way. Examples: Toronto
`2_27_5` "The Digital 6 Created by Adidas", `1_27_9` "2016-17 Centennial", `2_27_3` "2017-18 Stadium".

**Seen in game:** the key `VINTAGE_2_900_12` = "Green Test Kit" was added to `eng_us.db` (in its own archive). The
added Toronto slot showed "GREEN TEST KIT" in the jersey select. So a new slot can be named by adding the key.
Only the English table is written; other languages show the type label.

Tools: `SlotNames` (`src/Blueline.Core/Teams/SlotNames.cs`), a Name box on the selected slot and in "Add a slot",
`name=` on `slot-add` and `slot-set`, and `blueline slots` lists the names. The `text` CLI verb now adds a key the
table does not have.
