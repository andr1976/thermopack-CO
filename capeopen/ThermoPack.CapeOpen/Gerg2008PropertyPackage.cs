using System;
using System.Runtime.InteropServices;
using ThermoPack.Core;
using ThermoPack.Core.Models;

namespace ThermoPack.CapeOpen;

[ComVisible(true)]
[Guid("E7A1B2C3-0A05-4D5E-9F00-A00E30AC0005")]
[ClassInterface(ClassInterfaceType.None)]
[ProgId("ThermoPack.GERG2008")]
public class Gerg2008PropertyPackage : ThermoPackPropertyPackageBase
{
    protected override string PackageName => "ThermoPack GERG-2008";
    protected override string PackageDescription => "GERG-2008 multiparameter equation of state (thermopack)";
    protected override EosType EosType => EosType.GERG2008;
    protected override Guid PackageClsid => new("E7A1B2C3-0A05-4D5E-9F00-A00E30AC0005");

    protected override void InitializeEngine(ThermoPackEngine engine, string compString)
    {
        engine.InitMultiparameter(compString, "NIST_MEOS", "DEFAULT");
    }

    [ComRegisterFunction]
    public static void Register(Type t) => RegisterPackage(t,
        "ThermoPack GERG-2008",
        "GERG-2008 multiparameter equation of state (thermopack)");

    [ComUnregisterFunction]
    public static void Unregister(Type t) => UnregisterPackage(t);
}
