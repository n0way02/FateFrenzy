using Dalamud.Game.ClientState.Objects.SubKinds;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace FateFrenzy.Core.VoucherTrading;

public sealed class SharedAccountState
{
    public string CharacterName { get; set; } = string.Empty;
    public uint WorldId { get; set; }
    public string WorldName { get; set; } = string.Empty;
    public MountAutomationRole Role { get; set; } = MountAutomationRole.Solo;
    public uint TerritoryId { get; set; }
    public int VoucherCount { get; set; }
    public bool HasMountInInventory { get; set; }
    public bool IsReadyForTrade { get; set; }
    public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool IsAlive => (DateTime.UtcNow - LastUpdateUtc).TotalSeconds < 60;
}

public static class SharedAccountCoordinator
{
    private static readonly Lock SyncLock = new();
    private static DateTime lastWrittenUtc = DateTime.MinValue;

    private static string SharedFilePath =>
        Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "shared_voucher_accounts.json");

    public static unsafe void Heartbeat(bool isReadyForTrade = false)
    {
        if (!Plugin.Cfg.MountAutomationEnabled) return;
        if ((DateTime.UtcNow - lastWrittenUtc).TotalSeconds < 3 && !isReadyForTrade) return;

        var player = Svc.Objects.LocalPlayer;
        if (player is null) return;

        var charName = player.Name.ToString();
        var worldId = player.HomeWorld.RowId;
        var worldName = player.HomeWorld.ValueNullable?.Name.ToString() ?? string.Empty;
        var territory = Svc.ClientState.TerritoryType;

        var im = InventoryManager.Instance();
        var voucherCount = im != null ? im->GetInventoryItemCount(Plugin.Cfg.MountTargetVoucherId, false, false, false) : 0;
        var hasMount = im != null && im->GetInventoryItemCount(Plugin.Cfg.MountTargetItemId, false, false, false) > 0;

        var myState = new SharedAccountState
        {
            CharacterName = charName,
            WorldId = worldId,
            WorldName = worldName,
            Role = Plugin.Cfg.MountRole,
            TerritoryId = territory,
            VoucherCount = voucherCount,
            HasMountInInventory = hasMount,
            IsReadyForTrade = isReadyForTrade,
            LastUpdateUtc = DateTime.UtcNow
        };

        WriteState(myState);
        lastWrittenUtc = DateTime.UtcNow;
    }

    public static List<SharedAccountState> ReadAllStates()
    {
        lock (SyncLock)
        {
            try
            {
                var file = SharedFilePath;
                if (!File.Exists(file)) return [];

                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                var json = sr.ReadToEnd();
                if (string.IsNullOrWhiteSpace(json)) return [];

                var list = JsonConvert.DeserializeObject<List<SharedAccountState>>(json) ?? [];
                return list.Where(s => s.IsAlive).ToList();
            }
            catch (Exception ex)
            {
                Svc.Log.Debug(ex, "[SharedAccountCoordinator] Failed to read shared states");
                return [];
            }
        }
    }

    private static void WriteState(SharedAccountState state)
    {
        lock (SyncLock)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var file = SharedFilePath;
                    var dir = Path.GetDirectoryName(file);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    var all = ReadAllStates();
                    all.RemoveAll(x => x.CharacterName.Equals(state.CharacterName, StringComparison.OrdinalIgnoreCase)
                                    && x.WorldId == state.WorldId);
                    all.Add(state);

                    var json = JsonConvert.SerializeObject(all, Formatting.Indented);
                    File.WriteAllText(file, json);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    Svc.Log.Warning(ex, "[SharedAccountCoordinator] Failed to write shared state");
                    return;
                }
            }
        }
    }

    public static List<SharedAccountState> GetActivePartners(string partnerNamesList)
    {
        var localName = Svc.Objects.LocalPlayer?.Name.ToString() ?? string.Empty;
        var names = partnerNamesList
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.Trim())
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var all = ReadAllStates();
        return all
            .Where(s => !s.CharacterName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            .Where(s => names.Count == 0 || names.Contains(s.CharacterName))
            .ToList();
    }
}
