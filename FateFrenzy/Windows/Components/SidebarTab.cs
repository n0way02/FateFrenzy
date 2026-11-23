using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace FateFrenzy.Windows.Components;

internal static class SidebarTab
{
    public static bool Draw(string label, FontAwesomeIcon icon, Vector4 accent, bool selected)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var height = 34f * scale;
        var width = ImGui.GetContentRegionAvail().X;

        var origin = ImGui.GetCursorScreenPos();
        var end = origin + new Vector2(width, height);
        var dl = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsMouseHoveringRect(origin, end);

        if (selected)
        {
            var bg = Styling.WithAlpha(Styling.BrandPurple, 0.34f);
            dl.AddRectFilled(origin, end, ImGui.GetColorU32(bg), 7f * scale);
            dl.AddRect(origin, end, ImGui.GetColorU32(Styling.WithAlpha(Styling.BrandPurpleGlow, 0.45f)), 7f * scale, ImDrawFlags.None, 1f);

            // Crisp electric yellow left accent indicator bar (Lightless-inspired active marker)
            var barMin = new Vector2(origin.X + 2f * scale, origin.Y + 6f * scale);
            var barMax = new Vector2(origin.X + 5f * scale, end.Y - 6f * scale);
            dl.AddRectFilled(barMin, barMax, ImGui.GetColorU32(Styling.BrandYellow), 2f * scale);
        }
        else if (hovered)
        {
            var bg = Styling.WithAlpha(Styling.BrandPurple, 0.16f);
            dl.AddRectFilled(origin, end, ImGui.GetColorU32(bg), 7f * scale);
        }

        var padX = 13f * scale;
        var iconStr = icon.ToIconString();
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            iconSize = ImGui.CalcTextSize(iconStr);

        var iconPos = new Vector2(origin.X + padX, origin.Y + (height - iconSize.Y) * 0.5f);
        ImGui.SetCursorScreenPos(iconPos);
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, selected ? Styling.BrandYellow : (hovered ? Styling.AccentVioletSoft : Styling.TextDim)))
            ImGui.TextUnformatted(iconStr);

        var labelSize = ImGui.CalcTextSize(label);
        var labelPos = new Vector2(
            origin.X + padX + 22f * scale,
            origin.Y + (height - labelSize.Y) * 0.5f);
        ImGui.SetCursorScreenPos(labelPos);
        using (ImRaii.PushColor(ImGuiCol.Text, selected ? Styling.TextStrong : (hovered ? Styling.TextSecondary : Styling.TextDim)))
            ImGui.TextUnformatted(label);

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, height));

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return true;
        }
        return false;
    }
}
