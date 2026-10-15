"""Which model was actually driving the AI in this replay?

The replay header only carries agent labels from the build that added them, so a folder of older
files cannot be attributed by reading it. But a replay stores BOTH players' per-frame inputs, and a
policy is a deterministic function of the sim state — so the model that produced those inputs can be
identified by replaying the sim and asking every candidate what it would press on each frame. The
one whose decisions match the recording IS the model that played, no guessing from behaviour.

This is what makes player feedback attributable: "this model cannot handle my knockdown loop" is only
actionable once it is known WHICH model the replay was against.

Usage:  python rl/identify_replay.py <file.mkr> [...]      (no args = newest replays)
"""
import glob
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from read_replay import parse, unpack                        # noqa: E402
from mk_env import _ensure_runtime, make_config               # noqa: E402
_ensure_runtime()
from MouseKombat.Sim import GameSim, CharacterId, ReplayData   # noqa: E402

FOLDERS = {int(CharacterId.Hamster): "Hamster", int(CharacterId.Kangaroo): "Kangaroo",
           int(CharacterId.Squirrel): "Squirrel"}
ZERO_THREAT_DIMS = '--legacy-obs' in sys.argv
MODELS = os.path.abspath(os.path.join(HERE, "..", "ai_rl_model"))


def pack(f):
    """Packed input word, identical to ReplayData.Pack: 4 direction bits + 6 button bits.

    Build BOTH sides of the comparison with this. The obvious one-liner at the call site,
    `pack(pred) == pack(f2) & 0x0F | ((f2.JustPressedMask & 0x3F) << 4)`, parses as
    `pack(pred) == ((pack(f2) & 0x0F) | (...))` because `==` binds looser than the bitwise operators —
    and that silently scored a PERFECT frame-for-frame match as 88%.
    """
    v = (1 if f.Left else 0) | (2 if f.Right else 0) | (4 if f.Up else 0) | (8 if f.Down else 0)
    return v | ((f.JustPressedMask & 0x3F) << 4)


class OnnxMirror:
    """Exact mirror of OnnxAgent.cs (see verify_onnx_ingame.py for the same thing with SB3 checks)."""

    def __init__(self, path):
        import onnxruntime as ort
        self.sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
        self.iname = self.sess.get_inputs()[0].name
        self.prev = np.zeros(6, bool)
        self.name = os.path.basename(path)

    def decide(self, sim, idx):
        from MouseKombat.Sim import Observation
        obs = np.array(list(Observation.Get(sim, idx)), dtype=np.float32)
        if ZERO_THREAT_DIMS:
            # Reproduce the observation a build from BEFORE the Threat fix fed the policy: dimensions
            # 20/21 were CurrentMove.Damage/20, which is a constant 0 for every Heroes/ move. If a
            # replay only matches at ~100% with this on, the game that recorded it was never rebuilt.
            obs[20] = 0.0
            obs[21] = 0.0
        logits = self.sess.run(None, {self.iname: obs.reshape(1, -1)})[0][0]
        pressed = logits > 0.0
        mask = 0
        for i in range(6):
            if pressed[4 + i] and not self.prev[i]:
                mask |= 1 << i
            self.prev[i] = pressed[4 + i]
        from MouseKombat.Sim import InputFrame
        return InputFrame(bool(pressed[0]), bool(pressed[1]), bool(pressed[2]), bool(pressed[3]), mask)


def identify(path, candidates=None):
    head, p1i, p2i = parse(path)
    chars = [CharacterId(int(head["p1char"])), CharacterId(int(head["p2char"]))]
    smin, smax, world = (float(x) for x in head["stage"].split(","))
    x1, y1 = (float(x) for x in head["p1start"].split(","))
    x2, y2 = (float(x) for x in head["p2start"].split(","))

    if candidates is None:
        candidates = sorted(glob.glob(os.path.join(MODELS, "*.onnx")))

    a1 = head.get("p1agent") or "?"
    a2 = head.get("p2agent") or "?"
    c1name, c2name = FOLDERS[int(chars[0])], FOLDERS[int(chars[1])]
    print(f"\n=== {os.path.basename(path)}  [{len(p1i)} frames]  "
          f"{c1name} vs {c2name}  P1={a1} P2={a2}")
    rows = []
    for cand in candidates:
        try:
            agent = OnnxMirror(cand)
        except Exception as e:                                  # noqa: BLE001
            print(f"   {os.path.basename(cand)}: load failed {e}")
            continue
        sim = GameSim(make_config("hero:" + FOLDERS[int(chars[0])], x1, y1, True),
                      make_config("hero:" + FOLDERS[int(chars[1])], x2, y2, False),
                      smin, smax, world)
        match = total = 0
        for i in range(len(p1i)):
            f1, f2 = unpack(p1i[i]), unpack(p2i[i])
            pred = agent.decide(sim, 1)          # what this model would do in the AI seat
            if pack(pred) == pack(f2):
                match += 1
            total += 1
            sim.Step(f1, f2)
        rows.append((match / max(1, total), agent.name, match, total))
        # one match gives a clear answer; the rest are context
    rows.sort(reverse=True)
    for rate, name, m, t in rows:
        flag = "  <<< MATCH" if rate > 0.999 else ""
        print(f"   {rate * 100:6.2f}%  {name:<32} ({m}/{t} frames identical){flag}")
    return rows


if __name__ == "__main__":
    files = sys.argv[1:]
    if not files:
        rp = os.path.join(os.environ["APPDATA"], "Godot", "app_userdata", "MouseKombat", "replays")
        # newest distinct replays (the game can write the same match twice; dedupe by size+frames)
        allf = sorted((os.path.join(r, f) for r, _d, fs in os.walk(rp) for f in fs
                       if f.endswith(".mkr")), key=os.path.getmtime)
        files = allf[-10:]
    for f in files:
        try:
            identify(f)
        except Exception as e:                                  # noqa: BLE001
            print(f"{os.path.basename(f)}: failed — {type(e).__name__}: {e}")

