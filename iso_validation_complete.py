import numpy as np
from thermopack.multiparameter import multiparam
from thermopack.utils import back_compatible_unpack

def unpack(val):
    if isinstance(val, (tuple, list)):
        return val[0]
    try:
        return back_compatible_unpack(val)
    except:
        return val

def validate_iso_gas(gas_id, x_vec, test_points):
    comp_idents = [
        "C1", "N2", "CO2", "C2", "C3", "NC4", "IC4", "NC5", "IC5", "NC6",
        "NC7", "NC8", "NC9", "NC10", "H2", "O2", "CO", "H2O", "H2S", "HE", "AR"
    ]
    active_indices = [i for i, val in enumerate(x_vec) if val > 0]
    comps = [comp_idents[i] for i in active_indices]
    x = np.array([x_vec[i] for i in active_indices])
    
    gerg = multiparam(",".join(comps), "GERG2008")
    
    mws = [gerg.compmoleweight(i+1) for i in range(len(comps))]
    MW_mix = sum(x[i] * mws[i] for i in range(len(comps)))
    
    print(f"\n--- ISO 20765-2 Gas {gas_id} (MW ~ {MW_mix:.4f} g/mol) ---")
    print(f"{'T [K]':>6} {'P [MPa]':>8} | {'rho Diff%':>10} {'Z Diff%':>10} {'w Diff%':>10} | {'Phase':>10}")
    print("-" * 85)
    
    for T, P_mpa, rho_ref, z_ref, H_ref, s_ref, w_ref in test_points:
        P_pa = P_mpa * 1e6
        try:
            fl_res = gerg.two_phase_tpflash(T, P_pa, x)
            betaV = fl_res.betaV
            phase_idx = fl_res.phase
            
            if phase_idx == gerg.TWOPH:
                phase_str = "Two-Phase"
                calc_phase = gerg.VAPPH # Use VAPPH as default for properties
            elif phase_idx == gerg.LIQPH:
                phase_str = "Liquid"
                calc_phase = gerg.LIQPH
            elif phase_idx == gerg.VAPPH:
                phase_str = "Vapour"
                calc_phase = gerg.VAPPH
            elif phase_idx == gerg.SINGLEPH:
                phase_str = "Single"
                # For single/supercritical, specific_volume usually works with VAPPH or LIQPH
                # often returning the same result. Let's use VAPPH.
                calc_phase = gerg.VAPPH
            else:
                phase_str = f"Unknown({phase_idx})"
                calc_phase = gerg.VAPPH

            V_mol = unpack(gerg.specific_volume(T, P_pa, x, calc_phase))
            rho_kg_m3 = (1.0 / V_mol) * (MW_mix / 1000.0)
            Z = unpack(gerg.zfac(T, P_pa, x, calc_phase))
            W = unpack(gerg.speed_of_sound(T, P_pa, x, x, x, 1.0, 0.0, calc_phase))
            
            d_rho = abs(rho_kg_m3 - rho_ref) / rho_ref * 100
            d_z = abs(Z - z_ref) / z_ref * 100
            d_w = abs(W - w_ref) / w_ref * 100
            
            print(f"{T:6.1f} {P_mpa:8.1f} | {d_rho:10.2e} {d_z:10.2e} {d_w:10.2e} | {phase_str:>10}")
        except Exception as e:
            print(f"{T:6.1f} {P_mpa:8.1f} | Point Failed | Error")

if __name__ == "__main__":
    # Gas 1
    v1 = np.zeros(21); v1[0:12] = [0.796, 0.100, 0.010, 0.057, 0.020, 0.005, 0.005, 0.002, 0.002, 0.001, 0.001, 0.001]
    tp1 = [(180.0, 10.0, 389.18, 0.33956, 0, 0, 796.80), (220.0, 10.0, 267.65, 0.40397, 0, 0, 427.36),
           (200.0, 20.0, 380.27, 0.62552, 0, 0, 826.44), (250.0, 20.0, 283.04, 0.67232, 0, 0, 568.65),
           (305.0, 3.0, 24.835, 0.94211, 0, 0, 394.93), (350.0, 10.0, 74.667, 0.91021, 0, 0, 430.98)]
    validate_iso_gas(1, v1, tp1)

    # Gas 2
    v2 = np.zeros(21); v2[0:11] = [0.650, 0.065, 0.190, 0.010, 0.010, 0.010, 0.010, 0.010, 0.010, 0.010, 0.020]; v2[19] = 0.005
    tp2 = [(180.0, 13.0, 548.61, 0.42502, 0, 0, 945.53), (400.0, 10.0, 88.041, 0.91676, 0, 0, 383.33)]
    validate_iso_gas(2, v2, tp2)

    # Gas 3
    v3 = np.zeros(21); v3[0:12] = [0.720, 0.010, 0.010, 0.150, 0.010, 0.010, 0.010, 0.010, 0.010, 0.020, 0.010, 0.010]; v3[15:17] = [0.010, 0.010]
    tp3 = [(150.0, 10.0, 504.68, 0.38600, 0, 0, 1267.9), (400.0, 10.0, 82.020, 0.89066, 0, 0, 392.34)]
    validate_iso_gas(3, v3, tp3)

    # Gas 4
    v4 = np.zeros(21); v4[0]=0.55; v4[4]=0.1; v4[14]=0.2; v4[15]=0.04999; v4[16]=0.1; v4[17]=0.00001
    tp4 = [(300.0, 10.0, 80.581, 0.89738, 0, 0, 425.19), (400.0, 10.0, 54.906, 0.98774, 0, 0, 495.53)]
    validate_iso_gas(4, v4, tp4)

    # Gas 5
    v5 = np.zeros(21); v5[1]=0.25; v5[2]=0.15; v5[14]=0.1; v5[15]=0.1; v5[16]=0.1; v5[18]=0.1; v5[19]=0.1; v5[20]=0.1
    tp5 = [(150.0, 30.0, 704.83, 0.94229, 0, 0, 713.43), (400.0, 5.0, 41.380, 1.0031, 0, 0, 416.75)]
    validate_iso_gas(5, v5, tp5)

    # Gas 6
    v6 = np.zeros(21); v6[4]=0.1; v6[5]=0.1; v6[6]=0.1; v6[7]=0.1; v6[8]=0.1; v6[9]=0.1; v6[10]=0.1; v6[11]=0.1; v6[12]=0.1; v6[13]=0.05; v6[17]=0.05
    tp6 = [(250.0, 10.0, 699.02, 0.55998, 0, 0, 1283.4), (500.0, 2.0, 52.175, 0.75024, 0, 0, 176.26)]
    validate_iso_gas(6, v6, tp6)
