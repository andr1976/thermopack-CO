using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using ThermoPack.Core.Models;

namespace ThermoPack.Core;

/// <summary>
/// High-level wrapper around thermopack Fortran shared library.
/// Manages model lifecycle, thread safety, and marshaling.
/// </summary>
public class ThermoPackEngine : IDisposable
{
    private readonly ThermoPackLibrary _lib;
    private int _modelIndex;
    private int _nc;
    private bool _disposed;

    // Static: if ANY engine's Fortran call hangs/crashes, the shared library
    // state is compromised and ALL engines must fail fast (they share a static lock).
    private static volatile bool _faulted;

    // Thread safety: Fortran global state is not thread-safe
    private static readonly object _lock = new();

    // Phase flags (set once from Fortran)
    private static int _LIQPH = 1;
    private static int _VAPPH = 2;
    private static int _MINGIBBSPH = 3;
    private static int _SINGLEPH = 4;
    private static bool _phaseFlagsLoaded;

    // Fortran true value (gfortran=1, ifort=-1)
    private int _trueInt = 1;

    // Error handling: prevent Fortran exit()/abort() from killing the host process
    private static bool _errorHandlingConfigured;

    /// <summary>
    /// Timeout in milliseconds for flash calculations.
    /// Protects the host process against Fortran solvers that hang
    /// (e.g. density solver infinite loops in multiparameter EOS).
    /// Default: 10 seconds.
    /// </summary>
    public static int FlashTimeoutMs { get; set; } = 10_000;

    // ─── Cached delegates ─────────────────────────────────────────────

    private ThermoPackInterop.ActivateModelDelegate? _activateModel;
    private ThermoPackInterop.DeleteEosDelegate? _deleteEos;
    private ThermoPackInterop.GetRgasDelegate? _getRgas;

    // Init
    private ThermoPackInterop.InitCubicDelegate? _initCubic;
    private ThermoPackInterop.InitTcPRDelegate? _initTcPR;
    private ThermoPackInterop.InitCpaDelegate? _initCpa;
    private ThermoPackInterop.InitPcSaftDelegate? _initPcSaft;
    private ThermoPackInterop.InitMultiparameterDelegate? _initMultiparameter;

    // Flash
    private ThermoPackInterop.TwoPhaseTPFlashDelegate? _tpFlash;
    private ThermoPackInterop.TwoPhasePHFlashDelegate? _phFlash;
    private ThermoPackInterop.TwoPhasePSFlashDelegate? _psFlash;
    private ThermoPackInterop.TwoPhaseUVFlashDelegate? _uvFlash;

    // Saturation
    private ThermoPackInterop.SafeBubTDelegate? _safeBubT;
    private ThermoPackInterop.SafeBubPDelegate? _safeBubP;
    private ThermoPackInterop.SafeDewTDelegate? _safeDewT;
    private ThermoPackInterop.SafeDewPDelegate? _safeDewP;

    // Properties
    private ThermoPackInterop.SpecificVolumeDelegate? _specificVolume;
    private ThermoPackInterop.EnthalpyDelegate? _enthalpy;
    private ThermoPackInterop.EnthalpyWithDhdtDelegate? _enthalpyWithDhdt;
    private ThermoPackInterop.EntropyDelegate? _entropy;
    private ThermoPackInterop.ThermoDelegate? _thermo;
    private ThermoPackInterop.ZfacDelegate? _zfac;
    private ThermoPackInterop.IdealEnthalpySingleDelegate? _idealEnthalpySingle;
    private ThermoPackInterop.CompMoleWeightDelegate? _compMoleWeight;
    private ThermoPackInterop.GetCriticalParamDelegate? _getCriticalParam;
    private ThermoPackInterop.GuessPhaseCDelegate? _guessPhase;

    public int ComponentCount => _nc;

    public ThermoPackEngine(ThermoPackLibrary lib)
    {
        _lib = lib ?? throw new ArgumentNullException(nameof(lib));
        if (!lib.IsLoaded)
            throw new InvalidOperationException("Library not loaded");

        ResolveDelegates();
        LoadPhaseFlags();
        LoadFortranTrue();
        ConfigureErrorHandling();

        // Create a new EOS slot
        var addEos = _lib.GetModuleDelegate<ThermoPackInterop.AddEosDelegate>(
            "thermopack_var", "add_eos");
        AcquireFortranLock();
        try { _modelIndex = addEos(); }
        finally { ReleaseFortranLock(); }
    }

    private void Activate()
    {
        _activateModel!(ref _modelIndex);
    }

    // ─── Initialization methods ───────────────────────────────────────

    public void InitCubic(string comps, string eos, string mixing = "vdW",
        string alpha = "Classic", string paramRef = "Default", bool volShift = false)
    {
        AcquireFortranLock();
        try
        {
            Activate();
            var compsB = ToFortranString(comps);
            var eosB = ToFortranString(eos);
            var mixingB = ToFortranString(mixing);
            var alphaB = ToFortranString(alpha);
            var paramRefB = ToFortranString(paramRef);
            int vs = volShift ? _trueInt : 0;

            SafeCall(() => _initCubic!(compsB, eosB, mixingB, alphaB, paramRefB, ref vs,
                (UIntPtr)comps.Length, (UIntPtr)eos.Length, (UIntPtr)mixing.Length,
                (UIntPtr)alpha.Length, (UIntPtr)paramRef.Length), "InitCubic");

            _nc = CountComponents(comps);
        }
        finally { ReleaseFortranLock(); }
    }

