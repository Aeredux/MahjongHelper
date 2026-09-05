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
/// KAN-55: fire FFXIV's built-in screenshot so PNG/JPG lands in the usual client folder.
/// Telesto is not required — PrintScreen is a keybind, not a slash command.
/// </summary>
public static class GameScreenshot
{
    public const ushort VkSnapshot = 0x2C;

    public enum TriggerMethod
    {
        None,
        GameApi,
        BoundKey,
        PrintScreenFallback,
    }

    public readonly record struct TriggerResult(bool Fired, TriggerMethod Method, string Detail);

    public static readonly string DefaultScreenshotsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "My Games",
        "FINAL FANTASY XIV - A Realm Reborn",
        "screenshots");

    /// <summary>
    /// Priority: game <c>ScreenShot.ScheduleScreenShot</c>, then the bound KEY_SCREENSHOT
    /// key, then emulate VK_SNAPSHOT (PrintScreen) so the game's handler still runs.
    /// </summary>
    public static TriggerResult TryTrigger()
    {
        string? apiDetail = null;
        string? bindDetail = null;

        try
        {
            if (TryScheduleGameScreenshot(out var scheduledDetail))
                return new TriggerResult(true, TriggerMethod.GameApi, scheduledDetail);
            apiDetail = scheduledDetail;
        }
        catch (Exception ex)
        {
            // Signature / CanTakeScreenShot can fail on a mismatched client; fall through.
            apiDetail = ex.Message;
        }

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
            return new TriggerResult(false, TriggerMethod.None, $"all methods failed; last={ex.Message}");
        }

        var why = string.Join("; ", new[] { apiDetail, bindDetail }.Where(s => !string.IsNullOrEmpty(s)));
        return new TriggerResult(false, TriggerMethod.None, string.IsNullOrEmpty(why) ? "SendInput returned 0" : why);
    }

    public static string ResolveScreenshotsDirectory()
    {
        foreach (var candidate in EnumerateScreenshotDirectoryCandidates())
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        return DefaultScreenshotsDirectory;
    }

    public static IEnumerable<string> EnumerateScreenshotDirectoryCandidates()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return DefaultScreenshotsDirectory;
        yield return Path.Combine(documents, "My Games", "FINAL FANTASY XIV", "screenshots");
        yield return Path.Combine(userProfile, "Documents", "My Games", "FINAL FANTASY XIV - A Realm Reborn", "screenshots");
        yield return Path.Combine(userProfile, "Documents", "My Games", "FINAL FANTASY XIV", "screenshots");
    }

    public static string? TryFindNewestScreenshot(string? directory, DateTime notBeforeUtc)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        try
        {
            return Directory.EnumerateFiles(directory)
                .Where(IsScreenshotFile)
                .Select(p => new FileInfo(p))
                .Where(f => f.LastWriteTimeUtc >= notBeforeUtc)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsScreenshotFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private static unsafe bool TryScheduleGameScreenshot(out string detail)
    {
        detail = "ScreenShot.Instance null";
        var shot = ScreenShot.Instance();
        if (shot == null)
            return false;

        if (!shot->CanTakeScreenShot)
        {
            detail = "CanTakeScreenShot=false";
            // Still try — the flag can be stale; a false return falls through.
        }

        var ok = shot->ScheduleScreenShot(null, null);
        detail = ok ? "ScheduleScreenShot" : "ScheduleScreenShot returned false";
        return ok;
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
