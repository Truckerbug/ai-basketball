using Godot;
using System;

// Stage 3 humanoid: fetch the ball, pick it up (the ball flies into the hands), then SHOOT.
// The ball has its own simple physics (gravity, floor bounce, rolling, backboard, invisible
// cell walls) written into the sim loop, so training stays fast.
// A basket counts when the ball passes downward through the rim circle.
// Simulation state lives in plain fields so the training loop never touches the engine;
// SyncVisual() copies it to the nodes once per rendered frame.
public partial class Humanoid : Node3D
{
    // Humanoid and world properties
    public const float MaxSpeed = 4.0f;
    public const float Accel = 8.0f;
    public const float MaxTurn = 3.5f;
    public const float GrabRadius = 1.1f;
    public const float BallRest = 0.25f;     // ball radius, also its resting height
    public const float RimY = 3.05f;
    public const float RimFinal = 0.55f;     // regulation: the ball clears the ring by about one ball radius
    public const float RimTube = 0.04f;      // thickness of the rim iron
    public const float RimBounce = 0.5f;     // bounciness of the rim
    public const float Gravity = 9.8f;
    public const bool RequireReach = true;   // false = auto-grab when close enough
    // Reward values
    public const float MakeBonus = 100f;
    public const float PickupBonus = 10f;
    public const float ShotMaxReward = 25f;  // near-miss reward, scaled by closest approach to the rim

    public const int NumInputs = 9;
    public const int NumOutputs = 6;         // thrust, turn, reach, shoot, power, loft

    // Rim size is shared by all bots and changed by the curriculum between gens
    public static float RimRadius = 1.2f;

    // --- simulation state ---
    public Vector3 Pos;
    public float Yaw;
    public float Speed;
    public float Phase;
    public float ArmAmt;       // 0 = arms swinging, 1 = arms stretched out
    public float Fitness;
    public int Makes;
    public int Shots;
    public int Pickups;
    public bool HasBall;
    public Vector3 BallPos;
    public Vector3 BallVel;
    public Vector3 HoopPos;    // point on the floor under the rim centre
    public Vector3 CellCenter;
    public float CellLimit;
    public Brain Brain;

    Vector2[] ballSeq;
    Vector2[] hoopSeq;
    int roundIdx;
    Vector3 grabStart;
    bool fetched; // only the first fetch of a ball in each round is rewarded; re-fetches earn nothing
    float grabT;
    bool shotActive;
    bool shotVoid;   // the ball rose up through the hoop from below: no basket for this shot
    float shotCrossD;   // distance from the rim centre where a missed shot first drops through rim height (-1 = hasn't yet)
    readonly float[] inputs = new float[NumInputs];

    // --- visual parts ---
    Node3D upper, legL, legR, armL, armR;
    StandardMaterial3D bodyMat, limbMat;

    public void Reset(float startYaw, Vector2[] balls, Vector2[] hoops)
    {
        Pos = CellCenter;
        Yaw = startYaw;
        Speed = 0f;
        Phase = 0f;
        ArmAmt = 0f;
        Fitness = 0f;
        Makes = 0;
        Shots = 0;
        Pickups = 0;
        HasBall = false;
        ballSeq = balls;
        hoopSeq = hoops;
        roundIdx = 0;
        LoadRound();
    }

    // A new ball and a new hoop position (after a basket).
    void LoadRound()
    {
        Vector2 b = ballSeq[roundIdx];
        Vector2 h = hoopSeq[roundIdx];
        BallPos = CellCenter + new Vector3(b.X, BallRest, b.Y);
        BallVel = Vector3.Zero;
        HoopPos = CellCenter + new Vector3(h.X, 0, h.Y);
        HasBall = false;
        shotActive = false;
        fetched = false;
    }

