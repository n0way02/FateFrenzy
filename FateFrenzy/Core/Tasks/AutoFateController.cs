using FateFrenzy.Core.External;
using FateFrenzy.Core.Game.Player;
using FateFrenzy.Core.Trading;
using FateFrenzy.Core.Zones;
using FateFrenzy.Core.Ipc;
using clib.Services;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Game;
using System.Text.Json;
using System.Threading.Tasks;

namespace FateFrenzy.Core.Tasks;

internal sealed partial class AutoFateController
{
    public bool Running => Svc.Automation.Running || Paused;

    public string Status => PauseReason switch
    {
        PauseReason.InContent => "Paused while you are in content",
        PauseReason.Manual    => "Paused",
        _                     => Svc.Automation.CurrentTask?.Status ?? "Idle",
    };

    public AutoPhase Phase { get; private set; } = AutoPhase.Idle;
    public bool WasStoppedManually { get; set; } = false;

    private AutoFateSession? session;
    private IReadOnlyList<ZoneInfo> activeZones = [];
    public AutoFateSession? SessionSnapshot => session;

    private static readonly Random rng = new();

    public AutoFateController()
    {
        EnsureTelemetryWorker();
    }

    private static void Diag(string message)
        => ECommons.DalamudServices.Svc.Log.Info($"{AfgConstants.LogPrefix} {message}");

    // First active-zone index whose territory matches origin (first match wins), or fallback when origin is
    // null / not in the current selection.
    private int ResumeIndexFor(ZoneInfo? origin, int fallback = 0)
    {
        if (origin is null) return fallback;
        for (var i = 0; i < activeZones.Count; i++)
            if (activeZones[i].TerritoryId == origin.TerritoryId) return i;
        return fallback;
    }

    public void RunAll(IEnumerable<ZoneInfo> zones)
    {
        WasStoppedManually = false;
        var list = zones.ToList();

        if (Plugin.Cfg.RelicModeEnabled)
        {
            var incomplete = list.Where(z => !RelicItemResolver.TerritoryToItemName.ContainsKey(z.TerritoryId) || !RelicItemResolver.IsComplete(z.TerritoryId)).ToList();
            if (incomplete.Count > 0)
            {
                list = incomplete;
            }
        }

        activeZones = list;
        if (activeZones.Count == 0)
        {
            Diag("Start aborted: no zones selected (or all selected relic zones are already completed).");
            ECommons.DalamudServices.Svc.Chat.PrintError("[FateFrenzy] Start aborted: all selected relic zones are already completed.");
            return;
        }

        if (!ExternalPlugins.AllRequiredInstalled())
        {
            var missing = string.Join(", ", ExternalPlugins.All
                .Where(p => ExternalPlugins.Catalog[p].Required && !ExternalPlugins.IsInstalled(p))
                .Select(p => ExternalPlugins.Catalog[p].DisplayName));
            Diag($"Start aborted: required plugins missing ({missing}).");
            ECommons.DalamudServices.Svc.Chat.PrintError($"[FateFrenzy] Cannot start — install all required plugins first: {missing}.");
            return;
        }

        PauseReason = PauseReason.None;

        Plugin.Cfg.WasRunningBeforeDisconnect = true;
        Plugin.Cfg.Save();

        var startWallet = GemstoneCatalog.CurrentWalletCount();
        var s = new AutoFateSession
        {
            GemstoneCurrent = startWallet,
        };
        s.CaptureStartExp();
        session = s;
        Diag($"Run starting: {activeZones.Count} zone(s), mode {Plugin.Cfg.ActiveMode.DisplayName}, wallet {startWallet}g, threshold {Plugin.Cfg.TradeThreshold}g, trade-on-cap {(Plugin.Cfg.TradeOnCap ? "on" : "off")}.");

        ApplyStartingClass();
        StartFateGrind(0, s);

        if (ExternalPlugins.IsInstalled(ExternalPlugin.TextAdvance) && !TextAdvanceIPC.IsPluginEnabled())
        {
            Chat.ExecuteCommand("/at");
        }

        StartTelemetry(s);
    }

