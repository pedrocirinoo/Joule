using System.Text.Json;

namespace BatteryTray.Core;

/// <summary>
/// Fork pedrocirinoo: estima quanto tempo a bateria dura. Guarda a % de cada aparelho
/// desde a ultima carga e a velocidade de descarga (pontos por hora) do ciclo. A conta
/// usa a velocidade deste ciclo quando ja ha queda de 2+ pontos em 20+ min; antes disso
/// (por exemplo, em 100% logo depois de carregar), usa a velocidade guardada do ciclo
/// anterior. Sem nenhuma das duas, nao mostra nada.
/// </summary>
public static class Previsao
{
    public sealed record Ponto(DateTime Utc, int Percent);

    private sealed class Estado
    {
        public Dictionary<string, List<Ponto>> Pontos { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> Taxas { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly object Gate = new();
    private static Estado? _estado;
    private static string Arquivo => Path.Combine(Path.GetDirectoryName(AppPaths.SettingsFile)!, "previsao.json");

    private static Estado Dados()
    {
        if (_estado is not null) return _estado;
        try
        {
            if (File.Exists(Arquivo))
                _estado = JsonSerializer.Deserialize<Estado>(File.ReadAllText(Arquivo));
        }
        catch (Exception ex) { Log.Error("Previsao.Load", ex); }
        _estado ??= new Estado();
        _estado.Pontos = new(_estado.Pontos, StringComparer.OrdinalIgnoreCase);
        _estado.Taxas = new(_estado.Taxas, StringComparer.OrdinalIgnoreCase);
        return _estado;
    }

    /// <summary>Uma leitura nova. Carregando ou subiu: comeca um ciclo novo (a taxa guardada fica).</summary>
    public static void Registrar(string key, int percent, bool charging, DateTime utc)
    {
        lock (Gate)
        {
            var e = Dados();
            if (!e.Pontos.TryGetValue(key, out var pts)) e.Pontos[key] = pts = new List<Ponto>();
            var ultimo = pts.Count > 0 ? pts[^1] : null;
            if (charging || (ultimo is not null && percent > ultimo.Percent + 1)) pts.Clear();
            if (charging) { Salvar(); return; }
            if (ultimo is not null && ultimo.Percent == percent && pts.Count > 0) return; // so guarda mudanca
            pts.Add(new Ponto(utc, percent));
            if (pts.Count > 300) pts.RemoveRange(0, pts.Count - 300);
            if (TaxaDoCiclo(pts) is double taxa) e.Taxas[key] = taxa;
            Salvar();
        }
    }

    /// <summary>"dura ~5 h" ou null quando ainda nao da para estimar.</summary>
    public static string? Texto(string key, int percentAgora)
    {
        lock (Gate)
        {
            var e = Dados();
            double? taxa = e.Pontos.TryGetValue(key, out var pts) ? TaxaDoCiclo(pts) : null;
            if (taxa is null && e.Taxas.TryGetValue(key, out var guardada)) taxa = guardada;
            if (taxa is not > 0) return null;
            double restante = percentAgora / taxa.Value;
            string t = restante < 1 ? $"{Math.Max(5, (int)Math.Round(restante * 60 / 5) * 5)} min"
                : restante < 48 ? $"{Math.Round(restante):0} h"
                : $"{Math.Round(restante / 24):0} dias";
            return L.T($"dura ~{t}", $"lasts ~{t}");
        }
    }

    /// <summary>Pontos por hora neste ciclo, com queda de 2+ pontos em 20+ min; senao null.</summary>
    private static double? TaxaDoCiclo(List<Ponto> pts)
    {
        if (pts.Count < 2) return null;
        int queda = pts[0].Percent - pts[^1].Percent;
        double horas = (pts[^1].Utc - pts[0].Utc).TotalHours;
        return queda >= 2 && horas >= 1.0 / 3 ? queda / horas : null;
    }

    private static void Salvar()
    {
        try
        {
            AppPaths.EnsureDir();
            AppPaths.WriteAtomic(Arquivo, JsonSerializer.Serialize(_estado));
        }
        catch (Exception ex) { Log.Error("Previsao.Save", ex); }
    }
}
