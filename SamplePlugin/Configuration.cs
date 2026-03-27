using Dalamud.Configuration;
using System;

namespace SamplePlugin;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;
    public bool SomePropertyToBeSavedAndWithADefault { get; set; } = true;

    /// <summary>Whether the suggestion overlay is visible.</summary>
    public bool OverlayVisible { get; set; } = true;

    /// <summary>Compact mode shows only the top suggestion in one line.</summary>
    public bool OverlayCompactMode { get; set; } = false;

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
