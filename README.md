# SPT-Optimizer

Frame-time optimizations for Escape from Tarkov under SPT (single-player Tarkov) and Fika (co-op), shipped as BepInEx 5 / HarmonyX plugins. Every optimization can be toggled and is measured with an in-game A/B profiler before it's switched on by default.

Targets **SPT 4.1.6 / Fika 2.4.6 (EFT 0.16.9.40743, Unity 2022.3.43f1, Mono, DX11)**. FINDINGS.md has measurements from both SPT 4.0.13 / Fika 2.3.9 (EFT 0.16.9.40087, up to 2026-10-20) and SPT 4.1.6 (batch 4, 2026-10-22, on a headless client); [PORTING.md](PORTING.md) covers what changed between them. Open work and ruled-out ideas are listed in [PLAN.md](PLAN.md#open-identified-not-built-yet).

## What's in here

| Path | What |
| --- | --- |
| [Optimizations/](Optimizations/README.md) | `SPTOptimizer.Optimizations` plugin: the optimizations and opt-in trade-offs. The README lists each one with its measured gain |
| [Profiler/](Profiler/README.md) | `SPTOptimizer.Profiler` plugin: per-frame capture (F9), method instrumentation (F8), scene census (F7), automated A/B sequences with screenshot comparison (F5) |
| [tools/analyze.py](tools/analyze.py) | Standalone analyzer for a capture's `frames.csv` (stdlib Python) |
| [FINDINGS.md](FINDINGS.md) | Dated lab notebook: every measurement, A/B result and dead end, with capture names |
| [PLAN.md](PLAN.md) | Plan of attack, status, open items and what has been ruled out |
| [TESTING.md](TESTING.md) | Test environment, how to run each kind of test, and where the data goes |
| [PORTING.md](PORTING.md) | Checklist for moving to a new EFT/SPT/Fika version |
| [reports/](reports/) | Research report: rendering and script techniques, Fika compatibility, headless offloading |
| [research_notes/](research_notes/) | The source notes behind the report |
| `data/` | Local only (git-ignored): archived captures, logs, configs, builds and the decompile. Layout in [TESTING.md](TESTING.md#archiving) |

## Results so far (Streets, 2026-10-19 to 2026-10-22)

| Optimization | Default | Measured gain |
| --- | --- | --- |
| GC policy (no gameplay GC) | on | removes 50–150 ms hitches after looting |
| Shadow proxy culling | on | −0.5 to −1.0 ms |
| Batched overlaps | on | −0.2 ms (self-hosted; nothing on a Fika client) |
| Runaway move guard (parked BTR gunner) | on | up to −6 ms before the BTR arrives (self-hosted / host) |
| Faded light culling | on | −0.24 ms |
| LOD group culling (0.4.0) | on | −0.56 ms camera culling (frame −0.5 ms) on a headless client |
| All of the above except LOD group culling, together | | −1.57 ms (+12%) self-hosted; −1.1 to −1.3 ms on a headless client |
| Declutter small / medium objects | off | ~0 ms (a visual preference) |
| Half-rate off-screen bot animation | off; since 0.3.0 Fika clients only | −0.7 to −1.1 ms self-hosted, where bots moved ~20% slower; ~0 on a Fika client (its savings and overhead cancel) |
| Far player arms at reduced rate (0.3.0) | off; Fika clients only | −0.31 ms on a headless client |
| Scope camera at reduced rate | off | −0.9 to −1.8 ms while looking through a magnified scope |

Outside the mod:
- **Borderless instead of exclusive fullscreen:** −3.5 ms on a headless client (exclusive adds a ~4 ms present wait).
- **A Fika headless host on a second PC:** −1.4 to −2.0 ms vs self-hosting, with 1% lows up from 61 to 86 fps.

## Build and deploy

Requires the .NET SDK. Each plugin compiles against the game's DLLs and copies itself into `BepInEx\plugins\SPTOptimizer` after building:

```bash
dotnet build Optimizations -c Release
```

```bash
dotnet build Profiler -c Release
```

The game folder defaults to `I:\Games\SPT-4-1-6-testing`. Override it with `-p:SptDir=<path>`, and skip the copy with `-p:Deploy=false`. Build while the game is closed; a running game locks the DLLs.

## Using it with Fika

Every machine in a raid needs the same plugin build (Fika compares DLL hashes). See [Optimizations/README.md → Multiplayer (Fika)](Optimizations/README.md#multiplayer-fika) for which features matter on the client vs the host.
