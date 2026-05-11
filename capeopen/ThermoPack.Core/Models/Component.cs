namespace ThermoPack.Core.Models;

/// <summary>
/// Represents a thermodynamic component parsed from fluids/*.json.
/// </summary>
public class Component
{
    public string Ident { get; set; } = "";
    public string Formula { get; set; } = "";
    public string CasNumber { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Aliases { get; set; } = Array.Empty<string>();
    public double MolWeight { get; set; }
    public double CriticalTemperature { get; set; }
    public double CriticalPressure { get; set; }
    public double AcentricFactor { get; set; }
    public double BoilingTemperature { get; set; }

    /// <summary>
    /// Set of EOS keys present in the JSON file (e.g. "PR", "SRK", "PC-SAFT-1", "CPA-1").
    /// </summary>
    public HashSet<string> AvailableEosKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasEos(EosType eos)
    {
        return eos switch
        {
            EosType.PengRobinson => AvailableEosKeys.Contains("PR"),
            EosType.SRK => AvailableEosKeys.Contains("SRK"),
            EosType.TcPR => AvailableEosKeys.Contains("PR"),
            EosType.CPA => AvailableEosKeys.Any(k => k.StartsWith("CPA-", StringComparison.OrdinalIgnoreCase))
                           || AvailableEosKeys.Contains("SRK"),
            EosType.GERG2008 => true, // GERG-2008 uses NIST_MEOS, availability checked at init time
            EosType.PCSAFT => AvailableEosKeys.Any(k => k.StartsWith("PC-SAFT-", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    public override string ToString() => $"{Name} ({Ident}, CAS {CasNumber})";
}
