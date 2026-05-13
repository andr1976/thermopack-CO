using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using CAPEOPEN;
using ThermoPack.Core;
using ThermoPack.Core.Models;
using ThermoPack.Editor;

namespace ThermoPack.CapeOpen;

/// <summary>
/// Abstract base class for all ThermoPack CAPE-OPEN 1.1 property packages.
/// Implements all required CAPE-OPEN interfaces.
/// </summary>
public abstract class ThermoPackPropertyPackageBase :
    ICapeIdentification,
    ICapeThermoMaterialContext,
    ICapeThermoPhases,
    ICapeThermoCompounds,
    ICapeThermoPropertyRoutine,
    ICapeThermoEquilibriumRoutine,
    ICapeThermoUniversalConstant,
    ICapeUtilities,
    IPersistStreamInit,
    IDisposable
{
    // ─── Constants ────────────────────────────────────────────────────

    private const int StreamVersion = 1;
    private const int CapeAtEquilibrium = 1; // eCapeAtEquilibrium

    // CAPE-OPEN HRESULT codes
    private const int ECapeUnknown = unchecked((int)0x80040501);
    private const int ECapeNoImpl = unchecked((int)0x80040507);
    private const int ECapeCalculation = unchecked((int)0x8004050C);

    // Phase labels
    private const string PhaseVapour = "Vapour";
    private const string PhaseLiquid = "Liquid";

    // ─── Shared static state ──────────────────────────────────────────

    private static ThermoPackLibrary? _sharedLib;
    private static ComponentDatabase? _sharedDb;
    private static readonly object _staticLock = new();
    private static string? _fluidsDir;
    private static string? _logPath;

    // Shared component selection: COFE creates multiple instances (master + solving).
    // The master gets Edit()/Load() calls; solving instances must inherit the selection.
    private static List<string>? _sharedSelectedCas;

    // ─── Instance state ───────────────────────────────────────────────

    private ThermoPackEngine? _engine;
    private ICapeThermoMaterial? _material;
    protected List<Component> _selectedComponents = new();
    private bool _isDirty;
    private string? _initError;

    // COM object references (must be released via Marshal.ReleaseComObject)
    private object? _simulationContext;

    // Flash cache
    private FlashResult? _lastResult;
    private double _lastT;
    private double _lastP;
    private double[]? _lastFeed;

    // ─── Abstract members for EOS-specific packages ───────────────────

    protected abstract string PackageName { get; }
    protected abstract string PackageDescription { get; }
    protected abstract EosType EosType { get; }
    protected abstract Guid PackageClsid { get; }
    protected abstract void InitializeEngine(ThermoPackEngine engine, string compString);

    // ─── ICapeIdentification ──────────────────────────────────────────

    public string ComponentName
    {
        get => PackageName;
        set { }
    }

    public string ComponentDescription
    {
        get => PackageDescription;
        set { }
    }

    // ─── ICapeThermoMaterialContext ───────────────────────────────────

    public void SetMaterial(object material)
    {
        ReleaseMaterial();
        _material = material as ICapeThermoMaterial;
    }

    public void UnsetMaterial()
    {
        ReleaseMaterial();
    }

    private void ReleaseMaterial()
    {
        if (_material != null)
        {
            try { Marshal.ReleaseComObject(_material); } catch { }
            _material = null;
        }
        _lastResult = null;
    }

    // ─── ICapeThermoPhases ────────────────────────────────────────────

    public int GetNumPhases()
    {
        return 2;
    }

    public void GetPhaseList(ref object phaseLabels, ref object stateOfAggregation,
        ref object keyCompoundId)
    {
        phaseLabels = new[] { PhaseVapour, PhaseLiquid };
        stateOfAggregation = new[] { "Vapor", "Liquid" };
        keyCompoundId = new[] { "", "" };
    }

    public object GetPhaseInfo(string phaseLabel, string phaseAttribute)
    {
        if (phaseAttribute.Equals("StateOfAggregation", StringComparison.OrdinalIgnoreCase))
        {
            return phaseLabel == PhaseVapour ? "Vapor" : "Liquid";
        }
        throw new COMException($"Unknown phase attribute: {phaseAttribute}");
    }

    // ─── ICapeThermoCompounds ─────────────────────────────────────────

    public int GetNumCompounds()
    {
        return _selectedComponents.Count;
    }

    public void GetCompoundList(ref object compIds, ref object formulae,
        ref object names, ref object boilTemps, ref object molwts, ref object casNos)
    {
        int nc = _selectedComponents.Count;
        if (nc == 0)
        {
            compIds = null!; formulae = null!; names = null!;
            boilTemps = null!; molwts = null!; casNos = null!;
            return;
        }

        var ids = new string[nc];
        var forms = new string[nc];
        var nms = new string[nc];
        var bts = new double[nc];
        var mws = new double[nc];
        var cas = new string[nc];

        for (int i = 0; i < nc; i++)
        {
            var c = _selectedComponents[i];
            ids[i] = c.CasNumber;
            forms[i] = c.Formula;
            nms[i] = c.Name;
            bts[i] = c.BoilingTemperature;
            mws[i] = c.MolWeight;
            cas[i] = c.CasNumber;
        }

        compIds = ids;
        formulae = forms;
        names = nms;
        boilTemps = bts;
        molwts = mws;
        casNos = cas;
    }

    public object GetCompoundConstant(object props, object compIds)
    {
        var propNames = (string[])props;
        var ids = (string[])compIds;
        int nc = ids.Length;
        int np = propNames.Length;

        var result = new object[np];
        for (int p = 0; p < np; p++)
        {
            string propLower = propNames[p].ToLowerInvariant();
            if (propLower == "casregistrynumber")
            {
                var strVals = new string[nc];
                for (int j = 0; j < nc; j++)
                {
                    var c2 = FindComponentByCas(ids[j]);
                    strVals[j] = c2?.CasNumber ?? "";
                }
                result[p] = strVals;
                continue;
            }

            var values = new double[nc];
            for (int i = 0; i < nc; i++)
            {
                var comp = FindComponentByCas(ids[i]);
                if (comp == null) continue;

                values[i] = propLower switch
                {
                    "molecularweight" => comp.MolWeight,
                    "criticaltemperature" => comp.CriticalTemperature,
                    "criticalpressure" => comp.CriticalPressure,
                    "acentricfactor" => comp.AcentricFactor,
                    _ => 0
                };
            }
            result[p] = values;
        }
        return result;
    }

    public object GetConstPropList()
    {
        return new[] { "molecularWeight", "criticalTemperature", "criticalPressure",
            "acentricFactor", "casRegistryNumber" };
    }

    public object GetTDependentPropList()
    {
        return new[] { "idealGasHeatCapacity" };
    }

    public object GetPDependentPropList()
    {
        return null!;
    }

    public void GetPDependentProperty(object props, double pressure, object compIds,
        ref object propVals)
    {
        throw new COMException("P-dependent properties not supported");
    }

    public void GetTDependentProperty(object props, double temperature, object compIds,
        ref object propVals)
    {
        var propNames = (string[])props;
        var ids = (string[])compIds;
        int nc = ids.Length;
        int np = propNames.Length;

        var result = new object[np];
        for (int p = 0; p < np; p++)
        {
            var values = new double[nc];
            switch (propNames[p].ToLowerInvariant())
            {
                case "idealgasheatcapacity":
                    if (_engine != null)
                    {
                        for (int i = 0; i < nc; i++)
                        {
                            int idx = FindFortranIndex(ids[i]);
                            if (idx > 0)
                            {
                                // Cp_id = dH_id/dT
                                double T1 = temperature;
                                double T2 = temperature + 0.01;
                                double h1 = _engine.IdealEnthalpySingle(T1, idx);
                                double h2 = _engine.IdealEnthalpySingle(T2, idx);
                                values[i] = (h2 - h1) / 0.01;
                            }
                        }
                    }
                    break;
                default:
                    break;
            }
            result[p] = values;
        }
        propVals = result;
    }

    // ─── ICapeThermoEquilibriumRoutine ─────────────────────────────────

    public void CalcEquilibrium(object specification1, object specification2,
        string solutionType)
    {
        if (_material == null) return;
        if (_selectedComponents.Count == 0)
            throw new COMException("No compounds configured. Use Edit() to select compounds first.",
                ECapeCalculation);

        try
        {
            CalcEquilibriumCore(specification1, specification2);
        }
        catch (COMException)
        {
            throw; // Already a proper COM exception
        }
        catch (Exception ex)
        {
            throw new COMException($"CalcEquilibrium failed: {ex.Message}", ECapeCalculation);
        }
    }

    private void CalcEquilibriumCore(object specification1, object specification2)
    {
        EnsureInitialized();

        var spec = ValidateFlashSpec(specification1, specification2);
        var wrapper = new MaterialObjectWrapper(_material!);
        int nc = _selectedComponents.Count;
        double[] rawFeed = wrapper.GetFeed(nc);

        // Check for zero flow
        double rawSum = 0;
        for (int i = 0; i < Math.Min(rawFeed.Length, nc); i++)
            rawSum += Math.Abs(rawFeed[i]);

        if (rawSum < 1e-12)
        {
            SetZeroFlowDefault(wrapper, nc);
            return;
        }

        double[] feed = NormalizeFeed(rawFeed, nc);

        double T = wrapper.GetTemperature();
        double P = wrapper.GetPressure();
        if (double.IsNaN(T) || T < 1.0) T = 298.15;
        if (double.IsNaN(P) || P < 1.0) P = 101325.0;

        double targetValue = double.NaN;

        switch (spec.Type)
        {
            case FlashType.PT:
                break;
            case FlashType.PH:
            case FlashType.PS:
            case FlashType.PVF:
            case FlashType.TVF:
                targetValue = wrapper.ReadTargetValue(
                    spec.TargetProperty, spec.TargetBasis, spec.TargetCalcType);
                break;
        }

        // Bootstrap for PH/PS if target is NaN
        if ((spec.Type == FlashType.PH || spec.Type == FlashType.PS) && double.IsNaN(targetValue))
        {
            var bootResult = _engine!.TwoPhaseTPFlash(T, P, feed);
            NormalizeFlashResult(bootResult, feed, nc);
            double bT = bootResult.Temperature;
            double bP = bootResult.Pressure;

            if (spec.Type == FlashType.PH)
            {
                targetValue = 0;
                if (bootResult.BetaL > 1e-12)
                    targetValue += bootResult.BetaL * _engine.Enthalpy(bT, bP, bootResult.X, _engine.LiquidPhase);
                if (bootResult.BetaV > 1e-12)
                    targetValue += bootResult.BetaV * _engine.Enthalpy(bT, bP, bootResult.Y, _engine.VaporPhase);
            }
            else
            {
                targetValue = 0;
                if (bootResult.BetaL > 1e-12)
                    targetValue += bootResult.BetaL * _engine.Entropy(bT, bP, bootResult.X, _engine.LiquidPhase);
                if (bootResult.BetaV > 1e-12)
                    targetValue += bootResult.BetaV * _engine.Entropy(bT, bP, bootResult.Y, _engine.VaporPhase);
            }
        }

        // PVF/TVF: default to 0 if no target found
        if ((spec.Type == FlashType.PVF || spec.Type == FlashType.TVF) && double.IsNaN(targetValue))
        {
            targetValue = wrapper.GetPhaseFraction(PhaseVapour);
            if (double.IsNaN(targetValue)) targetValue = 0.0;
        }

        Log($"[{PackageName}] CalcEquilibrium: type={spec.Type}, T={T:F2}, P={P:F0}, " +
            $"target={targetValue:G6}, feed=[{string.Join(",", feed.Select(f => f.ToString("G6")))}]");

        FlashResult result;
        switch (spec.Type)
        {
            case FlashType.PT:
                Log($"[{PackageName}] Calling TwoPhaseTPFlash...");
                result = _engine!.TwoPhaseTPFlash(T, P, feed);
                break;
            case FlashType.PH:
                Log($"[{PackageName}] Calling TwoPhasePHFlashSafe(P={P:F0}, h={targetValue:G6}, Tguess={T:F2})...");
                result = _engine!.TwoPhasePHFlashSafe(P, feed, targetValue, T);
                break;
            case FlashType.PS:
                Log($"[{PackageName}] Calling TwoPhasePSFlashSafe(P={P:F0}, s={targetValue:G6}, Tguess={T:F2})...");
                result = _engine!.TwoPhasePSFlashSafe(P, feed, targetValue, T);
                break;
            case FlashType.UV:
            {
                double uTarget = wrapper.ReadTargetValue("internalEnergy", "", "Overall");
                double vTarget = wrapper.ReadTargetValue("volume", "", "Overall");
                Log($"[{PackageName}] Calling TwoPhaseUVFlash(u={uTarget:G6}, v={vTarget:G6})...");
                result = _engine!.TwoPhaseUVFlash(feed, uTarget, vTarget, T, P);
                break;
            }
            case FlashType.PVF:
                Log($"[{PackageName}] Calling PVFFlash(P={P:F0}, vf={targetValue:G6})...");
                result = _engine!.PVFFlash(P, feed, targetValue);
                break;
            case FlashType.TVF:
                Log($"[{PackageName}] Calling TVFFlash(T={T:F2}, vf={targetValue:G6})...");
                result = _engine!.TVFFlash(T, feed, targetValue);
                break;
            default:
                throw new COMException($"Unsupported flash type: {spec.Type}", ECapeUnknown);
        }

        Log($"[{PackageName}] Flash returned: T={result.Temperature:F2}, P={result.Pressure:F0}, " +
            $"betaV={result.BetaV:G6}, betaL={result.BetaL:G6}, phase={result.Phase}, " +
            $"X=[{string.Join(",", result.X?.Select(v => v.ToString("G6")) ?? Array.Empty<string>())}], " +
            $"Y=[{string.Join(",", result.Y?.Select(v => v.ToString("G6")) ?? Array.Empty<string>())}]");

        // Normalize single-phase results before caching
        NormalizeFlashResult(result, feed, nc);
        Log($"[{PackageName}] After normalize: betaV={result.BetaV:G6}, betaL={result.BetaL:G6}, " +
            $"X=[{string.Join(",", result.X.Select(v => v.ToString("G6")))}], " +
            $"Y=[{string.Join(",", result.Y.Select(v => v.ToString("G6")))}]");

        // Cache
        _lastResult = result;
        _lastT = result.Temperature;
        _lastP = result.Pressure;
        _lastFeed = (double[])feed.Clone();

        // Write back to material
        Log($"[{PackageName}] Writing flash result to material...");
        WriteFlashResultToMaterial(wrapper, result, nc, feed, spec.Type);
        Log($"[{PackageName}] CalcEquilibrium complete.");
    }

    public bool CheckEquilibriumSpec(object specification1, object specification2, string solutionType)
    {
        try { ValidateFlashSpec(specification1, specification2); return true; }
        catch { return false; }
    }

    // ─── ICapeThermoPropertyRoutine ───────────────────────────────────

    public void CalcSinglePhaseProp(object props, string phaseLabel)
    {
        if (_material == null) throw new COMException("Material not set", ECapeUnknown);
        if (_selectedComponents.Count == 0) throw new COMException("No compounds configured.", ECapeUnknown);

        try
        {
            EnsureFlashResult();

            var propNames = (string[])props;
            var wrapper = new MaterialObjectWrapper(_material);

            Log($"[{PackageName}] CalcSinglePhaseProp: phase={phaseLabel}, props=[{string.Join(",", propNames)}], " +
                $"T={_lastResult?.Temperature:F2}, P={_lastResult?.Pressure:F0}");

            foreach (var prop in propNames)
            {
                double[] values = GetSinglePhasePropertyValues(prop, phaseLabel, wrapper);
                string basis = GetPropertyBasis(prop);
                wrapper.SetSinglePhaseProp(prop, phaseLabel, basis, values);
            }
        }
        catch (COMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"CalcSinglePhaseProp FAILED for phase {phaseLabel}: {ex.Message}");
            throw new COMException(
                $"CalcSinglePhaseProp failed for phase {phaseLabel}: {ex.Message}",
                ECapeCalculation);
        }
    }

    public void CalcTwoPhaseProp(object props, object phaseLabels)
    {
        if (_material == null) throw new COMException("Material not set", ECapeUnknown);
        EnsureFlashResult();
        EnsureInitialized();

        var propNames = (string[])props;
        var phases = (string[])phaseLabels;
        var wrapper = new MaterialObjectWrapper(_material);
        int nc = _selectedComponents.Count;

        foreach (var prop in propNames)
        {
            string propLower = prop.ToLowerInvariant();

            if (propLower == "kvalue")
            {
                if (_lastResult != null && _lastResult.X.Length > 0 && _lastResult.Y.Length > 0)
                {
                    var kvalues = new double[nc];
                    for (int i = 0; i < nc; i++)
                    {
                        kvalues[i] = (_lastResult.X[i] > 1e-30)
                            ? _lastResult.Y[i] / _lastResult.X[i]
                            : 0.0;
                    }
                    _material.SetTwoPhaseProp(prop, phases, "", kvalues);
                }
            }
            else if (propLower == "bubblepointpressure" || propLower == "dewpointpressure")
            {
                if (_engine != null && _lastResult != null)
                {
                    try
                    {
                        double T = _lastResult.Temperature;
                        double[] feed = _lastFeed ?? new double[nc];
                        double pVal;
                        if (propLower == "bubblepointpressure")
                        {
                            var (pBub, _) = _engine.BubblePressure(T, feed);
                            pVal = pBub;
                        }
                        else
                        {
                            var (pDew, _) = _engine.DewPressure(T, feed);
                            pVal = pDew;
                        }
                        _material.SetTwoPhaseProp(prop, phases, "", new double[] { pVal });
                    }
                    catch { /* bubble/dew may fail for some systems */ }
                }
            }
            else if (propLower == "bubblepointtemperature" || propLower == "dewpointtemperature")
            {
                if (_engine != null && _lastResult != null)
                {
                    try
                    {
                        double P = _lastResult.Pressure;
                        double[] feed = _lastFeed ?? new double[nc];
                        double tVal;
                        if (propLower == "bubblepointtemperature")
                        {
                            var (tBub, _) = _engine.BubbleTemperature(P, feed);
                            tVal = tBub;
                        }
                        else
                        {
                            var (tDew, _) = _engine.DewTemperature(P, feed);
                            tVal = tDew;
                        }
                        _material.SetTwoPhaseProp(prop, phases, "", new double[] { tVal });
                    }
                    catch { /* bubble/dew may fail for some systems */ }
                }
            }
        }
    }

    public void CalcAndGetLnPhi(string phaseLabel, double temperature, double pressure,
        object moleNumbers, int fFlags, ref object lnPhi, ref object lnPhiDT,
        ref object lnPhiDP, ref object lnPhiDn)
    {
        if (_selectedComponents.Count == 0)
            throw new COMException("No compounds configured.");

        EnsureInitialized();
        if (_engine == null) throw new COMException("Engine not initialized.");

        var moles = (double[])moleNumbers;
        int nc = _selectedComponents.Count;
        double total = 0;
        int len = Math.Min(moles.Length, nc);
        for (int i = 0; i < len; i++) total += moles[i];
        if (total <= 0) { lnPhi = new double[nc]; return; }

        var feed = new double[nc];
        for (int i = 0; i < len; i++) feed[i] = moles[i] / total;

        int phase = (phaseLabel == PhaseVapour || fFlags == 1)
            ? _engine.VaporPhase : _engine.LiquidPhase;
        lnPhi = _engine.LnFugacityCoefficients(temperature, pressure, feed, phase);
    }

    public bool CheckSinglePhasePropSpec(string property, string phaseLabel)
    {
        var supported = new[] { "enthalpy", "entropy", "internalenergy", "gibbsenergy",
            "heatcapacitycp", "volume", "density", "compressibilityfactor",
            "fugacitycoefficient", "logfugacitycoefficient", "fugacity",
            "molecularweight", "phasefraction", "fraction" };
        return Array.Exists(supported, p => p.Equals(property, StringComparison.OrdinalIgnoreCase));
    }

    public bool CheckTwoPhasePropSpec(string property, object phaseLabels)
    {
        var supported = new[] { "kvalue", "bubblepointpressure", "dewpointpressure",
            "bubblepointtemperature", "dewpointtemperature" };
        return Array.Exists(supported, p => p.Equals(property, StringComparison.OrdinalIgnoreCase));
    }

    public object GetSinglePhasePropList()
    {
        return new[]
        {
            "enthalpy", "entropy", "internalEnergy", "gibbsEnergy",
            "heatCapacityCp", "volume", "density", "compressibilityFactor",
            "fugacityCoefficient", "logFugacityCoefficient", "fugacity",
            "molecularWeight"
        };
    }

    public object GetTwoPhasePropList()
    {
        return new[] { "kvalue", "bubblePointPressure", "dewPointPressure",
            "bubblePointTemperature", "dewPointTemperature" };
    }

    // ─── ICapeThermoUniversalConstant ─────────────────────────────────

    public object GetUniversalConstant(string constantId)
    {
        return constantId.ToLowerInvariant() switch
        {
            "avogadroconstant" => 6.02214076e23,
            "boltzmannconstant" => 1.380649e-23,
            "molargasconstant" => _engine != null ? _engine.GetRgas() : 8.31446,
            _ => 0.0
        };
    }

    public object GetUniversalConstantList()
    {
        return new[] { "avogadroConstant", "boltzmannConstant", "molarGasConstant" };
    }

    // ─── ICapeUtilities ───────────────────────────────────────────────

    public object simulationContext
    {
        get => _simulationContext!;
        set
        {
            if (_simulationContext != null && !ReferenceEquals(_simulationContext, value))
            {
                try { Marshal.ReleaseComObject(_simulationContext); } catch { }
            }
            _simulationContext = value;
        }
    }

    public void Initialize()
    {
        Log($"Initialize() called on {GetType().Name}");
        AssemblyResolver.Initialize();
        EnsureStaticInit();
        Log($"Initialize(): sharedDb has {_sharedDb?.Components.Count ?? 0} components, sharedCas={_sharedSelectedCas?.Count ?? 0}");

        // If this is a solving instance (no components loaded yet), inherit from shared state
        if (_selectedComponents.Count == 0 && _sharedSelectedCas != null && _sharedSelectedCas.Count > 0)
        {
            _selectedComponents = ResolveSharedComponents(_sharedSelectedCas);
            Log($"Initialize(): inherited {_selectedComponents.Count} components from shared state");
            if (_selectedComponents.Count > 0)
                RecreateEngine();
        }
    }

    public int Edit()
    {
        try
        {
            Log($"Edit() called, EosType={EosType}");
            EnsureStaticInit();

            var available = _sharedDb!.GetComponentsForEos(EosType);
            Log($"Edit(): {available.Count} components available for {EosType}");
            var editorResult = EditorLauncher.ShowComponentEditor(
                available, _selectedComponents, EosType);

            if (editorResult != null)
            {
                Log($"Edit(): user selected {editorResult.Count} components");
                _selectedComponents = new List<Component>(editorResult);
                SyncShared();
                _isDirty = true;
                _lastResult = null;
                RecreateEngine();
                return 0; // S_OK — compounds changed
            }
            Log("Edit(): cancelled");
            return 1; // S_FALSE — cancelled
        }
        catch (Exception ex)
        {
            Log($"Edit() FAILED: {ex}");
            return 1; // S_FALSE — error
        }
    }

    public void Terminate()
    {
        ReleaseMaterial();
        if (_simulationContext != null)
        {
            try { Marshal.ReleaseComObject(_simulationContext); } catch { }
            _simulationContext = null;
        }
        _engine?.Dispose();
        _engine = null;
    }

    public object parameters
    {
        get
        {
            // Serialize EOS type + CAS list as string for COFE instance transfer
            var casString = string.Join(";", _selectedComponents.Select(c => c.CasNumber));
            var val = $"{(int)EosType};{casString}";
            Log($"parameters GET: {_selectedComponents.Count} components, value=\"{val}\"");
            return val;
        }
        set
        {
            Log($"parameters SET: value=\"{value}\"");
            if (value is string s && !string.IsNullOrWhiteSpace(s))
            {
                var tokens = s.Split(';');
                if (tokens.Length >= 2)
                {
                    EnsureStaticInit();
                    // First token is EOS type (ignored, we use our own)
                    var restored = new List<Component>();
                    for (int i = 1; i < tokens.Length; i++)
                    {
                        string cas = tokens[i].Trim();
                        if (cas.Length == 0) continue;
                        var comp = _sharedDb?.FindByCas(cas);
                        if (comp != null) restored.Add(comp);
                        else Log($"parameters SET: CAS '{cas}' not found in database");
                    }
                    if (restored.Count > 0)
                    {
                        _selectedComponents = restored;
                        SyncShared();
                        _lastResult = null;
                        RecreateEngine();
                        Log($"parameters SET: restored {restored.Count} components");
                    }
                    else
                    {
                        Log("parameters SET: no components resolved");
                    }
                }
            }
        }
    }

    // ─── IPersistStreamInit ───────────────────────────────────────────

    public void GetClassID(out Guid pClassID)
    {
        pClassID = PackageClsid;
    }

    public int IsDirty()
    {
        return _isDirty ? 0 : 1;
    }

    public void Load(IStream pStm)
    {
        Log("Load(IStream) called");
        try
        {
            using (var stream = new ComStreamWrapper(pStm))
            using (var reader = new BinaryReader(stream))
            {
                int version = reader.ReadInt32();
                if (version < 1) throw new InvalidDataException($"Unsupported stream version: {version}");

                int eosInt = reader.ReadInt32(); // Read but we use our own EosType
                int count = reader.ReadInt32();
                Log($"Load: version={version}, eos={eosInt}, count={count}");

                EnsureStaticInit();

                var compList = new List<Component>();
                for (int i = 0; i < count; i++)
                {
                    string cas = reader.ReadString();
                    var dbComp = _sharedDb!.FindByCas(cas);
                    if (dbComp != null) compList.Add(dbComp);
                    else Log($"Load: CAS '{cas}' not found in database");
                }

                _selectedComponents = compList;
                SyncShared();
                _isDirty = false;
                Log($"Load: restored {compList.Count} of {count} components");

                RecreateEngine();
            }
        }
        catch (Exception ex)
        {
            Log($"Load FAILED: {ex}");
            throw;
        }
    }

    public void Save(IStream pStm, [MarshalAs(UnmanagedType.Bool)] bool fClearDirty)
    {
        Log($"Save(IStream) called: {_selectedComponents.Count} components, clearDirty={fClearDirty}");
        using (var stream = new ComStreamWrapper(pStm))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(StreamVersion);
            writer.Write((int)EosType);
            writer.Write(_selectedComponents.Count);

            foreach (var c in _selectedComponents)
                writer.Write(c.CasNumber);

            writer.Flush();
        }
        if (fClearDirty) _isDirty = false;
    }

    public void GetSizeMax(out long pcbSize)
    {
        long size = 12; // version + eos + count
        foreach (var c in _selectedComponents)
            size += 4 + (c.CasNumber?.Length ?? 0) * 2;
        pcbSize = size;
    }

    public void InitNew()
    {
        _selectedComponents = new List<Component>();
        _isDirty = false;
    }

    // ─── IDisposable ──────────────────────────────────────────────────

    public void Dispose()
    {
        ReleaseMaterial();
        if (_simulationContext != null)
        {
            try { Marshal.ReleaseComObject(_simulationContext); } catch { }
            _simulationContext = null;
        }
        _engine?.Dispose();
        _engine = null;
    }

    // ─── COM Registration helpers (called by derived classes) ──────────

    protected static void RegisterPackage(Type t, string name, string description)
    {
        var clsid = t.GUID.ToString("B");
        var key = Microsoft.Win32.Registry.ClassesRoot.CreateSubKey(
            $@"CLSID\{clsid}\Implemented Categories\{{{CapeOpenCategories.PropertyPackage}}}");
        key?.Close();

        var desc = Microsoft.Win32.Registry.ClassesRoot.CreateSubKey(
            $@"CLSID\{clsid}\CapeDescription");
        desc?.SetValue("Name", name);
        desc?.SetValue("Description", description);
        desc?.SetValue("CapeVersion", "1.1");
        desc?.Close();
    }

    protected static void UnregisterPackage(Type t)
    {
        var clsid = t.GUID.ToString("B");
        try
        {
            Microsoft.Win32.Registry.ClassesRoot.DeleteSubKeyTree(
                $@"CLSID\{clsid}\Implemented Categories\{{{CapeOpenCategories.PropertyPackage}}}",
                false);
        }
        catch { }
        try
        {
            Microsoft.Win32.Registry.ClassesRoot.DeleteSubKeyTree(
                $@"CLSID\{clsid}\CapeDescription", false);
        }
        catch { }
    }

    // ─── Private methods ──────────────────────────────────────────────

    private void EnsureStaticInit()
    {
        if (_sharedLib != null) return;
        lock (_staticLock)
        {
            if (_sharedLib != null) return;

            // Must register assembly resolver before any System.Text.Json usage
            // (Load/IStream can call EnsureStaticInit before Initialize)
            AssemblyResolver.Initialize();

            var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            Log($"EnsureStaticInit: asmDir={asmDir}");

            // Find fluids directory: look relative to assembly, then thermopack source
            _fluidsDir = FindFluidsDirectory(asmDir);
            Log($"EnsureStaticInit: fluidsDir={_fluidsDir ?? "(null)"}");

            _sharedDb = new ComponentDatabase();
            if (_fluidsDir != null)
                _sharedDb.LoadFromDirectory(_fluidsDir);
            Log($"EnsureStaticInit: loaded {_sharedDb.Components.Count} components");
            if (_sharedDb.Components.Count == 0 && _sharedDb.LoadErrors.Count > 0)
            {
                foreach (var err in _sharedDb.LoadErrors.Take(5))
                    Log($"  DB: {err}");
            }

            _sharedLib = new ThermoPackLibrary();
            try
            {
                _sharedLib.Load(asmDir);
                Log("EnsureStaticInit: native library loaded OK");
            }
            catch (Exception ex)
            {
                Log($"EnsureStaticInit: native library load FAILED: {ex.Message}");
                // _sharedLib is non-null but not functional — DB is still valid for Edit
            }
        }
    }

    private static string? FindFluidsDirectory(string startDir)
    {
        // Try multiple starting points
        var searchDirs = new List<string> { startDir };

        // Also try CodeBase location (may differ from Location for COM activation)
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            string? codeBase = asm.CodeBase;
            if (!string.IsNullOrEmpty(codeBase))
            {
                var uri = new Uri(codeBase);
                var cbDir = Path.GetDirectoryName(uri.LocalPath);
                if (cbDir != null && cbDir != startDir)
                    searchDirs.Add(cbDir);
            }
        }
        catch { }

        foreach (var searchDir in searchDirs)
        {
            // Check alongside assembly
            var local = Path.Combine(searchDir, "fluids");
            if (Directory.Exists(local) &&
                File.Exists(Path.Combine(local, "Methane.json")))
                return local;

            // Walk up to find thermopack root
            var dir = searchDir;
            for (int i = 0; i < 8; i++)
            {
                var parent = Path.GetDirectoryName(dir);
                if (parent == null) break;
                dir = parent;
                var candidate = Path.Combine(dir, "fluids");
                if (Directory.Exists(candidate) &&
                    File.Exists(Path.Combine(candidate, "Methane.json")))
                    return candidate;
            }
        }

        return null;
    }

    private void EnsureInitialized()
    {
        if (_engine != null) return;
        if (_initError != null)
            throw new COMException(_initError, ECapeCalculation);
        EnsureStaticInit();
        RecreateEngine();
        if (_initError != null)
            throw new COMException(_initError, ECapeCalculation);
    }

    private void RecreateEngine()
    {
        _engine?.Dispose();
        _engine = null;
        _lastResult = null;
        _initError = null;

        if (_selectedComponents.Count == 0 || _sharedLib == null) return;

        var validationError = ValidateComponents(_selectedComponents);
        if (validationError != null)
        {
            _initError = validationError;
            Log($"RecreateEngine: validation failed: {validationError}");
            return;
        }

        _engine = new ThermoPackEngine(_sharedLib);
        var compString = string.Join(",", _selectedComponents.Select(c => c.Ident));
        InitializeEngine(_engine, compString);
    }

    /// <summary>
    /// Validate the selected component list before engine creation.
    /// Return null if valid, or an error message string if invalid.
    /// </summary>
    protected virtual string? ValidateComponents(List<Component> components) => null;

    private void EnsureFlashResult()
    {
        if (_material == null) return;
        var wrapper = new MaterialObjectWrapper(_material);
        double T = wrapper.GetTemperature();
        double P = wrapper.GetPressure();
        int nc = _selectedComponents.Count;
        double[] rawFeed = wrapper.GetFeed(nc);
        double[] feed = NormalizeFeed(rawFeed, nc);

        if (_lastResult != null &&
            Math.Abs(T - _lastT) < 1e-8 &&
            Math.Abs(P - _lastP) < 1e-8 &&
            FeedMatches(feed, _lastFeed))
            return;

        // Need a fresh TP flash
        EnsureInitialized();
        if (_engine == null) return;

        double rawSum = 0;
        for (int i = 0; i < nc; i++) rawSum += Math.Abs(rawFeed[i]);

        if (rawSum < 1e-12)
        {
            // Zero flow: use uniform composition to avoid passing all-zeros to Fortran
            var uniform = new double[nc];
            for (int i = 0; i < nc; i++) uniform[i] = 1.0 / nc;
            _lastResult = new FlashResult
            {
                Temperature = T, Pressure = P,
                BetaV = 0, BetaL = 1,
                X = uniform, Y = (double[])uniform.Clone()
            };
            _lastT = T; _lastP = P; _lastFeed = (double[])uniform.Clone();
            return;
        }

        try
        {
            Log($"[{PackageName}] EnsureFlashResult: TP flash T={T:F2}, P={P:F0}, " +
                $"feed=[{string.Join(",", feed.Select(f => f.ToString("G6")))}]");
            _lastResult = _engine.TwoPhaseTPFlash(T, P, feed);
            NormalizeFlashResult(_lastResult, feed, nc);
            _lastT = T; _lastP = P; _lastFeed = (double[])feed.Clone();
        }
        catch
        {
            if (_lastResult != null && _lastResult.X.Length == nc) return;
            // Use feed as composition fallback — never store all-zeros in X/Y
            // as that can crash CPA's LAPACK solver if passed to Fortran.
            var feedCopy = (double[])feed.Clone();
            _lastResult = new FlashResult
            {
                Temperature = T, Pressure = P,
                BetaV = 0, BetaL = 1,
                X = feedCopy, Y = (double[])feedCopy.Clone()
            };
            _lastT = T; _lastP = P; _lastFeed = (double[])feed.Clone();
        }
    }

    private double[] GetSinglePhasePropertyValues(string prop, string phaseLabel,
        MaterialObjectWrapper wrapper)
    {
        if (_engine == null || _lastResult == null)
            throw new COMException("No flash result available");

        int nc = _selectedComponents.Count;
        double T = _lastResult.Temperature;
        double P = _lastResult.Pressure;
        bool isVapour = phaseLabel == PhaseVapour;
        int phase = isVapour ? _engine.VaporPhase : _engine.LiquidPhase;
        int altPhase = isVapour ? _engine.LiquidPhase : _engine.VaporPhase;
        double[] x = isVapour
            ? FitArray(_lastResult.Y, nc)
            : FitArray(_lastResult.X, nc);
        SanitizeAndNormalize(x);

        // Guard: if composition is still all-zero after normalization (e.g. absent phase
        // from a single-phase flash), use the feed composition.  Passing zeros to Fortran
        // crashes CPA's LAPACK dsysv solver with an unrecoverable abort().
        if (ArraySum(x) < 1e-30 && _lastFeed != null)
        {
            Log($"[{PackageName}] WARNING: zero composition for {phaseLabel}, using feed fallback");
            x = NormalizeFeed(_lastFeed, nc);
        }

        Log($"[{PackageName}] GetProp({prop}, {phaseLabel}): T={T:F2}, P={P:F0}, " +
            $"x=[{string.Join(",", x.Select(v => v.ToString("G6")))}], phase={phase}");

        switch (prop.ToLowerInvariant())
        {
            case "enthalpy":
            case "enthalpyf":
            case "enthalpynf":
                return new[] { SafeCalc(() => _engine.Enthalpy(T, P, x, phase),
                                        () => _engine.Enthalpy(T, P, x, altPhase)) };

            case "entropy":
            case "entropyf":
            case "entropynf":
                return new[] { SafeCalc(() => _engine.Entropy(T, P, x, phase),
                                        () => _engine.Entropy(T, P, x, altPhase)) };

            case "volume":
            {
                double v = SafeCalc(() => _engine.SpecificVolume(T, P, x, phase),
                                    () => _engine.SpecificVolume(T, P, x, altPhase));
                return new[] { v };
            }

            case "density":
            {
                double v = SafeCalc(() => _engine.SpecificVolume(T, P, x, phase),
                                    () => _engine.SpecificVolume(T, P, x, altPhase));
                return new[] { v > 1e-30 ? 1.0 / v : 0.0 };
            }

            case "heatcapacitycp":
            {
                var dhdt = SafeCalc(() => _engine.EnthalpyWithCp(T, P, x, phase).Item2,
                                    () => _engine.EnthalpyWithCp(T, P, x, altPhase).Item2);
                return new[] { dhdt };
            }

            case "compressibilityfactor":
                return new[] { SafeCalc(() => _engine.ZFac(T, P, x, phase),
                                        () => _engine.ZFac(T, P, x, altPhase)) };

            case "fugacitycoefficient":
            {
                var lnphi = SafeCalcArr(() => _engine.LnFugacityCoefficients(T, P, x, phase),
                                        () => _engine.LnFugacityCoefficients(T, P, x, altPhase));
                var phi = new double[lnphi.Length];
                for (int i = 0; i < lnphi.Length; i++)
                    phi[i] = Math.Exp(Math.Max(-30, Math.Min(30, lnphi[i])));
                return phi;
            }

            case "logfugacitycoefficient":
            {
                var lnphi = SafeCalcArr(() => _engine.LnFugacityCoefficients(T, P, x, phase),
                                        () => _engine.LnFugacityCoefficients(T, P, x, altPhase));
                var result = new double[lnphi.Length];
                for (int i = 0; i < lnphi.Length; i++)
                    result[i] = Math.Max(-30, Math.Min(30, lnphi[i]));
                return result;
            }

            case "fugacity":
            {
                var lnphi = SafeCalcArr(() => _engine.LnFugacityCoefficients(T, P, x, phase),
                                        () => _engine.LnFugacityCoefficients(T, P, x, altPhase));
                var fug = new double[lnphi.Length];
                for (int i = 0; i < lnphi.Length; i++)
                    fug[i] = Math.Exp(Math.Max(-30, Math.Min(30, lnphi[i]))) * P * x[i];
                return fug;
            }

            case "internalenergy":
            {
                double h = SafeCalc(() => _engine.Enthalpy(T, P, x, phase),
                                    () => _engine.Enthalpy(T, P, x, altPhase));
                double v = SafeCalc(() => _engine.SpecificVolume(T, P, x, phase),
                                    () => _engine.SpecificVolume(T, P, x, altPhase));
                return new[] { h - P * v };
            }

            case "gibbsenergy":
            {
                double h = SafeCalc(() => _engine.Enthalpy(T, P, x, phase),
                                    () => _engine.Enthalpy(T, P, x, altPhase));
                double s = SafeCalc(() => _engine.Entropy(T, P, x, phase),
                                    () => _engine.Entropy(T, P, x, altPhase));
                return new[] { h - T * s };
            }

            case "molecularweight":
            {
                double mw = 0;
                for (int i = 0; i < nc; i++)
                    mw += x[i] * _selectedComponents[i].MolWeight;
                return new[] { mw };
            }

            case "phasefraction":
            {
                double beta = phaseLabel == PhaseVapour ? _lastResult.BetaV : _lastResult.BetaL;
                return new[] { beta };
            }

            case "fraction":
                return x;

            case "temperature":
                return new[] { T };

            case "pressure":
                return new[] { P };

            default:
                throw new COMException($"Unsupported single-phase property: {prop}");
        }
    }

    private static string GetPropertyBasis(string prop)
    {
        // Basis alignment with thermocalc: Mole for thermodynamic properties,
        // empty string for everything else (fugacity, coefficients, MW, etc.)
        switch (prop.ToLowerInvariant())
        {
            case "enthalpy":
            case "enthalpyf":
            case "enthalpynf":
            case "entropy":
            case "entropyf":
            case "entropynf":
            case "internalenergy":
            case "gibbsenergy":
            case "heatcapacitycp":
            case "volume":
            case "density":
            case "fraction":
            case "phasefraction":
                return "Mole";
            default:
                return "";
        }
    }

    private void WriteFlashResultToMaterial(MaterialObjectWrapper wrapper,
        FlashResult result, int nc, double[] feed, FlashType flashType)
    {
        Log($"WriteFlash: type={flashType}, T={result.Temperature:F2}, P={result.Pressure:F0}, " +
            $"betaV={result.BetaV:G6}, betaL={result.BetaL:G6}, nc={nc}");
        // Note: result has already been normalized by NormalizeFlashResult()
        // so BetaV/BetaL are in [0,1] and phase is LIQPH or VAPPH.
        double T = result.Temperature;
        double P = result.Pressure;
        double betaV = result.BetaV;
        double betaL = result.BetaL;

        // Prepare per-phase composition arrays (sanitize + normalize to sum=1)
        double[] xLiq = FitArray(result.X, nc);
        double[] yVap = FitArray(result.Y, nc);
        SanitizeAndNormalize(xLiq);
        SanitizeAndNormalize(yVap);

        // Guard: if either phase composition is still all-zero, use feed.
        // This prevents passing zeros to Fortran (crashes CPA's dsysv).
        if (ArraySum(xLiq) < 1e-30) xLiq = NormalizeFeed(feed, nc);
        if (ArraySum(yVap) < 1e-30) yVap = NormalizeFeed(feed, nc);

        // Compute overall fraction consistent with phase data: z_i = betaV*y_i + betaL*x_i
        // Then normalize to ensure it sums to exactly 1.0.
        var overallFraction = new double[nc];
        for (int i = 0; i < nc; i++)
            overallFraction[i] = betaV * yVap[i] + betaL * xLiq[i];
        SanitizeAndNormalize(overallFraction);

        // Write overall T, P, and consistent overall fraction
        try { wrapper.SetOverallProp("temperature", "", new[] { T }); } catch { }
        try { wrapper.SetOverallProp("pressure", "", new[] { P }); } catch { }
        try { wrapper.SetOverallProp("fraction", "Mole", overallFraction); } catch { }

        // Compute and write overall H, S, V (mixture properties).
        // Each property is independent so one failure must not block the others.
        if (_engine != null)
        {
            Log($"[{PackageName}] WriteFlash: computing overall H,S,V. " +
                $"xLiq=[{string.Join(",", xLiq.Select(v => v.ToString("G6")))}], " +
                $"yVap=[{string.Join(",", yVap.Select(v => v.ToString("G6")))}]");

            try
            {
                double hMix = 0;
                if (betaV > 1e-12)
                {
                    Log($"[{PackageName}] WriteFlash: Enthalpy(T={T:F2},P={P:F0},yVap,VAPPH)...");
                    hMix += betaV * _engine.Enthalpy(T, P, yVap, _engine.VaporPhase);
                }
                if (betaL > 1e-12)
                {
                    Log($"[{PackageName}] WriteFlash: Enthalpy(T={T:F2},P={P:F0},xLiq,LIQPH)...");
                    hMix += betaL * _engine.Enthalpy(T, P, xLiq, _engine.LiquidPhase);
                }
                wrapper.SetOverallProp("enthalpy", "Mole", new[] { hMix });
                Log($"[{PackageName}] WriteFlash: overall H={hMix:G6}");
            }
            catch (Exception ex) { Log($"[{PackageName}] WriteFlash: overall enthalpy FAILED: {ex.Message}"); }

            try
            {
                double sMix = 0;
                if (betaV > 1e-12)
                {
                    Log($"[{PackageName}] WriteFlash: Entropy(T={T:F2},P={P:F0},yVap,VAPPH)...");
                    sMix += betaV * _engine.Entropy(T, P, yVap, _engine.VaporPhase);
                }
                if (betaL > 1e-12)
                {
                    Log($"[{PackageName}] WriteFlash: Entropy(T={T:F2},P={P:F0},xLiq,LIQPH)...");
                    sMix += betaL * _engine.Entropy(T, P, xLiq, _engine.LiquidPhase);
                }
                wrapper.SetOverallProp("entropy", "Mole", new[] { sMix });
                Log($"[{PackageName}] WriteFlash: overall S={sMix:G6}");
            }
            catch (Exception ex) { Log($"[{PackageName}] WriteFlash: overall entropy FAILED: {ex.Message}"); }

            try
            {
                double vMix = 0;
                if (betaV > 1e-12)
                {
                    Log($"[{PackageName}] WriteFlash: SpecVol(T={T:F2},P={P:F0},yVap,VAPPH)...");
                    vMix += betaV * _engine.SpecificVolume(T, P, yVap, _engine.VaporPhase);
                }
                if (betaL > 1e-12)
                {
                    Log($"[{PackageName}] WriteFlash: SpecVol(T={T:F2},P={P:F0},xLiq,LIQPH)...");
                    vMix += betaL * _engine.SpecificVolume(T, P, xLiq, _engine.LiquidPhase);
                }
                wrapper.SetOverallProp("volume", "Mole", new[] { vMix });
                Log($"[{PackageName}] WriteFlash: overall V={vMix:G6}");
            }
            catch (Exception ex) { Log($"[{PackageName}] WriteFlash: overall volume FAILED: {ex.Message}"); }
        }

        // Only report phases that are actually present.  Reporting an absent
        // phase (beta=0) makes COFE think the stream is at the phase boundary.
        var labelList = new List<string>();
        var statusList = new List<int>();
        if (betaV > 1e-12) { labelList.Add(PhaseVapour); statusList.Add(CapeAtEquilibrium); }
        if (betaL > 1e-12) { labelList.Add(PhaseLiquid); statusList.Add(CapeAtEquilibrium); }
        // Safety: if neither phase has significant fraction, report the dominant one
        if (labelList.Count == 0)
        {
            labelList.Add(betaV >= betaL ? PhaseVapour : PhaseLiquid);
            statusList.Add(CapeAtEquilibrium);
        }
        wrapper.SetPresentPhases(labelList.ToArray(), statusList.ToArray());

        foreach (var label in labelList)
        {
            bool isVapour = label == PhaseVapour;
            double beta = isVapour ? betaV : betaL;
            double[] comp = isVapour ? yVap : xLiq;
            int phase = isVapour ? _engine!.VaporPhase : _engine!.LiquidPhase;
            int altPhase = isVapour ? _engine!.LiquidPhase : _engine!.VaporPhase;

            wrapper.SetSinglePhaseProp("temperature", label, "", new[] { T });
            wrapper.SetSinglePhaseProp("pressure", label, "", new[] { P });
            wrapper.SetSinglePhaseProp("phaseFraction", label, "Mole", new[] { Math.Max(0, beta) });
            wrapper.SetSinglePhaseProp("fraction", label, "Mole", comp);

            // Pre-compute per-phase H, S, V.  Try requested phase flag first;
            // if that returns NaN (supercritical, only one EOS root), use the
            // alternative phase flag.
            Log($"[{PackageName}] WriteFlash: phase {label} H(T={T:F2},P={P:F0}," +
                $"comp=[{string.Join(",", comp.Select(v => v.ToString("G6")))}])...");
            double h = SafeCalc(() => _engine.Enthalpy(T, P, comp, phase),
                                () => _engine.Enthalpy(T, P, comp, altPhase));
            wrapper.SetSinglePhaseProp("enthalpy", label, "Mole", new[] { h });

            double s = SafeCalc(() => _engine.Entropy(T, P, comp, phase),
                                () => _engine.Entropy(T, P, comp, altPhase));
            wrapper.SetSinglePhaseProp("entropy", label, "Mole", new[] { s });

            double v = SafeCalc(() => _engine.SpecificVolume(T, P, comp, phase),
                                () => _engine.SpecificVolume(T, P, comp, altPhase));
            wrapper.SetSinglePhaseProp("volume", label, "Mole", new[] { v });

            Log($"WriteFlash: {label} beta={beta:G4} h={h:G6} s={s:G6} v={v:G6}");
        }
    }

    private void SetZeroFlowDefault(MaterialObjectWrapper wrapper, int nc)
    {
        double T = wrapper.GetTemperature();
        double P = wrapper.GetPressure();
        if (double.IsNaN(T) || T < 1.0) T = 298.15;
        if (double.IsNaN(P) || P < 1.0) P = 101325.0;

        // Use uniform composition to avoid passing all-zeros to Fortran
        var uniform = new double[nc];
        for (int i = 0; i < nc; i++) uniform[i] = 1.0 / nc;

        _lastResult = new FlashResult
        {
            Temperature = T, Pressure = P,
            BetaV = 0, BetaL = 1,
            X = uniform, Y = (double[])uniform.Clone()
        };
        _lastT = T; _lastP = P; _lastFeed = (double[])uniform.Clone();

        wrapper.SetPresentPhases(new[] { PhaseLiquid }, new[] { CapeAtEquilibrium });
        wrapper.SetSinglePhaseProp("temperature", PhaseLiquid, "", new[] { T });
        wrapper.SetSinglePhaseProp("pressure", PhaseLiquid, "", new[] { P });
        wrapper.SetSinglePhaseProp("phaseFraction", PhaseLiquid, "Mole", new[] { 1.0 });
        wrapper.SetSinglePhaseProp("fraction", PhaseLiquid, "Mole", new double[nc]);
    }

    // ─── Flash spec validation ────────────────────────────────────────

    private enum FlashType { PT, PH, PS, UV, PVF, TVF }

    private struct FlashSpec
    {
        public FlashType Type;
        public string TargetProperty;
        public string TargetBasis;
        public string TargetCalcType;
    }

    private static (string name, string basis, string calcType) ParseSpec(object spec)
    {
        if (spec is string s) return (s, "", "Overall");
        if (spec is string[] sa)
            return (
                sa.Length > 0 ? sa[0] : "",
                sa.Length > 1 ? sa[1] : "",
                sa.Length > 2 ? sa[2] : "Overall"
            );
        return ("", "", "Overall");
    }

    private static FlashSpec ValidateFlashSpec(object specification1, object specification2)
    {
        var s1 = ParseSpec(specification1);
        var s2 = ParseSpec(specification2);
        string p1 = s1.name.ToLowerInvariant();
        string p2 = s2.name.ToLowerInvariant();

        FlashSpec result = new FlashSpec();

        if ((p1 == "temperature" && p2 == "pressure") ||
            (p1 == "pressure" && p2 == "temperature"))
        {
            result.Type = FlashType.PT;
            return result;
        }

        if (p1 == "pressure" && p2 == "enthalpy")
        { result.Type = FlashType.PH; result.TargetProperty = s2.name; result.TargetBasis = s2.basis; result.TargetCalcType = s2.calcType; return result; }
        if (p1 == "enthalpy" && p2 == "pressure")
        { result.Type = FlashType.PH; result.TargetProperty = s1.name; result.TargetBasis = s1.basis; result.TargetCalcType = s1.calcType; return result; }

        if (p1 == "pressure" && p2 == "entropy")
        { result.Type = FlashType.PS; result.TargetProperty = s2.name; result.TargetBasis = s2.basis; result.TargetCalcType = s2.calcType; return result; }
        if (p1 == "entropy" && p2 == "pressure")
        { result.Type = FlashType.PS; result.TargetProperty = s1.name; result.TargetBasis = s1.basis; result.TargetCalcType = s1.calcType; return result; }

        if ((p1 == "internalenergy" && p2 == "volume") ||
            (p1 == "volume" && p2 == "internalenergy"))
        {
            result.Type = FlashType.UV;
            return result;
        }

        if (p1 == "pressure" && p2 == "phasefraction")
        { result.Type = FlashType.PVF; result.TargetProperty = s2.name; result.TargetBasis = s2.basis; result.TargetCalcType = s2.calcType; return result; }
        if (p1 == "phasefraction" && p2 == "pressure")
        { result.Type = FlashType.PVF; result.TargetProperty = s1.name; result.TargetBasis = s1.basis; result.TargetCalcType = s1.calcType; return result; }

        if (p1 == "temperature" && p2 == "phasefraction")
        { result.Type = FlashType.TVF; result.TargetProperty = s2.name; result.TargetBasis = s2.basis; result.TargetCalcType = s2.calcType; return result; }
        if (p1 == "phasefraction" && p2 == "temperature")
        { result.Type = FlashType.TVF; result.TargetProperty = s1.name; result.TargetBasis = s1.basis; result.TargetCalcType = s1.calcType; return result; }

        throw new COMException($"Unsupported flash specification: {p1}/{p2}");
    }

    // ─── Utility methods ──────────────────────────────────────────────

    /// <summary>
    /// Store the current component selection in shared static state so that
    /// new COFE solving instances (created after the master) can inherit it.
    /// </summary>
    private void SyncShared()
    {
        _sharedSelectedCas = _selectedComponents.Select(c => c.CasNumber).ToList();
    }

    /// <summary>
    /// Resolve a list of CAS numbers back to Component objects from the database.
    /// Preserves the order from the shared list (important for feed index mapping).
    /// </summary>
    private static List<Component> ResolveSharedComponents(List<string> casList)
    {
        if (_sharedDb == null) return new List<Component>();
        var result = new List<Component>();
        foreach (var cas in casList)
        {
            var comp = _sharedDb.FindByCas(cas);
            if (comp != null) result.Add(comp);
        }
        return result;
    }

    private Component? FindComponentByCas(string cas)
        => _selectedComponents.FirstOrDefault(c => c.CasNumber == cas);

    private int FindFortranIndex(string cas)
    {
        for (int i = 0; i < _selectedComponents.Count; i++)
            if (_selectedComponents[i].CasNumber == cas)
                return i + 1; // 1-based Fortran index
        return -1;
    }

    private static double[] NormalizeFeed(double[] raw, int nc)
    {
        var feed = new double[nc];
        double sum = 0;
        int len = Math.Min(raw.Length, nc);
        for (int i = 0; i < len; i++)
        {
            feed[i] = raw[i];
            sum += raw[i];
        }
        if (sum < 1e-30) return feed;
        for (int i = 0; i < len; i++) feed[i] /= sum;
        return feed;
    }

    private static double[] FitArray(double[] src, int nc)
    {
        if (src == null) return new double[nc];
        if (src.Length == nc) return (double[])src.Clone();
        var fit = new double[nc];
        int len = Math.Min(src.Length, nc);
        Array.Copy(src, fit, len);
        return fit;
    }

    private static bool FeedMatches(double[] a, double[]? b)
    {
        if (b == null) return false;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 1e-10) return false;
        return true;
    }

    /// <summary>
    /// Correct flash results for single-phase / supercritical conditions.
    /// Thermopack may return betaV/betaL outside [0,1], phase==SINGLEPH,
    /// or (for PH/PS flash) zero x[]/y[] arrays for single-phase results.
    /// In such cases, set X=Y=feed.
    /// </summary>
    private void NormalizeFlashResult(FlashResult result, double[] feed, int nc)
    {
        if (_engine == null) return;

        double betaV = result.BetaV;
        double betaL = result.BetaL;
        if (double.IsNaN(betaV)) betaV = 0;
        if (double.IsNaN(betaL)) betaL = 0;

        bool phaseIsSingle = result.Phase == _engine.SinglePhase;
        bool betaOutOfRange = betaV < -1e-6 || betaV > 1.0 + 1e-6 ||
                              betaL < -1e-6 || betaL > 1.0 + 1e-6;
        bool betaSumBad = Math.Abs(betaV + betaL - 1.0) > 0.01;

        // Thermopack PH/PS flash returns x[]=0, y[]=0 for single-phase results.
        // Detect this and treat as single-phase.
        bool xAllZero = result.X == null || ArraySum(result.X) < 1e-30;
        bool yAllZero = result.Y == null || ArraySum(result.Y) < 1e-30;
        bool compositionsEmpty = xAllZero && yAllZero;

        if (phaseIsSingle || betaOutOfRange || betaSumBad || compositionsEmpty)
        {
            int guessedPhase;
            try
            {
                guessedPhase = _engine.GuessPhase(result.Temperature, result.Pressure, feed);
            }
            catch
            {
                guessedPhase = (result.Phase == _engine.LiquidPhase)
                    ? _engine.LiquidPhase : _engine.VaporPhase;
            }

            result.Phase = guessedPhase;
            var feedCopy = FitArray(feed, nc);

            if (guessedPhase == _engine.VaporPhase)
            {
                result.BetaV = 1.0;
                result.BetaL = 0.0;
            }
            else
            {
                result.BetaV = 0.0;
                result.BetaL = 1.0;
            }
            result.X = feedCopy;
            result.Y = (double[])feedCopy.Clone();
        }
        else
        {
            // Clamp two-phase betas to [0,1]
            result.BetaV = Math.Max(0, Math.Min(1, betaV));
            result.BetaL = Math.Max(0, Math.Min(1, betaL));

            // For single-phase results (betaV≈1 or betaL≈1), the absent phase's
            // composition may be zero/garbage from Fortran.  Fix it to feed.
            var feedCopy = FitArray(feed, nc);
            if (betaV > 1.0 - 1e-6 && xAllZero)
                result.X = feedCopy;
            if (betaL > 1.0 - 1e-6 && yAllZero)
                result.Y = (double[])feedCopy.Clone();
        }
    }

    private static double ArraySum(double[] arr)
    {
        double s = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            if (double.IsNaN(arr[i]) || double.IsInfinity(arr[i])) return 0;
            s += Math.Abs(arr[i]);
        }
        return s;
    }

    /// <summary>
    /// Try primary calculation; if it returns NaN/Inf or throws, try the fallback
    /// (alternative phase flag).  If both fail, throws — no silent swallowing.
    /// </summary>
    private static double SafeCalc(Func<double> primary, Func<double> fallback)
    {
        try
        {
            double v = primary();
            if (!double.IsNaN(v) && !double.IsInfinity(v)) return v;
        }
        catch { }
        // Fallback: let it throw if it fails too
        double fb = fallback();
        if (double.IsNaN(fb) || double.IsInfinity(fb))
            throw new InvalidOperationException(
                "Property calculation returned NaN/Inf for both phase flags");
        return fb;
    }

    private static double[] SafeCalcArr(Func<double[]> primary, Func<double[]> fallback)
    {
        try
        {
            double[] v = primary();
            if (v != null && v.Length > 0 && !double.IsNaN(v[0]) && !double.IsInfinity(v[0])) return v;
        }
        catch { }
        // Fallback: let it throw if it fails too
        return fallback();
    }

    private static void SanitizeAndNormalize(double[] arr)
    {
        double sum = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            if (double.IsNaN(arr[i]) || double.IsInfinity(arr[i]) || arr[i] < 0)
                arr[i] = 0;
            sum += arr[i];
        }
        if (sum > 1e-30)
        {
            for (int i = 0; i < arr.Length; i++)
                arr[i] /= sum;
        }
    }

    // ─── Diagnostic logging ──────────────────────────────────────────

    private static void Log(string message)
    {
        try
        {
            if (_logPath == null)
            {
                var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
                _logPath = Path.Combine(dir, "thermopack_capeopen.log");
            }
            File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    // ─── COM Stream Wrapper ───────────────────────────────────────────

    private class ComStreamWrapper : Stream
    {
        private readonly IStream _stream;

        public ComStreamWrapper(IStream stream)
            => _stream = stream ?? throw new ArgumentNullException(nameof(stream));

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => true;

        public override long Length
        {
            get { _stream.Stat(out var stat, 1); return stat.cbSize; }
        }

        public override long Position
        {
            get
            {
                var ptr = Marshal.AllocHGlobal(8);
                try
                {
                    _stream.Seek(0, 1, ptr);
                    return Marshal.ReadInt64(ptr);
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            set { _stream.Seek(value, 0, IntPtr.Zero); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var pcbRead = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                if (offset == 0)
                {
                    _stream.Read(buffer, count, pcbRead);
                }
                else
                {
                    var tmp = new byte[count];
                    _stream.Read(tmp, count, pcbRead);
                    int read = Marshal.ReadInt32(pcbRead);
                    Array.Copy(tmp, 0, buffer, offset, read);
                    return read;
                }
                return Marshal.ReadInt32(pcbRead);
            }
            finally { Marshal.FreeHGlobal(pcbRead); }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (offset == 0 && count == buffer.Length)
            {
                _stream.Write(buffer, count, IntPtr.Zero);
                return;
            }
            var tmp = new byte[count];
            Array.Copy(buffer, offset, tmp, 0, count);
            _stream.Write(tmp, count, IntPtr.Zero);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            int dwOrigin = origin == SeekOrigin.Begin ? 0 :
                (origin == SeekOrigin.Current ? 1 : 2);
            var ptr = Marshal.AllocHGlobal(8);
            try
            {
                _stream.Seek(offset, dwOrigin, ptr);
                return Marshal.ReadInt64(ptr);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        public override void SetLength(long value) => _stream.SetSize(value);
        public override void Flush() => _stream.Commit(0);
        protected override void Dispose(bool disposing) { }
    }
}
