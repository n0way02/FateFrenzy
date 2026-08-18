using FateFrenzy.Windows.Components;
using Dalamud.Bindings.ImGui;
using System;

namespace FateFrenzy.Windows.Sections.Config;

internal static class GeneralSettings
{
    public static void Draw(Configuration cfg)
    {
        DrawWindowGroup(cfg);
        DrawBehaviorGroup(cfg);
        DrawCombatGroup(cfg);
        DrawMultiZoneGroup(cfg);
        DrawChocoboGroup(cfg);
    }

    private static void DrawWindowGroup(Configuration cfg)
    {
        using var group = SettingsGroup.Begin("Window");

        SettingsRow.Draw("Open on login",
            "Pop the main window automatically the next time you log in.",
            SettingsControls.ToggleWidth,
            () => SettingsControls.DrawToggle(cfg, () => cfg.AutoShowOnLogin, v => cfg.AutoShowOnLogin = v, "##gen_autoshow"),
            SettingsRow.ToggleHeight);

        SettingsRow.Draw("Live FATE tracker popout",
            "Show the live FATE tracker as a small overlay window so you can keep it visible while the main window is closed.",
            SettingsControls.ToggleWidth,
            () => SettingsControls.DrawToggle(cfg, () => cfg.ShowLivePopout, v =>
            {
                cfg.ShowLivePopout = v;
                Plugin.Instance.LiveFateWindow.IsOpen = v;
            }, "##gen_popout"),
            SettingsRow.ToggleHeight);
    }

    private static void DrawBehaviorGroup(Configuration cfg)
    {
        using var group = SettingsGroup.Begin("Behavior");

        SettingsRow.Draw("Swap zones when empty",
            "When the current zone runs out of eligible FATEs, jump to the next zone in your priority order.",
            SettingsControls.ToggleWidth,
            () => SettingsControls.DrawToggle(cfg, () => cfg.SwapZonesWhenEmpty, v => cfg.SwapZonesWhenEmpty = v, "##gen_swap"),
            SettingsRow.ToggleHeight);

        SettingsRow.Draw("Auto-pause in content",
            "Pause the run while you are inside a duty, trial, raid, or any other instanced content, then resume it once you are back outside. Your zones, goal, and session stats are kept, and paused time does not count toward a time-based goal.",
            SettingsControls.ToggleWidth,
            () => SettingsControls.DrawToggle(cfg, () => cfg.AutoPauseInContent, v => cfg.AutoPauseInContent = v, "##gen_autopause"),
            SettingsRow.ToggleHeight);

        SettingsRow.Draw("Auto-resume on fault",
            "If the grind hits an unrecoverable error and stops, automatically restart it (up to 3 times in 5 minutes) instead of ending the run. Leave off if you want faults to surface.",
            SettingsControls.ToggleWidth,
            () => SettingsControls.DrawToggle(cfg, () => cfg.AutoResumeOnFault, v => cfg.AutoResumeOnFault = v, "##gen_autoresume"),
            SettingsRow.ToggleHeight);
    }

    private static void DrawCombatGroup(Configuration cfg)
    {
        using var group = SettingsGroup.Begin("Combat Solver");

        var rotationOptions = new SettingsControls.Choices.Choice[]
        {
            new("Any", "Let Dalamud process whatever rotation plugin is currently running (default)"),
            new("Wrath", "Enable/disable Wrath of the Righteous combat mod automatically"),
            new("RotationSolver", "Toggle Rotation Solver (RSR) automatic target mode on/off"),
            new("BossMod", "Assert selected BossMod combat preset is active"),
            new("BossModReborn", "Assert selected BossMod Reborn combat preset is active")
        };

        var selectedRotation = cfg.RotationPlugin switch
        {
            "Wrath" => 1,
            "RotationSolver" => 2,
            "BossMod" => 3,
            "BossModReborn" => 4,
            _ => 0
        };

        SettingsRow.Draw("Rotation plugin",
            "Choose which rotation/combat solver plugin FateFrenzy should automatically manage when engaging in combat.",
            SettingsControls.RowComboWidth,
            () => SettingsControls.Choices.DrawCombo("##gen_rotation", rotationOptions, selectedRotation, idx =>
            {
                cfg.RotationPlugin = idx switch
                {
                    1 => "Wrath",
                    2 => "RotationSolver",
                    3 => "BossMod",
                    4 => "BossModReborn",
                    _ => "Any"
                };
                cfg.SaveDebounced();
            }));

        var dodgingOptions = new SettingsControls.Choices.Choice[]
        {
            new("BossModReborn", "Use BossMod Reborn for active AoE dodging (recommended)"),
            new("BossMod", "Use original BossMod for active AoE dodging"),
            new("None", "Disable automated dodging helper")
        };

        var selectedDodging = cfg.DodgingPlugin switch
        {
            "BossMod" => 1,
            "None" => 2,
            _ => 0
        };

        SettingsRow.Draw("AoE Dodging",
            "Select which dodging engine to use for active AoE and mechanic dodging.",
            SettingsControls.RowComboWidth,
            () => SettingsControls.Choices.DrawCombo("##gen_dodging", dodgingOptions, selectedDodging, idx =>
            {
                cfg.DodgingPlugin = idx switch
                {
                    1 => "BossMod",
                    2 => "None",
                    _ => "BossModReborn"
                };
                cfg.SaveDebounced();
            }));
    }

