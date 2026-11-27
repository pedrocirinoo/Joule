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
        public Dictionary<string, List<Ponto>> Carga { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> TaxasCarga { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<double>> CiclosDescarga { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<double>> CiclosCarga { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
        _estado.Carga = new(_estado.Carga ?? new(), StringComparer.OrdinalIgnoreCase);
        _estado.TaxasCarga = new(_estado.TaxasCarga ?? new(), StringComparer.OrdinalIgnoreCase);
        _estado.CiclosDescarga = new(_estado.CiclosDescarga ?? new(), StringComparer.OrdinalIgnoreCase);
        _estado.CiclosCarga = new(_estado.CiclosCarga ?? new(), StringComparer.OrdinalIgnoreCase);
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
            if (!e.Carga.TryGetValue(key, out var carga)) e.Carga[key] = carga = new List<Ponto>();
            if (charging)
            {
                // Carregando: o ciclo de descarga termina (vai para a media) e recomeca.
                if (TaxaDoCiclo(pts) is double fim) Guardar(e.CiclosDescarga, key, fim);
                pts.Clear();
                if (percent > 0)
                {
                    if (carga.Count > 0 && percent < carga[^1].Percent) carga.Clear();
                    if (carga.Count == 0 || carga[^1].Percent != percent)
                    {
                        carga.Add(new Ponto(utc, percent));
                        if (TaxaDeCarga(carga) is double tc) e.TaxasCarga[key] = tc;
                    }
                }
                Salvar();
                return;
            }
            // Saiu da carga: a carga termina (vai para a media) e a proxima comeca do zero.
            if (TaxaDeCarga(carga) is double fimCarga) Guardar(e.CiclosCarga, key, fimCarga);
            carga.Clear();
            if (ultimo is not null && percent > ultimo.Percent + 1)
            {
                // Subiu sem o Joule ver a carga (controle desligado na base): o ciclo tambem termina.
                if (TaxaDoCiclo(pts) is double fim) Guardar(e.CiclosDescarga, key, fim);
                pts.Clear();
            }
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
            e.Pontos.TryGetValue(key, out var pts);
            double? media = Media(e.CiclosDescarga, key) ?? (e.Taxas.TryGetValue(key, out var antiga) ? antiga : null);
            int queda = pts is { Count: > 0 } ? pts[0].Percent - pts[^1].Percent : 0;
            double? taxa = Misturar(pts is null ? null : TaxaDoCiclo(pts), queda, media);
            if (taxa is not > 0)
            {
                // Sem queda ainda: se ficou 1 h+ sem cair 2 pontos, a taxa e' menor que 2/horas,
                // entao dura PELO MENOS % x horas / 2 (o numero cresce enquanto nao cai).
                if (pts is { Count: > 0 } && pts[0].Percent - percentAgora < 2)
                {
                    double horasSemCair = (DateTime.UtcNow - pts[0].Utc).TotalHours;
                    if (horasSemCair >= 1)
                    {
                        string m = Tempo(percentAgora * horasSemCair / 2, arredondaPraBaixo: true);
                        return L.T($"dura mais de ~{m}", $"lasts over ~{m}");
                    }
                }
                return null;
            }
            string t = Tempo(percentAgora / taxa.Value);
            return L.T($"dura ~{t}", $"lasts ~{t}");
        }
    }

    /// <summary>Minutos abaixo de 1 h; daí em diante sempre em horas (escolha do Pedro, 2026-11-26).</summary>
    private static string Tempo(double horas, bool arredondaPraBaixo = false)
    {
        Func<double, double> r = arredondaPraBaixo ? Math.Floor : Math.Round;
        if (horas < 1) return $"{Math.Max(5, (int)(r(horas * 60 / 5) * 5))} min";
        return $"{r(horas):0} h";
    }

    /// <summary>"carregado em ~40 min" ou null quando ainda nao da para estimar.</summary>
    public static string? TextoCarga(string key, int percentAgora)
    {
        lock (Gate)
        {
            if (percentAgora >= 100) return null;
            var e = Dados();
            e.Carga.TryGetValue(key, out var pts);
            double? media = Media(e.CiclosCarga, key) ?? (e.TaxasCarga.TryGetValue(key, out var antiga) ? antiga : null);
            int subida = pts is { Count: > 0 } ? pts[^1].Percent - pts[0].Percent : 0;
            double? taxa = Misturar(pts is null ? null : TaxaDeCarga(pts), subida, media);
            if (taxa is not > 0) return null;
            double horas = (100 - percentAgora) / taxa.Value;
            string t = horas < 1 ? $"{Math.Max(5, (int)Math.Round(horas * 60 / 5) * 5)} min"
                : $"{Math.Round(horas, horas < 3 ? 1 : 0):0.#} h";
            return L.T($"carregado em ~{t}", $"charged in ~{t}");
        }
    }

    private const int CiclosGuardados = 5;

    /// <summary>Guarda a velocidade de um ciclo terminado; ficam so os ultimos 5.</summary>
    private static void Guardar(Dictionary<string, List<double>> ciclos, string key, double taxa)
    {
        if (!ciclos.TryGetValue(key, out var lista)) ciclos[key] = lista = new List<double>();
        lista.Add(taxa);
        if (lista.Count > CiclosGuardados) lista.RemoveRange(0, lista.Count - CiclosGuardados);
    }

    /// <summary>Media dos ciclos guardados, o mais recente pesando mais (pesos 1, 2, 3...).</summary>
    private static double? Media(Dictionary<string, List<double>> ciclos, string key)
    {
        if (!ciclos.TryGetValue(key, out var lista) || lista.Count == 0) return null;
        double soma = 0, pesos = 0;
        for (int i = 0; i < lista.Count; i++) { soma += lista[i] * (i + 1); pesos += i + 1; }
        return soma / pesos;
    }

    /// <summary>
    /// Ciclo atual + media: o ritmo de agora ganha peso conforme a % anda (vale sozinho com
    /// 20 pontos); sem media, so o de agora; sem dados de agora, so a media.
    /// </summary>
    private static double? Misturar(double? agora, int pontosAndados, double? media)
    {
        if (agora is null) return media;
        if (media is null) return agora;
        double peso = Math.Clamp(pontosAndados / 20.0, 0, 1);
        return peso * agora.Value + (1 - peso) * media.Value;
    }

    /// <summary>Pontos por hora subindo nesta carga, com 2+ pontos em 10+ min; senao null.</summary>
    private static double? TaxaDeCarga(List<Ponto> pts)
    {
        if (pts.Count < 2) return null;
        int subida = pts[^1].Percent - pts[0].Percent;
        double horas = (pts[^1].Utc - pts[0].Utc).TotalHours;
        return subida >= 2 && horas >= 1.0 / 6 ? subida / horas : null;
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
