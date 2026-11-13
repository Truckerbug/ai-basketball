using Godot;
using System;

// One half-court 1v1 game: two bots, one ball, one hoop.
//
// Rules:
//  - The bot with the ball is on offense. Its opponent plays defense.
//  - A bot picks up a loose ball by reaching (arms out) while facing it and standing close.
//  - A defender who stays close, facing the ball carrier, with arms out for a short time steals the ball.
//  - A bot holding the ball for more than 8 s loses it (shot clock violation).
//  - After a basket, the bot that conceded gets the ball and both bots reset to their starting spots.
//  - A basket counts when the ball drops down through the rim with the ball fully clear of the iron,
//    and it did not rise up through the hoop from below.
// The ball has its own simple physics (gravity, floor bounce, rolling, backboard, rim iron,
// invisible walls) so training runs far faster than real time.
public class Match
{
    public const float BallRest = 0.25f;     // ball radius, also its resting height
    public const float RimY = 3.05f;
    public const float RimFinal = 0.55f;     // regulation rim radius for this ball size
    public const float RimTube = 0.04f;      // thickness of the rim iron
    public const float RimBounce = 0.5f;     // bounciness of the rim
    public const float Gravity = 9.8f;

    public const float GrabRadius = 1.1f;
    public const float StealRadius = 1.0f;
    public const float StealTime = 0.35f;    // seconds a defender must stay on the ball to take it
    public const float HandTime = 0.3f;      // time for the ball to fly into the hands
    public const float ShotClock = 8f;

    public const float MakeBonus = 100f;
    public const float ConcedePenalty = 30f;
    public const float StealBonus = 10f;     // zero-sum: the victim loses the same amount, otherwise two bots farm steals off each other
    public const float ShotMaxReward = 15f;  // near-miss reward for shots that arc over the rim
    public const float MinShotTravel = 3f;   // ...but only if the shot travelled this far sideways (no free lobs straight up)

    // Rim size is shared by all matches and changed by the curriculum between generations.
    public static float RimRadius = 0.8f;
    static float ClearRadius => MathF.Max(0.02f, RimRadius - RimTube - BallRest);

    public readonly Humanoid[] P = new Humanoid[2];
    public readonly int[] Points = new int[2];
    public Vector3 Center;
    public Vector3 HoopPos;
    public float CellLimit;

    public Vector3 Ball;
    public Vector3 BallVel;
    public int Holder = -1;                  // 0 or 1 = that bot holds the ball, -1 = loose
    public float ShotClockLeft => Holder >= 0 ? MathF.Max(0f, ShotClock - possessionT) : ShotClock;

    Vector3 grabStart;
    float grabT;
    float possessionT;
    bool shotActive;
    bool shotVoid;       // the ball rose up through the hoop from below: no basket for this shot
    int shooter;
    float relX, relZ;     // where the current shot was released
    float shotTravel;     // sideways distance from the release point to where it first dropped through rim height
    float shotCrossD;    // distance from the rim centre where a missed shot first drops through rim height (-1 = not yet)
    int tick;
    Random rng = new Random(0);

    public Match(Humanoid a, Humanoid b, Vector3 center, float cellLimit)
    {
        P[0] = a;
        P[1] = b;
        Center = center;
        CellLimit = cellLimit;
        HoopPos = center + new Vector3(0, 0, -4.5f);
    }

    public void Reset(int seed)
    {
        rng = new Random(seed);
        Points[0] = 0;
        Points[1] = 0;
        P[0].ClearStats();
        P[1].ClearStats();
        tick = 0;
        ResetPossession(rng.Next(2));
    }

    // Offense starts at the top of the key with the ball, defense stands between them and the hoop.
    void ResetPossession(int off)
    {
        int def = 1 - off;
        float jx = (float)(rng.NextDouble() * 2 - 1) * 1.5f;
        P[off].ResetBody(Center + new Vector3(jx, 0, 2.5f), 0f);
        P[def].ResetBody(Center + new Vector3(jx * 0.5f, 0, 0f), MathF.PI);

        Holder = off;
        grabT = HandTime;
        possessionT = 0f;
        BallVel = Vector3.Zero;
        shotActive = false;
        Ball = HeldPos(P[off]);
        grabStart = Ball;
    }

