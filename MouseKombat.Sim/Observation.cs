using System;

namespace MouseKombat.Sim;

// Fixed-shape observation vector for AI / RL. Built from the sim's logic-layer state so it is
// identical headless and in-game. The tail is RESERVED padding (zeros today) for future
// 斗气槽 / 必杀槽 / round-countdown inputs — adding those later fills reserved slots WITHOUT
// changing Size, so an existing policy's input layer keeps working (cheap transfer learning).
//
// Opponent input-buffer access is deliberately NOT included here: a state-machine AI may read
// it directly off the sim, but RL must not (reading the enemy's queued command = cheating).
public static class Observation
{
    private const int CoreSize = 23;
    private const int ProjSize = 5;       // incoming-fireball awareness (active, dx, dy, dir) + own-active
    private const int CharSize = 2;       // self + opponent CharacterId (asymmetric matchup)
    private const int ThrowSize = 2;      // self/opp "held by a throw" flags (was reserved padding)
    public const int Size = CoreSize + ProjSize + CharSize + ThrowSize; // 32

    // selfIndex: 0 = P1's view, 1 = P2's view. Values roughly normalized to ~[-1,1] / [0,1].
    public static float[] Get(GameSim sim, int selfIndex, float worldWidth = 800f, float worldHeight = 600f)
    {
        var o = new float[Size];
        Fill(sim, selfIndex, worldWidth, worldHeight, o);
        return o;
    }

    // allocation-free variant for hot RL loops (buffer length must be >= Size)
    public static void Fill(GameSim sim, int selfIndex, float worldWidth, float worldHeight, float[] o)
    {
        var self = sim.Player(selfIndex);
        var opp = sim.Player(1 - selfIndex);

        int i = 0;
        o[i++] = self.Hp / (float)self.MaxHp;
        o[i++] = opp.Hp / (float)opp.MaxHp;
        // Fix -> float happens HERE, at the observation boundary: the RL contract is 32 floats.
        // The sim itself stays fixed-point, so the numbers being converted are already identical
        // on every machine.
        o[i++] = (float)self.Position.X / worldWidth;
        o[i++] = (float)self.Position.Y / worldHeight;
        o[i++] = (float)opp.Position.X / worldWidth;
        o[i++] = (float)opp.Position.Y / worldHeight;
        o[i++] = (float)(opp.Position.X - self.Position.X) / worldWidth;   // signed horizontal gap
        o[i++] = (float)(opp.Position.Y - self.Position.Y) / worldHeight;
        o[i++] = self.FacingRight ? 1f : 0f;
        o[i++] = opp.FacingRight ? 1f : 0f;
        o[i++] = self.IsAirborne ? 1f : 0f;
        o[i++] = opp.IsAirborne ? 1f : 0f;
        o[i++] = (float)self.Vy / 2000f;
        o[i++] = (float)opp.Vy / 2000f;
        o[i++] = self.StateIndex / 13f;      // divisor intentionally frozen at 13 — see the throw block below
        o[i++] = opp.StateIndex / 13f;
        o[i++] = self.AttackPhase() / 3f;
        o[i++] = opp.AttackPhase() / 3f;
        o[i++] = Math.Clamp(self.AtkFrame, 0, 60) / 60f;
        o[i++] = Math.Clamp(opp.AtkFrame, 0, 60) / 60f;
        o[i++] = (self.CurrentMove?.Damage ?? 0) / 20f;
        o[i++] = (opp.CurrentMove?.Damage ?? 0) / 20f;
        o[i++] = (self.CurrentMove != null ? (int)self.CurrentMove.Guard : 0) / 2f;
        // i == CoreSize (23) here.

        // ---- projectile awareness (was reserved padding) ----
        // Nearest OPPONENT-owned fireball (the incoming threat) + whether we have one out.
        // All zero when nothing is on screen, so a policy trained before this still sees zeros
        // most of the time — only the brief fireball window is new information.
        SimProjectile incoming = null;
        Fix bestDx = Fix.FromRaw(int.MaxValue); // sentinel: float.MaxValue would overflow Q16.16
        bool ownActive = false;
        var projs = sim.Projectiles;
        for (int k = 0; k < projs.Count; k++)
        {
            var pr = projs[k];
            if (pr.OwnerIndex == selfIndex) { ownActive = true; continue; }
            Fix dx = Fix.Abs(pr.Position.X - self.Position.X);
            if (dx < bestDx) { bestDx = dx; incoming = pr; }
        }
        if (incoming != null)
        {
            o[i++] = 1f;
            o[i++] = (float)(incoming.Position.X - self.Position.X) / worldWidth;
            o[i++] = (float)(incoming.Position.Y - self.Position.Y) / worldHeight;
            o[i++] = incoming.Dir; // -1 / +1 travel direction
        }
        else { o[i++] = 0f; o[i++] = 0f; o[i++] = 0f; o[i++] = 0f; }
        o[i++] = ownActive ? 1f : 0f;
        // i == CoreSize + ProjSize (28) here.

        // ---- character ids (the matchup is asymmetric: Hamster vs Kangaroo) ----
        o[i++] = (int)self.Character;   // 0 = Hamster, 1 = Kangaroo
        o[i++] = (int)opp.Character;
        // i == 30 here.

        // ---- throw state (grew into the reserved tail; Size is UNCHANGED at 32, so an existing
        // policy's input layer still loads). Zero except during the brief held window, which is
        // also why StateIndex below keeps its /13f divisor: adding PlayerState.Grabbed must not
        // rescale a feature every trained model already depends on.
        o[i++] = self.State == PlayerState.Grabbed ? 1f : 0f;
        o[i++] = opp.State == PlayerState.Grabbed ? 1f : 0f;
        // i == Size (32) here.
    }
}
