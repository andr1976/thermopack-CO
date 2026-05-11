using System.Runtime.InteropServices;

namespace ThermoPack.Core;

/// <summary>
/// Delegate definitions for thermopack Fortran entry points.
/// All use Cdecl calling convention. Fortran strings are passed as byte[]
/// with trailing UIntPtr (size_t) length arguments.
/// </summary>
public static class ThermoPackInterop
{
    // ─── Model lifecycle (module: thermopack_var) ─────────────────────

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int AddEosDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ActivateModelDelegate(ref int modelIdx);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void DeleteEosDelegate(ref int modelIdx);

    // ─── Rgas (module: thermopack_var) ────────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate double GetRgasDelegate();

    // ─── Fortran boolean (module: thermopack_constants) ───────────────

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void GetTrueDelegate(ref int trueValue);

    // ─── EOS init (module: eoslibinit) ────────────────────────────────

    /// <summary>init_cubic(comps, eos, mixing, alpha, parameter_reference, vol_shift, len...)</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void InitCubicDelegate(
        byte[] comps, byte[] eos, byte[] mixing, byte[] alpha, byte[] paramRef,
        ref int volShift,
        UIntPtr compsLen, UIntPtr eosLen, UIntPtr mixingLen, UIntPtr alphaLen, UIntPtr paramRefLen);

    /// <summary>init_tcPR(comps, mixing, parameter_reference, len...)</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void InitTcPRDelegate(
        byte[] comps, byte[] mixing, byte[] paramRef,
        UIntPtr compsLen, UIntPtr mixingLen, UIntPtr paramRefLen);

    /// <summary>init_cpa(comps, eos, mixing, alpha, parameter_reference, len...)</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void InitCpaDelegate(
        byte[] comps, byte[] eos, byte[] mixing, byte[] alpha, byte[] paramRef,
        UIntPtr compsLen, UIntPtr eosLen, UIntPtr mixingLen, UIntPtr alphaLen, UIntPtr paramRefLen);

    /// <summary>init_pcsaft(comps, parameter_reference, simplified, polar, len...)</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void InitPcSaftDelegate(
        byte[] comps, byte[] paramRef, ref int simplified, ref int polar,
        UIntPtr compsLen, UIntPtr paramRefLen);

    /// <summary>init_multiparameter(comps, meos, ref_state, len...)</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void InitMultiparameterDelegate(
        byte[] comps, byte[] meos, byte[] refState,
        UIntPtr compsLen, UIntPtr meosLen, UIntPtr refStateLen);

    // ─── Flash (various modules) ──────────────────────────────────────

    /// <summary>tp_solver::twophasetpflash(T, p, z, betaV, betaL, phase, x, y)</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void TwoPhaseTPFlashDelegate(
        ref double T, ref double p, double[] z,
        ref double betaV, ref double betaL, ref int phase,
        double[] x, double[] y);

    /// <summary>ps_solver::twophasepsflash(T, p, z, betaV, betaL, x, y, s, phase, ierr)</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void TwoPhasePSFlashDelegate(
        ref double T, ref double p, double[] z,
        ref double betaV, ref double betaL,
        double[] x, double[] y,
        ref double s, ref int phase, ref int ierr);

    /// <summary>ph_solver::twophasephflash(T, p, z, betaV, betaL, x, y, h, phase, ierr)</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void TwoPhasePHFlashDelegate(
        ref double T, ref double p, double[] z,
        ref double betaV, ref double betaL,
        double[] x, double[] y,
        ref double h, ref int phase, ref int ierr);

    /// <summary>uv_solver::twophaseuvflash(T, p, z, betaV, betaL, x, y, u, v, phase)</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void TwoPhaseUVFlashDelegate(
        ref double T, ref double p, double[] z,
        ref double betaV, ref double betaL,
        double[] x, double[] y,
        ref double u, ref double v, ref int phase);

    // ─── Saturation (module: saturation) ──────────────────────────────

    /// <summary>safe_bubt(p, z, y, ierr) → T</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate double SafeBubTDelegate(ref double p, double[] z, double[] y, ref int ierr);

    /// <summary>safe_bubp(T, z, y, ierr) → P</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate double SafeBubPDelegate(ref double T, double[] z, double[] y, ref int ierr);

    /// <summary>safe_dewt(p, z, x, ierr) → T</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate double SafeDewTDelegate(ref double p, double[] z, double[] x, ref int ierr);

    /// <summary>safe_dewp(T, z, x, ierr) → P</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate double SafeDewPDelegate(ref double T, double[] z, double[] x, ref int ierr);

    // ─── Properties (module: eos) ─────────────────────────────────────

    /// <summary>specificvolume(T, p, z, phase, v, dvdt, dvdp, dvdn)</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SpecificVolumeDelegate(
        ref double T, ref double p, double[] z, ref int phase,
        ref double v, IntPtr dvdt, IntPtr dvdp, IntPtr dvdn);

    /// <summary>enthalpy(T, p, z, phase, h, dhdt, dhdp, dhdn, property_flag)</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EnthalpyDelegate(
        ref double T, ref double p, double[] z, ref int phase,
        ref double h, IntPtr dhdt, IntPtr dhdp, IntPtr dhdn, ref int propertyFlag);

    /// <summary>entropy(T, p, z, phase, s, dsdt, dsdp, dsdn, property_flag)</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EntropyDelegate(
        ref double T, ref double p, double[] z, ref int phase,
        ref double s, IntPtr dsdt, IntPtr dsdp, IntPtr dsdn, ref int propertyFlag);

    /// <summary>thermo(T, p, z, phase, lnfug, dlnfugdt, dlnfugdp, dlnfugdn, ophase, metaextremum, v)</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ThermoDelegate(
        ref double T, ref double p, double[] z, ref int phase,
        double[] lnfug, IntPtr dlnfugdt, IntPtr dlnfugdp, IntPtr dlnfugdn,
        ref int ophase, ref int metaextremum, ref double v);

    /// <summary>zfac(T, p, z, phase, zfac, dzdt, dzdp, dzdn)</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ZfacDelegate(
        ref double T, ref double p, double[] z, ref int phase,
        ref double zfac, IntPtr dzdt, IntPtr dzdp, IntPtr dzdn);

    /// <summary>ideal_enthalpy_single(T, comp_idx, h_id, dhdt)</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void IdealEnthalpySingleDelegate(
        ref double T, ref int compIdx, ref double hId, IntPtr dhdt);

    /// <summary>compmoleweight(j) → double (g/mol)</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate double CompMoleWeightDelegate(ref int j);

    /// <summary>getCriticalParam(i, tc, pc, omega, vc, tnb)</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void GetCriticalParamDelegate(
        ref int i, ref double tc, ref double pc, ref double omega,
        ref double vc, ref double tnb);

    // ─── Phase flags (C-bound, no mangling) ───────────────────────────

    /// <summary>get_phase_flags_c(TWOPH, LIQPH, VAPPH, MINGIBBSPH, SINGLEPH, SOLIDPH, FAKEPH)</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void GetPhaseFlagsCDelegate(
        ref int twoph, ref int liqph, ref int vapph, ref int mingibbsph,
        ref int singleph, ref int solidph, ref int fakeph);

    // ─── Enthalpy with Cp derivative ──────────────────────────────────

    /// <summary>
    /// enthalpy with dhdt output for Cp calculation.
    /// Same as EnthalpyDelegate but we pass a real pointer for dhdt.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EnthalpyWithDhdtDelegate(
        ref double T, ref double p, double[] z, ref int phase,
        ref double h, ref double dhdt, IntPtr dhdp, IntPtr dhdn, ref int propertyFlag);
}
