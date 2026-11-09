using System.Text.RegularExpressions;
using WinDiag.Core;
using WinDiag.Core.Rules;

namespace WinDiag.Tests;

/// <summary>
/// Varredura real nesta máquina com o idioma em inglês: nenhum texto gerado pelo próprio app
/// (títulos, rótulos, colunas, achados, plano, nota) pode sair em português.
/// Valores lidos do sistema (nomes de peças, mensagens do Windows em pt-BR, evidências com dados) ficam de fora.
/// </summary>
[Collection("Loc")]
public class EnglishScanTests
{
    // Acentos típicos do português e palavras frequentes nos textos do app.
    private static readonly Regex Pt = new(@"[ãõçáéíóúâêôà]|\b(não|você|placa|memória|leitura|exige|disponível|ligad[oa]|desligad[oa])\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public async Task RealScan_InEnglish_HasNoPortugueseInAppTexts()
    {
        Loc.Set("en");
        try
        {
            var engine = new ScanEngine();
            await engine.RunAsync();
            var problems = new List<string>();
            void Check(string where, string? text)
            {
                if (!string.IsNullOrWhiteSpace(text) && Pt.IsMatch(text)) problems.Add($"{where}: {text}");
            }

            foreach (var s in engine.Results)
            {
                Check($"[{s.Key}] section title", s.Title);
                foreach (var g in s.Groups)
                {
                    foreach (var r in g.Rows) Check($"[{s.Key}] label", r.Label);
                    foreach (var t in g.Tables)
                    {
                        Check($"[{s.Key}] table", t.Title);
                        foreach (var c in t.Columns) Check($"[{s.Key}] column", c);
                    }
                }
                foreach (var f in s.Findings)
                {
                    Check($"[{s.Key}] title {f.Id}", f.Title);
                    Check($"[{s.Key}] explain {f.Id}", f.Explain);
                    Check($"[{s.Key}] action {f.Id}", f.Action);
                    Check($"[{s.Key}] simple {f.Id}", f.Simple);
                    Check($"[{s.Key}] sev {f.Id}", f.SevLabel);
                }
            }

            var all = engine.AllFindings().ToList();
            var score = GameScore.Compute(all);
            Check("score grade", score.Grade);
            Check("score summary", score.Summary);
            foreach (var p in score.Parts) Check("score area", p.Area);
            foreach (var step in ActionPlan.Build(all))
            {
                Check($"plan where {step.Key}", step.Where);
                Check($"plan what {step.Key}", step.What);
            }

            // Perfil Trabalho: nota, áreas, resumo, plano e motivos dos programas de fábrica também saem em inglês.
            var work = GameScore.Compute(all, WinDiag.Core.Model.UsageProfile.Work);
            Check("work score grade", work.Grade);
            Check("work score summary", work.Summary);
            foreach (var p in work.Parts) Check("work score area", p.Area);
            foreach (var step in ActionPlan.Build(all, WinDiag.Core.Model.UsageProfile.Work))
            {
                Check($"work plan where {step.Key}", step.Where);
                Check($"work plan what {step.Key}", step.What);
            }
            foreach (var b in engine.Facts.Bloat) Check($"bloat reason {b.Name}", b.Reason);
            foreach (var pr in engine.Facts.Printers) Check($"printer status {pr.Name}", pr.StatusText);

            Assert.True(problems.Count == 0, "Textos em português no modo inglês:\n" + string.Join("\n", problems.Distinct().Take(40)));
        }
        finally
        {
            Loc.Set("pt-BR");
        }
    }
}
