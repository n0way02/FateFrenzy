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
        var height = 40f * ImGuiHelpers.GlobalScale;
        var width = ImGui.GetContentRegionAvail().X;

        var origin = ImGui.GetCursorScreenPos();
        var end = origin + new Vector2(width, height);
        var dl = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsMouseHoveringRect(origin, end);

        var bg = selected
            ? Styling.WithAlpha(accent, 0.12f)
            : hovered ? Styling.WithAlpha(accent, 0.05f) : new Vector4(0, 0, 0, 0);

        var borderCol = selected ? accent : (hovered ? Styling.WithAlpha(accent, 0.20f) : Styling.BorderDim);

        dl.AddRectFilled(origin, end, ImGui.GetColorU32(bg), 8f);
        dl.AddRect(origin, end, ImGui.GetColorU32(borderCol), 8f, ImDrawFlags.None, selected ? 1.5f : 1.0f);

        var padX = 14f * ImGuiHelpers.GlobalScale;
        var iconStr = icon.ToIconString();
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            iconSize = ImGui.CalcTextSize(iconStr);

        var iconPos = new Vector2(origin.X + padX, origin.Y + (height - iconSize.Y) * 0.5f);
        ImGui.SetCursorScreenPos(iconPos);
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, selected ? accent : Styling.TextSecondary))
            ImGui.TextUnformatted(iconStr);

        var labelSize = ImGui.CalcTextSize(label);
        var labelPos = new Vector2(
            origin.X + padX + iconSize.X + 10f * ImGuiHelpers.GlobalScale,
            origin.Y + (height - labelSize.Y) * 0.5f);
        ImGui.SetCursorScreenPos(labelPos);
        using (ImRaii.PushColor(ImGuiCol.Text, selected ? Styling.TextStrong : Styling.TextSecondary))
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
