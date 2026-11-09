using System.Globalization;
using System.Xml.Linq;
using WinDiag.Core.Util;

namespace WinDiag.Core.Work;

/// <summary>Capacidades lidas do relatório de bateria do powercfg.</summary>
/// <param name="DesignMWh">Capacidade de projeto somada de todas as baterias, em mWh (null se ausente).</param>
/// <param name="FullChargeMWh">Capacidade da carga completa atual somada, em mWh (null se ausente).</param>
/// <param name="Partial">true se alguma bateria do relatório foi ignorada por não ter carga completa legível (> 0); o projeto somado só inclui as baterias usadas.</param>
public sealed record BatteryCapacity(int? DesignMWh, int? FullChargeMWh, bool Partial = false)
{
    /// <summary>Desgaste em % (1 - carga completa / projeto), limitado a 0..100 e arredondado a 1 casa; null se faltar algum dado.</summary>
    public double? WearPct
    {
        get
        {
            if (DesignMWh is not > 0 || FullChargeMWh is null) return null;
            return Math.Round(Math.Clamp((1d - (double)FullChargeMWh.Value / DesignMWh.Value) * 100d, 0d, 100d), 1);
        }
    }
}

/// <summary>Leitura do relatório de bateria (<c>powercfg /batteryreport /xml</c>). Somente leitura; o arquivo é gravado numa pasta temporária e apagado.</summary>
public static class BatteryReport
{
    /// <summary>
    /// Interpreta o XML do relatório: soma DesignCapacity e FullChargeCapacity dos elementos Battery (o namespace é ignorado), mas só das baterias com FullChargeCapacity &gt; 0
    /// (a capacidade de projeto de uma bateria sem carga completa legível não entra, para não inflar o desgaste). Se alguma bateria for ignorada, <see cref="BatteryCapacity.Partial"/> é true.
    /// XML inválido ou sem os campos devolve capacidades nulas; nunca lança exceção.
    /// </summary>
    /// <param name="xml">Conteúdo do XML.</param>
    public static BatteryCapacity Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return new BatteryCapacity(null, null);
        try
        {
            var doc = XDocument.Parse(xml.TrimStart('﻿'));
            long design = 0, full = 0; bool hasD = false, hasF = false, partial = false;
            foreach (var b in doc.Descendants().Where(e => e.Name.LocalName == "Battery"))
            {
                var f = ReadInt(b, "FullChargeCapacity");
                var d = ReadInt(b, "DesignCapacity");
                if (f is not > 0) { if (d is > 0 || f is not null) partial = true; continue; }
                full += f.Value; hasF = true;
                if (d is > 0) { design += d.Value; hasD = true; }
            }
            return new BatteryCapacity(hasD ? (int)Math.Min(design, int.MaxValue) : null, hasF ? (int)Math.Min(full, int.MaxValue) : null, partial);
        }
        catch (Exception)
        {
            return new BatteryCapacity(null, null);
        }
    }

    /// <summary>Lê um campo inteiro de um elemento Battery do relatório XML (ignora o namespace). Null se ausente ou inválido.</summary>
    /// <param name="battery">Elemento Battery.</param>
    /// <param name="name">Nome do campo (DesignCapacity ou FullChargeCapacity).</param>
    private static long? ReadInt(XElement battery, string name)
    {
        var el = battery.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        if (el is null) return null;
        return long.TryParse(el.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>
    /// Gera o relatório com o powercfg (timeout de 30 s, sem janela) numa pasta temporária, lê as capacidades e apaga o arquivo.
    /// Devolve null e preenche <paramref name="error"/> se o powercfg falhou ou não gerou o arquivo (ex.: exige administrador). Se o resultado for parcial (<see cref="BatteryCapacity.Partial"/>), devolve as capacidades e também preenche <paramref name="error"/> com o aviso.
    /// </summary>
    /// <param name="error">Motivo da falha, no idioma atual.</param>
    public static BatteryCapacity? Read(out string? error)
    {
        error = null;
        string file = Path.Combine(Path.GetTempPath(), "windiag_battery_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var r = Proc.Run(Proc.Sys32("powercfg.exe"), $"/batteryreport /xml /output \"{file}\"", 30000);
            if (!File.Exists(file))
            {
                error = r.TimedOut
                    ? Loc.T("relatório de bateria: tempo esgotado", "battery report: timed out")
                    : Loc.T("relatório de bateria não gerado (pode exigir administrador)", "battery report not generated (may require administrator)");
                return null;
            }
            var cap = Parse(File.ReadAllText(file));
            if (cap.DesignMWh is null && cap.FullChargeMWh is null)
            {
                error = Loc.T("relatório de bateria sem capacidades legíveis", "battery report has no readable capacities");
                return null;
            }
            if (cap.Partial) error = Loc.T("relatório de bateria parcial: alguma bateria sem carga completa legível foi ignorada", "partial battery report: a battery without a readable full charge was ignored");
            return cap;
        }
        catch (Exception ex)
        {
            error = Loc.T("relatório de bateria: ", "battery report: ") + ex.Message.Trim();
            return null;
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); } catch (Exception) { /* temporário */ }
        }
    }
}
