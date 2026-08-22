using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MouseKombat.Sim;

// ==============================Heroes/ 落盘数据模型 ==============================
// The on-disk character format edited by MKEditor and consumed by the game at startup
// (see PROTOCOL-style docs at the top of HeroCompiler). Everything here is PLAIN DATA:
// no Godot types, no logic — the compiler below turns it into the runtime MoveDef/MoveSet
// structures the sim already runs on, so rollback determinism is untouched.
//
// Conventions shared with the editor:
//   * boxes are authored as CENTER + HALF-EXTENTS (cx, cy, hw, hh), local to the fighter's
//     feet anchor, facing RIGHT. The engine mirrors them by facing like every other rect.
//   * "root" is the frame's absolute offset from the origin (0,0 = the fighter's spawn
//     anchor); the engine turns consecutive roots into the per-frame displacement it applies.
//     X is FORWARD-relative (positive = toward the opponent), Y is screen space (negative = up).
//   * a layer's offset is where the CENTER of its image sits, relative to the frame root.
//   * frames[] index == logic frame == timeline cell (60 fps fixed).

public sealed class HeroVec
{
    public float X { get; set; }
    public float Y { get; set; }
    public HeroVec() { }
    public HeroVec(float x, float y) { X = x; Y = y; }
}

public sealed class HeroBox
{
    public float Cx { get; set; }
    public float Cy { get; set; }
    public float Hw { get; set; }
    public float Hh { get; set; }
    public HeroBox() { }
    public HeroBox(float cx, float cy, float hw, float hh) { Cx = cx; Cy = cy; Hw = hw; Hh = hh; }

    public SimRect ToSim() => new(Cx - Hw, Cy - Hh, Hw * 2f, Hh * 2f);
    public static HeroBox FromSim(in SimRect r) =>
        new((r.Position.X + r.Size.X * 0.5f).ToFloat(), (r.Position.Y + r.Size.Y * 0.5f).ToFloat(),
            (r.Size.X * 0.5f).ToFloat(), (r.Size.Y * 0.5f).ToFloat());
}

public sealed class HeroLayer
{
    public int Z { get; set; }                  // render order, ascending
    public HeroVec Off { get; set; } = new();   // image center relative to the frame root
    public string Img { get; set; } = "";       // path relative to the character folder ("images/...")
}

public sealed class HeroFx
{
    public List<string> Particles { get; set; } = new();  // res://-relative .tscn paths
    public List<string> Sounds { get; set; } = new();     // res://-relative .ogg paths
}

public sealed class HeroFrame
{
    public HeroVec Root { get; set; } = new();
    public List<HeroLayer> Layers { get; set; } = new();
    public HeroFx Fx { get; set; } = new();
    // per-frame defensive boxes; empty = the character's base boxes (char.json)
    public List<HeroBox> Hurtboxes { get; set; } = new();
    // throw followups only: damage applied to the victim when this frame is reached
    public int HurtDamage { get; set; }
}

public sealed class HeroActive
{
    public int[] ActiveRange { get; set; } = { 0, 0 };    // [from, to] inclusive
    public bool ShouldWhiffIfNotHit { get; set; }
    public string WhiffAction { get; set; } = "";          // action id to jump to on whiff
    public bool IsGrab { get; set; }
    public string ThrowAction { get; set; } = "";          // grab connected -> start this action
    public int Damage { get; set; }                        // per-interval melee damage
    // strike (or grab-judgement) boxes of this interval. Consumed ONCE: after a connection
    // every other box of THIS interval is dead; other intervals are unaffected.
    public List<HeroBox> Hitboxes { get; set; } = new();
}

public sealed class HeroProjectileSpawn
{
    public int SpawnFrame { get; set; }
    public string Prefab { get; set; } = "";    // FireballTSCN/<Prefab>.tscn (hitbox lives there)
    public float Speed { get; set; }
    public HeroVec Offset { get; set; } = new();
    public int Damage { get; set; }
    public string Guard { get; set; } = "High";
    public int OH { get; set; }
    public int OB { get; set; }
    public float Knockback { get; set; }
    public bool CanAirJuggle { get; set; }
    public int LifeTimeFrame { get; set; }     // 0 = unlimited
    public float MaxDistance { get; set; }     // 0 = unlimited
}

