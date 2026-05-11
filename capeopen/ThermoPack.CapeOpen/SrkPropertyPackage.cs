using System;
using System.Runtime.InteropServices;
using ThermoPack.Core;
using ThermoPack.Core.Models;

namespace ThermoPack.CapeOpen;

[ComVisible(true)]
[Guid("E7A1B2C3-0A02-4D5E-9F00-A00E30AC0002")]
[ClassInterface(ClassInterfaceType.None)]
[ProgId("ThermoPack.SRK")]
public class SrkPropertyPackage : ThermoPackPropertyPackageBase
{
    protected override string PackageName => "ThermoPack SRK";
    protected override string PackageDescription => "Soave-Redlich-Kwong cubic equation of state (thermopack)";
    protected override EosType EosType => EosType.SRK;
    protected override Guid PackageClsid => new("E7A1B2C3-0A02-4D5E-9F00-A00E30AC0002");

    protected override void InitializeEngine(ThermoPackEngine engine, string compString)
    {
        engine.InitCubic(compString, "SRK");
    }

    [ComRegisterFunction]
    public static void Register(Type t) => RegisterPackage(t,
        "ThermoPack SRK",
        "Soave-Redlich-Kwong cubic equation of state (thermopack)");

    [ComUnregisterFunction]
    public static void Unregister(Type t) => UnregisterPackage(t);
}
