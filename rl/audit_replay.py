"""Audit a REAL PLAYER's replay against an AI: where the AI whiffs, and what it does about
knockdown pressure.

Written because score-based eval and a human disagree. eval_suite scores a model against SCRIPTED
opponents and reported a perfect 20-0-0 across the board for models that a human then beat while
describing them as passive and unable to deal with one specific strategy. Scripted teachers never do
what a person does — pick one long-startup knockdown and loop it into wakeup pressure — so a model
can be excellent on that metric and useless against the person holding the pad.

Reads the same .mkr files (inputs only, re-simulated exactly) and reports, per seat:

  * every attack attempt, split into hit / blocked / WHIFF, with each whiff broken down by what the
    opponent was doing and how far away it was (the "shadow boxing at nothing" signature)
  * what the AI did on its first actionable frame after being knocked down, and whether the human's
    follow-up hit it — i.e. whether it can block and counter wakeup pressure at all
  * who stood still while nothing happened

Attribution: an attempt counts as landing when the opponent loses HP or enters blockstun while the
attempt is in flight. Damage that arrives with no attempt in flight is credited to "(projectile)"
(and to "(throw)" when the attacker was in a throw's follow-through).

Usage:  python rl/audit_replay.py <file.mkr> [...]      (no args = the newest replays)
"""
import glob
import os
import sys
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from read_replay import parse, unpack                       # noqa: E402
from mk_env import _ensure_runtime, make_config              # noqa: E402
_ensure_runtime()
from MouseKombat.Sim import GameSim, PlayerState, CharacterId     # noqa: E402

FOLDERS = {int(CharacterId.Hamster): "Hamster", int(CharacterId.Kangaroo): "Kangaroo",
           int(CharacterId.Squirrel): "Squirrel"}
NEUTRAL = {int(PlayerState.Idle), int(PlayerState.Walk), int(PlayerState.Crouch),
           int(PlayerState.CrouchExit)}
# how long after a move STARTS its damage may still be credited to it (throws land on release)
CREDIT_WINDOW = 40


class Attempt:
    __slots__ = ("id", "start", "end", "contact", "whiff_ctx")

    def __init__(self, id_, start):
        self.id, self.start, self.end = id_, start, None
        self.contact = False
        self.whiff_ctx = None


def phase_of(p):
    if p.State != PlayerState.Attack or p.CurrentMove is None:
        return "-"
    if p.IsInStartup:
        return "startup"
    if p.IsInRecovery:
        return "recovery"
    return "active"


def state_bucket(st):
    st = int(st)
    if st in NEUTRAL:
        return "idle/walk"
    if st == int(PlayerState.Attack):
        return "attack"
    if st in (int(PlayerState.Downed), int(PlayerState.Wakeup)):
        return "downed/wakeup"
    if st in (int(PlayerState.Hurt), int(PlayerState.Juggle), int(PlayerState.AirHurt)):
        return "hitstun"
    if st == int(PlayerState.Jump):
        return "jump"
    if st in (int(PlayerState.DefenseHit),):
        return "blockstun"
    return "other"


def dist_bucket(d):
    return "<120" if d < 120 else "120-200" if d < 200 else "200-320" if d < 320 else ">320"


