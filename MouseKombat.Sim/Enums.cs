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

public enum PlayerState { Idle, Walk, Attack, Hurt, Dead, DefenseHit, Jump, Crouch, CrouchExit, Juggle, AirHurt, Downed, Wakeup }

public enum HurtRegion { Head, Body, Arms, Legs }

public enum CharacterId { Hamster, Kangaroo }

public enum HitResult { None, Blocked, Hit } // outcome of ApplyDamage, drives FX/SFX
