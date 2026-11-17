using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FateFrenzy.Core.Game.Watchers;

// Handles automatic login on game launch and automatic relogin after a disconnect.
// Both features are disabled by default and operate strictly on the Title Screen / Lobby
// when Svc.ClientState.IsLoggedIn is false.
internal sealed unsafe class AutoLoginWatcher : IDisposable
{
    private const long ActionCooldownMs = 1500;

    private long nextActionTick = 0;
    private long nextWorldSwitchTick = 0;
    private bool wasLoggedIn = false;
    private bool hasLoggedInOnceThisLaunch = false;
    private bool pendingReloginFromDisconnect = false;
    private bool dcSelectedThisAttempt = false;

    private string lastCharName = string.Empty;
    private string lastCharWorld = string.Empty;

    public AutoLoginWatcher()
    {
        Svc.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Svc.Framework.Update -= OnUpdate;
    }

    private void OnUpdate(IFramework _)
    {
        var cfg = Plugin.Cfg;
        var isLoggedIn = Svc.ClientState.IsLoggedIn;

        if (isLoggedIn)
        {
            hasLoggedInOnceThisLaunch = true;
            pendingReloginFromDisconnect = false;
            dcSelectedThisAttempt = false;

            if (!wasLoggedIn)
                wasLoggedIn = true;

            var player = Svc.Objects.LocalPlayer;
            if (player is not null)
            {
                lastCharName = player.Name.TextValue;
                lastCharWorld = player.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;
            }
            return;
        }

        // Transition: Logged in -> Logged out / Disconnected
        if (wasLoggedIn)
        {
            wasLoggedIn = false;
            if (cfg.WasRunningBeforeDisconnect || Plugin.Instance.Controller.SessionSnapshot is not null)
                pendingReloginFromDisconnect = true;
        }

        var wantLaunchLogin = cfg.AutoLoginOnLaunch && !hasLoggedInOnceThisLaunch;
        var hasErrorPopup = IsDisconnectPopupOpen();
        var wantRelogin = cfg.AutoReloginOnDisconnect && (pendingReloginFromDisconnect || hasErrorPopup);

        if (!wantLaunchLogin && !wantRelogin)
            return;

        var now = Environment.TickCount64;
        if (now < nextActionTick)
            return;

        TryAdvanceLogin(cfg, now);
    }

    private static bool IsDisconnectPopupOpen()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Dialogue", out var dlg) && GenericHelpers.IsAddonReady(dlg))
            return true;

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectOk", out var ok) && GenericHelpers.IsAddonReady(ok))
        {
            var text = new AddonMaster.SelectOk(ok).Text;
            if (!IsQueuePrompt(text))
                return true;
        }

