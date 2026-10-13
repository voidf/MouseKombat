"""v10 campaign: staged self-play, evaluated at every gate, best checkpoint exported.

Why staged instead of one long learn() call: a 2-day run that goes wrong at hour 3 should not cost
40 hours. Each stage trains, then is scored against FIXED external baselines (never the training
pool, which the learner co-evolves with). The winner across all gates is what gets exported — not
whatever the last stage happened to leave behind, which is how a self-play run can end on a cycling
downturn.

Usage:  python rl/campaign_v10.py [--hours 40] [--dry]
"""
import argparse
import glob
import json
import os
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, ".."))
LOGS = os.path.join(HERE, "logs")
CKPT = os.path.join(HERE, "checkpoints")
INIT = os.path.join(CKPT, "ppo_selfplay_v9.zip")   # warm start: the recipe that beats scratch play

# Stage plan. `steps` are in millions. Weights on the scripted teachers, the rest of the opponent
# draw is the self-play pool (--pool-prob). Stage A hammers the exact exploit that farmed v9, then
# the mix broadens and the pool share grows so the policy keeps facing its own newest ideas.
STAGES = [
    # Focused zoning first, broad mix later. Stage A already produced a policy that beats every
    # melee baseline 20-0-0; what it still loses is the fireball game, so the middle stages spend
    # their steps there rather than diluting it.
    dict(name="b_zoning",  msteps=60,  pool_prob=0.30, lr=2e-4, ent=0.015,
         mix="zoner=0.30,spammer=0.25,blocker=0.10,jumper=0.05,rusher=0.05,statemachine=0.05",
         why="~55% of episodes against fireballs, with the corrected demonstrators in the mix"),
    dict(name="c_zoning2", msteps=100, pool_prob=0.40, lr=1.5e-4, ent=0.013,
         mix="zoner=0.25,spammer=0.20,blocker=0.08,jumper=0.04,rusher=0.06,statemachine=0.05",
         why="still zoning-heavy, self-play share rising"),
    dict(name="d_half1",   msteps=150, pool_prob=0.55, lr=1.2e-4, ent=0.012,
         mix="zoner=0.18,spammer=0.12,blocker=0.08,jumper=0.04,rusher=0.08,statemachine=0.05",
         why="broaden while keeping a real fireball floor"),
    dict(name="d_half2",   msteps=200, pool_prob=0.65, lr=1e-4, ent=0.010,
         mix="zoner=0.14,spammer=0.10,blocker=0.06,jumper=0.03,rusher=0.07,statemachine=0.05",
         why="long haul"),
    dict(name="d_half3",   msteps=240, pool_prob=0.70, lr=8e-5, ent=0.010,
         mix="zoner=0.12,spammer=0.08,blocker=0.05,jumper=0.02,rusher=0.06,statemachine=0.04",
         why="longest stage; the zoner+blocker floor keeps fireball defence from decaying"),
]
EVAL_OPPONENTS = "statemachine,rusher,blocker,jumper,zoner,spammer"
WEIGHTS = {"statemachine": 1.0, "rusher": 1.0, "blocker": 1.5, "jumper": 1.0,
           "zoner": 2.0, "spammer": 2.0}


def log(msg, fh=None):
    line = f"[campaign] {msg}"
    print(line, flush=True)
    if fh:
        fh.write(line + "\n")
        fh.flush()


def run(cmd, out_path, label):
    log(f"RUN {label}: {' '.join(cmd)}")
    with open(out_path, "w", encoding="utf-8", errors="replace") as f:
        p = subprocess.run(cmd, cwd=REPO, stdout=f, stderr=subprocess.STDOUT)
    return p.returncode


def tail(path, n=6):
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            lines = [l.rstrip() for l in f if l.strip()]
        return " | ".join(lines[-n:])
    except OSError:
        return ""


