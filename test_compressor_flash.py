#!/usr/bin/env python
"""Replicate the COFE compressor flash sequence in Python to verify Fortran behavior.

Conditions from the COFE log:
  - SRK EOS, components: CH4 (0.9), C2H6 (0.1)
  - Inlet: T=130 K, P=1e5 Pa (1 bar)
  - Outlet: P=11e5 Pa (11 bar)
  - Compressor does: PS flash (isentropic) -> compute H -> PH flash (actual)
"""
import numpy as np
from thermopack.cubic import cubic

def sv(val):
    """Extract scalar from specific_volume (may return tuple or scalar)."""
    return val[0] if isinstance(val, tuple) else val

# Init SRK with CH4, C2H6
srk = cubic("C1,C2", "SRK")
print(f"Phase flags: VAPPH={srk.VAPPH}, LIQPH={srk.LIQPH}, SINGLEPH={srk.SINGLEPH}, TWOPH={srk.TWOPH}")

z = np.array([0.9, 0.1])
T_in = 130.0   # K
P_in = 1.0e5   # Pa (1 bar)
P_out = 11.0e5 # Pa (11 bar)

print(f"\n=== Step 1: TP flash at inlet (T={T_in} K, P={P_in} Pa) ===")
flsh_in = srk.two_phase_tpflash(T_in, P_in, z)
print(f"  phase={flsh_in.phase}, betaV={flsh_in.betaV}, betaL={flsh_in.betaL}")
print(f"  x={flsh_in.x}")
print(f"  y={flsh_in.y}")

# Compute inlet entropy and enthalpy
h_v = h_l = s_v = s_l = 0
if flsh_in.betaV > 1e-12:
    h_v = srk.enthalpy(T_in, P_in, flsh_in.y, srk.VAPPH)[0]
    s_v = srk.entropy(T_in, P_in, flsh_in.y, srk.VAPPH)[0]
    v_v = sv(srk.specific_volume(T_in, P_in, flsh_in.y, srk.VAPPH))
    print(f"  Vapor: h={h_v:.2f} J/mol, s={s_v:.4f} J/mol/K, v={v_v:.6e} m3/mol")

if flsh_in.betaL > 1e-12:
    h_l = srk.enthalpy(T_in, P_in, flsh_in.x, srk.LIQPH)[0]
    s_l = srk.entropy(T_in, P_in, flsh_in.x, srk.LIQPH)[0]
    v_l = sv(srk.specific_volume(T_in, P_in, flsh_in.x, srk.LIQPH))
    print(f"  Liquid: h={h_l:.2f} J/mol, s={s_l:.4f} J/mol/K, v={v_l:.6e} m3/mol")

h_in = flsh_in.betaV * h_v + flsh_in.betaL * h_l
s_in = flsh_in.betaV * s_v + flsh_in.betaL * s_l
print(f"  Mixture: h_in={h_in:.2f} J/mol, s_in={s_in:.4f} J/mol/K")

print(f"\n=== Step 2: PS flash at outlet (P={P_out} Pa, s={s_in:.4f} J/mol/K) ===")
print(f"  (isentropic compression)")
h_isen = None
try:
    ps_flsh = srk.two_phase_psflash(P_out, z, s_in, temp=None)
    print(f"  T={ps_flsh.T:.2f} K, phase={ps_flsh.phase}")
    print(f"  betaV={ps_flsh.betaV}, betaL={ps_flsh.betaL}")
    print(f"  x={ps_flsh.x}")
    print(f"  y={ps_flsh.y}")
    print(f"  sum(x)={np.sum(ps_flsh.x):.10f}, sum(y)={np.sum(ps_flsh.y):.10f}")

    # Compute enthalpy at isentropic outlet
    h_isen = 0
    if ps_flsh.betaV > 1e-12:
        h_v2 = srk.enthalpy(ps_flsh.T, P_out, ps_flsh.y, srk.VAPPH)[0]
        s_v2 = srk.entropy(ps_flsh.T, P_out, ps_flsh.y, srk.VAPPH)[0]
        v_v2 = sv(srk.specific_volume(ps_flsh.T, P_out, ps_flsh.y, srk.VAPPH))
        h_isen += ps_flsh.betaV * h_v2
        print(f"  Vapor: h={h_v2:.2f}, s={s_v2:.4f}, v={v_v2:.6e}")
    if ps_flsh.betaL > 1e-12:
        h_l2 = srk.enthalpy(ps_flsh.T, P_out, ps_flsh.x, srk.LIQPH)[0]
        s_l2 = srk.entropy(ps_flsh.T, P_out, ps_flsh.x, srk.LIQPH)[0]
        v_l2 = sv(srk.specific_volume(ps_flsh.T, P_out, ps_flsh.x, srk.LIQPH))
        h_isen += ps_flsh.betaL * h_l2
        print(f"  Liquid: h={h_l2:.2f}, s={s_l2:.4f}, v={v_l2:.6e}")
    print(f"  h_isentropic={h_isen:.2f} J/mol")
