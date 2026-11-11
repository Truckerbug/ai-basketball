using System;

// Small fully-connected neural network.
// Genes are laid out layer by layer; for each neuron: its incoming weights, then its bias.
// All layers use tanh, so outputs are in the range -1..1.
public sealed class Brain
{
    readonly int[] layers;
    readonly float[][] acts;
    public float[] Genes;

    public Brain(int[] layers)
    {
        this.layers = layers;
        acts = new float[layers.Length][];
        for (int i = 0; i < layers.Length; i++)
            acts[i] = new float[layers[i]];
    }

    public static int GeneCount(int[] layers)
    {
        int n = 0;
        for (int i = 1; i < layers.Length; i++)
            n += layers[i] * (layers[i - 1] + 1);
        return n;
    }

    // Returns an internal buffer that is overwritten on the next call.
    public float[] Forward(float[] input)
    {
        Array.Copy(input, acts[0], layers[0]);
        int g = 0;
        for (int l = 1; l < layers.Length; l++)
        {
            float[] prev = acts[l - 1];
            float[] cur = acts[l];
            int nIn = layers[l - 1];
            int nOut = layers[l];
            for (int o = 0; o < nOut; o++)
            {
                float sum = 0f;
                for (int i = 0; i < nIn; i++)
                    sum += Genes[g++] * prev[i];
                sum += Genes[g++];
                cur[o] = MathF.Tanh(sum);
            }
        }
        return acts[layers.Length - 1];
    }
}
