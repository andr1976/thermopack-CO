using System;
using System.IO;
using System.Linq;
using ThermoPack.Core;
using ThermoPack.Core.Models;

namespace ThermoPack.Tests;

class Program
{
    static int _passed;
    static int _failed;

    static void Main(string[] args)
    {
        Console.WriteLine("ThermoPack Core Tests");
        Console.WriteLine("=====================");
        Console.WriteLine();

        TestComponentDatabase();
        TestLibraryLoadAndFlash();

        Console.WriteLine();
        Console.WriteLine($"Results: {_passed} passed, {_failed} failed");
        Environment.ExitCode = _failed > 0 ? 1 : 0;
    }

    static void TestComponentDatabase()
    {
        Console.WriteLine("--- ComponentDatabase Tests ---");

        var fluidsDir = FindFluidsDirectory();
        if (fluidsDir == null)
        {
            Fail("FindFluidsDirectory", "Could not find fluids/ directory");
            return;
        }
        Pass("FindFluidsDirectory", $"Found at {fluidsDir}");

        var db = new ComponentDatabase();
        db.LoadFromDirectory(fluidsDir);

        // Should load many components
        Assert("LoadCount", db.Components.Count > 50,
            $"Loaded {db.Components.Count} components");

        // Find methane by ident
        var methane = db.FindByIdent("C1");
        Assert("FindByIdent(C1)", methane != null, methane?.ToString() ?? "null");

        if (methane != null)
        {
            Assert("C1.CasNumber", methane.CasNumber == "74-82-8", methane.CasNumber);
            Assert("C1.Name", methane.Name == "METHANE", methane.Name);
            Assert("C1.MolWeight", Math.Abs(methane.MolWeight - 16.0425) < 0.01,
                $"{methane.MolWeight}");
            Assert("C1.Tc", Math.Abs(methane.CriticalTemperature - 190.555) < 0.1,
                $"{methane.CriticalTemperature}");
            Assert("C1.Pc", Math.Abs(methane.CriticalPressure - 4598837.0) < 100,
                $"{methane.CriticalPressure}");
            Assert("C1.HasPR", methane.AvailableEosKeys.Contains("PR"), "");
            Assert("C1.HasSRK", methane.AvailableEosKeys.Contains("SRK"), "");
            Assert("C1.HasPCSAFT",
                methane.AvailableEosKeys.Any(k => k.StartsWith("PC-SAFT-")), "");
        }

        // Find by CAS
        var ethane = db.FindByCas("74-84-0");
        Assert("FindByCas(74-84-0)", ethane != null,
            ethane?.ToString() ?? "null");

        // Find water
        var water = db.FindByIdent("H2O");
        Assert("FindByIdent(H2O)", water != null, water?.ToString() ?? "null");
        if (water != null)
        {
            Assert("H2O.HasCPA",
                water.AvailableEosKeys.Any(k => k.StartsWith("CPA-")),
                string.Join(",", water.AvailableEosKeys));
        }

        // EOS filtering
        var prComps = db.GetComponentsForEos(EosType.PengRobinson);
        Assert("PR components", prComps.Count > 30, $"{prComps.Count} components");

        var pcsaftComps = db.GetComponentsForEos(EosType.PCSAFT);
        Assert("PCSAFT components", pcsaftComps.Count > 10, $"{pcsaftComps.Count} components");

        Console.WriteLine();
    }

