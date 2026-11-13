using Godot;
using System;
using System.IO;

// Stage 4: half-court 1v1. Forty bots play twenty games at a time (random pairings every generation).
// Each game: two bots, one ball, one hoop. Score at the hoop, steal the ball, rebound misses.
// The rim starts a bit large and shrinks to regulation as the population gets good enough.
// Controls: Up/Down = sim speed, Tab = overview / follow-one-game camera, Left/Right = pick game,
//           S = save now, E = export population into the project, R = restart from scratch.
public partial class Main : Node3D
{
    const int Cols = 5;
    const int Rows = 4;
    const int Matches = Cols * Rows;
    const int PopSize = Matches * 2;
    const float CellSize = 14f;
    const float EpisodeTime = 40f;
    const float Dt = 1f / 30f;
    const int SaveEvery = 5;

    const string SaveName = "user://population_stage4c.bin";    // 4c = no free near-miss lobs; older 4/4b populations are ignored
    const string SeedPath = "res://saves/population_stage4c.bin";
    // Older stage 3 brains are converted into the bigger stage 4 network as a warm start.
    const string Legacy3cSave = "user://population_stage3c.bin";
    const string Legacy3cSeed = "res://saves/population_stage3c.bin";
    const string Legacy3bSave = "user://population_stage3b.bin";
    const string Legacy3bSeed = "res://saves/population_stage3b.bin";

    // Curriculum: shrink the rim 10% once the average bot scores this many baskets per game (smoothed).
    const float ShrinkAtAvgMakes = 0.6f;
    const int MinGensBetweenShrinks = 5;
    const float StartRimRadius = 0.8f;

    static readonly int[] Layers = { Humanoid.NumInputs, 20, Humanoid.NumOutputs };
    static readonly int[] OldLayers = { 9, 16, 6 };   // stage 3 network
    static readonly float[] SpeedLevels = { 0.25f, 1f, 2f, 4f, 16f, 64f, 256f, 1024f };

    Humanoid[] bots = new Humanoid[PopSize];      // slot 2m and 2m+1 play in match m
    int[] perm = new int[PopSize];                // slot -> genome index for this generation
    Match[] matches = new Match[Matches];
    MeshInstance3D[] balls = new MeshInstance3D[Matches];
    MeshInstance3D[] dots = new MeshInstance3D[Matches];   // marks who has the ball
    Node3D[] hoops = new Node3D[Matches];
    MeshInstance3D[] rims = new MeshInstance3D[Matches];
    MeshInstance3D[] boards = new MeshInstance3D[Matches];
    MeshInstance3D[] poles = new MeshInstance3D[Matches];
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
    float shownRim = -1f;
    int gensSinceShrink;
    float makesEma;

    float lastBest, lastAvg, bestEver, lastAvgMakes, lastAvgNearMiss;
    int lastBestMakes, lastBestShots, lastBestSteals;

    static readonly Color Blue = new Color(0.3f, 0.5f, 0.9f);
    static readonly Color Red = new Color(0.9f, 0.3f, 0.25f);
    static readonly Color Gold = new Color(1.0f, 0.78f, 0.2f);

    string SavePath => ProjectSettings.GlobalizePath(SaveName);

    public override void _Ready()
    {
        BuildEnvironment();
        BuildArena();
        BuildMatches();
        BuildHud();

        evo = new Evolution(PopSize, Brain.GeneCount(Layers), 1234);
        Match.RimRadius = StartRimRadius;
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
        for (int i = 0; i < Matches; i++)
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
        float r = Match.RimRadius;
        shownRim = r;
        float rimScale = r / 0.46f; // the torus mid-radius is 0.46
        for (int i = 0; i < Matches; i++)
        {
            rims[i].Scale = new Vector3(rimScale, 1f, rimScale);
            rims[i].Position = new Vector3(0, Match.RimY, 0);
            boards[i].Scale = new Vector3(2f * r + 1f, 1f, 1f);
            boards[i].Position = new Vector3(0, Match.RimY + 0.375f, -(r + 0.08f));
            poles[i].Position = new Vector3(0, 1.6f, -(r + 0.21f));
        }
    }

    void BuildMatches()
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
        var dotMat = new StandardMaterial3D
        {
            AlbedoColor = Gold,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };

        for (int s = 0; s < PopSize; s++)
        {
            var bot = new Humanoid { Brain = new Brain(Layers) };
            bot.BuildBody();
            bot.SetTint(s % 2 == 0 ? Blue : Red);
            AddChild(bot);
            bots[s] = bot;
        }

