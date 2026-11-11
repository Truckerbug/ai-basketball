using Godot;
using System;
using System.IO;

// Stage 3: 40 humanoid bots, each in its own cell, evolving to fetch a ball and SHOOT baskets.
// The rim starts huge and shrinks toward regulation as the population gets good enough.
// Controls: Up/Down = sim speed, Tab = overview / follow-a-bot camera, Left/Right = pick bot,
//           S = save now, R = restart from scratch (deletes the save).
public partial class Main : Node3D
{
    const int Cols = 8;
    const int Rows = 5;
    const int PopSize = Cols * Rows;
    const float CellSize = 12f;
    const float EpisodeTime = 25f;
    const float Dt = 1f / 30f;
    const int SaveEvery = 5;
    const string SaveName = "user://population_stage3c.bin";   // 3c = real rim collisions + strict basket rules
    const string SeedPath = "res://saves/population_stage3c.bin";
    // Older 3b brains (lenient rim) are still worth keeping: they load as a warm start with the rim reset.
    const string LegacySaveName = "user://population_stage3b.bin";
    const string LegacySeedPath = "res://saves/population_stage3b.bin"; // brains shipped with the project (see [E])

    // Curriculum: shrink the rim 10% once the average bot makes this many baskets per generation.
    const float ShrinkAtAvgMakes = 1.0f;   // compared against a smoothed average, so one lucky generation can't trigger it
    const int MinGensBetweenShrinks = 5;
    const float StartRimRadius = 1.2f;

    static readonly int[] Layers = { Humanoid.NumInputs, 16, Humanoid.NumOutputs };
    static readonly float[] SpeedLevels = { 0.25f, 1f, 2f, 4f, 16f, 64f, 256f, 1024f };

    Humanoid[] bots = new Humanoid[PopSize];
    MeshInstance3D[] balls = new MeshInstance3D[PopSize];
    Node3D[] hoops = new Node3D[PopSize];
    MeshInstance3D[] rims = new MeshInstance3D[PopSize];
    MeshInstance3D[] boards = new MeshInstance3D[PopSize];
    MeshInstance3D[] poles = new MeshInstance3D[PopSize];
    MeshInstance3D leaderDot;
    Camera3D cam;
    Label hud;
    Evolution evo;

    int generation = 1;
    float simTime;
    float acc;
    int speedIdx = 3;
    bool followCam;
    int followIdx;
    int savedSpeedIdx;
    bool showElites;
    float shownRim = -1f;
    int gensSinceShrink;
    float makesEma;

    float lastBest, lastAvg, bestEver, lastAvgMakes;
    int lastBestMakes, lastBestShots;
    int leaderIdx;

    static readonly Color Blue = new Color(0.3f, 0.5f, 0.9f);
    static readonly Color Gold = new Color(1.0f, 0.78f, 0.2f);

    string SavePath => ProjectSettings.GlobalizePath(SaveName);

    public override void _Ready()
    {
        BuildEnvironment();
        BuildArena();
        BuildBots();
        BuildHud();

        evo = new Evolution(PopSize, Brain.GeneCount(Layers), 1234);
        Humanoid.RimRadius = StartRimRadius;
        TryLoad();
        ApplyRimVisuals();
        StartGeneration();
        SetOverviewCam();
    }

    // ---------------- scene building ----------------

    Vector3 CellCenterOf(int i)
    {
        int c = i % Cols, r = i / Cols;
        return new Vector3((c - (Cols - 1) / 2f) * CellSize, 0, (r - (Rows - 1) / 2f) * CellSize);
    }

