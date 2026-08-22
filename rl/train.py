"""First PPO run: train a P1 (Hamster) policy against the C# state-machine AI.

Usage:  python rl/train.py [total_timesteps]
Saves to rl/checkpoints/. A short run (~200k) just validates the pipeline learns; scale up later.
"""
import os
import sys

from stable_baselines3 import PPO
from stable_baselines3.common.env_util import make_vec_env
from stable_baselines3.common.callbacks import CheckpointCallback

from mk_env import MouseKombatEnv


def main():
    total = int(sys.argv[1]) if len(sys.argv) > 1 else 200_000
    n_envs = 8

    # chars come from env vars so the same script trains a data-driven hero:
    #   MK_AGENT_CHAR=hero:Kangaroo MK_OPP_CHAR=hero:新角色 python rl/train.py 2000
    agent_char = os.environ.get("MK_AGENT_CHAR", "Hamster")
    opp_char = os.environ.get("MK_OPP_CHAR", "Kangaroo")
    venv = make_vec_env(MouseKombatEnv, n_envs=n_envs,
                        env_kwargs=dict(agent_char=agent_char, opp_char=opp_char))
    model = PPO(
        "MlpPolicy", venv,
        n_steps=1024, batch_size=2048, gae_lambda=0.95, gamma=0.99,
        ent_coef=0.01, learning_rate=3e-4, n_epochs=4,
        policy_kwargs=dict(net_arch=[256, 256]),
        verbose=1, device="cpu",  # tiny MLP: CPU avoids per-rollout GPU transfer overhead
    )

    ckpt_dir = os.path.join(os.path.dirname(__file__), "checkpoints")
    os.makedirs(ckpt_dir, exist_ok=True)
    cb = CheckpointCallback(save_freq=max(1, 100_000 // n_envs), save_path=ckpt_dir, name_prefix="ppo_hamster")

    model.learn(total_timesteps=total, callback=cb, progress_bar=False)
    out = os.path.join(ckpt_dir, "ppo_hamster_v0")
    model.save(out)
    print(f"saved {out}.zip")


if __name__ == "__main__":
    main()
