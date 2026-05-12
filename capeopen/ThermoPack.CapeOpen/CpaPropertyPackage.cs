using System;
using System.Linq;
using System.Runtime.InteropServices;
using ThermoPack.Core;
using ThermoPack.Core.Models;

namespace ThermoPack.CapeOpen;

[ComVisible(true)]
[Guid("E7A1B2C3-0A04-4D5E-9F00-A00E30AC0004")]
[ClassInterface(ClassInterfaceType.None)]
[ProgId("ThermoPack.CPA")]
public class CpaPropertyPackage : ThermoPackPropertyPackageBase
{
    protected override string PackageName => "ThermoPack CPA";
    protected override string PackageDescription => "Cubic-Plus-Association equation of state (thermopack)";
    protected override EosType EosType => EosType.CPA;
    protected override Guid PackageClsid => new("E7A1B2C3-0A04-4D5E-9F00-A00E30AC0004");

    protected override void InitializeEngine(ThermoPackEngine engine, string compString)
    {
        // CPA requires at least one self-associating component (one with CPA-* keys).
        // Non-associating components are fine in the mixture, but Fortran crashes
        // if no component self-associates.
        bool hasAssociating = _selectedComponents.Any(c =>
            c.AvailableEosKeys.Any(k => k.StartsWith("CPA-", StringComparison.OrdinalIgnoreCase)));

        if (!hasAssociating)
        {
            var names = string.Join(", ", _selectedComponents.Select(c => c.Ident));
            throw new InvalidOperationException(
                $"CPA requires at least one self-associating component (e.g. H2O, MEOH, ETOH). " +
                $"None of the selected components ({names}) have CPA association data. " +
                $"Use SRK or PR instead for non-associating mixtures.");
        }

        engine.InitCpa(compString, "SRK");
    }

    [ComRegisterFunction]
    public static void Register(Type t) => RegisterPackage(t,
        "ThermoPack CPA",
        "Cubic-Plus-Association equation of state (thermopack)");

    [ComUnregisterFunction]
    public static void Unregister(Type t) => UnregisterPackage(t);
}
