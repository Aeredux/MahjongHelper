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

        GameScreenshot.ApiSnapshot before;
        GameScreenshot.ScheduleAttempt attempt;
        try
        {
            before = GameScreenshot.CaptureApiSnapshot();
            NotifyScreenshot(
                $"/mj screenshot pre-schedule: CanTake={before.CanTake} Requested={before.Requested} " +
                $"Result={before.Result} Location={before.Location ?? "(none)"} — {watchNote}");
            attempt = GameScreenshot.TryScheduleOnce();
        }
        catch (Exception ex)
        {
            before = new GameScreenshot.ApiSnapshot(false, false, false, "n/a", null, 0);
            attempt = new GameScreenshot.ScheduleAttempt(false, false, false, "n/a", ex.Message);
        }

        if (!attempt.Scheduled && before.Requested)
        {
            NotifyScreenshot(
                $"/mj screenshot game API did not schedule ({attempt.Detail}). " +
                $"Waiting up to {GameScreenshot.StuckRequestWaitMs}ms for ScreenShotRequested to clear, then retrying once.");
            var cleared = await WaitOnFrameworkAsync(
                () => !GameScreenshot.CaptureApiSnapshot().Requested,
                GameScreenshot.StuckRequestWaitMs).ConfigureAwait(false);

            if (!cleared)
            {
                var stuck = SafeCapture();
                NotifyScreenshot(
                    $"/mj screenshot game API stuck: ScreenShotRequested still true after {GameScreenshot.StuckRequestWaitMs}ms. " +
                    $"CanTake={stuck.CanTake} Result={stuck.Result} Location={stuck.Location ?? "(none)"}. " +
                    "Not injecting PrintScreen while a request is pending.");
                return;
            }

            try
            {
                attempt = GameScreenshot.TryScheduleOnce();
            }
            catch (Exception ex)
            {
                attempt = new GameScreenshot.ScheduleAttempt(false, false, false, "n/a", ex.Message);
            }
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
            var loc = after.Location ?? "(none)";
            var file = newest ?? "(none)";

            if (!done)
            {
                NotifyScreenshot(
                    $"/mj screenshot game API timed out: ScreenShotRequested still true after {GameScreenshot.CompletionWaitMs}ms. " +
                    $"Result={after.Result} Location={loc} file={file}.");
                return;
            }

            if (string.Equals(after.Result, "Success", StringComparison.OrdinalIgnoreCase) || newest != null)
            {
                NotifyScreenshot(
                    $"/mj screenshot game API Result={after.Result} Location={loc} file={file}.");
            }
            else
            {
                NotifyScreenshot(
                    $"/mj screenshot game API finished but not success: Result={after.Result} Location={loc} file={file}.");
            }

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