    public void InitTcPR(string comps, string mixing = "vdW", string paramRef = "Default")
    {
        AcquireFortranLock();
        try
        {
            Activate();
            var compsB = ToFortranString(comps);
            var mixingB = ToFortranString(mixing);
            var paramRefB = ToFortranString(paramRef);

            SafeCall(() => _initTcPR!(compsB, mixingB, paramRefB,
                (UIntPtr)comps.Length, (UIntPtr)mixing.Length, (UIntPtr)paramRef.Length), "InitTcPR");

            _nc = CountComponents(comps);
        }
        finally { ReleaseFortranLock(); }
    }

    public void InitCpa(string comps, string eos = "SRK", string mixing = "vdW",
        string alpha = "Classic", string paramRef = "Default")
    {
        AcquireFortranLock();
        try
        {
            Activate();
            var compsB = ToFortranString(comps);
            var eosB = ToFortranString(eos);
            var mixingB = ToFortranString(mixing);
            var alphaB = ToFortranString(alpha);
            var paramRefB = ToFortranString(paramRef);

            SafeCall(() => _initCpa!(compsB, eosB, mixingB, alphaB, paramRefB,
                (UIntPtr)comps.Length, (UIntPtr)eos.Length, (UIntPtr)mixing.Length,
                (UIntPtr)alpha.Length, (UIntPtr)paramRef.Length), "InitCpa");

            _nc = CountComponents(comps);
        }
        finally { ReleaseFortranLock(); }
    }

    public void InitPcSaft(string comps, string paramRef = "Default",
        bool simplified = false, bool polar = false)
    {
        AcquireFortranLock();
        try
        {
            Activate();
            var compsB = ToFortranString(comps);
            var paramRefB = ToFortranString(paramRef);
            int simplifiedInt = simplified ? _trueInt : 0;
            int polarInt = polar ? _trueInt : 0;

            SafeCall(() => _initPcSaft!(compsB, paramRefB, ref simplifiedInt, ref polarInt,
                (UIntPtr)comps.Length, (UIntPtr)paramRef.Length), "InitPcSaft");

            _nc = CountComponents(comps);
        }
        finally { ReleaseFortranLock(); }
    }

    public void InitMultiparameter(string comps, string meos = "NIST_MEOS",
        string refState = "DEFAULT")
    {
        AcquireFortranLock();
        try
        {
            Activate();
            var compsB = ToFortranString(comps);
            var meosB = ToFortranString(meos);
            var refStateB = ToFortranString(refState);

            SafeCall(() => _initMultiparameter!(compsB, meosB, refStateB,
                (UIntPtr)comps.Length, (UIntPtr)meos.Length, (UIntPtr)refState.Length), "InitMultiparameter");

            _nc = CountComponents(comps);
        }
        finally { ReleaseFortranLock(); }
    }

    // ─── Flash calculations ───────────────────────────────────────────

    public FlashResult TwoPhaseTPFlash(double T, double P, double[] z)
    {
        return RunWithTimeout(() =>
        {
            AcquireFortranLock();
            try { return TPFlashUnsafe(T, P, z); }
            finally { ReleaseFortranLock(); }
        }, "TPFlash");
    }

    /// <summary>TP flash without locking. Caller must hold _lock.</summary>
    private FlashResult TPFlashUnsafe(double T, double P, double[] z)
    {
        Activate();
        double betaV = 0, betaL = 0;
        int phase = 0;
        var x = new double[_nc];
        var y = new double[_nc];

        SafeCall(() => _tpFlash!(ref T, ref P, z, ref betaV, ref betaL, ref phase, x, y), "TPFlash");

        return new FlashResult
        {
            Temperature = T, Pressure = P,
            BetaV = betaV, BetaL = betaL,
            Phase = phase, X = x, Y = y
        };
    }

    public FlashResult TwoPhasePHFlash(double P, double[] z, double h, double tempGuess = 298.15)
    {
        return RunWithTimeout(() =>
        {
            AcquireFortranLock();
            try
            {
                Activate();
                double T = tempGuess;
                double betaV = 0, betaL = 0;
                int phase = 0, ierr = 0;
                var x = new double[_nc];
                var y = new double[_nc];

                SafeCall(() => _phFlash!(ref T, ref P, z, ref betaV, ref betaL, x, y, ref h, ref phase, ref ierr), "PHFlash");

                if (ierr != 0)
                    throw new InvalidOperationException(
                        $"PH flash failed (ierr={ierr}) at P={P}, h={h}, T_guess={tempGuess}");

                return new FlashResult
                {
                    Temperature = T, Pressure = P,
                    BetaV = betaV, BetaL = betaL,
                    Phase = phase, X = x, Y = y,
                    ErrorCode = ierr
                };
            }
            finally { ReleaseFortranLock(); }
        }, "PHFlash");
    }

    public FlashResult TwoPhasePSFlash(double P, double[] z, double s, double tempGuess = 298.15)
    {
        return RunWithTimeout(() =>
        {
            AcquireFortranLock();
            try
            {
                Activate();
                double T = tempGuess;
                double betaV = 0, betaL = 0;
                int phase = 0, ierr = 0;
                var x = new double[_nc];
                var y = new double[_nc];

                SafeCall(() => _psFlash!(ref T, ref P, z, ref betaV, ref betaL, x, y, ref s, ref phase, ref ierr), "PSFlash");

                if (ierr != 0)
                    throw new InvalidOperationException(
                        $"PS flash failed (ierr={ierr}) at P={P}, s={s}, T_guess={tempGuess}");

                return new FlashResult
                {
                    Temperature = T, Pressure = P,
                    BetaV = betaV, BetaL = betaL,
                    Phase = phase, X = x, Y = y,
                    ErrorCode = ierr
                };
            }
            finally { ReleaseFortranLock(); }
        }, "PSFlash");
    }

