using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Numerics;

namespace FateFrenzy.Windows;

internal static class Styling
{
    // Unified n0way02 Brand Palette: #6410C9 (Royal Purple) + #FFE600 (Electric Yellow) + #E9E9E9 (Light Gray)
    public static readonly Vector4 BrandPurple      = new(0.392f, 0.063f, 0.788f, 1.00f); // #6410C9
    public static readonly Vector4 BrandPurpleGlow  = new(0.557f, 0.255f, 0.945f, 1.00f); // #8E41F1 (bright readable violet)
    public static readonly Vector4 BrandYellow      = new(1.000f, 0.902f, 0.000f, 1.00f); // #FFE600
    public static readonly Vector4 BrandYellowSoft  = new(1.000f, 0.941f, 0.302f, 1.00f); // #FFF04D
    public static readonly Vector4 BrandGray        = new(0.914f, 0.914f, 0.914f, 1.00f); // #E9E9E9

    // Semantic accents mapped cleanly to the unified palette
    public static readonly Vector4 AccentViolet     = BrandPurpleGlow;                     // Primary violet accent
    public static readonly Vector4 AccentVioletSoft = new(0.740f, 0.560f, 0.990f, 1.00f); // Soft lavender highlight
    public static readonly Vector4 AccentPink       = BrandYellow;                         // Electric yellow highlight
    public static readonly Vector4 AccentMint       = new(0.200f, 0.840f, 0.480f, 1.00f); // Live / Installed emerald
    public static readonly Vector4 AccentMintSoft   = new(0.450f, 0.940f, 0.680f, 1.00f); // Soft emerald
    public static readonly Vector4 AccentAmber      = BrandYellow;                         // Electric yellow
    public static readonly Vector4 AccentAmberSoft  = BrandYellowSoft;                     // Soft electric yellow
    public static readonly Vector4 AccentRose       = new(0.950f, 0.280f, 0.420f, 1.00f); // Error / Danger
    public static readonly Vector4 AccentBlue       = BrandYellow;                         // Primary interactive highlight
    public static readonly Vector4 AccentBlueSoft   = BrandYellowSoft;

    // Aliases kept for components that still reference the teal naming.
    public static readonly Vector4 AccentTeal     = AccentViolet;
    public static readonly Vector4 AccentTealSoft = AccentVioletSoft;

    // Deep violet-obsidian surfaces (Lightless-inspired dark layered depth)
    public static readonly Vector4 WindowBg      = new(0.055f, 0.024f, 0.105f, 0.97f); // #0E061B
    public static readonly Vector4 SidebarBg     = new(0.075f, 0.032f, 0.140f, 0.96f); // #130824
    public static readonly Vector4 TopBarBg      = new(0.068f, 0.028f, 0.128f, 0.98f); // #110721
    public static readonly Vector4 CardBg        = new(0.088f, 0.042f, 0.160f, 0.95f); // #160B29
    public static readonly Vector4 CardBgSoft    = new(0.105f, 0.052f, 0.188f, 0.86f); // #1B0D30
    public static readonly Vector4 CardBgHover   = new(0.145f, 0.075f, 0.255f, 0.95f); // #251341
    public static readonly Vector4 DescBoxBg     = new(0.068f, 0.030f, 0.128f, 0.82f); // Subtle inner description box
    public static readonly Vector4 SliderBg      = new(0.165f, 0.095f, 0.275f, 1.00f); // Track purple-slate
    public static readonly Vector4 BorderDim     = new(0.280f, 0.160f, 0.450f, 0.55f); // Subtle royal purple border
    public static readonly Vector4 BorderGlow    = new(0.557f, 0.255f, 0.945f, 0.65f); // Active violet border

    public static readonly Vector4 TextStrong    = new(1.000f, 1.000f, 1.000f, 1.00f); // #FFFFFF
    public static readonly Vector4 TextSecondary = BrandGray;                           // #E9E9E9
    public static readonly Vector4 TextDim       = new(0.750f, 0.710f, 0.840f, 1.00f); // Soft lavender-gray
    public static readonly Vector4 TextMuted     = new(0.540f, 0.495f, 0.640f, 1.00f); // Muted lavender-gray

    public static readonly Vector4 Hairline = new(0.78f, 0.62f, 1.00f, 0.10f);

