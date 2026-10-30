using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using ECommons.DalamudServices;
using FateFrenzy.Core.VoucherTrading;
using FateFrenzy.Windows.Components;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Linq;

namespace FateFrenzy.Windows.Sections.Config;

internal static class VoucherMountSettings
{
    private static readonly SettingsControls.Choices.Choice[] roleChoices =
    [
        new("Solo (Single Account)", "Farms 500 vouchers alone, exchanges for mount at Edelina, and lists it on your Retainer."),
        new("Supplier (Voucher Sender)", "Farms vouchers until reaching threshold (e.g. 250), then meets Master Seller to trade."),
        new("Master Seller (Voucher Receiver)", "Receives vouchers from Suppliers, buys mount at 500 vouchers, and lists on Retainer."),
    ];

    public static void Draw(Configuration cfg)
    {
        DrawTriggerGroup(cfg);
        if (!cfg.MountAutomationEnabled) return;

        DrawRoleGroup(cfg);
        DrawTargetItemGroup(cfg);
        DrawPricingGroup(cfg);
        DrawRetainerGroup(cfg);
    }

    private static void DrawTriggerGroup(Configuration cfg)
    {
        using var group = SettingsGroup.Begin("Mount & Market Automation");

        SettingsRow.Draw("Enable Voucher & Mount Automation",
            "Automates voucher exchange for Mount Whistles, multi-account trading, and Retainer Market Board listing with Anti-Bait undercut protection.",
            SettingsControls.ToggleWidth,
            () => SettingsControls.DrawToggle(cfg, () => cfg.MountAutomationEnabled, v => cfg.MountAutomationEnabled = v, "##mount_enabled"),
            SettingsRow.ToggleHeight);

        if (!cfg.MountAutomationEnabled)
        {
            SettingsRow.Note("Feature is disabled by default. Toggle on to configure multi-account and market automation.");
        }
    }

    private static void DrawRoleGroup(Configuration cfg)
    {
        using var group = SettingsGroup.Begin("Account Role & Multi-Account");

        var roleIndex = Math.Clamp((int)cfg.MountRole, 0, roleChoices.Length - 1);
        SettingsRow.Draw("Automation Role", "Select the responsibility of this game client.",
            220f * ImGuiHelpers.GlobalScale,
            () => SettingsControls.Choices.DrawCombo("##mount_role", roleChoices, roleIndex, idx =>
            {
                cfg.MountRole = (MountAutomationRole)idx;
                cfg.SaveDebounced();
            }));
        SettingsRow.Caption(roleChoices[roleIndex].Detail);

        if (cfg.MountRole == MountAutomationRole.Supplier)
        {
            SettingsRow.Draw("Supplier Transfer Threshold",
                "Number of vouchers this character farms before pausing FATEs to hand them off to the Master Seller.",
                SettingsControls.RowSliderWidth,
                () => SettingsControls.DrawIntSlider(cfg, "##supp_threshold",
                    () => cfg.MountSupplierThreshold, v => cfg.MountSupplierThreshold = Math.Clamp(v, 10, 500),
                    10, 500, "%d vouchers"));
        }
        else if (cfg.MountRole == MountAutomationRole.MasterSeller)
        {
            SettingsRow.Draw("Master Exchange Threshold",
                "Number of vouchers required before travelling to Edelina in Mor Dhona to exchange for the Mount Whistle.",
                SettingsControls.RowSliderWidth,
                () => SettingsControls.DrawIntSlider(cfg, "##buyer_threshold",
                    () => cfg.MountBuyerThreshold, v => cfg.MountBuyerThreshold = Math.Clamp(v, 100, 500),
                    100, 500, "%d vouchers"));
        }

        if (cfg.MountRole != MountAutomationRole.Solo)
        {
            SettingsRow.DrawBlock("Partner Character Names",
                "Names of your other characters running on this PC (comma or semicolon separated). Leave empty to match any active local partner.",
                () =>
                {
                    var names = cfg.MountPartnerNames;
                    ImGui.SetNextItemWidth(350f * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("##partner_names", ref names, 256))
                    {
                        cfg.MountPartnerNames = names;
                        cfg.SaveDebounced();
                    }
                });

            DrawPartnerStatusBox(cfg);
        }
    }

