using System;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongHelper.Mahjong;

public sealed unsafe class EmjAddonReader : IAddonStateReader
{
    public AddonReaderStatus Status { get; private set; } = AddonReaderStatus.AddonNotFound;
    public bool IsVisible => Status == AddonReaderStatus.NoErrors;
    public IntPtr CurrentAddonPtr { get; private set; }

    public event Action<IntPtr>? OnShown;
    public event Action<IntPtr>? OnUpdated;
    public event Action? OnLost;

    public string GetAddonName() => "EmjL";

    public void OnAddonShown(IntPtr addonPtr)
    {
        CurrentAddonPtr = addonPtr;
        Status = addonPtr == IntPtr.Zero ? AddonReaderStatus.InvalidAddonPointer : AddonReaderStatus.NoErrors;

        if (Status == AddonReaderStatus.NoErrors)
            OnShown?.Invoke(addonPtr);
    }

    public void OnAddonUpdate(IntPtr addonPtr)
    {
        try
        {
            if (addonPtr == IntPtr.Zero)
            {
                Status = AddonReaderStatus.InvalidAddonPointer;
                return;
            }

            var addon = (AtkUnitBase*)addonPtr;
            if (addon == null || addon->RootNode == null)
            {
                Status = AddonReaderStatus.InvalidAddonPointer;
                return;
            }

            CurrentAddonPtr = addonPtr;
            Status = addon->RootNode->IsVisible() ? AddonReaderStatus.NoErrors : AddonReaderStatus.AddonNotVisible;
            if (Status == AddonReaderStatus.NoErrors)
                OnUpdated?.Invoke(addonPtr);
        }
        catch
        {
            Status = AddonReaderStatus.UpdateError;
        }
    }

    public void OnAddonLost()
    {
        CurrentAddonPtr = IntPtr.Zero;
        Status = AddonReaderStatus.AddonNotFound;
        OnLost?.Invoke();
    }
}
