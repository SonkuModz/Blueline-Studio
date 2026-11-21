# Upscaling and DLSS for NHL 19 under shadPS4

Status: feasibility study, 2026-11-19, followed by a working DLSS (DLAA) build in a scratch emulator; see
section 8. Claims are tagged:
**Measured** (seen on this PC), **Read** (from source code or docs), **Untested** (reasoned, not tried).

## 1. Short answer

> **Update:** after this study the user chose to build real DLSS anyway. It now runs as DLAA at 1080p in a scratch
> emulator; see section 8 for what was built, the address-space fix it needed, and measured costs. The text below is
> the original feasibility answer.

**Real DLSS Super Resolution would add nothing on this setup today.** DLSS upscales a low-resolution frame to a
higher one and buys back GPU time. Here:

- the game presents a **1920 x 1080** display buffer (**Measured**). The 3D scene itself is drawn into either a
  "high-res" target set at that size **without MSAA**, or a "low-res" set **with 2x MSAA** at a size from
  `ren_neo_low_res_*`. The frame script switches between them per scene (`PRESENTATION render_lowres`). Which one
  gameplay uses, and the low-res size, are **not yet measured** (section 3),
- both monitors are **1920 x 1080** and the game window is **1600 x 900**, so the presented frame is currently being
  *shrunk*, not enlarged (**Measured**),
- the GPU is not the limit in the scene measured: **136 FPS uncapped at 35–37 % GPU use and 45 W** on an RTX 4060 Ti
  (**Measured**, light on-ice scene, see section 3).

At the presented-frame level there is nothing to upscale and no GPU time to buy back. The one open question that
could change this is the low-res scene size: if gameplay draws the scene well below 1080p, an upscaler would have a
gap to fill. The parts of the DLSS family that could help at 1080p (DLAA-style anti-aliasing) need inputs the game
does not produce, chiefly sub-pixel jitter (section 5).

**Recommendation:** use the cheaper wins in section 6 first. Fix the window size, then try **supersampling**: have the
game draw above 1080p and scale down. That gives the anti-aliasing DLSS or DLAA would give, there is GPU headroom for
it, and it may need no code. Revisit DLSS only if you move to a 1440p or 4K display.

## 2. Hardware and emulator

| Item | Value | Source |
|---|---|---|
| GPU | NVIDIA GeForce RTX 4060 Ti, 8 GB, driver 621.01 | **Measured** (`nvidia-smi`) |
| DLSS support | Super Resolution and DLAA on any RTX card; Frame Generation needs RTX 40 or newer, which this is | General knowledge, not checked against current NVIDIA docs |
| Displays | `DISPLAY1` 1920 x 1080 60 Hz (Samsung C32R50x, primary), `DISPLAY2` 1920 x 1080 (32S301) | **Measured** (Windows screen list; DPI scaling could hide a 4K panel, but the primary reports 1920 x 1080 at 60 Hz through the GPU) |
| Emulator | shadPS4 v0.21.1 WIP, `Kravickas/shadPS4` branch `NHL19-Build`, commit `b23fb6716c44` | **Measured** (exe strings, window title) |
| That branch vs upstream | 26 commits ahead, 1 behind `shadps4-emu/shadPS4` main (buffer cache, page manager, rasterizer, video out) | **Read** (GitHub compare) |
| Real per-game config | `neo_mode` on (PS4 Pro), window 1600 x 900, `vblank_frequency` 9999, readbacks mode 2, FSR off, RCAS on 250, networking on | **Read** (not changed; hashes checked before and after) |
| Global config | `internal_screen_width/height` 1280 x 720 (the "TV resolution" reported to the game, not overridable per game) | **Read** |

## 3. What the game actually renders

From one boot of a copied emulator in a scratch folder (own `user` folder, networking off, Info logging):

```
sceVideoOutGetResolutionStatus: called
RegisterBuffers: ... pixelFormat = A8R8G8B8Srgb, width = 1920, height = 1080, pitchInPixel = 1920
```

- The game registers **two 1920 x 1080 sRGB display buffers**, even though the emulator reports a 1280 x 720 TV
  (**Measured**). Either it ignores the reported size or clamps to 1080p.