    public FlashResult TwoPhaseUVFlash(double[] z, double u, double v,
        double tempGuess = 298.15, double pressGuess = 101325.0)
    {
        return RunWithTimeout(() =>
        {
            AcquireFortranLock();
            try
            {
                Activate();
                double T = tempGuess, P = pressGuess;
                double betaV = 0, betaL = 0;
                int phase = 0;
                var x = new double[_nc];
                var y = new double[_nc];

                SafeCall(() => _uvFlash!(ref T, ref P, z, ref betaV, ref betaL, x, y, ref u, ref v, ref phase), "UVFlash");

                return new FlashResult
                {
                    Temperature = T, Pressure = P,
                    BetaV = betaV, BetaL = betaL,
                    Phase = phase, X = x, Y = y
                };
            }
            finally { ReleaseFortranLock(); }
        }, "UVFlash");
    }

    // ─── Safe PS/PH flash (TP-flash based, avoids Fortran PS/PH solver crash) ──

    /// <summary>
    /// PS flash using TP flash + Brent's method.  Avoids the Fortran PS solver
    /// which can call abort() for some EOS/composition combinations (e.g.
    /// GERG-2008 with near-zero mole fractions).
    /// Finds T such that S_mix(T, P, z) == s_target.
    /// </summary>
    public FlashResult TwoPhasePSFlashSafe(double P, double[] z, double s, double tempGuess = 298.15)
    {
        return RunWithTimeout(() =>
        {
            AcquireFortranLock();
            try { return PSFlashViaBrent(P, z, s, tempGuess); }
            finally { ReleaseFortranLock(); }
        }, "PSFlashSafe");
    }

    /// <summary>
    /// PH flash using TP flash + Brent's method.  Avoids the Fortran PH solver.
    /// Finds T such that H_mix(T, P, z) == h_target.
    /// </summary>
    public FlashResult TwoPhasePHFlashSafe(double P, double[] z, double h, double tempGuess = 298.15)
    {
        return RunWithTimeout(() =>
        {
            AcquireFortranLock();
            try { return PHFlashViaBrent(P, z, h, tempGuess); }
            finally { ReleaseFortranLock(); }
        }, "PHFlashSafe");
    }

    private FlashResult PSFlashViaBrent(double P, double[] z, double sTarget, double Tguess)
    {
        // Bracket: search a wide range around the guess
        double Tlo = Math.Max(50.0, Tguess - 200.0);
        double Thi = Tguess + 500.0;

        double SFunc(double T)
        {
            try
            {
                var fl = TPFlashUnsafe(T, P, z);
                // Phase < 0 means solver failed (continueOnError returns phase=-1)
                if (fl.Phase < 0 || double.IsNaN(fl.BetaV)) return double.NaN;
                return MixEntropy(T, P, fl, z) - sTarget;
            }
            catch { return double.NaN; }
        }

        double Tsol = BrentSolve(SFunc, Tlo, Thi, 1e-6, 80);
        return TPFlashUnsafe(Tsol, P, z);
    }

    private FlashResult PHFlashViaBrent(double P, double[] z, double hTarget, double Tguess)
    {
        double Tlo = Math.Max(50.0, Tguess - 200.0);
        double Thi = Tguess + 500.0;

        double HFunc(double T)
        {
            try
            {
                var fl = TPFlashUnsafe(T, P, z);
                if (fl.Phase < 0 || double.IsNaN(fl.BetaV)) return double.NaN;
                return MixEnthalpy(T, P, fl, z) - hTarget;
            }
            catch { return double.NaN; }
        }

        double Tsol = BrentSolve(HFunc, Tlo, Thi, 1e-6, 80);
        return TPFlashUnsafe(Tsol, P, z);
    }

    /// <summary>Compute mixture entropy from a flash result. Caller must hold _lock.</summary>
    private double MixEntropy(double T, double P, FlashResult fl, double[] z)
    {
        Activate();
        double sMix = 0;
        if (fl.BetaV > 1e-12 && fl.Y != null && ArraySumAbs(fl.Y) > 1e-30)
        {
            double sv = 0; int flag = 0; int phase = _VAPPH;
            SafeCall(() => _entropy!(ref T, ref P, fl.Y, ref phase, ref sv,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref flag), "Entropy");
            sMix += fl.BetaV * sv;
        }
        if (fl.BetaL > 1e-12 && fl.X != null && ArraySumAbs(fl.X) > 1e-30)
        {
            double sl = 0; int flag = 0; int phase = _LIQPH;
            SafeCall(() => _entropy!(ref T, ref P, fl.X, ref phase, ref sl,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref flag), "Entropy");
            sMix += fl.BetaL * sl;
        }
        if (fl.BetaV <= 1e-12 && fl.BetaL <= 1e-12)
        {
            // Single-phase fallback: use feed
            double ss = 0; int flag = 0; int phase = _VAPPH;
            SafeCall(() => _entropy!(ref T, ref P, z, ref phase, ref ss,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref flag), "Entropy");
            sMix = ss;
        }
        return sMix;
    }

