using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ECommons.DalamudServices;

namespace FateFrenzy.Core.External;

internal static class DiscordWebhookService
{
    private static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const string DefaultAvatar = "https://raw.githubusercontent.com/n0way02/FateFrenzy/master/FateFrenzy/Images/Icon.png";

    public static async Task<(bool success, string error)> SendEmbedAsync(string webhookUrl, string title, string description, int color, List<(string Name, string Value, bool Inline)>? fields = null)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            return (false, "Webhook URL is empty");

        try
        {
            var embed = new Dictionary<string, object>
            {
                ["title"] = title,
                ["description"] = description,
                ["color"] = color,
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["footer"] = new Dictionary<string, string>
                {
                    ["text"] = "FateFrenzy Telemetry & Alerts"
                }
            };

            if (fields != null && fields.Count > 0)
            {
                var fieldList = new List<Dictionary<string, object>>();
                foreach (var (name, value, inline) in fields)
                {
                    fieldList.Add(new Dictionary<string, object>
                    {
                        ["name"] = name,
                        ["value"] = string.IsNullOrWhiteSpace(value) ? "-" : value,
                        ["inline"] = inline
                    });
                }
                embed["fields"] = fieldList;
            }

            var payload = new Dictionary<string, object>
            {
                ["username"] = "FateFrenzy",
                ["avatar_url"] = DefaultAvatar,
                ["embeds"] = new[] { embed }
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(webhookUrl.Trim(), content);

            if (response.IsSuccessStatusCode)
                return (true, string.Empty);

            var respBody = await response.Content.ReadAsStringAsync();
            return (false, $"HTTP {(int)response.StatusCode}: {respBody}");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[FateFrenzy] Failed to send Discord webhook.");
            return (false, ex.Message);
        }
    }

    public static void FireAndForget(Func<Task> asyncAction)
    {
        _ = Task.Run(async () =>
        {
            try { await asyncAction(); }
            catch (Exception ex) { Svc.Log.Warning(ex, "[FateFrenzy] Discord webhook background task error."); }
        });
    }

    private static (string name, string world) GetLocalPlayerInfo()
    {
        var player = Svc.Objects.LocalPlayer;
        if (player is null) return ("Unknown", "Unknown");
        var pName = player.Name.TextValue;
        var pWorld = player.HomeWorld.ValueNullable?.Name.ExtractText() ?? player.CurrentWorld.ValueNullable?.Name.ExtractText() ?? "";
        return (pName, pWorld);
    }

    public static void SendTellAlert(string senderName, string senderWorld, string message)
    {
        var cfg = Plugin.Cfg;
        if (!cfg.DiscordNotificationsEnabled || !cfg.DiscordNotifyOnTell) return;
        var (localName, localWorld) = GetLocalPlayerInfo();

        var senderFull = string.IsNullOrEmpty(senderWorld) ? senderName : $"{senderName} ({senderWorld})";
        var localFull = string.IsNullOrEmpty(localWorld) ? localName : $"{localName} ({localWorld})";

        FireAndForget(async () =>
        {
            var fields = new List<(string, string, bool)>
            {
                ("👤 Sender", senderFull, true),
                ("🎯 Target Character", localFull, true),
                ("💬 Message", $"```{message}```", false),
            };

            await SendEmbedAsync(cfg.DiscordWebhookUrl, "📩 Incoming Whisper (/tell)", $"You received a private message while grinding!", 0xF59E0B, fields);
        });
    }

    public static void SendPartyInviteAlert(string inviterName, string inviterWorld)
    {
        var cfg = Plugin.Cfg;
        if (!cfg.DiscordNotificationsEnabled || !cfg.DiscordNotifyOnPartyInvite) return;
        var (localName, localWorld) = GetLocalPlayerInfo();

        var inviterFull = string.IsNullOrEmpty(inviterWorld) ? inviterName : $"{inviterName} ({inviterWorld})";
        var localFull = string.IsNullOrEmpty(localWorld) ? localName : $"{localName} ({localWorld})";

        FireAndForget(async () =>
        {
            var fields = new List<(string, string, bool)>
            {
                ("👥 Inviter", inviterFull, true),
                ("🎯 Target Character", localFull, true),
                ("⚙️ Auto-Decline", cfg.DeclinePartyInvites ? "Enabled (Decline queued)" : "Disabled (Pending manually)", false)
            };

            await SendEmbedAsync(cfg.DiscordWebhookUrl, "💌 Party Invite Received", $"A player invited you to a party!", 0x8B5CF6, fields);
        });
    }

    public static void SendGmAlert(string gmName, string zoneName)
    {
        var cfg = Plugin.Cfg;
        if (!cfg.DiscordNotificationsEnabled || !cfg.DiscordNotifyOnGmAlert) return;
        var (localName, localWorld) = GetLocalPlayerInfo();

        FireAndForget(async () =>
        {
            var fields = new List<(string, string, bool)>
            {
                ("🚨 GM Name", gmName, true),
                ("🗺️ Location", zoneName, true),
                ("👤 Character", $"{localName} ({localWorld})", true)
            };

            await SendEmbedAsync(cfg.DiscordWebhookUrl, "⚠️ GAME MASTER DETECTED!", "A Game Master was detected near your character!", 0xEF4444, fields);
        });
    }

    public static void SendGoalReachedAlert(string summary, int gems, int fates, TimeSpan duration)
    {
        var cfg = Plugin.Cfg;
        if (!cfg.DiscordNotificationsEnabled || !cfg.DiscordNotifyOnGoalReached) return;
        var (localName, localWorld) = GetLocalPlayerInfo();

        FireAndForget(async () =>
        {
            var fields = new List<(string, string, bool)>
            {
                ("👤 Character", $"{localName} ({localWorld})", true),
                ("⏱️ Duration", $"{Math.Floor(duration.TotalHours):00}h {duration.Minutes:00}m {duration.Seconds:00}s", true),
                ("⚔️ FATEs Done", fates.ToString(), true),
                ("💎 Gemstones", gems.ToString(), true),
                ("🏁 Stop Reason", summary, false)
            };

            await SendEmbedAsync(cfg.DiscordWebhookUrl, "🏆 Grind Goal Completed!", "The automated FATE run has reached its target condition.", 0x10B981, fields);
        });
    }

    public static void SendRunStoppedAlert(string reason)
    {
        var cfg = Plugin.Cfg;
        if (!cfg.DiscordNotificationsEnabled || !cfg.DiscordNotifyOnRunStopped) return;
        var (localName, localWorld) = GetLocalPlayerInfo();

        FireAndForget(async () =>
        {
            var fields = new List<(string, string, bool)>
            {
                ("👤 Character", $"{localName} ({localWorld})", true),
                ("ℹ️ Reason", reason, false)
            };

            await SendEmbedAsync(cfg.DiscordWebhookUrl, "⏹️ Grind Run Stopped", "The FATE run was stopped or encountered an issue.", 0x64748B, fields);
        });
    }

    public static async Task<(bool success, string error)> SendTestMessageAsync(string webhookUrl)
    {
        var (localName, localWorld) = GetLocalPlayerInfo();
        var fields = new List<(string, string, bool)>
        {
            ("Status", "Connected & Operational ✅", true),
            ("Character", $"{localName} ({localWorld})", true),
            ("Plugin Version", "FateFrenzy", true)
        };

        return await SendEmbedAsync(webhookUrl, "🔔 FateFrenzy Discord Alerts Configured!", "Your Discord webhook is working properly. You will receive notifications based on your chosen settings.", 0x06B6D4, fields);
    }
}
