using System.Text;
using MahjongHelper.Mahjong;
using Xunit;

namespace MahjongHelper.Tests;

public class FfxivCfgScreenshotDirTests
{
    [Fact]
    public void Parse_tab_separated_custom_dir()
    {
        var text = "ScreenShotImageType\t3\nScreenShotDir\tC:\\Users\\alvin\\Documents\\FF14Modding\\Screenshots\n";
        Assert.Equal(
            @"C:\Users\alvin\Documents\FF14Modding\Screenshots",
            FfxivCfgScreenshotDir.ParseScreenShotDir(text));
    }

    [Fact]
    public void Parse_empty_value_is_unset()
    {
        Assert.Null(FfxivCfgScreenshotDir.ParseScreenShotDir("ScreenShotDir\t\n"));
        Assert.Null(FfxivCfgScreenshotDir.ParseScreenShotDir("ScreenShotDir\n"));
        Assert.Null(FfxivCfgScreenshotDir.ParseScreenShotDir(""));
        Assert.Null(FfxivCfgScreenshotDir.ParseScreenShotDir(null));
    }

    [Fact]
    public void Parse_ignores_similar_keys()
    {
        Assert.Null(FfxivCfgScreenshotDir.ParseScreenShotDir("ScreenShotDirExtra\tC:\\nope\n"));
        Assert.Null(FfxivCfgScreenshotDir.ParseScreenShotDir("MyScreenShotDir\tC:\\nope\n"));
    }

    [Fact]
    public void Parse_quoted_and_spaced_values()
    {
        Assert.Equal(
            @"D:\shots",
            FfxivCfgScreenshotDir.ParseScreenShotDir("ScreenShotDir = \"D:\\shots\"\n"));
        Assert.Equal(
            @"D:\shots",
            FfxivCfgScreenshotDir.ParseScreenShotDir("ScreenShotDir   D:\\shots  \r\n"));
    }

    [Fact]
    public void Parse_bom_and_windows_newlines()
    {
        var text = "\uFEFFGuid\tabc\r\nScreenShotDir\tE:\\FF14\\Screenshots\r\nOther\t1\r\n";
        Assert.Equal(@"E:\FF14\Screenshots", FfxivCfgScreenshotDir.ParseScreenShotDir(text));
    }

    [Fact]
    public void Candidates_put_configured_dir_first()
    {
        var configured = @"C:\Users\alvin\Documents\FF14Modding\Screenshots";
        var documents = @"C:\Users\alvin\OneDrive\Documents";
        var profile = @"C:\Users\alvin";
        var dirs = FfxivCfgScreenshotDir.EnumerateScreenshotDirectoryCandidates(configured, documents, profile).ToList();
        Assert.Equal(configured, dirs[0]);
        Assert.Contains(
            Path.Combine(documents, "My Games", FfxivCfgScreenshotDir.RealmRebornFolder, "screenshots"),
            dirs);
        Assert.Contains(
            Path.Combine(profile, "Documents", "My Games", FfxivCfgScreenshotDir.RealmRebornFolder, "screenshots"),
            dirs);
    }

    [Fact]
    public void Cfg_candidates_cover_onedrive_and_documents()
    {
        var documents = Path.Combine("docroot");
        var profile = Path.Combine("profile");
        var cfgs = FfxivCfgScreenshotDir.EnumerateCfgFileCandidates(documents, profile).ToList();
        Assert.Contains(
            Path.Combine(documents, "My Games", FfxivCfgScreenshotDir.RealmRebornFolder, FfxivCfgScreenshotDir.CfgFileName),
            cfgs);
        Assert.Contains(
            Path.Combine(profile, "Documents", "My Games", FfxivCfgScreenshotDir.RealmRebornFolder, FfxivCfgScreenshotDir.CfgFileName),
            cfgs);
        Assert.Contains(
            Path.Combine(profile, "OneDrive", "Documents", "My Games", FfxivCfgScreenshotDir.RealmRebornFolder, FfxivCfgScreenshotDir.CfgFileName),
            cfgs);
    }

    [Fact]
    public void TryReadConfiguredDirectory_prefers_cfg_with_custom_dir()
    {
        var root = Path.Combine(Path.GetTempPath(), "mh-cfg-" + Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "Documents");
        var profile = Path.Combine(root, "profile");
        var emptyCfgDir = Path.Combine(documents, "My Games", FfxivCfgScreenshotDir.RealmRebornFolder);
        var customCfgDir = Path.Combine(profile, "Documents", "My Games", FfxivCfgScreenshotDir.RealmRebornFolder);
        Directory.CreateDirectory(emptyCfgDir);
        Directory.CreateDirectory(customCfgDir);
        File.WriteAllText(Path.Combine(emptyCfgDir, FfxivCfgScreenshotDir.CfgFileName), "ScreenShotDir\t\n", Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(customCfgDir, FfxivCfgScreenshotDir.CfgFileName),
            "ScreenShotDir\tC:\\Users\\alvin\\Documents\\FF14Modding\\Screenshots\n",
            Encoding.UTF8);

        try
        {
            string? found = null;
            foreach (var cfg in FfxivCfgScreenshotDir.EnumerateCfgFileCandidates(documents, profile))
            {
                if (!File.Exists(cfg))
                    continue;
                found = FfxivCfgScreenshotDir.ParseScreenShotDir(File.ReadAllText(cfg));
                if (found != null)
                    break;
            }

            Assert.Equal(@"C:\Users\alvin\Documents\FF14Modding\Screenshots", found);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
