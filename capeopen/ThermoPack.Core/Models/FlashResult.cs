namespace ThermoPack.Core.Models;

/// <summary>
/// Result of a two-phase flash calculation.
/// </summary>
public class FlashResult
{
    public double Temperature { get; set; }
    public double Pressure { get; set; }
    public double BetaV { get; set; }
    public double BetaL { get; set; }
    public int Phase { get; set; }
    public double[] X { get; set; } = Array.Empty<double>();
    public double[] Y { get; set; } = Array.Empty<double>();
    public int ErrorCode { get; set; }

    public bool HasVapour => BetaV > 1e-12;
    public bool HasLiquid => BetaL > 1e-12;
}
