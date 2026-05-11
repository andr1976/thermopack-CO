using System;
using System.Runtime.InteropServices;
using ThermoPack.Core;
using ThermoPack.Core.Models;

namespace ThermoPack.CapeOpen;

[ComVisible(true)]
[Guid("E7A1B2C3-0A06-4D5E-9F00-A00E30AC0006")]
[ClassInterface(ClassInterfaceType.None)]
[ProgId("ThermoPack.PCSAFT")]
public class PcSaftPropertyPackage : ThermoPackPropertyPackageBase
{
    protected override string PackageName => "ThermoPack PC-SAFT";
    protected override string PackageDescription => "Perturbed-Chain SAFT equation of state (thermopack)";
    protected override EosType EosType => EosType.PCSAFT;
    protected override Guid PackageClsid => new("E7A1B2C3-0A06-4D5E-9F00-A00E30AC0006");

    protected override void InitializeEngine(ThermoPackEngine engine, string compString)
    {
        engine.InitPcSaft(compString);
    }

    [ComRegisterFunction]
    public static void Register(Type t) => RegisterPackage(t,
        "ThermoPack PC-SAFT",
        "Perturbed-Chain SAFT equation of state (thermopack)");

    [ComUnregisterFunction]
    public static void Unregister(Type t) => UnregisterPackage(t);
}
