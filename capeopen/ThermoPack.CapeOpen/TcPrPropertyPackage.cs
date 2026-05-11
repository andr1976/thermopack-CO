using System;
using System.Runtime.InteropServices;
using ThermoPack.Core;
using ThermoPack.Core.Models;

namespace ThermoPack.CapeOpen;

[ComVisible(true)]
[Guid("E7A1B2C3-0A03-4D5E-9F00-A00E30AC0003")]
[ClassInterface(ClassInterfaceType.None)]
[ProgId("ThermoPack.TcPR")]
public class TcPrPropertyPackage : ThermoPackPropertyPackageBase
{
    protected override string PackageName => "ThermoPack tcPR";
    protected override string PackageDescription => "Translated-consistent Peng-Robinson equation of state (thermopack)";
    protected override EosType EosType => EosType.TcPR;
    protected override Guid PackageClsid => new("E7A1B2C3-0A03-4D5E-9F00-A00E30AC0003");

    protected override void InitializeEngine(ThermoPackEngine engine, string compString)
    {
        engine.InitTcPR(compString);
    }

    [ComRegisterFunction]
    public static void Register(Type t) => RegisterPackage(t,
        "ThermoPack tcPR",
        "Translated-consistent Peng-Robinson equation of state (thermopack)");

    [ComUnregisterFunction]
    public static void Unregister(Type t) => UnregisterPackage(t);
}
