using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ThermoPack.Core;

namespace ThermoPack.Tests;

/// <summary>
/// Validation of advanced models (CPA, tcPR) comparing C# calls to Python/ISO references.
/// </summary>
public static class AdvancedModelsValidation
{
    // Low-level delegates for missing engine features
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SetCpaKijDelegate(ref int i, ref int j, ref double kij_a, ref double kij_eps);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CalcCriticalTVDelegate(ref double T, ref double V, double[] n, ref int ierr, ref double tol, IntPtr vMin, ref double p);

    public static void Run(ThermoPackLibrary lib)
    {
        Console.WriteLine("\n--- Advanced Models Validation (C#) ---");
        
        ValidateCpa(lib);
        ValidateTcPr(lib);
    }

    private static void ValidateCpa(ThermoPackLibrary lib)
    {
        Console.WriteLine("\n[CPA] Validating Ethanol/Water Bubble Pressure...");
        
        using var engine = new ThermoPackEngine(lib);
        engine.InitCpa("ETOH,H2O", "SRK", "vdW", "Classic", "Queimada2005");

        // Resolve set_kij directly from library since it's not in the engine
        var setKij = lib.GetModuleDelegate<SetCpaKijDelegate>("saft_interface", "cpa_set_kij");
        
        // Match Python: cpa_srk.set_kij(1, 2, kij_a=-0.08, kij_eps=0.015)
        int i = 1, j = 2;
        double ka = -0.08, ke = 0.015;
        setKij(ref i, ref j, ref ka, ref ke);
        setKij(ref j, ref i, ref ka, ref ke);

        double T = 303.15; // K
        double[] z = { 0.50492, 0.49508 };
        
        var (Pbub, y) = engine.BubblePressure(T, z);
        double PbubKpa = Pbub * 1e-3;
        double targetKpa = 9.6630; // From Pemberton and Mash (1978)

        double diff = Math.Abs(PbubKpa - targetKpa) / targetKpa * 100;
        
        Console.WriteLine($"  T = {T} K, x_EtOH = {z[0]}");
        Console.WriteLine($"  P_calc: {PbubKpa,10:F4} kPa");
        Console.WriteLine($"  P_ref:  {targetKpa,10:F4} kPa (Pemberton & Mash)");
        Console.WriteLine($"  Diff%:  {diff:E4}%");
    }

    private static void ValidateTcPr(ThermoPackLibrary lib)
    {
        Console.WriteLine("\n[tcPR] Validating CO2/N2 Critical Point...");

        using var engine = new ThermoPackEngine(lib);
        engine.InitTcPR("CO2,N2");

        // mixture critical point calculation
        var calcCrit = lib.GetModuleDelegate<CalcCriticalTVDelegate>("critical", "calccriticaltv");

        double[] n = { 0.9, 0.1 };
        double tc = 0, vc = 0, pc = 0, tol = 1e-7;
        int ierr = 0;
        
        calcCrit(ref tc, ref vc, n, ref ierr, ref tol, IntPtr.Zero, ref pc);

        double targetTc = 296.875; // From Python example
        double targetPcMpa = 8.9609;

        Console.WriteLine($"  Mixture: 90% CO2, 10% N2");
        Console.WriteLine($"  Tc_calc: {tc,10:F4} K  (Ref: {targetTc:F4})");
        Console.WriteLine($"  Pc_calc: {pc * 1e-6,10:F4} MPa (Ref: {targetPcMpa:F4})");
    }
}