    static void Aim(float dx, float dz, float sy, float cy, out float cosA, out float sinA, out float dist)
    {
        // Godot: yaw 0 faces -Z. forward = (-sin, -cos), right = (cos, -sin).
        float localFwd = dx * -sy + dz * -cy;
        float localRight = dx * cy + dz * -sy;
        float ang = MathF.Atan2(localRight, localFwd); // positive = target on the right
        cosA = MathF.Cos(ang);
        sinA = MathF.Sin(ang);
        dist = MathF.Sqrt(dx * dx + dz * dz);
    }

    bool BallGrounded()
    {
        return BallPos.Y <= BallRest + 0.05f && (BallVel.X * BallVel.X + BallVel.Z * BallVel.Z) < 9f;
    }

    public void Step(float dt)
    {
        float sy = MathF.Sin(Yaw), cy = MathF.Cos(Yaw);

        Aim(BallPos.X - Pos.X, BallPos.Z - Pos.Z, sy, cy, out float bCos, out float bSin, out float bDist);
        Aim(HoopPos.X - Pos.X, HoopPos.Z - Pos.Z, sy, cy, out float hCos, out float hSin, out float hDist);

        inputs[0] = bCos;
        inputs[1] = bSin;
        inputs[2] = MathF.Min(bDist / 10f, 1f);
        inputs[3] = hCos;
        inputs[4] = hSin;
        inputs[5] = MathF.Min(hDist / 10f, 1f);
        inputs[6] = Speed / MaxSpeed;
        inputs[7] = HasBall ? 1f : 0f;
        inputs[8] = (!HasBall && BallPos.Y > BallRest + 0.35f) ? 1f : 0f; // ball is in the air

        float[] o = Brain.Forward(inputs);
        float thrust = o[0];
        float turn = o[1];       // positive = turn right
        bool reach = o[2] > 0f;
        bool shoot = o[3] > 0.3f;
        float power = o[4];
        float loft = o[5];

        float target = thrust >= 0f ? thrust * MaxSpeed : thrust * MaxSpeed * 0.3f;
        Speed = Mathf.MoveToward(Speed, target, Accel * dt);
        Yaw -= turn * MaxTurn * dt;

        sy = MathF.Sin(Yaw);
        cy = MathF.Cos(Yaw);
        Pos.X += -sy * Speed * dt;
        Pos.Z += -cy * Speed * dt;
        Pos.X = Mathf.Clamp(Pos.X, CellCenter.X - CellLimit, CellCenter.X + CellLimit);
        Pos.Z = Mathf.Clamp(Pos.Z, CellCenter.Z - CellLimit, CellCenter.Z + CellLimit);
        Phase += Speed * dt * 2.4f;

        // Arms: stretched out when reaching, held forward while carrying.
        float armTarget = HasBall ? 0.75f : (reach ? 1f : 0f);
        ArmAmt = Mathf.MoveToward(ArmAmt, armTarget, 6f * dt);

        if (!HasBall)
        {
            float ndx = BallPos.X - Pos.X;
            float ndz = BallPos.Z - Pos.Z;
            float newDist = MathF.Sqrt(ndx * ndx + ndz * ndz);

            // Reward progress toward the ball, but only for the first fetch of the round.
            // If chasing a thrown ball paid out, throwing it as far away as possible would be a way to farm reward.
            if (!fetched) Fitness += bDist - newDist;

            // Arms stretched out for no reason is wasted effort.
            if (reach && bDist > 2f) Fitness -= 0.4f * dt;

            if (newDist < GrabRadius && (reach || !RequireReach) && BallGrounded())
            {
                HasBall = true;
                Pickups++;
                if (!fetched)
                {
                    Fitness += PickupBonus;
                    fetched = true;
                }
                grabStart = BallPos;
                grabT = 0f;
                BallVel = Vector3.Zero;
            }
        }

        if (HasBall)
        {
            // The ball flies from the floor into the hands over 0.3 s.
            Vector3 held = new Vector3(Pos.X - sy * 0.6f, 1.05f, Pos.Z - cy * 0.6f);
            grabT += dt;
            float t = MathF.Min(1f, grabT / 0.3f);
            t = t * t * (3f - 2f * t);
            BallPos = grabStart.Lerp(held, t);

            if (shoot && grabT >= 0.3f) Release(sy, cy, power, loft);
        }
        else
        {
            StepBall(dt);
        }
    }

