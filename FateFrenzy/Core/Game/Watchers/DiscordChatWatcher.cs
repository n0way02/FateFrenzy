using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.DalamudServices;
using FateFrenzy.Core.External;

namespace FateFrenzy.Core.Game.Watchers;

internal sealed class DiscordChatWatcher : IDisposable
{
    public DiscordChatWatcher()
    {
        Svc.Chat.ChatMessage += OnChatMessage;
    }

    public void Dispose()
    {
        Svc.Chat.ChatMessage -= OnChatMessage;
    }

    private void OnChatMessage(IHandleableChatMessage chat)
    {
        if (chat.LogKind != XivChatType.TellIncoming) return;

        var cfg = Plugin.Cfg;
        if (!cfg.DiscordNotificationsEnabled || !cfg.DiscordNotifyOnTell) return;
        if (!Plugin.Instance.Controller.Running) return;

        string senderName = "";
        string senderWorld = "";

        var sender = chat.Sender;
        var playerPayload = sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if (playerPayload != null)
        {
            senderName = playerPayload.PlayerName;
            try
            {
                senderWorld = playerPayload.World.ValueNullable?.Name.ExtractText() ?? "";
            }
            catch
            {
                // Fallback
            }
        }

        if (string.IsNullOrEmpty(senderName))
        {
            var raw = sender.TextValue.Trim();
            var parts = raw.Split('@');
            senderName = parts[0].Trim();
            if (parts.Length > 1) senderWorld = parts[1].Trim();
        }

        var messageText = chat.Message.TextValue;
        Svc.Log.Info($"[FateFrenzy] Incoming tell from {senderName}@{senderWorld}: {messageText}");
        DiscordWebhookService.SendTellAlert(senderName, senderWorld, messageText);
    }
}
