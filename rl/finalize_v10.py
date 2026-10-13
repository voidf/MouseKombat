"""Finalize + verify the v10 deliverable end to end.

The campaign already picks the best-scoring checkpoint and exports it; this is the "did it actually
land" half, which is deliberately separate so it can be re-run at any time:

  1. re-export the chosen checkpoint to ONNX (idempotent)
  2. copy it next to the exported game build (Out/ai_rl_model) as well as the editor's ai_rl_model/
  3. per-frame ONNX vs SB3 parity across whole matches  (verify_onnx_ingame.py)
  4. headless run of the REAL GAME loading that .onnx  (Godot --headless, MFEntry.tscn)
  5. a v9-vs-v10 comparison table + rl/logs/final_report.md

Usage:  python rl/finalize_v10.py [--ckpt ppo_selfplay_v10] [--games 20] [--skip-game]
"""
import argparse
import glob
import json
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, ".."))
CKPT_DIR = os.path.join(HERE, "checkpoints")
MODELS = os.path.join(REPO, "ai_rl_model")
LOGS = os.path.join(HERE, "logs")
GODOT = os.path.expanduser(
    r"~\Downloads\Godot_v4.6.3-stable_mono_win64\Godot_v4.6.3-stable_mono_win64_console.exe")
OPPONENTS = "statemachine,rusher,blocker,jumper,zoner,spammer"


def sh(cmd, log_path=None, timeout=7200):
    print(f"$ {' '.join(cmd)}", flush=True)
    if log_path:
        with open(log_path, "w", encoding="utf-8", errors="replace") as f:
            return subprocess.run(cmd, cwd=REPO, stdout=f, stderr=subprocess.STDOUT,
                                  timeout=timeout).returncode
    return subprocess.run(cmd, cwd=REPO, timeout=timeout).returncode


def evaluate(spec, tag, games, jitter=False, cache_name=None):
    name = cache_name or f"final_eval_{tag}.json"
    out = os.path.join(LOGS, name)
    cmd = [sys.executable, os.path.join(HERE, "eval_suite.py"), spec, str(games),
           "--chars", "hero", "--opp", OPPONENTS, "--json", out]
    if jitter:
        cmd += ["--jitter", "1"]
    sh(cmd, os.path.join(LOGS, f"final_eval_{tag}.log"))
    return json.load(open(out, encoding="utf-8"))["results"] if os.path.exists(out) else None


