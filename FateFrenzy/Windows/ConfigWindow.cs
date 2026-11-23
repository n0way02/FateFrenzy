using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
using FateFrenzy.Core.External;
using FateFrenzy.Windows.Components;
using FateFrenzy.Windows.Sections.Config;
using System;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace FateFrenzy.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private enum Tab { General, Filters, Classes, Gemstones, Repair, Consumables, Humanize, PartyInvites, GmAlert, Vouchers, Discord }

    private sealed record TabEntry(Tab Id, string Label, FontAwesomeIcon Icon, string Subtitle, string Keywords);

    private static readonly TabEntry[] Tabs =
    [
        new(Tab.General,      "General",         FontAwesomeIcon.Cog,        "Window, startup, and general automation preferences.", "general window startup login resume popout"),
        new(Tab.Filters,      "FATE filters",    FontAwesomeIcon.Filter,     "Keeps the plugin off dying, late, or unwanted FATEs.", "fate filter progress time boss collection blacklist"),
        new(Tab.Classes,      "Class queue",     FontAwesomeIcon.UserShield, "Switch gearsets on start and advance when a job hits cap.", "class job gearset level queue swap"),
        new(Tab.Gemstones,    "Gemstones",       FontAwesomeIcon.Gem,        "Auto-spend Bicolor Gemstones once your wallet hits threshold.", "gemstone bicolor trader voucher shop"),
        new(Tab.Vouchers,     "Voucher & Mount", FontAwesomeIcon.Store,      "Multi-account voucher trading, Edelina mount exchange, and Retainer market seller.", "voucher mount edelina retainer market board universalis"),
        new(Tab.Repair,       "Repair",          FontAwesomeIcon.Wrench,     "Auto-repair gear when equipped durability drops below threshold.", "repair durability dark matter mender"),
        new(Tab.Consumables,  "Consumables",     FontAwesomeIcon.Utensils,   "Keep food (+3% EXP) and medicine buffs active while grinding.", "food potion medicine consumable chocobo gysahl"),
        new(Tab.Humanize,     "Humanizer",       FontAwesomeIcon.Walking,    "Take periodic city breaks between FATEs to look natural.", "humanize break city idle wander"),
        new(Tab.PartyInvites, "Party invites",   FontAwesomeIcon.UserSlash,  "Auto-decline incoming party invites with optional humanized reply.", "party invite decline reply tell"),
        new(Tab.GmAlert,      "GM alert",        FontAwesomeIcon.UserSecret, "Detects nearby Game Masters and reacts immediately.", "gm game master alert sound stop"),
        new(Tab.Discord,      "Discord alerts",  FontAwesomeIcon.Bell,       "Instant Discord webhook alerts for tells, invites, GMs, and goals.", "discord webhook notification whisper tell"),
    ];

    private static readonly string PluginVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(4) ?? "1.8.8.24";

    private readonly Plugin plugin;
    private Tab activeTab = Tab.General;
    private string searchQuery = string.Empty;

    public ConfigWindow(Plugin plugin) : base("FateFrenzy Settings###FateFrenzyConfig")
    {
        this.plugin = plugin;
        Size = new Vector2(1040, 660);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(600, 420),
            MaximumSize = new Vector2(2200, 1600),
        };
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Styling.WindowBg);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Styling.TopBarBg);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(0.11f, 0.04f, 0.21f, 1.00f));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar(1);
    }

    public override void Draw()
    {
        var cfg = plugin.Configuration;
        using var style = Styling.PushWindowStyle();

        var scale = ImGuiHelpers.GlobalScale;
        var avail = ImGui.GetContentRegionAvail();
        var topBarH = 44f * scale;

        DrawTopBar(avail.X, topBarH);

        var bodyH = MathF.Max(120f, avail.Y - topBarH);
        var leftW = 192f * scale;
        var showRightPanel = avail.X >= 780f * scale;
        var rightW = showRightPanel ? 228f * scale : 0f;
        var centerW = MathF.Max(260f * scale, avail.X - leftW - rightW);

        // 1. LEFT SIDEBAR (Dot-grid + vertical nav tabs)
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(12, 14) * scale))
        using (ImRaii.Child("##cfg_left", new Vector2(leftW, bodyH), border: false, flags: ImGuiWindowFlags.AlwaysUseWindowPadding))
        {
            var dl = ImGui.GetWindowDrawList();
            var pMin = ImGui.GetWindowPos();
            var pMax = pMin + ImGui.GetWindowSize();
            dl.AddRectFilled(pMin, pMax, ImGui.GetColorU32(Styling.SidebarBg));
            Styling.DrawAmbientDotGrid(dl, pMin, pMax);
            dl.AddLine(new Vector2(pMax.X - 1f, pMin.Y), new Vector2(pMax.X - 1f, pMax.Y), ImGui.GetColorU32(Styling.Hairline), 1f);

            DrawSidebar();
        }

        ImGui.SameLine(0, 0);

        // 2. CENTER CONTENT AREA
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(24, 18) * scale))
        using (ImRaii.Child("##cfg_center", new Vector2(centerW, bodyH), border: false, flags: ImGuiWindowFlags.AlwaysUseWindowPadding))
        {
            DrawContent(cfg);
        }

        // 3. RIGHT INFO / STATUS PANEL (Lightless-style Service, Plugins & Community)
        if (showRightPanel)
        {
            ImGui.SameLine(0, 0);
            using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(16, 16) * scale))
            using (ImRaii.Child("##cfg_right", new Vector2(rightW, bodyH), border: false, flags: ImGuiWindowFlags.AlwaysUseWindowPadding))
            {
                var dl = ImGui.GetWindowDrawList();
                var pMin = ImGui.GetWindowPos();
                var pMax = pMin + ImGui.GetWindowSize();
                dl.AddRectFilled(pMin, pMax, ImGui.GetColorU32(Styling.SidebarBg));
                Styling.DrawAmbientDotGrid(dl, pMin, pMax);
                dl.AddLine(new Vector2(pMin.X, pMin.Y), new Vector2(pMin.X, pMax.Y), ImGui.GetColorU32(Styling.Hairline), 1f);

                DrawRightPanel();
            }
        }
    }

    private void DrawTopBar(float totalWidth, float height)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + new Vector2(totalWidth, height);
        var dl = ImGui.GetWindowDrawList();

        dl.AddRectFilled(origin, end, ImGui.GetColorU32(Styling.TopBarBg));
        dl.AddLine(new Vector2(origin.X, end.Y - 1f), new Vector2(end.X, end.Y - 1f), ImGui.GetColorU32(Styling.BorderDim), 1f);

        // Brand title on left
        var padX = 16f * scale;
        var midY = origin.Y + height * 0.5f;
        var titleH = ImGui.GetTextLineHeight();
        ImGui.SetCursorScreenPos(new Vector2(origin.X + padX, midY - titleH * 0.5f));
        using (ImRaii.PushColor(ImGuiCol.Text, Styling.TextStrong))
            ImGui.TextUnformatted("FATE");
        ImGui.SameLine(0, 0);
        using (ImRaii.PushColor(ImGuiCol.Text, Styling.BrandYellow))
            ImGui.TextUnformatted("FRENZY");
        ImGui.SameLine(0, 8f * scale);
        using (ImRaii.PushColor(ImGuiCol.Text, Styling.TextMuted))
            ImGui.TextUnformatted(PluginVersion);

        // Centered search input
        var searchW = Math.Clamp(totalWidth * 0.48f, 180f * scale, 520f * scale);
        var searchX = origin.X + MathF.Max(192f * scale, (totalWidth - searchW) * 0.5f);
        var frameH = ImGui.GetFrameHeight();
        ImGui.SetCursorScreenPos(new Vector2(searchX, midY - frameH * 0.5f));
        ImGui.SetNextItemWidth(searchW);
        using (ImRaii.PushColor(ImGuiCol.FrameBg, new Vector4(0.10f, 0.045f, 0.19f, 0.95f)))
        using (ImRaii.PushColor(ImGuiCol.Border, Styling.BorderDim))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 1f))
            ImGui.InputTextWithHint("##cfg_search", "Search settings tabs...", ref searchQuery, 64);

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(totalWidth, height));
    }

    private void DrawSidebar()
    {
        var query = searchQuery.Trim();
        var anyShown = false;

        foreach (var entry in Tabs)
        {
            if (query.Length > 0
                && !entry.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !entry.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !entry.Keywords.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            anyShown = true;
            if (SidebarTab.Draw(entry.Label, entry.Icon, Styling.BrandPurpleGlow, activeTab == entry.Id))
                activeTab = entry.Id;
        }

        if (!anyShown)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Styling.TextMuted))
                ImGui.TextWrapped("No matching tabs.");
        }
    }

    private void DrawContent(Configuration cfg)
    {
        var current = Tabs.First(t => t.Id == activeTab);
        DrawSectionHeader(current.Label, current.Subtitle);

        switch (activeTab)
        {
            case Tab.General:      GeneralSettings.Draw(cfg); break;
            case Tab.Filters:      FilterSettings.Draw(cfg); break;
            case Tab.Classes:      ClassSettings.Draw(cfg); break;
            case Tab.Gemstones:    GemstoneSettings.Draw(cfg); break;
            case Tab.Vouchers:     VoucherMountSettings.Draw(cfg); break;
            case Tab.Repair:       RepairSettings.Draw(cfg); break;
            case Tab.Consumables:  ConsumableSettings.Draw(cfg); break;
            case Tab.Humanize:     HumanizerSettings.Draw(cfg); break;
            case Tab.PartyInvites: PartyInviteSettings.Draw(cfg); break;
            case Tab.GmAlert:      GmAlertSettings.Draw(cfg); break;
            case Tab.Discord:      DiscordSettings.Draw(cfg); break;
        }
    }

    private static void DrawSectionHeader(string title, string subtitle)
    {
        Styling.DrawSectionDividerHeader(title);
        using (ImRaii.PushColor(ImGuiCol.Text, Styling.TextDim))
            ImGui.TextWrapped(subtitle);
        Styling.VSpace(10f);
    }

    private void DrawRightPanel()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var ctrl = plugin.Controller;

        // --- SERVICE / SESSION ---
        DrawRightSectionTitle(FontAwesomeIcon.Globe, "Service");
        Styling.VSpace(3f);

        var isRunning = ctrl.Running;
        var statusText = isRunning ? $"Active • {ctrl.Phase}" : "Idle (Ready)";
        var statusColor = isRunning ? Styling.AccentMint : Styling.BrandYellow;
        DrawStatusPill(statusText, statusColor);

        Styling.VSpace(4f);
        using (ImRaii.PushColor(ImGuiCol.Text, Styling.TextDim))
            ImGui.TextWrapped("n0wayplugins.discloud.app");

        Styling.VSpace(14f);

        // --- PLUGINS / DEPENDENCIES ---
        DrawRightSectionTitle(FontAwesomeIcon.Plug, "Plugins");
        Styling.VSpace(4f);

        var allDeps = ExternalPlugins.All.ToArray();
        var installedCount = allDeps.Count(ExternalPlugins.IsInstalled);
        using (ImRaii.PushColor(ImGuiCol.Text, Styling.AccentVioletSoft))
            ImGui.TextUnformatted($"Required {installedCount}/{allDeps.Length}");
        Styling.VSpace(2f);

        foreach (var dep in allDeps)
            DrawPluginChip(dep);

        var missingDeps = allDeps.Where(p => !ExternalPlugins.IsInstalled(p)).ToArray();
        if (missingDeps.Length > 0)
        {
            Styling.VSpace(4f);
            var anyInstalling = missingDeps.Any(PluginInstaller.IsInstalling);
            if (DrawMiniInstallButton(anyInstalling ? "Installing..." : $"Install Missing ({missingDeps.Length})", Styling.BrandYellow, !anyInstalling))
            {
                foreach (var dep in missingDeps)
                    _ = PluginInstaller.Install(dep);
            }
        }

        Styling.VSpace(16f);

        // --- COMMUNITY / LINKS ---
        DrawRightSectionTitle(FontAwesomeIcon.Link, "Community");
        Styling.VSpace(5f);

        if (DrawLinkPill(FontAwesomeIcon.Globe, "Website & Hub", Styling.BrandPurpleGlow))
            UrlActions.OpenInBrowser("https://n0wayplugins.discloud.app/");
        Styling.VSpace(3f);

        if (DrawLinkPill(FontAwesomeIcon.ChartLine, "Fate Tracker", Styling.BrandYellow))
            UrlActions.OpenInBrowser("https://n0wayplugins.discloud.app/tracker");
        Styling.VSpace(3f);

        if (DrawLinkPill(FontAwesomeIcon.Heart, "Support on Ko-fi", Styling.AccentRose))
            UrlActions.OpenInBrowser("https://ko-fi.com/n0way02");
        Styling.VSpace(3f);

        if (DrawLinkPill(FontAwesomeIcon.Code, "FateFrenzy GitHub", Styling.AccentMint))
            UrlActions.OpenInBrowser("https://github.com/n0way02/FateFrenzy");

        _ = scale;
    }

    private static void DrawRightSectionTitle(FontAwesomeIcon icon, string label)
    {
        var scale = ImGuiHelpers.GlobalScale;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, Styling.BrandYellow))
            ImGui.TextUnformatted(icon.ToIconString());
        ImGui.SameLine(0, 7f * scale);
        using (ImRaii.PushColor(ImGuiCol.Text, Styling.TextStrong))
            ImGui.TextUnformatted(label);
    }

    private static void DrawStatusPill(string text, Vector4 dotColor)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        var textSize = ImGui.CalcTextSize(text);
        var padX = 10f * scale;
        var padY = 4f * scale;
        var dotR = 3.5f * scale;
        var w = padX * 2f + dotR * 2f + 7f * scale + textSize.X;
        var h = textSize.Y + padY * 2f;
        var end = origin + new Vector2(w, h);
        var dl = ImGui.GetWindowDrawList();

        dl.AddRectFilled(origin, end, ImGui.GetColorU32(Styling.WithAlpha(dotColor, 0.14f)), h * 0.5f);
        dl.AddRect(origin, end, ImGui.GetColorU32(Styling.WithAlpha(dotColor, 0.45f)), h * 0.5f);
        dl.AddCircleFilled(new Vector2(origin.X + padX + dotR, origin.Y + h * 0.5f), dotR, ImGui.GetColorU32(dotColor));

        ImGui.SetCursorScreenPos(new Vector2(origin.X + padX + dotR * 2f + 7f * scale, origin.Y + padY));
        using (ImRaii.PushColor(ImGuiCol.Text, Styling.TextStrong))
            ImGui.TextUnformatted(text);

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(w, h));
    }

    private static void DrawPluginChip(ExternalPlugin dep)
    {
        var info = ExternalPlugins.Catalog[dep];
        var installed = ExternalPlugins.IsInstalled(dep);
        var installing = PluginInstaller.IsInstalling(dep);
        var label = installing ? $"{info.DisplayName}…" : installed ? info.DisplayName : $"{info.DisplayName}  +";

        var scale = ImGuiHelpers.GlobalScale;
        var origin = ImGui.GetCursorScreenPos();
        var textSize = ImGui.CalcTextSize(label);
        var padX = 9f * scale;
        var padY = 3.5f * scale;
        var dotR = 3f * scale;
        var w = MathF.Min(ImGui.GetContentRegionAvail().X, padX * 2f + dotR * 2f + 6f * scale + textSize.X);
        var h = textSize.Y + padY * 2f;
        var end = origin + new Vector2(w, h);
        var hovered = ImGui.IsMouseHoveringRect(origin, end);
        var dl = ImGui.GetWindowDrawList();

        var col = installed ? Styling.AccentMint : installing ? Styling.BrandYellow : (hovered ? Styling.BrandYellow : Styling.TextMuted);
        var bg = installed
            ? Styling.WithAlpha(Styling.AccentMint, 0.12f)
            : installing
                ? Styling.WithAlpha(Styling.BrandYellow, 0.16f)
                : hovered
                    ? Styling.WithAlpha(Styling.BrandPurpleGlow, 0.28f)
                    : Styling.WithAlpha(Styling.CardBgSoft, 0.72f);
        var border = installed
            ? Styling.WithAlpha(Styling.AccentMint, 0.42f)
            : installing || hovered
                ? Styling.WithAlpha(Styling.BrandYellow, 0.65f)
                : Styling.Hairline;

        dl.AddRectFilled(origin, end, ImGui.GetColorU32(bg), h * 0.5f);
        dl.AddRect(origin, end, ImGui.GetColorU32(border), h * 0.5f);
        dl.AddCircleFilled(new Vector2(origin.X + padX + dotR, origin.Y + h * 0.5f), dotR, ImGui.GetColorU32(col));

        ImGui.SetCursorScreenPos(new Vector2(origin.X + padX + dotR * 2f + 6f * scale, origin.Y + padY));
        using (ImRaii.PushColor(ImGuiCol.Text, installed ? Styling.AccentMintSoft : (installing || hovered ? Styling.TextStrong : Styling.TextDim)))
            ImGui.TextUnformatted(label);

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(w, h));

        if (hovered)
        {
            if (!installed && !installing)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    _ = PluginInstaller.Install(dep);
            }

            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                ImGui.SetClipboardText(info.RepoUrl);
                Svc.Chat.Print($"[FateFrenzy] Copied {info.DisplayName} repo URL to clipboard.");
            }

            using (ImRaii.Tooltip())
            {
                using (ImRaii.PushColor(ImGuiCol.Text, Styling.TextStrong))
                    ImGui.TextUnformatted(info.DisplayName);
                using (ImRaii.PushColor(ImGuiCol.Text, Styling.TextDim))
                    ImGui.TextUnformatted(info.Purpose);
                ImGui.Spacing();
                using (ImRaii.PushColor(ImGuiCol.Text, installed ? Styling.AccentMint : Styling.BrandYellow))
                    ImGui.TextUnformatted(installed
                        ? "Installed & loaded (right-click to copy repo URL)"
                        : installing
                            ? "Installing via Dalamud..."
                            : "Click to install via Dalamud (right-click to copy repo URL)");
            }
        }
    }

    private static bool DrawMiniInstallButton(string label, Vector4 accent, bool enabled)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var w = ImGui.GetContentRegionAvail().X;
        var h = 25f * scale;
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + new Vector2(w, h);
        var hovered = enabled && ImGui.IsMouseHoveringRect(origin, end);
        var dl = ImGui.GetWindowDrawList();

        var isYellow = accent == Styling.BrandYellow;
        var bg = !enabled
            ? Styling.WithAlpha(Styling.CardBgSoft, 0.5f)
            : isYellow
                ? (hovered ? Styling.BrandYellowSoft : Styling.BrandYellow)
                : (hovered ? Styling.WithAlpha(accent, 0.45f) : Styling.WithAlpha(accent, 0.25f));
        var border = !enabled ? Styling.Hairline : accent;
        var textCol = !enabled ? Styling.TextMuted : (isYellow ? Styling.WindowBg : Styling.TextStrong);

        dl.AddRectFilled(origin, end, ImGui.GetColorU32(bg), 6f * scale);
        dl.AddRect(origin, end, ImGui.GetColorU32(border), 6f * scale);

        var iconStr = FontAwesomeIcon.Download.ToIconString();
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            iconSize = ImGui.CalcTextSize(iconStr);
        var textSize = ImGui.CalcTextSize(label);
        var totalW = iconSize.X + 6f * scale + textSize.X;
        var startX = origin.X + MathF.Max(8f * scale, (w - totalW) * 0.5f);

        ImGui.SetCursorScreenPos(new Vector2(startX, origin.Y + (h - iconSize.Y) * 0.5f));
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, textCol))
            ImGui.TextUnformatted(iconStr);

        ImGui.SetCursorScreenPos(new Vector2(startX + iconSize.X + 6f * scale, origin.Y + (h - textSize.Y) * 0.5f));
        using (ImRaii.PushColor(ImGuiCol.Text, textCol))
            ImGui.TextUnformatted(label);

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(w, h));

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return true;
        }
        return false;
    }

    private static bool DrawLinkPill(FontAwesomeIcon icon, string label, Vector4 accent)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var w = ImGui.GetContentRegionAvail().X;
        var h = 28f * scale;
        var origin = ImGui.GetCursorScreenPos();
        var end = origin + new Vector2(w, h);
        var hovered = ImGui.IsMouseHoveringRect(origin, end);
        var dl = ImGui.GetWindowDrawList();

        var bg = hovered ? Styling.WithAlpha(accent, 0.28f) : Styling.WithAlpha(accent, 0.14f);
        var border = hovered ? accent : Styling.WithAlpha(accent, 0.40f);
        dl.AddRectFilled(origin, end, ImGui.GetColorU32(bg), h * 0.5f);
        dl.AddRect(origin, end, ImGui.GetColorU32(border), h * 0.5f);

        var iconStr = icon.ToIconString();
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            iconSize = ImGui.CalcTextSize(iconStr);

        var padX = 11f * scale;
        ImGui.SetCursorScreenPos(new Vector2(origin.X + padX, origin.Y + (h - iconSize.Y) * 0.5f));
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, accent))
            ImGui.TextUnformatted(iconStr);

        var textSize = ImGui.CalcTextSize(label);
        ImGui.SetCursorScreenPos(new Vector2(origin.X + padX + 20f * scale, origin.Y + (h - textSize.Y) * 0.5f));
        using (ImRaii.PushColor(ImGuiCol.Text, Styling.TextStrong))
            ImGui.TextUnformatted(label);

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(w, h));

        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return true;
        }
        return false;
    }
}
