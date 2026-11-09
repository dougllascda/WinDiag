using System.IO;
using System.Text.Json;

namespace WinDiag.App.Views;

/// <summary>
/// Passos do plano marcados como "Feito", guardados em <c>UserPrefs.PlanDone</c> (settings.json) pela chave estável do passo.
/// Na primeira leitura, o antigo plan.json (se existir) é mesclado uma única vez e renomeado.
/// </summary>
public static class PlanStore
{
    /// <summary>Trava das operações sobre o conjunto em memória e a gravação.</summary>
    private static readonly object Gate = new();
    /// <summary>Conjunto em memória das chaves concluídas (carregado sob demanda).</summary>
    private static HashSet<string>? _done;

    /// <summary>Caminho do arquivo da versão antiga, migrado uma única vez.</summary>
    private static string LegacyPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinDiag", "plan.json");

    /// <summary>Carrega o conjunto na primeira chamada, a partir das preferências e do arquivo antigo.</summary>
    private static HashSet<string> Load()
    {
        if (_done != null) return _done;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in Prefs.Read().PlanDone ?? new List<string>()) set.Add(k);
        _done = set;
        MigrateLegacy(set);
        return set;
    }

    /// <summary>Mescla o plan.json antigo e o renomeia para ".migrado" para não repetir; falhas de E/S são ignoradas.</summary>
    private static void MigrateLegacy(HashSet<string> set)
    {
        try
        {
            if (!File.Exists(LegacyPath)) return;
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(LegacyPath));
            if (list != null)
                foreach (var k in list)
                {
                    set.Add(k);
                    Added.Add(k);
                    Removed.Remove(k);
                }
            Persist();
            File.Move(LegacyPath, LegacyPath + ".migrado", true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // arquivo antigo ilegível: ignora (a migração é uma conveniência)
        }
    }

    // Alterações feitas nesta sessão; a gravação mescla com o arquivo atual (atual ∪ adicionados − removidos).
    /// <summary>Chaves marcadas nesta sessão (ainda a mesclar no arquivo).</summary>
    private static readonly HashSet<string> Added = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Chaves desmarcadas nesta sessão.</summary>
    private static readonly HashSet<string> Removed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Grava mesclando com o arquivo atual (e não sobrescrevendo), para não perder marcações de outra instância ou de outro campo.</summary>
    private static void Persist() =>
        Prefs.Update(p =>
        {
            var merged = new HashSet<string>(p.PlanDone ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            merged.UnionWith(Added);
            merged.ExceptWith(Removed);
            p.PlanDone = merged.OrderBy(x => x, StringComparer.Ordinal).ToList();
        });

    /// <summary>true se o passo da chave foi marcado como feito.</summary>
    public static bool IsDone(string key)
    {
        lock (Gate) return Load().Contains(key);
    }

    /// <summary>Marca ou desmarca o passo e grava as preferências (falhas de gravação são ignoradas).</summary>
    public static void SetDone(string key, bool done)
    {
        lock (Gate)
        {
            var set = Load();
            if (done)
            {
                set.Add(key);
                Added.Add(key);
                Removed.Remove(key);
            }
            else
            {
                set.Remove(key);
                Removed.Add(key);
                Added.Remove(key);
            }
            Persist();
        }
    }
}