public sealed class HeroAttack
{
    public int[] StartupRange { get; set; } = { 0, 0 };
    public int[] RecoveryRange { get; set; } = { 0, 0 };
    public string Guard { get; set; } = "High";
    public int OH { get; set; } = 14;
    public int OB { get; set; } = 10;
    public float Knockback { get; set; }
    public float KnockbackOnBlock { get; set; }
    public bool Launches { get; set; }
    public float LaunchUp { get; set; } = 1250f;   // juggle trajectory (kept from the engine)
    public float LaunchBack { get; set; } = 120f;
    public bool CanAirJuggle { get; set; } = true;
    public bool ImmuneOnStartup { get; set; }
    public string Motion { get; set; } = "";       // "" / "236" / "214" / "623"
    public string CommandLabel { get; set; } = "";
    public List<string> Buttons { get; set; } = new();  // one = command button, two = simultaneous (throw input)
    public bool AnyPunch { get; set; }
    public bool AnyKick { get; set; }
    public string Stance { get; set; } = "Stand";
    public bool Unblockable { get; set; }          // throws ignore guard
    public List<string> StartupCancelInto { get; set; } = new();   // cancellable while in startup
    public List<string> RecoveryCancelInto { get; set; } = new();  // cancellable while in recovery
    public List<HeroActive> Actives { get; set; } = new();
    public List<HeroProjectileSpawn> Projectiles { get; set; } = new();
}

public sealed class HeroHurtTick
{
    public int Frame { get; set; }
    public int Damage { get; set; }
}

public sealed class HeroBindKey
{
    public int Frame { get; set; }                // attacker frame this key takes effect on
    public HeroVec BindPos { get; set; } = new(); // victim anchor relative to the attacker's anchor
    public string VictimAnim { get; set; } = "";  // action name on the VICTIM to display
    public bool IsResetVictimAnim { get; set; }   // restart the clip even when the name is unchanged
    public bool VictimSameDir { get; set; }       // face the same way as the attacker (back throws)
}

public sealed class HeroThrow
{
    public bool CanGrabAirborne { get; set; }
    public HeroVec ReleaseVel { get; set; } = new();
    public bool ReleaseToJuggle { get; set; }
    public List<HeroHurtTick> HurtTimeline { get; set; } = new();  // multi-hit throws
    public List<HeroBindKey> VictimBind { get; set; } = new();
}

public sealed class HeroActionDef
{
    public string Name { get; set; } = "";        // unique per character; the file name + the Id
    public bool Loop { get; set; }                // presentation: cycles instead of holding the last frame
    public List<HeroFrame> Frames { get; set; } = new();
    public bool IsAttack { get; set; }
    public HeroAttack Attack { get; set; } = new();
    public bool IsThrow { get; set; }             // throw-followup action (what a grab jumps to)
    public HeroThrow Throw { get; set; } = new();
    // from this frame on the fighter may act as if idle while the clip tail keeps playing
    public int CanActNextActionAt { get; set; } = -1;
}

public sealed class HeroBoxes4
{
    public HeroBox Head { get; set; } = new(-40, -172, 40, 27);
    public HeroBox Body { get; set; } = new(0, -102, 55, 47);
    public HeroBox Arms { get; set; } = new(0, -135, 65, 30);
    public HeroBox Legs { get; set; } = new(0, -35, 45, 35);
}

public sealed class HeroPhysics
{
    public int MaxHp { get; set; } = 10000;               // ×100 scale (config convention)
    public float WalkSpeed { get; set; } = 220f;
    public float DefDamageMultiplier { get; set; } = 0.1f;
    public float CornerPushbackScale { get; set; } = 1f;
    public int HurtStunFrames { get; set; } = 14;
    public int DefHitStunFrames { get; set; } = 10;
    public int CrouchEnterFrames { get; set; } = 8;
    public int DownedFrames { get; set; } = 30;
    public int DownedMinFrames { get; set; } = 12;
    public int WakeupFrames { get; set; } = 24;
    public float AirResetPop { get; set; } = 350f;
    public float JumpVelocity { get; set; } = 1350f;
    public float Gravity { get; set; } = 3600f;
    public float ForwardJumpSpeed { get; set; } = 420f;
    public float BackJumpSpeed { get; set; } = 380f;
    public HeroBoxes4 StandBoxes { get; set; } = new();
    public HeroBoxes4 CrouchBoxes { get; set; } = new();
}

