# Local test data

Everything here except this file is git-ignored (see `.gitignore`): the captures are gigabytes, the logs contain LAN/WAN IP addresses and a listing of the game folder, and `reference/` is decompiled game code.

## `spt-4.0.13/`: archive of the SPT 4.0.13 test install

Copied 2026-10-20 from `I:\Games\spt-4-0-13-hideoutroulette`. Setup at the time: EFT 0.16.9.40087, Unity 2022.3.43f1, SPT 4.0.13, Fika.Core 2.3.9 (server 2.3.5), headless host on a second PC. The file counts and byte totals matched the source.

| Folder | What | Source in the game install |
| --- | --- | --- |
| `captures/` | All profiler runs, 2026-10-18 to 10-20: 629 files, 2.7 GB (`frames.csv`, `summary.txt`, `methods.csv`, `shots/`, census, slow work). The run names match those cited in FINDINGS.md | `SPTOptimizer\captures` |
| `logs/` | BepInEx `LogOutput.log` (last session: the headless raid of the self-hosted vs headless comparison), Unity `Player.log` / `Player-prev.log`, `Patcher.log`, `fika-installer.log`, SPT server logs (`spt-server/`) | `BepInEx\`, `%USERPROFILE%\AppData\LocalLow\Battlestate Games\EscapeFromTarkov\`, `SPT\user\logs` |
| `config/` | Optimizations and profiler configs as last used, profiler method targets, Fika client config | `BepInEx\config` |
| `game-settings/` | SPT's copy of the game settings (Graphics.ini: borderless, Shadows Medium, visibility 1000, LOD 2, Reflex off) | `SPT\user\sptSettings` |
| `build/plugins-SPTOptimizer/` | The deployed plugin DLLs built for 0.16.9 | `BepInEx\plugins\SPTOptimizer` |
| `reports/` | Declutter scan report for Streets | `SPTOptimizer\` |
| `reference/eft-decomp/`, `reference/Development/` | Decompiled `Assembly-CSharp` for 0.16.9, plus modding notes. Use it to diff against the new client when porting patches | `eft-decomp`, `Development` |

Older BepInEx `LogOutput.log` files were overwritten by each game start and no longer exist. The Stats and Batched-overlaps lines quoted in FINDINGS.md are the record of those sessions.

## Next install

Put the SPT 4.1.6 / Fika 2.4.6 install's data in a sibling folder (e.g. `spt-4.1.6/`) with the same layout, so runs from the two clients never mix.
