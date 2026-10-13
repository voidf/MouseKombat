"""Rank every checkpoint that has a gate/eval JSON on disk, with the campaign's own scoring.

Why this exists: the campaign exports the best checkpoint IT scored, but a mid-stage snapshot can
beat every stage output (stage b at ~10M steps already reached 20-0-0 across all melee baselines
AND 10-0-10 against both fireball baselines, which no stage output had managed). Picking the final
deliverable is a decision worth looking at directly rather than trusting one automated rule.

Usage:  python rl/rank_candidates.py
"""
import glob
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from campaign_v10 import WEIGHTS, score   # noqa: E402


def row(path):
    try:
        d = json.load(open(path, encoding="utf-8"))
    except (OSError, ValueError):
        return None
    res = d.get("results")
    if not res:
        return None
    return dict(path=os.path.basename(path), spec=d.get("spec", "?"), score=score(res), results=res)


def main():
    cands = [r for r in (row(p) for p in sorted(glob.glob(os.path.join(HERE, "logs", "eval_*.json"))))
             if r]
    # the leaderboard is authoritative for stage outputs (it carries the checkpoint path too)
    seen = {os.path.basename(c["spec"]) for c in cands}
    lb = os.path.join(HERE, "logs", "leaderboard.json")
    if os.path.exists(lb):
        for e in json.load(open(lb, encoding="utf-8")):
            if os.path.basename(e["ckpt"]) in seen:
                continue          # already listed through its own eval_*.json
            cands.append(dict(path=e["stage"] + ".zip", spec=e["ckpt"], score=e["score"],
                              results=e["results"]))

    # one line per CHECKPOINT: the same model gets evaluated more than once (a quick probe, then a
    # full gate, then the shipping run), and the extra rows would just be noise in the ranking.
    best_by_file = {}
    for c in cands:
        key = os.path.basename(c["spec"] if c["spec"].endswith(".zip") else c["spec"] + ".zip")
        if key not in best_by_file or c["score"] > best_by_file[key]["score"]:
            best_by_file[key] = c
    cands = list(best_by_file.values())
    if not cands:
        print("no eval JSONs yet")
        return 1

    cands.sort(key=lambda c: -c["score"])
    opps = [o for o in WEIGHTS if any(o in c["results"] for c in cands)]
    print(f"{'rank':<5}{'score':>7}  {'checkpoint':<34}{'chars':>6}  " +
          "  ".join(f"{o[:9]:>10}" for o in opps))
    for i, c in enumerate(cands, 1):
        cells = []
        for o in opps:
            r = c["results"].get(o)
            cells.append(f"{r['wins']}-{r['losses']}-{r['draws']}" if r else "-")
        print(f"{i:<5}{c['score']:>7.3f}  {c['spec'][:34]:<34}{'hero':>6}  " +
              "  ".join(f"{x:>10}" for x in cells))
    print("\nscore = 0.5 * (1 + weighted (W-L)/games); weights " +
          ", ".join(f"{k}={v}" for k, v in WEIGHTS.items()))
    best = cands[0]
    print(f"\nBEST: {best['spec']}  score={best['score']:.3f}  (from {best['path']})")
    with open(os.path.join(HERE, "logs", "ranking.json"), "w", encoding="utf-8") as f:
        json.dump(cands, f, indent=1)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
