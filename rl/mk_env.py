"""Gymnasium env wrapping GameSim via the pythonnet bridge.

Our policy controls P1 (Hamster by default); the opponent is the C# StateMachineAgent controlling
P2. obs = Observation.Get (32 floats). action = MultiBinary(10): [L,R,U,D, LP,MP,HP,LK,MK,HK] ->
an InputFrame (dirs held; buttons EDGE-detected into the just-pressed mask, like a real press).
Reward = opp-HP-lost - self-HP-lost per frame, +/-1 terminal on win/loss, small time penalty.
"""
import os
import sys
import numpy as np
import gymnasium as gym
from gymnasium import spaces

_BASE = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "MouseKombat.Sim", "bin", "Release", "net8.0"))
_loaded = False


def _ensure_runtime():
    global _loaded
    if _loaded:
        return
    from pythonnet import set_runtime
    from clr_loader import get_coreclr
    set_runtime(get_coreclr(runtime_config=os.path.join(_BASE, "MouseKombat.Sim.runtimeconfig.json")))
    import clr
    sys.path.append(_BASE)
    clr.AddReference("MouseKombat.Sim")
    _loaded = True


_ensure_runtime()
from MouseKombat.Sim import (  # noqa: E402
    GameSim, PlayerConfig, InputFrame, StateMachineAgent, CharacterId, Observation,
)
from System.Numerics import Vector2  # noqa: E402

OBS = 32
NUM_ACT = 10


class MouseKombatEnv(gym.Env):
    metadata = {"render_modes": []}

    def __init__(self, agent_char="Hamster", opp_char="Kangaroo", max_steps=3600, opp_seed=0):
        super().__init__()
        self.observation_space = spaces.Box(low=-4.0, high=4.0, shape=(OBS,), dtype=np.float32)
        self.action_space = spaces.MultiBinary(NUM_ACT)
        self._agent_char = agent_char
        self._opp_char = opp_char
        self._max_steps = max_steps
        self._opp_seed = opp_seed
        self._sim = None
        self._opp = None
        self._prev_btn = np.zeros(6, dtype=bool)
        self._steps = 0

    def _make_sim(self):
        c1 = PlayerConfig()
        c1.Character = getattr(CharacterId, self._agent_char)
        c1.StartPos = Vector2(300.0, 560.0)
        c1.StartFacingRight = True
        c2 = PlayerConfig()
        c2.Character = getattr(CharacterId, self._opp_char)
        c2.StartPos = Vector2(500.0, 560.0)
        c2.StartFacingRight = False
        return GameSim(c1, c2, 40.0, 760.0, 800.0)

    def _obs(self):
        return np.array(list(Observation.Get(self._sim, 0)), dtype=np.float32)

    def reset(self, *, seed=None, options=None):
        super().reset(seed=seed)
        self._sim = self._make_sim()
        self._opp = StateMachineAgent(self._opp_seed)
        self._prev_btn[:] = False
        self._steps = 0
        return self._obs(), {}

    def step(self, action):
        a = np.asarray(action).astype(bool)
        btn = a[4:10]
        mask = 0
        for i in range(6):
            if btn[i] and not self._prev_btn[i]:
                mask |= (1 << i)
        self._prev_btn = btn.copy()

        f_agent = InputFrame(bool(a[0]), bool(a[1]), bool(a[2]), bool(a[3]), int(mask))
        f_opp = self._opp.Decide(self._sim, 1)

        hp0_self, hp0_opp = self._sim.P1.Hp, self._sim.P2.Hp
        res = self._sim.Step(f_agent, f_opp)
        self._steps += 1
        hp1_self, hp1_opp = self._sim.P1.Hp, self._sim.P2.Hp

        reward = (hp0_opp - hp1_opp) / 100.0 - (hp0_self - hp1_self) / 100.0 - 0.0005

        terminated = truncated = False
        w = res.MatchOverWinner
        if w == 0:
            reward += 1.0
            terminated = True
        elif w == 1:
            reward -= 1.0
            terminated = True
        elif self._steps >= self._max_steps:
            reward += 0.5 * ((hp1_self - hp1_opp) / 100.0)
            truncated = True

        return self._obs(), float(reward), terminated, truncated, {}
