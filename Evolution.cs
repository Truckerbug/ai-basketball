using System;
using System.Collections.Generic;
using System.Linq;

// Plain neuroevolution: keep the best few unchanged, fill the rest with
// mutated copies of parents picked from the top quarter.
// Stagnation boost: every 20 generations, if the best score has not improved by
// at least 3%, mutation strength goes up (max x1.6); once it improves again it relaxes.
public class Evolution
{
    public const int Elites = 4;
    const int Window = 20;

    public readonly int PopSize;
    public readonly int GeneCount;
    public float[][] Genomes;
    public float MutationScale = 1f;

    readonly Random rng;
    readonly List<float> bests = new List<float>();

    public Evolution(int popSize, int geneCount, int seed)
    {
        PopSize = popSize;
        GeneCount = geneCount;
        rng = new Random(seed);
        Genomes = new float[popSize][];
        for (int i = 0; i < popSize; i++)
        {
            Genomes[i] = new float[geneCount];
            for (int g = 0; g < geneCount; g++)
                Genomes[i][g] = Gauss() * 0.5f;
        }
    }

    public void NextGeneration(float[] fitness)
    {
        int[] order = Enumerable.Range(0, PopSize)
            .OrderByDescending(i => fitness[i])
            .ToArray();

        bests.Add(fitness[order[0]]);
        AdaptMutation();

        var next = new float[PopSize][];
        for (int e = 0; e < Elites; e++)
            next[e] = (float[])Genomes[order[e]].Clone();

        int poolSize = Math.Max(Elites, PopSize / 4);
        for (int i = Elites; i < PopSize; i++)
        {
            float[] parent = Genomes[order[rng.Next(poolSize)]];
            float[] child = (float[])parent.Clone();
            Mutate(child);
            next[i] = child;
        }
        Genomes = next;
    }

    void AdaptMutation()
    {
        if (bests.Count < 2 * Window || bests.Count % Window != 0) return;
        float recent = Mean(bests.Count - Window);
        float prev = Mean(bests.Count - 2 * Window);
        if (recent < prev + Math.Abs(prev) * 0.03f)
            MutationScale = Math.Min(MutationScale * 1.15f, 1.6f);
        else
            MutationScale = Math.Max(MutationScale / 1.15f, 1f);
    }

    float Mean(int start)
    {
        float sum = 0f;
        for (int i = start; i < start + Window; i++) sum += bests[i];
        return sum / Window;
    }

    void Mutate(float[] genes)
    {
        float sigma = 0.3f * MutationScale;
        for (int g = 0; g < genes.Length; g++)
        {
            double r = rng.NextDouble();
            if (r < 0.02) genes[g] = Gauss() * 0.5f;     // rare full re-roll
            else if (r < 0.22) genes[g] += Gauss() * sigma; // small nudge
        }
    }

    float Gauss()
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }
}
