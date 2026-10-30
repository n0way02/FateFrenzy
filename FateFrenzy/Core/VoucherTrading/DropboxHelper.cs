using ECommons.DalamudServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;

namespace FateFrenzy.Core.VoucherTrading;

public static class DropboxHelper
{
    private static string DropboxConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs", "Dropbox", "DefaultConfig.json");

    public static void ConfigureForVoucherTrade(bool isSupplier, uint voucherId, int quantity)
    {
        try
        {
            var path = DropboxConfigPath;
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            JObject config;
            if (File.Exists(path))
            {
                var content = File.ReadAllText(path);
                config = JObject.Parse(content);
            }
            else
            {
                config = new JObject();
            }

            config["Active"] = true;
            config["AutoConfirm5"] = true;
            config["WhitelistMode"] = false;

            if (isSupplier)
            {
                var queue = new JArray
                {
                    new JObject
                    {
                        ["ItemId"] = voucherId,
                        ["Quantity"] = quantity
                    }
                };
                config["TradeQueue"] = queue;
            }
            else
            {
                config["TradeQueue"] = new JArray();
            }

            File.WriteAllText(path, config.ToString(Formatting.Indented));
            Svc.Log.Info("[FateFrenzy] Configured Dropbox DefaultConfig.json for voucher trading.");
        }
        catch (Exception ex)
        {
            Svc.Log.Debug(ex, "[FateFrenzy] Could not write to Dropbox DefaultConfig.json");
        }
    }
}