// Which action names serve the engine's built-in locomotion/stun clips.
public sealed class HeroAnimNames
{
    public string Idle { get; set; } = "IDLE";
    public string Walk { get; set; } = "WALK";
    public string Hurt { get; set; } = "HURT";
    public string Def { get; set; } = "DEF";
    public string CrouchDef { get; set; } = "CROUCHDEF";
    public string Jump { get; set; } = "JUMP";
    public string EnterCrouch { get; set; } = "ENTER_CROUCH";
    public string CrouchIdle { get; set; } = "CROUCH";
    public string LaunchRise { get; set; } = "LAUNCH";
    public string Fall { get; set; } = "FALL";
    public string Knockdown { get; set; } = "KNOCKDOWN";
    public string Wakeup { get; set; } = "WAKEUP";
    public string AirHurt { get; set; } = "AIR_HURT";
}

public sealed class HeroCharDef
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "";         // == folder name == unique Id
    public string DisplayName { get; set; } = "";  // select-screen label (仓鼠 …)
    public HeroPhysics Physics { get; set; } = new();
    public HeroAnimNames AnimNames { get; set; } = new();
    public List<HeroActionDef> Actions { get; set; } = new();
}

// ============================== JSON IO ==============================

public static class HeroJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

// ============================== compiler ==============================
// HeroCharDef (plain data) -> the runtime MoveSet the sim consumes. The output only uses the
// SAME MoveDef extensions the legacy tables could also use, so there is exactly one combat
// code path and the rollback savestate is oblivious to where a table came from.
//
// Determinism note: the compiled table must be IDENTICAL on every machine of a match. The
// prefabHitbox resolver is fed from FireballTSCN/ scenes, and the lobby asset-hash check
// refuses mismatched peers, so both machines compile from byte-identical inputs.

public static class HeroCompiler
{
    // displacement smaller than this is authored noise, not motion (Q16.16 resolves 1.5e-5 px)
    private static readonly Fix Eps = 0.01f;

    // resolver: prefab name -> local hit rect read out of FireballTSCN/<name>.tscn at load time
    public static MoveSet Compile(HeroCharDef hero, Func<string, SimRect> prefabHitbox = null)
    {
        var moves = new List<MoveDef>();
        foreach (var a in hero.Actions)
            moves.Add(CompileAction(hero, a, prefabHitbox));
        return new MoveSet(moves);
    }

