using System;

namespace SamplePlugin.Mahjong;

public interface IAddonStateReader
{
    string GetAddonName();
    AddonReaderStatus Status { get; }
    bool IsVisible { get; }
    void OnAddonShown(IntPtr addonPtr);
    void OnAddonUpdate(IntPtr addonPtr);
    void OnAddonLost();
}