    private static void ApplyStartingClass()
    {
        var cfg = Plugin.Cfg;
        if (!cfg.ApplyClassOnStart) return;
        if (cfg.ClassQueue.Count == 0) return;

        var idx = ClassSwitcher.FindActiveEntryIndex(cfg.ClassQueue);
        if (idx < 0)
        {
            ECommons.DalamudServices.Svc.Chat.Print("[FateFrenzy] Class queue: every entry is at its level cap, staying on current class.");
            return;
        }
        var entry = cfg.ClassQueue[idx];
        var label = $"gearset {entry.GearsetIndex} ({ClassSwitcher.JobNameForUserIndex(entry.GearsetIndex)})";
        if (ClassSwitcher.TryEquip(entry))
            ECommons.DalamudServices.Svc.Chat.Print($"[FateFrenzy] Switching to {label}.");
        else
            ECommons.DalamudServices.Svc.Chat.PrintError($"[FateFrenzy] Could not equip {label} (game refused — combat, mount, or transient lock?). See /xllog for details.");
    }

    public void Stop()
    {
        WasStoppedManually = true;
        Plugin.Cfg.WasRunningBeforeDisconnect = false;
        Plugin.Cfg.Save();

        var ending = session;
        currentTask = null;
        grindTask = null;
        PauseReason = PauseReason.None;
        Svc.Automation.Stop();

        if (!Plugin.Cfg.KeepTextAdvanceOnStop
            && ExternalPlugins.IsInstalled(ExternalPlugin.TextAdvance) && TextAdvanceIPC.IsPluginEnabled())
        {
            Chat.ExecuteCommand("/at");
        }

        FinalizeRun(ending);
        session = null;
        activeZones = [];
        Phase = AutoPhase.Idle;
        if (ending is not null) Diag("Stop requested; session cleared.");
    }

    public void AbortOnDisconnect()
    {
        WasStoppedManually = false;
        currentTask = null;
        grindTask = null;
        session = null;
        activeZones = [];
        Phase = AutoPhase.Idle;
    }

    private System.Threading.CancellationTokenSource? telemetryCts;
    private readonly System.Threading.SemaphoreSlim telemetryWakeSignal = new(0, 1);

    private static readonly string[] TelemetryEndpoints =
    [
        "https://fatefrenzytracker.discloud.app/api/track"
    ];

    // Catálogo garantido dos 54 itens oficiais de Bicolor Gemstone (incluindo os 2 Vouchers)
    // para que o inventário seja lido mesmo antes ou independentemente do cache do Lumina.
    private static readonly (uint Id, string Name, uint Cost)[] GuaranteedBicolorItems =
    [
        (43961, "Turali Bicolor Gemstone Voucher", 100),
        (35833, "Bicolor Gemstone Voucher", 100),
        // Dawntrail (3 Gemstones)
        (44072, "Alexandrian Axe Beak Wing", 3),
        (44063, "Alpaca Fillet", 3),
        (44055, "Br'aax Hide", 3),
        (44065, "Branchbearer Fruit", 3),
        (44057, "Gargantua Hide", 3),
        (44056, "Gomphotherium Skin", 3),
        (44054, "Hammerhead Crocodile Skin", 3),
        (44069, "Lesser Apollyon Shell", 3),
        (44067, "Megamaguey Pineapple", 3),
        (44066, "Nopalitender Tuna", 3),
        (44068, "Poison Frog Secretions", 3),
        (44106, "Rroneek Chuck", 3),
        (44027, "Rroneek Fleece", 3),
        (44053, "Silver Lobo Hide", 3),
        (44064, "Swampmonk Thigh", 3),
        (44071, "Tumbleclaw Weeds", 3),
        (44070, "Ty'aitya Wingblade", 3),
        // Endwalker (2 Gemstones)
        (36203, "Almasty Fur", 2),
        (36264, "Amra", 2),
        (36261, "Berkanan Sap", 2),
        (36630, "Bird of Elpis Breast", 2),
        (36262, "Dynamis Crystal", 2),
        (36259, "Dynamite Ash", 2),
        (36256, "Egg of Elpis", 2),
        (36242, "Gaja Hide", 2),
        (36253, "Hamsa Tenderloin", 2),
        (36245, "Kumbhira Skin", 2),
        (36258, "Lunatender Blossom", 2),
        (36243, "Luncheon Toad Skin", 2),
        (36257, "Mousse Flesh", 2),
        (36246, "Ophiotauros Hide", 2),
        (36255, "Ovibos Milk", 2),
        (36260, "Petalouda Scales", 2),
        (36244, "Saiga Hide", 2),
        (36254, "Yakow Chuck", 2),
        // Shadowbringers (1-2 Gemstones)
        (27763, "Hoptrap Leaf", 1),
        (27798, "Island Wolf Fang", 1),
        (27851, "Lorikeet Egg", 1),
        (27852, "Ovim Meat", 1),
        (27850, "Rail Tenderloin", 1),
        (27764, "Silkmoth Scales", 1),
        (27773, "Vampire Cup Vine", 1),
        (27774, "Vampire Vine Sap", 1),
        (27734, "Atrociraptor Skin", 2),
        (27799, "Cubus Flesh", 2),
        (27733, "Green Glider Skin", 2),
        (27800, "Hydrozoan Umbrella", 2),
        (27797, "Lorikeet Down", 2),
        (27756, "Ovim Fleece", 2),
        (27736, "Sea Swallow Skin", 2),
        (27732, "Smilodon Skin", 2),
        (27735, "Zonure Skin", 2)
    ];

