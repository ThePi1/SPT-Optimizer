# SPT-Optimizer

Frame-time optimizations for Escape from Tarkov under SPT (single-player Tarkov) and Fika (co-op), as BepInEx plugins. Every optimization can be switched on and off in game, and each one was measured with an in-game A/B profiler before being switched on by default.

**Version 0.4.0 · for SPT 4.1.6** (EFT 0.16.9.40743). Fika 2.4.x is optional. See [CHANGELOG.md](CHANGELOG.md).

## Install

1. Download `SPT-Optimizer-v0.4.0-SPT-4.1.6.zip` from the [Releases](https://github.com/ThePi1/SPT-Optimizer/releases) page.
2. Extract it into your SPT folder, the one with `EscapeFromTarkov.exe`. You should end up with:
   ```
   BepInEx\plugins\SPTOptimizer\SPTOptimizer.Optimizations.dll
   BepInEx\plugins\SPTOptimizer\SPTOptimizer.Profiler.dll
   BepInEx\plugins\SPTOptimizer\default-targets.txt
   ```
3. Start the game. `BepInEx\LogOutput.log` should contain a line with `SPTOptimizer.Optimizations] Loaded.`

**Which files you need:**
- `SPTOptimizer.Optimizations.dll` is the mod.
- `SPTOptimizer.Profiler.dll` and `default-targets.txt` are a measuring tool (frame captures and A/B tests on F5–F9). You can delete them if you only want the optimizations.
- **Fika headless host:** install `SPTOptimizer.Optimizations.dll` only.

**SPT version matters.** SPT refuses plugins built for a different SPT version (the "Outdated Mods" screen), so use the zip whose name matches your SPT version.

**Updating from 0.2.0:** the settings menu was reorganised, so 0.4.0 starts from the default settings. Delete `BepInEx\config\com.sptoptimizer.optimizations.cfg` and `com.sptoptimizer.profiler.cfg` to drop the old entries, then re-apply anything you had changed.

**Uninstall:** delete `BepInEx\plugins\SPTOptimizer` and the two `com.sptoptimizer.*.cfg` files in `BepInEx\config`.

## Quick start

The defaults are the recommended setup: everything that measured faster with no visible change is on, and everything with a visible trade-off is off. To change anything, press **F12** in game and open **SPTOptimizer.Optimizations**:

| Section | What's in it |
| --- | --- |
| **1. Standard Optimizations** | On by default, no visible change. Leave these on. |
| **2. Extra Optimizations (potential visible impact)** | Off by default. Each trades a little visual quality for speed, for example *Scope camera at reduced rate* or *Far player arms at reduced rate* (Fika clients). The description says what you give up. |
| **3. Experimental / Other** | Not recommended for normal play. |
| **4. Diagnostics** | Logging for testing. Hidden unless you tick **Advanced settings**. |

Tick **Advanced settings** at the top of the F12 window to see the fine-tuning values (distances, intervals).

Two settings outside the mod matter more than anything in it:
- **Display mode: Borderless**, not Fullscreen. Exclusive fullscreen added ~4 ms per frame of waiting in testing (−3.5 ms when switched).
- **In Fika, a headless host on a second PC** takes the bots' AI off your PC: −1.4 to −2.0 ms per frame, 1% lows up from 61 to 86 fps.

## What it does

Measured on Streets of Tarkov (7800X3D, RTX 3090, 1440p); your gains depend on your CPU, map and settings. The game is limited by its main CPU thread, so these target CPU time, not the GPU.

| Optimization | Default | Measured gain |
| --- | --- | --- |
| GC policy (no gameplay garbage collection) | on | removes 50–150 ms hitches after looting |
| Shadow proxy culling | on | −0.5 to −1.0 ms |
| LOD group culling (new in 0.4.0) | on | −0.56 ms camera culling (frame −0.5 ms) on a Fika client |
| Faded light culling | on | −0.24 ms |
| Batched overlaps | on | −0.2 ms where bots are simulated (self-hosted, or the Fika host) |
| Runaway move guard (parked BTR gunner on Streets) | on | up to −6 ms before the BTR arrives (self-hosted, or the Fika host) |
| All of the above except LOD group culling, together | | −1.57 ms (+12%) self-hosted; −1.1 to −1.3 ms on a Fika client |
| Scope camera at reduced rate | off | −0.9 to −1.8 ms while looking through a magnified scope |
| Far player arms at reduced rate | off; Fika clients only | −0.31 ms with ~20 bots |
| Declutter small / medium objects | off | ~0 ms (a visual preference) |
| Distant bot animation at half rate | off; experimental | ~0 on a Fika client |

Each optimization is described in [Optimizations/README.md](Optimizations/README.md).

## Fika

- **What runs where:** your PC draws the raid, so the culling and rendering optimizations help clients. Bot simulation runs on the host, so *Batched overlaps* and the *Runaway move guard* help the host (or you, when you host). Details: [Optimizations/README.md → Multiplayer (Fika)](Optimizations/README.md#multiplayer-fika).
- **Multiplayer-safe:** nothing here changes what is solid, where bodies are, or how hits are decided. The visual trade-offs only change what your own screen shows.
- **Mod checks:** by default Fika allows any mod. If your server uses Fika's *required* or *optional* mod lists, add `com.sptoptimizer.optimizations` (and `com.sptoptimizer.profiler` if you keep it) to *optional*, and give every player the same build: Fika then compares a checksum of each DLL.

## Compatibility with other mods

Checked against the source of PerformanceScope, Dynamic External Resolution (DERP), PiP-Disabler and Tarkov DLSS 4.5: none of them patch the same methods, and all can be installed together. See [Optimizations/README.md → Other performance mods](Optimizations/README.md#other-performance-mods-checked-2026-10-22-against-their-source) for how they interact.

## For developers

### What's in here

| Path | What |
| --- | --- |
| [Optimizations/](Optimizations/README.md) | `SPTOptimizer.Optimizations` plugin: the optimizations and opt-in trade-offs, each with its measured gain |
| [Profiler/](Profiler/README.md) | `SPTOptimizer.Profiler` plugin: per-frame capture (F9), method instrumentation (F8), scene census (F7), automated A/B sequences with screenshot comparison (F5) |
| [Shared/](Shared/ConfigBuilder.cs) | Code compiled into both plugins (settings menu order) |
| [tools/analyze.py](tools/analyze.py) | Standalone analyzer for a capture's `frames.csv` (stdlib Python) |
| [FINDINGS.md](FINDINGS.md) | Dated lab notebook: every measurement, A/B result and dead end, with capture names |
| [PLAN.md](PLAN.md) | Plan of attack, status, open items and what has been ruled out |
| [TESTING.md](TESTING.md) | Test environment, how to run each kind of test, and where the data goes |
| [PORTING.md](PORTING.md) | Checklist for moving to a new EFT/SPT/Fika version |
| [CHANGELOG.md](CHANGELOG.md) | Changes per version |
| [CREDITS.md](CREDITS.md) | Mods and tools this builds on |
| [reports/](reports/) | Research report: rendering and script techniques, Fika compatibility, headless offloading |
| [research_notes/](research_notes/) | The source notes behind the report |
| `data/` | Local only (git-ignored): archived captures, logs, configs, builds and the decompile. Layout in [TESTING.md](TESTING.md#archiving) |

FINDINGS.md has measurements from both SPT 4.0.13 / Fika 2.3.9 (EFT 0.16.9.40087, up to 2026-10-20) and SPT 4.1.6 (from 2026-10-22, on a headless client); [PORTING.md](PORTING.md) covers what changed between them. Open work and ruled-out ideas are in [PLAN.md](PLAN.md#open-identified-not-built-yet).

### Build

Requires the .NET SDK and an SPT 4.1.6 install to compile against. Pass your SPT folder as `SptDir`: each plugin compiles against that install's DLLs and, after building, copies itself into its `BepInEx\plugins\SPTOptimizer`.

```bash
dotnet build Optimizations -c Release -p:SptDir="C:\SPT"
```

```bash
dotnet build Profiler -c Release -p:SptDir="C:\SPT"
```

Add `-p:Deploy=false` to build without copying. Build while the game is closed: a running game locks the DLLs. Without `SptDir`, the project files fall back to the author's test install path.

## License and credits

MIT, see [LICENSE](LICENSE). The declutter settings are based on the idea of [Simple-Declutter](https://github.com/gitTerebi/Simple-Declutter) by gitTerebi; see [CREDITS.md](CREDITS.md).
