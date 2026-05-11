using System.Text.Json;
using ThermoPack.Core.Models;

namespace ThermoPack.Core;

/// <summary>
/// Parses component data from thermopack fluids/*.json files.
/// </summary>
public class ComponentDatabase
{
    private readonly List<Component> _components = new();

    /// <summary>All loaded components.</summary>
    public IReadOnlyList<Component> Components => _components;

    /// <summary>
    /// Loads all JSON files from the specified fluids directory.
    /// </summary>
    public void LoadFromDirectory(string fluidsDir)
    {
        _components.Clear();
        if (!Directory.Exists(fluidsDir)) return;

        foreach (var file in Directory.GetFiles(fluidsDir, "*.json"))
        {
            try
            {
                var comp = ParseFluidJson(file);
                if (comp != null)
                    _components.Add(comp);
            }
            catch
            {
                // Skip malformed files
            }
        }
    }

    private static Component? ParseFluidJson(string path)
    {
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("ident", out var identProp)) return null;
        if (!root.TryGetProperty("name", out var nameProp)) return null;

        var comp = new Component
        {
            Ident = identProp.GetString() ?? "",
            Name = nameProp.GetString() ?? ""
        };

        if (root.TryGetProperty("formula", out var f)) comp.Formula = f.GetString() ?? "";
        if (root.TryGetProperty("cas_number", out var cas)) comp.CasNumber = cas.GetString() ?? "";
        if (root.TryGetProperty("mol_weight", out var mw)) comp.MolWeight = mw.GetDouble();

        if (root.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
        {
            comp.Aliases = aliases.EnumerateArray()
                .Where(a => a.ValueKind == JsonValueKind.String)
                .Select(a => a.GetString()!)
                .ToArray();
        }

        if (root.TryGetProperty("critical", out var crit))
        {
            if (crit.TryGetProperty("temperature", out var tc)) comp.CriticalTemperature = tc.GetDouble();
            if (crit.TryGetProperty("pressure", out var pc)) comp.CriticalPressure = pc.GetDouble();
        }

        if (root.TryGetProperty("acentric_factor", out var acf))
        {
            if (acf.TryGetProperty("acf", out var acfVal)) comp.AcentricFactor = acfVal.GetDouble();
        }

        if (root.TryGetProperty("boiling_temperature", out var bt))
        {
            if (bt.TryGetProperty("temperature", out var btVal)) comp.BoilingTemperature = btVal.GetDouble();
        }

        // Detect available EOS keys
        var eosKeys = new[] { "PR", "SRK", "SAFTVRMIE-1", "SAFTVRMIE-2" };
        foreach (var key in eosKeys)
        {
            if (root.TryGetProperty(key, out _))
                comp.AvailableEosKeys.Add(key);
        }

        // Detect numbered EOS keys (PC-SAFT-1, CPA-1, etc.)
        foreach (var prop in root.EnumerateObject())
        {
            var name = prop.Name;
            if (name.StartsWith("PC-SAFT-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("CPA-", StringComparison.OrdinalIgnoreCase))
            {
                comp.AvailableEosKeys.Add(name);
            }
        }

        // Also check for CPA entries nested inside SRK
        if (root.TryGetProperty("SRK", out var srk))
        {
            foreach (var sub in srk.EnumerateObject())
            {
                if (sub.Name.StartsWith("CPA-", StringComparison.OrdinalIgnoreCase))
                    comp.AvailableEosKeys.Add(sub.Name);
            }
        }

        return comp;
    }

    public Component? FindByIdent(string ident)
        => _components.FirstOrDefault(c => c.Ident.Equals(ident, StringComparison.OrdinalIgnoreCase));

    public Component? FindByCas(string cas)
        => _components.FirstOrDefault(c => c.CasNumber == cas);

    public Component? FindByName(string name)
        => _components.FirstOrDefault(c =>
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            c.Aliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)));

    public IReadOnlyList<Component> GetComponentsForEos(EosType eos)
        => _components.Where(c => c.HasEos(eos)).ToList();
}
