namespace WinDiag.Core.Model;

/// <summary>Linha "rótulo: valor" no estilo CPU-Z.</summary>
public sealed class InfoRow
{
    /// <summary>Rótulo.</summary>
    public string Label { get; init; } = "";
    /// <summary>Valor já formatado no idioma atual ("n/d" ou "n/a" se ausente).</summary>
    public string Value { get; init; } = "";
    /// <summary>Opcional: colore o valor na interface.</summary>
    public Severity? Sev { get; init; }
}

/// <summary>Tabela simples (ex.: S.M.A.R.T., pentes de RAM, minifilters).</summary>
public sealed class InfoTable
{
    /// <summary>Título da tabela.</summary>
    public string Title { get; init; } = "";
    /// <summary>Nomes das colunas.</summary>
    public List<string> Columns { get; init; } = new();
    /// <summary>Linhas (células já formatadas).</summary>
    public List<List<string>> Rows { get; } = new();

    /// <summary>Acrescenta uma linha; cada célula é formatada por <see cref="Fmt.Val"/>.</summary>
    public InfoTable AddRow(params object?[] cells)
    {
        Rows.Add(cells.Select(c => Fmt.Val(c)).ToList());
        return this;
    }
}

/// <summary>Bloco com título (ex.: "Processador", "Disco 0 [D:]").</summary>
public sealed class InfoGroup
{
    /// <summary>Título do grupo.</summary>
    public string Title { get; init; } = "";
    /// <summary>Linhas rótulo/valor.</summary>
    public List<InfoRow> Rows { get; } = new();
    /// <summary>Tabelas do grupo.</summary>
    public List<InfoTable> Tables { get; } = new();
    /// <summary>Notas curtas exibidas abaixo do grupo (texto livre).</summary>
    public List<string> Notes { get; } = new();

    /// <summary>Adiciona "rótulo: valor". Valor nulo ou vazio vira "n/d".</summary>
    public InfoGroup Add(string label, object? value, Severity? sev = null)
    {
        Rows.Add(new InfoRow { Label = label, Value = Fmt.Val(value), Sev = sev });
        return this;
    }

    /// <summary>Cria e acrescenta uma tabela ao grupo.</summary>
    public InfoTable Table(string title, params string[] columns)
    {
        var t = new InfoTable { Title = title, Columns = columns.ToList() };
        Tables.Add(t);
        return t;
    }
}

/// <summary>Saída de um coletor: grupos de dados + verificações + notas de leitura.</summary>
public sealed class SectionResult
{
    /// <summary>Chave estável da seção.</summary>
    public string Key { get; init; } = "";
    /// <summary>Título em pt-BR.</summary>
    public string Title { get; init; } = "";
    /// <summary>Grupos de dados.</summary>
    public List<InfoGroup> Groups { get; } = new();
    /// <summary>Verificações geradas.</summary>
    public List<Finding> Findings { get; } = new();
    /// <summary>Leituras que falharam ou ficaram parciais (ex.: "S.M.A.R.T.: exige administrador").</summary>
    public List<string> Notes { get; } = new();
    /// <summary>Tempo gasto pelo coletor.</summary>
    public TimeSpan Elapsed { get; set; }
    /// <summary>Erro fatal do coletor (não deve acontecer; coletores capturam as próprias falhas).</summary>
    public string? Error { get; set; }

    /// <summary>Cria e acrescenta um grupo.</summary>
    public InfoGroup Group(string title)
    {
        var g = new InfoGroup { Title = title };
        Groups.Add(g);
        return g;
    }

    /// <summary>Acrescenta uma verificação (a seção de origem é preenchida automaticamente).</summary>
    /// <param name="sev">Gravidade.</param>
    /// <param name="title">Título curto.</param>
    /// <param name="evidence">O que foi medido.</param>
    /// <param name="explain">O que significa.</param>
    /// <param name="action">O que fazer.</param>
    /// <param name="fixId">Otimização relacionada.</param>
    /// <param name="impact">Impacto de 0 a 100.</param>
    /// <param name="id">Id estável (padrão "seção.assunto"); vazio gera Seção + "." + slug do título.</param>
    /// <param name="simple">Explicação simples para leigos.</param>
    /// <param name="guide">Nome de um BiosTopic, ou "".</param>
    public void Find(Severity sev, string title, string evidence, string explain = "", string action = "", string fixId = "", int impact = 0,
        string id = "", string simple = "", string guide = "")
        => Findings.Add(new Finding(sev, title, evidence, explain, action, fixId, impact)
        {
            Section = Key,
            Id = string.IsNullOrWhiteSpace(id) ? DefaultId(Key, title) : id,
            Simple = simple,
            GuideTopic = guide,
        });

    /// <summary>Id padrão: seção + "." + título em ASCII minúsculo com "_" entre palavras.</summary>
    /// <param name="section">Chave da seção.</param>
    /// <param name="title">Título do achado.</param>
    public static string DefaultId(string section, string title)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char ch in title.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (ch < 128 && char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        return section + "." + sb.ToString().Trim('_');
    }

    /// <summary>Acrescenta uma nota de leitura, sem duplicar.</summary>
    public void Note(string text)
    {
        if (!Notes.Contains(text)) Notes.Add(text);
    }
}
