// Copied from Katalepsis (Horizen.Ria.Tests/Engine/EngineTestData.cs, commit 9847e4f): the two series helpers the
// indicator tests use. Unchanged.
namespace KatalepsisFT.Analytics.Tests.Statistics;

internal static class EngineTestData
{
    /// <summary>Geometric random walk ending exactly at <paramref name="last"/>.</summary>
    public static List<decimal> Walk(decimal last, int days, double drift, double vol, int seed)
    {
        var random = new Random(seed);
        var logs = new double[days];
        double level = 0d;
        for (int i = 0; i < days; i++)
        {
            double u1 = 1d - random.NextDouble();
            double u2 = random.NextDouble();
            double z = Math.Sqrt(-2d * Math.Log(u1)) * Math.Cos(2d * Math.PI * u2);
            level += drift + vol * z;
            logs[i] = level;
        }

        double shift = Math.Log((double)last) - logs[^1];
        return logs.Select(l => Math.Round((decimal)Math.Exp(l + shift), 8)).ToList();
    }

    /// <summary>Straight line from <paramref name="first"/> to <paramref name="last"/>.</summary>
    public static List<decimal> Line(decimal first, decimal last, int days) =>
        Enumerable.Range(0, days).Select(i => first + (last - first) * i / Math.Max(1, days - 1)).ToList();
}
