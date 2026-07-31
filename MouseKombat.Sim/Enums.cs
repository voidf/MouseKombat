namespace MouseKombat.Sim;

// Shared combat enums, promoted out of the Godot classes so the headless sim and the
// Godot views both reference the same definitions. (Previously: the first four were
// top-level in Moves.cs; the rest were nested inside Player.)

public enum AttackButton { LP, MP, HP, LK, MK, HK }

public enum Stance { Stand, Crouch, Air }

// Guard height of an attack (which stances can block it):
//   High = standing OR crouching block   (上段, most normals)
//   Mid  = standing block only           (中段, overhead — crouchers get hit)
//   Low  = crouching block only          (下段, low — standers get hit)
public enum GuardHeight { High, Mid, Low }

// Motion command (facing-relative): Qcf = 236 (↓↘→), Qcb = 214 (↓↙←), Dp = 623 (→↓↘).
public enum MotionInput { None, Qcf, Qcb, Dp }

// NOTE: append new members at the END only. Observation feeds StateIndex to the RL policy, so
// reordering would silently invalidate every trained model.
// Grabbed = held by an opponent's throw: the victim's position + pose are driven entirely by the
// ATTACKER's ThrowSpec bind timeline (see GameSim.TickThrowBind). The grabber itself stays in
// Attack (its move frames keep running), exposed as SimPlayer.IsGrabbing.
public enum PlayerState { Idle, Walk, Attack, Hurt, Dead, DefenseHit, Jump, Crouch, CrouchExit, Juggle, AirHurt, Downed, Wakeup, Grabbed }

public enum HurtRegion { Head, Body, Arms, Legs }

// NOTE: append new members at the END only. Observation feeds the raw int to the RL policy, so
// reordering would silently invalidate every trained model. Squirrel (松鼠) was appended third:
// policies trained before it existed only ever saw 0/1 in that slot, so they need a retrain (a
// warm start from the last two-character checkpoint) before they can handle the matchup.
public enum CharacterId { Hamster, Kangaroo, Squirrel }

// outcome of ApplyDamage, drives FX/SFX. Grabbed = a throw connected (no impact spark; the
// throw's damage arrives later as a separate Hit at the release frame).
public enum HitResult { None, Blocked, Hit, Grabbed }
