using System;
using System.Runtime.InteropServices;
using ThermoPack.Core;
using ThermoPack.Core.Models;

namespace ThermoPack.CapeOpen;

[ComVisible(true)]
[Guid("E7A1B2C3-0A01-4D5E-9F00-A00E30AC0001")]
[ClassInterface(ClassInterfaceType.None)]
[ProgId("ThermoPack.PengRobinson")]
public class PengRobinsonPropertyPackage : ThermoPackPropertyPackageBase
{
    protected override string PackageName => "ThermoPack Peng-Robinson";
    protected override string PackageDescription => "Peng-Robinson cubic equation of state (thermopack)";
    protected override EosType EosType => EosType.PengRobinson;
    protected override Guid PackageClsid => new("E7A1B2C3-0A01-4D5E-9F00-A00E30AC0001");

    protected override void InitializeEngine(ThermoPackEngine engine, string compString)
    {
        engine.InitCubic(compString, "PR");
    }

    [ComRegisterFunction]
    public static void Register(Type t) => RegisterPackage(t,
        "ThermoPack Peng-Robinson",
        "Peng-Robinson cubic equation of state (thermopack)");

    [ComUnregisterFunction]
    public static void Unregister(Type t) => UnregisterPackage(t);
}
