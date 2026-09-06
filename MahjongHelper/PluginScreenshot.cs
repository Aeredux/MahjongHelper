using System;
using System.Threading.Tasks;
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

    private void StartGameScreenshot()
    {
        if (_screenshotInFlight)
        {
            NotifyScreenshot("/mj screenshot already in progress — wait for Result/Location (or stuck) before retrying");
            return;
        }

        _screenshotInFlight = true;
        _ = FireGameScreenshotAsync();
    }

    private async Task FireGameScreenshotAsync()
    {
        try
        {
            await FireGameScreenshotCoreAsync().ConfigureAwait(false);
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

    private async Task FireGameScreenshotCoreAsync()
    {
        var requestedAtUtc = DateTime.UtcNow;
        var folder = GameScreenshot.ResolveScreenshotsDirectory();
        var configured = FfxivCfgScreenshotDir.TryReadConfiguredDirectory();
        var watchNote = configured != null
            ? $"watching cfg ScreenShotDir={configured} (plus fallbacks; resolved={folder})"
            : $"watching resolved={folder} (cfg ScreenShotDir unset; scanning all candidates)";

        var forceClearUsed = false;
        GameScreenshot.ScheduleAttempt attempt = default;
        GameScreenshot.ApiSnapshot snap;

        try
        {
            snap = GameScreenshot.CaptureApiSnapshot();
            NotifyScreenshot(
                $"/mj screenshot pre-schedule: CanTake={snap.CanTake} Requested={snap.Requested} " +
                $"Result={snap.Result} Location={snap.Location ?? "(none)"} — {watchNote}");
        }
        catch (Exception ex)
        {
            snap = new GameScreenshot.ApiSnapshot(false, false, false, "n/a", null, 0);
            NotifyScreenshot($"/mj screenshot pre-schedule failed: {ex.Message} — {watchNote}");
        }

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
                    "/mj screenshot game API stuck: ScreenShotRequested still true. " +
                    "Not injecting PrintScreen while a request is pending (relog also clears the bit).");
                return;
            }

            if (attempt.Scheduled)
            {
                NotifyScreenshot(
                    $"/mj screenshot scheduled via game API ({attempt.Detail}). " +
                    "Waiting for ScreenShotRequested to clear — not injecting a key on this request.");
                var done = await WaitOnFrameworkAsync(
                    () => !GameScreenshot.CaptureApiSnapshot().Requested,
                    GameScreenshot.CompletionWaitMs).ConfigureAwait(false);
                var after = SafeCapture();
                var newest = GameScreenshot.TryFindNewestScreenshot(requestedAtUtc.AddSeconds(-2));

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
                        "Not injecting PrintScreen while a request is pending.");
                    return;
                }

                if (ScreenshotStuckRecovery.IsPhantomSuccess(after.Result, after.Location))
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
                        "/mj screenshot game API Result=Success but file still missing after retry. Not injecting PrintScreen.");
                    return;
                }

                var loc = after.Location ?? "(none)";
                var file = newest ?? "(none)";
                if (string.Equals(after.Result, "Success", StringComparison.OrdinalIgnoreCase) || newest != null)
                    NotifyScreenshot($"/mj screenshot game API Result={after.Result} Location={loc} file={file}.");
                else
                    NotifyScreenshot($"/mj screenshot game API finished but not success: Result={after.Result} Location={loc} file={file}.");
                return;
            }

            // Requested is false and schedule failed — keys are allowed.
            break;
        }

        snap = SafeCapture();
        if (snap.Requested)
        {
            NotifyScreenshot(
                $"/mj screenshot game API still Requested=true after recovery. " +
                "Not injecting PrintScreen while a request is pending.");
            return;
        }

        if (!attempt.CanTake)
        {
            NotifyScreenshot(
                $"/mj screenshot game API did not schedule: CanTakeScreenShot=false ({attempt.Detail}). Falling back to key inject.");
        }
        else
        {
            NotifyScreenshot(
                $"/mj screenshot game API did not schedule ({attempt.Detail}). Falling back to key inject.");
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
        NotifyScreenshot(
            keyFile != null
                ? $"/mj screenshot wrote {keyFile}"
                : $"/mj screenshot: no new file detected after {GameScreenshot.CompletionWaitMs}ms — scanned all candidates (resolved={folder}).");
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
