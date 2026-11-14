# Visual survey tools

Small scripts that reproduce the numbers in [docs/VISUALS.md](../../docs/VISUALS.md). They only read your game files.
Run Python with `-I`. The paths below assume the `blueline` command line from a release or `dotnet run --project src/Blueline.Cli`.

| Tool | What it does |
|---|---|
| `eboot_strings.py` | ELF program headers of `eboot.bin` and every readable string with its offset (section 2) |
| `TextureScan` | mounts the stock game and writes `stock_paths.tsv` (every stock path, archive and size) and `gnf_info.tsv` (size, format and mips of every texture): `dotnet run --project tools/visuals-survey/TextureScan -- "<game folder>" <output folder>` |
| `rsf_survey.py` | passes, render states, constant buffers, samplers and GPU binary counts of the `.rsf` shader files (section 6) |
| `vault_hash.py` | the attribute vault name hash (section 9.1): `python -I vault_hash.py Attrib::Types::Vector3` prints `0xd680287d` |

The other results came from reading files directly: `blueline extract <game> <path> <file>` for the frame scripts
(`nhl/data/kettle/scrape/*.scrape`), `nhl/data/common/postfx/*.xml`, `stadiumlighting.json` and the vaults in
`nhl/data/common/database/attrib/`.