- In Pro mode it runs `sample_backend_4k.scrape` (**Read**, extracted with `blueline extract`). Every main scene target
  exists twice: `*_highres` at `$width0 x $height0` with **no MSAA**, and `*_lowres` at `$lowres_width0 x
  $lowres_height0` with **2x MSAA** (colour, depth, resolve, velocity, frame buffer). `proc SetFrameTextureAliasing`
  picks one per frame: `if PRESENTATION render_lowres` → `settexturealias lowres`, else `highres`. The engine fills the
  sizes from `ren_neo_4k_res_*`, `ren_neo_low_res_*` and `ren_neo_force_4k` (VISUALS.md 12.3). **Not measured:**
  which path gameplay takes and what `$lowres_width0` is. If gameplay uses the high-res path, the scene is drawn at
  1080p with no MSAA and FXAA only, which explains jaggies on lines and boards.
- The emulator then scales the 1080p frame down into the 1600 x 900 window with a **single linear sample** (**Read**,
  `pp_pass.cpp` sampler `eLinear`). The FSR pass **returns early when the output is no larger than the input, so
  neither EASU nor RCAS runs** at this window size (**Read**, `fsr_pass.cpp` line 139).
- Performance in the first on-ice scene (centre ice, one skater, HUD up), uncapped because of `vblank_frequency 9999`.
  These are the only FPS readings taken at the same moment as GPU readings:

| Sample | FPS (frame time) | GPU use | GPU power | Emulator RAM | Emulator CPU |
|---|---|---|---|---|---|
| on ice | 130 (7.7 ms), then 136 (7.3 ms) 40 s later | 35–37 % | 45 W of about 160 W | 3.97 GB | 2.6 of 24 threads |

  Earlier 2-second samples showed 67–71 % GPU use about three minutes before, but what was on screen then is unknown
  (loading or intro), so they are left out.

Screenshot (context only; it was captured from a scaled desktop, so it says nothing about image quality):
[upscaling/stock_1080p_in_1600x900_window_130fps.jpg](upscaling/stock_1080p_in_1600x900_window_130fps.jpg).
**Caveat:** this is a light scene. A full game with crowd, replays and post-processing is heavier and has not been
measured. That is the first check before any decision that depends on GPU headroom.

## 4. Where upscaling could hook in

### 4.1 The game (VISUALS.md sections 4, 10, 12)

| Input DLSS needs | NHL 19 has | Usable? |
|---|---|---|
| Lower-resolution colour, before post | HDR `R11G11B10` scene, resolved, then post (HBAO, bloom, scatter, DOF, motion blur, refraction, uber tone map, FXAA), then Scaleform UI composited | Only the final frame is easy to reach. The pre-post image exists but lives in guest memory with no fixed address |
| Motion vectors | `VelocityColorTexture`, **RGBA8**, cleared to `0.5,0.5,…`, written **only for objects** and **only when motion blur is on** (**Read**). Camera motion is presumably rebuilt from depth in the motion blur shader (inferred, not read) | Too coarse (8-bit) and incomplete (no static scenery) for DLSS |
| Depth | MSAA depth, then a min-resolved `depthResolveTexture` (`D32S8`) | Yes, if found each frame |
| Sub-pixel camera jitter | **None found.** The Pro frame script resolves MSAA and runs `fxaa.fx` (`if POSTFX FXAA`). No TAA pass. `smaa` shaders ship in the game, but `smaa` does not appear in `sample_backend_4k.scrape` (**Read**) | **Missing.** Without jitter DLSS cannot recover detail, and at 1:1 it acts as a smoothing filter that may ghost |
| UI separated | Drawn to its own target, then composited before present | A present-time pass hits the HUD as well |

There is **no TAA, no jitter and no full-screen motion-vector buffer**, so nothing exposes DLSS-ready inputs.

### 4.2 shadPS4

- **Host passes** run on the guest's final image at present time, in `vk_presenter.cpp`: an optional
  **FSR 1** pass (`fsr_pass.cpp`, EASU upscale plus RCAS sharpen, `fsr_enabled`, `rcas_enabled`,
  `rcas_attenuation`), then a post pass (`pp_pass.cpp`, gamma and HDR) (**Read**). This is where any spatial upscaler
  goes. It sees **only** the final 1080p frame, HUD included.
