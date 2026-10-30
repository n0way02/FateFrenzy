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
    public bool IsAlive => (DateTime.UtcNow - LastUpdateUtc).TotalSeconds < 90;
}

public static class SharedAccountCoordinator
{
    private static readonly Lock SyncLock = new();
    private static DateTime lastWrittenUtc = DateTime.MinValue;

    private static readonly string[] SharedDirectories =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FateFrenzyShared"),
        Path.Combine(Path.GetTempPath(), "FateFrenzyShared"),
        Plugin.PluginInterface.ConfigDirectory.FullName
    ];

    public static string PrimarySharedFilePath =>
        Path.Combine(SharedDirectories[0], "shared_voucher_accounts.json");

    public static unsafe void Heartbeat(bool isReadyForTrade = false)
    {
        if ((DateTime.UtcNow - lastWrittenUtc).TotalSeconds < 2 && !isReadyForTrade) return;

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
            var dictionary = new Dictionary<string, SharedAccountState>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in SharedDirectories)
            {
                try
                {
                    var file = Path.Combine(dir, "shared_voucher_accounts.json");
                    if (!File.Exists(file)) continue;

                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs);
                    var json = sr.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(json)) continue;

                    var list = JsonConvert.DeserializeObject<List<SharedAccountState>>(json);
                    if (list == null) continue;

                    foreach (var state in list)
                    {
                        if (!state.IsAlive) continue;
                        var key = $"{state.CharacterName}@{state.WorldId}";
                        if (!dictionary.TryGetValue(key, out var existing) || state.LastUpdateUtc > existing.LastUpdateUtc)
                        {
                            dictionary[key] = state;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Svc.Log.Debug(ex, $"[SharedAccountCoordinator] Failed reading shared state from {dir}");
                }
            }

            return dictionary.Values.ToList();
        }
    }

    private static void WriteState(SharedAccountState state)
    {
        lock (SyncLock)
        {
            var all = ReadAllStates();
            all.RemoveAll(x => x.CharacterName.Equals(state.CharacterName, StringComparison.OrdinalIgnoreCase)
                            && x.WorldId == state.WorldId);
            all.Add(state);

            var json = JsonConvert.SerializeObject(all, Formatting.Indented);

            foreach (var dir in SharedDirectories)
            {
                try
                {
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    var file = Path.Combine(dir, "shared_voucher_accounts.json");
                    File.WriteAllText(file, json);
                }
                catch (Exception ex)
                {
                    Svc.Log.Debug(ex, $"[SharedAccountCoordinator] Failed writing shared state to {dir}");
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