    private static MoveDef CompileAction(HeroCharDef hero, HeroActionDef a, Func<string, SimRect> prefabHitbox)
    {
        int total = Math.Max(1, a.Frames.Count);

        var m = new MoveDef
        {
            Id = a.Name,
            AnimName = a.Name,
            IsCommandMove = a.IsAttack && !a.IsThrow,   // locomotion + throw followups are not input-resolvable
            LoopAnim = a.Loop,
            TotalFramesOverride = total,
            CanActNextActionAt = a.CanActNextActionAt,
        };

        CompileRootMotion(a, m);

        if (a.Frames.Count > 0)
        {
            var perFrame = new SimRect[a.Frames.Count][];
            for (int i = 0; i < a.Frames.Count; i++)
            {
                var hb = a.Frames[i].Hurtboxes;
                if (hb == null || hb.Count == 0) continue;
                var arr = new SimRect[hb.Count];
                for (int j = 0; j < hb.Count; j++) arr[j] = hb[j].ToSim();
                perFrame[i] = arr;
            }
            m.FrameHurtboxes = perFrame;
        }

        if (a.IsThrow)
        {
            CompileThrow(a, m);
            return m;
        }

        if (!a.IsAttack) return m;

        var atk = a.Attack ?? new HeroAttack();
        m.Stance = ParseStance(atk.Stance);
        m.Guard = ParseGuard(atk.Guard);
        m.oH = atk.OH;
        m.oB = atk.OB;
        m.Knockback = atk.Knockback;
        m.KnockbackOnBlock = atk.KnockbackOnBlock;
        m.Launches = atk.Launches;
        m.LaunchUp = atk.LaunchUp;
        m.LaunchBack = atk.LaunchBack;
        m.CanAirJuggle = atk.CanAirJuggle;
        m.ImmuneOnStartup = atk.ImmuneOnStartup;
        m.Motion = ParseMotion(atk.Motion);
        m.CommandLabel = atk.CommandLabel;
        m.Unblockable = atk.Unblockable;
        m.Damage = 0;   // melee damage lives per active interval; kept 0 on the move itself

        // buttons: one entry = command button, two entries = simultaneous press (throw input)
        if (atk.Buttons != null && atk.Buttons.Count > 0)
        {
            m.Button = ParseButton(atk.Buttons[0]);
            if (atk.Buttons.Count >= 2)
            {
                m.ComboButtons = new AttackButton[atk.Buttons.Count];
                for (int i = 0; i < atk.Buttons.Count; i++) m.ComboButtons[i] = ParseButton(atk.Buttons[i]);
            }
        }
        m.AnyPunch = atk.AnyPunch;
        m.AnyKick = atk.AnyKick;
        if (atk.StartupCancelInto != null) m.StartupCancelInto = atk.StartupCancelInto.ToArray();
        if (atk.RecoveryCancelInto != null) m.RecoveryCancelInto = atk.RecoveryCancelInto.ToArray();

        // ---- phase ranges -> legacy triple + range fields ----
        int sFrom = RangeLo(atk.StartupRange, 0);
        int sTo = RangeHi(atk.StartupRange, Math.Min(sFrom, total - 1));
        int rFrom = RangeLo(atk.RecoveryRange, total);
        int rTo = RangeHi(atk.RecoveryRange, total - 1);
        m.StartupRange = (sFrom, sTo);
        m.RecoveryRange = (rFrom, Math.Min(rTo, Math.Max(total - 1, rFrom)));

        int lastActiveEnd = sTo;
        var windows = new List<ActiveSpec>();
        foreach (var act in atk.Actives)
        {
            int from = Math.Max(RangeLo(act.ActiveRange, sTo + 1), 0);
            int to = Math.Min(RangeHi(act.ActiveRange, from), total - 1);
            if (to < from) continue;
            var spec = new ActiveSpec
            {
                From = from,
                To = to,
                ShouldWhiffIfNotHit = act.ShouldWhiffIfNotHit,
                WhiffActionId = act.WhiffAction ?? "",
                IsGrab = act.IsGrab,
                ThrowActionId = act.ThrowAction ?? "",
                Hitboxes = new SimRect[Math.Max(1, act.Hitboxes?.Count ?? 0)],
                Damage = act.Damage,
            };
            for (int i = 0; i < (act.Hitboxes?.Count ?? 0); i++) spec.Hitboxes[i] = act.Hitboxes[i].ToSim();
            windows.Add(spec);
            if (to > lastActiveEnd) lastActiveEnd = to;
        }
        m.ActiveWindows = windows.ToArray();
        m.Startup = Math.Min(sTo + 1, total);
        m.Active = Math.Max(0, Math.Min(lastActiveEnd + 1, total) - m.Startup);
        m.Recovery = Math.Max(0, total - m.Startup - m.Active);
        if (windows.Count > 0 && windows[0].Hitboxes.Length > 0)
            m.Hitbox = windows[0].Hitboxes[0];   // debug draw / legacy readers

        // ---- projectiles ----
        if (atk.Projectiles != null && atk.Projectiles.Count > 0)
        {
            var spawns = new ProjectileSpawnSpec[atk.Projectiles.Count];
            for (int i = 0; i < atk.Projectiles.Count; i++)
            {
                var p = atk.Projectiles[i];
                var spec = new ProjectileSpec
                {
                    Speed = p.Speed,
                    Offset = new Vec2(p.Offset?.X ?? 0f, p.Offset?.Y ?? 0f),
                    Damage = p.Damage,
                    Guard = ParseGuard(p.Guard),
                    MaxDistance = p.MaxDistance,
                    Hitbox = prefabHitbox != null ? prefabHitbox(p.Prefab ?? "") : default,
                    CanAirJuggle = p.CanAirJuggle,
                    Knockback = p.Knockback,
                    oH = p.OH,
                    oB = p.OB,
                    LifeTimeFrame = p.LifeTimeFrame,
                    PrefabId = p.Prefab ?? "",
                };
                spawns[i] = new ProjectileSpawnSpec { SpawnFrame = p.SpawnFrame, Spec = spec };
            }
            m.ProjectileSpawns = spawns;
            m.SpawnsProjectile = true;
        }
        return m;
    }

