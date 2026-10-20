# Testing

How the measurements in [FINDINGS.md](FINDINGS.md) were taken, so new results stay comparable.

## Test environment (SPT 4.0.13 install)

| | |
| --- | --- |
| Install | `I:\Games\spt-4-0-13-hideoutroulette`, a separate test copy that can be changed freely |
| Hardware | Ryzen 7 7800X3D, RTX 3090, 64 GB, 2560×1440 at 240 Hz |
| Client | EFT 0.16.9.40087, Unity 2022.3.43f1, SPT 4.0.13, BepInEx 5.4.23.2 |
| Fika | Fika.Core 2.3.9, server 2.3.5. The headless host runs on a second PC on the LAN (192.168.4.53) |
| Graphics (`SPT\user\sptSettings\Graphics.ini`) | **Borderless** (`FullScreenMode: 1`), Shadows Medium, Textures Medium, visibility 1000, object LOD 2, SSAO Fastest, SSR Low, TAA Low, Reflex off, frame limit off, mip streaming off |

### Changes made to the test install

- **Weather forced to sunny.** `SPT\SPT_Data\configs\weather.json`: the "default" preset weights allow only SUNNY, so benchmarks are consistent. The original is saved as `weather.json.orig`. Rain and overcast switch off sun shadows and change the render cost, so only compare same-weather runs.
- **Fika installed on 2026-10-20.** Fika moved `FullScreenMode` to exclusive (0). It was set back to borderless after the exclusive/borderless test showed exclusive adds ~4 ms per frame.
- **Plugins:** `BepInEx\plugins\SPTOptimizer\` holds both DLLs and the profiler's default method-target list.
- **SPT's Graphics.ini is the one that counts.** SPT keeps game settings in `SPT\user\sptSettings\`. The AppData copy (`%APPDATA%\Battlestate Games\Escape from Tarkov\Settings\`) belongs to live Tarkov and isn't read.

## Kinds of test

All captures are written to `<game>\SPTOptimizer\captures\<date-time>_<map>\`, with `summary.txt`, `frames.csv` and, when enabled, `methods.csv` and `shots/`.

| Test | How | Use it for |
| --- | --- | --- |
| **Plain capture** | F9 to start, F9 to stop | Baselines, play sessions, hitches |
| **Instrumented capture** | F8 (instrument methods), then F9 | Per-method costs. Instrumentation inflates script slots, so don't compare its frame times with uninstrumented runs |
| **A/B sequence** | F5. Runs Baseline and experiment steps (12 s each, 3 repeats, round-robin) chosen by *Sequence experiments* in the profiler config, with a screenshot check per step | Measuring one change while standing still. Report: the "A/B experiments" table in `summary.txt`, and `shots/diff.txt` |
| **Auto-start A/B** | Profiler config *Auto-start A/B after raid start (s)* > 0 | Hands-off runs at a fixed time into the raid |
| **Declutter report** | Optimizations config *Diagnostics → Declutter report* | Tuning the declutter keyword list. Writes `<game>\SPTOptimizer\declutter_<map>.txt` |

### Rules that keep A/B results honest

- **Stand still and keep the view fixed** during F5. The report skips the first 3 s of each step, and per-repeat deltas show the spread. A spread above ~0.5 ms means the run was noisy.
- **Streets before the BTR arrives:** the parked BTR gunner inflates frame time by up to 6 ms when the runaway guard is off. Note the timing for any guard-related test.
- **Same weather, same display mode, same optimization config** between runs you compare.
- **Bot count differs between raids.** Record `bots_alive` from the summary and correct for it. The client costs ~0.1 ms per observed player on a headless client, and ~0.25–0.3 ms per bot when self-hosted.
- **Compare at matched draw-call counts** when views differ. Frame time grows ~1 ms per 1,000 draw calls on Streets.

### Self-hosted vs headless comparison (2026-10-20 protocol)

Two Streets raids on the same install: Fika self-hosted, then on the headless host. In each:
1. 60 s F9 capture standing still at the same landmark, facing the same way.
2. 3–5 min F9 capture of normal play.

No F8 and no F5. Compare by category: rendering, Update, LateUpdate, animation, other.

## Analysis

- `summary.txt` covers most questions: frame stats, PlayerLoop breakdown, A/B table, worst frames.
- `python tools/analyze.py <capture dir> [--raid] [--experiment NAME] [--from T --to T]` handles time windows and per-experiment filtering.
- Ad-hoc scripts (per-slot category sums, draw-call bins, present-wait analysis) were run from the session scratch folder. FINDINGS.md quotes their results.

## Archiving

Copy captures, logs and configs into `data\<install-name>\` in this repo before an install is retired or replaced. [data/README.md](data/README.md) describes the layout. The 4.0.13 install is archived in `data\spt-4.0.13\`. Logs to grab, because they are overwritten on every game start:
- `BepInEx\LogOutput.log`
- `%USERPROFILE%\AppData\LocalLow\Battlestate Games\EscapeFromTarkov\Player.log` and `Player-prev.log`