        for (int m = 0; m < Matches; m++)
        {
            matches[m] = new Match(bots[2 * m], bots[2 * m + 1], CellCenterOf(m), CellSize / 2f - 0.8f);

            balls[m] = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = Match.BallRest, Height = Match.BallRest * 2f },
                MaterialOverride = ballMat
            };
            AddChild(balls[m]);

            hoops[m] = MakeHoop(m, zoneMat, rimMat, boardMat, poleMat);
            hoops[m].Position = matches[m].HoopPos;
            AddChild(hoops[m]);

            dots[m] = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.18f, Height = 0.36f },
                MaterialOverride = dotMat,
                Visible = false
            };
            AddChild(dots[m]);
        }
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

    void StartGeneration()
    {
        simTime = 0f;

        // Random pairings: shuffle the genomes, slot 2m and 2m+1 play each other.
        var rnd = new Random(generation * 7919 + 13);
        for (int i = 0; i < PopSize; i++) perm[i] = i;
        for (int i = PopSize - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (perm[i], perm[j]) = (perm[j], perm[i]);
        }

        for (int s = 0; s < PopSize; s++)
            bots[s].Brain.Genes = evo.Genomes[perm[s]];
        for (int m = 0; m < Matches; m++)
            matches[m].Reset(generation * 7919 + m * 31 + 13);
    }

    void EndGeneration()
    {
        var fit = new float[PopSize];
        float sum = 0f, best = float.MinValue;
        int bestSlot = 0, totalMakes = 0;
        float totalNear = 0f;
        for (int s = 0; s < PopSize; s++)
        {
            fit[perm[s]] = bots[s].Fitness;
            sum += bots[s].Fitness;
            totalMakes += bots[s].Makes;
            totalNear += bots[s].NearMiss;
            if (bots[s].Fitness > best) { best = bots[s].Fitness; bestSlot = s; }
        }
        lastBest = best;
        lastAvg = sum / PopSize;
        lastAvgMakes = totalMakes / (float)PopSize;
        lastAvgNearMiss = totalNear / PopSize;
        lastBestMakes = bots[bestSlot].Makes;
        lastBestShots = bots[bestSlot].Shots;
        lastBestSteals = bots[bestSlot].Steals;
        if (best > bestEver) bestEver = best;

        // Curriculum: make the rim smaller once the population is comfortable.
        gensSinceShrink++;
        makesEma = 0.8f * makesEma + 0.2f * lastAvgMakes;
        if (makesEma >= ShrinkAtAvgMakes
            && gensSinceShrink >= MinGensBetweenShrinks
            && Match.RimRadius > Match.RimFinal + 0.001f)
        {
            Match.RimRadius = MathF.Max(Match.RimFinal, Match.RimRadius * 0.9f);
            gensSinceShrink = 0;
            makesEma = 0f;
        }

        evo.NextGeneration(fit);
        generation++;
        if (generation % SaveEvery == 0) Save();
        StartGeneration();
    }

    public override void _Process(double delta)
    {
        acc += (float)delta * SpeedLevels[speedIdx];
        int steps = 0;
        while (acc >= Dt && steps < 3000)
        {
            for (int m = 0; m < Matches; m++) matches[m].Step(Dt);
            simTime += Dt;
            acc -= Dt;
            steps++;
            if (simTime >= EpisodeTime) EndGeneration();
        }
        if (steps >= 3000) acc = 0f;

        if (!Mathf.IsEqualApprox(shownRim, Match.RimRadius)) ApplyRimVisuals();

        // Visuals once per frame.
        for (int s = 0; s < PopSize; s++) bots[s].SyncVisual();
        for (int m = 0; m < Matches; m++)
        {
            Match mm = matches[m];
            balls[m].Position = mm.Ball;
            dots[m].Visible = mm.Holder >= 0;
            if (mm.Holder >= 0)
                dots[m].Position = mm.P[mm.Holder].Pos + new Vector3(0, 2.3f, 0);
        }

        if (followCam) UpdateFollowCam((float)delta);
        UpdateHud();
    }

    // ---------------- camera + HUD + input ----------------

    void SetOverviewCam()
    {
        if (followCam) speedIdx = savedSpeedIdx;
        followCam = false;
        cam.Position = new Vector3(0, 60, 12);
        cam.LookAt(Vector3.Zero);
    }

    // Watches one game from a fixed spot at 1x speed. Leaving follow mode restores the old speed.
    void EnterFollowCam()
    {
        followCam = true;
        savedSpeedIdx = speedIdx;
        speedIdx = 1;
        cam.Position = FollowSpot(followIdx);
    }

    Vector3 FollowSpot(int m)
    {
        return matches[m].Center + new Vector3(0, 8f, 11f);
    }

    void UpdateFollowCam(float delta)
    {
        Vector3 desired = FollowSpot(followIdx);
        if (cam.Position.DistanceTo(desired) > 15f)
            cam.Position = desired; // switched game, so snap instead of swooping
        else
            cam.Position = cam.Position.Lerp(desired, 1f - MathF.Exp(-5f * delta));
        cam.LookAt(matches[followIdx].Center + new Vector3(0, 1.2f, -1.5f));
    }

    void UpdateHud()
    {
        string follow = "";
        if (followCam)
        {
            Match mm = matches[followIdx];
            Humanoid a = mm.P[0], b = mm.P[1];
            follow =
                $"Game #{followIdx}:  Blue {mm.Points[0]} - {mm.Points[1]} Red    shot clock {mm.ShotClockLeft:0.0}s\n" +
                $"Blue: fitness {a.Fitness:0}, shots {a.Shots}, steals {a.Steals}    Red: fitness {b.Fitness:0}, shots {b.Shots}, steals {b.Steals}\n";
        }

        hud.Text =
            $"Generation {generation}    Time {simTime:0.0}/{EpisodeTime:0}s    Speed x{SpeedLevels[speedIdx]:0.##}\n" +
            $"Last gen: best {lastBest:0.0}, avg {lastAvg:0.0}; best bot made {lastBestMakes} baskets on {lastBestShots} shots, {lastBestSteals} steals\n" +
            $"Avg baskets per bot: {lastAvgMakes:0.00} (smoothed {makesEma:0.00}, rim shrinks at {ShrinkAtAvgMakes:0.0})    Rim radius {Match.RimRadius:0.00} (regulation {Match.RimFinal:0.00})    Mutation x{evo.MutationScale:0.0}\n" +
            $"Avg near-miss points per bot: {lastAvgNearMiss:0.0}    Best ever: {bestEver:0.0}\n" +
            follow +
            "[Up/Down] speed   [Tab] camera   [Left/Right] pick game   [S] save   [E] export to project   [R] restart";
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
                case Key.Right: followIdx = (followIdx + 1) % Matches; break;
                case Key.Left: followIdx = (followIdx + Matches - 1) % Matches; break;
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
            w.Write(Match.RimRadius);
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

    // Turns a stage 3 genome (9 inputs, 16 hidden) into a stage 4 genome (13 inputs, 20 hidden).
    // The new inputs get zero weights and the new hidden neurons get zero outgoing weights,
    // so the converted bot starts out behaving exactly like the old shooter.
    static float[] ConvertGenome(float[] old, Random r)
    {
        int oi = OldLayers[0], oh = OldLayers[1], oo = OldLayers[2];
        int ni = Layers[0], nh = Layers[1], no = Layers[2];
        var g = new float[Brain.GeneCount(Layers)];
        int pos = 0;

        // Layer 1: hidden neuron h has ni weights, then a bias.
        for (int h = 0; h < nh; h++)
        {
            for (int i = 0; i < ni; i++)
            {
                float w;
                if (h < oh) w = i < oi ? old[h * (oi + 1) + i] : 0f;
                else w = (float)((r.NextDouble() * 2 - 1) * 0.3);
                g[pos++] = w;
            }
            g[pos++] = h < oh ? old[h * (oi + 1) + oi] : 0f;
        }

        // Layer 2: output neuron o has nh weights, then a bias.
        int oldL2 = oh * (oi + 1);
        for (int o = 0; o < no; o++)
        {
            for (int h = 0; h < nh; h++)
                g[pos++] = h < oh ? old[oldL2 + o * (oh + 1) + h] : 0f;
            g[pos++] = old[oldL2 + o * (oh + 1) + oh];
        }
        return g;
    }

    void TryLoad()
    {
        try
        {
            // Prefer the local stage 4 save, then brains shipped with the project, then stage 3 brains.
            string[] candidates =
            {
                SavePath,
                ProjectSettings.GlobalizePath(SeedPath),
                ProjectSettings.GlobalizePath(Legacy3cSave),
                ProjectSettings.GlobalizePath(Legacy3cSeed),
                ProjectSettings.GlobalizePath(Legacy3bSave),
                ProjectSettings.GlobalizePath(Legacy3bSeed)
            };
            string path = null;
            for (int c = 0; c < candidates.Length; c++)
            {
                if (File.Exists(candidates[c])) { path = candidates[c]; break; }
            }
            if (path == null) return;

            using var r = new BinaryReader(File.OpenRead(path));
            int gen = r.ReadInt32();
            int pop = r.ReadInt32();
            int genes = r.ReadInt32();
            float rim = r.ReadSingle();
            int oldGenes = Brain.GeneCount(OldLayers);
            bool convert = genes == oldGenes;
            if (pop != PopSize || (genes != evo.GeneCount && !convert))
            {
                GD.Print("Save file has a different network shape, ignoring it.");
                return;
            }

            var conv = new Random(99);
            for (int i = 0; i < pop; i++)
            {
                var raw = new float[genes];
                for (int g = 0; g < genes; g++) raw[g] = r.ReadSingle();
                evo.Genomes[i] = convert ? ConvertGenome(raw, conv) : raw;
            }

            if (convert)
            {
                generation = 1;
                Match.RimRadius = StartRimRadius;
                GD.Print("Warm-started from stage 3 brains (converted to the stage 4 network).");
            }
            else
            {
                generation = gen;
                Match.RimRadius = rim;
                GD.Print($"Loaded population at generation {gen}, rim radius {rim:0.00}.");
            }
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
        gensSinceShrink = 0;
        makesEma = 0f;
        Match.RimRadius = StartRimRadius;
        bestEver = lastBest = lastAvg = lastAvgMakes = lastAvgNearMiss = 0f;
        lastBestMakes = lastBestShots = lastBestSteals = 0;
        StartGeneration();
    }
}