def audit(path):
    head, p1i, p2i = parse(path)
    chars = [CharacterId(int(head["p1char"])), CharacterId(int(head["p2char"]))]
    smin, smax, world = (float(x) for x in head["stage"].split(","))
    x1, y1 = (float(x) for x in head["p1start"].split(","))
    x2, y2 = (float(x) for x in head["p2start"].split(","))
    sim = GameSim(make_config("hero:" + FOLDERS[int(chars[0])], x1, y1, True),
                  make_config("hero:" + FOLDERS[int(chars[1])], x2, y2, False),
                  smin, smax, world)

    attempts = [[], []]                     # finished Attempt objects per seat
    inflight = [None, None]
    stats = [defaultdict(lambda: [0, 0, 0, 0]) for _ in range(2)]   # uses, hits, blocked, whiffs
    whiff_ctx = [defaultdict(int) for _ in range(2)]
    unattributed = [0, 0]                   # damage with no attempt in flight (projectiles, throws)
    idle_frames = [0, 0]
    block_frames = [0, 0]                   # frames spent in blockstun = it actually guarded
    block_events = [0, 0]                   # separate guard instances
    prev_block = [None, None]
    knockdowns = []
    wakeups = []
    pending = None
    ko_frame = -1

    for i in range(len(p1i)):
        f1, f2 = unpack(p1i[i]), unpack(p2i[i])
        pre = [
            dict(state=sim.P1.State, hp=sim.P1.Hp, x=sim.P1.Position.X.ToFloat(),
                 ph=phase_of(sim.P1), mv=sim.P1.CurrentMove.Id if sim.P1.CurrentMove else "-"),
            dict(state=sim.P2.State, hp=sim.P2.Hp, x=sim.P2.Position.X.ToFloat(),
                 ph=phase_of(sim.P2), mv=sim.P2.CurrentMove.Id if sim.P2.CurrentMove else "-"),
        ]
        dist = abs(pre[0]["x"] - pre[1]["x"])

        # --- open / close attack attempts from the Attack state itself ---
        for s in (0, 1):
            if pre[s]["state"] == PlayerState.Attack and pre[s]["mv"] != "-":
                if inflight[s] is None or inflight[s].id != pre[s]["mv"]:
                    if inflight[s] is not None:            # replaced without leaving Attack (cancel)
                        _close(inflight[s], i, stats[s], whiff_ctx[s], pre[1 - s], dist)
                    inflight[s] = Attempt(pre[s]["mv"], i)
                    stats[s][pre[s]["mv"]][0] += 1
            elif inflight[s] is not None:
                _close(inflight[s], i, stats[s], whiff_ctx[s], pre[1 - s], dist)
                inflight[s] = None
            if int(pre[s]["state"]) in NEUTRAL:
                idle_frames[s] += 1
            if pre[s]["state"] == PlayerState.DefenseHit:
                block_frames[s] += 1
                if prev_block[s] != PlayerState.DefenseHit and prev_block[s] is not None:
                    block_events[s] += 1
            prev_block[s] = pre[s]["state"]

        res = sim.Step(f1, f2)
        post = [dict(state=sim.P1.State, hp=sim.P1.Hp), dict(state=sim.P2.State, hp=sim.P2.Hp)]

        # --- contact: the opponent lost HP, or entered blockstun, this frame ---
        for s in (0, 1):
            o = 1 - s
            took_damage = post[o]["hp"] < pre[o]["hp"]
            was_blocked = post[o]["state"] == PlayerState.DefenseHit
            if not (took_damage or was_blocked):
                continue
            if inflight[s] is not None:
                if not inflight[s].contact:
                    inflight[s].contact = True
                    stats[s][inflight[s].id][2 if was_blocked else 1] += 1
            else:
                last = next((a for a in reversed(attempts[s]) if i - a.start <= CREDIT_WINDOW), None)
                if last is not None:
                    if not last.contact:
                        last.contact = True
                        stats[s][last.id][2 if was_blocked else 1] += 1
                else:
                    unattributed[s] += 1

        # --- knockdowns ---
        for s in (0, 1):
            if post[s]["state"] == PlayerState.Downed and pre[s]["state"] != PlayerState.Downed:
                cause = pre[1 - s]["mv"]
                knockdowns.append((i, s, cause))
                if s == 1:
                    pending = dict(frame=i, cause=cause, first_act=None, hit_after=False,
                                   wake_frame=None, damage_after=0)
        if pending is not None:
            if post[1]["hp"] < pre[1]["hp"]:
                pending["hit_after"] = True
                pending["damage_after"] += pre[1]["hp"] - post[1]["hp"]
            if pending["wake_frame"] is None and int(post[1]["state"]) in NEUTRAL:
                pending["wake_frame"] = i
                pending["first_act"] = state_bucket(post[1]["state"])
            if pending["wake_frame"] is not None and i - pending["wake_frame"] >= 45:
                wakeups.append(pending)
                pending = None

        if res.MatchOverWinner >= 0:
            ko_frame = i
            break

    for s in (0, 1):
        if inflight[s] is not None:
            o = sim.P2 if s == 0 else sim.P1
            _close(inflight[s], len(p1i), stats[s], whiff_ctx[s],
                   dict(state=o.State, x=o.Position.X.ToFloat()), 0)
    if pending is not None:
        wakeups.append(pending)

    # ---------------- report ----------------
    agents = [head.get("p1agent") or "?", head.get("p2agent") or "?"]
    print(f"\n{'=' * 100}")
    print(f"{os.path.basename(path)}  [{len(p1i)} frames, {len(p1i) / 60:.1f}s]  "
          f"{FOLDERS[int(chars[0])]}(P1={agents[0]}) vs {FOLDERS[int(chars[1])]}(P2={agents[1]})"
          + (f"  KO@f{ko_frame}" if ko_frame >= 0 else "  no KO"))
    print(f"最终 HP {sim.P1.Hp}/{sim.P1.MaxHp} vs {sim.P2.Hp}/{sim.P2.MaxHp}")

    for s, label in ((0, "P1 (你)"), (1, "P2 (AI)")):
        tot = [sum(v[k] for v in stats[s].values()) for k in range(4)]
        if tot[0] == 0:
            print(f"\n  {label}: 没有出招")
            continue
        print(f"\n  {label}: 出招 {tot[0]} · 命中 {tot[1]} · 被挡 {tot[2]} · 空挥 {tot[3]}"
              f" ({tot[3] / max(1, tot[0]) * 100:.0f}%) · 发呆帧 {idle_frames[s]}"
              f" · 自己防御 {block_events[s]} 次/{block_frames[s]} 帧"
              f" · 无归属伤害(火球/投技) {unattributed[s]}")
        for mv, v in sorted(stats[s].items(), key=lambda kv: -kv[1][3])[:7]:
            if v[0] == 0:
                continue
            print(f"     {mv:>7} 用{v[0]:>4}  命中{v[1]:>4}  被挡{v[2]:>3}  空挥{v[3]:>4}"
                  f" ({v[3] / v[0] * 100:>3.0f}%)")
        if whiff_ctx[s]:
            by_st, by_d = defaultdict(int), defaultdict(int)
            for (st, db), n in whiff_ctx[s].items():
                by_st[st] += n
                by_d[db] += n
            print("     空挥时对手: " + "  ".join(f"{k}={v}" for k, v in
                                              sorted(by_st.items(), key=lambda kv: -kv[1])))
            print("     空挥时距离: " + "  ".join(f"{k}={v}" for k, v in
                                              sorted(by_d.items(), key=lambda kv: -kv[1])))

    ai_downs = sum(1 for k in knockdowns if k[1] == 1)
    me_downs = sum(1 for k in knockdowns if k[1] == 0)
    print(f"\n  击倒: AI 被打倒 {ai_downs} 次 · 你被打倒 {me_downs} 次")
    if wakeups:
        b = sum(1 for w in wakeups if w["first_act"] == "idle/walk")
        print(f"  AI 起身首帧: 站着/走 {b} · 其他 {len(wakeups) - b}"
              f"   |  起身后 45 帧内又挨打: {sum(1 for w in wakeups if w['hit_after'])}/{len(wakeups)}")
        for w in wakeups[:10]:
            print(f"     f{w['frame']:>5} 被你用 {w['cause']:>7} 打倒 → 起身首帧 "
                  f"{str(w['first_act']):>9}"
                  + (f"  ← 又吃 {w['damage_after']} 伤害" if w["hit_after"] else ""))
    return dict(ai_downs=ai_downs, wakeups=wakeups, stats=stats)


def _close(a, end, stat, ctx, opp_pre, dist):
    a.end = end
    if a.contact:
        return
    stat[a.id][3] += 1
    ctx[(state_bucket(opp_pre["state"] if isinstance(opp_pre, dict) else opp_pre.state),
         dist_bucket(dist))] += 1


if __name__ == "__main__":
    files = sys.argv[1:]
    if not files:
        rp = os.path.join(os.environ["APPDATA"], "Godot", "app_userdata", "MouseKombat", "replays")
        allf = sorted((os.path.join(r, f) for r, _d, fs in os.walk(rp) for f in fs
                       if f.endswith(".mkr")), key=os.path.getmtime)
        files = allf[-8:]
    for f in files:
        try:
            audit(f)
        except Exception as e:                                  # noqa: BLE001
            import traceback
            print(f"{os.path.basename(f)}: audit failed — {type(e).__name__}: {e}")
            traceback.print_exc()