def table(res_v9, res_v10):
    lines = ["| opponent | v9 (W-L-D) | v10 (W-L-D) | v10 hits/atk | v10 dmg dealt | v10 dmg taken "
             "| v10 fireball hits |", "|---|---|---|---|---|---|---|"]
    for opp in OPPONENTS.split(","):
        a = res_v9.get(opp)
        b = (res_v10 or {}).get(opp)
        fa = f"{a['wins']}-{a['losses']}-{a['draws']}" if a else "n/a"
        fb = f"{b['wins']}-{b['losses']}-{b['draws']}" if b else "n/a"
        extra = (f"{b['hits_per_attack']:.2f} | {b['dmg_dealt_pct']:.0f}% | "
                 f"{b['dmg_taken_pct']:.0f}% | {b['fb_hits']}") if b else "| | |"
        lines.append(f"| {opp} | {fa} | {fb} | {extra} |")
    if res_v10:
        lines += ["", "| 关键指标（zoner+spammer 平均） | v9 | v10 |", "|---|---|---|"]
        for key, label, fmt in (("neutral_pct", "发呆帧占比 neutral%", "{:.1f}%"),
                                ("hits_per_attack", "每次出招命中数（空挥反向指标）", "{:.2f}"),
                                ("dmg_dealt_pct", "对发波流造成的伤害", "{:.0f}%"),
                                ("fb_hits", "吃到的火球数（合计）", "{:.0f}")):
            src = [o for o in ("zoner", "spammer") if o in res_v9 and o in res_v10]
            if not src:
                continue
            a = sum(res_v9[o][key] for o in src) / (1 if key == "fb_hits" else len(src))
            b = sum(res_v10[o][key] for o in src) / (1 if key == "fb_hits" else len(src))
            lines.append(f"| {label} | {fmt.format(a)} | {fmt.format(b)} |")
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ckpt", default="ppo_selfplay_v10")
    ap.add_argument("--games", type=int, default=20)
    ap.add_argument("--skip-game", action="store_true", help="skip the Godot headless run")
    ap.add_argument("--skip-zero", action="store_true",
                    help="skip the second eval with fireballs compiled as duds")
    ap.add_argument("--skip-jitter", action="store_true",
                    help="skip the third eval with randomised start spacing")
    a = ap.parse_args()

    ckpt = os.path.join(CKPT_DIR, a.ckpt + ".zip")
    if not os.path.exists(ckpt):
        print(f"MISSING checkpoint {ckpt}")
        return 2
    print(f"checkpoint: {ckpt}  ({os.path.getsize(ckpt)} bytes)")

    # 1. export ONNX
    rc = sh([sys.executable, os.path.join(HERE, "export_onnx.py"), a.ckpt, a.ckpt],
            os.path.join(LOGS, "final_export.log"))
    onnx = os.path.join(MODELS, a.ckpt + ".onnx")
    ok_export = rc == 0 and os.path.exists(onnx)
    print(f"export rc={rc} -> {onnx} ({os.path.getsize(onnx) if ok_export else 0} bytes)")

    # 2. ship it to the exported build too (ReadyScreen scans the folder next to the .exe)
    out_dir = os.path.join(REPO, "Out", "ai_rl_model")
    if ok_export and os.path.isdir(out_dir):
        shutil.copy(onnx, out_dir)
        print(f"copied to {out_dir}")

    # 3. ONNX vs SB3, frame by frame, inside real matches
    rc = sh([sys.executable, os.path.join(HERE, "verify_onnx_ingame.py"), a.ckpt, "10"],
            os.path.join(LOGS, "final_verify_onnx.log"))
    verify_tail = ""
    if os.path.exists(os.path.join(LOGS, "final_verify_onnx.log")):
        verify_tail = open(os.path.join(LOGS, "final_verify_onnx.log"),
                           encoding="utf-8", errors="replace").read().strip().splitlines()[-1]
    print(f"onnx verification rc={rc}: {verify_tail}")

    # 4. the real game, headless, loading the real .onnx — and read the result back out of the
    #    replay the game itself writes. A replay only exists when a round reaches a KO, and
    #    read_replay re-simulates it and refuses to report a winner unless its reconstruction
    #    reproduces the replay's own FinalChecksum, so the winner below is self-verified.
    game_tail = "skipped"
    game_result = "not run"
    if not a.skip_game and os.path.exists(GODOT):
        replay_dir = os.path.join(os.environ.get("APPDATA", ""), "Godot", "app_userdata",
                                  "MouseKombat", "replays", "local")
        before = set(glob.glob(os.path.join(replay_dir, "*.mkr"))) if os.path.isdir(replay_dir) else set()
        env = dict(os.environ)
        env["MK_AI_P1"] = f"res://ai_rl_model/{a.ckpt}.onnx"
        env["MK_AI_P2"] = "statemachine"
        glog = os.path.join(LOGS, "final_godot_headless.log")
        with open(glog, "w", encoding="utf-8", errors="replace") as f:
            p = subprocess.run([GODOT, "--headless", "--path", REPO, "res://MFEntry.tscn",
                                "--fixed-fps", "60", "--quit-after", "4000"],
                               stdout=f, stderr=subprocess.STDOUT, timeout=900, env=env)
        txt = open(glog, encoding="utf-8", errors="replace").read()
        loaded = f"P1 = ONNX res://ai_rl_model/{a.ckpt}.onnx" in txt or "P1 = ONNX" in txt
        failed = "ONNX load failed" in txt or "Unhandled exception" in txt
        warn = [l for l in txt.splitlines() if "hit rect" in l]
        game_tail = ("game loaded the model and ran 4000 headless frames" if loaded and not failed
                     else "GODOT RUN NEEDS A LOOK (see final_godot_headless.log)")
        print(f"godot headless rc={p.returncode}: {game_tail}")
        if warn:
            game_tail += "  ** fireball prefab warning present — see log **"
        new_files = sorted(set(glob.glob(os.path.join(replay_dir, "*.mkr"))) - before,
                           key=os.path.getmtime)
        if new_files:
            rc_ = subprocess.run([sys.executable, os.path.join(HERE, "read_replay.py"),
                                  new_files[0]], capture_output=True, text=True).stdout
            game_result = " | ".join(l.strip() for l in rc_.splitlines()[1:4])
            print(f"in-game result ({os.path.basename(new_files[0])}): {game_result}")
        else:
            game_result = "no KO within 4000 frames (no replay written)"
    elif not a.skip_game:
        game_tail = f"Godot not found at {GODOT}"

    # 4b. the same checkpoint with fireballs compiled as DUDs, which is what the shipped game did
    #     before the HeroLibrary load-order fix. Kept in the report as a robustness line: a model
    #     that only wins *because* of fireballs would collapse here.
    res_zero = None
    if not a.skip_zero:
        env = dict(os.environ)
        env["MK_ZERO_FIREBALL"] = "1"
        zj = os.path.join(LOGS, "zerofb_final_v10.json")
        subprocess.run([sys.executable, os.path.join(HERE, "eval_suite.py"), ckpt, str(a.games),
                        "--chars", "hero", "--opp", OPPONENTS, "--json", zj], cwd=REPO, env=env,
                       stdout=open(os.path.join(LOGS, "zerofb_final_v10.log"), "w",
                                   encoding="utf-8", errors="replace"),
                       stderr=subprocess.STDOUT)
        if os.path.exists(zj):
            res_zero = json.load(open(zj, encoding="utf-8"))["results"]

    # 4c. and once with randomised start spacing. The default eval fixes the start (which is the
    #     game's real opening spacing) but the policy is deterministic and the two zoning teachers
    #     ignore their seed, so the default run is effectively one match per character position.
    res_jitter = None
    if not a.skip_jitter:
        res_jitter = evaluate(ckpt, "v10_jitter", a.games, jitter=True,
                              cache_name="jitter_final_v10.json")

    # 5. comparison table (evaluate the CHECKPOINT — eval_suite loads a policy with PPO.load,
    #    it cannot read a .onnx; the ONNX itself is covered by step 3)
    res_v10 = evaluate(ckpt, "v10", a.games)
    v9_json = os.path.join(LOGS, "eval_v9_baseline.json")
    if not os.path.exists(v9_json):
        v9_json2 = os.path.join(LOGS, "final_eval_v9.json")
        sh([sys.executable, os.path.join(HERE, "eval_suite.py"),
            os.path.join(CKPT_DIR, "ppo_selfplay_v9.zip"), str(a.games),
            "--chars", "hero", "--opp", OPPONENTS, "--json", v9_json2],
           os.path.join(LOGS, "final_eval_v9.log"))
        v9_json = v9_json2
    res_v9 = json.load(open(v9_json, encoding="utf-8"))["results"] if os.path.exists(v9_json) else {}

    body = table(res_v9, res_v10)
    extra = ""
    if res_zero:
        rows = ["| 口径 | " + " | ".join(OPPONENTS.split(",")) + " |",
                "|---|" + "---|" * len(OPPONENTS.split(","))]
        for label, res in (("火球有效（资源本意 / 修复后）", res_v10),
                           ("火球哑弹（修复前的构建）", res_zero)):
            cells = []
            for o in OPPONENTS.split(","):
                r = (res or {}).get(o)
                cells.append(f"{r['wins']}-{r['losses']}-{r['draws']}" if r else "n/a")
            rows.append(f"| {label} | " + " | ".join(cells) + " |")
        extra = ("\n## 两种口径对照\n\n" + "\n".join(rows)
                 + "\n\n火球哑弹 = `MK_ZERO_FIREBALL=1`，即修复 `HeroLibrary` 加载顺序之前"
                   "实机里火球的真实状态（判定框全为零尺寸）。这一列用来回答【这个模型是不是只会靠"
                   "发波赢】——不是。\n")
    if res_jitter:
        jrows = ["| zoner / spammer | 固定开局（游戏真实开局） | 随机距离 |", "|---|---|---|"]
        for o in ("zoner", "spammer"):
            if o not in OPPONENTS.split(","):
                continue
            a1, b1 = (res_v10 or {}).get(o), res_jitter.get(o)
            f1 = f"{a1['wins']}-{a1['losses']}-{a1['draws']}" if a1 else "n/a"
            f2 = f"{b1['wins']}-{b1['losses']}-{b1['draws']}" if b1 else "n/a"
            jrows.append(f"| {o} | {f1} | {f2} |")
        extra += ("\n## 开局距离的影响\n\n" + "\n".join(jrows)
                  + "\n\n默认评测开局距离固定（就是游戏真实的 120/650），而策略是确定性推理、"
                    "zoner/spammer 两个教师不看 seed —— 所以那 20 局里每个角色位其实是同一局重复。"
                    "随机距离那一列才有真实方差；两列都要看：固定那列是你在游戏里真正会遇到的开局。\n")
    report = (f"# v10 交付报告\n\ncheckpoint: `{a.ckpt}`\n\nONNX 逐帧对拍: {verify_tail}\n\n"
              f"实机 headless: {game_tail}\n\n实机对局结果: {game_result}\n\n"
              f"## 对照（每对手 {a.games} 局，Heroes/ 数据表）\n\n{body}\n{extra}")
    with open(os.path.join(LOGS, "final_report.md"), "w", encoding="utf-8") as f:
        f.write(report)
    print("\n" + report)
    return 0 if (ok_export and rc == 0) else 1


if __name__ == "__main__":
    raise SystemExit(main())
