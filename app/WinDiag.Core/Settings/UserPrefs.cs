using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinDiag.Core.Settings;

/// <summary>
/// Preferências do usuário, gravadas em %AppData%\WinDiag\settings.json. Tolerante a arquivo antigo
/// (que tinha só o tema) e a erros: qualquer falha de leitura devolve os valores padrão.
/// </summary>
public sealed class UserPrefs
{
    /// <summary>Tema: "Auto", "Light" ou "Dark".</summary>
    public string Theme { get; set; } = "Auto";
    /// <summary>Idioma: "Auto" ou um código de cultura.</summary>
    public string Language { get; set; } = "Auto";
    /// <summary>Modo de exibição: "Simple" ou "Advanced".</summary>
    public string Mode { get; set; } = "Simple";
    /// <summary>Potência da fonte (PSU) em watts, informada pelo usuário.</summary>
    public int? PsuWatts { get; set; }
    /// <summary>Modelo da fonte, informado pelo usuário.</summary>
    public string? PsuModel { get; set; }
    /// <summary>Ids dos passos do plano de ação já concluídos.</summary>
    public List<string> PlanDone { get; set; } = new();
    /// <summary>Perfil de uso: "" = ainda não escolhido; "Gaming" ou "Work".</summary>
    public string Profile { get; set; } = "";

    /// <summary>Frequência da verificação de atualizações: "Manual", "OnStartup", "Daily", "Weekly" ou "Monthly". Padrão: "Weekly"; valor vazio ou inválido vale "Weekly".</summary>
    public string UpdateFrequency { get; set; } = "Weekly";
    /// <summary>Data e hora (UTC) da última verificação de atualização; null se nunca verificou.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }
    /// <summary>Versão que o usuário escolheu pular (ex.: "1.3.0"); null se nenhuma.</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>Converte a preferência de frequência em <see cref="Update.UpdateFrequency"/>; vazio, nulo ou inválido é Semanal (Weekly).</summary>
    /// <param name="pref">Valor de <see cref="UpdateFrequency"/>.</param>
    public static Update.UpdateFrequency UpdateFrequencyOf(string? pref) =>
        Enum.TryParse<Update.UpdateFrequency>(pref?.Trim(), true, out var f) && Enum.IsDefined(f) && !int.TryParse(pref?.Trim(), out _) ? f : Update.UpdateFrequency.Weekly;

    /// <summary>Converte a frequência de atualização de um conjunto de preferências em <see cref="Update.UpdateFrequency"/>.</summary>
    /// <param name="prefs">Preferências.</param>
    public static Update.UpdateFrequency UpdateFrequencyOf(UserPrefs prefs) => UpdateFrequencyOf(prefs.UpdateFrequency);

    /// <summary>Converte a preferência de perfil em <see cref="Model.UsageProfile"/>: "Work" (sem diferenciar maiúsculas) é Trabalho; vazio, nulo ou desconhecido é Jogos.</summary>
    /// <param name="pref">Valor de <see cref="Profile"/>.</param>
    public static Model.UsageProfile ProfileOf(string? pref) =>
        string.Equals(pref?.Trim(), "Work", StringComparison.OrdinalIgnoreCase) ? Model.UsageProfile.Work : Model.UsageProfile.Gaming;

    /// <summary>Opções do JSON das preferências: indentado, sem diferenciar maiúsculas nos nomes (aceita arquivos editados à mão) e sem escape de acentos.</summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Caminho do arquivo de configurações.</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinDiag", "settings.json");

    /// <summary>Lê as preferências. Arquivo ausente, antigo (só com o tema) ou ilegível devolve o que for possível/padrão.</summary>
    public static UserPrefs Load() => Load(FilePath);

    /// <summary>
    /// true quando o arquivo existe mas NÃO pôde ser lido (bloqueado, sem permissão ou erro inesperado, após 3 tentativas): os valores são os padrão
    /// e não representam o que está gravado. Quem for gravar não deve sobrescrever o arquivo; <see cref="Save()"/> já recusa gravar nesse caso.
    /// Não é gravada no JSON.
    /// </summary>
    [JsonIgnore]
    public bool IsFallback { get; private set; }

    /// <summary>Tentativas de leitura quando o arquivo está bloqueado (por exemplo, outra instância gravando).</summary>
    private const int ReadAttempts = 3;
    /// <summary>Espera entre tentativas de leitura, em milissegundos.</summary>
    private const int RetryDelayMs = 50;

    /// <summary>Lê as preferências de um caminho específico (usado em testes).</summary>
    /// <remarks>
    /// Arquivo ausente: valores padrão. JSON inválido: o arquivo é copiado para <c>settings.json.bad</c> e voltam os valores padrão
    /// (<see cref="IsFallback"/> false, pois o conteúdo ruim foi preservado). IOException/UnauthorizedAccess: até 3 tentativas com 50 ms;
    /// se continuar falhando, devolve os padrão com <see cref="IsFallback"/> = true.
    /// </remarks>
    /// <param name="path">Caminho do JSON.</param>
    public static UserPrefs Load(string path)
    {
        string text = "";
        try
        {
            if (!File.Exists(path)) return new UserPrefs();
            for (int attempt = 1; ; attempt++)
            {
                try { text = File.ReadAllText(path, System.Text.Encoding.UTF8); break; }
                catch (FileNotFoundException) { return new UserPrefs(); }
                catch (DirectoryNotFoundException) { return new UserPrefs(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= ReadAttempts) return new UserPrefs { IsFallback = true };
                    Thread.Sleep(RetryDelayMs);
                }
            }
        }
        catch (Exception)
        {
            return new UserPrefs { IsFallback = true };
        }

        try
        {
            var p = JsonSerializer.Deserialize<UserPrefs>(text, Json) ?? new UserPrefs();
            p.Theme = string.IsNullOrWhiteSpace(p.Theme) ? "Auto" : p.Theme;
            p.Language = string.IsNullOrWhiteSpace(p.Language) ? "Auto" : p.Language;
            p.Mode = string.IsNullOrWhiteSpace(p.Mode) ? "Simple" : p.Mode;
            p.PlanDone ??= new List<string>();
            p.Profile ??= "";
            if (string.IsNullOrWhiteSpace(p.UpdateFrequency)) p.UpdateFrequency = "Weekly";
            if (p.PsuWatts is <= 0) p.PsuWatts = null;
            return p;
        }
        catch (JsonException)
        {
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (Exception) { /* cópia é um extra */ }
            return new UserPrefs();
        }
        catch (Exception)
        {
            return new UserPrefs { IsFallback = true };
        }
    }

    /// <summary>Grava as preferências de forma atômica. Falhas de gravação são ignoradas (preferências são opcionais); com <see cref="IsFallback"/> não grava nada. Devolve true se gravou.</summary>
    public bool Save() => Save(FilePath);

    /// <summary>Grava em um caminho específico (usado em testes): escreve em <c>settings.json.tmp</c> e troca com File.Replace/File.Move, sem deixar o arquivo pela metade.</summary>
    /// <param name="path">Caminho do JSON.</param>
    public bool Save(string path)
    {
        if (IsFallback) return false; // o arquivo existente não foi lido: não sobrescrever
        string tmp = path + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json), new System.Text.UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
            return false;
        }
    }
}