    private static void CompileThrow(HeroActionDef a, MoveDef m)
    {
        var t = a.Throw ?? new HeroThrow();
        var fu = new ThrowFollowupSpec
        {
            CanGrabAirborne = t.CanGrabAirborne,
            ReleaseFrame = Math.Max(1, a.Frames.Count),   // the victim is let go when the action ends
            ReleaseVel = new Vec2(t.ReleaseVel?.X ?? 0f, t.ReleaseVel?.Y ?? 0f),
            ReleaseToJuggle = t.ReleaseToJuggle,
        };
        if (t.HurtTimeline != null && t.HurtTimeline.Count > 0)
        {
            fu.HurtFrames = new int[t.HurtTimeline.Count];
            fu.HurtDamages = new int[t.HurtTimeline.Count];
            for (int i = 0; i < t.HurtTimeline.Count; i++)
            {
                fu.HurtFrames[i] = t.HurtTimeline[i].Frame;
                fu.HurtDamages[i] = t.HurtTimeline[i].Damage;
            }
        }
        if (t.VictimBind != null && t.VictimBind.Count > 0)
        {
            var keys = new List<BindKey>();
            for (int i = 0; i < t.VictimBind.Count; i++)
            {
                var b = t.VictimBind[i];
                int from = b.Frame;
                int to = i + 1 < t.VictimBind.Count ? t.VictimBind[i + 1].Frame - 1 : fu.ReleaseFrame - 1;
                keys.Add(new BindKey
                {
                    From = from,
                    To = Math.Max(to, from),
                    VictimAnim = b.VictimAnim ?? "",
                    Offset = new Vec2(b.BindPos?.X ?? 0f, b.BindPos?.Y ?? 0f),
                    VictimSameDir = b.VictimSameDir,
                    ResetAnim = b.IsResetVictimAnim,
                });
            }
            fu.Bind = keys.ToArray();
        }
        m.IsThrowFollowup = true;
        m.Followup = fu;
        m.Unblockable = true;
        m.Startup = 0;
        m.Active = 0;
        m.Recovery = Math.Max(1, a.Frames.Count);
    }

    // root[] -> per-frame displacement merged into MoveKey runs (From..To inclusive)
    private static void CompileRootMotion(HeroActionDef a, MoveDef m)
    {
        if (a.Frames == null || a.Frames.Count < 2) return;
        var deltas = new Vec2[a.Frames.Count];
        bool any = false;
        var prev = new Vec2(a.Frames[0].Root?.X ?? 0f, a.Frames[0].Root?.Y ?? 0f);
        deltas[0] = prev;   // frame 0: jump straight to the first root
        if (Fix.Abs(prev.X) > Eps || Fix.Abs(prev.Y) > Eps) any = true;
        for (int i = 1; i < a.Frames.Count; i++)
        {
            var cur = new Vec2(a.Frames[i].Root?.X ?? 0f, a.Frames[i].Root?.Y ?? 0f);
            deltas[i] = cur - prev;
            if (Fix.Abs(deltas[i].X) > Eps || Fix.Abs(deltas[i].Y) > Eps) any = true;
            prev = cur;
        }
        if (!any) return;

        var keys = new List<MoveKey>();
        int start = 0;
        for (int i = 1; i <= deltas.Length; i++)
        {
            bool breakRun = i == deltas.Length
                || Fix.Abs(deltas[i].X - deltas[start].X) > Eps
                || Fix.Abs(deltas[i].Y - deltas[start].Y) > Eps;
            if (!breakRun) continue;
            if (Fix.Abs(deltas[start].X) > Eps || Fix.Abs(deltas[start].Y) > Eps)
                keys.Add(new MoveKey { From = start, To = i - 1, PerFrame = deltas[start] });
            start = i;
        }
        m.MotionTimeline = keys.ToArray();
    }

