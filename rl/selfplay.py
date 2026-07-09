"""Self-play PPO: train a Hamster policy against a growing pool of its own past snapshots
(plus the state-machine as a bootstrap/anchor). Randomizes which side the learner controls.

Why self-play: beating one fixed weak opponent (train.py) caps skill. Playing evolving copies of
itself forces real spacing / punishes / anti-airs. Snapshots are saved to checkpoints/pool/.

Usage:  python rl/selfplay.py [total_timesteps]
"""
import os
import sys
import random
import numpy as np
import gymnasium as gym
from gymnasium import spaces
from stable_baselines3 import PPO
from stable_baselines3.common.env_util import make_vec_env
from stable_baselines3.common.callbacks import BaseCallback

from mk_env import _ensure_runtime
_ensure_runtime()
from MouseKombat.Sim import (  # noqa: E402
    GameSim, PlayerConfig, InputFrame, StateMachineAgent, CharacterId, Observation,
)
from System.Numerics import Vector2  # noqa: E402

OBS = 32
NUM_ACT = 10
HERE = os.path.dirname(__file__)
POOL_DIR = os.path.join(HERE, "checkpoints", "pool")

# in-process shared pool (DummyVecEnv => envs share module globals)
_pool_paths = []
_pol_cache = {}


def _load_policy(path):
    if path not in _pol_cache:
        _pol_cache[path] = PPO.load(path, device="cpu")
    return _pol_cache[path]


def _decode(press, prev):
    mask = 0
    for i in range(6):
        if press[4 + i] and not prev[i]:
            mask |= 1 << i
        prev[i] = press[4 + i]
    return InputFrame(bool(press[0]), bool(press[1]), bool(press[2]), bool(press[3]), int(mask))


class SelfPlayEnv(gym.Env):
    def __init__(self, max_steps=3600, pool_prob=0.8):
        super().__init__()
        self.observation_space = spaces.Box(-4.0, 4.0, (OBS,), np.float32)
        self.action_space = spaces.MultiBinary(NUM_ACT)
        self._max = max_steps
        self._pool_prob = pool_prob

    def _make(self):
        ch = getattr(CharacterId, os.environ.get("MK_CHAR", "Hamster"))  # MK_CHAR=Kangaroo to train the roo
        c1 = PlayerConfig(); c1.Character = ch; c1.StartPos = Vector2(300.0, 560.0); c1.StartFacingRight = True
        c2 = PlayerConfig(); c2.Character = ch; c2.StartPos = Vector2(500.0, 560.0); c2.StartFacingRight = False
        return GameSim(c1, c2, 40.0, 760.0, 800.0)

    def reset(self, *, seed=None, options=None):
        super().reset(seed=seed)
        self._sim = self._make()
        self._self = random.randint(0, 1)          # learner controls a random side
        self._opp = 1 - self._self
        if _pool_paths and random.random() < self._pool_prob:
            self._opp_pol = _load_policy(random.choice(_pool_paths)); self._opp_sm = None
        else:
            self._opp_pol = None; self._opp_sm = StateMachineAgent(random.randint(0, 9999))
        self._prev_self = np.zeros(6, bool)
        self._prev_opp = np.zeros(6, bool)
        self._steps = 0
        return self._obs(self._self), {}

    def _obs(self, idx):
        return np.array(list(Observation.Get(self._sim, idx)), dtype=np.float32)

    def _opp_frame(self):
        if self._opp_sm is not None:
            return self._opp_sm.Decide(self._sim, self._opp)
        act, _ = self._opp_pol.predict(self._obs(self._opp), deterministic=False)
        return _decode(np.asarray(act).astype(bool), self._prev_opp)

    def step(self, action):
        f_self = _decode(np.asarray(action).astype(bool), self._prev_self)
        f_opp = self._opp_frame()
        f1, f2 = (f_self, f_opp) if self._self == 0 else (f_opp, f_self)

        me = self._sim.Player(self._self); op = self._sim.Player(self._opp)
        h0s, h0o = me.Hp, op.Hp
        res = self._sim.Step(f1, f2)
        self._steps += 1
        h1s, h1o = me.Hp, op.Hp

        reward = (h0o - h1o) / 100.0 - (h0s - h1s) / 100.0 - 0.0005
        term = trunc = False
        w = res.MatchOverWinner
        if w == self._self:
            reward += 1.0; term = True
        elif w == self._opp:
            reward -= 1.0; term = True
        elif self._steps >= self._max:
            reward += 0.5 * ((h1s - h1o) / 100.0); trunc = True
        return self._obs(self._self), float(reward), term, trunc, {}


class SnapshotCallback(BaseCallback):
    def __init__(self, every, verbose=0):
        super().__init__(verbose)
        self.every = every
        self._last = 0

    def _on_step(self):
        if self.num_timesteps - self._last >= self.every:
            self._last = self.num_timesteps
            os.makedirs(POOL_DIR, exist_ok=True)
            p = os.path.join(POOL_DIR, f"snap_{self.num_timesteps}.zip")
            self.model.save(p)
            _pool_paths.append(p)
            if self.verbose:
                print(f"[selfplay] snapshot -> {os.path.basename(p)} (pool={len(_pool_paths)})")
        return True


def main():
    total = int(sys.argv[1]) if len(sys.argv) > 1 else 1_000_000
    init = sys.argv[2] if len(sys.argv) > 2 else None  # warm-start + pool anchor (e.g. v0)
    n_envs = 8

    # Anchor the pool with a fixed reference policy so the learner must keep beating it
    # (prevents drifting into a self-play bubble that loses to outside styles). FSM stays the
    # bootstrap anchor via reset()'s pool_prob branch.
    if init:
        _pool_paths.append(init)

    venv = make_vec_env(SelfPlayEnv, n_envs=n_envs)
    if init:
        model = PPO.load(init, env=venv, device="cpu")  # warm-start from init's weights
        print(f"[selfplay] warm-started from {init}")
    else:
        model = PPO(
            "MlpPolicy", venv,
            n_steps=1024, batch_size=2048, gamma=0.99, gae_lambda=0.95,
            ent_coef=0.01, learning_rate=3e-4, n_epochs=4,
            policy_kwargs=dict(net_arch=[256, 256]),
            verbose=1, device="cpu",
        )
    cb = SnapshotCallback(every=max(1, total // 10), verbose=1)
    model.learn(total_timesteps=total, callback=cb, progress_bar=False)
    out_name = sys.argv[3] if len(sys.argv) > 3 else "ppo_hamster_selfplay_v1"
    out = os.path.join(HERE, "checkpoints", out_name)
    model.save(out)
    print(f"saved {out}.zip")


if __name__ == "__main__":
    main()
