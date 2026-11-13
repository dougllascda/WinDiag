using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinDiag.Core.Update;

/// <summary>Frequência com que o aplicativo verifica se há versão nova. Quem decide quando verificar é o App; o Core nunca consulta a rede sozinho.</summary>
public enum UpdateFrequency
{
    /// <summary>Só quando o usuário pede.</summary>
    Manual,
    /// <summary>A cada abertura do aplicativo.</summary>
    OnStartup,
    /// <summary>No máximo uma vez por dia.</summary>
    Daily,
    /// <summary>No máximo uma vez por semana.</summary>
    Weekly,
    /// <summary>No máximo uma vez por mês (30 dias).</summary>
    Monthly,
}

/// <summary>Arquivo anexado a uma versão publicada.</summary>
/// <param name="Name">Nome do arquivo (ex.: "WinDiag-Setup-1.3.0.exe").</param>
/// <param name="DownloadUrl">Endereço de download (browser_download_url do GitHub).</param>
/// <param name="Size">Tamanho informado em bytes (0 se não informado).</param>
public sealed record UpdateAsset(string Name, string DownloadUrl, long Size);

/// <summary>Informações da versão mais recente publicada.</summary>
/// <param name="Version">Versão publicada, sem o "v" inicial (ex.: "1.3.0").</param>
/// <param name="Url">Página da versão para abrir no navegador.</param>
/// <param name="IsNewer">true se a versão publicada é maior que a instalada.</param>
/// <param name="Notes">Texto das notas da versão (corpo do release).</param>
/// <param name="Assets">Arquivos anexados à versão.</param>
public sealed record UpdateInfo(string Version, string Url, bool IsNewer, string Notes, IReadOnlyList<UpdateAsset> Assets);

/// <summary>
/// Consulta pontual da última versão publicada no GitHub. Só roda quando o App pede: o Core nunca a chama sozinho,
/// não baixa nem instala nada aqui e, em qualquer falha (sem rede, limite da API, resposta inesperada), devolve null.
/// </summary>
public static class UpdateCheck
{
    /// <summary>Endereço da API com a última versão publicada.</summary>
    private const string LatestUrl = "https://api.github.com/repos/dougllascda/WinDiag/releases/latest";

    /// <summary>Consulta a última versão (GET com User-Agent "WinDiag" e limite de 8 s). Null em qualquer falha; versões rascunho ou pré-lançamento são ignoradas.</summary>
    /// <param name="ct">Cancelamento.</param>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            req.Headers.UserAgent.ParseAdd("WinDiag");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(json, AppInfo.Version);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Interpreta o JSON de um release da API (tag_name, html_url, body, draft, prerelease e assets). Null se o JSON for inválido, faltar a versão ou for rascunho/pré-lançamento.</summary>
    /// <param name="json">Corpo da resposta.</param>
    /// <param name="current">Versão instalada.</param>
    public static UpdateInfo? Parse(string json, string current)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Flag(root, "draft") || Flag(root, "prerelease")) return null;
            if (!root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) return null;
            var rawTag = (tag.GetString() ?? "").Trim();
            var latest = rawTag.TrimStart('v', 'V');
            if (latest.Length == 0) return null;
            var url = Str(root, "html_url");
            var notes = Str(root, "body");
            var assets = new List<UpdateAsset>();
            if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) continue;
                    var name = Str(a, "name");
                    var dl = Str(a, "browser_download_url");
                    if (name.Length == 0 || dl.Length == 0) continue;
                    long size = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var l) ? l : 0;
                    assets.Add(new UpdateAsset(name, dl, size));
                }
            }
            // Só tags no formato de versão são aceitas como "mais nova": qualquer outra coisa nunca dispara atualização.
            bool validTag = Regex.IsMatch(rawTag, @"^v?\d+(\.\d+){1,3}$");
            return new UpdateInfo(latest, url, validTag && IsNewer(current, latest), notes, assets);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Lê um campo booleano; ausente ou de outro tipo vale false.</summary>
    /// <param name="o">Objeto JSON.</param>
    /// <param name="name">Nome do campo.</param>
    private static bool Flag(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Lê um campo de texto; ausente, nulo ou de outro tipo vale "".</summary>
    /// <param name="o">Objeto JSON.</param>
    /// <param name="name">Nome do campo.</param>
    private static string Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>true se <paramref name="latest"/> é uma versão maior que <paramref name="current"/>. Aceita "v" inicial e sufixo de pré-lançamento (ignorado); versão ilegível conta como não mais nova.</summary>
    /// <param name="current">Versão instalada (ex.: "1.2.0").</param>
    /// <param name="latest">Versão publicada (ex.: "v1.3.0").</param>
    public static bool IsNewer(string current, string latest)
    {
        var a = ParseVersion(current);
        var b = ParseVersion(latest);
        if (a is null || b is null) return false;
        if (b != a) return b > a;
        // Mesmos números: "1.3.0-rc1" é menor que "1.3.0". Só é mais nova a final sobre a pré-versão.
        return IsPrerelease(current) && !IsPrerelease(latest);
    }

    /// <summary>true se a versão tem sufixo de pré-lançamento ("-rc1", "-beta"). O sufixo "+build" não conta.</summary>
    /// <param name="version">Texto da versão.</param>
    public static bool IsPrerelease(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var v = version.Trim();
        int plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        return v.Contains('-');
    }

    /// <summary>true se o endereço é do repositório oficial do WinDiag: https e começando com <see cref="AppInfo.RepoUrl"/> seguido de "/". Usado antes de abrir links vindos da rede.</summary>
    /// <param name="url">Endereço.</param>
    public static bool IsOfficialUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith(AppInfo.RepoUrl + "/", StringComparison.Ordinal)
        && AppInfo.RepoUrl.StartsWith("https://", StringComparison.Ordinal) && !url.Any(char.IsControl) && !url.Contains('\\') && !url.Contains("..", StringComparison.Ordinal);
    /// <summary>true se já passou tempo suficiente desde a última verificação, segundo a frequência. Manual nunca está vencida; OnStartup sempre; as demais comparam com 1, 7 e 30 dias (inclusive); nunca verificado conta como vencido, exceto no Manual.</summary>
    /// <param name="f">Frequência escolhida.</param>
    /// <param name="lastCheckUtc">Data (UTC) da última verificação ou null; uma data no futuro conta como vencido.</param>
    /// <param name="nowUtc">Agora (UTC).</param>
    public static bool IsDue(UpdateFrequency f, DateTime? lastCheckUtc, DateTime nowUtc)
    {
        int days;
        switch (f)
        {
            case UpdateFrequency.Manual: return false;
            case UpdateFrequency.OnStartup: return true;
            case UpdateFrequency.Daily: days = 1; break;
            case UpdateFrequency.Weekly: days = 7; break;
            case UpdateFrequency.Monthly: days = 30; break;
            default: return false;
        }
        if (lastCheckUtc is null) return true;
        if (lastCheckUtc.Value > nowUtc) return true; // data no futuro (relógio mudou): não confia e verifica
        return nowUtc - lastCheckUtc.Value >= TimeSpan.FromDays(days);
    }

    /// <summary>Lê os números da versão (até 4 partes), ignorando o "v" inicial e o que vier depois de "-" ou "+".</summary>
    /// <param name="text">Texto da versão.</param>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text.Trim().TrimStart('v', 'V'), @"^\d+(\.\d+){0,3}");
        if (!m.Success) return null;
        var s = m.Value;
        if (!s.Contains('.')) s += ".0";
        // Completa as partes ausentes com zero: "1.2" e "1.2.0" são a mesma versão.
        return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision)) : null;
    }
}
