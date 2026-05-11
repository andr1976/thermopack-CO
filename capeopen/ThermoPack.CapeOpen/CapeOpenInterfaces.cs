using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ThermoPack.CapeOpen;

// ──────────────────────────────────────────────────────────────────────
// CAPE-OPEN 1.1 interfaces are provided by the CO-LaN Primary Interop
// Assembly (CAPE-OPENv1-1-0.dll), referenced in the project file.
//
// Import with: using CAPEOPEN;
//
// This file contains category GUIDs for COM registration and
// additional COM interface declarations not in the .NET BCL.
// ──────────────────────────────────────────────────────────────────────

public static class CapeOpenCategories
{
    /// <summary>CATID for CAPE-OPEN 1.1 Property Package Manager.</summary>
    public const string PropertyPackageManager = "CF51E383-0110-4ed8-ACB7-B50CFDE6908E";

    /// <summary>CATID for CAPE-OPEN 1.1 Property Package.</summary>
    public const string PropertyPackage = "CF51E384-0110-4ed8-ACB7-B50CFDE6908E";
}

/// <summary>
/// IPersistStreamInit COM interface for COFE flowsheet persistence.
/// Not in .NET BCL — declared via [ComImport].
/// </summary>
[ComImport, Guid("7FD52380-4E07-101B-AE2D-08002B2EC713")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPersistStreamInit
{
    void GetClassID(out Guid pClassID);
    [PreserveSig] int IsDirty();
    void Load(IStream pStm);
    void Save(IStream pStm, [MarshalAs(UnmanagedType.Bool)] bool fClearDirty);
    void GetSizeMax(out long pcbSize);
    void InitNew();
}
