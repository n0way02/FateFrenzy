using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace FateFrenzy.Windows;

internal static class Styling
{
    public static readonly Vector4 AccentViolet     = new(0.00f, 0.82f, 0.88f, 1.00f); // Bright Cyber Cyan
    public static readonly Vector4 AccentVioletSoft = new(0.35f, 0.90f, 0.95f, 1.00f); // Soft Cyan
    public static readonly Vector4 AccentPink       = new(0.98f, 0.50f, 0.15f, 1.00f); // Cyberpunk Orange / Rose Gold
    public static readonly Vector4 AccentMint       = new(0.18f, 0.80f, 0.44f, 1.00f); // Deep Emerald
    public static readonly Vector4 AccentMintSoft   = new(0.40f, 0.92f, 0.65f, 1.00f); // Soft Emerald
    public static readonly Vector4 AccentAmber      = new(0.95f, 0.72f, 0.15f, 1.00f); // Cyber Gold
    public static readonly Vector4 AccentAmberSoft  = new(1.00f, 0.84f, 0.35f, 1.00f); // Soft Gold
    public static readonly Vector4 AccentRose       = new(0.92f, 0.25f, 0.38f, 1.00f); // Crimson Pink
    public static readonly Vector4 AccentBlue       = new(0.10f, 0.50f, 0.95f, 1.00f); // Deep Sapphire
    public static readonly Vector4 AccentBlueSoft   = new(0.45f, 0.75f, 1.00f, 1.00f); // Soft Sapphire

    // Aliases kept for components that still reference the teal naming.
    public static readonly Vector4 AccentTeal     = AccentViolet;
    public static readonly Vector4 AccentTealSoft = AccentVioletSoft;

    public static readonly Vector4 CardBg        = new(0.05f, 0.055f, 0.065f, 0.92f); // Deep Dark Carbon
    public static readonly Vector4 CardBgSoft    = new(0.07f, 0.075f, 0.085f, 0.65f); // Soft Dark Carbon
    public static readonly Vector4 CardBgHover   = new(0.09f, 0.105f, 0.125f, 0.95f); // Active Hover Gray
    public static readonly Vector4 SliderBg      = new(0.15f, 0.17f, 0.20f, 1.00f); // Slider track gray
    public static readonly Vector4 BorderDim     = new(0.14f, 0.17f, 0.20f, 1.00f); // Subtle border

    public static readonly Vector4 TextStrong    = new(0.98f, 0.98f, 0.99f, 1.00f); // Pure white
    public static readonly Vector4 TextSecondary = new(0.80f, 0.82f, 0.86f, 1.00f); // Light gray
    public static readonly Vector4 TextDim       = new(0.58f, 0.60f, 0.65f, 1.00f); // Medium gray
    public static readonly Vector4 TextMuted     = new(0.40f, 0.42f, 0.46f, 1.00f); // Dark gray

    public static readonly Vector4 Hairline = new(1f, 1f, 1f, 0.055f);

    // Corner radii shared between the ImGui style pushes and hand-drawn cards/tiles (single retune point).
    public const float CardRounding = 10f;
    public const float FrameRounding = 8f;
    public const float WindowRounding = 12f;

    public const double PulseFast = 600.0;
    public const double PulseMedium = 800.0;

    public const double PulseBreath = 2600.0;
    public const double PulseCalm = 1900.0;
    public const double PulseOrbit = 3400.0;

    public static float Pulse(double periodMs = PulseMedium)
    {
        var t = (Environment.TickCount % periodMs) / periodMs;
        return (float)((Math.Sin(t * Math.PI * 2.0) + 1.0) * 0.5);
    }

    public static Vector4 PulseColor(Vector4 a, Vector4 b, double periodMs = PulseMedium)
        => Vector4.Lerp(a, b, Pulse(periodMs));

    public static float Phase(double periodMs)
        => (float)((Environment.TickCount % periodMs) / periodMs);

    public static Vector4 WithAlpha(Vector4 c, float a) => c with { W = a };

    public static void TextCentered(string text, Vector4 color, float fontScale = 1f)
    {
        if (fontScale != 1f) ImGui.SetWindowFontScale(fontScale);
        var w = ImGui.CalcTextSize(text).X;
        var avail = ImGui.GetContentRegionAvail().X;
        if (avail > w) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - w) * 0.5f);
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextUnformatted(text);
        if (fontScale != 1f) ImGui.SetWindowFontScale(1f);
    }

    public static void VSpace(float pixels)
        => ImGui.Dummy(new Vector2(0, pixels * ImGuiHelpers.GlobalScale));

    public static void CenterNextItem(float width)
    {
        var avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (avail - width) * 0.5f));
    }

    public static IDisposable PushCardStyle()
    {
        var p = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, CardRounding * ImGuiHelpers.GlobalScale);
        p.Push(ImGuiStyleVar.ChildBorderSize, 1f);
        p.Push(ImGuiStyleVar.WindowPadding, new Vector2(11, 9) * ImGuiHelpers.GlobalScale);
        p.Push(ImGuiStyleVar.FrameRounding, FrameRounding);
        return p;
    }

    public static IDisposable PushWindowStyle()
    {
        var p = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, FrameRounding);
        p.Push(ImGuiStyleVar.WindowRounding, WindowRounding);
        p.Push(ImGuiStyleVar.ChildRounding, CardRounding);
        p.Push(ImGuiStyleVar.ItemSpacing, new Vector2(8, 7) * ImGuiHelpers.GlobalScale);
        return p;
    }

    public static void SectionLabel(string label)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, TextDim))
            ImGui.TextUnformatted(label.ToUpperInvariant());
    }
}
