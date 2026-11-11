# Contributing

Thanks for helping. Blueline Studio is a young project, so careful reports are as valuable as code.

## Reporting a problem

Open an issue and include:

- What you did, what you expected, what happened
- The game version and whether you used shadPS4 or a retail PS4
- The relevant lines from the Log panel or `%LOCALAPPDATA%\Blueline\logs`
- A screenshot if it is visual

Please do not attach game files. Name the file path instead, for example `nhl/ui/...`.

## Sending a change

1. Build and test: `dotnet build Blueline.slnx` and `dotnet test tests/Blueline.Tests` (the tests need your own dump at
   `NHL 19/CUSA11126`).
2. Keep changes small and modular. No placeholder buttons: a control either works or is not shown.
3. Do not claim support for something you have not tested. If it is unproven, say so in the UI and in
   [docs/STATUS.md](docs/STATUS.md).
4. Never write to the game's original archives. Mods are new `patch_*_99_*` archives.
5. Update the docs when behaviour changes.

## Ground rules

- No game content, `sx.exe`, FFmpeg, or emulator binaries in the repository.
- Validate before writing, and log errors rather than hiding them.