    /// <summary>Compute mixture enthalpy from a flash result. Caller must hold _lock.</summary>
    private double MixEnthalpy(double T, double P, FlashResult fl, double[] z)
    {
        Activate();
        double hMix = 0;
        if (fl.BetaV > 1e-12 && fl.Y != null && ArraySumAbs(fl.Y) > 1e-30)
        {
            double hv = 0; int flag = 0; int phase = _VAPPH;
            SafeCall(() => _enthalpy!(ref T, ref P, fl.Y, ref phase, ref hv,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref flag), "Enthalpy");
            hMix += fl.BetaV * hv;
        }
        if (fl.BetaL > 1e-12 && fl.X != null && ArraySumAbs(fl.X) > 1e-30)
        {
            double hl = 0; int flag = 0; int phase = _LIQPH;
            SafeCall(() => _enthalpy!(ref T, ref P, fl.X, ref phase, ref hl,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref flag), "Enthalpy");
            hMix += fl.BetaL * hl;
        }
        if (fl.BetaV <= 1e-12 && fl.BetaL <= 1e-12)
        {
            double hs = 0; int flag = 0; int phase = _VAPPH;
            SafeCall(() => _enthalpy!(ref T, ref P, z, ref phase, ref hs,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref flag), "Enthalpy");
            hMix = hs;
        }
        return hMix;
    }

    private static double ArraySumAbs(double[] arr)
    {
        double s = 0;
        for (int i = 0; i < arr.Length; i++) s += Math.Abs(arr[i]);
        return s;
    }

    // ─── Saturation points ────────────────────────────────────────────

    public (double T, double[] y) BubbleTemperature(double P, double[] z)
    {
        AcquireFortranLock();
        try { return BubbleTemperatureUnsafe(P, z); }
        finally { ReleaseFortranLock(); }
    }

    public (double P, double[] y) BubblePressure(double T, double[] z)
    {
        AcquireFortranLock();
        try { return BubblePressureUnsafe(T, z); }
        finally { ReleaseFortranLock(); }
    }

    public (double T, double[] x) DewTemperature(double P, double[] z)
    {
        AcquireFortranLock();
        try { return DewTemperatureUnsafe(P, z); }
        finally { ReleaseFortranLock(); }
    }

    public (double P, double[] x) DewPressure(double T, double[] z)
    {
        AcquireFortranLock();
        try { return DewPressureUnsafe(T, z); }
        finally { ReleaseFortranLock(); }
    }

    private (double T, double[] y) BubbleTemperatureUnsafe(double P, double[] z)
    {
        Activate();
        var y = new double[_nc];
        int ierr = 0;
        double T = SafeCall(() => _safeBubT!(ref P, z, y, ref ierr), "BubbleTemperature");
        if (ierr != 0)
            throw new InvalidOperationException($"BubbleTemperature failed (ierr={ierr})");
        return (T, y);
    }

    private (double P, double[] y) BubblePressureUnsafe(double T, double[] z)
    {
        Activate();
        var y = new double[_nc];
        int ierr = 0;
        double P = SafeCall(() => _safeBubP!(ref T, z, y, ref ierr), "BubblePressure");
        if (ierr != 0)
            throw new InvalidOperationException($"BubblePressure failed (ierr={ierr})");
        return (P, y);
    }

    private (double T, double[] x) DewTemperatureUnsafe(double P, double[] z)
    {
        Activate();
        var x = new double[_nc];
        int ierr = 0;
        // Fortran safe_dewT(P, X_output, Y_input, ierr): output array first, input array second
        double T = SafeCall(() => _safeDewT!(ref P, x, z, ref ierr), "DewTemperature");
        if (ierr != 0)
            throw new InvalidOperationException($"DewTemperature failed (ierr={ierr})");
        return (T, x);
    }

    private (double P, double[] x) DewPressureUnsafe(double T, double[] z)
    {
        Activate();
        var x = new double[_nc];
        int ierr = 0;
        // Fortran safe_dewP(T, X_output, Y_input, ierr): output array first, input array second
        double P = SafeCall(() => _safeDewP!(ref T, x, z, ref ierr), "DewPressure");
        if (ierr != 0)
            throw new InvalidOperationException($"DewPressure failed (ierr={ierr})");
        return (P, x);
    }

    // ─── PVF/TVF Flash (Brent solver) ─────────────────────────────────

    /// <summary>
    /// Flash at given P, z, and target vapor fraction.
    /// Uses saturation endpoints + Brent's method.
    /// </summary>
    public FlashResult PVFFlash(double P, double[] z, double betaVTarget)
    {
        AcquireFortranLock();
        try
        {
            if (betaVTarget <= 0.0)
            {
                var (Tbub, yBub) = BubbleTemperatureUnsafe(P, z);
                return new FlashResult
                {
                    Temperature = Tbub, Pressure = P,
                    BetaV = 0.0, BetaL = 1.0, Phase = _LIQPH,
                    X = (double[])z.Clone(), Y = yBub
                };
            }
            if (betaVTarget >= 1.0)
            {
                var (Tdew, xDew) = DewTemperatureUnsafe(P, z);
                return new FlashResult
                {
                    Temperature = Tdew, Pressure = P,
                    BetaV = 1.0, BetaL = 0.0, Phase = _VAPPH,
                    X = xDew, Y = (double[])z.Clone()
                };
            }

            var (Tlo, _) = BubbleTemperatureUnsafe(P, z);
            var (Thi, _) = DewTemperatureUnsafe(P, z);

            double BrentFunc(double T) => TPFlashUnsafe(T, P, z).BetaV - betaVTarget;

            double Tsol = BrentSolve(BrentFunc, Tlo, Thi, 1e-8, 50);
            return TPFlashUnsafe(Tsol, P, z);
        }
        finally { ReleaseFortranLock(); }
    }

