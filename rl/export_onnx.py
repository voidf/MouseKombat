"""Export a trained SB3 PPO policy to ONNX for in-game inference (Godot OnnxAgent).

Contract (must match OnnxAgent.cs and mk_env.py):
  input  "obs"    : float32 [batch, 32]  = Observation.Get
  output "logits" : float32 [batch, 10]  = [L,R,U,D, LP,MP,HP,LK,MK,HK]; pressed = logit > 0

Usage:  python rl/export_onnx.py [checkpoint_name] [out_name]
Writes to ai_rl_model/<out_name>.onnx (where the ReadyScreen menu scans).
"""
import os
import sys
import numpy as np
import torch as th
from stable_baselines3 import PPO

HERE = os.path.dirname(__file__)
CKPT_DIR = os.path.join(HERE, "checkpoints")
OUT_DIR = os.path.abspath(os.path.join(HERE, "..", "ai_rl_model"))
OBS = 32


class OnnxPolicy(th.nn.Module):
    """obs -> action logits (the deterministic action is logits > 0 for a MultiBinary head)."""
    def __init__(self, policy):
        super().__init__()
        self.policy = policy

    def forward(self, obs):
        features = self.policy.extract_features(obs)
        latent_pi, _ = self.policy.mlp_extractor(features)
        return self.policy.action_net(latent_pi)


def main():
    ckpt = sys.argv[1] if len(sys.argv) > 1 else "ppo_hamster_v0"
    out = sys.argv[2] if len(sys.argv) > 2 else ckpt
    model = PPO.load(os.path.join(CKPT_DIR, ckpt), device="cpu")

    wrap = OnnxPolicy(model.policy).eval()
    dummy = th.zeros(1, OBS, dtype=th.float32)
    os.makedirs(OUT_DIR, exist_ok=True)
    out_path = os.path.join(OUT_DIR, out + ".onnx")
    th.onnx.export(
        wrap, dummy, out_path,
        input_names=["obs"], output_names=["logits"],
        dynamic_axes={"obs": {0: "batch"}, "logits": {0: "batch"}},
        opset_version=17,
    )
    print(f"exported {out_path}")

    # validate: ONNX threshold-actions must match SB3 deterministic predict
    import onnxruntime as ort
    sess = ort.InferenceSession(out_path)
    iname = sess.get_inputs()[0].name
    rng = np.random.default_rng(0)
    mismatch = 0
    for _ in range(200):
        o = rng.standard_normal((1, OBS)).astype(np.float32)
        logits = sess.run(None, {iname: o})[0]
        onnx_act = (logits > 0).astype(np.int64)[0]
        sb3_act, _ = model.predict(o[0], deterministic=True)
        if not np.array_equal(onnx_act, np.asarray(sb3_act)):
            mismatch += 1
    print(f"validation: {200 - mismatch}/200 actions match SB3 deterministic" + (" — OK" if mismatch == 0 else " — MISMATCH"))


if __name__ == "__main__":
    main()