def score(res):
    num = den = 0.0
    for opp, row in res.items():
        w = WEIGHTS.get(opp, 1.0)
        num += w * (row["wins"] - row["losses"])
        den += w * row["games"]
    return 0.5 * (1.0 + (num / den if den else 0.0))   # 0..1, 0.5 = even


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--games", type=int, default=20, help="eval games per opponent per gate")
    ap.add_argument("--stages", default="", help="comma-separated stage names to run (default all)")
    ap.add_argument("--init", default="", help="warm-start checkpoint for the FIRST stage "
                                               "(default: the v9 baseline)")
    ap.add_argument("--dry", action="store_true")
    a = ap.parse_args()

    os.makedirs(LOGS, exist_ok=True)
    want = [s for s in a.stages.split(",") if s] or None
    stages = [s for s in STAGES if not want or s["name"] in want]

    leaderboard = []
    lb_path = os.path.join(LOGS, "leaderboard.json")
    if os.path.exists(lb_path):
        leaderboard = json.load(open(lb_path, encoding="utf-8"))

    with open(os.path.join(LOGS, "campaign.log"), "a", encoding="utf-8") as fh:
        log(f"=== campaign start: {[s['name'] for s in stages]} dry={a.dry}", fh)
        init = a.init or INIT
        for st in stages:
            out_name = f"ppo_v10_{st['name']}"
            out_ckpt = os.path.join(CKPT, out_name + ".zip")
            tlog = os.path.join(LOGS, f"train_{st['name']}.log")
            cmd = [sys.executable, os.path.join(HERE, "train_v10.py"),
                   "--steps", str(int(st["msteps"] * 1_000_000)),
                   "--out", out_name,
                   "--pool-prob", str(st["pool_prob"]),
                   "--mix", st["mix"],
                   "--lr", str(st["lr"]),
                   "--ent", str(st["ent"])]
            if init and os.path.exists(init):
                cmd += ["--init", init]
            log(f"--- stage {st['name']} ({st['msteps']}M steps): {st['why']}", fh)
            if a.dry:
                log(f"    would run: {' '.join(cmd)}", fh)
            else:
                t0 = time.time()
                rc = run(cmd, tlog, st["name"])
                log(f"    stage {st['name']} rc={rc} in {(time.time() - t0) / 3600:.2f}h", fh)
                log(f"    tail: {tail(tlog, 3)}", fh)
                if rc != 0 or not os.path.exists(out_ckpt):
                    log(f"    STAGE FAILED/NO OUTPUT — keeping previous checkpoint {init}", fh)
                    continue

            # ---- gate: score against fixed external baselines ----
            ev_json = os.path.join(LOGS, f"eval_{st['name']}.json")
            ev_log = os.path.join(LOGS, f"eval_{st['name']}.log")
            rc = run([sys.executable, os.path.join(HERE, "eval_suite.py"), out_ckpt,
                      str(a.games), "--chars", "hero", "--opp", EVAL_OPPONENTS,
                      "--json", ev_json], ev_log, f"eval {st['name']}")
            if rc == 0 and os.path.exists(ev_json):
                res = json.load(open(ev_json, encoding="utf-8"))["results"]
                sc = score(res)
                log(f"    gate {st['name']}: score={sc:.3f}  " + "  ".join(
                    f"{o}={r['wins']}-{r['losses']}-{r['draws']}" for o, r in res.items()), fh)
                log(f"    {tail(ev_log, 2)}", fh)
                leaderboard.append(dict(stage=st["name"], ckpt=out_ckpt, score=sc,
                                        steps=st["msteps"], results=res))
                with open(lb_path, "w", encoding="utf-8") as f:
                    json.dump(leaderboard, f, indent=1)
            else:
                log(f"    gate {st['name']} FAILED (rc={rc}) — checkpoint not scored", fh)

            init = out_ckpt    # next stage continues from this one

        # ---- pick the best scored checkpoint, export it as the v10 model ----
        if leaderboard:
            best = max(leaderboard, key=lambda e: e["score"])
            log(f"=== BEST: {best['stage']} score={best['score']:.3f} -> {best['ckpt']}", fh)
            final = os.path.join(CKPT, "ppo_selfplay_v10.zip")
            if not a.dry:
                shutil.copy(best["ckpt"], final)
                rc = run([sys.executable, os.path.join(HERE, "export_onnx.py"),
                          "ppo_selfplay_v10", "ppo_selfplay_v10"],
                         os.path.join(LOGS, "export.log"), "export onnx")
                log(f"    export rc={rc}: {tail(os.path.join(LOGS, 'export.log'), 3)}", fh)
                # head-to-head against the model this replaces, on the game's own tables
                rc = run([sys.executable, os.path.join(HERE, "eval_suite.py"), final,
                          str(a.games), "--chars", "hero", "--opp", INIT,
                          "--json", os.path.join(LOGS, "eval_vs_v9.json")],
                         os.path.join(LOGS, "eval_vs_v9.log"), "v10 vs v9")
                log(f"    v10 vs v9: {tail(os.path.join(LOGS, 'eval_vs_v9.log'), 2)}", fh)
        else:
            log("=== no scored checkpoint; nothing exported", fh)
        log("=== campaign done", fh)


if __name__ == "__main__":
    main()
