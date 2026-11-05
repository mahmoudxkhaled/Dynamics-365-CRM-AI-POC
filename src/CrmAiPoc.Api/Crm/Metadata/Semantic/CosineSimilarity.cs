namespace CrmAiPoc.Api.Crm.Metadata.Semantic;

public static class CosineSimilarity
{
    public static float Compute(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length)
        {
            return 0f;
        }

        double dot = 0;
        double normA = 0;
        double normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var x = a[i];
            var y = b[i];
            dot += x * y;
            normA += x * x;
            normB += y * y;
        }

        if (normA <= double.Epsilon || normB <= double.Epsilon)
        {
            return 0f;
        }

        return (float)(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)));
    }
}
