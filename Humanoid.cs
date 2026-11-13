using Godot;
using System;

// Stage 4 humanoid: just the body (movement, arms, visuals) and the bot's brain outputs.
// The game rules, the shared ball and the scoring live in Match.cs.
// Simulation state lives in plain fields so the training loop never touches the engine;
// SyncVisual() copies it to the nodes once per rendered frame.
public partial class Humanoid : Node3D
{
    public const float MaxSpeed = 4.0f;
    public const float Accel = 8.0f;
    public const float MaxTurn = 3.5f;
    public const float BodyRadius = 0.35f;

    public const int NumInputs = 13;
    public const int NumOutputs = 6;   // thrust, turn, reach, shoot, power, loft

    // --- body state ---
    public Vector3 Pos;
    public float Yaw;
    public float Speed;
    public float Phase;
    public float ArmAmt;       // 0 = arms swinging, 1 = arms stretched out

    // --- bookkeeping managed by Match ---
    public Brain Brain;
    public float Fitness;
    public int Makes;
    public int Shots;
    public int Steals;
    public float NearMiss;     // points earned from near-miss arcs (diagnostics)
    public float GrabLock;     // can't pick up or steal while this is above zero
    public float StealT;       // how long this bot has been trying to take the ball
    public readonly float[] Inputs = new float[NumInputs];
    public float Thrust, Turn, Power, Loft;
    public bool Reach, Shoot;

    // --- visual parts ---
    Node3D upper, legL, legR, armL, armR;
    StandardMaterial3D bodyMat, limbMat;

    public void ResetBody(Vector3 pos, float yaw)
    {
        Pos = pos;
        Yaw = yaw;
        Speed = 0f;
        Phase = 0f;
        ArmAmt = 0f;
        StealT = 0f;
        GrabLock = 0f;
    }

    public void ClearStats()
    {
        Fitness = 0f;
        Makes = 0;
        Shots = 0;
        Steals = 0;
        NearMiss = 0f;
    }

    // Run the neural network on the inputs Match filled in.
    public void Think()
    {
        float[] o = Brain.Forward(Inputs);
        Thrust = o[0];
        Turn = o[1];        // positive = turn right
        Reach = o[2] > 0f;
        Shoot = o[3] > 0.3f;
        Power = o[4];
        Loft = o[5];
    }

    public void Move(float dt, Vector3 center, float limit)
    {
        float target = Thrust >= 0f ? Thrust * MaxSpeed : Thrust * MaxSpeed * 0.3f;
        Speed = Mathf.MoveToward(Speed, target, Accel * dt);
        Yaw -= Turn * MaxTurn * dt;

        float sy = MathF.Sin(Yaw), cy = MathF.Cos(Yaw);
        Pos.X += -sy * Speed * dt;
        Pos.Z += -cy * Speed * dt;
        Pos.X = Mathf.Clamp(Pos.X, center.X - limit, center.X + limit);
        Pos.Z = Mathf.Clamp(Pos.Z, center.Z - limit, center.Z + limit);
        Phase += Speed * dt * 2.4f;
    }

    // Arms: stretched out when reaching, held forward while carrying.
    public void UpdateArms(float dt, bool holding)
    {
        float armTarget = holding ? 0.75f : (Reach ? 1f : 0f);
        ArmAmt = Mathf.MoveToward(ArmAmt, armTarget, 6f * dt);
    }

    // ---------------- visuals ----------------

    public void BuildBody()
    {
        bodyMat = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.5f, 0.9f), Roughness = 0.7f };
        limbMat = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.35f, 0.65f), Roughness = 0.7f };
        var skinMat = new StandardMaterial3D { AlbedoColor = new Color(0.92f, 0.78f, 0.62f), Roughness = 0.8f };
        var visorMat = new StandardMaterial3D { AlbedoColor = new Color(0.08f, 0.08f, 0.1f), Roughness = 0.4f };

        // Upper body pivots at the hips so the torso can lean forward.
        upper = new Node3D { Position = new Vector3(0, 0.8f, 0) };
        AddChild(upper);

        var torso = MakeBox(new Vector3(0.55f, 0.7f, 0.3f), bodyMat);
        torso.Position = new Vector3(0, 0.35f, 0);
        upper.AddChild(torso);

        var head = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.2f, Height = 0.4f },
            MaterialOverride = skinMat,
            Position = new Vector3(0, 0.95f, 0)
        };
        upper.AddChild(head);

        // Visor on the front (-Z) so you can see which way the bot faces.
        var visor = MakeBox(new Vector3(0.28f, 0.1f, 0.1f), visorMat);
        visor.Position = new Vector3(0, 0.97f, -0.17f);
        upper.AddChild(visor);

        armL = MakeLimb(upper, new Vector3(-0.38f, 0.65f, 0), new Vector3(0.14f, 0.6f, 0.14f), limbMat);
        armR = MakeLimb(upper, new Vector3(0.38f, 0.65f, 0), new Vector3(0.14f, 0.6f, 0.14f), limbMat);
        legL = MakeLimb(this, new Vector3(-0.15f, 0.8f, 0), new Vector3(0.2f, 0.8f, 0.22f), limbMat);
        legR = MakeLimb(this, new Vector3(0.15f, 0.8f, 0), new Vector3(0.2f, 0.8f, 0.22f), limbMat);
    }

    public void SetTint(Color c)
    {
        bodyMat.AlbedoColor = c;
        limbMat.AlbedoColor = c.Darkened(0.25f);
    }

    public void SyncVisual()
    {
        Position = Pos;
        Rotation = new Vector3(0, Yaw, 0);

        float speedFrac = Mathf.Clamp(Speed / MaxSpeed, -1f, 1f);
        float swing = MathF.Sin(Phase) * 0.8f * MathF.Abs(speedFrac);

        legL.Rotation = new Vector3(swing, 0, 0);
        legR.Rotation = new Vector3(-swing, 0, 0);

        // Blend between walking arm-swing and arms stretched forward (positive X rotation = forward).
        float reachAngle = 1.5f * ArmAmt;
        float free = 1f - ArmAmt;
        armL.Rotation = new Vector3(-swing * 0.9f * free + reachAngle, 0, 0);
        armR.Rotation = new Vector3(swing * 0.9f * free + reachAngle, 0, 0);

        upper.Rotation = new Vector3(-0.2f * speedFrac, 0, 0);
    }

    static MeshInstance3D MakeBox(Vector3 size, Material mat)
    {
        return new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = mat };
    }

    // A limb is a pivot node at the joint with a box hanging below it.
    static Node3D MakeLimb(Node3D parent, Vector3 jointPos, Vector3 size, Material mat)
    {
        var pivot = new Node3D { Position = jointPos };
        var mesh = MakeBox(size, mat);
        mesh.Position = new Vector3(0, -size.Y / 2f, 0);
        pivot.AddChild(mesh);
        parent.AddChild(pivot);
        return pivot;
    }
}
