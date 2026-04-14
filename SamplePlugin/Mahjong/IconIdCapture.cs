using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SamplePlugin.Mahjong;

/// <summary>
/// Hooks AtkImageNode.LoadIconTexture to capture icon IDs as they're loaded onto image nodes.
/// This is necessary because icon-mode images (flags=0x80) don't persist the icon ID
/// anywhere in the ATK UI tree after loading.
/// </summary>
public sealed unsafe class IconIdCapture : IDisposable
{
    private sealed class PersistedEntry
    {
        public ulong Address { get; set; }
        public uint IconId { get; set; }
    }

    private delegate void LoadIconTextureDelegate(AtkImageNode* thisPtr, uint iconId, int language);
    private readonly Hook<LoadIconTextureDelegate> _hook;
    private static readonly string CacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MahjongHelper");
    private static readonly string CacheFilePath = Path.Combine(CacheDirectory, "icon_capture_cache.json");

    // Maps image node address → last loaded icon ID
    private readonly ConcurrentDictionary<nint, uint> _iconMap = new();

    // Recent captures for diagnostics (ring buffer of last 50)
    private readonly List<(DateTime time, nint addr, uint iconId)> _recentCaptures = new();
    private readonly object _recentLock = new();

    // Throttle disk writes: only save when dirty AND at least 5s since last save
    private bool _cacheDirty;
    private DateTime _lastSaveUtc = DateTime.MinValue;
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(5);

    public IconIdCapture(IGameInteropProvider gameInterop)
    {
        LoadCache();

        _hook = gameInterop.HookFromAddress<LoadIconTextureDelegate>(
            (nint)AtkImageNode.MemberFunctionPointers.LoadIconTexture,
            OnLoadIconTexture);
        _hook.Enable();
    }

    private void OnLoadIconTexture(AtkImageNode* thisPtr, uint iconId, int language)
    {
        _iconMap[(nint)thisPtr] = iconId;

        lock (_recentLock)
        {
            _recentCaptures.Add((DateTime.UtcNow, (nint)thisPtr, iconId));
            if (_recentCaptures.Count > 50)
                _recentCaptures.RemoveAt(0);
        }

        MarkDirty();

        _hook.Original(thisPtr, iconId, language);
    }

    /// <summary>
    /// Gets the last icon ID loaded on the given image node, or 0 if unknown.
    /// </summary>
    public uint GetIconId(nint imageNodeAddress)
    {
        return _iconMap.TryGetValue(imageNodeAddress, out var id) ? id : 0;
    }

    /// <summary>
    /// Gets the full map of captured icon IDs (for diagnostics).
    /// </summary>
    public ConcurrentDictionary<nint, uint> IconMap => _iconMap;

    /// <summary>
    /// Gets recent captures for diagnostics.
    /// </summary>
    public List<(DateTime time, nint addr, uint iconId)> GetRecentCaptures()
    {
        lock (_recentLock)
        {
            return new List<(DateTime, nint, uint)>(_recentCaptures);
        }
    }

    public void ResetCapturedIcons()
    {
        _iconMap.Clear();
        lock (_recentLock)
        {
            _recentCaptures.Clear();
        }

        SaveCacheNow();
    }

    /// <summary>
    /// Call periodically (e.g. once per frame) to flush dirty cache to disk if enough time has elapsed.
    /// </summary>
    public void FlushIfNeeded()
    {
        if (!_cacheDirty) return;
        if (DateTime.UtcNow - _lastSaveUtc < SaveInterval) return;
        SaveCacheNow();
    }

    private void MarkDirty()
    {
        _cacheDirty = true;
    }

    private void SaveCacheNow()
    {
        _cacheDirty = false;
        _lastSaveUtc = DateTime.UtcNow;
        SaveCache();
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFilePath))
                return;

            var json = File.ReadAllText(CacheFilePath);
            var entries = JsonSerializer.Deserialize<List<PersistedEntry>>(json);
            if (entries == null)
                return;

            foreach (var entry in entries)
                _iconMap[(nint)entry.Address] = entry.IconId;
        }
        catch
        {
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var entries = new List<PersistedEntry>(_iconMap.Count);
            foreach (var pair in _iconMap)
            {
                entries.Add(new PersistedEntry
                {
                    Address = (ulong)pair.Key,
                    IconId = pair.Value,
                });
            }

            File.WriteAllText(CacheFilePath, JsonSerializer.Serialize(entries));
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        SaveCacheNow();
        _hook.Disable();
        _hook.Dispose();
    }
}