- **The fork author's `DLSS` branch** (`Kravickas/shadPS4@DLSS`, last commit 2026-09-26) (**Read**):
  - `xfb_velocity_pass.cpp` (1,169 lines) plus `xfb_capture.cpp` and shaders: captures clip-space vertex positions of
    scene draws through **transform feedback**, two frames at a time, and rasterises per-vertex motion into an
    `RG16F` motion-vector target with a matching `R32F` depth copy. A compute-shader camera solve fits a
    clip-to-previous-clip transform. **This is the hard part of real DLSS, inferring motion vectors and depth inside
    the emulator, and it is already written.**
  - `dlss_nr_pass.cpp`: **not DLSS Super Resolution.** It drives NGX **feature 18, "Neural Rendering"**
    (`DLSSNR.*`: Intensity, Style, LocalToneStrength, SkinStructureStrength, UICorrection) at **1:1 size** on the
    **final presented image with the HUD**. It does no upscaling and uses no jitter.
  - It loads a model file `nvngx_dlssnr.dll` placed beside the exe. The branch's CI (`build.yml`) packages only the
    forwarder `nvngx.dll_dlssnr.dll`, never the model, so where the model comes from is not recorded anywhere in the
    repo. I don't know of it in the public DLSS SDK. `dlss_nr_forwarder.cpp` is built as `nvngx.dll_dlssnr.dll` **so that the model's
    caller-path check passes**. That works around a protection in NVIDIA's binary. I would not build on that part or
    fetch the model file without your decision.
  - The branch is **359 commits behind** `NHL19-Build`'s upstream base and adds no config toggle. Nothing shows it ever
    ran on NHL 19.
- **The public route** for real DLSS is NVIDIA's DLSS SDK (Vulkan NGX API, redistributable `nvngx_dlss.dll`) or
  Streamline. These are from memory; I'll check the current source and licence before asking. It needs your OK to download; I will give the exact file and size first.
- The 4060 Ti exposes `VK_EXT_transform_feedback`, `VK_NV_optical_flow` and `VK_NV_low_latency2` (**Measured**,
  emulator log), so both the XFB approach and Frame Generation would be possible on this card.

## 5. Options compared