    /// <summary>
    /// Flash at given T, z, and target vapor fraction.
    /// Uses saturation endpoints + Brent's method.
    /// </summary>
    public FlashResult TVFFlash(double T, double[] z, double betaVTarget)
    {
        AcquireFortranLock();
        try
        {
            if (betaVTarget <= 0.0)
            {
                var (Pbub, yBub) = BubblePressureUnsafe(T, z);
                return new FlashResult
                {
                    Temperature = T, Pressure = Pbub,
                    BetaV = 0.0, BetaL = 1.0, Phase = _LIQPH,
                    X = (double[])z.Clone(), Y = yBub
                };
            }
            if (betaVTarget >= 1.0)
            {
                var (Pdew, xDew) = DewPressureUnsafe(T, z);
                return new FlashResult
                {
                    Temperature = T, Pressure = Pdew,
                    BetaV = 1.0, BetaL = 0.0, Phase = _VAPPH,
                    X = xDew, Y = (double[])z.Clone()
                };
            }

            var (Phi, _) = BubblePressureUnsafe(T, z);
            var (Plo, _) = DewPressureUnsafe(T, z);

            double BrentFunc(double P) => TPFlashUnsafe(T, P, z).BetaV - betaVTarget;

            double Psol = BrentSolve(BrentFunc, Plo, Phi, 1.0, 50);
            return TPFlashUnsafe(T, Psol, z);
        }
        finally { ReleaseFortranLock(); }
    }

    // ─── Property calculations ────────────────────────────────────────

    public double Enthalpy(double T, double P, double[] x, int phase)
    {
        AcquireFortranLock();
        try
        {
            Activate();
            double h = 0;
            int flag = 0;
            SafeCall(() => _enthalpy!(ref T, ref P, x, ref phase, ref h,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref flag), "Enthalpy");
            return h;
        }
        finally { ReleaseFortranLock(); }
    }

    public (double h, double dhdt) EnthalpyWithCp(double T, double P, double[] x, int phase)
    {
        AcquireFortranLock();
        try
        {
            Activate();
            double h = 0, dhdt = 0;
            int flag = 0;
            SafeCall(() => _enthalpyWithDhdt!(ref T, ref P, x, ref phase, ref h, ref dhdt,
                IntPtr.Zero, IntPtr.Zero, ref flag), "EnthalpyWithCp");
            return (h, dhdt);
        }
        finally { ReleaseFortranLock(); }
    }

    public double Entropy(double T, double P, double[] x, int phase)
    {
        AcquireFortranLock();
        try
        {
            Activate();
            double s = 0;
            int flag = 0;
            SafeCall(() => _entropy!(ref T, ref P, x, ref phase, ref s,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref flag), "Entropy");
            return s;
        }
        finally { ReleaseFortranLock(); }
    }

    public double SpecificVolume(double T, double P, double[] x, int phase)
    {
        AcquireFortranLock();
        try
        {
            Activate();
            double v = 0;
            SafeCall(() => _specificVolume!(ref T, ref P, x, ref phase, ref v,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), "SpecificVolume");
            return v;
        }
        finally { ReleaseFortranLock(); }
    }

    public double[] LnFugacityCoefficients(double T, double P, double[] x, int phase)
    {
        AcquireFortranLock();
        try
        {
            Activate();
            var lnfug = new double[_nc];
            int ophase = 0, meta = 0;
            double v = 0;
            SafeCall(() => _thermo!(ref T, ref P, x, ref phase, lnfug,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                ref ophase, ref meta, ref v), "LnFugacityCoefficients");
            return lnfug;
        }
        finally { ReleaseFortranLock(); }
    }

    public double ZFac(double T, double P, double[] x, int phase)
    {
        AcquireFortranLock();
        try
        {
            Activate();
            double z = 0;
            SafeCall(() => _zfac!(ref T, ref P, x, ref phase, ref z,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), "ZFac");
            return z;
        }
        finally { ReleaseFortranLock(); }
    }

    public double CompMoleWeight(int index)
    {
        AcquireFortranLock();
        try
        {
            Activate();
            int j = index;
            return SafeCall(() => _compMoleWeight!(ref j), "CompMoleWeight");
        }
        finally { ReleaseFortranLock(); }
    }

    public (double Tc, double Pc, double omega, double Vc, double Tnb) GetCriticalParam(int index)
    {
        AcquireFortranLock();
        try
        {
            Activate();
            int i = index;
            double tc = 0, pc = 0, omega = 0, vc = 0, tnb = 0;
            SafeCall(() => _getCriticalParam!(ref i, ref tc, ref pc, ref omega, ref vc, ref tnb), "GetCriticalParam");
            return (tc, pc, omega, vc, tnb);
        }
        finally { ReleaseFortranLock(); }
    }

    public double GetRgas()
    {
        AcquireFortranLock();
        try
        {
            Activate();
            return SafeCall(() => _getRgas!(), "GetRgas");
        }
        finally { ReleaseFortranLock(); }
    }

    public double IdealEnthalpySingle(double T, int compIdx)
    {
        AcquireFortranLock();
        try
        {
            Activate();
            double hId = 0;
            SafeCall(() => _idealEnthalpySingle!(ref T, ref compIdx, ref hId, IntPtr.Zero), "IdealEnthalpySingle");
            return hId;
        }
        finally { ReleaseFortranLock(); }
    }

    // Phase flag accessors
    public int LiquidPhase => _LIQPH;
    public int VaporPhase => _VAPPH;
    public int MinGibbsPhase => _MINGIBBSPH;
    public int SinglePhase => _SINGLEPH;

