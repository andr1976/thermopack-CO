using System;
using System.Runtime.InteropServices;
using CAPEOPEN;

namespace ThermoPack.CapeOpen;

[ComVisible(true)]
[Guid("E7A1B2C3-0A00-4D5E-9F00-A00E30AC0000")]
[ClassInterface(ClassInterfaceType.None)]
[ProgId("ThermoPack.PackageManager")]
public class ThermoPackPackageManager :
    ICapeIdentification,
    ICapeThermoPropertyPackageManager
{
    private static readonly string[] PackageNames =
    {
        "ThermoPack Peng-Robinson",
        "ThermoPack SRK",
        "ThermoPack tcPR",
        "ThermoPack CPA",
        "ThermoPack GERG-2008",
        "ThermoPack PC-SAFT"
    };

    // ─── ICapeIdentification ──────────────────────────────────────────

    public string ComponentName
    {
        get => "ThermoPack Package Manager";
        set { }
    }

    public string ComponentDescription
    {
        get => "Manages ThermoPack CAPE-OPEN property packages";
        set { }
    }

    // ─── ICapeThermoPropertyPackageManager ─────────────────────────────

    public object GetPropertyPackageList()
    {
        return (string[])PackageNames.Clone();
    }

    public object GetPropertyPackage(string packageName)
    {
        foreach (var (name, factory) in new (string, Func<object>)[]
        {
            ("ThermoPack Peng-Robinson", () => new PengRobinsonPropertyPackage()),
            ("ThermoPack SRK", () => new SrkPropertyPackage()),
            ("ThermoPack tcPR", () => new TcPrPropertyPackage()),
            ("ThermoPack CPA", () => new CpaPropertyPackage()),
            ("ThermoPack GERG-2008", () => new Gerg2008PropertyPackage()),
            ("ThermoPack PC-SAFT", () => new PcSaftPropertyPackage()),
        })
        {
            if (string.Equals(name, packageName, StringComparison.OrdinalIgnoreCase))
                return factory();
        }
        throw new COMException($"Unknown package: {packageName}");
    }

    [ComRegisterFunction]
    public static void Register(Type t)
    {
        var clsid = t.GUID.ToString("B");
        var key = Microsoft.Win32.Registry.ClassesRoot.CreateSubKey(
            $@"CLSID\{clsid}\Implemented Categories\{{{CapeOpenCategories.PropertyPackageManager}}}");
        key?.Close();

        var desc = Microsoft.Win32.Registry.ClassesRoot.CreateSubKey(
            $@"CLSID\{clsid}\CapeDescription");
        desc?.SetValue("Name", "ThermoPack Package Manager");
        desc?.SetValue("Description", "Manages ThermoPack CAPE-OPEN property packages");
        desc?.SetValue("CapeVersion", "1.1");
        desc?.Close();
    }

    [ComUnregisterFunction]
    public static void Unregister(Type t)
    {
        var clsid = t.GUID.ToString("B");
        try
        {
            Microsoft.Win32.Registry.ClassesRoot.DeleteSubKeyTree(
                $@"CLSID\{clsid}\Implemented Categories\{{{CapeOpenCategories.PropertyPackageManager}}}",
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
}
