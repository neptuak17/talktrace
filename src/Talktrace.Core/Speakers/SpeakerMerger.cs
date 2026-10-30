namespace Talktrace.Core.Speakers;

/// <summary>One speaker group from diarization: how long it speaks, and its voice print.</summary>
/// <param name="Embedding">A speaker embedding of the group's speech, or null if it has too little for one.</param>
internal sealed record VoiceGroup(int Speaker, double Seconds, float[]? Embedding);

/// <summary>
/// Merges diarization's speaker groups by voice (DESIGN.md section 7, step 4). sherpa-onnx's complete-linkage
/// clustering leaves stray embeddings as near-empty groups, and with a fixed count it spends the count on
/// them, so diarization deliberately over-splits and this step decides who is who, as pyannote's own
/// pipeline does.
/// </summary>
internal static class SpeakerMerger
{
    /// <summary>
    /// Groups with at least <paramref name="minSeconds"/> of speech merge while their voices are closer than
    /// <paramref name="threshold"/> (cosine distance). With a <paramref name="count"/>, they then keep merging
    /// down to it, or if there are too few, the most distinct small groups become speakers too. Remaining
    /// small groups join the speaker whose voice is closest.
    /// </summary>
    /// <returns>The speaker, from 0, that each group with a voice print becomes. Groups without one are left out.</returns>
    public static IReadOnlyDictionary<int, int> Merge(
        IReadOnlyList<VoiceGroup> groups, int? count, double threshold, double minSeconds)
    {
        List<VoiceGroup> voiced = groups.Where(g => g.Embedding is not null).ToList();
        if (voiced.Count == 0)
            return new Dictionary<int, int>();

        // If no group has enough speech, the largest still makes a speaker.
        List<VoiceGroup> small = voiced.Where(g => g.Seconds < minSeconds).ToList();
        if (small.Count == voiced.Count)
            small.Remove(voiced.MaxBy(g => g.Seconds)!);
        List<Speaker> speakers = voiced.Except(small).Select(g => new Speaker(g)).ToList();

        while (speakers.Count > 1)
        {
            (int a, int b) = ClosestPair(speakers, (x, y) => x.Distance(y));
            if (speakers[a].Distance(speakers[b]) >= threshold)
                break;
            Join(a, b);
        }

        // Down to the count, weighting by speaking time (Ward's method) so that a small group joins a
        // speaker long before two people who each talk a lot are taken for one.
        while (speakers.Count > Math.Max(count ?? int.MaxValue, 1))
        {
            (int a, int b) = ClosestPair(
                speakers, (x, y) => x.Seconds * y.Seconds / Math.Max(x.Seconds + y.Seconds, 1e-9) * x.Distance(y));
            Join(a, b);
        }

        // Someone who barely spoke may only have a small group; with a count, the most distinct ones are people.
        while (speakers.Count < count && small.Count > 0)
        {
            VoiceGroup distinct = small.MinBy(g => speakers.Max(s => s.Similarity(g.Embedding!)))!;
            small.Remove(distinct);
            speakers.Add(new Speaker(distinct));
        }

        // Decide every small group against the same voices, so the order they're taken in doesn't matter.
        List<(Speaker Target, VoiceGroup Group)> joins =
            small.Select(g => (speakers.MaxBy(s => s.Similarity(g.Embedding!))!, g)).ToList();
        foreach ((Speaker target, VoiceGroup group) in joins)
            target.Absorb(new Speaker(group));

        var mapping = new Dictionary<int, int>();
        for (int i = 0; i < speakers.Count; i++)
            foreach (int member in speakers[i].Members)
                mapping[member] = i;
        return mapping;

        void Join(int a, int b)
        {
            speakers[a].Absorb(speakers[b]);
            speakers.RemoveAt(b);
        }
    }

    private static (int A, int B) ClosestPair(List<Speaker> speakers, Func<Speaker, Speaker, double> cost)
    {
        (int, int) best = (0, 1);
        double lowest = double.MaxValue;
        for (int a = 0; a < speakers.Count; a++)
            for (int b = a + 1; b < speakers.Count; b++)
            {
                double c = cost(speakers[a], speakers[b]);
                if (c < lowest)
                    (best, lowest) = ((a, b), c);
            }
        return best;
    }

    /// <summary>A speaker so far: its groups, and its voice as their embeddings weighted by speaking time.</summary>
    private sealed class Speaker
    {
        public Speaker(VoiceGroup group)
        {
            Members = [group.Speaker];
            Seconds = group.Seconds;
            float[] embedding = group.Embedding!;
            double length = Math.Sqrt(embedding.Sum(x => (double)x * x));
            Voice = embedding.Select(x => length > 0 ? x / length * group.Seconds : 0).ToArray();
        }

        public List<int> Members { get; }
        public double Seconds { get; private set; }
        public double[] Voice { get; }

        public void Absorb(Speaker other)
        {
            Members.AddRange(other.Members);
            Seconds += other.Seconds;
            for (int i = 0; i < Voice.Length; i++)
                Voice[i] += other.Voice[i];
        }

        /// <summary>Cosine distance between the two voices.</summary>
        public double Distance(Speaker other) => 1 - Similarity(other.Voice);

        public double Similarity(IReadOnlyList<float> embedding) => Similarity(embedding.Select(x => (double)x).ToArray());

        public double Similarity(double[] other)
        {
            double dot = 0, a = 0, b = 0;
            for (int i = 0; i < Voice.Length; i++)
            {
                dot += Voice[i] * other[i];
                a += Voice[i] * Voice[i];
                b += other[i] * other[i];
            }
            return a > 0 && b > 0 ? dot / Math.Sqrt(a * b) : 0;
        }
    }
}
