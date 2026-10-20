"""Analyze an SPTOptimizer.Profiler frames.csv (stdlib only).

usage: python analyze.py <capture dir or frames.csv> [--raid] [--skip S] [--from T] [--to T] [--bin S]

  --raid      keep only frames where the main player exists (pos != 0 and alive_players > 0)
  --skip S    with --raid: drop the first S seconds after the player appears (default 10)
  --from/--to time window in capture seconds
  --bin S     timeline bin size in seconds (default 5)
  --experiment NAME  keep only frames tagged with that A/B experiment
"""
import argparse
import csv
import math
import os
import sys



def pct(sorted_vals, p):
    if not sorted_vals:
        return float("nan")
    i = max(0, min(len(sorted_vals) - 1, math.ceil(p / 100 * len(sorted_vals)) - 1))
    return sorted_vals[i]


def num(s):
    return float(s) if s not in ("", None) else float("nan")


def load(path):
    if os.path.isdir(path):
        path = os.path.join(path, "frames.csv")
    with open(path, newline="", encoding="utf-8") as f:
        r = csv.reader(f)
        header = next(r)
        rows = [row for row in r]
    return header, rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("path")
    ap.add_argument("--raid", action="store_true")
    ap.add_argument("--skip", type=float, default=10.0)
    ap.add_argument("--from", dest="t0", type=float)
    ap.add_argument("--to", dest="t1", type=float)
    ap.add_argument("--bin", type=float, default=5.0)
    ap.add_argument("--experiment", help="keep only frames tagged with this experiment name (e.g. Baseline)")
    a = ap.parse_args()

    header, rows = load(a.path)
    ix = {h: i for i, h in enumerate(header)}
    # Fixed columns end at the last known fixed column; slots follow, then "ctr:" Unity counters.
    # PlayerLoop/camera slot names always contain a dot; fixed columns never do.
    slot_idx = [i for i in range(len(header)) if "." in header[i] and not header[i].startswith("ctr:")]
    ctr_idx = [i for i in range(len(header)) if header[i].startswith("ctr:")]
    slot_names = [header[i] for i in slot_idx]

    def g(row, name):
        return num(row[ix[name]])

    sel = rows
    if a.raid:
        in_raid = [r for r in rows if g(r, "alive_players") > 0 and (g(r, "pos_x") != 0 or g(r, "pos_z") != 0)]
        if not in_raid:
            sys.exit("no in-raid frames")
        start = g(in_raid[0], "time_s") + a.skip
        sel = [r for r in in_raid if g(r, "time_s") >= start]
    if a.experiment and "experiment" in ix:
        sel = [r for r in sel if r[ix["experiment"]] == a.experiment]
    if a.t0 is not None:
        sel = [r for r in sel if g(r, "time_s") >= a.t0]
    if a.t1 is not None:
        sel = [r for r in sel if g(r, "time_s") <= a.t1]
    if not sel:
        sys.exit("no frames selected")

    ft = [g(r, "frame_ms") for r in sel]
    s = sorted(ft)
    n = len(ft)
    avg = sum(ft) / n
    worst1 = s[-max(1, n // 100):]
    print(f"Window {g(sel[0], 'time_s'):.1f}-{g(sel[-1], 'time_s'):.1f} s, {n} frames")
    print(f"avg {avg:.2f} ms ({1000 / avg:.1f} fps)  p50 {pct(s, 50):.2f}  p95 {pct(s, 95):.2f}  p99 {pct(s, 99):.2f}  max {s[-1]:.2f}")
    print(f"1% low {1000 / (sum(worst1) / len(worst1)):.1f} fps   >25ms {sum(x > 25 for x in ft)}  >50ms {sum(x > 50 for x in ft)}")
    gc_frames = [r for r in sel if g(r, "gc_collections") > 0]
    print(f"GC collections {sum(g(r, 'gc_collections') for r in sel):.0f} on {len(gc_frames)} frames; gc_enabled on {sum(g(r, 'gc_enabled') > 0 for r in sel)} frames")
    print(f"mono used {g(sel[0], 'mono_used_mb'):.0f} -> {g(sel[-1], 'mono_used_mb'):.0f} MB; players avg {sum(g(r, 'alive_players') for r in sel) / n:.1f}")
    for c in ("draw_calls", "setpass_calls", "triangles_k"):
        v = sorted(x for x in (g(r, c) for r in sel) if not math.isnan(x))
        if v:
            print(f"{c:14} avg {sum(v) / len(v):9.1f}  p99 {pct(v, 99):9.1f}")

    print("\nslot                                                   avg     p50     p95     p99     max  %frame")
    stats = []
    for k, name in enumerate(slot_names):
        col = [num(r[slot_idx[k]]) for r in sel]
        col = [0.0 if math.isnan(x) else x for x in col]
        m = sum(col) / n
        if m < 0.005 and max(col) < 1:
            continue
        cs = sorted(col)
        stats.append((m, name, pct(cs, 50), pct(cs, 95), pct(cs, 99), cs[-1]))
    for m, name, p50, p95, p99, mx in sorted(stats, reverse=True)[:30]:
        print(f"{name[:52]:52} {m:7.3f} {p50:7.3f} {p95:7.3f} {p99:7.3f} {mx:7.1f} {100 * m / avg:5.1f}%")

    cams = {}
    for (m, name, p50, p95, p99, mx) in stats:
        if name.startswith("Cam."):
            cam, kind = name[4:].rsplit(".", 1)
            cams.setdefault(cam, {})[kind] = m
    if cams:
        print("\ncamera                                         cull   render    total")
        for cam, d in sorted(cams.items(), key=lambda kv: -sum(kv[1].values())):
            print(f"{cam[:44]:44} {d.get('Cull', 0):8.3f} {d.get('Render', 0):8.3f} {sum(d.values()):8.3f}")

    if ctr_idx:
        print("\nUnity counters/markers (avg, p99)")
        rows_c = []
        for i in ctr_idx:
            col = sorted(x for x in (num(r[i]) for r in sel) if not math.isnan(x))
            if col and col[-1] != 0:
                rows_c.append((header[i], sum(col) / len(col), pct(col, 99)))
        for name, m, p99 in rows_c:
            print(f"{name[:72]:72} {m:12.3f} {p99:12.3f}")

    work = [g(r, "frame_ms") - g(r, "TimeUpdate.WaitForLastPresentationAndUpdateTime") for r in sel]
    ws = sorted(work)
    print(f"\nMain-thread work excluding present wait: avg {sum(work) / n:.2f}  p50 {pct(ws, 50):.2f}  p99 {pct(ws, 99):.2f} ms")

    print(f"\nTimeline ({a.bin:.0f} s bins)")
    print("   t_s  frames  avg_ms  p99_ms  max_ms  wait_ms  players  gc_on  mono_mb  draws  top slot")
    bins = {}
    for r in sel:
        bins.setdefault(int(g(r, "time_s") // a.bin), []).append(r)
    for b in sorted(bins):
        br = bins[b]
        f = sorted(g(r, "frame_ms") for r in br)
        totals = [0.0] * len(slot_names)
        for r in br:
            for k in range(len(slot_names)):
                v = num(r[slot_idx[k]])
                if not math.isnan(v):
                    totals[k] += v
        cand = [(t, slot_names[k]) for k, t in enumerate(totals) if slot_names[k] != "TimeUpdate.WaitForLastPresentationAndUpdateTime"]
        top = max(cand)
        wait = sum(g(r, "TimeUpdate.WaitForLastPresentationAndUpdateTime") for r in br) / len(br)
        print(f"{b * a.bin:6.0f} {len(br):7d} {sum(f) / len(f):7.2f} {pct(f, 99):7.2f} {f[-1]:7.1f} {wait:8.2f} {g(br[-1], 'alive_players'):8.0f} {g(br[-1], 'gc_enabled'):6.0f} {g(br[-1], 'mono_used_mb'):8.0f} {g(br[-1], 'draw_calls'):6.0f}  {top[1]} {top[0] / len(br):.2f}")


if __name__ == "__main__":
    main()
