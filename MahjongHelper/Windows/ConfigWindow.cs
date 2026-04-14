using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace MahjongHelper.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin) : base("Mahjong Helper Settings###MahjongHelperConfig")
    {
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 200),
            MaximumSize = new Vector2(400, 500),
        };

        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        // ─── Auto-Play ───
        ImGui.TextColored(new Vector4(1, 0.8f, 0.3f, 1), "Auto-Play");
        ImGui.Separator();

        var autoPlay = configuration.AutoPlayEnabled;
        if (ImGui.Checkbox("Enable Auto-Play", ref autoPlay))
        {
            configuration.AutoPlayEnabled = autoPlay;
            configuration.Save();
        }

        if (autoPlay)
        {
            ImGui.Indent(16);

            var autoDiscard = configuration.AutoDiscardEnabled;
            if (ImGui.Checkbox("Auto-Discard", ref autoDiscard))
            {
                configuration.AutoDiscardEnabled = autoDiscard;
                configuration.Save();
            }

            var autoCall = configuration.AutoCallEnabled;
            if (ImGui.Checkbox("Auto-Call Decisions", ref autoCall))
            {
                configuration.AutoCallEnabled = autoCall;
                configuration.Save();
            }

            ImGui.Unindent(16);

            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1, 0.8f, 0.3f, 1), "Timing");
            ImGui.Separator();

            // Discard delay
            ImGui.Text("Discard Delay (ms)");
            var discardMin = configuration.AutoDiscardDelayMinMs;
            var discardMax = configuration.AutoDiscardDelayMaxMs;
            ImGui.SetNextItemWidth(120);
            if (ImGui.DragInt("Min##DiscardMin", ref discardMin, 50, 200, 10000))
            {
                if (discardMin > discardMax) discardMax = discardMin;
                configuration.AutoDiscardDelayMinMs = discardMin;
                configuration.AutoDiscardDelayMaxMs = discardMax;
                configuration.Save();
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            if (ImGui.DragInt("Max##DiscardMax", ref discardMax, 50, 200, 10000))
            {
                if (discardMax < discardMin) discardMin = discardMax;
                configuration.AutoDiscardDelayMinMs = discardMin;
                configuration.AutoDiscardDelayMaxMs = discardMax;
                configuration.Save();
            }

            // Call delay
            ImGui.Text("Call Decision Delay (ms)");
            var callMin = configuration.AutoCallDelayMinMs;
            var callMax = configuration.AutoCallDelayMaxMs;
            ImGui.SetNextItemWidth(120);
            if (ImGui.DragInt("Min##CallMin", ref callMin, 50, 200, 10000))
            {
                if (callMin > callMax) callMax = callMin;
                configuration.AutoCallDelayMinMs = callMin;
                configuration.AutoCallDelayMaxMs = callMax;
                configuration.Save();
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            if (ImGui.DragInt("Max##CallMax", ref callMax, 50, 200, 10000))
            {
                if (callMax < callMin) callMin = callMax;
                configuration.AutoCallDelayMinMs = callMin;
                configuration.AutoCallDelayMaxMs = callMax;
                configuration.Save();
            }
        }
    }
}
