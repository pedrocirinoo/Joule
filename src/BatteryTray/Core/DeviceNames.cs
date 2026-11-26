namespace BatteryTray.Core;

/// <summary>
/// Nome dado pela pessoa a um aparelho (fork pedrocirinoo). Vale em todo lugar
/// que mostra o nome: painel, widget, tooltip da bandeja e avisos. O cache
/// guarda o nome original, entao apagar o apelido volta ao nome de fabrica.
/// </summary>
public static class DeviceNames
{
    private static IReadOnlyDictionary<string, string> _map = new Dictionary<string, string>();

    public static void Use(IReadOnlyDictionary<string, string> map) => _map = map;

    public static string Resolve(string key, string original) =>
        key.Length > 0 && _map.TryGetValue(key, out var name) && !string.IsNullOrWhiteSpace(name) ? name : original;
}
