namespace SpaceBattle;

/// <summary>有序样本的线性插值百分位计算。</summary>
internal static class PercentileMath
{
    /// <summary>返回有序数组在给定百分位（0..1）处的线性插值。</summary>
    public static double Percentile(double[] ordered, double percentile)
    {
        var position = (ordered.Length - 1) * percentile;
        var lower = (int)position;
        var upper = Math.Min(lower + 1, ordered.Length - 1);
        var fraction = position - lower;
        return ordered[lower] + ((ordered[upper] - ordered[lower]) * fraction);
    }
}