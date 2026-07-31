// Assembly-wide alias so ported combat logic reads like the original (`Vector2` -> `Vec2`).
// Points at the fixed-point vector (see Fix.cs): the sim must be bit-identical across machines
// for rollback netcode + replays, which floats cannot guarantee across architectures.
global using Vec2 = MouseKombat.Sim.FixVec2;
