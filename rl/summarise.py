"""Summarise every candidate that has an eval JSON: score, activity, and per-opponent record.

Two candidates can both score 1.000 while differing enormously in how they play — d_half2 wins
20-0-0 across the board but spends 23% of its actionable frames doing NOTHING, while b_zoning does the
same 20-0-0 at 3.3%. Since "the AI stands still" was the original complaint, the passive one is the
worse deliverable even with an identical score, so activity is shown right next to the score.

Usage:  python rl/summarise.py [top]
"""
import glob
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from campaign_v10 import score      # noqa: E402

OPPS = ["statemachine", "rusher", "blocker", "jumper", "zoner", "spammer"]


def main():
    top = int(sys.argv[1]) if len(sys.argv) > 1 else 12
    rows = []
    for f in sorted(glob.glob(os.path.join(HERE, "logs", "eval_*.json"))):
        try:
            d = json.load(open(f, encoding="utf-8"))
        except (OSError, ValueError):
            continue
        res = d.get("results")
        if not res:
            continue
        spec = os.path.basename(str(d.get("spec", "?")))
        neut = sum(v["neutral_pct"] for v in res.values()) / len(res)
        hpa = sum(v["hits_per_attack"] for v in res.values()) / len(res)
        rows.append((score(res), neut, hpa, spec, res))
    rows.sort(key=lambda r: (-r[0], r[1]))
    print(f"{'score':>6} {'neutral%':>9} {'hits/atk':>9}  {'checkpoint':<40} " +
          " ".join(f"{o[:6]:>7}" for o in OPPS))
    for s, neut, hpa, spec, res in rows[:top]:
        cells = []
        for o in OPPS:
            v = res.get(o)
            cells.append(f"{v['wins']}-{v['losses']}-{v['draws']}" if v else "-")
        print(f"{s:>6.3f} {neut:>8.1f}% {hpa:>9.2f}  {spec[:40]:<40} " +
              " ".join(f"{c:>7}" for c in cells))
    print("\n排序: 分数降序，同分时按 发呆% 升序（更主动的排前面）")


if __name__ == "__main__":
    main()
