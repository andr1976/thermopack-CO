using System.Runtime.InteropServices;
using System.Text;
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

        // Create a new EOS slot
        var addEos = _lib.GetModuleDelegate<ThermoPackInterop.AddEosDelegate>(
            "thermopack_var", "add_eos");
        lock (_lock)
        {
            _modelIndex = addEos();
        }
    }

    private void Activate()
    {
        _activateModel!(ref _modelIndex);
    }

    // ─── Initialization methods ───────────────────────────────────────

    public void InitCubic(string comps, string eos, string mixing = "vdW",
        string alpha = "Classic", string paramRef = "", bool volShift = false)
    {
        lock (_lock)
        {
            Activate();
            var compsB = ToFortranString(comps);
            var eosB = ToFortranString(eos);
            var mixingB = ToFortranString(mixing);
            var alphaB = ToFortranString(alpha);
            var paramRefB = ToFortranString(paramRef);
            int vs = volShift ? _trueInt : 0;

            _initCubic!(compsB, eosB, mixingB, alphaB, paramRefB, ref vs,
                (UIntPtr)comps.Length, (UIntPtr)eos.Length, (UIntPtr)mixing.Length,
                (UIntPtr)alpha.Length, (UIntPtr)paramRef.Length);

            _nc = CountComponents(comps);
        }
    }

    public void InitTcPR(string comps, string mixing = "vdW", string paramRef = "")
    {
        lock (_lock)
        {
            Activate();
            var compsB = ToFortranString(comps);
            var mixingB = ToFortranString(mixing);
            var paramRefB = ToFortranString(paramRef);

            _initTcPR!(compsB, mixingB, paramRefB,
                (UIntPtr)comps.Length, (UIntPtr)mixing.Length, (UIntPtr)paramRef.Length);

            _nc = CountComponents(comps);
        }
    }

    public void InitCpa(string comps, string eos = "SRK", string mixing = "vdW",
        string alpha = "Classic", string paramRef = "")
    {
        lock (_lock)
        {
            Activate();
            var compsB = ToFortranString(comps);
            var eosB = ToFortranString(eos);
            var mixingB = ToFortranString(mixing);
            var alphaB = ToFortranString(alpha);
            var paramRefB = ToFortranString(paramRef);

            _initCpa!(compsB, eosB, mixingB, alphaB, paramRefB,
                (UIntPtr)comps.Length, (UIntPtr)eos.Length, (UIntPtr)mixing.Length,
                (UIntPtr)alpha.Length, (UIntPtr)paramRef.Length);

            _nc = CountComponents(comps);
        }
    }

    public void InitPcSaft(string comps, string paramRef = "",
        bool simplified = false, bool polar = false)
    {
        lock (_lock)
        {
            Activate();
            var compsB = ToFortranString(comps);
            var paramRefB = ToFortranString(paramRef);
            int simplifiedInt = simplified ? _trueInt : 0;
            int polarInt = polar ? _trueInt : 0;

            _initPcSaft!(compsB, paramRefB, ref simplifiedInt, ref polarInt,
                (UIntPtr)comps.Length, (UIntPtr)paramRef.Length);

            _nc = CountComponents(comps);
        }
    }

    public void InitMultiparameter(string comps, string meos = "NIST_MEOS",
        string refState = "DEFAULT")
    {
        lock (_lock)
        {
            Activate();
            var compsB = ToFortranString(comps);
            var meosB = ToFortranString(meos);
            var refStateB = ToFortranString(refState);

            _initMultiparameter!(compsB, meosB, refStateB,
                (UIntPtr)comps.Length, (UIntPtr)meos.Length, (UIntPtr)refState.Length);

            _nc = CountComponents(comps);
        }
    }

    // ─── Flash calculations ───────────────────────────────────────────

    public FlashResult TwoPhaseTPFlash(double T, double P, double[] z)
    {
        lock (_lock)
        {
            return TPFlashUnsafe(T, P, z);
        }
    }

    /// <summary>TP flash without locking. Caller must hold _lock.</summary>
    private FlashResult TPFlashUnsafe(double T, double P, double[] z)
    {
        Activate();
        double betaV = 0, betaL = 0;
        int phase = 0;
        var x = new double[_nc];
        var y = new double[_nc];

        _tpFlash!(ref T, ref P, z, ref betaV, ref betaL, ref phase, x, y);

        return new FlashResult
        {
            Temperature = T, Pressure = P,
            BetaV = betaV, BetaL = betaL,
            Phase = phase, X = x, Y = y
        };
    }

    public FlashResult TwoPhasePHFlash(double P, double[] z, double h, double tempGuess = 298.15)
    {
        lock (_lock)
        {
            Activate();
            double T = tempGuess;
            double betaV = 0, betaL = 0;
            int phase = 0, ierr = 0;
            var x = new double[_nc];
            var y = new double[_nc];

            _phFlash!(ref T, ref P, z, ref betaV, ref betaL, x, y, ref h, ref phase, ref ierr);

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
    }

    public FlashResult TwoPhasePSFlash(double P, double[] z, double s, double tempGuess = 298.15)
    {
        lock (_lock)
        {
            Activate();
            double T = tempGuess;
            double betaV = 0, betaL = 0;
            int phase = 0, ierr = 0;
            var x = new double[_nc];
            var y = new double[_nc];

            _psFlash!(ref T, ref P, z, ref betaV, ref betaL, x, y, ref s, ref phase, ref ierr);

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
    }

    public FlashResult TwoPhaseUVFlash(double[] z, double u, double v,
        double tempGuess = 298.15, double pressGuess = 101325.0)
    {
        lock (_lock)
        {
            Activate();
            double T = tempGuess, P = pressGuess;
            double betaV = 0, betaL = 0;
            int phase = 0;
            var x = new double[_nc];
            var y = new double[_nc];

            _uvFlash!(ref T, ref P, z, ref betaV, ref betaL, x, y, ref u, ref v, ref phase);

            return new FlashResult
            {
                Temperature = T, Pressure = P,
                BetaV = betaV, BetaL = betaL,
                Phase = phase, X = x, Y = y
            };
        }
    }

    // ─── Saturation points ────────────────────────────────────────────

    public (double T, double[] y) BubbleTemperature(double P, double[] z)
    {
        lock (_lock) { return BubbleTemperatureUnsafe(P, z); }
    }

    public (double P, double[] y) BubblePressure(double T, double[] z)
    {
        lock (_lock) { return BubblePressureUnsafe(T, z); }
    }

    public (double T, double[] x) DewTemperature(double P, double[] z)
    {
        lock (_lock) { return DewTemperatureUnsafe(P, z); }
    }

    public (double P, double[] x) DewPressure(double T, double[] z)
    {
        lock (_lock) { return DewPressureUnsafe(T, z); }
    }

    private (double T, double[] y) BubbleTemperatureUnsafe(double P, double[] z)
    {
        Activate();
        var y = new double[_nc];
        int ierr = 0;
        double T = _safeBubT!(ref P, z, y, ref ierr);
        if (ierr != 0)
            throw new InvalidOperationException($"BubbleTemperature failed (ierr={ierr})");
        return (T, y);
    }

    private (double P, double[] y) BubblePressureUnsafe(double T, double[] z)
    {
        Activate();
        var y = new double[_nc];
        int ierr = 0;
        double P = _safeBubP!(ref T, z, y, ref ierr);
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
        double T = _safeDewT!(ref P, x, z, ref ierr);
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
        double P = _safeDewP!(ref T, x, z, ref ierr);
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
        lock (_lock)
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
    }

    /// <summary>
    /// Flash at given T, z, and target vapor fraction.
    /// Uses saturation endpoints + Brent's method.
    /// </summary>
    public FlashResult TVFFlash(double T, double[] z, double betaVTarget)
    {
        lock (_lock)
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
    }

    // ─── Property calculations ────────────────────────────────────────

    public double Enthalpy(double T, double P, double[] x, int phase)
    {
        lock (_lock)
        {
            Activate();
            double h = 0;
            int flag = 0; // residual=false → full enthalpy (ideal + residual)
            _enthalpy!(ref T, ref P, x, ref phase, ref h,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref flag);
            return h;
        }
    }

    public (double h, double dhdt) EnthalpyWithCp(double T, double P, double[] x, int phase)
    {
        lock (_lock)
        {
            Activate();
            double h = 0, dhdt = 0;
            int flag = 0; // residual=false → full enthalpy
            _enthalpyWithDhdt!(ref T, ref P, x, ref phase, ref h, ref dhdt,
                IntPtr.Zero, IntPtr.Zero, ref flag);
            return (h, dhdt);
        }
    }

    public double Entropy(double T, double P, double[] x, int phase)
    {
        lock (_lock)
        {
            Activate();
            double s = 0;
            int flag = 0; // residual=false → full entropy (ideal + residual)
            _entropy!(ref T, ref P, x, ref phase, ref s,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref flag);
            return s;
        }
    }

    public double SpecificVolume(double T, double P, double[] x, int phase)
    {
        lock (_lock)
        {
            Activate();
            double v = 0;
            _specificVolume!(ref T, ref P, x, ref phase, ref v,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return v;
        }
    }

    public double[] LnFugacityCoefficients(double T, double P, double[] x, int phase)
    {
        lock (_lock)
        {
            Activate();
            var lnfug = new double[_nc];
            int ophase = 0, meta = 0;
            double v = 0;
            _thermo!(ref T, ref P, x, ref phase, lnfug,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                ref ophase, ref meta, ref v);
            return lnfug;
        }
    }

    public double ZFac(double T, double P, double[] x, int phase)
    {
        lock (_lock)
        {
            Activate();
            double z = 0;
            _zfac!(ref T, ref P, x, ref phase, ref z,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return z;
        }
    }

    public double CompMoleWeight(int index)
    {
        lock (_lock)
        {
            Activate();
            int j = index; // 1-based Fortran index
            return _compMoleWeight!(ref j);
        }
    }

    public (double Tc, double Pc, double omega, double Vc, double Tnb) GetCriticalParam(int index)
    {
        lock (_lock)
        {
            Activate();
            int i = index;
            double tc = 0, pc = 0, omega = 0, vc = 0, tnb = 0;
            _getCriticalParam!(ref i, ref tc, ref pc, ref omega, ref vc, ref tnb);
            return (tc, pc, omega, vc, tnb);
        }
    }

    public double GetRgas()
    {
        lock (_lock)
        {
            Activate();
            return _getRgas!();
        }
    }

    public double IdealEnthalpySingle(double T, int compIdx)
    {
        lock (_lock)
        {
            Activate();
            double hId = 0;
            _idealEnthalpySingle!(ref T, ref compIdx, ref hId, IntPtr.Zero);
            return hId;
        }
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
        lock (_lock)
        {
            Activate();
            int phase = 0;
            _guessPhase(ref T, ref P, z, ref phase);
            return phase;
        }
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
        lock (_lock)
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
    }

    private void LoadFortranTrue()
    {
        try
        {
            var getTrue = _lib.GetModuleDelegate<ThermoPackInterop.GetTrueDelegate>(
                "thermopack_constants", "get_true");
            int val = 0;
            lock (_lock)
            {
                getTrue(ref val);
            }
            _trueInt = val;
        }
        catch
        {
            _trueInt = 1; // Default gfortran
        }
    }

    /// <summary>
    /// Brent's method for root finding. Finds x such that f(x) = 0 in [a, b].
    /// </summary>
    private static double BrentSolve(Func<double, double> f, double a, double b,
        double tol, int maxIter)
    {
        double fa = f(a);
        double fb = f(b);

        if (fa * fb > 0)
        {
            // Try to proceed anyway; use midpoint as fallback
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
        }

        return b;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_modelIndex > 0)
        {
            try
            {
                lock (_lock)
                {
                    _deleteEos!(ref _modelIndex);
                }
            }
            catch { }
        }
    }
}