    public static PlayerConfig BuildConfig(HeroCharDef hero, float startX, float startY, bool facingRight)
    {
        var p = hero.Physics ?? new HeroPhysics();
        var cfg = new PlayerConfig
        {
            Character = CharacterId.Hamster,   // replaced by the caller for built-ins; heroes are table-driven
            MaxHp = p.MaxHp,
            WalkSpeedPxPerSec = p.WalkSpeed,
            DefDamageMultiplier = p.DefDamageMultiplier,
            CornerPushbackScale = p.CornerPushbackScale,
            HurtStunFrames = p.HurtStunFrames,
            DefHitStunFrames = p.DefHitStunFrames,
            CrouchEnterFrames = p.CrouchEnterFrames,
            DownedFrames = p.DownedFrames,
            DownedMinFrames = p.DownedMinFrames,
            WakeupFrames = p.WakeupFrames,
            AirResetPop = p.AirResetPop,
            JumpVelocity = p.JumpVelocity,
            Gravity = p.Gravity,
            ForwardJumpSpeed = p.ForwardJumpSpeed,
            BackJumpSpeed = p.BackJumpSpeed,
        };
        cfg.SetStart(startX, startY, facingRight);
        var n = hero.AnimNames ?? new HeroAnimNames();
        cfg.IdleAnimName = n.Idle;
        cfg.WalkAnimName = n.Walk;
        cfg.HurtAnimName = n.Hurt;
        cfg.DefAnimName = n.Def;
        cfg.CrouchDefAnimName = n.CrouchDef;
        cfg.JumpAnimName = n.Jump;
        cfg.EnterCrouchAnimName = n.EnterCrouch;
        cfg.CrouchIdleAnimName = n.CrouchIdle;
        cfg.LaunchRiseAnimName = n.LaunchRise;
        cfg.FallAnimName = n.Fall;
        cfg.KnockdownAnimName = n.Knockdown;
        cfg.WakeupAnimName = n.Wakeup;
        cfg.AirHurtAnimName = n.AirHurt;
        var s = p.StandBoxes ?? new HeroBoxes4();
        cfg.HeadBox = s.Head.ToSim();
        cfg.BodyBox = s.Body.ToSim();
        cfg.ArmsBox = s.Arms.ToSim();
        cfg.LegsBox = s.Legs.ToSim();
        var c = p.CrouchBoxes ?? new HeroBoxes4();
        cfg.CrouchHeadBox = c.Head.ToSim();
        cfg.CrouchBodyBox = c.Body.ToSim();
        cfg.CrouchArmsBox = c.Arms.ToSim();
        cfg.CrouchLegsBox = c.Legs.ToSim();
        return cfg;
    }

    // ---- small parse helpers (strings in JSON -> enums; unknown falls back safely) ----

    public static Stance ParseStance(string s) => s switch
    {
        "Crouch" => Stance.Crouch,
        "Air" => Stance.Air,
        _ => Stance.Stand,
    };

    public static GuardHeight ParseGuard(string s) => s switch
    {
        "Mid" => GuardHeight.Mid,
        "Low" => GuardHeight.Low,
        _ => GuardHeight.High,
    };

    public static MotionInput ParseMotion(string s) => s switch
    {
        "236" => MotionInput.Qcf,
        "214" => MotionInput.Qcb,
        "623" => MotionInput.Dp,
        _ => MotionInput.None,
    };

    public static string MotionToString(MotionInput m) => m switch
    {
        MotionInput.Qcf => "236",
        MotionInput.Qcb => "214",
        MotionInput.Dp => "623",
        _ => "",
    };

    public static AttackButton ParseButton(string s) => s switch
    {
        "LP" => AttackButton.LP, "MP" => AttackButton.MP, "HP" => AttackButton.HP,
        "LK" => AttackButton.LK, "MK" => AttackButton.MK, "HK" => AttackButton.HK,
        _ => AttackButton.LP,
    };

    private static int RangeLo(int[] r, int fallback) => r != null && r.Length > 0 ? r[0] : fallback;
    private static int RangeHi(int[] r, int fallback) => r != null && r.Length > 1 ? r[1] : fallback;
}
