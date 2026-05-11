using System;
using CAPEOPEN110;

namespace ThermoPack.CapeOpen;

/// <summary>
/// Thin wrapper around ICapeThermoMaterial for reading/writing properties
/// with proper variant boxing/unboxing.
/// </summary>
public class MaterialObjectWrapper
{
    private readonly ICapeThermoMaterial _mat;

    public MaterialObjectWrapper(ICapeThermoMaterial material)
    {
        _mat = material ?? throw new ArgumentNullException(nameof(material));
    }

    public double GetTemperature()
    {
        foreach (var name in new[] { "temperature", "Temperature" })
        {
            try
            {
                object r = null!;
                _mat.GetOverallProp(name, "", ref r);
                return ExtractDouble(r);
            }
            catch { }
        }
        return double.NaN;
    }

    public double GetPressure()
    {
        foreach (var name in new[] { "pressure", "Pressure" })
        {
            try
            {
                object r = null!;
                _mat.GetOverallProp(name, "", ref r);
                return ExtractDouble(r);
            }
            catch { }
        }
        return double.NaN;
    }

    public double[] GetFeed(int expectedCount = 0)
    {
        try
        {
            object r = null!;
            _mat.GetOverallProp("fraction", "Mole", ref r);
            if (r is double[] da)
            {
                if (expectedCount > 0 && da.Length != expectedCount)
                {
                    var fit = new double[expectedCount];
                    int len = Math.Min(da.Length, expectedCount);
                    Array.Copy(da, fit, len);
                    return fit;
                }
                return da;
            }
        }
        catch { }
        return expectedCount > 0 ? new double[expectedCount] : Array.Empty<double>();
    }

    public double GetOverallPropScalar(string property, string basis = "")
    {
        try
        {
            object r = null!;
            _mat.GetOverallProp(property, basis, ref r);
            return ExtractDouble(r);
        }
        catch
        {
            return double.NaN;
        }
    }

    public double GetSinglePhasePropScalar(string property, string phaseLabel, string basis = "")
    {
        try
        {
            object r = null!;
            _mat.GetSinglePhaseProp(property, phaseLabel, basis, ref r);
            return ExtractDouble(r);
        }
        catch
        {
            return double.NaN;
        }
    }

    public double GetPhaseFraction(string phaseLabel)
    {
        return GetSinglePhasePropScalar("phaseFraction", phaseLabel, "Mole");
    }

    public void SetPresentPhases(string[] labels, int[] status)
    {
        _mat.SetPresentPhases(labels, status);
    }

    public void SetSinglePhaseProp(string property, string phaseLabel, string basis, double[] values)
    {
        _mat.SetSinglePhaseProp(property, phaseLabel, basis, values);
    }

    public void SetOverallProp(string property, string basis, double[] values)
    {
        _mat.SetOverallProp(property, basis, values);
    }

    /// <summary>
    /// Reads a target value from the material object, trying multiple bases.
    /// Used for PH/PS/PVF/TVF flash specs.
    /// </summary>
    public double ReadTargetValue(string property, string specBasis, string calcType)
    {
        var bases = new List<string>();
        if (!string.IsNullOrEmpty(specBasis)) bases.Add(specBasis);
        foreach (var b in new[] { "", "Mole", "mole" })
        {
            if (!bases.Contains(b)) bases.Add(b);
        }

        bool isOverall = string.IsNullOrEmpty(calcType) ||
                         calcType.IndexOf("overall", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         calcType.Equals("UNDEFINED", StringComparison.OrdinalIgnoreCase);

        foreach (var basis in bases)
        {
            try
            {
                object r = null!;
                if (isOverall)
                    _mat.GetOverallProp(property, basis, ref r);
                else
                    _mat.GetSinglePhaseProp(property, calcType, basis, ref r);
                double val = ExtractDouble(r);
                if (!double.IsNaN(val)) return val;
            }
            catch { }
        }
        return double.NaN;
    }

    private static double ExtractDouble(object? r)
    {
        if (r is double[] da && da.Length > 0) return da[0];
        if (r is double d) return d;
        if (r is float f) return f;
        return double.NaN;
    }
}
