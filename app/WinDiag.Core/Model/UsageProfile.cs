namespace WinDiag.Core.Model;

/// <summary>Perfil de uso escolhido pelo usuário: define quais achados, otimizações e áreas da nota são exibidos.</summary>
public enum UsageProfile
{
    /// <summary>Jogos (padrão).</summary>
    Gaming,
    /// <summary>Trabalho e escritório.</summary>
    Work
}

/// <summary>A que perfil um achado ou uma otimização se aplica. <see cref="Both"/> vale para qualquer perfil.</summary>
public enum Scope
{
    /// <summary>Vale para os dois perfis.</summary>
    Both,
    /// <summary>Relevante só para o perfil Jogos.</summary>
    Gaming,
    /// <summary>Relevante só para o perfil Trabalho.</summary>
    Work
}

/// <summary>Regras de escopo: decide se um item de determinado <see cref="Scope"/> aparece em um perfil.</summary>
public static class ScopeRules
{
    /// <summary>true se um item com o escopo dado deve ser considerado no perfil (Both vale sempre).</summary>
    /// <param name="scope">Escopo do achado ou da otimização.</param>
    /// <param name="profile">Perfil de uso ativo.</param>
    public static bool Matches(Scope scope, UsageProfile profile) =>
        scope == Scope.Both
        || (scope == Scope.Gaming && profile == UsageProfile.Gaming)
        || (scope == Scope.Work && profile == UsageProfile.Work);
}
