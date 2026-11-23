using FateFrenzy.Core.External;
using FateFrenzy.Core.Tasks;
using FateFrenzy.Core.Zones;
using FateFrenzy.Windows.Components;
using FateFrenzy.Windows.Sections;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System.Numerics;

namespace FateFrenzy.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin) : base("FateFrenzy###FateFrenzyMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(100, 100),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(780, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Styling.WindowBg);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Styling.TopBarBg);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(0.11f, 0.04f, 0.21f, 1.00f));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(3);
    }

    public override void Draw()
    {
        var cfg = plugin.Configuration;
        var ctrl = plugin.Controller;

        using var style = Styling.PushWindowStyle();

        // Subtle Lightless-style ambient purple dot-grid glow at the top of the main window
        var dl = ImGui.GetWindowDrawList();
        var winMin = ImGui.GetWindowPos();
        var winSize = ImGui.GetWindowSize();
        Styling.DrawAmbientDotGrid(dl, winMin, winMin + new Vector2(winSize.X, MathF.Min(winSize.Y, 260f)), 16f);

        DependencyBanner.Draw(plugin);

        if (!cfg.CompletedTutorial)
        {
            Tutorial.Draw(plugin);
        }
        else if (ctrl.Running)
        {
            RunningPanel.Draw(cfg, ctrl);
        }
        else
        {
            DrawIdle(cfg, ctrl);
        }
    }

    private void DrawIdle(Configuration cfg, AutoFateController ctrl)
    {
        IdleHeader.Draw(cfg, plugin);
        ImGui.Spacing();

        var zoneCount = ZoneSelection.ResolveStartList(cfg).Count;
        StepHeader.Draw(1, "Zones", zoneCount > 0 ? $"{zoneCount} selected" : null);
        ZonePicker.Draw(cfg, ctrl);

        ImGui.Spacing();
        StepHeader.Draw(2, "Run until");
        GoalSummary.Draw(cfg);

        ImGui.Spacing();
        ImGui.Spacing();
        DrawStart(cfg, ctrl);
    }

    private static void DrawStart(Configuration cfg, AutoFateController ctrl)
    {
        var startList = ZoneSelection.ResolveStartList(cfg);
        var depsOk = ExternalPlugins.AllRequiredInstalled();
        var canStart = startList.Count > 0 && !ctrl.Running && depsOk;
        var reason = !depsOk ? "install required plugins"
            : startList.Count == 0 ? "pick at least one zone below"
            : "";
        var sub = $"{startList.Count} zone{(startList.Count == 1 ? "" : "s")}";

        if (StartButton.Draw(sub, canStart, reason))
            ctrl.RunAll(startList);
    }
}
