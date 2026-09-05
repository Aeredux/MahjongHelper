using Dalamud.Configuration;
using System;

namespace MahjongHelper;

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

    /// <summary>Which suggestion provider to use: 0 = In-Game, 1 = Server.</summary>
    public int StrategyProvider { get; set; } = 0;

    /// <summary>Master toggle for auto-play (off by default).</summary>
    public bool AutoPlayEnabled { get; set; } = false;

    /// <summary>Auto-discard the suggested tile when it's the player's turn.</summary>
    public bool AutoDiscardEnabled { get; set; } = true;

    /// <summary>Auto-accept or decline call prompts based on server evaluation.</summary>
    public bool AutoCallEnabled { get; set; } = true;

    /// <summary>Minimum delay in milliseconds before auto-discard executes.</summary>
    public int AutoDiscardDelayMinMs { get; set; } = DelayFloorMs;

    /// <summary>Maximum delay in milliseconds before auto-discard executes.</summary>
    public int AutoDiscardDelayMaxMs { get; set; } = 3000;

    /// <summary>Minimum delay in milliseconds before auto-call decision executes.</summary>
    public int AutoCallDelayMinMs { get; set; } = DelayFloorMs;

    /// <summary>Maximum delay in milliseconds before auto-call decision executes.</summary>
    public int AutoCallDelayMaxMs { get; set; } = 3000;

    /// <summary>
    /// Floor for all autoplay delays. 500ms scheduled a native crash storm
    /// (ReceiveEvent on a hand tile every tick at atk0=15).
    /// </summary>
    public const int DelayFloorMs = 1500;

    public const int DelayCeilMs = 10000;

    public static int ClampDelayMs(int ms)
        => Math.Clamp(ms, DelayFloorMs, DelayCeilMs);

    public void ClampAutoPlayDelays()
    {
        AutoDiscardDelayMinMs = ClampDelayMs(AutoDiscardDelayMinMs);
        AutoDiscardDelayMaxMs = ClampDelayMs(AutoDiscardDelayMaxMs);
        if (AutoDiscardDelayMaxMs < AutoDiscardDelayMinMs)
            AutoDiscardDelayMaxMs = AutoDiscardDelayMinMs;

        AutoCallDelayMinMs = ClampDelayMs(AutoCallDelayMinMs);
        AutoCallDelayMaxMs = ClampDelayMs(AutoCallDelayMaxMs);
        if (AutoCallDelayMaxMs < AutoCallDelayMinMs)
            AutoCallDelayMaxMs = AutoCallDelayMinMs;
    }

    public int NextDiscardDelayMs(Random rng)
    {
        ClampAutoPlayDelays();
        return rng.Next(AutoDiscardDelayMinMs, AutoDiscardDelayMaxMs + 1);
    }

    public int NextCallDelayMs(Random rng)
    {
        ClampAutoPlayDelays();
        return rng.Next(AutoCallDelayMinMs, AutoCallDelayMaxMs + 1);
    }

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        ClampAutoPlayDelays();
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
