"""Ship the best-ranked checkpoint as the v10 deliverable.

The campaign exports the best of its own STAGE outputs, but a mid-stage snapshot can outrank every
stage output (that is exactly what happened: stage B at ~10M steps reached 20-0-0 across all four
melee baselines AND 10-0-10 against both fireball baselines, which stage A could not). So the final
choice is made from `rank_candidates.py`'s ranking, not from the campaign's own rule.

Steps: refresh the ranking -> resolve the top candidate's checkpoint file -> copy it to
checkpoints/ppo_selfplay_v10.zip -> run finalize_v10.py (export, out/ copy, ONNX==SB3 frame check,
real-game headless boot, v9-vs-v10 report).

Usage:  python rl/ship_v10.py [--games 20] [--top 1] [--dry]
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
CKPT = os.path.join(HERE, "checkpoints")
LOGS = os.path.join(HERE, "logs")


def resolve_checkpoint(spec):
    """The ranking may carry a path from before a file was renamed; fall back to the basename."""
    if not spec:
        return None
    if spec.endswith(".zip") and os.path.exists(spec):
        return spec
    base = os.path.basename(spec if spec.endswith(".zip") else spec + ".zip")
    cand = os.path.join(CKPT, base)
    if os.path.exists(cand):
        return cand
    hits = glob.glob(os.path.join(CKPT, "**", base), recursive=True)
    return hits[0] if hits else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--games", type=int, default=20)
    ap.add_argument("--top", type=int, default=1, help="ship the Nth best candidate")
    ap.add_argument("--min-score", type=float, default=0.5,
                    help="refuse to ship below this weighted score")
    ap.add_argument("--dry", action="store_true")
    a = ap.parse_args()

    print("$ rank_candidates.py")
    subprocess.run([sys.executable, os.path.join(HERE, "rank_candidates.py")], cwd=HERE, check=True)
    ranking = json.load(open(os.path.join(LOGS, "ranking.json"), encoding="utf-8"))
    if len(ranking) < a.top:
        print(f"only {len(ranking)} candidate(s) ranked")
        return 2

    pick = ranking[a.top - 1]
    ckpt = resolve_checkpoint(pick["spec"])
    print(f"\nchosen: {pick['spec']}  score={pick['score']:.3f}")
    if not ckpt:
        print(f"!! cannot resolve a checkpoint file for {pick['spec']}")
        return 2
    if pick["score"] < a.min_score:
        print(f"!! score {pick['score']:.3f} is below the floor {a.min_score} — not shipping")
        return 3
    print(f"file:   {ckpt}  ({os.path.getsize(ckpt)} bytes)")
    print("record: " + "  ".join(f"{o}={r['wins']}-{r['losses']}-{r['draws']}"
                                 for o, r in pick["results"].items()))
    if a.dry:
        return 0

    final = os.path.join(CKPT, "ppo_selfplay_v10.zip")
    shutil.copy(ckpt, final)
    print(f"copied -> {final}")
    rc = subprocess.run([sys.executable, os.path.join(HERE, "finalize_v10.py"),
                         "--ckpt", "ppo_selfplay_v10", "--games", str(a.games)], cwd=HERE).returncode
    print(f"finalize rc={rc}  (report: rl/logs/final_report.md)")
    return rc


if __name__ == "__main__":
    raise SystemExit(main())
