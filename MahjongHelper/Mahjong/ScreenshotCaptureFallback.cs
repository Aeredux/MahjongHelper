using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace MahjongHelper.Mahjong;

/// <summary>
/// KAN-55 non-game PNG writer. Prefers Dalamud viewport/backbuffer capture
/// (game scene, not ImGui-only), then GDI PrintWindow/BitBlt.
/// </summary>
public static class ScreenshotCaptureFallback
{
    public const string MethodName = ScreenshotCapturePaths.MethodName;
    public const string ViewportMethod = "CaptureFallback/DalamudViewport";

    /// <summary>WIC <c>GUID_ContainerFormatPng</c>.</summary>
    public static readonly Guid WicPngContainer = new("1B7CFAF4-713F-473C-BBCD-6137425FAEAF");

    private const int DxgiRgba32 = 28;       // DXGI_FORMAT_R8G8B8A8_UNORM
    private const int DxgiRgba32Srgb = 29;   // DXGI_FORMAT_R8G8B8A8_UNORM_SRGB
    private const int DxgiBgra32 = 87;       // DXGI_FORMAT_B8G8R8A8_UNORM
    private const int DxgiBgra32Srgb = 91;   // DXGI_FORMAT_B8G8R8A8_UNORM_SRGB

    public readonly record struct Result(
        bool Wrote,
        string Method,
        string? PrimaryPath,
        string? ExtraCopyPath,
        string Detail);

    public static async Task<Result> TryCaptureAsync(
        ITextureProvider? textures,
        ITextureReadbackProvider? readback,
        uint viewportId,
        string? configuredScreenShotDir,
        DateTime utcNow)
    {
        ScreenshotCapturePaths.EnsureAppDataDirectory();
        var primary = ScreenshotCapturePaths.PrimaryPath(utcNow);

        var viewport = await TryDalamudViewportAsync(textures, readback, viewportId, primary).ConfigureAwait(false);
        if (viewport.Wrote)
            return WithConfiguredCopy(viewport, configuredScreenShotDir);

        var gdi = TryGdi(primary);
        if (gdi.Wrote)
        {
            var detail = viewport.Detail.Length == 0
                ? gdi.Detail
                : $"{gdi.Detail}; viewport skipped: {viewport.Detail}";
            return WithConfiguredCopy(gdi with { Detail = detail }, configuredScreenShotDir);
        }

        var fail = string.IsNullOrEmpty(viewport.Detail)
            ? gdi.Detail
            : $"viewport={viewport.Detail}; gdi={gdi.Detail}";
        return new Result(false, MethodName, null, null, fail);
    }

    private static Result WithConfiguredCopy(Result result, string? configuredDir)
    {
        if (!result.Wrote || result.PrimaryPath == null)
            return result;
        var copy = ScreenshotCapturePaths.TryCopyToConfiguredDir(result.PrimaryPath, configuredDir);
        var detail = copy == null
            ? result.Detail
            : $"{result.Detail}; also copied to {copy}";
        return result with { ExtraCopyPath = copy, Detail = detail };
    }

    private static async Task<Result> TryDalamudViewportAsync(
        ITextureProvider? textures,
        ITextureReadbackProvider? readback,
        uint viewportId,
        string primaryPath)
    {
        if (textures == null)
            return Fail(ViewportMethod, "ITextureProvider unavailable");
        if (readback == null)
            return Fail(ViewportMethod, "ITextureReadbackProvider unavailable");
        if (viewportId == 0)
            return Fail(ViewportMethod, "ImGui main viewport id is 0");

        IDalamudTextureWrap? wrap = null;
        try
        {
            // TakeBeforeImGuiRender: game scene + native UI (EmjL / overlay), not ImGui-only.
            wrap = await textures.CreateFromImGuiViewportAsync(
                new ImGuiViewportTextureArgs
                {
                    ViewportId = viewportId,
                    TakeBeforeImGuiRender = true,
                    KeepTransparency = false,
                },
                debugName: "MahjongHelper.CaptureFallback").ConfigureAwait(false);

            if (wrap == null)
                return Fail(ViewportMethod, "CreateFromImGuiViewportAsync returned null");

            var wicDetail = await TrySaveViaWicAsync(readback, wrap, primaryPath).ConfigureAwait(false);
            if (wicDetail != null)
                return Saved(ViewportMethod, primaryPath, wicDetail);

            if (await TrySaveViaRawAsync(readback, wrap, primaryPath).ConfigureAwait(false) is { } rawDetail)
                return Saved(ViewportMethod, primaryPath, rawDetail);

            return Fail(ViewportMethod, "WIC/raw save failed");
        }
        catch (Exception ex)
        {
            return Fail(ViewportMethod, ex.Message);
        }
        finally
        {
            try { wrap?.Dispose(); } catch { }
        }
    }

