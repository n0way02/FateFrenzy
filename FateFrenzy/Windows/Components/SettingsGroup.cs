using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Numerics;

namespace FateFrenzy.Windows.Components;

internal sealed class SettingsGroup : IDisposable
{
    private const float PaddingX = 14f;
    private const float PaddingY = 9f;
    private const float GroupGap = 14f;
    private const float FootnoteFontScale = 0.92f;
    private const float FootnotePullUp = 6f;
    private const float FootnoteIndent = 4f;

    internal static float ContentRightEdge { get; private set; }
    internal static float ContentLeftEdge { get; private set; }
    internal static bool RowDrawnInGroup;

    private readonly Vector2 cardOrigin;
    private readonly float cardWidth;

    public static SettingsGroup Begin(string title)
    {
        if (title.Length > 0)
        {
            Styling.SectionLabel(title);
            Styling.VSpace(4f);
        }

        return new SettingsGroup();
    }

    private SettingsGroup()
    {
        var scale = ImGuiHelpers.GlobalScale;
        cardOrigin = ImGui.GetCursorScreenPos();
        cardWidth = ImGui.GetContentRegionAvail().X;
        ContentLeftEdge = cardOrigin.X + PaddingX * scale;
        ContentRightEdge = cardOrigin.X + cardWidth - PaddingX * scale;
        RowDrawnInGroup = false;

        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.SetCursorScreenPos(cardOrigin + new Vector2(PaddingX, PaddingY) * scale);
        ImGui.BeginGroup();
    }

    public void Dispose()
    {
        ImGui.EndGroup();
        var scale = ImGuiHelpers.GlobalScale;
        var cardEnd = new Vector2(cardOrigin.X + cardWidth, ImGui.GetItemRectMax().Y + PaddingY * scale);

        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSetCurrent(0);
        var rounding = Styling.CardRounding * scale;
        drawList.AddRectFilled(cardOrigin, cardEnd, ImGui.GetColorU32(Styling.CardBg), rounding);
        drawList.AddRect(cardOrigin, cardEnd, ImGui.GetColorU32(Styling.BorderDim), rounding, ImDrawFlags.None, 1f);
        drawList.ChannelsMerge();

        ImGui.SetCursorScreenPos(new Vector2(cardOrigin.X, cardEnd.Y));
        ImGui.Dummy(new Vector2(cardWidth, 0f));
        Styling.VSpace(GroupGap);
    }

    public static void Footnote(string text)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - FootnotePullUp * scale);
        ImGui.Indent(FootnoteIndent * scale);
        ImGui.SetWindowFontScale(FootnoteFontScale);
        using (ImRaii.PushColor(ImGuiCol.Text, Styling.TextMuted))
        {
            ImGui.TextWrapped(text);
        }

        ImGui.SetWindowFontScale(1f);
        ImGui.Unindent(FootnoteIndent * scale);
        Styling.VSpace(GroupGap);
    }

    // Inside BeginGroup(), ImGui.SameLine(offset) positions at GroupStartX (= ContentLeftEdge) + offset,
    // so returning (ContentRightEdge - ContentLeftEdge) aligns every right-edge control pixel-perfectly to ContentRightEdge.
    public static float InnerRightLocalX()
        => ContentRightEdge - ContentLeftEdge;
}
