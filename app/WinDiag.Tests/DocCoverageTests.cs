using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace WinDiag.Tests;

/// <summary>
/// Cobertura de documentação: todo tipo e membro (de qualquer visibilidade) em WinDiag.Core e WinDiag.App deve ter
/// comentário de documentação XML ou &lt;inheritdoc/&gt;. O teste só parseia a sintaxe; não compila nada.
/// </summary>
public class DocCoverageTests
{
    private const int MaxListed = 60;

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "app", "WinDiag.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Raiz do repositório (app\\WinDiag.sln) não encontrada a partir de " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> SourceFiles(string root)
    {
        foreach (var project in new[] { "WinDiag.Core", "WinDiag.App" })
        {
            var projDir = Path.Combine(root, "app", project);
            foreach (var file in Directory.EnumerateFiles(projDir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(projDir, file);
                var parts = rel.Split(Path.DirectorySeparatorChar);
                if (parts.Any(p => p.Equals("obj", StringComparison.OrdinalIgnoreCase) || p.Equals("bin", StringComparison.OrdinalIgnoreCase))) continue;
                var name = Path.GetFileName(file);
                if (name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("AssemblyInfo", StringComparison.OrdinalIgnoreCase)) continue;
                yield return file;
            }
        }
    }

    private static bool HasDoc(SyntaxNode node)
    {
        foreach (var t in node.GetLeadingTrivia())
        {
            if (!t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) && !t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)) continue;
            return true; // inclui <inheritdoc/> e <summary>
        }
        return false;
    }

    private static string MemberName(MemberDeclarationSyntax m) => m switch
    {
        BaseTypeDeclarationSyntax t => t.Identifier.Text,
        MethodDeclarationSyntax x => x.Identifier.Text,
        ConstructorDeclarationSyntax x => x.Identifier.Text + " (construtor)",
        DestructorDeclarationSyntax x => "~" + x.Identifier.Text,
        PropertyDeclarationSyntax x => x.Identifier.Text,
        IndexerDeclarationSyntax => "this[]",
        EventDeclarationSyntax x => x.Identifier.Text,
        EventFieldDeclarationSyntax x => x.Declaration.Variables.First().Identifier.Text,
        FieldDeclarationSyntax x => x.Declaration.Variables.First().Identifier.Text,
        OperatorDeclarationSyntax x => "operator " + x.OperatorToken.Text,
        ConversionOperatorDeclarationSyntax x => "operator " + x.Type,
        DelegateDeclarationSyntax x => x.Identifier.Text,
        _ => m.Kind().ToString(),
    };

    private static void Visit(SyntaxNode container, string file, List<(string Where, string Name)> missing, Dictionary<string, bool> partialDocs, List<(string Key, string Where, string Name)> partialMissing)
    {
        IEnumerable<SyntaxNode> children = container switch
        {
            BaseNamespaceDeclarationSyntax ns => ns.Members,
            TypeDeclarationSyntax td => td.Members,
            EnumDeclarationSyntax ed => ed.Members,
            CompilationUnitSyntax cu => cu.Members,
            _ => Array.Empty<SyntaxNode>(),
        };
        foreach (var child in children)
        {
            switch (child)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    Visit(ns, file, missing, partialDocs, partialMissing);
                    continue;
                case GlobalStatementSyntax:
                    continue;
                case EnumMemberDeclarationSyntax em:
                    if (!HasDoc(em)) missing.Add((Where(file, em), Owner(em) + "." + em.Identifier.Text));
                    continue;
                case MemberDeclarationSyntax m:
                {
                    bool doc = HasDoc(m);
                    var where = Where(file, m);
                    var name = (m.Parent is BaseTypeDeclarationSyntax p ? p.Identifier.Text + "." : "") + MemberName(m);
                    if (m is BaseTypeDeclarationSyntax bt && bt.Modifiers.Any(SyntaxKind.PartialKeyword))
                    {
                        var key = QualifiedName(bt);
                        partialDocs[key] = partialDocs.GetValueOrDefault(key) | doc;
                        if (!doc) partialMissing.Add((key, where, name));
                    }
                    else if (!doc) missing.Add((where, name));
                    if (m is TypeDeclarationSyntax or EnumDeclarationSyntax) Visit(m, file, missing, partialDocs, partialMissing);
                    break;
                }
            }
        }
    }

    private static string Owner(EnumMemberDeclarationSyntax em) => ((EnumDeclarationSyntax)em.Parent!).Identifier.Text;

    private static string QualifiedName(BaseTypeDeclarationSyntax t)
    {
        var parts = new List<string>();
        for (SyntaxNode? n = t; n != null; n = n.Parent)
        {
            if (n is BaseTypeDeclarationSyntax b) parts.Add(b.Identifier.Text);
            else if (n is BaseNamespaceDeclarationSyntax ns) parts.Add(ns.Name.ToString());
        }
        parts.Reverse();
        return string.Join(".", parts);
    }

    private static string Where(string file, SyntaxNode node)
    {
        var line = node.SyntaxTree.GetLineSpan(node.GetFirstToken().Span).StartLinePosition.Line + 1;
        return $"{file}:{line}";
    }

    /// <summary>Procura o primeiro membro sem documentação em Core e App e lista todos (até 60) com arquivo:linha:membro.</summary>
    [Fact]
    public void AllTypesAndMembersAreDocumented()
    {
        var root = FindRepoRoot();
        var missing = new List<(string Where, string Name)>();
        var partialDocs = new Dictionary<string, bool>();
        var partialMissing = new List<(string Key, string Where, string Name)>();
        foreach (var file in SourceFiles(root).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), new CSharpParseOptions(LanguageVersion.Latest), file);
            Visit(tree.GetCompilationUnitRoot(), Path.GetRelativePath(root, file), missing, partialDocs, partialMissing);
        }
        // Tipo parcial: basta uma das partes ter comentário.
        foreach (var (key, where, name) in partialMissing)
            if (!partialDocs.GetValueOrDefault(key)) missing.Add((where, name));

        if (missing.Count == 0) return;
        var lines = missing.Take(MaxListed).Select(x => $"{x.Where}:{x.Name}");
        var extra = missing.Count > MaxListed ? $"\n... e mais {missing.Count - MaxListed}" : "";
        Assert.Fail($"{missing.Count} tipo(s)/membro(s) sem comentário de documentação (mostrando até {MaxListed}):\n" + string.Join("\n", lines) + extra);
    }
}
