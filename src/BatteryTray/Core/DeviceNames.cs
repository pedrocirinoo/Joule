namespace BatteryTray.Core;

/// <summary>
/// Nome dado pela pessoa a um aparelho (fork pedrocirinoo). Vale em todo lugar
/// que mostra o nome: painel, widget, tooltip da bandeja e avisos. O cache
/// guarda o nome original, entao apagar o apelido volta ao nome de fabrica.
/// </summary>
public static class DeviceNames
{
    private static IReadOnlyDictionary<string, string> _map = new Dictionary<string, string>();

    /// <summary>
    /// O mesmo aparelho com outra chave (chave -> chave principal). O apelido fica gravado
    /// na principal e vale nas duas.
    /// Redragon M916 PRO 4K: pelo receptor e' 3554:F54F; no cabo, 3554:F54D ("VGN/VXE 3-Mode Mouse").
    /// </summary>
    private static readonly Dictionary<string, string> Same = new(StringComparer.OrdinalIgnoreCase)
    {
        ["compx:3554:F54D"] = "compx:3554:F54F",
    };

    public static void Use(IReadOnlyDictionary<string, string> map) => _map = map;

    /// <summary>Chave onde o apelido e' gravado.</summary>
    public static string Principal(string key) => Same.TryGetValue(key, out var main) ? main : key;

    /// <summary>As outras chaves do mesmo aparelho (sem a principal).</summary>
    public static IEnumerable<string> Others(string key)
    {
        string main = Principal(key);
        return Same.Where(p => p.Value.Equals(main, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key);
    }

    /// <summary>Apelido da chave principal; senao o de uma chave irma (gravado antes da ligacao); senao o original.</summary>
    public static string Resolve(string key, string original)
    {
        if (key.Length == 0) return original;
        foreach (var k in Others(key).Prepend(Principal(key)))
            if (_map.TryGetValue(k, out var name) && !string.IsNullOrWhiteSpace(name)) return name;
        return original;
    }
}