    /// <summary>
    /// Guess whether a single-phase state is liquid or vapor using
    /// pseudo-critical properties or volume/co-volume ratio.
    /// Returns LIQPH or VAPPH.
    /// </summary>
    public int GuessPhase(double T, double P, double[] z)
    {
        if (_guessPhase == null) return _VAPPH; // fallback
        AcquireFortranLock();
        try
        {
            Activate();
            int phase = 0;
            SafeCall(() => _guessPhase(ref T, ref P, z, ref phase), "GuessPhase");
            return phase;
        }
        finally { ReleaseFortranLock(); }
    }

    // ─── Helper methods ───────────────────────────────────────────────

    private static byte[] ToFortranString(string s)
    {
        return Encoding.ASCII.GetBytes(s);
    }

    private static int CountComponents(string comps)
    {
        // Thermopack accepts comma or space separated component lists
        var parts = comps.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return Math.Max(1, parts.Length);
    }

    private void ResolveDelegates()
    {
        _activateModel = _lib.GetModuleDelegate<ThermoPackInterop.ActivateModelDelegate>(
            "thermopack_var", "activate_model");
        _deleteEos = _lib.GetModuleDelegate<ThermoPackInterop.DeleteEosDelegate>(
            "thermopack_var", "delete_eos");
        _getRgas = _lib.GetModuleDelegate<ThermoPackInterop.GetRgasDelegate>(
            "thermopack_var", "get_rgas");

        // Init
        _initCubic = _lib.GetModuleDelegate<ThermoPackInterop.InitCubicDelegate>(
            "eoslibinit", "init_cubic");
        _initTcPR = _lib.GetModuleDelegate<ThermoPackInterop.InitTcPRDelegate>(
            "eoslibinit", "init_tcpr");
        _initCpa = _lib.GetModuleDelegate<ThermoPackInterop.InitCpaDelegate>(
            "eoslibinit", "init_cpa");
        _initPcSaft = _lib.GetModuleDelegate<ThermoPackInterop.InitPcSaftDelegate>(
            "eoslibinit", "init_pcsaft");
        _initMultiparameter = _lib.GetModuleDelegate<ThermoPackInterop.InitMultiparameterDelegate>(
            "eoslibinit", "init_multiparameter");

        // Flash
        _tpFlash = _lib.GetModuleDelegate<ThermoPackInterop.TwoPhaseTPFlashDelegate>(
            "tp_solver", "twophasetpflash");
        _phFlash = _lib.GetModuleDelegate<ThermoPackInterop.TwoPhasePHFlashDelegate>(
            "ph_solver", "twophasephflash");
        _psFlash = _lib.GetModuleDelegate<ThermoPackInterop.TwoPhasePSFlashDelegate>(
            "ps_solver", "twophasepsflash");
        _uvFlash = _lib.GetModuleDelegate<ThermoPackInterop.TwoPhaseUVFlashDelegate>(
            "uv_solver", "twophaseuvflash");

        // Saturation
        _safeBubT = _lib.GetModuleDelegate<ThermoPackInterop.SafeBubTDelegate>(
            "saturation", "safe_bubt");
        _safeBubP = _lib.GetModuleDelegate<ThermoPackInterop.SafeBubPDelegate>(
            "saturation", "safe_bubp");
        _safeDewT = _lib.GetModuleDelegate<ThermoPackInterop.SafeDewTDelegate>(
            "saturation", "safe_dewt");
        _safeDewP = _lib.GetModuleDelegate<ThermoPackInterop.SafeDewPDelegate>(
            "saturation", "safe_dewp");

        // Properties
        _specificVolume = _lib.GetModuleDelegate<ThermoPackInterop.SpecificVolumeDelegate>(
            "eos", "specificvolume");
        _enthalpy = _lib.GetModuleDelegate<ThermoPackInterop.EnthalpyDelegate>(
            "eos", "enthalpy");
        _enthalpyWithDhdt = _lib.GetModuleDelegate<ThermoPackInterop.EnthalpyWithDhdtDelegate>(
            "eos", "enthalpy");
        _entropy = _lib.GetModuleDelegate<ThermoPackInterop.EntropyDelegate>(
            "eos", "entropy");
        _thermo = _lib.GetModuleDelegate<ThermoPackInterop.ThermoDelegate>(
            "eos", "thermo");
        _zfac = _lib.GetModuleDelegate<ThermoPackInterop.ZfacDelegate>(
            "eos", "zfac");
        _idealEnthalpySingle = _lib.GetModuleDelegate<ThermoPackInterop.IdealEnthalpySingleDelegate>(
            "eos", "ideal_enthalpy_single");
        _compMoleWeight = _lib.GetModuleDelegate<ThermoPackInterop.CompMoleWeightDelegate>(
            "eos", "compmoleweight");
        _getCriticalParam = _lib.GetModuleDelegate<ThermoPackInterop.GetCriticalParamDelegate>(
            "eos", "getcriticalparam");

        // Phase guessing (C-bound, no mangling)
        try
        {
            _guessPhase = _lib.GetCDelegate<ThermoPackInterop.GuessPhaseCDelegate>(
                "thermopack_guess_phase_c");
        }
        catch { /* Optional: not all builds may have this */ }
    }

    private void LoadPhaseFlags()
    {
        if (_phaseFlagsLoaded) return;
        AcquireFortranLock();
        try
        {
            if (_phaseFlagsLoaded) return;
            try
            {
                var getFlags = _lib.GetCDelegate<ThermoPackInterop.GetPhaseFlagsCDelegate>(
                    "get_phase_flags_c");
                int twoph = 0, liqph = 0, vapph = 0, mingibbsph = 0;
                int singleph = 0, solidph = 0, fakeph = 0;
                getFlags(ref twoph, ref liqph, ref vapph, ref mingibbsph,
                    ref singleph, ref solidph, ref fakeph);
                _LIQPH = liqph;
                _VAPPH = vapph;
                _MINGIBBSPH = mingibbsph;
                _SINGLEPH = singleph;
            }
            catch
            {
                // Use defaults
            }
            _phaseFlagsLoaded = true;
        }
        finally { ReleaseFortranLock(); }
    }

