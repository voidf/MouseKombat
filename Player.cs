using Godot;
using System;

public partial class Player : Node2D // 改名：从 Player1 改为 Player
{
    [Export] public AnimatedSprite2D anim;

    // 【核心改动】将输入的动作名称暴露给编辑器，默认填入 P1 的按键
    [Export] public string ActionLeft = "p1_left";
    [Export] public string ActionRight = "p1_right";
    [Export] public string ActionAttack = "p1_attack";

    public enum PlayerState
    {
        Idle,
        Walk,
        Attack
    }

    private PlayerState currentState = PlayerState.Idle;

    public override void _Ready()
    {
        anim.AnimationFinished += OnAnimationFinished;
    }

    public override void _Process(double delta)
    {
        // 【核心改动】使用变量替代写死的字符串
        if (Input.IsActionJustPressed(ActionAttack) && currentState != PlayerState.Attack)
        {
            StartAttack();
        }

        switch (currentState)
        {
            case PlayerState.Idle:
            case PlayerState.Walk:
                HandleMovement(delta);
                break;
            case PlayerState.Attack:
                break;
        }
    }

    private void HandleMovement(double delta)
    {
        // 【核心改动】使用变量获取移动输入
        bool isMovingLeft = Input.IsActionPressed(ActionLeft);
        bool isMovingRight = Input.IsActionPressed(ActionRight);
        float deltaX = 0f;

        if (isMovingLeft && !isMovingRight)
        {
            deltaX = -1f;
            anim.FlipH = false; // Player1默认朝右，向左走不需要翻转(或者视你的美术素材而定)
            ChangeState(PlayerState.Walk);
        }
        else if (isMovingRight && !isMovingLeft)
        {
            deltaX = 1f;
            anim.FlipH = true; 
            ChangeState(PlayerState.Walk);
        }
        else
        {
            ChangeState(PlayerState.Idle);
        }

        Position += new Vector2((float)delta * deltaX * 400f, 0);
    }

    private void StartAttack()
    {
        ChangeState(PlayerState.Attack);
    }

    private void ChangeState(PlayerState newState)
    {
        if (currentState == newState && newState != PlayerState.Attack) 
            return; 

        currentState = newState;

        switch (currentState)
        {
            case PlayerState.Idle:
                anim.Play("IDLE");
                break;
            case PlayerState.Walk:
                anim.Play("WALK");
                break;
            case PlayerState.Attack:
                anim.Play("ATK");
                break;
        }
    }

    private void OnAnimationFinished()
    {
        if (currentState == PlayerState.Attack && anim.Animation == "ATK")
        {
            ChangeState(PlayerState.Idle);
        }
    }
}