    // Corner radii shared between the ImGui style pushes and hand-drawn cards/tiles
    public const float CardRounding = 10f;
    public const float FrameRounding = 7f;
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
        p.Push(ImGuiStyleVar.WindowPadding, new Vector2(12, 10) * ImGuiHelpers.GlobalScale);
        p.Push(ImGuiStyleVar.FrameRounding, FrameRounding);
        return p;
    }

    /// <summary>
    /// Applies the unified n0way02 dark purple + electric yellow + crisp white ImGui chrome.
    /// </summary>
    public static IDisposable PushWindowStyle()
    {
        var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, FrameRounding);
        style.Push(ImGuiStyleVar.WindowRounding, WindowRounding);
        style.Push(ImGuiStyleVar.ChildRounding, CardRounding);
        style.Push(ImGuiStyleVar.PopupRounding, 9f);
        style.Push(ImGuiStyleVar.GrabRounding, 6f);
        style.Push(ImGuiStyleVar.ScrollbarRounding, 8f);
        style.Push(ImGuiStyleVar.ScrollbarSize, 10f * ImGuiHelpers.GlobalScale);
        style.Push(ImGuiStyleVar.ItemSpacing, new Vector2(8, 7) * ImGuiHelpers.GlobalScale);

        var colors = ImRaii.PushColor(ImGuiCol.WindowBg, WindowBg);
        colors.Push(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0));
        colors.Push(ImGuiCol.PopupBg, CardBg);
        colors.Push(ImGuiCol.Border, BorderDim);
        colors.Push(ImGuiCol.TitleBg, TopBarBg);
        colors.Push(ImGuiCol.TitleBgActive, new Vector4(0.11f, 0.04f, 0.21f, 1.00f));
        colors.Push(ImGuiCol.FrameBg, new Vector4(0.13f, 0.065f, 0.23f, 0.90f));
        colors.Push(ImGuiCol.FrameBgHovered, new Vector4(0.19f, 0.095f, 0.33f, 0.95f));
        colors.Push(ImGuiCol.FrameBgActive, new Vector4(0.24f, 0.11f, 0.42f, 1.00f));
        colors.Push(ImGuiCol.Button, new Vector4(0.21f, 0.09f, 0.38f, 0.85f));
        colors.Push(ImGuiCol.ButtonHovered, WithAlpha(BrandPurple, 0.95f));
        colors.Push(ImGuiCol.ButtonActive, BrandPurpleGlow);
        colors.Push(ImGuiCol.Header, WithAlpha(BrandPurple, 0.55f));
        colors.Push(ImGuiCol.HeaderHovered, WithAlpha(BrandPurple, 0.80f));
        colors.Push(ImGuiCol.HeaderActive, BrandPurpleGlow);
        colors.Push(ImGuiCol.SliderGrab, BrandYellow);
        colors.Push(ImGuiCol.SliderGrabActive, BrandYellowSoft);
        colors.Push(ImGuiCol.CheckMark, BrandYellow);
        colors.Push(ImGuiCol.ScrollbarBg, new Vector4(0.05f, 0.02f, 0.09f, 0.45f));
        colors.Push(ImGuiCol.ScrollbarGrab, WithAlpha(BrandPurpleGlow, 0.55f));
        colors.Push(ImGuiCol.ScrollbarGrabHovered, WithAlpha(BrandYellow, 0.75f));
        colors.Push(ImGuiCol.ScrollbarGrabActive, BrandYellow);
        colors.Push(ImGuiCol.Separator, Hairline);
        colors.Push(ImGuiCol.Text, TextSecondary);

        return new CombinedDisposable(style, colors);
    }

    /// <summary>
    /// Draws Lightless-style subtle ambient purple glow + dot-grid texture inside a panel rect.
    /// </summary>
    public static void DrawAmbientDotGrid(ImDrawListPtr dl, Vector2 min, Vector2 max, float dotSpacing = 14f)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var topGlow = ImGui.GetColorU32(WithAlpha(BrandPurple, 0.22f));
        var botClear = ImGui.GetColorU32(new Vector4(0.055f, 0.024f, 0.105f, 0.0f));
        var glowBottom = MathF.Min(max.Y, min.Y + (max.Y - min.Y) * 0.55f);
        dl.AddRectFilledMultiColor(min, new Vector2(max.X, glowBottom), topGlow, topGlow, botClear, botClear);

        var step = MathF.Max(10f, dotSpacing * scale);
        var dotCol = ImGui.GetColorU32(new Vector4(0.75f, 0.55f, 1.0f, 0.09f));
        dl.PushClipRect(min, max, true);
        for (var y = min.Y + step * 0.5f; y < max.Y; y += step)
        {
            for (var x = min.X + step * 0.5f; x < max.X; x += step)
            {
                dl.AddCircleFilled(new Vector2(x, y), 1.0f * scale, dotCol, 4);
            }
        }
        dl.PopClipRect();
    }

    /// <summary>
    /// Draws Lightless-style section header: ─── SECTION TITLE ───────────────────
    /// </summary>
    public static void DrawSectionDividerHeader(string title)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var upper = title.ToUpperInvariant();
        var textSize = ImGui.CalcTextSize(upper);
        var avail = ImGui.GetContentRegionAvail().X;
        var origin = ImGui.GetCursorScreenPos();
        var midY = origin.Y + textSize.Y * 0.5f;
        var dl = ImGui.GetWindowDrawList();

        var leadLineW = 22f * scale;
        var gap = 10f * scale;
        var lineCol = ImGui.GetColorU32(WithAlpha(BrandPurpleGlow, 0.55f));
        var accentCol = ImGui.GetColorU32(BrandYellow);

        // Short yellow lead accent line
        dl.AddLine(new Vector2(origin.X, midY), new Vector2(origin.X + leadLineW, midY), accentCol, 2.0f * scale);

        // Title text
        ImGui.SetCursorScreenPos(new Vector2(origin.X + leadLineW + gap, origin.Y));
        using (ImRaii.PushColor(ImGuiCol.Text, BrandYellow))
            ImGui.TextUnformatted(upper);

        // Trailing hairline across remaining width
        var trailStart = origin.X + leadLineW + gap + textSize.X + gap;
        var trailEnd = origin.X + avail;
        if (trailEnd > trailStart + 8f)
            dl.AddLine(new Vector2(trailStart, midY), new Vector2(trailEnd, midY), lineCol, 1.0f);

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(avail, textSize.Y + 4f * scale));
    }

    public static void SectionLabel(string label)
    {
        DrawSectionDividerHeader(label);
    }

    private sealed class CombinedDisposable(IDisposable a, IDisposable b) : IDisposable
    {
        public void Dispose()
        {
            b.Dispose();
            a.Dispose();
        }
    }
}
