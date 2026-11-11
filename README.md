# Blueline Studio

A modding suite for NHL 19 on PS4 (CUSA11126). It edits textures, text, databases and audio, previews models, and
installs changes as new overlay archives, so the game's own files are never modified.

**Tested on one dump under the shadPS4 emulator only. Retail PS4 is untested.** See [docs/STATUS.md](docs/STATUS.md)
for exactly what has been proven and what is limited.

## Run it

Needs the .NET 10 SDK on Windows.

```bash
dotnet build Blueline.slnx
```

```bash
src/Blueline.App/bin/Debug/net10.0-windows/BluelineStudio.exe
```

1. On **Home**, choose the game folder (the one holding `databigs`), and optionally your own `sx.exe` for audio.
2. Create a project. All edits go into its folder.
3. Edit in any tab. Changes appear in **Build**, where each can be reverted.
4. On **Build**, press **Build**, then **Install**. **Uninstall** removes the mod again.

## Test it

The tests read the real game, so they need the dump at `NHL 19/CUSA11126`.

```bash
dotnet test tests/Blueline.Tests
```

## Docs

- [docs/STATUS.md](docs/STATUS.md): features, proof, limits, decisions, remaining work
- [docs/RESEARCH.md](docs/RESEARCH.md): file format findings and old claims retested
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): how the code is organized
- [docs/screenshots](docs/screenshots): every tab with real data
