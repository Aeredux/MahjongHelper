using System;
using System.IO;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using MahjongHelper.Mahjong;

namespace MahjongHelper;

public sealed partial class Plugin
{
    private void PrintScreenshotStatus()
    {
        try
        {
            var msg = $"/mj screenshot status: {GameScreenshot.FormatStatus()}";
            Log.Information(msg);
            LogToFile("autoplay.log", $"[SCREENSHOT] {msg}");
            try { ChatGui.Print(msg); } catch { }
            AppendRecentTransition($"{DateTime.UtcNow:O} {msg}");
        }
        catch (Exception ex)
        {
            var msg = $"/mj screenshot status failed: {ex.Message}";
            Log.Warning(ex, msg);
            try { ChatGui.Print(msg); } catch { }
        }
    }

    private void QueueScreenshot(bool preferGameApi)
    {
        if (_screenshotInFlight)
        {
            NotifyScreenshot("/mj screenshot already in progress — wait before retrying");
            return;
        }

        _screenshotPreferGameApi = preferGameApi;
        _screenshotRequested = true;
        Log.Information(
            preferGameApi
                ? "/mj screenshot game queued — ScheduleScreenShot path on the next framework tick"
                : "/mj screenshot queued — CaptureFallback on the next framework tick");
    }

    private void StartGameScreenshot()
    {
        if (_screenshotInFlight)
        {
            NotifyScreenshot("/mj screenshot already in progress — wait before retrying");
            return;
        }

        _screenshotInFlight = true;
        var preferGameApi = _screenshotPreferGameApi;
        _screenshotPreferGameApi = false;
        _ = FireGameScreenshotAsync(preferGameApi);
    }