| Option | What it takes | Gain on a 1080p monitor | Risk / cost | Verdict |
|---|---|---|---|---|
| **(a) Spatial "DLSS-like"** at present time (FSR 1 EASU, or NGX NR filter) | Exists (FSR 1); NR needs the unofficial model DLL | None for upscaling (input = output). NR is a stylising filter, not resolution | NR: unknown DLL, caller-check workaround, HUD gets filtered | **No** |
| **(b) Real DLSS SR / DLAA** | Port XFB velocity and depth (about 3k lines, 359 commits of drift), add **jitter injection** (offset `gl_Position` by a sub-pixel amount for main-scene draws only, and tell DLSS the offset), run DLSS **before** the game's post stack (needs locating the resolved HDR target per frame), keep the HUD out | DLAA at 1080p could beat FXAA on shimmering ice lines and boards. SR gains nothing at 1080p output | Weeks of work. Post effects (motion blur, DOF, grain, FXAA) after DLSS fight it. Ghosting on pucks and stick blades. XFB capture costs GPU every frame | **Not now.** Only worth it for a 1440p or 4K display, and only after (d2) |
| **(c) Frame Generation** (DLSS-G via Streamline, or driver Smooth Motion) | Streamline plus Reflex integration in the presenter | None: the game already runs 130+ FPS on a 60 Hz monitor | Latency, UI artefacts | **No** |
| **(d1) Fix the window** | Config only: window 1920 x 1080 or borderless fullscreen | Removes the current 1080p to 900p downscale blur | None | **Do first** |
| **(d2) Supersampling** (render above 1080p, scale down) | Game side: `ren_neo_force_4k` / `ren_neo_4k_res_*`, or a larger reported TV size (`internal_screen_*`, global only) so the Pro path picks 4K. Emulator side: a good downsample filter (today's scale is plain linear) | Real anti-aliasing on lines, logos and jerseys, the main thing DLAA would give | GPU headroom looks fine (about 36 % used); **8 GB VRAM** and 16 GB RAM need watching at 4K targets | **Recommended next test** |
| **(d3) RCAS sharpening only** | **Needs a small patch:** the FSR pass skips RCAS whenever output ≤ input, which is always the case at 1080p | Small crispness gain, cheap | Halos if pushed | Optional, fold into the d2 patch |
| **(d4) Turn off FXAA / motion blur / grain** via the frame script or postfx data | Blueline patch to `sample_backend_4k.scrape` / `postfx/*.xml` (untested levers, VISUALS.md 13) | Sharper image; pairs with d2 | Script errors break rendering; test in the mods overlay | Optional |

## 6. Proposed next steps (each needs your go-ahead)

0. **Settle the scene size:** a RenderDoc capture of one gameplay frame in the scratch copy (`renderdoc_enabled`),
   to read whether the `lowres` or `highres` targets are bound and their size. If gameplay renders well below 1080p,
   this whole recommendation is revisited.
1. **Measure a real game** in the scratch copy (networking off, own user folder): FPS, GPU use, VRAM and RAM during a
   full period with crowd and replays. I can boot it, but you may need to drive the menus.
2. **Supersampling test, no code:** in the scratch config only, set `internal_screen_*` to 3840 x 2160 and the
   window to 1920 x 1080, and see whether the game registers a 3840 x 2160 buffer. If it stays at 1080p, try
   `ren_neo_force_4k` through the game's own settings data. Report buffer size, FPS, VRAM and screenshots against
   stock.
3. If 2 works: a **small emulator patch** for a proper downsample filter (box or Lanczos, replacing the single linear
   sample) plus RCAS at 1:1, with toggles `downsample_filter` and `rcas_always` in the config, built from `NHL19-Build` at `b23fb67` and tested only on a scratch exe.
4. **Real DLSS (option b)** only if you plan to play at 1440p or 4K, or if step 1 shows the GPU is the limit in full
   games. It would start by porting `xfb_velocity_pass` and validating the motion vectors visually, before any NVIDIA
   SDK download.

## 7. Test hygiene used

- Copied `boost\` into the session scratchpad. Ran with the working directory there so shadPS4 used a portable
  `user\` folder holding a copy of the configs with `connected_to_network` and `shad_net_enabled` set to false.
- SHA-256 of the real `config.json`, `custom_configs\CUSA11126.json` and `shadPS4.exe` matched before and after:
  `5d648f95…`, `f0610c47…`, `fe88293d…`.
- Free RAM before the run: about 7 GB. The emulator used about 4 GB.

## 8. Real DLSS build (after the "go for real DLSS" decision)

### 8.1 What was built

- Fork of the user's emulator source (Kravickas NHL19-Build b23fb67) at `C:\Users\sonku\dev\shadPS4-dlss`, branch
  `nhl19-dlss`, uncommitted. Official NVIDIA NGX SDK 310.9.16 (`nvsdk_ngx_d.lib`, `nvngx_dlss.dll`, signed).
- Inputs: depth and motion vectors from the Kravickas `xfb_velocity_pass` (transform-feedback capture of each scene
  draw, replayed against last frame's positions). Its "Neural Rendering" part and caller-check forwarder were left out.
- Sub-pixel jitter (Halton 2,3, 16 phases) added to the viewport of scene draws only (depth-tested, full-size depth).
- DLSS runs on the presented 1920 x 1080 frame. Input and output sizes match, so this is **DLAA** (anti-aliasing),
  not upscaling. There is no HUD mask: DLSS sees the HUD too.
- Settings, all in the `GPU` group of the emulator JSON (per game: `custom_configs/CUSA11126.json`):
  `dlss_enabled` (default false), `dlss_jitter` (true), `dlss_preset` (0 = NGX default), `dlss_jitter_flip`,
  `dlss_debug_view` (1 = motion vectors, 2 = depth). With everything off the emulator behaves as before.

### 8.2 The blocker and its fix (**Measured**)

- DLSS needs the device extension `VK_NVX_binary_import`, which starts CUDA inside the driver. With it,
  `vkCreateDevice` failed (`ErrorInitializationFailed`).
- Cause: for games built with SDK 3.00 or later (NHL 19 is), shadPS4 on Windows reserves every free address range from
  0x400000 up to 1 TB. CUDA needs one free block of more than 4 GB (8 GB was enough) below that 1 TB line.
  Reproduced outside the emulator with a small test program that reserves the same ranges.
- Fix, applied only when `dlss_enabled` is true: stop the guest user range at 0xFC00010000 (NHL 19 maps fixed pages
  counting down from 0xFBFFFFC000, so it cannot go lower; a first try at 0xF800000000 crashed the game) and place
  the 8.4 GB guest memory backing above 1 TB so it does not fill the gap. That leaves about 16 GB free for CUDA.
- Also: the NGX extension list includes `VK_EXT_buffer_device_address`, which must not be enabled next to the core
  1.2 feature the emulator already uses; it is skipped.

### 8.3 Numbers (**Measured**, scratch copy, networking off, 1920 x 1080 borderless, RTX 4060 Ti)

| Scene | DLSS off | DLSS on (DLAA, jitter on) |
|---|---|---|
| Title screen ("Press X") | 48–49 FPS, 72 % GPU | 45.3 FPS, 76 % GPU |
| Free-skate tutorial, centre-ice start (same frame both ways) | **133–138 FPS** (7.3 ms) | **113 FPS** (8.8 ms) |
| On ice, wide camera near the goal | not measured | 81 FPS |
| Replay free cam, close-up on a player | not measured yet | 30–34 FPS |

GPU time per frame, from timestamps in the build:

- DLSS evaluate: **0.86–1.0 ms**.
- Velocity pass (motion vectors and depth copy): 0.28 ms at the title, 0.8–0.9 ms on ice (120–215 scene draws per
  frame, 97–98 % matched to the previous frame).

The two timed passes add about 1.2–1.9 ms per frame. The in-draw capture is not timed; the title A/B (20.6 to
22.1 ms per frame) puts the whole cost there at about 1.5 ms. Note: the title screen runs at about 48 FPS, not 130+.
The 130–136 figure in section 3 is the tutorial skate at centre ice, in the real emulator (1600 x 900 window, its
own settings). The 81 FPS DLSS shot is the same tutorial at a different spot and camera in the scratch copy, so the
two are **not** comparable. The like-for-like on-ice A/B (row 2 of the table) gives the same answer as the title:
about **+1.5 ms per frame** for DLAA (7.3 → 8.8 ms).

**Quality, same frame (Measured, but through a scaled 1456 x 819 desktop capture):**
[player](upscaling/ab_player_off_vs_dlaa.png), [centre-ice text](upscaling/ab_logo_off_vs_dlaa.png),
[Red River logo](upscaling/ab_redriver_off_vs_dlaa.png), full frames [off](upscaling/ab_full_dlss_off_133fps.jpg)
and [on](upscaling/ab_full_dlaa_113fps.jpg). Left is off, right is DLAA. With DLAA the jersey name and numbers are
cleaner and the faceoff circle line is smoother with fewer stair-steps. The ice text is a touch softer. The scaling
in the capture hides some aliasing, so the real difference on screen is likely larger than these crops show.

Screenshots (DLSS on): [on ice, wide](upscaling/dlaa_on_ice_wide_81fps.jpg),
[replay, jersey](upscaling/dlaa_replay_jersey_33fps.jpg),
[replay, face close-up](upscaling/dlaa_replay_faceoff_closeup_34fps.jpg). These are scaled-down desktop captures
(1456 x 819), good for context and FPS, **not** for judging anti-aliasing. The close-up shows a bright fringe above
the helmet that still needs checking against DLSS off. Depth direction: the build reads the game's depth-test
functions each frame and found normal (less-than) depth throughout, so DLSS was given non-inverted depth.

### 8.4 Open items

- Native-resolution crops (the tutorial moves on within seconds, so only full frames matched this time), DLSS on with
  jitter flipped, and the stock-built exe at the same frame.
- FSR is not a third option at 1080p: the pass returns early when output is not larger than input (**Read**).
- The diagnostic GPU timers in the build (DLSS evaluate, velocity pass) must come out or be tidied before shipping.
- Jitter direction (`dlss_jitter_flip`) A/B on a still scene; HUD ghosting; depth-inverted detection.
- Keyboard input does not reach the scratch emulator while a controller is connected; someone has to press X.
- Nothing is in the user's real emulator. Shipping it needs the user's OK, the NGX DLL beside the exe, and Overtime
  Roster updating its recorded exe hash.

### 8.5 Installed (2026-11-19)

The DLSS build now sits in `%APPDATA%\OvertimeRoster\boost\shadPS4.exe`, with `nvngx_dlss.dll` beside it. The stock
exe (sha256 fe88293d...) is backed up in `OvertimeRoster\backups\dlss-20261119\` with a README on how to go back.
The old file was unlinked rather than overwritten, so its other hard links, such as `online-emu\...\shadPS4.exe`, are still stock.
Neither config file was touched. DLSS stays off until it is turned on.

Studio's Play tab shows a **DLSS (DLAA)** switch (`GPU.dlss_enabled`) only when the chosen emulator is this build:
the exe holds the key and `nvngx_dlss.dll` is beside it. A key the file lacks reads as off, and saving writes it into
the file being edited, normally `custom_configs\CUSA11126.json`. An Overtime Roster update to boost\ would bring the
stock exe back, and the switch would then disappear.


## 9. Build program for other players

`tools/dlss-emulator` (the `dlss-emulator` folder in the release zip) rebuilds this emulator on any PC. It holds `nhl19-dlss.patch` (the
fork's changes against Kravickas NHL19-Build `b23fb67`, GPL-2.0) and `Build-DlssEmulator.ps1`. The program fetches that commit
and its submodules, applies the patch, shows NVIDIA's RTX SDK licence and asks for "yes", downloads the DLSS SDK v310.9.16 files
from github.com/NVIDIA/DLSS (SHA-256 checked against the files this build was made with), and builds with Visual Studio's
clang-cl, CMake and Ninja. Blueline ships no NVIDIA files, and a built emulator should not be passed on, since it contains
NVIDIA code linked into a GPL program.
