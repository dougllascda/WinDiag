using WinDiag.Core.Model;

namespace WinDiag.Core.Collectors;

/// <summary>
/// Um coletor lê uma área do sistema (somente leitura) e devolve dados + verificações.
/// Regras: nunca lança exceção para fora (o ScanEngine também protege); cada leitura que
/// pode falhar fica em try/catch e vira res.Note(...); campo indisponível mostra "n/d".
/// </summary>
public interface ICollector
{
    /// <summary>Chave estável (ex.: "cpu"). Usada pela interface para escolher a aba.</summary>
    string Key { get; }
    /// <summary>Título da seção em pt-BR, exibido na interface e no relatório.</summary>
    string Title { get; }

    /// <summary>
    /// Lê a área do sistema e devolve o resultado. Nunca lança exceção; leituras que falham viram notas e o campo mostra "n/d".
    /// </summary>
    /// <param name="facts">Fatos compartilhados: o coletor lê o que a fase A já preencheu e grava os da sua área.</param>
    SectionResult Collect(SystemFacts facts);
}