    private void LoadFortranTrue()
    {
        try
        {
            var getTrue = _lib.GetModuleDelegate<ThermoPackInterop.GetTrueDelegate>(
                "thermopack_constants", "get_true");
            int val = 0;
            AcquireFortranLock();
            try { getTrue(ref val); }
            finally { ReleaseFortranLock(); }
            _trueInt = val;
        }
        catch
        {
            _trueInt = 1; // Default gfortran
        }
    }

    /// <summary>
    /// Configure Fortran error handling to prevent process termination.
    /// Sets thermopack_constants::continueOnError = .true. so that solvers
    /// return error codes instead of calling stoperror() → exit(1).
    /// Sets error::dostop = .false. as a safety net so that any stoperror()
    /// call that IS reached won't terminate the host process.
    /// These are the same flags used by thermopack's own unit tests.
    /// </summary>
    private void ConfigureErrorHandling()
    {
        if (_errorHandlingConfigured) return;
        AcquireFortranLock();
        try
        {
            if (_errorHandlingConfigured) return;

            // Set continueOnError = .true. in thermopack_constants module.
            // This makes solvers (TP, PS, PH, density solver, etc.) return
            // with error indicators instead of calling exit().
            try
            {
                var ptr = _lib.GetModuleVariableAddress("thermopack_constants", "continueonerror");
                if (ptr != IntPtr.Zero)
                    Marshal.WriteInt32(ptr, _trueInt);
            }
            catch { /* Symbol may not exist in some builds */ }

            // Set dostop = .false. in error module.
            // Safety net: if stoperror() IS reached, it won't call exit().
            try
            {
                var ptr = _lib.GetModuleVariableAddress("error", "dostop");
                if (ptr != IntPtr.Zero)
                    Marshal.WriteInt32(ptr, 0); // .false. = 0 for both gfortran and ifort
            }
            catch { }

            _errorHandlingConfigured = true;
        }
        finally { ReleaseFortranLock(); }
    }

    /// <summary>
    /// Brent's method for root finding. Finds x such that f(x) = 0 in [a, b].
    /// Handles NaN function values (from failed Fortran calls) by falling back
    /// to bisection away from the problematic point.
    /// </summary>
    private static double BrentSolve(Func<double, double> f, double a, double b,
        double tol, int maxIter)
    {
        double fa = f(a);
        double fb = f(b);

        // If endpoints failed (NaN), try to shrink bracket to find valid points
        if (double.IsNaN(fa) || double.IsNaN(fb))
        {
            if (!TryFindBracket(f, ref a, ref b, ref fa, ref fb, 10))
                throw new InvalidOperationException(
                    "PS/PH flash: cannot find valid bracket — " +
                    "Fortran solver fails at all sampled temperatures.");
        }

        if (fa * fb > 0)
        {
            // No sign change — use midpoint as fallback
            return (a + b) / 2.0;
        }

        double c = a, fc = fa;
        double d = b - a, e = d;

        for (int i = 0; i < maxIter; i++)
        {
            if (fb * fc > 0)
            {
                c = a; fc = fa;
                d = b - a; e = d;
            }

            if (Math.Abs(fc) < Math.Abs(fb))
            {
                a = b; b = c; c = a;
                fa = fb; fb = fc; fc = fa;
            }

            double tol1 = 2.0 * 2.22e-16 * Math.Abs(b) + 0.5 * tol;
            double m = 0.5 * (c - b);

            if (Math.Abs(m) <= tol1 || Math.Abs(fb) < 1e-15)
                return b;

            if (Math.Abs(e) >= tol1 && Math.Abs(fa) > Math.Abs(fb))
            {
                double s2, p2, q2;
                s2 = fb / fa;
                if (Math.Abs(a - c) < 1e-30)
                {
                    p2 = 2.0 * m * s2;
                    q2 = 1.0 - s2;
                }
                else
                {
                    q2 = fa / fc;
                    double r = fb / fc;
                    p2 = s2 * (2.0 * m * q2 * (q2 - r) - (b - a) * (r - 1.0));
                    q2 = (q2 - 1.0) * (r - 1.0) * (s2 - 1.0);
                }
                if (p2 > 0) q2 = -q2; else p2 = -p2;

                if (2.0 * p2 < Math.Min(3.0 * m * q2 - Math.Abs(tol1 * q2), Math.Abs(e * q2)))
                {
                    e = d;
                    d = p2 / q2;
                }
                else
                {
                    d = m; e = m;
                }
            }
            else
            {
                d = m; e = m;
            }

            a = b; fa = fb;
            if (Math.Abs(d) > tol1)
                b += d;
            else
                b += (m > 0 ? tol1 : -tol1);

            fb = f(b);

            // If function evaluation failed (NaN), bisect toward the valid side
            if (double.IsNaN(fb))
            {
                b = 0.5 * (a + c);
                fb = f(b);
                if (double.IsNaN(fb))
                {
                    // Both sides failing — return best known point
                    return a;
                }
                d = b - a; e = d;
            }
        }

        return b;
    }

