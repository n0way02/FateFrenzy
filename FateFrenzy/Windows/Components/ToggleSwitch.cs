using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using System.Numerics;

namespace FateFrenzy.Windows.Components;

internal static class ToggleSwitch
{
    public static bool Draw(string id, ref bool value)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var trackWidth = 40f * scale;
        var trackHeight = 20f * scale;
        var knobRadius = (trackHeight - 5f * scale) * 0.5f;
        var padX = 3f * scale;

        var origin = ImGui.GetCursorScreenPos();
        var end = origin + new Vector2(trackWidth, trackHeight);
        var dl = ImGui.GetWindowDrawList();
        var hovered = ImGui.IsMouseHoveringRect(origin, end);

        var trackColor = value
            ? (hovered ? Styling.BrandYellowSoft : Styling.BrandYellow)
            : (hovered ? Styling.CardBgHover : Styling.SliderBg);
        var borderColor = value
            ? Styling.BrandYellow
            : (hovered ? Styling.BorderGlow : Styling.BorderDim);
        var knobColor = value ? Styling.WindowBg : Styling.TextSecondary;

        dl.AddRectFilled(origin, end, ImGui.GetColorU32(trackColor), trackHeight * 0.5f);
        dl.AddRect(origin, end, ImGui.GetColorU32(borderColor), trackHeight * 0.5f);

        var knobX = value ? end.X - knobRadius - padX : origin.X + knobRadius + padX;
        var knobY = origin.Y + trackHeight * 0.5f;
        dl.AddCircleFilled(new Vector2(knobX, knobY), knobRadius, ImGui.GetColorU32(knobColor));

        ImGui.Dummy(new Vector2(trackWidth, trackHeight));

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            value = !value;
            return true;
        }
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return false;
    }
}
