using System.Runtime.CompilerServices;
using WinDiag.Core;

namespace WinDiag.Tests;

/// <summary>
/// Os testes comparam textos em português; fixa o idioma antes de qualquer teste, para que o resultado
/// não dependa do idioma do Windows de quem roda (o CI do GitHub roda em inglês).
/// Testes de inglês trocam o idioma explicitamente e voltam ao final.
/// </summary>
internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Init() => Loc.Set("pt-BR");
}