    // Power 4..12 m/s, loft 0.35..1.45 rad, thrown in the direction the bot is facing.
    void Release(float sy, float cy, float power, float loft)
    {
        float v = 4f + 8f * (power * 0.5f + 0.5f);
        float ang = 0.35f + 1.1f * (loft * 0.5f + 0.5f);
        float horiz = MathF.Cos(ang) * v;

        BallPos = new Vector3(Pos.X - sy * 0.5f, 1.9f, Pos.Z - cy * 0.5f);
        BallVel = new Vector3(-sy * horiz, MathF.Sin(ang) * v, -cy * horiz);
        HasBall = false;
        Shots++;
        shotActive = true;
        shotCrossD = -1f;
        shotVoid = false;
        ArmAmt = 1f; // arms fly up with the shot
    }

    // How far from the hoop centre the CENTRE of the ball may pass and still clear the rim iron.
    static float ClearRadius => MathF.Max(0.02f, RimRadius - RimTube - BallRest);

    void EndShot()
    {
        // Near-miss reward, only for shots that arc OVER the rim and come back down:
        // the closer to the rim circle it lands, the more we pay.
        // Flat shots that fly under the rim never drop through rim height, so they earn nothing.
        if (shotCrossD >= 0f && !shotVoid)
        {
            float excess = MathF.Max(0f, shotCrossD - ClearRadius);
            Fitness += ShotMaxReward * Mathf.Clamp(1f - excess / 3f, 0f, 1f);
        }
        shotActive = false;
    }

    void Score()
    {
        Makes++;
        Fitness += MakeBonus;
        shotActive = false;
        roundIdx = (roundIdx + 1) % ballSeq.Length;
        LoadRound();
    }

