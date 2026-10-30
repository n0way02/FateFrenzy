using ECommons.DalamudServices;
using FateFrenzy.Core.VoucherTrading;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace FateFrenzy.Core.Tasks;

public sealed partial class AutoFate
{
    private static long lastUndercutCheckMs;

    private unsafe bool TryQueueMountAutomation()
    {
        if (!Plugin.Cfg.MountAutomationEnabled) return false;

        SharedAccountCoordinator.Heartbeat();

        var im = InventoryManager.Instance();
        if (im == null) return false;

        var whistleCount = im->GetInventoryItemCount(Plugin.Cfg.MountTargetItemId, false, false, false);
        var voucherCount = im->GetInventoryItemCount(Plugin.Cfg.MountTargetVoucherId, false, false, false);

        // 1. Whistle in inventory -> list it on Retainer
        if (whistleCount > 0)
        {
            Diag($"Mount Whistle (ID {Plugin.Cfg.MountTargetItemId}) in inventory; queueing Retainer listing.");
            session.PendingRetainerSell = true;
            session.PendingRetainerUndercutOnly = false;
            session.PendingRetainerSellFromZone = zone;
            return true;
        }

        // 2. Master Seller or Solo reached 500 vouchers -> go exchange at Edelina in Mor Dhona
        if (Plugin.Cfg.MountRole is MountAutomationRole.MasterSeller or MountAutomationRole.Solo)
        {
            if (voucherCount >= Plugin.Cfg.MountBuyerThreshold)
            {
                Diag($"Vouchers ({voucherCount}/{Plugin.Cfg.MountBuyerThreshold}) reached; queueing mount exchange at Edelina.");
                session.PendingMountExchange = true;
                session.PendingMountExchangeFromZone = zone;
                return true;
            }
        }

        // 3. Coordination for Handoff:
        // A) If I am Supplier and I have >= threshold vouchers:
        if (Plugin.Cfg.MountRole == MountAutomationRole.Supplier)
        {
            if (voucherCount >= Plugin.Cfg.MountSupplierThreshold)
            {
                var partners = SharedAccountCoordinator.GetActivePartners(Plugin.Cfg.MountPartnerNames);
                var master = partners.FirstOrDefault(p => p.Role == MountAutomationRole.MasterSeller);
                if (master != null)
                {
                    Diag($"Supplier vouchers ({voucherCount}/{Plugin.Cfg.MountSupplierThreshold}) reached; queueing trade with Master '{master.CharacterName}'.");
                    session.PendingVoucherHandoff = true;
                    session.PendingVoucherHandoffPartner = master.CharacterName;
                    session.PendingVoucherHandoffFromZone = zone;
                    return true;
                }
            }
        }

        // B) If I am MasterSeller and a Supplier is ready to deliver vouchers:
        if (Plugin.Cfg.MountRole == MountAutomationRole.MasterSeller)
        {
            var partners = SharedAccountCoordinator.GetActivePartners(Plugin.Cfg.MountPartnerNames);
            var readySupplier = partners.FirstOrDefault(p => p.Role == MountAutomationRole.Supplier &&
                (p.IsReadyForTrade || p.VoucherCount >= Plugin.Cfg.MountSupplierThreshold));

            if (readySupplier != null)
            {
                Diag($"MasterSeller detected Supplier '{readySupplier.CharacterName}' ready with {readySupplier.VoucherCount} vouchers; queueing trade meeting at Mor Dhona.");
                session.PendingVoucherHandoff = true;
                session.PendingVoucherHandoffPartner = readySupplier.CharacterName;
                session.PendingVoucherHandoffFromZone = zone;
                return true;
            }
        }

        // 4. Undercut check for Master Seller or Solo
        if (Plugin.Cfg.MountRole is MountAutomationRole.MasterSeller or MountAutomationRole.Solo)
        {
            if (ShouldCheckUndercut())
            {
                lastUndercutCheckMs = Environment.TickCount64;
                _ = TriggerUndercutCheckAsync();
            }
        }

        return false;
    }

    private static bool ShouldCheckUndercut()
    {
        var intervalMs = Math.Max(1, Plugin.Cfg.MountUndercutCheckIntervalMinutes) * 60_000;
        return (Environment.TickCount64 - lastUndercutCheckMs) > intervalMs;
    }

    private async Task TriggerUndercutCheckAsync()
    {
        try
        {
            var p = Svc.Objects.LocalPlayer;
            if (p == null) return;

            var homeWorld = p.HomeWorld.ValueNullable?.Name.ToString();
            if (string.IsNullOrEmpty(homeWorld)) return;

            var market = await UniversalisPriceService.GetListingsAsync(homeWorld, Plugin.Cfg.MountTargetItemId, forceRefresh: true);
            if (market == null || market.Listings.Count == 0) return;

            // Check if lowest price on world is below our initial price and above our floor
            var targetPrice = UniversalisPriceService.CalculateUndercutPrice(
                market,
                Plugin.Cfg.MountInitialPrice,
                Plugin.Cfg.MountFloorPrice,
                Plugin.Cfg.MountUndercutAmount);

            // If calculated price is different and below initial, someone undercut us safely above floor!
            if (targetPrice < Plugin.Cfg.MountInitialPrice && targetPrice >= Plugin.Cfg.MountFloorPrice)
            {
                Diag($"Competitor undercut detected on {homeWorld}! New anti-bait target price: {targetPrice:N0} gil.");
                session.PendingRetainerSell = true;
                session.PendingRetainerUndercutOnly = true;
                session.PendingRetainerSellFromZone = zone;
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[AutoFate.MountAutomation] Error during background undercut check");
        }
    }
}

