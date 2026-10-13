"""Score the self-play POOL snapshots against the fixed external baselines.

Why this exists: the campaign only ever scores its STAGE outputs. The pool it builds along the way
holds ~80 mid-stage snapshots that nobody ever evaluated against the fixed baselines — and self-play
is non-transitive, so "the newest model" is not "the best model". Measured example: the current best
candidate goes 15-18-6 against its own mid-run snapshots and loses 0-6 to one of them. A snapshot in
there can therefore outscore everything the campaign shipped.

Results are written as logs/eval_pool_<snap>.json, i.e. the same shape rank_candidates.py already
reads — so a screened snapshot automatically becomes shippable via ship_v10.py.

Usage:  python rl/screen_pool.py [--count 8] [--games 8] [--newest]
"""
import argparse
import glob
import json
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from campaign_v10 import WEIGHTS, score      # noqa: E402

CKPT = os.path.join(HERE, "checkpoints")
POOL = os.path.join(CKPT, "pool")
LOGS = os.path.join(HERE, "logs")
OPPONENTS = "statemachine,rusher,blocker,jumper,zoner,spammer"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--count", type=int, default=8, help="how many snapshots to screen")
    ap.add_argument("--games", type=int, default=8, help="games per opponent (screening)")
    ap.add_argument("--newest", action="store_true", help="screen the newest instead of a spread")
    ap.add_argument("--rescue-above", type=float, default=0.9,
                    help="copy a snapshot scoring at least this out of the (pruned) pool")
    a = ap.parse_args()

    files = sorted(f for f in glob.glob(os.path.join(POOL, "*.zip"))
                   if "anchor" not in os.path.basename(f))
    if not files:
        print("pool is empty")
        return 1
    if a.newest:
        picks = files[-a.count:]
    else:                       # a spread across the whole run finds older peaks, not just the tail
        step = max(1, len(files) // a.count)
        picks = files[::step][:a.count]

    print(f"screening {len(picks)} of {len(files)} snapshots, {a.games} games/opponent")
    rows = []
    for f in picks:
        name = os.path.basename(f)[:-4]
        out = os.path.join(LOGS, f"eval_pool_{name}.json")
        subprocess.run([sys.executable, os.path.join(HERE, "eval_suite.py"), f, str(a.games),
                        "--chars", "hero", "--opp", OPPONENTS, "--json", out], cwd=HERE,
                       stdout=open(os.path.join(LOGS, f"screen_{name}.log"), "w",
                                   encoding="utf-8", errors="replace"),
                       stderr=subprocess.STDOUT)
        if not os.path.exists(out):
            print(f"  {name}: eval failed (see logs/screen_{name}.log)")
            continue
        res = json.load(open(out, encoding="utf-8"))["results"]
        s = score(res)
        rows.append((s, name, res))
        print(f"  {name}: score={s:.3f}  " + "  ".join(
            f"{o}={r['wins']}-{r['losses']}-{r['draws']}" for o, r in list(res.items())[-2:]))

        # Rescue anything promising OUT of the pool immediately. The pool keeps only the newest 80
        # snapshots, so a peak can be pruned away between being measured and being shipped — that
        # nearly happened to the 0.982 snapshot found by hand. Rewriting the eval JSON's spec makes
        # rank_candidates/ship_v10 point at the stable copy instead of the perishable pool file.
        if s >= a.rescue_above:
            keep = os.path.join(CKPT, f"ppo_v10_pool_{name.split('_')[-1]}.zip")
            if not os.path.exists(keep):
                shutil.copy(f, keep)
            d = json.load(open(out, encoding="utf-8"))
            d["spec"] = keep
            json.dump(d, open(out, "w", encoding="utf-8"), indent=1)
            print(f"      -> rescued to {os.path.basename(keep)}")

    rows.sort(reverse=True)
    print("\n=== rank (screen only; confirm the winner with 20 games before shipping) ===")
    for s, name, _ in rows:
        print(f"  {s:.3f}  {name}")
    if rows:
        print(f"\nbest: {rows[0][1]} (score {rows[0][0]:.3f})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

