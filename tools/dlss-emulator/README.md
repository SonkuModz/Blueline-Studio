# DLSS emulator build program

Blueline's Play tab has a **DLSS (DLAA)** switch for a DLSS build of the shadPS4 emulator. Blueline doesn't ship
that emulator. This folder builds it on your own PC instead:

| File | What it is |
|---|---|
| `Build DLSS Emulator.cmd` | Double-click this to run the build program |
| `Build-DlssEmulator.ps1` | The build program |
| `nhl19-dlss.patch` | Blueline's DLSS changes to shadPS4 (source code, GPL-2.0) |

## What it does

1. Downloads the shadPS4 source it is based on: [Kravickas/shadPS4](https://github.com/Kravickas/shadPS4), branch
   NHL19-Build, commit `b23fb67`.
2. Applies `nhl19-dlss.patch`. The patch adds:
   - DLSS through NVIDIA's NGX SDK
   - depth and motion vectors from the scene draws
   - camera jitter
   - an address-space fix that leaves room for CUDA
   See `docs/UPSCALING.md` in the Blueline repo for details.
3. Shows you NVIDIA's RTX SDK licence and asks you to accept it. Then it downloads the DLSS SDK files from
   [NVIDIA's own GitHub](https://github.com/NVIDIA/DLSS/tree/v310.9.16) (v310.9.16) and checks each file's SHA-256.
4. Builds the emulator with Visual Studio's Clang, CMake and Ninja.
5. Puts the finished `shadPS4.exe` and `nvngx_dlss.dll` in `shadPS4-dlss\`, next to the program, with both licences.

## What you need

- Windows 10 or 11 (64-bit), and an NVIDIA RTX graphics card for DLSS
- [Git](https://git-scm.com/download/win), or run `winget install Git.Git`
- Visual Studio 2022 or its free Build Tools, with these installed:
  - **Desktop development with C++**
  - **C++ Clang tools for Windows**
  - **C++ CMake tools for Windows**
- About 6 GB free disk space, and 15 to 40 minutes

## Running it

Double-click `Build DLSS Emulator.cmd`. If the build stops, run it again; it carries on where it stopped.

To also copy the result into your emulator folder, run it from PowerShell like this. It keeps your old exe as
`shadPS4.exe.before-dlss-<date>`.

```powershell
.\Build-DlssEmulator.ps1 -InstallTo "C:\path\to\your\shadPS4"
```

Then in Blueline Studio, go to the Play tab and turn on **DLSS (DLAA)**. With it off, the emulator works as before.

## Licences

- shadPS4 and `nhl19-dlss.patch` are under GPL-2.0. The patch is the full source of Blueline's changes.
- DLSS is NVIDIA software under the NVIDIA RTX SDKs licence. The program downloads it from NVIDIA for your own use.
  Blueline doesn't include it or pass it on.
- An emulator you build this way includes NVIDIA's code, so don't share the built files with others. Share this folder
  instead, and let each person build their own.