except Exception as e:
    print(f"  PS flash FAILED: {e}")
    import traceback; traceback.print_exc()

if h_isen is not None:
    # Simulate compressor with eta=0.75
    eta = 0.75
    h_actual = h_in + (h_isen - h_in) / eta
    print(f"\n=== Step 3: PH flash at outlet (P={P_out} Pa, h={h_actual:.2f} J/mol) ===")
    print(f"  (actual compression, eta={eta})")
    try:
        ph_flsh = srk.two_phase_phflash(P_out, z, h_actual, temp=None)
        print(f"  T={ph_flsh.T:.2f} K, phase={ph_flsh.phase}")
        print(f"  betaV={ph_flsh.betaV}, betaL={ph_flsh.betaL}")
        print(f"  x={ph_flsh.x}")
        print(f"  y={ph_flsh.y}")

        # Check if x/y arrays contain valid data
        print(f"  sum(x)={np.sum(ph_flsh.x):.10f}, sum(y)={np.sum(ph_flsh.y):.10f}")
        print(f"  any NaN in x: {np.any(np.isnan(ph_flsh.x))}, in y: {np.any(np.isnan(ph_flsh.y))}")

        # Compute properties at the PH flash result
        print(f"\n  --- Properties at PH flash result ---")

        # Try vapor phase properties
        print(f"  Trying VAPPH properties with y:")
        try:
            h_vap = srk.enthalpy(ph_flsh.T, P_out, ph_flsh.y, srk.VAPPH)[0]
            s_vap = srk.entropy(ph_flsh.T, P_out, ph_flsh.y, srk.VAPPH)[0]
            v_vap = sv(srk.specific_volume(ph_flsh.T, P_out, ph_flsh.y, srk.VAPPH))
            print(f"    h={h_vap:.2f}, s={s_vap:.4f}, v={v_vap:.6e}")
        except Exception as e:
            print(f"    FAILED: {e}")

        # Try liquid phase properties (even though single-phase vapor)
        print(f"  Trying LIQPH properties with y (vapor comp):")
        try:
            h_liq = srk.enthalpy(ph_flsh.T, P_out, ph_flsh.y, srk.LIQPH)[0]
            s_liq = srk.entropy(ph_flsh.T, P_out, ph_flsh.y, srk.LIQPH)[0]
            v_liq = sv(srk.specific_volume(ph_flsh.T, P_out, ph_flsh.y, srk.LIQPH))
            print(f"    h={h_liq:.2f}, s={s_liq:.4f}, v={v_liq:.6e}")
        except Exception as e:
            print(f"    FAILED: {e}")

        # Try with x array (which may be garbage for single-phase)
        if ph_flsh.betaV > 0.999:
            print(f"  x array from single-phase PH flash: {ph_flsh.x}")
            print(f"  Trying LIQPH properties with x (liquid comp, may be garbage):")
            try:
                h_liq_x = srk.enthalpy(ph_flsh.T, P_out, ph_flsh.x, srk.LIQPH)[0]
                s_liq_x = srk.entropy(ph_flsh.T, P_out, ph_flsh.x, srk.LIQPH)[0]
                v_liq_x = sv(srk.specific_volume(ph_flsh.T, P_out, ph_flsh.x, srk.LIQPH))
                print(f"    h={h_liq_x:.2f}, s={s_liq_x:.4f}, v={v_liq_x:.6e}")
            except Exception as e:
                print(f"    FAILED: {e}")

    except Exception as e:
        print(f"  PH flash FAILED: {e}")
        import traceback; traceback.print_exc()

print(f"\n=== Step 4: Direct PH flash tests at various enthalpies ===")
for h_test in [-79940, -75000, -70000, -65000]:
    try:
        flsh = srk.two_phase_phflash(P_out, z, float(h_test), temp=None)
        print(f"  h={h_test}: T={flsh.T:.2f} K, betaV={flsh.betaV:.6f}, betaL={flsh.betaL:.6f}, "
              f"phase={flsh.phase}, sum(x)={np.sum(flsh.x):.6f}, sum(y)={np.sum(flsh.y):.6f}")
    except Exception as e:
        print(f"  h={h_test}: FAILED: {e}")