    private static unsafe int GetCompleteItemCount(InventoryManager* im, uint itemId)
    {
        if (im is null || itemId == 0) return 0;

        // 1. Inventário Principal (Inventory1..4), NQ + HQ
        var total = im->GetInventoryItemCount(itemId, false, false, false)
                  + im->GetInventoryItemCount(itemId, true, false, false);

        // 2. Chocobo Saddlebags (SaddleBag1, SaddleBag2, PremiumSaddleBag1, PremiumSaddleBag2), NQ + HQ
        ReadOnlySpan<InventoryType> saddleBags =
        [
            InventoryType.SaddleBag1,
            InventoryType.SaddleBag2,
            InventoryType.PremiumSaddleBag1,
            InventoryType.PremiumSaddleBag2
        ];

        foreach (var bag in saddleBags)
        {
            total += im->GetItemCountInContainer(itemId, bag, false)
                   + im->GetItemCountInContainer(itemId, bag, true);
        }

        return total;
    }

    private void StartTelemetry(AutoFateSession _)
    {
        EnsureTelemetryWorker();
        if (telemetryWakeSignal.CurrentCount == 0)
        {
            try { telemetryWakeSignal.Release(); } catch { }
        }
    }

    private void EnsureTelemetryWorker()
    {
        if (telemetryCts is not null && !telemetryCts.IsCancellationRequested)
            return;

        telemetryCts = new System.Threading.CancellationTokenSource();
        var token = telemetryCts.Token;

        Task.Run(async () =>
        {
            var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            // Aguarda 3 segundos após carregar o plugin para o LocalPlayer e inventário estabilizarem
            try { await Task.Delay(3000, token); } catch (TaskCanceledException) { return; }

            var lastIdleSyncAt = DateTime.MinValue;

            while (!token.IsCancellationRequested)
            {
                var nextDelayMs = 45000;
                try
                {
                    var activeSession = session;
                    var isActivelyRunning = clib.Services.Svc.Automation.Running && !Paused && activeSession is not null;
                    var shouldIdleSync = !isActivelyRunning && (DateTime.UtcNow - lastIdleSyncAt).TotalSeconds >= 90;

                    if (isActivelyRunning || shouldIdleSync)
                    {
                        string? playerName = null;
                        string? serverName = null;
                        string? currentServer = null;
                        string? currentMap = null;
                        string classJob = "";
                        int level = 0;
                        int walletGemstones = 0;
                        int vouchersFound = 0;
                        string selectedTradeItem = "";
                        List<object> tradeItems = [];

                        var taskCompletion = new TaskCompletionSource<bool>();
                        _ = Svc.Framework.RunOnFrameworkThread(() =>
                        {
                            try
                            {
                                var pc = Svc.Objects.LocalPlayer;
                                if (pc is not null && !Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas])
                                {
                                    playerName = pc.Name.ToString();
                                    serverName = pc.HomeWorld.Value.Name.ToString();
                                    currentServer = pc.CurrentWorld.Value.Name.ToString();
                                    classJob = pc.ClassJob.Value.Abbreviation.ToString();
                                    level = pc.Level;

                                    var currentTerritory = Svc.ClientState.TerritoryType;
                                    var currentZone = ZoneRegistry.Zones.FirstOrDefault(z => z.TerritoryId == currentTerritory);
                                    if (currentZone is not null)
                                    {
                                        currentMap = currentZone.Name;
                                    }
                                    else
                                    {
                                        var terrRow = ECommons.DalamudServices.Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRowOrDefault(currentTerritory);
                                        var placeName = terrRow?.PlaceName.ValueNullable?.Name.ExtractText();
                                        currentMap = !string.IsNullOrWhiteSpace(placeName)
                                            ? placeName
                                            : (activeZones.Count > 0 ? activeZones[0].Name : "Unknown");
                                    }

                                    if (activeSession is not null)
                                    {
                                        activeSession.UpdateGemstones();
                                        activeSession.UpdateExp();
                                    }
                                    walletGemstones = GemstoneCatalog.CurrentWalletCount();

                                    var targetItem = GemstoneCatalog.FindById(Plugin.Cfg.TargetTradeItemId);
                                    selectedTradeItem = targetItem?.ItemName ?? "";

                                    unsafe
                                    {
                                        var im = InventoryManager.Instance();
                                        if (im is not null)
                                        {
                                            var addedIds = new HashSet<uint>();

                                            // 1. Sempre escaneia todos os 54 itens garantidos (incluindo os Vouchers 35833 e 43961)
                                            foreach (var gItem in GuaranteedBicolorItems)
                                            {
                                                addedIds.Add(gItem.Id);
                                                var count = GetCompleteItemCount(im, gItem.Id);
                                                if (gItem.Id is 35833 or 43961)
                                                    vouchersFound += count;

                                                tradeItems.Add(new
                                                {
                                                    id = gItem.Id,
                                                    name = gItem.Name,
                                                    count,
                                                    cost = gItem.Cost
                                                });
                                            }

                                            // 2. Escaneia itens extras do catálogo Lumina que o jogador possua no inventário ou tenha selecionado
                                            foreach (var item in GemstoneCatalog.All)
                                            {
                                                if (addedIds.Contains(item.ItemId)) continue;
                                                var count = GetCompleteItemCount(im, item.ItemId);
                                                if (count > 0 || item.ItemId == Plugin.Cfg.TargetTradeItemId)
                                                {
                                                    addedIds.Add(item.ItemId);
                                                    tradeItems.Add(new
                                                    {
                                                        id = item.ItemId,
                                                        name = item.ItemName,
                                                        count,
                                                        cost = item.CostPerOne
                                                    });
                                                }
                                            }
                                        }
                                    }
                                }
                                taskCompletion.SetResult(true);
                            }
                            catch (Exception ex)
                            {
                                taskCompletion.SetException(ex);
                            }
                        });

                        await taskCompletion.Task;

                        if (playerName is not null && serverName is not null)
                        {
                            var isIdleSync = !isActivelyRunning;
                            if (isIdleSync)
                                lastIdleSyncAt = DateTime.UtcNow;

                            var payload = new
                            {
                                playerName,
                                source = "plugin",
                                isPlugin = true,
                                clientType = "FateFrenzy.Plugin",
                                isIdleSync,
                                version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "1.8.7.0",
                                serverName,
                                currentServer,
                                currentMap,
                                classJob,
                                level,
                                phase = Phase.ToString(),
                                state = Status,
                                walletGemstones,
                                gemstonesFarmed = activeSession?.GemstonesEarned ?? 0,
                                fatesCompleted = activeSession?.CompletedCount ?? 0,
                                fatesPerHour = activeSession is not null ? Math.Round(activeSession.FatesPerHour, 1) : 0,
                                expEarned = activeSession?.ExpEarned ?? 0,
                                expPerHour = activeSession is not null ? (long)Math.Round(activeSession.ExpPerHour) : 0,
                                levelsGained = activeSession?.LevelsGained ?? 0,
                                sessionDurationMs = activeSession is not null ? (long)activeSession.Elapsed.TotalMilliseconds : 0,
                                selectedTradeItem,
                                tradeItems
                            };

                            var json = JsonSerializer.Serialize(payload);
                            foreach (var endpoint in TelemetryEndpoints)
                            {
                                try
                                {
                                    using var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
                                    var response = await client.PostAsync(endpoint, content, token);
                                    if (response.IsSuccessStatusCode)
                                    {
                                        Diag($"Telemetry ping sent ({endpoint}, idle={isIdleSync}, gems={walletGemstones}, vouchers={vouchersFound}): status={response.StatusCode}");
                                        break;
                                    }
                                }
                                catch
                                {
                                    // Tenta o próximo endpoint
                                }
                            }
                        }
                        else if (isActivelyRunning)
                        {
                            // Se estava em tela de loading/teleporte, tenta novamente em 3 segundos em vez de esperar 45s
                            nextDelayMs = 3000;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Diag($"Telemetry tracking request failed: {ex.Message}");
                }

                try
                {
                    await telemetryWakeSignal.WaitAsync(nextDelayMs, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    internal void StopTelemetry()
    {
        telemetryCts?.Cancel();
        telemetryCts = null;
    }

}

internal enum AutoPhase { Idle, Grinding, Trading, Repairing, Humanizing, Finishing, Paused }
