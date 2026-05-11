namespace ThermoPack.Core.Models;

/// <summary>
/// Supported equation of state types.
/// </summary>
public enum EosType
{
    PengRobinson = 0,
    SRK = 1,
    TcPR = 2,
    CPA = 3,
    GERG2008 = 4,
    PCSAFT = 5
}
