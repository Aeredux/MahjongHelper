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

    // ─── Auto-Play Settings ───

    /// <summary>Master toggle for auto-play (off by default).</summary>
    public bool AutoPlayEnabled { get; set; } = false;

    /// <summary>Auto-discard the suggested tile when it's the player's turn.</summary>
    public bool AutoDiscardEnabled { get; set; } = true;

    /// <summary>Auto-accept or decline call prompts based on server evaluation.</summary>
    public bool AutoCallEnabled { get; set; } = true;

    /// <summary>Minimum delay in milliseconds before auto-discard executes.</summary>
    public int AutoDiscardDelayMinMs { get; set; } = 1000;

    /// <summary>Maximum delay in milliseconds before auto-discard executes.</summary>
    public int AutoDiscardDelayMaxMs { get; set; } = 3000;

    /// <summary>Minimum delay in milliseconds before auto-call decision executes.</summary>
    public int AutoCallDelayMinMs { get; set; } = 500;

    /// <summary>Maximum delay in milliseconds before auto-call decision executes.</summary>
    public int AutoCallDelayMaxMs { get; set; } = 2000;

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