    private static unsafe void DrawPartnerStatusBox(Configuration cfg)
    {
        SharedAccountCoordinator.Heartbeat();

        var im = InventoryManager.Instance();
        var myVouchers = im != null ? im->GetInventoryItemCount(cfg.MountTargetVoucherId, false, false, false) : 0;
        var myWhistle = im != null && im->GetInventoryItemCount(cfg.MountTargetItemId, false, false, false) > 0;

        ImGui.Spacing();
        ImGui.TextColored(Styling.AccentViolet, "Active Multi-Account Status on this PC:");
        ImGui.TextDisabled($"This Character: {myVouchers} vouchers | Whistle in bags: {(myWhistle ? "YES" : "No")}");

        var partners = SharedAccountCoordinator.GetActivePartners(cfg.MountPartnerNames);
        if (partners.Count == 0)
        {
            ImGui.TextDisabled("No other game client instances detected on this PC.");
        }
        else
        {
            foreach (var partner in partners)
            {
                var readyText = partner.IsReadyForTrade ? " [READY AT MEETING SPOT]" : "";
                ImGui.BulletText($"{partner.CharacterName} ({partner.WorldName}) - Role: {partner.Role} - Vouchers: {partner.VoucherCount}{readyText}");
            }
        }
    }

    private static void DrawTargetItemGroup(Configuration cfg)
    {
        using var group = SettingsGroup.Begin("Target Exchange Item");

        SettingsRow.Note("Mount: Ty'aitya Whistle (ID: 43590)\nCurrency: Turali Bicolor Gemstone Voucher (ID: 43961, 500 required at Edelina, Mor Dhona)");
    }

    private static void DrawPricingGroup(Configuration cfg)
    {
        using var group = SettingsGroup.Begin("Market Board & Anti-Bait Pricing");

        SettingsRow.DrawBlock("Initial Listing Price (Gil)",
            "The initial price you want to list the mount for when first putting it up for sale.",
            () =>
            {
                var price = (int)Math.Clamp(cfg.MountInitialPrice, 1, int.MaxValue);
                ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
                if (ImGui.InputInt("##init_price", ref price, 100_000, 1_000_000))
                {
                    cfg.MountInitialPrice = Math.Max(1, price);
                    cfg.SaveDebounced();
                }
            });

        SettingsRow.DrawBlock("Floor Price / Anti-Bait Limit (Gil)",
            "Minimum price boundary. If a competitor undercuts down to troll prices (e.g. 30M), the bot ignores the bait and preserves your listing. Undercuts only occur above this floor.",
            () =>
            {
                var floor = (int)Math.Clamp(cfg.MountFloorPrice, 1, int.MaxValue);
                ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
                if (ImGui.InputInt("##floor_price", ref floor, 100_000, 1_000_000))
                {
                    cfg.MountFloorPrice = Math.Max(1, floor);
                    cfg.SaveDebounced();
                }
            });

        SettingsRow.Draw("Undercut Amount (Gil)",
            "How much gil to undercut valid competitors by (e.g. 1 gil or 100 gil).",
            SettingsControls.RowSliderWidth,
            () => SettingsControls.DrawIntSlider(cfg, "##undercut_amt",
                () => (int)cfg.MountUndercutAmount, v => cfg.MountUndercutAmount = Math.Clamp(v, 1, 100_000),
                1, 10_000, "%d gil"));

        SettingsRow.Draw("Price Check Interval",
            "How frequently the bot queries Universalis in the background while FATE farming to check for competitor undercuts.",
            SettingsControls.RowSliderWidth,
            () => SettingsControls.DrawIntSlider(cfg, "##check_interval",
                () => cfg.MountUndercutCheckIntervalMinutes, v => cfg.MountUndercutCheckIntervalMinutes = Math.Clamp(v, 5, 120),
                5, 120, "%d min"));
    }

    private static void DrawRetainerGroup(Configuration cfg)
    {
        using var group = SettingsGroup.Begin("Retainer Settings");

        SettingsRow.Draw("Retainer Index",
            "Which Retainer to use for listing the mount (0 is first retainer, 1 is second retainer, etc.).",
            SettingsControls.RowSliderWidth,
            () => SettingsControls.DrawIntSlider(cfg, "##retainer_idx",
                () => cfg.MountRetainerIndex, v => cfg.MountRetainerIndex = Math.Clamp(v, 0, 9),
                0, 9, "Retainer #%d"));

        SettingsRow.Draw("Use Private Inn Room",
            "Retires to your Grand Company Inn room before accessing the Summoning Bell. Highly recommended for privacy away from other players.",
            SettingsControls.ToggleWidth,
            () => SettingsControls.DrawToggle(cfg, () => cfg.MountUsePrivateInn, v => cfg.MountUsePrivateInn = v, "##inn_bell"),
            SettingsRow.ToggleHeight);
    }
}