    static Vector3 HeldPos(Humanoid b)
    {
        return new Vector3(b.Pos.X - MathF.Sin(b.Yaw) * 0.6f, 1.05f, b.Pos.Z - MathF.Cos(b.Yaw) * 0.6f);
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
        return Ball.Y <= BallRest + 0.05f && (BallVel.X * BallVel.X + BallVel.Z * BallVel.Z) < 9f;
    }

    // ---------------- one simulation step ----------------

    public void Step(float dt)
    {
        tick++;

        // 1) Both bots look at the same snapshot and decide.
        Think(0);
        Think(1);

        // 2) Move, then keep the bodies from overlapping.
        for (int p = 0; p < 2; p++)
        {
            P[p].Move(dt, Center, CellLimit);
            P[p].GrabLock = MathF.Max(0f, P[p].GrabLock - dt);
        }
        SeparateBodies();

        // 3) Ball: held (shoot / steal / shot clock), or loose (pick up / physics).
        if (Holder >= 0)
        {
            StepHeld(dt);
        }
        else
        {
            TryGrab();
            if (Holder < 0) StepBall(dt);
        }

        // 4) Arms, plus a small cost for stretching them out when nothing is near.
        for (int p = 0; p < 2; p++)
        {
            Humanoid b = P[p];
            b.UpdateArms(dt, Holder == p);
            float dx = Ball.X - b.Pos.X;
            float dz = Ball.Z - b.Pos.Z;
            if (b.Reach && dx * dx + dz * dz > 4f) b.Fitness -= 0.4f * dt;
        }
    }

    void Think(int p)
    {
        Humanoid me = P[p];
        Humanoid op = P[1 - p];
        float sy = MathF.Sin(me.Yaw), cy = MathF.Cos(me.Yaw);

        Aim(Ball.X - me.Pos.X, Ball.Z - me.Pos.Z, sy, cy, out float bCos, out float bSin, out float bDist);
        Aim(HoopPos.X - me.Pos.X, HoopPos.Z - me.Pos.Z, sy, cy, out float hCos, out float hSin, out float hDist);
        Aim(op.Pos.X - me.Pos.X, op.Pos.Z - me.Pos.Z, sy, cy, out float oCos, out float oSin, out float oDist);

        float[] x = me.Inputs;
        x[0] = bCos;
        x[1] = bSin;
        x[2] = MathF.Min(bDist / 10f, 1f);
        x[3] = hCos;
        x[4] = hSin;
        x[5] = MathF.Min(hDist / 10f, 1f);
        x[6] = me.Speed / Humanoid.MaxSpeed;
        x[7] = Holder == p ? 1f : 0f;
        x[8] = (Holder < 0 && Ball.Y > BallRest + 0.35f) ? 1f : 0f;   // ball is in the air
        x[9] = oCos;
        x[10] = oSin;
        x[11] = MathF.Min(oDist / 10f, 1f);
        x[12] = Holder == 1 - p ? 1f : 0f;                           // opponent has the ball

        me.Think();
    }

    void SeparateBodies()
    {
        float dx = P[1].Pos.X - P[0].Pos.X;
        float dz = P[1].Pos.Z - P[0].Pos.Z;
        float d = MathF.Sqrt(dx * dx + dz * dz);
        float min = 2f * Humanoid.BodyRadius;
        if (d >= min) return;

        if (d < 0.0001f)
        {
            dx = 1f;
            dz = 0f;
            d = 1f;
        }
        float push = (min - d) * 0.5f;
        float nx = dx / d, nz = dz / d;
        P[0].Pos.X -= nx * push;
        P[0].Pos.Z -= nz * push;
        P[1].Pos.X += nx * push;
        P[1].Pos.Z += nz * push;
        for (int p = 0; p < 2; p++)
        {
            P[p].Pos.X = Mathf.Clamp(P[p].Pos.X, Center.X - CellLimit, Center.X + CellLimit);
            P[p].Pos.Z = Mathf.Clamp(P[p].Pos.Z, Center.Z - CellLimit, Center.Z + CellLimit);
        }
    }

    // ---------------- loose ball: pick up ----------------

