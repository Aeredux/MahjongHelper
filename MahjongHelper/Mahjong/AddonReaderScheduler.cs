using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongHelper.Mahjong;

public class AddonReaderScheduler(IGameGui gameGui)
{
    private class AddonInfo
    {
        public string Name = string.Empty;
        public IAddonStateReader Reader = null!;
        public bool IsActive;
        public IntPtr AddonPtr;
    }

    private readonly IGameGui gameGui = gameGui;
    private readonly List<AddonInfo> addons = [];

    private const float SlowCheckInterval = 0.5f;
    private float slowCheckRemaining = 0.0f;
    private bool hasActiveAddons;

    public void AddObservedAddon(IAddonStateReader reader)
    {
        addons.Add(new AddonInfo
        {
            Name = reader.GetAddonName(),
            Reader = reader,
            IsActive = false,
            AddonPtr = IntPtr.Zero,
        });
    }

    public void Update(float deltaSeconds)
    {
        if (gameGui == null)
            return;

        slowCheckRemaining -= deltaSeconds;
        if (slowCheckRemaining <= 0.0f)
        {
            slowCheckRemaining = SlowCheckInterval;

            foreach (var addon in addons)
            {
                if (addon.IsActive)
                    continue;

                var addonPtr = GetAddonPtrIfValid(addon.Name);
                if (addonPtr == IntPtr.Zero)
                    continue;

                addon.AddonPtr = addonPtr;
                addon.IsActive = true;
                hasActiveAddons = true;
                addon.Reader.OnAddonShown(addonPtr);
            }
        }

        if (!hasActiveAddons)
            return;

        hasActiveAddons = false;
        foreach (var addon in addons)
        {
            if (!addon.IsActive)
                continue;

            var addonPtr = GetAddonPtrIfValid(addon.Name);
            if (addonPtr != addon.AddonPtr)
            {
                addon.IsActive = false;
                addon.AddonPtr = IntPtr.Zero;
                addon.Reader.OnAddonLost();

                if (addonPtr != IntPtr.Zero)
                {
                    addon.IsActive = true;
                    addon.AddonPtr = addonPtr;
                    addon.Reader.OnAddonShown(addonPtr);
                }
            }

            if (addon.IsActive && addon.AddonPtr != IntPtr.Zero)
            {
                addon.Reader.OnAddonUpdate(addon.AddonPtr);
                hasActiveAddons = true;
            }
        }
    }

    private unsafe IntPtr GetAddonPtrIfValid(string name)
    {
        IntPtr addonPtr = gameGui.GetAddonByName(name, 1);
        if (addonPtr == IntPtr.Zero)
            return IntPtr.Zero;

        var baseNode = (AtkUnitBase*)addonPtr;
        if (baseNode == null || baseNode->RootNode == null)
            return IntPtr.Zero;

        return baseNode->RootNode->IsVisible() ? addonPtr : IntPtr.Zero;
    }
}
