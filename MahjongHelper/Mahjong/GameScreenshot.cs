using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.System.Photo;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace MahjongHelper.Mahjong;

/// <summary>
/// KAN-55: screenshot helpers. Default chat command uses CaptureFallback
/// (Dalamud viewport / GDI). <c>/mj screenshot game</c> still calls
/// <c>ScheduleScreenShot</c> with stuck recovery. Telesto is not required.
/// </summary>
public static class GameScreenshot
{
    public const ushort VkSnapshot = 0x2C;
    public const int StuckRequestWaitMs = 3000;
    public const int CompletionWaitMs = 5000;
    public const int StaleRequestSeconds = ScreenshotStuckRecovery.StaleRequestSeconds;
    public const int PollIntervalMs = 100;
    public const int ScreenShotLocationOffset = 0x78;
    public const int FileAccessPathLongStringOffset = 0x208;
    public const int FileAccessPathBufferChars = 260;

    public enum TriggerMethod
    {
        None,
        GameApi,
        BoundKey,
        PrintScreenFallback,
        CaptureFallback,
    }

    public static string? LastCaptureMethod { get; private set; }
    public static string? LastWrittenPath { get; private set; }

    public static void RememberLastCapture(string? method, string? path)
    {
        LastCaptureMethod = method;
        LastWrittenPath = path;
    }

    public readonly record struct TriggerResult(bool Fired, TriggerMethod Method, string Detail);

    public readonly record struct ApiSnapshot(
        bool InstanceAvailable,
        bool CanTake,
        bool Requested,
        string Result,
        string? Location,
        long Timestamp);

    public readonly record struct ScheduleAttempt(
        bool Scheduled,
        bool CanTake,
        bool RequestedBefore,
        string ResultBefore,
        string Detail);