    void TryGrab()
    {
        if (!BallGrounded()) return;
        for (int k = 0; k < 2; k++)
        {
            int p = (tick + k) & 1;   // alternate who gets first chance
            Humanoid b = P[p];
            if (!b.Reach || b.GrabLock > 0f) continue;

            Aim(Ball.X - b.Pos.X, Ball.Z - b.Pos.Z, MathF.Sin(b.Yaw), MathF.Cos(b.Yaw), out float bCos, out _, out float bDist);
            if (bDist < GrabRadius && bCos > 0.5f)   // close enough and facing the ball
            {
                Holder = p;
                grabStart = Ball;
                grabT = 0f;
                possessionT = 0f;
                BallVel = Vector3.Zero;
                shotActive = false;
                return;
            }
        }
    }

    // ---------------- held ball: steal, shoot, shot clock ----------------

    void StepHeld(float dt)
    {
        Humanoid h = P[Holder];
        Humanoid d = P[1 - Holder];

        // The ball flies from where it was into the hands over HandTime.
        grabT += dt;
        possessionT += dt;
        float t = MathF.Min(1f, grabT / HandTime);
        t = t * t * (3f - 2f * t);
        Ball = grabStart.Lerp(HeldPos(h), t);

        // Steal: the defender stays close, facing the ball, arms out.
        bool stealing = d.Reach && d.GrabLock <= 0f && grabT >= HandTime;
        if (stealing)
        {
            Aim(Ball.X - d.Pos.X, Ball.Z - d.Pos.Z, MathF.Sin(d.Yaw), MathF.Cos(d.Yaw), out float dCos, out _, out float dDist);
            stealing = dDist < StealRadius && dCos > 0.5f;
        }
        d.StealT = stealing ? d.StealT + dt : 0f;
        if (d.StealT >= StealTime)
        {
            int newHolder = 1 - Holder;
            P[Holder].GrabLock = 0.8f;   // the victim can't immediately take it back
            d.StealT = 0f;
            d.Fitness += StealBonus;
            h.Fitness -= StealBonus;
            d.Steals++;
            Holder = newHolder;
            grabStart = Ball;
            grabT = 0f;
            possessionT = 0f;
            return;
        }

        // Shoot.
        if (h.Shoot && grabT >= HandTime)
        {
            Release(Holder);
            return;
        }

        // Shot clock violation: turnover.
        if (possessionT > ShotClock)
        {
            h.Fitness -= 5f;
            ResetPossession(1 - Holder);
        }
    }

    // Power 4..12 m/s, loft 0.35..1.45 rad, thrown in the direction the bot is facing.
    void Release(int p)
    {
        Humanoid b = P[p];
        float sy = MathF.Sin(b.Yaw), cy = MathF.Cos(b.Yaw);
        float v = 4f + 8f * (b.Power * 0.5f + 0.5f);
        float ang = 0.35f + 1.1f * (b.Loft * 0.5f + 0.5f);
        float horiz = MathF.Cos(ang) * v;

        Ball = new Vector3(b.Pos.X - sy * 0.5f, 1.9f, b.Pos.Z - cy * 0.5f);
        BallVel = new Vector3(-sy * horiz, MathF.Sin(ang) * v, -cy * horiz);
        Holder = -1;
        shooter = p;
        shotActive = true;
        shotVoid = false;
        shotCrossD = -1f;
        shotTravel = 0f;
        relX = Ball.X;
        relZ = Ball.Z;
        b.Shots++;
        b.ArmAmt = 1f;       // arms fly up with the shot
        b.GrabLock = 0.4f;
    }

    void EndShot()
    {
        // Near-miss reward, only for shots that arc OVER the rim and come back down:
        // the closer to the rim circle it lands, the more we pay.
        // Flat shots under the rim never drop through rim height, so they earn nothing.
        if (shotCrossD >= 0f && !shotVoid && shotTravel >= MinShotTravel)
        {
            float excess = MathF.Max(0f, shotCrossD - ClearRadius);
            float r = ShotMaxReward * Mathf.Clamp(1f - excess / 2.5f, 0f, 1f);
            P[shooter].Fitness += r;
            P[shooter].NearMiss += r;
        }
        shotActive = false;
    }