    /// <summary>
    /// Try to find a valid bracket [a,b] where f(a) and f(b) have opposite signs
    /// and neither is NaN. Samples points within the original interval.
    /// </summary>
    private static bool TryFindBracket(Func<double, double> f,
        ref double a, ref double b, ref double fa, ref double fb, int nSamples)
    {
        double origA = a, origB = b;
        var points = new List<(double x, double fx)>();

        // Sample uniformly across the interval
        for (int i = 0; i <= nSamples; i++)
        {
            double x = origA + (origB - origA) * i / nSamples;
            double fx = f(x);
            if (!double.IsNaN(fx) && !double.IsInfinity(fx))
                points.Add((x, fx));
        }

        if (points.Count < 2) return false;

        // Find a pair with opposite signs (largest interval preferred)
        for (int i = 0; i < points.Count - 1; i++)
        {
            for (int j = points.Count - 1; j > i; j--)
            {
                if (points[i].fx * points[j].fx <= 0)
                {
                    a = points[i].x; fa = points[i].fx;
                    b = points[j].x; fb = points[j].fx;
                    return true;
                }
            }
        }

        // No sign change found — pick the closest-to-zero point
        var best = points.OrderBy(p => Math.Abs(p.fx)).First();
        a = best.x; fa = best.fx;
        b = best.x; fb = best.fx;
        return false;
    }

    // ─── Timeout protection ──────────────────────────────────────────

    /// <summary>
    /// Runs a flash calculation on a background thread with a timeout.
    /// If the Fortran call hangs (e.g. density solver infinite loop),
    /// the library is marked as faulted after the timeout expires.
    /// The background thread may leak, but the host process survives.
    /// </summary>
    private T RunWithTimeout<T>(Func<T> work, string operation)
    {
        CheckFaulted();

        if (FlashTimeoutMs <= 0)
            return work();

        T result = default!;
        Exception? workerException = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                workerException = ex;
            }
        });
        thread.IsBackground = true;
        thread.Start();

        if (thread.Join(FlashTimeoutMs))
        {
            // Thread completed within timeout
            if (workerException != null)
                ExceptionDispatchInfo.Capture(workerException).Throw();
            return result;
        }

        // Timeout expired — Fortran is stuck, the static lock is held by the
        // stuck worker thread so ALL engines must fail fast from now on.
        _faulted = true;
        throw new InvalidOperationException(
            $"{operation} timed out after {FlashTimeoutMs}ms. " +
            "The Fortran solver appears to be stuck (e.g. GERG-2008 density solver infinite loop). " +
            "All ThermoPack engines are disabled — restart the application to recover.");
    }

    // ─── Fortran lock helpers ────────────────────────────────────────

    /// <summary>
    /// Acquires the Fortran lock with a timeout. Checks faulted state first
    /// to avoid blocking on a lock permanently held by a stuck worker thread.
    /// Caller MUST use try/finally { ReleaseFortranLock(); }.
    /// </summary>
    private static void AcquireFortranLock()
    {
        CheckFaulted();
        int timeout = FlashTimeoutMs > 0 ? FlashTimeoutMs : 30000;
        bool taken = false;
        Monitor.TryEnter(_lock, timeout, ref taken);
        if (!taken)
        {
            _faulted = true;
            throw new InvalidOperationException(
                "Fortran lock acquisition timed out — a previous calculation may be stuck. " +
                "All ThermoPack engines are disabled — restart the application to recover.");
        }
    }

    private static void ReleaseFortranLock() => Monitor.Exit(_lock);

    // ─── Fortran crash protection ────────────────────────────────────

    public static bool IsFaulted => _faulted;

    private static void CheckFaulted()
    {
        if (_faulted)
            throw new InvalidOperationException(
                "Fortran library has faulted (a previous calculation timed out or crashed). " +
                "All ThermoPack engines are disabled — restart the application to recover.");
    }

    /// <summary>
    /// Execute a Fortran call with SEH protection. On AccessViolationException
    /// (segfault), marks the engine as faulted and throws a managed exception
    /// instead of crashing the host process.
    /// </summary>
    [HandleProcessCorruptedStateExceptions]
    [SecurityCritical]
    private void SafeCall(Action action, string operation)
    {
        try
        {
            action();
        }
        catch (AccessViolationException)
        {
            _faulted = true;
            throw new InvalidOperationException(
                $"Fortran library crashed (access violation) during {operation}. " +
                "The engine is no longer usable.");
        }
        catch (SEHException ex)
        {
            _faulted = true;
            throw new InvalidOperationException(
                $"Fortran library crashed (SEH 0x{ex.ErrorCode:X8}) during {operation}. " +
                "The engine is no longer usable.");
        }
    }

    /// <summary>
    /// Execute a Fortran call that returns a value, with SEH protection.
    /// </summary>
    [HandleProcessCorruptedStateExceptions]
    [SecurityCritical]
    private T SafeCall<T>(Func<T> func, string operation)
    {
        try
        {
            return func();
        }
        catch (AccessViolationException)
        {
            _faulted = true;
            throw new InvalidOperationException(
                $"Fortran library crashed (access violation) during {operation}. " +
                "The engine is no longer usable.");
        }
        catch (SEHException ex)
        {
            _faulted = true;
            throw new InvalidOperationException(
                $"Fortran library crashed (SEH 0x{ex.ErrorCode:X8}) during {operation}. " +
                "The engine is no longer usable.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_modelIndex > 0 && !_faulted)
        {
            try
            {
                bool taken = false;
                Monitor.TryEnter(_lock, 2000, ref taken);
                if (taken)
                {
                    try { _deleteEos!(ref _modelIndex); }
                    finally { Monitor.Exit(_lock); }
                }
                // If lock acquisition fails (stuck thread), skip cleanup silently
            }
            catch { }
        }
    }
}
