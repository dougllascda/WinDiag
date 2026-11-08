using WinDiag.Core.Util;

namespace WinDiag.Tests;

public class StorageTests
{
    private static void Put(byte[] b, int i, byte id, byte value, byte worst, long raw)
    {
        b[i] = id; b[i + 3] = value; b[i + 4] = worst;
        for (int k = 0; k < 6; k++) b[i + 5 + k] = (byte)((raw >> (8 * k)) & 0xFF);
    }

    private static byte[] Sample()
    {
        var b = new byte[512];
        Put(b, 2, 5, 200, 200, 0);
        Put(b, 14, 9, 51, 51, 35795);
        Put(b, 26, 199, 200, 190, 1);
        return b;
    }

    [Fact]
    public void ParseExtraiAtributos5_9_199()
    {
        var a = Smart.Parse(Sample());
        Assert.Equal(3, a.Count);
        Assert.Equal(0, a[5].Raw);
        Assert.Equal(200, a[5].Value);
        Assert.Equal(35795, a[9].Raw);
        Assert.Equal(51, a[9].Worst);
        Assert.Equal(1, a[199].Raw);
        Assert.Equal(190, a[199].Worst);
    }

    [Fact]
    public void ParseRawDe48Bits()
    {
        var b = new byte[512];
        Put(b, 2, 177, 100, 100, 72908145);
        Assert.Equal(72908145, Smart.Parse(b)[177].Raw);
    }

    [Fact]
    public void ParseNuloOuCurtoNaoLanca()
    {
        Assert.Empty(Smart.Parse(null));
        Assert.Empty(Smart.Parse(new byte[5]));
    }

    [Fact]
    public void Temperatura194UsaSoByteBaixo()
    {
        var b = new byte[512];
        Put(b, 2, 194, 107, 87, 0x0057_0000_0024);
        Assert.Equal(36, Smart.RawShown(Smart.Parse(b)[194]));
    }

    [Fact]
    public void NomesDeAtributosEBrutoEspecifico()
    {
        Assert.Equal("Desgaste (SSD)", Smart.NameOf(177));
        Assert.Equal("Vida restante (SSD)", Smart.NameOf(231));
        Assert.Equal("Média de wearout", Smart.NameOf(233));
        Assert.Equal("Total gravado (LBAs)", Smart.NameOf(241));
        Assert.Equal("outro", Smart.NameOf(250));
        Assert.Equal(VendorRaw, Smart.VendorSpecificRaw);
        Assert.Contains("específico do fabricante", Smart.VendorRawNote);
    }

    [Fact]
    public void HashDeSerialEhSha256Hex()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Smart.SerialHash(" abc "));
        Assert.Null(Smart.SerialHash("  "));
    }

    [Fact]
    public void CasaInstanciaComModelo()
    {
        Assert.True(Smart.InstanceMatches(@"SCSI\Disk&Ven_WDC&Prod_WD10EZEX-00BN5A0\4&1", "WDC WD10EZEX-00BN5A0"));
        Assert.False(Smart.InstanceMatches(@"SCSI\Disk&Ven_WDC&Prod_OUTRO\4&1", "WDC WD10EZEX-00BN5A0"));
    }

    [Fact]
    public void HistoricoRoundTripCompativelComV7()
    {
        var f = Path.Combine(Path.GetTempPath(), "wd_smart_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var h = new Dictionary<string, SmartHistoryEntry> { ["abc"] = new() { Date = "08/11/2026", A5 = 0, A197 = null, A198 = 0, A199 = 1 } };
            Assert.Null(Smart.SaveHistory(h, f));
            var r = Smart.LoadHistory(out var err, f);
            Assert.Null(err);
            Assert.Equal("08/11/2026", r["abc"].Date);
            Assert.Equal(1, r["abc"].A199);
            Assert.Null(r["abc"].A197);
        }
        finally { File.Delete(f); }
    }

    private static readonly int[] VendorRaw = { 177, 231, 233, 241 };
    private static readonly string[] SteamPaths = { @"C:\Program Files (x86)\Steam", @"D:\Games\SteamLibrary" };
    private static readonly string[] DedupInput = { "c:/program files (x86)/steam", @"C:\Program Files (x86)\Steam", @"D:\Games\SteamLibrary\" };

    private const string Vdf = """
        "libraryfolders"
        {
            "0"
            {
                "path"		"C:\\Program Files (x86)\\Steam"
                "label"		""
            }
            "1"
            {
                "path"		"D:\\Games\\SteamLibrary"
            }
        }
        """;

    [Fact]
    public void ParseVdfExtraiCaminhos()
    {
        var p = Steam.ParseVdf(Vdf.Split('\n'));
        Assert.Equal(SteamPaths, p);
    }

    [Fact]
    public void DedupNormalizaCaixaEBarras()
    {
        var r = Steam.Dedup(DedupInput);
        Assert.Equal(2, r.Count);
        Assert.Equal(@"C:\program files (x86)\steam", r[0]);
        Assert.Equal(@"D:\Games\SteamLibrary", r[1]);
    }
}