    void Score()
    {
        P[shooter].Makes++;
        P[shooter].Fitness += MakeBonus;
        P[1 - shooter].Fitness -= ConcedePenalty;
        Points[shooter]++;
        shotActive = false;
        ResetPossession(1 - shooter);   // whoever conceded gets the ball
    }

    // ---------------- loose ball physics ----------------

    // Three substeps so a fast ball can't skip through the rim plane.
    void StepBall(float dt)
    {
        bool moving = Ball.Y > BallRest + 0.001f
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
            Vector3 prev = Ball;
            BallVel.Y -= Gravity * h;
            Ball += BallVel * h;

            // Backboard: bounces off both faces.
            bool onBoard = MathF.Abs(Ball.X - HoopPos.X) <= halfW
                && Ball.Y > RimY - 0.15f && Ball.Y < RimY + 0.9f;
            if (onBoard)
            {
                float frontContact = frontZ + BallRest;
                float backContact = backZ - BallRest;
                if (prev.Z > frontContact && Ball.Z <= frontContact)
                {
                    Ball.Z = frontContact;
                    BallVel.Z = -BallVel.Z * 0.6f;
                    BallVel.X *= 0.9f;
                }
                else if (prev.Z < backContact && Ball.Z >= backContact)
                {
                    Ball.Z = backContact;
                    BallVel.Z = -BallVel.Z * 0.6f;
                    BallVel.X *= 0.9f;
                }
            }

            // Rim iron: a thin ring at rim height around the hoop centre. The ball bounces off it.
            float rdx = Ball.X - HoopPos.X;
            float rdz = Ball.Z - HoopPos.Z;
            float rd = MathF.Sqrt(rdx * rdx + rdz * rdz);
            if (rd > 0.0001f)
            {
                float px = HoopPos.X + rdx / rd * RimRadius;   // nearest point on the ring
                float pz = HoopPos.Z + rdz / rd * RimRadius;
                float nx = Ball.X - px;
                float ny = Ball.Y - RimY;
                float nz = Ball.Z - pz;
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
                    Ball.X = px + nx * contact;
                    Ball.Y = RimY + ny * contact;
                    Ball.Z = pz + nz * contact;
                }
            }

            // A ball rising up through the hoop from below voids the shot.
            if (prev.Y < RimY && Ball.Y >= RimY && BallVel.Y > 0f)
            {
                float tu = (RimY - prev.Y) / (Ball.Y - prev.Y);
                float ux = prev.X + (Ball.X - prev.X) * tu - HoopPos.X;
                float uz = prev.Z + (Ball.Z - prev.Z) * tu - HoopPos.Z;
                if (ux * ux + uz * uz <= clear * clear) shotVoid = true;
            }

            // A basket is a downward crossing of rim height with the ball fully inside the ring.
            if (prev.Y > RimY && Ball.Y <= RimY && BallVel.Y < 0f)
            {
                float t = (prev.Y - RimY) / (prev.Y - Ball.Y);
                float cx = prev.X + (Ball.X - prev.X) * t - HoopPos.X;
                float cz = prev.Z + (Ball.Z - prev.Z) * t - HoopPos.Z;
                float d2 = cx * cx + cz * cz;
                if (shotActive && !shotVoid && d2 <= clear * clear)
                {
                    Score();
                    return;
                }
                if (shotActive && shotCrossD < 0f)
                {
                    shotCrossD = MathF.Sqrt(d2);
                    float tx = cx + HoopPos.X - relX;
                    float tz = cz + HoopPos.Z - relZ;
                    shotTravel = MathF.Sqrt(tx * tx + tz * tz);
                }
            }

            // Floor: bounce, then roll to a stop.
            if (Ball.Y < BallRest)
            {
                Ball.Y = BallRest;
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
            if (Ball.X > Center.X + lim) { Ball.X = Center.X + lim; BallVel.X = -BallVel.X * 0.5f; }
            if (Ball.X < Center.X - lim) { Ball.X = Center.X - lim; BallVel.X = -BallVel.X * 0.5f; }
            if (Ball.Z > Center.Z + lim) { Ball.Z = Center.Z + lim; BallVel.Z = -BallVel.Z * 0.5f; }
            if (Ball.Z < Center.Z - lim) { Ball.Z = Center.Z - lim; BallVel.Z = -BallVel.Z * 0.5f; }
        }
    }
}
