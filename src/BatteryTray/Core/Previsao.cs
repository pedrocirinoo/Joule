using System.Text.Json;

namespace BatteryTray.Core;

/// <summary>
/// Fork pedrocirinoo: estima quanto tempo a bateria dura. Guarda a % de cada aparelho
/// desde a ultima carga (historico.json, na pasta de dados) e divide a % de agora pela
/// queda por hora. So responde com queda de 2+ pontos em 20+ minutos: antes disso, nada.
/// </summary>
public static class Previsao
{
    private sealed record Ponto(DateTime Utc, int Percent);

    private static readonly object Gate = new();
    private static Dictionary<string, List<Ponto>>? _hist;
    private static string Arquivo => Path.Combine(Path.GetDirectoryName(AppPaths.SettingsFile)!, "historico.json");

    private static Dictionary<string, List<Ponto>> Hist()
    {
        if (_hist is not null) return _hist;
        try
        {
            if (File.Exists(Arquivo))
                _hist = JsonSerializer.Deserialize<Dictionary<string, List<Ponto>>>(File.ReadAllText(Arquivo));
        }
        catch (Exception ex) { Log.Error("Previsao.Load", ex); }
        return _hist ??= new Dictionary<string, List<Ponto>>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Uma leitura nova. Carregando ou subiu: comeca de novo (houve carga).</summary>
    public static void Registrar(string key, int percent, bool charging, DateTime utc)
    {
        lock (Gate)
        {
            var hist = Hist();
            if (!hist.TryGetValue(key, out var pts)) hist[key] = pts = new List<Ponto>();
            var ultimo = pts.Count > 0 ? pts[^1] : null;
            if (charging || (ultimo is not null && percent > ultimo.Percent + 1)) pts.Clear();
            if (charging) { Salvar(); return; }
            if (ultimo is not null && ultimo.Percent == percent && pts.Count > 0) return; // so guarda mudanca
            pts.Add(new Ponto(utc, percent));
            if (pts.Count > 300) pts.RemoveRange(0, pts.Count - 300);
            Salvar();
        }
    }

    /// <summary>"dura ~5 h" ou null quando ainda nao da para estimar.</summary>
    public static string? Texto(string key, int percentAgora)
    {
        lock (Gate)
        {
            if (!Hist().TryGetValue(key, out var pts) || pts.Count < 2) return null;
            var primeiro = pts[0];
            var ultimo = pts[^1];
            int queda = primeiro.Percent - ultimo.Percent;
            double horas = (ultimo.Utc - primeiro.Utc).TotalHours;
            if (queda < 2 || horas < 1.0 / 3) return null;
            double restante = percentAgora / (queda / horas);
            string t = restante < 1 ? $"{Math.Max(5, (int)Math.Round(restante * 60 / 5) * 5)} min"
                : restante < 48 ? $"{Math.Round(restante):0} h"
                : $"{Math.Round(restante / 24):0} dias";
            return L.T($"dura ~{t}", $"lasts ~{t}");
        }
    }

    private static void Salvar()
    {
        try
        {
            AppPaths.EnsureDir();
            AppPaths.WriteAtomic(Arquivo, JsonSerializer.Serialize(_hist));
        }
        catch (Exception ex) { Log.Error("Previsao.Save", ex); }
    }
}