    // Free-ball physics (not held). Three substeps so a fast ball can't skip through the rim plane.
    void StepBall(float dt)
    {
        bool moving = BallPos.Y > BallRest + 0.001f
            || BallVel.X != 0f || BallVel.Y != 0f || BallVel.Z != 0f;
        if (!moving) return;

        const int sub = 3;
        float h = dt / sub;
        float lim = CellLimit + 0.4f;

        // Backboard faces (front is +Z of the hoop, board hangs behind the rim).
        float frontZ = HoopPos.Z - RimRadius - 0.05f;
        float backZ = frontZ - 0.06f;
        float halfW = RimRadius + 0.5f;
        float clear = ClearRadius;

        for (int s = 0; s < sub; s++)
        {
            Vector3 prev = BallPos;
            BallVel.Y -= Gravity * h;
            BallPos += BallVel * h;

            // Backboard: bounces off both faces.
            bool onBoard = MathF.Abs(BallPos.X - HoopPos.X) <= halfW
                && BallPos.Y > RimY - 0.15f && BallPos.Y < RimY + 0.9f;
            if (onBoard)
            {
                float frontContact = frontZ + BallRest;
                float backContact = backZ - BallRest;
                if (prev.Z > frontContact && BallPos.Z <= frontContact)
                {
                    BallPos.Z = frontContact;
                    BallVel.Z = -BallVel.Z * 0.6f;
                    BallVel.X *= 0.9f;
                }
                else if (prev.Z < backContact && BallPos.Z >= backContact)
                {
                    BallPos.Z = backContact;
                    BallVel.Z = -BallVel.Z * 0.6f;
                    BallVel.X *= 0.9f;
                }
            }

            // Rim iron: a thin ring at rim height around the hoop centre. The ball bounces off it,
            // so it can no longer clip through the ring and still count.
            float rdx = BallPos.X - HoopPos.X;
            float rdz = BallPos.Z - HoopPos.Z;
            float rd = MathF.Sqrt(rdx * rdx + rdz * rdz);
            if (rd > 0.0001f)
            {
                float px = HoopPos.X + rdx / rd * RimRadius;   // nearest point on the ring
                float pz = HoopPos.Z + rdz / rd * RimRadius;
                float nx = BallPos.X - px;
                float ny = BallPos.Y - RimY;
                float nz = BallPos.Z - pz;
                float nl = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                float contact = BallRest + RimTube;
                if (nl < contact && nl > 0.0001f)
                {
                    nx /= nl;
                    ny /= nl;
                    nz /= nl;
                    float vn = BallVel.X * nx + BallVel.Y * ny + BallVel.Z * nz;
                    if (vn < 0f)
                    {
                        float k = -(1f + RimBounce) * vn;
                        BallVel.X += k * nx;
                        BallVel.Y += k * ny;
                        BallVel.Z += k * nz;
                    }
                    BallPos.X = px + nx * contact;
                    BallPos.Y = RimY + ny * contact;
                    BallPos.Z = pz + nz * contact;
                }
            }

            // A ball rising up through the hoop from below voids the shot,
            // so shooting straight up from under the basket doesn't work.
            if (prev.Y < RimY && BallPos.Y >= RimY && BallVel.Y > 0f)
            {
                float tu = (RimY - prev.Y) / (BallPos.Y - prev.Y);
                float ux = prev.X + (BallPos.X - prev.X) * tu - HoopPos.X;
                float uz = prev.Z + (BallPos.Z - prev.Z) * tu - HoopPos.Z;
                if (ux * ux + uz * uz <= clear * clear) shotVoid = true;
            }

            // A basket is a downward crossing of rim height with the ball fully inside the ring.
            if (prev.Y > RimY && BallPos.Y <= RimY && BallVel.Y < 0f)
            {
                float t = (prev.Y - RimY) / (prev.Y - BallPos.Y);
                float cx = prev.X + (BallPos.X - prev.X) * t - HoopPos.X;
                float cz = prev.Z + (BallPos.Z - prev.Z) * t - HoopPos.Z;
                float d2 = cx * cx + cz * cz;
                if (!shotVoid && d2 <= clear * clear)
                {
                    Score();
                    return;
                }
                if (shotActive && shotCrossD < 0f) shotCrossD = MathF.Sqrt(d2);
            }

            // Floor: bounce, then roll to a stop.
            if (BallPos.Y < BallRest)
            {
                BallPos.Y = BallRest;
                if (shotActive) EndShot();
                if (BallVel.Y < -1f)
                {
                    BallVel.Y = -BallVel.Y * 0.6f;
                    BallVel.X *= 0.85f;
                    BallVel.Z *= 0.85f;
                }
                else
                {
                    BallVel.Y = 0f;
                    float k = MathF.Max(0f, 1f - 1.5f * h);
                    BallVel.X *= k;
                    BallVel.Z *= k;
                    if (BallVel.X * BallVel.X + BallVel.Z * BallVel.Z < 0.01f)
                    {
                        BallVel.X = 0f;
                        BallVel.Z = 0f;
                    }
                }
            }

            // Invisible walls around the cell.
            if (BallPos.X > CellCenter.X + lim) { BallPos.X = CellCenter.X + lim; BallVel.X = -BallVel.X * 0.5f; }
            if (BallPos.X < CellCenter.X - lim) { BallPos.X = CellCenter.X - lim; BallVel.X = -BallVel.X * 0.5f; }
            if (BallPos.Z > CellCenter.Z + lim) { BallPos.Z = CellCenter.Z + lim; BallVel.Z = -BallVel.Z * 0.5f; }
            if (BallPos.Z < CellCenter.Z - lim) { BallPos.Z = CellCenter.Z - lim; BallVel.Z = -BallVel.Z * 0.5f; }

        }
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
