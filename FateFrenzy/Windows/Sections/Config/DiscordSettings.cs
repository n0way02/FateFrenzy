using FateFrenzy.Core.External;
using FateFrenzy.Windows.Components;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;
using System.Threading.Tasks;

namespace FateFrenzy.Windows.Sections.Config;

internal static class DiscordSettings
{
    private static string testStatus = string.Empty;
    private static bool isTesting = false;
    private static Vector4 testStatusColor = Styling.AccentMint;
    private static long testStatusExpireTick = 0;

    public static void Draw(Configuration cfg)
    {
        DrawWebhookGroup(cfg);

        if (!cfg.DiscordNotificationsEnabled)
        {
            return;
        }

        DrawTriggersGroup(cfg);
    }

    private static void DrawWebhookGroup(Configuration cfg)
    {
        using var group = SettingsGroup.Begin("Webhook Connection");

        SettingsRow.Draw("Enable Discord notifications",
            "Send notifications directly to your private Discord server or channel via incoming webhook.",
            SettingsControls.ToggleWidth,
            () => SettingsControls.DrawToggle(cfg, () => cfg.DiscordNotificationsEnabled, v => cfg.DiscordNotificationsEnabled = v, "##discord_enabled"),
            SettingsRow.ToggleHeight);

        if (!cfg.DiscordNotificationsEnabled)
        {
            SettingsRow.Note("Discord notifications are off. Enable them above to configure your webhook.");
            return;
        }

        SettingsRow.DrawBlock("Webhook URL",
            "Paste your Discord Channel Webhook URL here (e.g. https://discord.com/api/webhooks/...)",
            () =>
            {
                var url = cfg.DiscordWebhookUrl ?? string.Empty;
                ImGui.SetNextItemWidth(380f * ImGuiHelpers.GlobalScale);
                if (ImGui.InputTextWithHint("##discord_url", "https://discord.com/api/webhooks/...", ref url, 500))
                {
                    cfg.DiscordWebhookUrl = url.Trim();
                    cfg.SaveDebounced();
                }

                ImGui.SameLine();
                using (ImRaii.Disabled(isTesting || string.IsNullOrWhiteSpace(cfg.DiscordWebhookUrl)))
                {
                    if (ImGui.Button(isTesting ? "Testing...##discord_test" : "Test Webhook##discord_test"))
                    {
                        isTesting = true;
                        testStatus = "Sending test message...";
                        testStatusColor = Styling.AccentAmber;
                        testStatusExpireTick = Environment.TickCount64 + 10000;

                        // Safely capture character info on the main UI thread
                        var (charName, charWorld) = DiscordWebhookService.GetLocalPlayerInfo();
                        var targetUrl = (cfg.DiscordWebhookUrl ?? string.Empty).Trim();

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var (ok, err) = await DiscordWebhookService.SendTestMessageAsync(targetUrl, charName, charWorld);
                                if (ok)
                                {
                                    testStatus = "Success! Test alert received in Discord.";
                                    testStatusColor = Styling.AccentMint;
                                }
                                else
                                {
                                    testStatus = $"Failed: {err}";
                                    testStatusColor = Styling.AccentRose;
                                }
                            }
                            catch (Exception ex)
                            {
                                testStatus = $"Error: {ex.Message}";
                                testStatusColor = Styling.AccentRose;
                            }
                            finally
                            {
                                isTesting = false;
                                testStatusExpireTick = Environment.TickCount64 + 5000;
                            }
                        });
                    }
                }

                if (!string.IsNullOrEmpty(testStatus))
                {
                    if (Environment.TickCount64 > testStatusExpireTick && !isTesting)
                    {
                        testStatus = string.Empty;
                    }
                    else
                    {
                        using (ImRaii.PushColor(ImGuiCol.Text, testStatusColor))
                            ImGui.TextUnformatted(testStatus);
                    }
                }
            });
    }

    private static void DrawTriggersGroup(Configuration cfg)
    {
        using var group = SettingsGroup.Begin("Notification Triggers");

        SettingsRow.Draw("Incoming whispers (/tell)",
            "Send an instant alert when another player whispers you. Includes player name, home world, and full message text.",
            SettingsControls.ToggleWidth,
            () => SettingsControls.DrawToggle(cfg, () => cfg.DiscordNotifyOnTell, v => cfg.DiscordNotifyOnTell = v, "##discord_tell"),
            SettingsRow.ToggleHeight);

        SettingsRow.Draw("Party invites",
            "Send an alert when someone invites you to a party. Includes inviter name and home world.",
            SettingsControls.ToggleWidth,
            () => SettingsControls.DrawToggle(cfg, () => cfg.DiscordNotifyOnPartyInvite, v => cfg.DiscordNotifyOnPartyInvite = v, "##discord_party"),
            SettingsRow.ToggleHeight);

        SettingsRow.Draw("Game Master (GM) alert",
            "Urgent notification if a Game Master is detected nearby.",
            SettingsControls.ToggleWidth,
            () => SettingsControls.DrawToggle(cfg, () => cfg.DiscordNotifyOnGmAlert, v => cfg.DiscordNotifyOnGmAlert = v, "##discord_gm"),
            SettingsRow.ToggleHeight);

        SettingsRow.Draw("Grind goal completed",
            "Notification when target Gemstones, FATE count, or Mount Voucher exchange is completed.",
            SettingsControls.ToggleWidth,
            () => SettingsControls.DrawToggle(cfg, () => cfg.DiscordNotifyOnGoalReached, v => cfg.DiscordNotifyOnGoalReached = v, "##discord_goal"),
            SettingsRow.ToggleHeight);

        SettingsRow.Draw("Run stopped / Disconnect",
            "Notification if the automation run terminates or stops.",
            SettingsControls.ToggleWidth,
            () => SettingsControls.DrawToggle(cfg, () => cfg.DiscordNotifyOnRunStopped, v => cfg.DiscordNotifyOnRunStopped = v, "##discord_stopped"),
            SettingsRow.ToggleHeight);
    }
}
