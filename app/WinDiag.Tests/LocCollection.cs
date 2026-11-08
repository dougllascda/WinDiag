using Xunit;

namespace WinDiag.Tests;

/// <summary>Testes que trocam o idioma global (<c>Loc.Set</c>) não podem rodar em paralelo com outros.</summary>
[CollectionDefinition("Loc", DisableParallelization = true)]
public sealed class LocCollection
{
}