        return false;
    }

    private static bool IsQueuePrompt(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("queue", StringComparison.OrdinalIgnoreCase)
            || text.Contains("fila", StringComparison.OrdinalIgnoreCase);
    }

    private (string name, string world) ResolveTargetCharacter(Configuration cfg)
    {
        if (pendingReloginFromDisconnect && !string.IsNullOrEmpty(lastCharName))
            return (lastCharName, lastCharWorld);

        return (cfg.AutoLoginCharacterName?.Trim() ?? string.Empty, cfg.AutoLoginWorldName?.Trim() ?? string.Empty);
    }

    private void TryAdvanceLogin(Configuration cfg, long now)
    {
        try
        {
            // 1. Dismiss disconnect error popups (Dialogue / SelectOk)
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Dialogue", out var dlg) && GenericHelpers.IsAddonReady(dlg))
            {
                Svc.Log.Info("[FateFrenzy] AutoLogin: dismissing Dialogue error popup.");
                new AddonMaster.Dialogue(dlg).Ok();
                nextActionTick = now + ActionCooldownMs;
                return;
            }

            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectOk", out var ok) && GenericHelpers.IsAddonReady(ok))
            {
                var okMaster = new AddonMaster.SelectOk(ok);
                if (!IsQueuePrompt(okMaster.Text))
                {
                    Svc.Log.Info("[FateFrenzy] AutoLogin: dismissing SelectOk popup.");
                    okMaster.Ok();
                    nextActionTick = now + ActionCooldownMs;
                }
                return;
            }

            // Wait quietly if lobby connection is currently in progress
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("TitleConnect", out _))
                return;

            var (targetName, targetWorld) = ResolveTargetCharacter(cfg);

            // 2. Data Center selection screen (TitleDCWorldMap)
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("TitleDCWorldMap", out var dcAddon) && GenericHelpers.IsAddonReady(dcAddon))
            {
                if (!string.IsNullOrEmpty(targetWorld) && ExcelWorldHelper.TryGet(targetWorld, out var wInfo) && wInfo.DataCenter.RowId > 0)
                {
                    var dcId = (int)wInfo.DataCenter.RowId;
                    var dcName = wInfo.DataCenter.ValueNullable?.Name.ToString() ?? dcId.ToString();
                    Svc.Log.Info($"[FateFrenzy] AutoLogin: selecting Data Center {dcName} (id={dcId}) for world {targetWorld}.");
                    new AddonMaster.TitleDCWorldMap(dcAddon).Select(dcId);
                    dcSelectedThisAttempt = true;
                    nextActionTick = now + 2000;
                }
                return;
            }

            // 3. Title Screen: select Data Center first if world is configured, then click Start
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("_TitleMenu", out var titleAddon))
            {
                var title = new AddonMaster._TitleMenu(titleAddon);
                if (title.IsReady)
                {
                    if (!dcSelectedThisAttempt && !string.IsNullOrEmpty(targetWorld) && ExcelWorldHelper.TryGet(targetWorld, out var wInfo) && wInfo.DataCenter.RowId > 0)
                    {
                        Svc.Log.Info($"[FateFrenzy] AutoLogin: opening Data Center menu for world {targetWorld}.");
                        title.DataCenter();
                        dcSelectedThisAttempt = true;
                        nextActionTick = now + 1500;
                        return;
                    }

                    Svc.Log.Info("[FateFrenzy] AutoLogin: clicking Start on Title Menu.");
                    title.Start();
                    nextActionTick = now + 2000;
                }
                return;
            }

            // 4. Character Select confirmation ("Log in with <Name>?")
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var ynAddon) && GenericHelpers.IsAddonReady(ynAddon))
            {
                Svc.Log.Info("[FateFrenzy] AutoLogin: confirming character login (SelectYesno Yes).");
                new AddonMaster.SelectYesno(ynAddon).Yes();
                nextActionTick = now + 2500;
                return;
            }

            // 5. World Selection popup (_CharaSelectWorldServer)
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("_CharaSelectWorldServer", out var worldAddon) && GenericHelpers.IsAddonReady(worldAddon))
            {
                if (!string.IsNullOrEmpty(targetWorld))
                {
                    var worldMenu = new AddonMaster._CharaSelectWorldServer(worldAddon);
                    var match = worldMenu.Worlds.FirstOrDefault(w => w.Name.Equals(targetWorld, StringComparison.OrdinalIgnoreCase));
                    if (match is not null)
                    {
                        Svc.Log.Info($"[FateFrenzy] AutoLogin: selecting world {match.Name}.");
                        match.Select();
                        nextActionTick = now + 2000;
                        return;
                    }
                    Svc.Log.Warning($"[FateFrenzy] AutoLogin: configured world '{targetWorld}' not found in world list.");
                }
                return;
            }

            // 5. Character Select List: select and log in
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("_CharaSelectListMenu", out var charaAddon) && GenericHelpers.IsAddonReady(charaAddon))
            {
                var menu = new AddonMaster._CharaSelectListMenu(charaAddon);
                if (menu.TemporarilyLocked) return;

                var chars = menu.Characters;
                var target = FindCharacter(chars, targetName, targetWorld);

                if (target is null)
                {
                    // If character not on currently displayed world and user specified a world, switch worlds
                    if (!string.IsNullOrEmpty(targetWorld) && now >= nextWorldSwitchTick)
                    {
                        Svc.Log.Info($"[FateFrenzy] AutoLogin: '{targetName}' not on current world list; opening World selection for {targetWorld}.");
                        menu.World();
                        nextWorldSwitchTick = now + 4000;
                        nextActionTick = now + 1500;
                    }
                    return;
                }

                if (!target.IsSelected)
                {
                    Svc.Log.Info($"[FateFrenzy] AutoLogin: selecting character {target.Name} ({ExcelWorldHelper.GetName(target.HomeWorld)}).");
                    target.Select();
                    nextActionTick = now + 1000;
                    return;
                }

                if (target.CanLoginNormally)
                {
                    Svc.Log.Info($"[FateFrenzy] AutoLogin: logging in as {target.Name}.");
                    target.Login();
                    nextActionTick = now + 2000;
                }
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[FateFrenzy] AutoLogin: error while advancing login.");
            nextActionTick = now + 2500;
        }
    }

    private static AddonMaster._CharaSelectListMenu.Character? FindCharacter(
        AddonMaster._CharaSelectListMenu.Character[] chars,
        string targetName,
        string targetWorld)
    {
        if (chars.Length == 0) return null;

        if (!string.IsNullOrEmpty(targetName))
        {
            foreach (var c in chars)
            {
                if (!c.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(targetWorld))
                {
                    var homeName = ExcelWorldHelper.GetName(c.HomeWorld);
                    if (!homeName.Equals(targetWorld, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                return c;
            }
            return null;
        }

        // If no character name specified, fall back to first character on the current world
        if (!string.IsNullOrEmpty(targetWorld))
        {
            var firstOnWorld = chars.FirstOrDefault(c =>
                ExcelWorldHelper.GetName(c.HomeWorld).Equals(targetWorld, StringComparison.OrdinalIgnoreCase));
            return firstOnWorld;
        }

        return chars[0];
    }
}
