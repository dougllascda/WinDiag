using System.Text;
using System.Text.RegularExpressions;
using WinDiag.Core.Model;

namespace WinDiag.Core.Report;

/// <summary>
/// Mascaramento aplicado somente ao relatório exportado (a interface mostra tudo):
/// nome do usuário vira &lt;USUARIO&gt; (&lt;USER&gt; em inglês) e números de série mostram apenas os 4 últimos caracteres.
/// </summary>
internal static class ReportMask
{
    /// <summary>Marcador do nome de usuário no relatório (&lt;USUARIO&gt; em português, &lt;USER&gt; em inglês).</summary>
    public static string UserTag => Loc.T("<USUARIO>", "<USER>");

    /// <summary>true se o nome de uma coluna ou rótulo indica número de série (contém "Série", "Serie" ou "Serial"). Esses valores passam por <see cref="Serial"/>.</summary>
    /// <param name="name">Rótulo ou nome de coluna.</param>
    public static bool IsSerialName(string? name) =>
        !string.IsNullOrEmpty(name) &&
        (name.Contains("Série", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("Serie", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("Serial", StringComparison.OrdinalIgnoreCase));

    /// <summary>Oculta um número de série mostrando só os 4 últimos caracteres (até 4 caracteres viram "****"). Valores vazios ou "n/d" ficam como estão. Os 4 finais bastam para o usuário conferir o item sem expor o número inteiro.</summary>
    /// <param name="value">Número de série.</param>
    public static string Serial(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == Fmt.NA) return value ?? Fmt.NA;
        var v = value.Trim();
        return v.Length <= 4 ? "****" : "****" + v[^4..];
    }

    /// <summary>Marcador do nome do computador no relatório.</summary>
    public static string PcTag => "<PC>";

    /// <summary>Cache do último regex de mascaramento, indexado pelos termos usados, para não recompilar a cada célula do relatório.</summary>
    private static (string Key, Regex Rx, Dictionary<string, string> Tags)? _cache;

    /// <summary>
    /// Mascara o nome do usuário e o nome da pasta do perfil por &lt;USUARIO&gt; e o nome do computador por &lt;PC&gt;.
    /// Só troca palavras inteiras (sem letra, dígito ou "_" colado): "Admin" não corrompe "Administrator".
    /// </summary>
    /// <param name="text">Texto do relatório.</param>
    /// <param name="userName">Usuário (null = Environment.UserName; vazio = não mascara).</param>
    /// <param name="profileName">Nome da pasta do perfil (null = Path.GetFileName do perfil do usuário; vazio = não mascara).</param>
    /// <param name="machineName">Nome do computador (null = Environment.MachineName; vazio = não mascara).</param>
    public static string User(string? text, string? userName = null, string? profileName = null, string? machineName = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        userName ??= SafeEnv(() => Environment.UserName);
        profileName ??= SafeEnv(() => Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/')));
        machineName ??= SafeEnv(() => Environment.MachineName);

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? term, string tag)
        {
            term = term?.Trim();
            if (string.IsNullOrEmpty(term) || term.Length < 2 || tags.ContainsKey(term)) return;
            tags[term] = tag;
        }
        var userTag = UserTag;
        Add(profileName, userTag);
        Add(userName, userTag);
        Add(machineName, PcTag);
        if (tags.Count == 0) return text;

        var key = string.Join("\u0001", tags.Select(kv => kv.Key.ToUpperInvariant() + "=" + kv.Value));
        var c = _cache;
        if (c is null || c.Value.Key != key)
        {
            var alt = string.Join("|", tags.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
            var rx = new Regex(@"(?<![\p{L}\p{N}_])(?:" + alt + @")(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            c = (key, rx, tags);
            _cache = c;
        }
        var map = c.Value.Tags;
        return c.Value.Rx.Replace(text, m => map.TryGetValue(m.Value, out var tag) ? tag : m.Value);
    }

    /// <summary>Lê um valor do ambiente sem deixar exceção escapar; falha vira texto vazio (e então nada é mascarado por esse termo).</summary>
    /// <param name="f">Leitura do ambiente.</param>
    private static string SafeEnv(Func<string> f)
    {
        try { return f() ?? ""; } catch { return ""; }
    }

    /// <summary>Texto livre: apenas máscara de usuário.</summary>
    public static string Text(string? text) => User(text);

    /// <summary>Valor de uma linha rótulo/valor: serial mascarado se o rótulo indicar série, e sempre com o usuário/PC mascarados.</summary>
    /// <param name="label">Rótulo da linha.</param>
    /// <param name="value">Valor exibido na interface.</param>
    public static string RowValue(string label, string value) =>
        IsSerialName(label) ? User(Serial(value)) : User(value);

    /// <summary>Células de uma linha de tabela; colunas com nome de série são mascaradas.</summary>
    public static List<string> Cells(InfoTable t, List<string> row)
    {
        var r = new List<string>(row.Count);
        for (int i = 0; i < row.Count; i++)
        {
            var col = i < t.Columns.Count ? t.Columns[i] : "";
            r.Add(IsSerialName(col) ? User(Serial(row[i])) : User(row[i]));
        }
        return r;
    }

    /// <summary>Junta o texto em uma linha só, trocando quebras por espaço (necessário para tabelas alinhadas em TXT).</summary>
    /// <param name="s">Texto original.</param>
    public static string OneLine(string? s) =>
        (s ?? "").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>Quebra o texto em linhas de até <paramref name="width"/> colunas, todas com o mesmo recuo.</summary>
    public static string Wrap(string text, int width, string indent) => Wrap(text, width, indent, indent);

    /// <summary>
    /// Quebra o texto com recuo deslocado: a primeira linha começa com <paramref name="first"/>
    /// (ex.: "  [ AVISO ] ") e as seguintes com <paramref name="cont"/> (espaços do mesmo tamanho),
    /// de modo que a continuação fique alinhada ao texto.
    /// </summary>
    public static string Wrap(string text, int width, string first, string cont)
    {
        var sb = new StringBuilder();
        var line = new StringBuilder(first);
        int baseLen = first.Length;
        foreach (var w in OneLine(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > baseLen && line.Length + 1 + w.Length > width)
            {
                sb.Append(line).Append("\r\n");
                line.Clear().Append(cont);
                baseLen = cont.Length;
            }
            if (line.Length > baseLen) line.Append(' ');
            line.Append(w);
        }
        if (line.Length > baseLen || sb.Length == 0) sb.Append(line);
        return sb.ToString();
    }

    /// <summary>"Rótulo:" alinhado em <paramref name="col"/> colunas, sempre com pelo menos 2 espaços antes do valor.</summary>
    public static string LabelCell(string label, int col = 26)
    {
        var s = label + ":";
        return s.Length + 2 > col ? s + "  " : s.PadRight(col);
    }
}
