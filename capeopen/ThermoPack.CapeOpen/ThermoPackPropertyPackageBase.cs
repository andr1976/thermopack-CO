using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using CAPEOPEN110;
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

    // Phase labels
    private const string PhaseVapour = "Vapour";
    private const string PhaseLiquid = "Liquid";

    // ─── Shared static state ──────────────────────────────────────────

    private static ThermoPackLibrary? _sharedLib;
    private static ComponentDatabase? _sharedDb;
    private static readonly object _staticLock = new();
    private static string? _fluidsDir;

    // ─── Instance state ───────────────────────────────────────────────

    private ThermoPackEngine? _engine;
    private ICapeThermoMaterial? _material;
    private List<Component> _selectedComponents = new();
    private bool _loadedFromStream;
    private bool _isDirty;

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
        _material = material as ICapeThermoMaterial;
    }

    public void UnsetMaterial()
    {
        _material = null;
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

    public void GetPhaseInfo(string phaseLabel, string phaseAttribute, ref object value)
    {
        if (phaseAttribute.Equals("StateOfAggregation", StringComparison.OrdinalIgnoreCase))
        {
            value = phaseLabel == PhaseVapour ? "Vapor" : "Liquid";
        }
        else
        {
            throw new COMException($"Unknown phase attribute: {phaseAttribute}");
        }
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

    public void GetCompoundConstant(object props, object compIds, ref object propVals)
    {
        var propNames = (string[])props;
        var ids = (string[])compIds;
        int nc = ids.Length;
        int np = propNames.Length;

        var result = new object[np];
        for (int p = 0; p < np; p++)
        {
            var values = new double[nc];
            for (int i = 0; i < nc; i++)
            {
                var comp = FindComponentByCas(ids[i]);
                if (comp == null) continue;

                switch (propNames[p].ToLowerInvariant())
                {
                    case "molecularweight":
                        values[i] = comp.MolWeight;
                        break;
                    case "criticaltemperature":
                        values[i] = comp.CriticalTemperature;
                        break;
                    case "criticalpressure":
                        values[i] = comp.CriticalPressure;
                        break;
                    case "acentricfactor":
                        values[i] = comp.AcentricFactor;
                        break;
                    case "casregistrynumber":
                        // Return as string array
                        var strVals = new string[nc];
                        for (int j = 0; j < nc; j++)
                        {
                            var c2 = FindComponentByCas(ids[j]);
                            strVals[j] = c2?.CasNumber ?? "";
                        }
                        result[p] = strVals;
                        goto NextProp;
                    default:
                        values[i] = 0;
                        break;
                }
            }
            result[p] = values;
            NextProp:;
        }
        propVals = result;
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
            throw new COMException("No compounds configured. Use Edit() to select compounds first.");

        EnsureInitialized();

        var spec = ValidateFlashSpec(specification1, specification2);
        var wrapper = new MaterialObjectWrapper(_material);
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

        FlashResult result;
        switch (spec.Type)
        {
            case FlashType.PT:
                result = _engine!.TwoPhaseTPFlash(T, P, feed);
                break;
            case FlashType.PH:
                result = _engine!.TwoPhasePHFlash(P, feed, targetValue, T);
                break;
            case FlashType.PS:
                result = _engine!.TwoPhasePSFlash(P, feed, targetValue, T);
                break;
            case FlashType.UV:
            {
                double uTarget = wrapper.ReadTargetValue("internalEnergy", "", "Overall");
                double vTarget = wrapper.ReadTargetValue("volume", "", "Overall");
                result = _engine!.TwoPhaseUVFlash(feed, uTarget, vTarget, T, P);
                break;
            }
            case FlashType.PVF:
                result = _engine!.PVFFlash(P, feed, targetValue);
                break;
            case FlashType.TVF:
                result = _engine!.TVFFlash(T, feed, targetValue);
                break;
            default:
                throw new COMException($"Unsupported flash type: {spec.Type}");
        }

        // Cache
        _lastResult = result;
        _lastT = result.Temperature;
        _lastP = result.Pressure;
        _lastFeed = (double[])feed.Clone();

        // Write back to material
        WriteFlashResultToMaterial(wrapper, result, nc, feed, spec.Type);
    }

    // ─── ICapeThermoPropertyRoutine ───────────────────────────────────

    public void CalcSinglePhaseProp(object props, string phaseLabel)
    {
        if (_material == null) throw new COMException("Material not set");
        if (_selectedComponents.Count == 0) throw new COMException("No compounds configured.");
        EnsureFlashResult();

        var propNames = (string[])props;
        var wrapper = new MaterialObjectWrapper(_material);

        foreach (var prop in propNames)
        {
            double[] values = GetSinglePhasePropertyValues(prop, phaseLabel, wrapper);
            string basis = GetPropertyBasis(prop);
            wrapper.SetSinglePhaseProp(prop, phaseLabel, basis, values);
        }
    }

    public void CalcTwoPhaseProp(object props, object phaseLabels)
    {
        if (_material == null) throw new COMException("Material not set");
        EnsureFlashResult();

        var propNames = (string[])props;
        var phases = (string[])phaseLabels;
        var wrapper = new MaterialObjectWrapper(_material);

        foreach (var prop in propNames)
        {
            if (prop.Equals("kvalue", StringComparison.OrdinalIgnoreCase))
            {
                if (_lastResult != null && _lastResult.X.Length > 0 && _lastResult.Y.Length > 0)
                {
                    int nc = _selectedComponents.Count;
                    var kvalues = new double[nc];
                    for (int i = 0; i < nc; i++)
                    {
                        kvalues[i] = (_lastResult.X[i] > 1e-30)
                            ? _lastResult.Y[i] / _lastResult.X[i]
                            : 1e10;
                    }
                    // Write to first phase pair
                    _material.SetTwoPhaseProp(prop, phases, "Mole", kvalues);
                }
            }
        }
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
        return new[] { "kvalue" };
    }

    // ─── ICapeThermoUniversalConstant ─────────────────────────────────

    public void GetUniversalConstant(object props, ref object propVals)
    {
        var propNames = (string[])props;
        var values = new double[propNames.Length];

        for (int i = 0; i < propNames.Length; i++)
        {
            switch (propNames[i].ToLowerInvariant())
            {
                case "avogadroconstant":
                    values[i] = 6.02214076e23;
                    break;
                case "boltzmannconstant":
                    values[i] = 1.380649e-23;
                    break;
                case "molargasconstant":
                    values[i] = _engine != null ? _engine.GetRgas() : 8.31446;
                    break;
                default:
                    values[i] = 0;
                    break;
            }
        }
        propVals = values;
    }

    // ─── ICapeUtilities ───────────────────────────────────────────────

    public object simulationContext
    {
        set { /* Not used */ }
    }

    public void Initialize()
    {
        AssemblyResolver.Initialize();
        EnsureStaticInit();
    }

    public void Edit()
    {
        EnsureStaticInit();

        var available = _sharedDb!.GetComponentsForEos(EosType);
        var editorResult = EditorLauncher.ShowComponentEditor(
            available, _selectedComponents, EosType);

        if (editorResult != null)
        {
            _selectedComponents = new List<Component>(editorResult);
            _isDirty = true;
            _lastResult = null;
            RecreateEngine();
        }
    }

    public void Terminate()
    {
        _engine?.Dispose();
        _engine = null;
    }

    public object parameters
    {
        get => null!;
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
        using (var stream = new ComStreamWrapper(pStm))
        using (var reader = new BinaryReader(stream))
        {
            int version = reader.ReadInt32();
            if (version < 1) throw new InvalidDataException($"Unsupported stream version: {version}");

            int eosInt = reader.ReadInt32(); // Read but we use our own EosType
            int count = reader.ReadInt32();

            EnsureStaticInit();

            var compList = new List<Component>();
            for (int i = 0; i < count; i++)
            {
                string cas = reader.ReadString();
                var dbComp = _sharedDb!.FindByCas(cas);
                if (dbComp != null) compList.Add(dbComp);
            }

            _selectedComponents = compList;
            _loadedFromStream = true;
            _isDirty = false;

            RecreateEngine();
        }
    }

    public void Save(IStream pStm, [MarshalAs(UnmanagedType.Bool)] bool fClearDirty)
    {
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
        _loadedFromStream = false;
        _isDirty = false;
    }

    // ─── IDisposable ──────────────────────────────────────────────────

    public void Dispose()
    {
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

            var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";

            // Find fluids directory: look relative to assembly, then thermopack source
            _fluidsDir = FindFluidsDirectory(asmDir);

            _sharedDb = new ComponentDatabase();
            if (_fluidsDir != null)
                _sharedDb.LoadFromDirectory(_fluidsDir);

            _sharedLib = new ThermoPackLibrary();
            _sharedLib.Load(asmDir);
        }
    }

    private static string? FindFluidsDirectory(string startDir)
    {
        // Check alongside assembly
        var local = Path.Combine(startDir, "fluids");
        if (Directory.Exists(local)) return local;

        // Walk up to find thermopack root
        var dir = startDir;
        for (int i = 0; i < 6; i++)
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent == null) break;
            dir = parent;
            var candidate = Path.Combine(dir, "fluids");
            if (Directory.Exists(candidate) &&
                File.Exists(Path.Combine(candidate, "Methane.json")))
                return candidate;
        }

        return null;
    }

    private void EnsureInitialized()
    {
        if (_engine != null) return;
        EnsureStaticInit();
        RecreateEngine();
    }

    private void RecreateEngine()
    {
        _engine?.Dispose();
        _engine = null;
        _lastResult = null;

        if (_selectedComponents.Count == 0 || _sharedLib == null) return;

        _engine = new ThermoPackEngine(_sharedLib);
        var compString = string.Join(",", _selectedComponents.Select(c => c.Ident));
        InitializeEngine(_engine, compString);
    }

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
            _lastResult = new FlashResult
            {
                Temperature = T, Pressure = P,
                BetaV = 0, BetaL = 1,
                X = new double[nc], Y = new double[nc]
            };
            _lastT = T; _lastP = P; _lastFeed = new double[nc];
            return;
        }

        try
        {
            _lastResult = _engine.TwoPhaseTPFlash(T, P, feed);
            _lastT = T; _lastP = P; _lastFeed = (double[])feed.Clone();
        }
        catch
        {
            if (_lastResult != null && _lastResult.X.Length == nc) return;
            _lastResult = new FlashResult
            {
                Temperature = T, Pressure = P,
                BetaV = 0, BetaL = 1,
                X = new double[nc], Y = new double[nc]
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
        int phase = phaseLabel == PhaseVapour ? _engine.VaporPhase : _engine.LiquidPhase;
        double[] x = phaseLabel == PhaseVapour
            ? FitArray(_lastResult.Y, nc)
            : FitArray(_lastResult.X, nc);

        // Ensure composition sums to 1
        double xsum = 0;
        for (int i = 0; i < x.Length; i++) xsum += x[i];
        if (xsum < 1e-30)
        {
            x = new double[nc];
            if (nc > 0) x[0] = 1.0;
        }

        switch (prop.ToLowerInvariant())
        {
            case "enthalpy":
            case "enthalpyf":
            case "enthalpynf":
                return new[] { _engine.Enthalpy(T, P, x, phase) };

            case "entropy":
            case "entropyf":
            case "entropynf":
                return new[] { _engine.Entropy(T, P, x, phase) };

            case "volume":
            {
                double v = _engine.SpecificVolume(T, P, x, phase);
                return new[] { v };
            }

            case "density":
            {
                double v = _engine.SpecificVolume(T, P, x, phase);
                return new[] { v > 1e-30 ? 1.0 / v : 0.0 };
            }

            case "heatcapacitycp":
            {
                var (_, dhdt) = _engine.EnthalpyWithCp(T, P, x, phase);
                return new[] { dhdt };
            }

            case "compressibilityfactor":
                return new[] { _engine.ZFac(T, P, x, phase) };

            case "fugacitycoefficient":
            {
                var lnphi = _engine.LnFugacityCoefficients(T, P, x, phase);
                var phi = new double[lnphi.Length];
                for (int i = 0; i < lnphi.Length; i++)
                    phi[i] = Math.Exp(Math.Max(-30, Math.Min(30, lnphi[i])));
                return phi;
            }

            case "logfugacitycoefficient":
            {
                var lnphi = _engine.LnFugacityCoefficients(T, P, x, phase);
                var result = new double[lnphi.Length];
                for (int i = 0; i < lnphi.Length; i++)
                    result[i] = Math.Max(-30, Math.Min(30, lnphi[i]));
                return result;
            }

            case "fugacity":
            {
                var lnphi = _engine.LnFugacityCoefficients(T, P, x, phase);
                var fug = new double[lnphi.Length];
                for (int i = 0; i < lnphi.Length; i++)
                    fug[i] = Math.Exp(Math.Max(-30, Math.Min(30, lnphi[i]))) * P * x[i];
                return fug;
            }

            case "internalenergy":
            {
                double h = _engine.Enthalpy(T, P, x, phase);
                double v = _engine.SpecificVolume(T, P, x, phase);
                return new[] { h - P * v };
            }

            case "gibbsenergy":
            {
                double h = _engine.Enthalpy(T, P, x, phase);
                double s = _engine.Entropy(T, P, x, phase);
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

            default:
                throw new COMException($"Unsupported property: {prop}");
        }
    }

    private static string GetPropertyBasis(string prop)
    {
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
            case "fugacity":
                return "Mole";
            case "fugacitycoefficient":
            case "logfugacitycoefficient":
            case "compressibilityfactor":
            case "molecularweight":
            case "fraction":
            case "phasefraction":
                return "";
            default:
                return "Mole";
        }
    }

    private void WriteFlashResultToMaterial(MaterialObjectWrapper wrapper,
        FlashResult result, int nc, double[] feed, FlashType flashType)
    {
        double T = result.Temperature;
        double P = result.Pressure;

        // Write overall T and P
        try { wrapper.SetOverallProp("temperature", "", new[] { T }); } catch { }
        try { wrapper.SetOverallProp("pressure", "", new[] { P }); } catch { }

        // Determine present phases
        var labels = new List<string>();
        var statuses = new List<int>();

        bool forceTwo = flashType == FlashType.PVF || flashType == FlashType.TVF;

        if (result.HasVapour || forceTwo)
        {
            labels.Add(PhaseVapour);
            statuses.Add(CapeAtEquilibrium);
        }
        if (result.HasLiquid || forceTwo)
        {
            labels.Add(PhaseLiquid);
            statuses.Add(CapeAtEquilibrium);
        }
        if (labels.Count == 0)
        {
            labels.Add(PhaseVapour);
            statuses.Add(CapeAtEquilibrium);
        }

        wrapper.SetPresentPhases(labels.ToArray(), statuses.ToArray());

        foreach (var label in labels)
        {
            double beta = label == PhaseVapour ? result.BetaV : result.BetaL;
            double[] comp = label == PhaseVapour
                ? FitArray(result.Y, nc)
                : FitArray(result.X, nc);

            SanitizeArray(comp);
            if (double.IsNaN(beta) || double.IsInfinity(beta)) beta = 0;

            wrapper.SetSinglePhaseProp("temperature", label, "", new[] { T });
            wrapper.SetSinglePhaseProp("pressure", label, "", new[] { P });
            wrapper.SetSinglePhaseProp("phaseFraction", label, "Mole", new[] { Math.Max(0, beta) });
            wrapper.SetSinglePhaseProp("fraction", label, "Mole", comp);
        }
    }

    private void SetZeroFlowDefault(MaterialObjectWrapper wrapper, int nc)
    {
        double T = wrapper.GetTemperature();
        double P = wrapper.GetPressure();
        if (double.IsNaN(T) || T < 1.0) T = 298.15;
        if (double.IsNaN(P) || P < 1.0) P = 101325.0;

        _lastResult = new FlashResult
        {
            Temperature = T, Pressure = P,
            BetaV = 0, BetaL = 1,
            X = new double[nc], Y = new double[nc]
        };
        _lastT = T; _lastP = P; _lastFeed = new double[nc];

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
        for (int i = 0; i < len; i++) sum += raw[i];
        if (sum < 1e-30) return feed;
        for (int i = 0; i < len; i++) feed[i] = raw[i] / sum;
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

    private static void SanitizeArray(double[] arr)
    {
        for (int i = 0; i < arr.Length; i++)
            if (double.IsNaN(arr[i]) || double.IsInfinity(arr[i]))
                arr[i] = 0;
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
