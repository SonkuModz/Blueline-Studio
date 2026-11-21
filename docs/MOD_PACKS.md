# Blueline mod packs (`.blpack`)

Status: **agreed 2026-11-20.** The user picked layout A (load-order list), no score clock in packs, a roster slot
that never touches Puck Peak's `RosterCustom0`, account fields blanked, and a full Export page with pack art.

A mod pack is one file that carries a Blueline project to someone else's game. It holds only the files the mod changes
or adds, never whole game archives. It can also hold the custom roster save. The mod manager in Studio (and the CLI)
reads a pack, checks it against the player's own game, and installs it the same way Blueline installs a project.

## 1. Goals and limits

- **Small and shareable.** Only changed or new files go in, by content hash, so a pack that recolours one team is a few
  MB, not GB.
- **Built on the player's machine.** A pack does not ship `.big` archives. At install, the manager builds the
  `patch_*_99_*` archives from the pack against the player's own install. That keeps every rule the builder already
  follows:
  - Files go to the right family, inxp or game.
  - Wrapped files are wrapped again.
  - Each archive holds one database.
  - Every archive gets its flag table.
  - Every archive is read back before it counts as built.
- **Never touches stock files.** As with projects, install only adds new archives and uninstall only removes them.
  The roster save is the one file that gets replaced, and it is backed up first, after the player says yes.