    private static void DrawMultiZoneGroup(Configuration cfg)
    {
        using var group = SettingsGroup.Begin("Multi-Zone & World Rotation");

        SettingsRow.Draw("Multi-Zone Farming",
            "Enable expansion-wide multi-zone farming. FateFrenzy will detect your current expansion and automatically cycle through all zones in that expansion.",
            SettingsControls.ToggleWidth,
            () => SettingsControls.DrawToggle(cfg, () => cfg.EnableMultiZone, v => cfg.EnableMultiZone = v, "##gen_multizone"),
            SettingsRow.ToggleHeight);

        SettingsRow.Draw("Change instances",
            "Cycle instances (Instance 1, 2, 3, etc.) in the zone if no FATEs are found, before swapping zones.",
            SettingsControls.ToggleWidth,
            () => SettingsControls.DrawToggle(cfg, () => cfg.NumberOfInstances > 1, v =>
            {
                cfg.NumberOfInstances = v ? 3 : 1;
            }, "##gen_changeinstances"),
            SettingsRow.ToggleHeight);

        if (cfg.NumberOfInstances > 1)
        {
            SettingsRow.Draw("Number of instances",
                "Max number of instances to rotate through in the zone.",
                SettingsControls.RowSliderWidth,
                () => SettingsControls.DrawIntSlider(cfg, "##gen_numinstances", () => cfg.NumberOfInstances, v => cfg.NumberOfInstances = v, 1, 9));
        }

        SettingsRow.Draw("World Rotation",
            "When cycling through all zones in the expansion, automatically hop to the next world in the rotation list when returning to the first zone.",
            SettingsControls.ToggleWidth,
            () => SettingsControls.DrawToggle(cfg, () => cfg.EnableWorldRotation, v => cfg.EnableWorldRotation = v, "##gen_worldrot"),
            SettingsRow.ToggleHeight);

        if (cfg.EnableWorldRotation)
        {
            SettingsRow.Draw("World list",
                "Comma-separated list of worlds to cycle through.",
                SettingsControls.RowComboWidth,
                () =>
                {
                    var str = cfg.WorldRotationList;
                    ImGui.SetNextItemWidth(SettingsControls.RowComboWidth * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("##gen_worldlist", ref str, 256))
                    {
                        cfg.WorldRotationList = str;
                        cfg.SaveDebounced();
                    }
                });
        }

        SettingsRow.Draw("Blacklisted zones",
            "Comma-separated list of zone names to exclude from multi-zone cycle (e.g. South Horn, North Horn).",
            SettingsControls.RowComboWidth,
            () =>
            {
                var str = cfg.BlacklistedZones;
                ImGui.SetNextItemWidth(SettingsControls.RowComboWidth * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
                if (ImGui.InputText("##gen_blacklistedzones", ref str, 512))
                {
                    cfg.BlacklistedZones = str;
                    cfg.SaveDebounced();
                }
            });
    }

    private static void DrawChocoboGroup(Configuration cfg)
    {
        using var group = SettingsGroup.Begin("Chocobo Companion");

        var stanceOptions = new SettingsControls.Choices.Choice[]
        {
            new("None", "Do not summon the chocobo companion"),
            new("Healer", "Summon chocobo in Healer stance (recommended)"),
            new("Attacker", "Summon chocobo in Attacker stance"),
            new("Defender", "Summon chocobo in Defender stance"),
            new("Free", "Summon chocobo in Free stance"),
            new("Follow", "Summon chocobo in Follow stance")
        };

        var selectedStance = cfg.ChocoboStance switch
        {
            "Healer" => 1,
            "Attacker" => 2,
            "Defender" => 3,
            "Free" => 4,
            "Follow" => 5,
            _ => 0
        };

        SettingsRow.Draw("Chocobo Stance",
            "Choose the combat stance for your summoned Chocobo companion. Setting this to 'None' disables summoning.",
            SettingsControls.RowComboWidth,
            () => SettingsControls.Choices.DrawCombo("##gen_chocobostance", stanceOptions, selectedStance, idx =>
            {
                cfg.ChocoboStance = idx switch
                {
                    1 => "Healer",
                    2 => "Attacker",
                    3 => "Defender",
                    4 => "Free",
                    5 => "Follow",
                    _ => "None"
                };
                cfg.SaveDebounced();
            }));

        if (cfg.ChocoboStance != "None")
        {
            SettingsRow.Draw("Auto-buy Gysahl Greens",
                "Automatically teleport to Bango Zango in Limsa Lominsa to buy more Gysahl Greens if you run out, then return to farming.",
                SettingsControls.ToggleWidth,
                () => SettingsControls.DrawToggle(cfg, () => cfg.BuyGysahlGreens, v => cfg.BuyGysahlGreens = v, "##gen_buygreens"),
                SettingsRow.ToggleHeight);
        }
    }
}