    void BuildEnvironment()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.06f, 0.07f, 0.09f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.8f, 0.85f, 1f),
            AmbientLightEnergy = 0.5f
        };
        AddChild(new WorldEnvironment { Environment = env });
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-60, 30, 0), LightEnergy = 1.0f });

        cam = new Camera3D { Fov = 60f };
        AddChild(cam);
        cam.Current = true;
    }

    void BuildArena()
    {
        var matA = new StandardMaterial3D { AlbedoColor = new Color(0.17f, 0.19f, 0.22f), Roughness = 1f };
        var matB = new StandardMaterial3D { AlbedoColor = new Color(0.21f, 0.23f, 0.27f), Roughness = 1f };
        for (int i = 0; i < PopSize; i++)
        {
            int c = i % Cols, r = i / Cols;
            var floor = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(CellSize - 0.4f, 0.2f, CellSize - 0.4f) },
                MaterialOverride = ((r + c) % 2 == 0) ? matA : matB,
                Position = CellCenterOf(i) + new Vector3(0, -0.1f, 0)
            };
            AddChild(floor);
        }
    }

    // The rim, backboard and pole are resized by ApplyRimVisuals() to match the current rim radius.
    Node3D MakeHoop(int i, Material zoneMat, Material rimMat, Material boardMat, Material poleMat)
    {
        var hoop = new Node3D();

        // Marker on the floor under the basket.
        hoop.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.5f, BottomRadius = 0.5f, Height = 0.04f },
            MaterialOverride = zoneMat,
            Position = new Vector3(0, 0.03f, 0)
        });

        poles[i] = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.08f, BottomRadius = 0.08f, Height = 3.2f },
            MaterialOverride = poleMat
        };
        boards[i] = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1f, 1.05f, 0.06f) },
            MaterialOverride = boardMat
        };
        rims[i] = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = 0.42f, OuterRadius = 0.5f },
            MaterialOverride = rimMat
        };
        hoop.AddChild(poles[i]);
        hoop.AddChild(boards[i]);
        hoop.AddChild(rims[i]);
        return hoop;
    }

    void ApplyRimVisuals()
    {
        float r = Humanoid.RimRadius;
        shownRim = r;
        float rimScale = r / 0.46f; // the torus mid-radius is 0.46
        for (int i = 0; i < PopSize; i++)
        {
            rims[i].Scale = new Vector3(rimScale, 1f, rimScale);
            rims[i].Position = new Vector3(0, Humanoid.RimY, 0);
            boards[i].Scale = new Vector3(2f * r + 1f, 1f, 1f);
            boards[i].Position = new Vector3(0, Humanoid.RimY + 0.375f, -(r + 0.08f));
            poles[i].Position = new Vector3(0, 1.6f, -(r + 0.21f));
        }
    }

    void BuildBots()
    {
        var orange = new Color(0.95f, 0.4f, 0.1f);
        var zoneMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.85f, 0.2f, 0.12f),
            EmissionEnabled = true,
            Emission = new Color(0.85f, 0.2f, 0.12f),
            EmissionEnergyMultiplier = 0.8f
        };
        var rimMat = new StandardMaterial3D
        {
            AlbedoColor = orange,
            EmissionEnabled = true,
            Emission = orange,
            EmissionEnergyMultiplier = 1.0f
        };
        var boardMat = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.9f, 0.95f), Roughness = 0.5f };
        var poleMat = new StandardMaterial3D { AlbedoColor = new Color(0.5f, 0.5f, 0.55f), Roughness = 0.6f };
        var ballMat = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.5f, 0.12f), Roughness = 0.8f };

        for (int i = 0; i < PopSize; i++)
        {
            var bot = new Humanoid
            {
                CellCenter = CellCenterOf(i),
                CellLimit = CellSize / 2f - 0.8f,
                Brain = new Brain(Layers)
            };
            bot.BuildBody();
            AddChild(bot);
            bots[i] = bot;

            balls[i] = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = Humanoid.BallRest, Height = Humanoid.BallRest * 2f },
                MaterialOverride = ballMat
            };
            AddChild(balls[i]);

            hoops[i] = MakeHoop(i, zoneMat, rimMat, boardMat, poleMat);
            AddChild(hoops[i]);
        }

        leaderDot = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.18f, Height = 0.36f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = Gold,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            }
        };
        AddChild(leaderDot);
    }

    void BuildHud()
    {
        var layer = new CanvasLayer();
        AddChild(layer);
        hud = new Label { Position = new Vector2(14, 10) };
        hud.AddThemeFontSizeOverride("font_size", 20);
        hud.AddThemeColorOverride("font_color", Colors.White);
        hud.AddThemeColorOverride("font_shadow_color", Colors.Black);
        hud.AddThemeConstantOverride("shadow_offset_x", 1);
        hud.AddThemeConstantOverride("shadow_offset_y", 1);
        layer.AddChild(hud);
    }

    // ---------------- training loop ----------------

    // Every bot gets the same ball/hoop sequence in a generation, so scores are fair.
    // Each ball is at least 4 m from the previous hoop; each hoop at least 4 m from its ball.
    void MakeSequences(int seed, out Vector2[] ballSeq, out Vector2[] hoopSeq)
    {
        var r = new Random(seed);
        float range = CellSize / 2f - 1.6f;
        ballSeq = new Vector2[40];
        hoopSeq = new Vector2[40];
        Vector2 prevHoop = Vector2.Zero;
        for (int i = 0; i < ballSeq.Length; i++)
        {
            ballSeq[i] = RandomFar(r, range, prevHoop);
            hoopSeq[i] = RandomFar(r, range, ballSeq[i]);
            prevHoop = hoopSeq[i];
        }
    }

    static Vector2 RandomFar(Random r, float range, Vector2 from)
    {
        Vector2 p;
        int tries = 0;
        do
        {
            p = new Vector2((float)(r.NextDouble() * 2 - 1), (float)(r.NextDouble() * 2 - 1)) * range;
            tries++;
        } while (p.DistanceTo(from) < 4f && tries < 50);
        return p;
    }

    void StartGeneration()
    {
        simTime = 0f;
        int seed = generation * 7919 + 13;
        MakeSequences(seed, out Vector2[] ballSeq, out Vector2[] hoopSeq);
        float yaw = (float)(new Random(seed + 1).NextDouble() * Math.PI * 2);

        for (int i = 0; i < PopSize; i++)
        {
            bots[i].Brain.Genes = evo.Genomes[i];
            bots[i].Reset(yaw, ballSeq, hoopSeq);
            bots[i].SetTint(showElites && i < Evolution.Elites ? Gold : Blue);
        }
    }

    void EndGeneration()
    {
        var fit = new float[PopSize];
        float sum = 0f, best = float.MinValue;
        int bestIdx = 0, totalMakes = 0;
        for (int i = 0; i < PopSize; i++)
        {
            fit[i] = bots[i].Fitness;
            sum += fit[i];
            totalMakes += bots[i].Makes;
            if (fit[i] > best) { best = fit[i]; bestIdx = i; }
        }
        lastBest = best;
        lastAvg = sum / PopSize;
        lastAvgMakes = totalMakes / (float)PopSize;
        lastBestMakes = bots[bestIdx].Makes;
        lastBestShots = bots[bestIdx].Shots;
        if (best > bestEver) bestEver = best;

        // Curriculum: make the rim smaller once the population is comfortable.
        gensSinceShrink++;
        makesEma = 0.8f * makesEma + 0.2f * lastAvgMakes;
        if (makesEma >= ShrinkAtAvgMakes
            && gensSinceShrink >= MinGensBetweenShrinks
            && Humanoid.RimRadius > Humanoid.RimFinal + 0.001f)
        {
            Humanoid.RimRadius = MathF.Max(Humanoid.RimFinal, Humanoid.RimRadius * 0.9f);
            gensSinceShrink = 0;
            makesEma = 0f;
        }

        evo.NextGeneration(fit);
        generation++;
        showElites = true;
        if (generation % SaveEvery == 0) Save();
        StartGeneration();
    }

    public override void _Process(double delta)
    {
        acc += (float)delta * SpeedLevels[speedIdx];
        int steps = 0;
        while (acc >= Dt && steps < 3000)
        {
            for (int i = 0; i < PopSize; i++) bots[i].Step(Dt);
            simTime += Dt;
            acc -= Dt;
            steps++;
            if (simTime >= EpisodeTime) EndGeneration();
        }
        if (steps >= 3000) acc = 0f;

        if (!Mathf.IsEqualApprox(shownRim, Humanoid.RimRadius)) ApplyRimVisuals();

        // Visuals once per frame.
        float lead = float.MinValue;
        for (int i = 0; i < PopSize; i++)
        {
            bots[i].SyncVisual();
            balls[i].Position = bots[i].BallPos;
            hoops[i].Position = bots[i].HoopPos;
            if (bots[i].Fitness > lead) { lead = bots[i].Fitness; leaderIdx = i; }
        }
        leaderDot.Position = bots[leaderIdx].Pos + new Vector3(0, 2.3f, 0);

        if (followCam) UpdateFollowCam((float)delta);
        UpdateHud(lead);
    }

    // ---------------- camera + HUD + input ----------------

    void SetOverviewCam()
    {
        if (followCam) speedIdx = savedSpeedIdx;
        followCam = false;
        cam.Position = new Vector3(0, 62, 14);
        cam.LookAt(Vector3.Zero);
    }

    // Follows one fixed bot (not the live leader, which changes every few frames),
    // and drops to 1x speed so you can actually watch it. Leaving follow mode restores the old speed.
    void EnterFollowCam()
    {
        followCam = true;
        savedSpeedIdx = speedIdx;
        speedIdx = 1;
        cam.Position = FollowSpot(bots[followIdx]);
    }

    static Vector3 FollowSpot(Humanoid b)
    {
        float a = b.Yaw + 0.7f; // a bit off to the side so the arms and ball are visible
        return b.Pos + new Vector3(MathF.Sin(a) * 5.5f, 3.2f, MathF.Cos(a) * 5.5f);
    }

    void UpdateFollowCam(float delta)
    {
        Humanoid b = bots[followIdx];
        Vector3 desired = FollowSpot(b);
        if (cam.Position.DistanceTo(desired) > 15f)
            cam.Position = desired; // bot was reset or switched, so snap instead of swooping
        else
            cam.Position = cam.Position.Lerp(desired, 1f - MathF.Exp(-5f * delta));
        cam.LookAt(b.Pos + new Vector3(0, 1.1f, 0));
    }

    void UpdateHud(float leaderFit)
    {
        hud.Text =
            $"Generation {generation}    Time {simTime:0.0}/{EpisodeTime:0}s    Speed x{SpeedLevels[speedIdx]:0.##}\n" +
            $"Last gen: best {lastBest:0.0}, avg {lastAvg:0.0}, best bot made {lastBestMakes} baskets on {lastBestShots} shots\n" +
            $"Avg baskets per bot: {lastAvgMakes:0.00} (smoothed {makesEma:0.00}, rim shrinks at {ShrinkAtAvgMakes:0.0})    Rim radius {Humanoid.RimRadius:0.00} (regulation {Humanoid.RimFinal:0.00})    Mutation x{evo.MutationScale:0.0}\n" +
            $"Best ever: {bestEver:0.0}    Live leader: {leaderFit:0.0} ({bots[leaderIdx].Makes} baskets)\n" +
            (followCam
                ? $"Following bot #{followIdx} (gold = last gen's best)   pickups {bots[followIdx].Pickups}   shots {bots[followIdx].Shots}   baskets {bots[followIdx].Makes}   carrying: {(bots[followIdx].HasBall ? "yes" : "no")}\n"
                : "") +
            "[Up/Down] speed   [Tab] camera   [Left/Right] pick bot   [S] save   [E] export to project   [R] restart";
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey k && k.Pressed && !k.Echo)
        {
            switch (k.Keycode)
            {
                case Key.Up: speedIdx = Math.Min(speedIdx + 1, SpeedLevels.Length - 1); break;
                case Key.Down: speedIdx = Math.Max(speedIdx - 1, 0); break;
                case Key.Tab:
                    if (followCam) SetOverviewCam(); else EnterFollowCam();
                    break;
                case Key.Right: followIdx = (followIdx + 1) % PopSize; break;
                case Key.Left: followIdx = (followIdx + PopSize - 1) % PopSize; break;
                case Key.S: Save(); break;
                case Key.E: ExportToProject(); break;
                case Key.R: Restart(); break;
            }
        }
    }

    // ---------------- save / load ----------------

    // With no argument, saves to the local user:// location; the [E] key passes a path inside the project.
    void Save(string path = null)
    {
        try
        {
            using var w = new BinaryWriter(File.Create(path ?? SavePath));
            w.Write(generation);
            w.Write(PopSize);
            w.Write(evo.GeneCount);
            w.Write(Humanoid.RimRadius);
            foreach (float[] genome in evo.Genomes)
                foreach (float f in genome)
                    w.Write(f);
        }
        catch (Exception e)
        {
            GD.PrintErr("Save failed: " + e.Message);
        }
    }

    // Writes the current population into <project>/saves/ so it can be committed to git
    // and picked up on another machine. (Works when running from the editor.)
    void ExportToProject()
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(SeedPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Save(path);
            GD.Print("Exported population to " + path);
        }
        catch (Exception e)
        {
            GD.PrintErr("Export failed: " + e.Message);
        }
    }

    void TryLoad()
    {
        try
        {
            // Prefer the local save, then brains shipped with the project, then older 3b brains as a warm start.
            string path = null;
            bool legacy = false;
            string[] candidates =
            {
                SavePath,
                ProjectSettings.GlobalizePath(SeedPath),
                ProjectSettings.GlobalizePath(LegacySaveName),
                ProjectSettings.GlobalizePath(LegacySeedPath)
            };
            for (int c = 0; c < candidates.Length; c++)
            {
                if (File.Exists(candidates[c]))
                {
                    path = candidates[c];
                    legacy = c >= 2;
                    break;
                }
            }
            if (path == null) return;
            using var r = new BinaryReader(File.OpenRead(path));
            int gen = r.ReadInt32();
            int pop = r.ReadInt32();
            int genes = r.ReadInt32();
            float rim = r.ReadSingle();
            if (pop != PopSize || genes != evo.GeneCount)
            {
                GD.Print("Save file has a different network shape, ignoring it.");
                return;
            }
            for (int i = 0; i < pop; i++)
                for (int g = 0; g < genes; g++)
                    evo.Genomes[i][g] = r.ReadSingle();
            generation = gen;
            Humanoid.RimRadius = legacy ? StartRimRadius : rim;
            showElites = true;
            GD.Print(legacy
                ? $"Warm-started from older brains (generation {gen}); rim reset to {StartRimRadius:0.00}."
                : $"Loaded population at generation {gen}, rim radius {rim:0.00}.");
        }
        catch (Exception e)
        {
            GD.PrintErr("Load failed: " + e.Message);
        }
    }

    void Restart()
    {
        try { if (File.Exists(SavePath)) File.Delete(SavePath); } catch (Exception) { }
        evo = new Evolution(PopSize, Brain.GeneCount(Layers), System.Environment.TickCount);
        generation = 1;
        showElites = false;
        gensSinceShrink = 0;
        makesEma = 0f;
        Humanoid.RimRadius = StartRimRadius;
        bestEver = lastBest = lastAvg = lastAvgMakes = 0f;
        lastBestMakes = lastBestShots = 0;
        StartGeneration();
    }
}