    private static async Task<string?> TrySaveViaWicAsync(
        ITextureReadbackProvider readback,
        IDalamudTextureWrap wrap,
        string path)
    {
        try
        {
            var guid = TryFindPngGuid(readback) ?? WicPngContainer;
            await readback.SaveToFileAsync(wrap, guid, path, leaveWrapOpen: true).ConfigureAwait(false);
            return IsWrittenPng(path)
                ? $"WIC PNG {new FileInfo(path).Length} bytes"
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TrySaveViaRawAsync(
        ITextureReadbackProvider readback,
        IDalamudTextureWrap wrap,
        string path)
    {
        try
        {
            var (spec, raw) = await readback.GetRawImageAsync(wrap, leaveWrapOpen: true).ConfigureAwait(false);
            if (raw == null || raw.Length == 0 || spec.Width <= 0 || spec.Height <= 0)
                return null;

            var stride = spec.Pitch > 0 ? spec.Pitch : spec.Width * 4;
            switch (spec.DxgiFormat)
            {
                case DxgiBgra32:
                case DxgiBgra32Srgb:
                    PngRgbaWriter.WriteBgra32(path, spec.Width, spec.Height, raw, stride);
                    break;
                case DxgiRgba32:
                case DxgiRgba32Srgb:
                    File.WriteAllBytes(path, PngRgbaWriter.EncodeRgba32(spec.Width, spec.Height, raw, stride));
                    break;
                default:
                    return null;
            }

            return IsWrittenPng(path)
                ? $"GetRawImageAsync DXGI={spec.DxgiFormat} {spec.Width}x{spec.Height}"
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static Guid? TryFindPngGuid(ITextureReadbackProvider readback)
    {
        try
        {
            foreach (var info in readback.GetSupportedImageEncoderInfos())
            {
                if (info.Extensions.Any(ext =>
                        ext.Contains("png", StringComparison.OrdinalIgnoreCase)))
                    return info.ContainerGuid;
                if (info.MimeTypes.Any(m =>
                        m.Contains("png", StringComparison.OrdinalIgnoreCase)))
                    return info.ContainerGuid;
            }
        }
        catch
        {
            // use WIC GUID
        }

        return null;
    }

    private static Result TryGdi(string primaryPath)
    {
        try
        {
            var shot = WindowCaptureGdi.TryCapture();
            if (!shot.Ok)
                return Fail(shot.Method, shot.Detail);

            PngRgbaWriter.WriteBgra32(primaryPath, shot.Width, shot.Height, shot.Bgra, shot.Stride);
            return IsWrittenPng(primaryPath)
                ? Saved(shot.Method, primaryPath, shot.Detail)
                : Fail(shot.Method, $"{shot.Detail}; PNG write missing");
        }
        catch (Exception ex)
        {
            return Fail(WindowCaptureGdi.PrintWindowMethod, ex.Message);
        }
    }

    private static bool IsWrittenPng(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 64;
        }
        catch
        {
            return false;
        }
    }

    private static Result Saved(string method, string path, string detail)
        => new(true, method, path, null, detail);

    private static Result Fail(string method, string detail)
        => new(false, method, null, null, detail);
}
