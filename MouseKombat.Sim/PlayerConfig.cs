namespace MouseKombat.Sim;

// Per-character tuning + anim-clip names + start state, captured from the Godot Player's
// [Export] fields at construction (so scene overrides in MFEntry.tscn are honored). All the
// numbers the logic reads live here; the sim holds no Godot dependency.
public sealed class PlayerConfig
{
    public CharacterId Character = CharacterId.Hamster;
    public Vec2 StartPos;             // world spawn position (== node position; identity parent)
    public bool StartFacingRight = true;

    public int MaxHp = 100;
    public Fix WalkSpeedPxPerSec = 220f;
    public Fix DefDamageMultiplier = 0.1f;

    // Corner pushback: when a hit's knockback shoves the defender into a stage wall, the part the
    // wall refuses to absorb is handed BACK to this attacker (it gets pushed away instead). Without
    // it, a fast-startup normal can loop a cornered opponent forever, since the knockback that would
    // normally reset the spacing goes nowhere. 1 = full transfer, 0 = disabled (old behavior).
    // Read off the ATTACKER's config.
    public Fix CornerPushbackScale = 1f;

    public int HurtStunFrames = 14;
    public int DefHitStunFrames = 10;
    public int CrouchEnterFrames = 8;

    public int DownedFrames = 30;
    public int DownedMinFrames = 12;
    public int WakeupFrames = 24;
    public Fix AirResetPop = 350f;

    public Fix JumpVelocity = 1350f;
    public Fix Gravity = 3600f;
    public Fix ForwardJumpSpeed = 420f;
    public Fix BackJumpSpeed = 380f;

    // hurtboxes (local rects, flipped by facing). Defaults mirror Player.cs [Export] defaults
    // so headless/test configs have valid boxes even before a view fills them.
    public SimRect HeadBox = new SimRect(-40, -200, 80, 55);
    public SimRect BodyBox = new SimRect(-55, -150, 110, 95);
    public SimRect ArmsBox = new SimRect(-65, -165, 130, 60);
    public SimRect LegsBox = new SimRect(-45, -70, 90, 70);
    public SimRect CrouchHeadBox = new SimRect(-15, -110, 80, 55);
    public SimRect CrouchBodyBox = new SimRect(-55, -75, 110, 75);
    public SimRect CrouchArmsBox = new SimRect(-60, -90, 120, 55);
    public SimRect CrouchLegsBox = new SimRect(-45, -35, 90, 35);

    // animation clip names the sim references when emitting AnimCommands
    public string IdleAnimName = "IDLE";
    public string WalkAnimName = "WALK";
    public string HurtAnimName = "HURT";
    public string DefAnimName = "DEF";
    public string CrouchDefAnimName = "CROUCHDEF";
    public string JumpAnimName = "JUMP";
    public string EnterCrouchAnimName = "ENTER_CROUCH";
    public string CrouchIdleAnimName = "CROUCH";
    public string LaunchRiseAnimName = "LAUNCH";
    public string FallAnimName = "FALL";
    public string KnockdownAnimName = "KNOCKDOWN";
    public string WakeupAnimName = "WAKEUP";
    public string AirHurtAnimName = "AIR_HURT";

    // Float-friendly setter for callers that live outside the fixed-point world: the Godot view
    // (Player.BuildConfig) and the pythonnet RL bridge, which cannot use Fix's implicit
    // conversions. Values are converted once, here, and never again.
    public void SetStart(float x, float y, bool facingRight)
    {
        StartPos = Vec2.FromFloat(x, y);
        StartFacingRight = facingRight;
    }
}