    private async Task FireGameScreenshotAsync(bool preferGameApi)
    {
        try
        {
            await FireGameScreenshotCoreAsync(preferGameApi).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "/mj screenshot failed");
            NotifyScreenshot($"/mj screenshot failed: {ex.Message}");
        }
        finally
        {
            _screenshotInFlight = false;
        }
    }

    private async Task FireGameScreenshotCoreAsync(bool preferGameApi)
    {
        var requestedAtUtc = DateTime.UtcNow;
        var folder = GameScreenshot.ResolveScreenshotsDirectory();
        var configured = FfxivCfgScreenshotDir.TryReadConfiguredDirectory();

        if (preferGameApi)
        {
            var watchNote = configured != null
                ? $"watching cfg ScreenShotDir={configured} (plus fallbacks; resolved={folder})"
                : $"watching resolved={folder} (cfg ScreenShotDir unset; scanning all candidates)";

            try
            {
                var snap = GameScreenshot.CaptureApiSnapshot();
                NotifyScreenshot(
                    $"/mj screenshot game pre-schedule: CanTake={snap.CanTake} Requested={snap.Requested} " +
                    $"Result={snap.Result} Location={snap.Location ?? "(none)"} — {watchNote}");
            }
            catch (Exception ex)
            {
                NotifyScreenshot($"/mj screenshot game pre-schedule failed: {ex.Message} — {watchNote}");
            }

            var gameFile = await TryGameApiForRealFileAsync(requestedAtUtc).ConfigureAwait(false);
            if (gameFile != null)
            {
                GameScreenshot.RememberLastCapture(GameScreenshot.TriggerMethod.GameApi.ToString(), gameFile);
                NotifyScreenshot($"/mj screenshot wrote {gameFile} via GameApi.");
                return;
            }

            if (SafeCapture().Requested)
                await ForceClearOnFrameworkAsync().ConfigureAwait(false);

            NotifyScreenshot(
                "/mj screenshot game API did not produce a file. Running CaptureFallback.");
        }

        var fallback = await RunCaptureFallbackAsync(configured, requestedAtUtc).ConfigureAwait(false);
        if (fallback.Wrote)
        {
            GameScreenshot.RememberLastCapture(ScreenshotCaptureFallback.MethodName, fallback.PrimaryPath);
            var extra = fallback.ExtraCopyPath == null ? string.Empty : $" extraCopy={fallback.ExtraCopyPath}";
            NotifyScreenshot(
                $"/mj screenshot wrote {fallback.PrimaryPath} via {fallback.Method} " +
                $"({ScreenshotCaptureFallback.MethodName}). {fallback.Detail}{extra}");
            return;
        }

        GameScreenshot.RememberLastCapture(ScreenshotCaptureFallback.MethodName, null);
        NotifyScreenshot($"/mj screenshot CaptureFallback failed ({fallback.Detail}).");

        if (!preferGameApi)
            return;

        NotifyScreenshot(
            "Trying BoundKey / VK_SNAPSHOT last (also dead when the game writer is broken).");
        await TryKeyInjectAfterFallbackAsync(requestedAtUtc, folder).ConfigureAwait(false);
    }

    /// <summary>
    /// Existing ScheduleScreenShot wait + stuck recovery + Location verify.
    /// Returns a real on-disk path, or null so the caller can CaptureFallback.
    /// </summary>
    private async Task<string?> TryGameApiForRealFileAsync(DateTime requestedAtUtc)
    {
        var forceClearUsed = false;
        GameScreenshot.ScheduleAttempt attempt = default;

        // At most: natural wait retry + one force-clear retry.
        for (var pass = 0; pass < 3; pass++)
        {
            try
            {
                attempt = GameScreenshot.TryScheduleOnce();
            }
            catch (Exception ex)
            {
                attempt = new GameScreenshot.ScheduleAttempt(false, false, false, "n/a", ex.Message);
            }

            if (!attempt.Scheduled && (attempt.RequestedBefore || SafeCapture().Requested))
            {
                NotifyScreenshot(
                    $"/mj screenshot game API did not schedule ({attempt.Detail}). " +
                    $"Waiting up to {GameScreenshot.StuckRequestWaitMs}ms for ScreenShotRequested to clear.");
                var cleared = await WaitOnFrameworkAsync(
                    () => !GameScreenshot.CaptureApiSnapshot().Requested,
                    GameScreenshot.StuckRequestWaitMs).ConfigureAwait(false);
                if (cleared)
                    continue;

                if (await TryForceClearIfStuckAsync(requestedAtUtc, forceClearUsed).ConfigureAwait(false) is true)
                {
                    forceClearUsed = true;
                    continue;
                }

                NotifyScreenshot(
                    "/mj screenshot game API stuck: ScreenShotRequested still true after wait. " +
                    "Force-clearing if needed, then CaptureFallback.");
                return null;
            }

            if (attempt.Scheduled)
            {
                NotifyScreenshot(
                    $"/mj screenshot scheduled via game API ({attempt.Detail}). " +
                    "Waiting for ScreenShotRequested to clear.");
                var done = await WaitOnFrameworkAsync(
                    () => !GameScreenshot.CaptureApiSnapshot().Requested,
                    GameScreenshot.CompletionWaitMs).ConfigureAwait(false);
                var after = SafeCapture();
                var newest = GameScreenshot.TryFindNewestScreenshot(requestedAtUtc.AddSeconds(-2));
                var verified = VerifiedGameFile(after, newest);

                if (!done && after.Requested)
                {
                    if (await TryForceClearIfStuckAsync(requestedAtUtc, forceClearUsed).ConfigureAwait(false) is true)
                    {
                        forceClearUsed = true;
                        continue;
                    }

                    NotifyScreenshot(
                        $"/mj screenshot game API timed out: ScreenShotRequested still true after {GameScreenshot.CompletionWaitMs}ms. " +
                        $"Result={after.Result} Location={after.Location ?? "(none)"} file={newest ?? "(none)"}. " +
                        "Proceeding to CaptureFallback.");
                    return null;
                }

                if (ScreenshotStuckRecovery.IsPhantomSuccess(after.Result, after.Location) && verified == null)
                {
                    NotifyScreenshot(
                        $"/mj screenshot {ScreenshotStuckRecovery.FormatPhantomSuccess(after.Location)}.");
                    await ForceClearOnFrameworkAsync().ConfigureAwait(false);
                    if (!forceClearUsed)
                    {
                        forceClearUsed = true;
                        NotifyScreenshot("/mj screenshot retrying ScheduleScreenShot once after phantom Success.");
                        continue;
                    }

                    NotifyScreenshot(
                        "/mj screenshot game API Result=Success but file still missing after retry. CaptureFallback next.");
                    return null;
                }

                if (verified != null)
                {
                    NotifyScreenshot(
                        $"/mj screenshot game API Result={after.Result} Location={after.Location ?? "(none)"} file={verified}.");
                    return verified;
                }

                NotifyScreenshot(
                    $"/mj screenshot game API finished but no file: Result={after.Result} " +
                    $"Location={after.Location ?? "(none)"} file={newest ?? "(none)"}. CaptureFallback next.");
                return null;
            }

            NotifyScreenshot(
                attempt.CanTake
                    ? $"/mj screenshot game API did not schedule ({attempt.Detail}). CaptureFallback next."
                    : $"/mj screenshot game API did not schedule: CanTakeScreenShot=false ({attempt.Detail}). CaptureFallback next.");
            return null;
        }

        return VerifiedGameFile(SafeCapture(), GameScreenshot.TryFindNewestScreenshot(requestedAtUtc.AddSeconds(-2)));
    }

    private static string? VerifiedGameFile(GameScreenshot.ApiSnapshot after, string? newest)
    {
        if (newest != null && FileExistsSafe(newest))
            return newest;
        if (after.Location != null
            && string.Equals(after.Result, "Success", StringComparison.OrdinalIgnoreCase)
            && !ScreenshotStuckRecovery.IsLocationMissingOnDisk(after.Location))
        {
            return ScreenshotStuckRecovery.NormalizeGamePath(after.Location);
        }

        return null;
    }

    private static bool FileExistsSafe(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    private async Task<ScreenshotCaptureFallback.Result> RunCaptureFallbackAsync(
        string? configured,
        DateTime utcNow)
    {
        var viewportId = 0u;
        try
        {
            await Framework.RunOnFrameworkThread(() =>
            {
                try { viewportId = ImGui.GetMainViewport().ID; }
                catch { viewportId = 0; }
            }).ConfigureAwait(false);
        }
        catch
        {
            viewportId = 0;
        }

        try
        {
            return await ScreenshotCaptureFallback.TryCaptureAsync(
                TextureProvider,
                TextureReadback,
                viewportId,
                configured,
                utcNow).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ScreenshotCaptureFallback.Result(
                false,
                ScreenshotCaptureFallback.MethodName,
                null,
                null,
                ex.Message);
        }
    }

    private async Task TryKeyInjectAfterFallbackAsync(DateTime requestedAtUtc, string folder)
    {
        if (SafeCapture().Requested)
            await ForceClearOnFrameworkAsync().ConfigureAwait(false);

        if (SafeCapture().Requested)
        {
            NotifyScreenshot(
                "/mj screenshot skipping key inject: ScreenShotRequested still true after force-clear.");
            return;
        }

        GameScreenshot.TriggerResult keys;
        try
        {
            keys = GameScreenshot.TryTriggerKeys();
        }
        catch (Exception ex)
        {
            keys = new GameScreenshot.TriggerResult(false, GameScreenshot.TriggerMethod.None, ex.Message);
        }

        NotifyScreenshot(FormatKeyInjectMessage(keys, folder));
        await Task.Delay(GameScreenshot.CompletionWaitMs).ConfigureAwait(false);
        var keyFile = GameScreenshot.TryFindNewestScreenshot(requestedAtUtc.AddSeconds(-2));
        if (keyFile != null)
        {
            GameScreenshot.RememberLastCapture(keys.Method.ToString(), keyFile);
            NotifyScreenshot($"/mj screenshot wrote {keyFile} via {keys.Method}.");
        }
        else
        {
            NotifyScreenshot(
                $"/mj screenshot: no new file after CaptureFallback and key inject — scanned all candidates (resolved={folder}).");
        }
    }

    /// <returns>true if the stuck bit was force-cleared and the caller should retry schedule.</returns>
    private async Task<bool> TryForceClearIfStuckAsync(DateTime requestedAtUtc, bool forceClearUsed)
    {
        if (forceClearUsed)
            return false;

        var stuck = SafeCapture();
        var newest = GameScreenshot.TryFindNewestScreenshot(requestedAtUtc.AddSeconds(-2));
        if (!ScreenshotStuckRecovery.ShouldForceClearStuckRequest(
                stuck.Requested,
                stuck.Location,
                stuck.Timestamp,
                newest != null,
                DateTime.UtcNow))
        {
            return false;
        }

        if (ScreenshotStuckRecovery.IsPhantomSuccess(stuck.Result, stuck.Location))
            NotifyScreenshot($"/mj screenshot {ScreenshotStuckRecovery.FormatPhantomSuccess(stuck.Location)}.");

        return await ForceClearOnFrameworkAsync().ConfigureAwait(false);
    }

    private async Task<bool> ForceClearOnFrameworkAsync()
    {
        var cleared = false;
        var detail = "force-clear not run";
        await Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                cleared = GameScreenshot.TryForceClearRequested(out detail);
            }
            catch (Exception ex)
            {
                cleared = false;
                detail = ex.Message;
            }
        }).ConfigureAwait(false);

        NotifyScreenshot($"/mj screenshot {detail}");
        return cleared;
    }

    private static string FormatKeyInjectMessage(GameScreenshot.TriggerResult keys, string folder)
    {
        var rebind = GameScreenshot.UsesPrintScreenBind(keys)
            ? $" Key injected; {GameScreenshot.PrintScreenRebindHint}."
            : string.Empty;

        if (!keys.Fired)
            return $"/mj screenshot key inject failed ({keys.Detail}).{rebind} Folder {folder}.";

        if (keys.Method == GameScreenshot.TriggerMethod.BoundKey)
        {
            return $"/mj screenshot bound KEY_SCREENSHOT ({keys.Detail}) injected — not confirmed written.{rebind} " +
                   $"PNG should land in {folder}.";
        }

        return $"/mj screenshot VK_SNAPSHOT / PrintScreen fallback injected — not confirmed written.{rebind} " +
               $"PNG should land in {folder}.";
    }

    private GameScreenshot.ApiSnapshot SafeCapture()
    {
        try
        {
            return GameScreenshot.CaptureApiSnapshot();
        }
        catch
        {
            return new GameScreenshot.ApiSnapshot(false, false, false, "n/a", null, 0);
        }
    }

    private async Task<bool> WaitOnFrameworkAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(GameScreenshot.PollIntervalMs).ConfigureAwait(false);
            var hit = false;
            await Framework.RunOnFrameworkThread(() =>
            {
                try { hit = condition(); }
                catch { hit = false; }
            }).ConfigureAwait(false);
            if (hit)
                return true;
        }

        return false;
    }

    private void NotifyScreenshot(string msg)
    {
        Log.Information(msg);
        LogToFile("autoplay.log", $"[SCREENSHOT] {msg}");
        try { ChatGui.Print(msg); } catch { }
        AppendRecentTransition($"{DateTime.UtcNow:O} {msg}");
    }
}