- **Out of scope for v1:**
  - `boot.big` content, such as the score clock. It cannot load from a patch archive, so export leaves it out and
    says so (user's call, 2026-11-20).
  - Merging two packs inside one file. When two packs change the same database, one whole file wins, the same rule as
    "Add another mod".
  - Saves other than the custom roster. The format leaves room for them.

## 2. The file

A `.blpack` is a zip (deflate) with this layout:

```
manifest.json            required
art/cover.png            optional, 16:9, 1280x720: the banner on the Mods page and the Export page
art/icon.png             optional, square, 256x256: the pack's tile in the load-order list
art/shot1.png ... shot6  optional, 16:9, up to six screenshots for the gallery
files/<sha256>           one entry per distinct content, named by the SHA-256 of its unwrapped bytes
saves/roster/...         optional, the roster save folder as the emulator stores it, account id blanked
```

Art is re-encoded as PNG at export at exactly these sizes (cropped to fit, centred), so a pack never carries a huge
picture. Without a cover the manager draws one from the pack's name and the Ice palette.

Zip entry names are never used as game paths. The manifest maps game paths to content hashes. On reading, the
manager:

- refuses any entry outside these names, and any name with `..`, a drive letter or a leading slash;
- checks that every `files/` blob hashes to its name before use;
- refuses a pack that unpacks to more than 4 GB, or any entry over 1 MB that unpacks to more than 1,000 times its packed size,
  which guards against zip bombs.

### 2.1 `manifest.json`

Field names are camelCase, as written by the serializer.

```json
{
  "format": 1,
  "id": "3f9c2b1e-8d0a-4c51-9a77-1b2c3d4e5f60",
  "slug": "nhl-27-overhaul",
  "name": "NHL 27 Overhaul",
  "author": "SonkuModz",
  "version": "1.2.0",
  "description": "2026-27 jerseys, full-ice rinks and colours for every NHL and AHL club.",
  "art": { "cover": "art/cover.png", "icon": "art/icon.png", "shots": [ "art/shot1.png", "art/shot2.png" ] },
  "created": "2026-11-20T18:04:00-05:00",
  "blueline": "0.12.0",
  "game": { "titleId": "CUSA11126", "appVersion": "01.60" },
  "files": [
    {
      "path": "nhl/data/kettle/loc/eng_us.db",
      "blob": "9b1f...e2",
      "size": 4812800,
      "stock": "51aa...07",
      "description": "Kit names"
    },
    {
      "path": "nhl/blueline/new_logo.dds",
      "blob": "c0de...11",
      "size": 87552,
      "stock": null,
      "description": "New file"
    }
  ],
  "saves": [
    {
      "kind": "roster",
      "from": "RosterCustom1",
      "folder": "saves/roster",
      "title": "Roster 2026-11-20 Overhaul",
      "files": { "sce_sys/param.sfo": "aa01...", "DATA": "77fe...", "INDEX": "0b3c..." }
    }
  ],
  "leftOut": [ "nhl/ui/hud/clock.swf (boot.big content cannot load from a patch archive)" ]
}
```

| Field | Meaning |
|---|---|
| `format` | Pack format, `1`. A newer number is refused with "update Blueline". |
| `id` | A GUID made when the project first exports. It stays the same across versions, so v1.3 replaces v1.2 instead of being installed beside it. It is kept in the project's `project.json`. |
| `slug` | Lowercase letters, digits and hyphens, at most 24 characters. It names the archives: `patch_inxp_99_pk-<slug>.big`. |
| `name`, `author`, `version`, `description` | Shown to the player. `version` is free text; semver is suggested. |
| `art` | Optional pictures inside the pack: `cover`, `icon`, `shots`. |
| `blueline` | The Studio version that wrote the pack. |
| `game.titleId`, `game.appVersion` | Taken from the exporter's `sce_sys/param.sfo` (`TITLE_ID`, `APP_VER`). |
| `files[].path` | Game path, case-sensitive, as the VFS names it. |
| `files[].blob` | SHA-256 of the unwrapped content. Chunkzip wrapping is redone at install from the player's own game. |
| `files[].stock` | SHA-256 of the unwrapped stock file this one replaces, read from the install alone, without `CUSA11126-mods`. `null` means a new path. |
| `saves[]` | Optional saves. For v1, only `kind: "roster"`. `from` is the slot it was exported from, for information only. Each file is listed with its SHA-256. |
| `leftOut` | What export could not include, so the player knows the pack does not carry it. |

### 2.2 What export leaves out

- **Changes identical to stock.** These are dropped, so a project that was edited and reverted by hand does not ship
  extra files.
- **Paths that only `boot.big` holds.** These are listed in `leftOut`.
- **Loose files.** The builder refuses these today, so export does too.

## 3. Export

**Export mod pack** on the Build tab opens a full **Export** page (Ship group, opened from Build, not its own rail
item). It has:

- **Details:** name, author, version, description. Name and author start from the project.
- **Art:** drop or pick a cover, an icon and up to six screenshots. Each slot shows the crop it will use. **Pick from
  project** offers the pictures the project changes (team logos, jerseys, menu backgrounds), decoded to PNG, so a pack
  can use its own art without leaving Studio.
- **Preview:** how the pack will look in the player's Mods page, both its list tile and its banner, live as you
  edit.
- **Contents:** file count by group, packed size, and anything left out (boot.big content, files identical to stock).
- **Roster:** include a custom roster save. The slots are listed by their in-game names, read from each save's
  `param.sfo`. The default is the first slot that is not Puck Peak's `RosterCustom0`. The account id is always blanked.

The CLI does the same:

```
blueline pack-export <project> <game> <out.blpack> [--roster <saveFolder>] [--cover <img>] [--icon <img>] [--shot <img>]... [--version 1.2.0] [--description "..."]
```

Steps:

1. Mount the game with `useModsFolder: false`, read `param.sfo`, and hash each changed path's stock content.
2. Write the blobs, the art and the manifest to a temporary zip. Then reopen it and check every blob and hash against
   the project before it is renamed to the final name.
3. The pack's `id` is made on first export and saved in the project folder as `pack.json`, along with the last
   version, description and art. The next export starts from those values.
4. A roster save is read with `RosterSave.Read` to prove it is valid, including the u32 size tail and CRCValue. It is
   then copied in with `ACCOUNT_ID` in `sce_sys/param.sfo` set to zero.

## 4. The mod manager

This is a new **Mods** page in the Ship group, beside Build and Play. The CLI gets the same features as `pack-*`
verbs.

### 4.1 Opening a pack

Open or drag in a `.blpack`. The page then shows:

- the preview picture, name, author, version, description and file count;
- what the pack changes, grouped the way Build groups a project (teams, textures, text, database...), with sizes;
- whether it carries a roster save, and its title;
- its screenshots, as a strip under the banner.

Nothing is written until **Install**.

### 4.2 Checking the player's game

Each check is a line with a green, amber or red mark:

| Check | Result |
|---|---|
| Title id matches `CUSA11126` | Red if not. Install is blocked. |
| App version matches `01.60` | Amber if not. The player can still install, with a warning that files may not line up. |
| Stock hashes | For each file with a `stock` hash, the player's install-only content is hashed. If it differs: amber, "your game's copy of X is not the one this pack was made against". Common causes are a different dump or an edited stock archive. |
| Mods folder | When `CUSA11126-mods` also changes a file the pack changes: amber, "the pack's copy wins over your mods folder for N files". Database files are named, because the mods folder's edits in them are lost. |
| Archive limit | Every pack, project and Puck Peak archive is counted against the 50-per-family limit. Red if it would be exceeded. |
| Free space | As for projects: the build size plus 1 GB. |

### 4.3 Conflicts, load order and priority

The manager keeps a list of installed packs in priority order: top wins. When two enabled packs change the same path,
only the winner's archive holds it. The loser's archive is rebuilt without it. So no path is ever in two `_99`
archives at once. That is the only arrangement proven safe; whether the game's sort order picks the right winner for
duplicates has not been tested.

- **Pack against pack:** the conflict panel lists each shared file and which pack wins. Dragging a pack up or down, or
  pressing ▲ ▼, changes priority. The manager then rebuilds only the archives whose contents change.
- **Pack against a Blueline project or an unknown `_99` archive:** these are archives the manager did not make, so it
  cannot rebuild them. For each overlap the player picks one of two options:
  - **Keep theirs:** the pack leaves those files out.
  - **Pack wins:** the other archive is removed with the existing `ModInstaller.RemoveOther`. Its project can be
    installed again later.
- **Databases:** when two packs both change one `.db`, the panel says plainly that the loser's edits in that database
  are lost.

### 4.4 Install

1. Unpack the pack into the manager's store and check every hash.
2. Work out which files this pack wins.
3. Build them with the existing `OverlayBuilder`, from a temporary project made from the store, into the pack's own
   names:
   - `patch_inxp_99_pk-<slug>.big` and `patch_game_99_pk-<slug>.big`;
   - `_dbN` suffixes, one database per archive;
   - `_cube`, so colour cubes are never in the same archive as XML. This is a new rule in the builder, applied to
     projects too.
4. Install each archive with a temporary-then-rename write, recording its SHA-256.
5. **Roster save, only after the player says yes, and optional.** The pack's roster never goes into
   `RosterCustom0`, which belongs to Puck Peak and Overtime Roster, and roster sync writes there.
   - The default slot is the first free `RosterCustomN` with N of 1 or more. The player can instead pick an existing
     slot of 1 or more, which is then backed up first. A pack that is updated keeps the slot it had.
   - Refuse while shadPS4 is running.
   - An existing slot is copied to `savedata/CUSA11126-backups/<slot>-<time>-before-<slug>`, the same backup folder
     roster sync uses.
   - The save is copied into a temporary folder. The slot name is rewritten in its three places: the `NAME` file, the
     first 32 bytes of `INDEX`, and `SAVEDATA_DIRECTORY` in `param.sfo`. It is read back with `RosterSave.Read`, then
     renamed into place.
   - The slot, the backup path (if any) and the hash of each installed file are recorded.
   - In game, the player picks the roster from the custom roster list. Whether the game lists a slot it did not make
     itself, such as `RosterCustom2`, is proven in the sandbox before this is called done.

### 4.5 Enable, disable, uninstall, update

- **Disable** removes the pack's archives but keeps it in the store. Any file it was winning goes to the next pack
  down, whose archives are rebuilt. **Enable** reverses this.
- **Uninstall** removes the archives (only if their hash still matches, as `ModInstaller` does today) and deletes the
  store copy.
  - If the pack installed a roster save, the manager removes it from its slot, or restores the backup when the slot
    had a save before.
  - This happens without asking only if the save is unchanged since install.
  - If the player saved over that roster in game, they are asked, because restoring would lose those changes.
  - The backup itself is never deleted.
- **Update:** opening a pack with the same `id` and a different version offers **Update**, which is uninstall then
  install, keeping its place in the order.

### 4.6 Where the manager keeps its state

The state sits beside the game, not in `%APPDATA%`. That way the CLI, Studio and tests all see the same state, and a
sandbox copy carries its own:

```
NHL 19/CUSA11126-packs/
  packs.json               order, enabled, installed archives + hashes, roster backup, installed save hashes
  store/<id>/manifest.json
  store/<id>/files/<sha256>
  store/<id>/saves/...
```

The emulator does not read this folder; it only lays `CUSA11126-mods` over the game. **Back to stock** means two
things: `databigs` holds no `patch_*_99_pk-*` archive, and `CUSA11126-packs` is removed once the last pack is
uninstalled. The save folder path is always passed in explicitly, never worked out inside Core, because processes run
from the Claude app see a redirected Roaming folder.

## 5. CLI

```
blueline pack-export  <project> <game> <out.blpack> [--roster <saveFolder>] [--preview <png>] [--version v] [--description d]
blueline pack-info    <pack.blpack>                       manifest, files, save
blueline pack-check   <pack.blpack> <game>                game id/version, stock hashes, mods folder, conflicts
blueline pack-install <pack.blpack> <game> [--savedata <folder> [--slot RosterCustomN]] [--remove <archive>]...
blueline pack-list    <game>                              installed packs in priority order
blueline pack-order   <game> <slug> <position>
blueline pack-enable  <game> <slug> | pack-disable <game> <slug>
blueline pack-uninstall <game> <slug> [--force-roster | --keep-roster]
blueline pack-slots    <savedata>                          roster slots and their in-game names
```

## 6. Testing plan

- **Unit tests** (temporary folders only, never `%APPDATA%`):
  - export, then read back;
  - manifest validation: bad zip names, a wrong blob hash, a newer format;
  - stock-hash mismatch detection;
  - pack against pack: the winner holds the path and the loser's archive leaves it out;
  - reorder rebuilds;
  - disable and enable;
  - uninstall leaves `databigs` and the packs folder as before;
  - roster backup and restore, including the "save changed since install" case.
- **Round trip on a sandbox:** a hardlinked copy of the install and emulator in the scratchpad, with its own `user`
  folder and networking off. Export a pack from a project, install it into the sandbox, and boot. The log must reach
  `rostersaveloadutility.lreq`, and the player checks it in game. Then uninstall, and compare the sandbox `databigs`
  listing and hashes with stock. The real game, the real saves and `CUSA11126-mods` are not touched.

## 7. Built and proven (2026-11-20)

- **Code:**
  - `Core/Packs/`: `ModPack` (format, reader, writer), `PackExporter`, `PackManager`, `RosterSlots`, `PackSummary`.
  - The builder has a new `only` filter and puts `.cube` files in their own archive.
  - `ParamSfo.SetText` and `ParamSfo.Clear`.
  - Studio: a **Mods** page (Ship group) and an **Export mod pack** page, opened from Build › Share.
  - CLI: the `pack-*` verbs.
- **Tests:** `ModPackTests`, 9 tests, on a fake game made with the real writer. Roster tests use a copy of the player's
  `RosterCustom1`. The full suite passes.
- **Sandbox round trip:**
  - A copy of the Menu Backgrounds project, plus a `PressStart` edit ("PACK TEST"), was exported as a 12 MB pack: 5
    files, roster from `RosterCustom1`, account id blank.
  - It was installed into a hardlinked clean install (no mods folder, no project archives) as two archives, one being
    the database's. Its roster went into `RosterCustom2`. The game booted past `rostersaveloadutility.lreq` to the home
    menu.
  - Uninstall left `databigs` identical to stock: the same names, every file still the stock file's hardlink. It also
    removed `CUSA11126-packs` and moved `RosterCustom2` to the backups.
  - The real saves and settings kept the same hashes.
  - **Seen in game:** the user checked the installed pack in the sandbox (2026-11-20). It was then uninstalled a
    second time and the sandbox was back to stock again.
- **Sandbox note:** the game-specific config's `shad_net_enabled` turns networking on separately from
  `connected_to_network`. Both must be false in a sandbox.

## 8. Decisions (2026-11-20)

- **Score clock:** not in packs for now.
- **Roster slot:** never `RosterCustom0` (Puck Peak's); the first free custom slot, or one the player picks.
- **Account fields:** blanked.
- **Layout:** A, the load-order list, plus a full Export page with pack art.