    public static readonly string DefaultScreenshotsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "My Games",
        FfxivCfgScreenshotDir.RealmRebornFolder,
        "screenshots");

    public static ApiSnapshot CaptureApiSnapshot()
    {
        unsafe
        {
            var shot = ScreenShot.Instance();
            if (shot == null)
                return new ApiSnapshot(false, false, false, "n/a", null, 0);

            return new ApiSnapshot(
                true,
                shot->CanTakeScreenShot,
                shot->ScreenShotRequested,
                shot->ScreenShotResult.ToString(),
                TryReadLocation(shot),
                shot->ScreenShotTimestamp);
        }
    }

    public static ScheduleAttempt TryScheduleOnce()
    {
        unsafe
        {
            var shot = ScreenShot.Instance();
            if (shot == null)
                return new ScheduleAttempt(false, false, false, "n/a", "ScreenShot.Instance null");

            var canTake = shot->CanTakeScreenShot;
            var requested = shot->ScreenShotRequested;
            var resultBefore = shot->ScreenShotResult.ToString();

            if (requested)
            {
                return new ScheduleAttempt(
                    false,
                    canTake,
                    true,
                    resultBefore,
                    $"ScreenShotRequested=true (already pending) CanTake={canTake} priorResult={resultBefore}");
            }

            var ok = shot->ScheduleScreenShot(null, null);
            var detail = ok
                ? $"ScheduleScreenShot=true CanTake={canTake} RequestedNow={shot->ScreenShotRequested} priorResult={resultBefore}"
                : $"ScheduleScreenShot=false CanTake={canTake} RequestedNow={shot->ScreenShotRequested} priorResult={resultBefore}";
            return new ScheduleAttempt(ok, canTake, requested, resultBefore, detail);
        }
    }

    /// <summary>
    /// Unsafe write: clear a stuck <c>ScreenShotRequested</c> so
    /// <c>ScheduleScreenShot</c> can be retried. Framework-thread only.
    /// </summary>
    public static bool TryForceClearRequested(out string detail)
    {
        unsafe
        {
            var shot = ScreenShot.Instance();
            if (shot == null)
            {
                detail = "ScreenShot.Instance null — cannot force-clear ScreenShotRequested";
                return false;
            }

            var before = shot->ScreenShotRequested;
            var result = shot->ScreenShotResult.ToString();
            var location = TryReadLocation(shot) ?? "(none)";
            if (!before)
            {
                detail = $"ScreenShotRequested already false Result={result} Location={location}";
                return true;
            }

            shot->ScreenShotRequested = false;
            var after = shot->ScreenShotRequested;
            detail =
                $"FORCE-CLEARED ScreenShotRequested true→{after} Result={result} Location={location} " +
                "(stuck bit; relog also clears this)";
            return !after;
        }
    }

    /// <summary>
    /// Key-only cascade (bound KEY_SCREENSHOT, then VK_SNAPSHOT). Does not call
    /// <c>ScheduleScreenShot</c> — the caller must not mix this with a successful schedule.
    /// </summary>
    public static TriggerResult TryTriggerKeys()
    {
        string? bindDetail = null;
        try
        {
            if (TrySendBoundScreenshotKey(out var keyDetail))
                return new TriggerResult(true, TriggerMethod.BoundKey, keyDetail);
            bindDetail = keyDetail;
        }
        catch (Exception ex)
        {
            bindDetail = ex.Message;
        }

        try
        {
            if (TrySendVirtualKey(VkSnapshot, KeyModifierFlag.None, extended: true))
                return new TriggerResult(true, TriggerMethod.PrintScreenFallback, "VK_SNAPSHOT");
        }
        catch (Exception ex)
        {
            return new TriggerResult(false, TriggerMethod.None, $"all key methods failed; last={ex.Message}");
        }

        return new TriggerResult(
            false,
            TriggerMethod.None,
            string.IsNullOrEmpty(bindDetail) ? "SendInput returned 0" : bindDetail);
    }

    public static bool UsesPrintScreenBind(TriggerResult result)
    {
        if (result.Method == TriggerMethod.PrintScreenFallback)
            return true;
        return result.Detail.Contains("SNAPSHOT", StringComparison.OrdinalIgnoreCase);
    }

    public static string PrintScreenRebindHint
        => "if OS steals PrintScreen, rebind Screenshot in System Config (e.g. F12)";

    public static string ResolveScreenshotsDirectory()
    {
        var configured = FfxivCfgScreenshotDir.TryReadConfiguredDirectory();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                if (!Directory.Exists(configured))
                    Directory.CreateDirectory(configured);
                if (Directory.Exists(configured))
                    return configured;
            }
            catch
            {
                // Prefer the cfg path in messaging even if create failed; search still
                // walks every candidate.
            }
        }

        foreach (var candidate in EnumerateScreenshotDirectoryCandidates())
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        return configured ?? DefaultScreenshotsDirectory;
    }

    public static IEnumerable<string> EnumerateScreenshotDirectoryCandidates()
        => FfxivCfgScreenshotDir.EnumerateScreenshotDirectoryCandidates(
            FfxivCfgScreenshotDir.TryReadConfiguredDirectory(),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string FormatStatus()
    {
        var api = CaptureApiSnapshot();
        var configured = FfxivCfgScreenshotDir.TryReadConfiguredDirectory() ?? "(unset)";
        var cfgFiles = string.Join(", ", FfxivCfgScreenshotDir.EnumerateCfgFileCandidates().Where(File.Exists));
        if (string.IsNullOrEmpty(cfgFiles))
            cfgFiles = "(none found)";
        var resolved = ResolveScreenshotsDirectory();
        var dirs = string.Join(" | ", EnumerateScreenshotDirectoryCandidates());
        var instance = api.InstanceAvailable ? "yes" : "null";
        var loc = api.Location ?? "(none)";
        var locOnDisk = api.Location == null
            ? "n/a"
            : (ScreenshotStuckRecovery.IsLocationMissingOnDisk(api.Location) ? "missing" : "exists");
        var phantom = ScreenshotStuckRecovery.IsPhantomSuccess(api.Result, api.Location)
            ? $" {ScreenshotStuckRecovery.FormatPhantomSuccess(api.Location)}"
            : string.Empty;
        var lastMethod = LastCaptureMethod ?? "(none)";
        var lastPath = LastWrittenPath ?? "(none)";
        return
            $"Instance={instance} CanTake={api.CanTake} Requested={api.Requested} Result={api.Result} " +
            $"Location={loc} LocationOnDisk={locOnDisk} Timestamp={api.Timestamp} " +
            $"cfgScreenShotDir={configured} cfgFiles=[{cfgFiles}] resolved={resolved} candidates=[{dirs}] " +
            $"lastMethod={lastMethod} lastPath={lastPath}" +
            phantom;
    }

    /// <summary>
    /// Newest screenshot across every candidate directory, not just the first existing one.
    /// </summary>
    public static string? TryFindNewestScreenshot(DateTime notBeforeUtc)
        => TryFindNewestScreenshot(EnumerateScreenshotDirectoryCandidates(), notBeforeUtc);

    public static string? TryFindNewestScreenshot(string? directory, DateTime notBeforeUtc)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return TryFindNewestScreenshot(notBeforeUtc);
        return TryFindNewestScreenshot([directory], notBeforeUtc);
    }

    public static string? TryFindNewestScreenshot(IEnumerable<string> directories, DateTime notBeforeUtc)
    {
        FileInfo? best = null;
        foreach (var directory in directories.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory))
                continue;

            try
            {
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    if (!IsScreenshotFile(path))
                        continue;
                    var info = new FileInfo(path);
                    if (info.LastWriteTimeUtc < notBeforeUtc)
                        continue;
                    if (best == null
                        || info.LastWriteTimeUtc > best.LastWriteTimeUtc
                        || (info.LastWriteTimeUtc == best.LastWriteTimeUtc
                            && string.Compare(info.Name, best.Name, StringComparison.OrdinalIgnoreCase) > 0))
                    {
                        best = info;
                    }
                }
            }
            catch
            {
                // skip unreadable folders
            }
        }

        return best?.FullName;
    }

    private static bool IsScreenshotFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private static unsafe string? TryReadLocation(ScreenShot* shot)
    {
        if (shot == null)
            return null;

        try
        {
            // ScreenShotLocation is private FileAccessPath @ 0x78 in current ClientStructs.
            var basePtr = (byte*)shot + ScreenShotLocationOffset;
            var longPtr = *(char**)(basePtr + FileAccessPathLongStringOffset);
            if (longPtr != null)
            {
                var longText = new string(longPtr);
                if (!string.IsNullOrWhiteSpace(longText) && longText.Length < 1024)
                    return longText.Trim();
            }

            var chars = (char*)basePtr;
            var len = 0;
            while (len < FileAccessPathBufferChars && chars[len] != '\0')
                len++;
            if (len == 0)
                return null;
            var text = new string(chars, 0, len).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private static unsafe bool TrySendBoundScreenshotKey(out string detail)
    {
        detail = "UIInputData.Instance null";
        var input = UIInputData.Instance();
        if (input == null)
            return false;

        var bind = input->GetKeybind(InputId.KEY_SCREENSHOT);
        if (bind == null)
        {
            detail = "KEY_SCREENSHOT keybind null";
            return false;
        }

        foreach (var setting in bind->KeySettings)
        {
            if (setting.Key == SeVirtualKey.NO_KEY)
                continue;

            var vk = (ushort)setting.Key;
            var extended = vk == VkSnapshot;
            if (!TrySendVirtualKey(vk, setting.KeyModifier, extended))
            {
                detail = $"SendInput failed for {setting.Key}+{setting.KeyModifier}";
                continue;
            }

            detail = $"{setting.Key}+{setting.KeyModifier}";
            return true;
        }

        detail = "KEY_SCREENSHOT has no keyboard binding";
        return false;
    }

    private static bool TrySendVirtualKey(ushort vk, KeyModifierFlag modifiers, bool extended)
    {
        var inputs = new List<INPUT>(8);
        if (modifiers.HasFlag(KeyModifierFlag.Shift))
            inputs.Add(KeyEvent(VkShift, down: true, extended: false));
        if (modifiers.HasFlag(KeyModifierFlag.Ctrl))
            inputs.Add(KeyEvent(VkControl, down: true, extended: false));
        if (modifiers.HasFlag(KeyModifierFlag.Alt))
            inputs.Add(KeyEvent(VkMenu, down: true, extended: false));

        inputs.Add(KeyEvent(vk, down: true, extended));
        inputs.Add(KeyEvent(vk, down: false, extended));

        if (modifiers.HasFlag(KeyModifierFlag.Alt))
            inputs.Add(KeyEvent(VkMenu, down: false, extended: false));
        if (modifiers.HasFlag(KeyModifierFlag.Ctrl))
            inputs.Add(KeyEvent(VkControl, down: false, extended: false));
        if (modifiers.HasFlag(KeyModifierFlag.Shift))
            inputs.Add(KeyEvent(VkShift, down: false, extended: false));

        var arr = inputs.ToArray();
        var sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        if (sent == (uint)arr.Length)
            return true;

        // Last-resort path for PrintScreen if SendInput is blocked.
        if (vk == VkSnapshot && modifiers == KeyModifierFlag.None)
        {
            keybd_event((byte)VkSnapshot, 0, 0, UIntPtr.Zero);
            keybd_event((byte)VkSnapshot, 0, KeyeventfKeyup, UIntPtr.Zero);
            return true;
        }

        return false;
    }

    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfExtendedkey = 0x0001;
    private const uint KeyeventfKeyup = 0x0002;

    private static INPUT KeyEvent(ushort vk, bool down, bool extended)
    {
        var flags = 0u;
        if (extended)
            flags |= KeyeventfExtendedkey;
        if (!down)
            flags |= KeyeventfKeyup;

        return new INPUT
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = nint.Zero,
                },
            },
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