    static void TestLibraryLoadAndFlash()
    {
        Console.WriteLine("--- Library & Engine Tests ---");

        // Find the native library
        var libDir = FindLibraryDirectory();
        if (libDir == null)
        {
            Console.WriteLine("  SKIP: Native library not found. Build thermopack first.");
            Console.WriteLine("  Expected: libthermopack.so in installed/bin/ or addon/pycThermopack/thermopack/");
            Console.WriteLine();
            return;
        }
        Pass("FindLibrary", $"Found in {libDir}");

        var lib = new ThermoPackLibrary();
        try
        {
            lib.Load(libDir);
            Pass("LoadLibrary", "Loaded successfully");
        }
        catch (Exception ex)
        {
            Fail("LoadLibrary", ex.Message);
            return;
        }

        // Create engine
        ThermoPackEngine engine;
        try
        {
            engine = new ThermoPackEngine(lib);
            Pass("CreateEngine", "Engine created");
        }
        catch (Exception ex)
        {
            Fail("CreateEngine", ex.Message);
            return;
        }

        // Init PR with C1,C2
        try
        {
            engine.InitCubic("C1,C2", "PR");
            Assert("InitCubic(PR)", engine.ComponentCount == 2,
                $"nc={engine.ComponentCount}");
        }
        catch (Exception ex)
        {
            Fail("InitCubic(PR)", ex.Message);
            return;
        }

        // TP flash at 200 K, 1e6 Pa, z=[0.5, 0.5]
        // (At 200K, 1MPa, C1/C2 is two-phase: Tbub~166K, Tdew~220K)
        try
        {
            double T = 200.0, P = 1e6;
            var z = new double[] { 0.5, 0.5 };
            var result = engine.TwoPhaseTPFlash(T, P, z);

            Console.WriteLine($"  TP Flash: T={T} K, P={P} Pa, z=[0.5, 0.5]");
            Console.WriteLine($"    betaV={result.BetaV:F6}, betaL={result.BetaL:F6}");
            Console.WriteLine($"    x=[{result.X[0]:F6}, {result.X[1]:F6}]");
            Console.WriteLine($"    y=[{result.Y[0]:F6}, {result.Y[1]:F6}]");

            Assert("TPFlash.betaV+betaL~1",
                Math.Abs(result.BetaV + result.BetaL - 1.0) < 0.01,
                $"sum={result.BetaV + result.BetaL}");

            // At 200K, 1MPa, C1/C2 mixture should be two-phase
            Assert("TPFlash.TwoPhase",
                result.BetaV > 0.01 && result.BetaL > 0.01,
                $"betaV={result.BetaV}, betaL={result.BetaL}");

            // y should be richer in lighter component (C1)
            Assert("TPFlash.y[0]>x[0]", result.Y[0] > result.X[0],
                $"y[0]={result.Y[0]:F6}, x[0]={result.X[0]:F6}");
        }
        catch (Exception ex)
        {
            Fail("TPFlash", ex.Message);
        }

        // Property calculations
        try
        {
            double T = 200.0, P = 1e6;
            var z = new double[] { 0.5, 0.5 };
            int vapPhase = engine.VaporPhase;
            int liqPhase = engine.LiquidPhase;

            double hV = engine.Enthalpy(T, P, z, vapPhase);
            double hL = engine.Enthalpy(T, P, z, liqPhase);
            Console.WriteLine($"  H_vap={hV:F1} J/mol, H_liq={hL:F1} J/mol");
            Assert("Enthalpy.VapGtLiq", hV > hL, $"hV={hV:F1}, hL={hL:F1}");

            double sV = engine.Entropy(T, P, z, vapPhase);
            double sL = engine.Entropy(T, P, z, liqPhase);
            Console.WriteLine($"  S_vap={sV:F2} J/mol/K, S_liq={sL:F2} J/mol/K");
            Assert("Entropy.VapGtLiq", sV > sL, $"sV={sV:F2}, sL={sL:F2}");

            double vV = engine.SpecificVolume(T, P, z, vapPhase);
            double vL = engine.SpecificVolume(T, P, z, liqPhase);
            Console.WriteLine($"  V_vap={vV:E4} m3/mol, V_liq={vL:E4} m3/mol");
            Assert("Volume.VapGtLiq", vV > vL, $"vV={vV:E4}, vL={vL:E4}");

            double zfacV = engine.ZFac(T, P, z, vapPhase);
            Console.WriteLine($"  Z_vap={zfacV:F6}");
            Assert("ZFac.Reasonable", zfacV > 0.1 && zfacV < 2.0, $"Z={zfacV}");

            var lnphi = engine.LnFugacityCoefficients(T, P, z, vapPhase);
            Console.WriteLine($"  lnphi_vap=[{lnphi[0]:F6}, {lnphi[1]:F6}]");
            Assert("LnFugacity.Finite",
                !double.IsNaN(lnphi[0]) && !double.IsInfinity(lnphi[0]),
                $"lnphi[0]={lnphi[0]}");

            double mw1 = engine.CompMoleWeight(1);
            double mw2 = engine.CompMoleWeight(2);
            Console.WriteLine($"  MW_1={mw1:F3} g/mol, MW_2={mw2:F3} g/mol");
            Assert("CompMoleWeight.C1", Math.Abs(mw1 - 16.04) < 0.1, $"{mw1}");
            Assert("CompMoleWeight.C2", Math.Abs(mw2 - 30.07) < 0.1, $"{mw2}");

            var (Tc, Pc, omega, Vc, Tnb) = engine.GetCriticalParam(1);
            Console.WriteLine($"  C1: Tc={Tc:F2}K, Pc={Pc:F0}Pa, omega={omega:F4}");
            Assert("CriticalParam.Tc", Math.Abs(Tc - 190.6) < 1.0, $"Tc={Tc}");

            double Rgas = engine.GetRgas();
            Console.WriteLine($"  Rgas={Rgas:F4}");
            Assert("Rgas.Reasonable", Rgas > 8.0 && Rgas < 9.0, $"Rgas={Rgas}");

            var (hCp, cp) = engine.EnthalpyWithCp(T, P, z, vapPhase);
            Console.WriteLine($"  Cp_vap={cp:F2} J/mol/K");
            Assert("Cp.Positive", cp > 0, $"Cp={cp}");
        }
        catch (Exception ex)
        {
            Fail("Properties", ex.Message);
        }

        // Saturation points
        try
        {
            double P = 1e6;
            var z = new double[] { 0.5, 0.5 };

            var (Tbub, yBub) = engine.BubbleTemperature(P, z);
            var (Tdew, xDew) = engine.DewTemperature(P, z);
            Console.WriteLine($"  Bubble T={Tbub:F2} K, Dew T={Tdew:F2} K at P={P} Pa");
            Assert("Saturation.TbubLtTdew", Tbub < Tdew,
                $"Tbub={Tbub:F2}, Tdew={Tdew:F2}");
        }
        catch (Exception ex)
        {
            Fail("Saturation", ex.Message);
        }

        // PVF flash
        try
        {
            double P = 1e6;
            var z = new double[] { 0.5, 0.5 };

            var pvf = engine.PVFFlash(P, z, 0.5);
            Console.WriteLine($"  PVF(0.5): T={pvf.Temperature:F2} K, betaV={pvf.BetaV:F6}");
            Assert("PVFFlash.BetaV~0.5", Math.Abs(pvf.BetaV - 0.5) < 0.01,
                $"betaV={pvf.BetaV}");
        }
        catch (Exception ex)
        {
            Fail("PVFFlash", ex.Message);
        }

        engine.Dispose();

        // ─── CPA init tests ─────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("--- CPA Init Tests ---");

        // CPA with associating components (should work)
        ThermoPackEngine cpaEngine;
        try
        {
            cpaEngine = new ThermoPackEngine(lib);
            cpaEngine.InitCpa("H2O,MEOH", "SRK");
            Assert("InitCpa(H2O,MEOH)", cpaEngine.ComponentCount == 2,
                $"nc={cpaEngine.ComponentCount}");

            // TP flash
            var cpaFlash = cpaEngine.TwoPhaseTPFlash(350.0, 1e5, new[] { 0.5, 0.5 });
            Console.WriteLine($"  CPA Flash: T=350, P=1e5, betaV={cpaFlash.BetaV:F4}");
            Assert("CPA.TPFlash", cpaFlash.BetaV > 0.01 && cpaFlash.BetaV < 0.99,
                $"betaV={cpaFlash.BetaV}");

            // Properties
            double hCpa = cpaEngine.Enthalpy(350.0, 1e5, new[] { 0.5, 0.5 }, cpaEngine.VaporPhase);
            Assert("CPA.Enthalpy", !double.IsNaN(hCpa), $"h={hCpa:F1}");

            cpaEngine.Dispose();
        }
        catch (Exception ex)
        {
            Fail("InitCpa(H2O,MEOH)", ex.Message);
        }

        // CPA with mixed: one associating + one non-associating (should work)
        try
        {
            cpaEngine = new ThermoPackEngine(lib);
            cpaEngine.InitCpa("H2O,C1", "SRK");
            Assert("InitCpa(H2O,C1)", cpaEngine.ComponentCount == 2,
                $"nc={cpaEngine.ComponentCount}");

            var cpaFlash2 = cpaEngine.TwoPhaseTPFlash(350.0, 1e5, new[] { 0.5, 0.5 });
            Console.WriteLine($"  CPA H2O+C1: betaV={cpaFlash2.BetaV:F4}");
            Assert("CPA.H2O+C1.Flash", !double.IsNaN(cpaFlash2.BetaV),
                $"betaV={cpaFlash2.BetaV}");

            cpaEngine.Dispose();
        }
        catch (Exception ex)
        {
            Fail("InitCpa(H2O,C1)", ex.Message);
        }

        // CPA with only non-associating (should fail gracefully, NOT crash)
        try
        {
            cpaEngine = new ThermoPackEngine(lib);
            cpaEngine.InitCpa("C1,C2", "SRK");
            // If we get here, Fortran didn't crash - check if it worked or silently fell back
            Console.WriteLine($"  WARNING: InitCpa(C1,C2) did not throw, nc={cpaEngine.ComponentCount}");
            Fail("InitCpa(C1,C2)", "Expected error for non-associating components, but init succeeded");
            cpaEngine.Dispose();
        }
        catch (Exception ex)
        {
            // This is the expected path - should get a clear error, not a crash
            Pass("InitCpa(C1,C2) rejected", ex.Message);
        }

        AdvancedModelsValidation.Run(lib);

        lib.Dispose();
        Console.WriteLine();
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    static string? FindFluidsDirectory()
    {
        // Walk up from cwd or script location
        var dir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "fluids");
            if (Directory.Exists(candidate) &&
                File.Exists(Path.Combine(candidate, "Methane.json")))
                return candidate;
            var parent = Path.GetDirectoryName(dir);
            if (parent == null) break;
            dir = parent;
        }
        return null;
    }

    static string? FindLibraryDirectory()
    {
        var dir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 8; i++)
        {
            // Check installed/bin
            var installed = Path.Combine(dir, "installed", "bin");
            if (HasNativeLib(installed)) return installed;

            // Check addon/pycThermopack/thermopack
            var pyc = Path.Combine(dir, "addon", "pycThermopack", "thermopack");
            if (HasNativeLib(pyc)) return pyc;

            var parent = Path.GetDirectoryName(dir);
            if (parent == null) break;
            dir = parent;
        }
        return null;
    }

    static bool HasNativeLib(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        return File.Exists(Path.Combine(dir, "libthermopack.so")) ||
               File.Exists(Path.Combine(dir, "thermopack.dll")) ||
               File.Exists(Path.Combine(dir, "libthermopack.dylib"));
    }

    static void Assert(string name, bool condition, string detail)
    {
        if (condition)
            Pass(name, detail);
        else
            Fail(name, detail);
    }

    static void Pass(string name, string detail)
    {
        Console.WriteLine($"  PASS: {name} ({detail})");
        _passed++;
    }

    static void Fail(string name, string detail)
    {
        Console.WriteLine($"  FAIL: {name} ({detail})");
        _failed++;
    }
}
