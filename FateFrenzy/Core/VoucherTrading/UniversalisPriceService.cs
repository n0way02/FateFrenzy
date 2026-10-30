using ECommons.DalamudServices;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace FateFrenzy.Core.VoucherTrading;

public sealed class UniversalisListing
{
    [JsonProperty("pricePerUnit")]
    public long PricePerUnit { get; set; }

    [JsonProperty("quantity")]
    public int Quantity { get; set; }

    [JsonProperty("retainerName")]
    public string RetainerName { get; set; } = string.Empty;

    [JsonProperty("hq")]
    public bool Hq { get; set; }
}

public sealed class UniversalisResponse
{
    [JsonProperty("itemId")]
    public uint ItemId { get; set; }

    [JsonProperty("worldId")]
    public uint? WorldId { get; set; }

    [JsonProperty("worldName")]
    public string? WorldName { get; set; }

    [JsonProperty("listings")]
    public List<UniversalisListing> Listings { get; set; } = [];

    [JsonProperty("minPrice")]
    public long MinPrice { get; set; }
}

public static class UniversalisPriceService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static DateTime lastFetchTime = DateTime.MinValue;
    private static uint cachedItemId = 0;
    private static string cachedWorld = string.Empty;
    private static UniversalisResponse? cachedData = null;

    public static async Task<UniversalisResponse?> GetListingsAsync(string worldName, uint itemId, bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(worldName) || itemId == 0) return null;

        if (!forceRefresh && cachedData != null && cachedItemId == itemId && cachedWorld.Equals(worldName, StringComparison.OrdinalIgnoreCase))
        {
            if ((DateTime.UtcNow - lastFetchTime).TotalSeconds < 60)
            {
                return cachedData;
            }
        }

        try
        {
            var url = $"https://universalis.app/api/v2/{Uri.EscapeDataString(worldName)}/{itemId}?listings=10";
            var json = await HttpClient.GetStringAsync(url);
            var parsed = JsonConvert.DeserializeObject<UniversalisResponse>(json);
            if (parsed != null)
            {
                cachedData = parsed;
                cachedItemId = itemId;
                cachedWorld = worldName;
                lastFetchTime = DateTime.UtcNow;
            }
            return parsed;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[UniversalisPriceService] Failed to fetch prices for item {itemId} on {worldName}");
            return cachedData;
        }
    }

    /// <summary>
    /// Computes optimal listing price with Anti-Bait protection.
    /// Competitor prices below floorPrice are ignored as troll bait.
    /// Competitor prices above floorPrice are undercut by undercutAmount.
    /// </summary>
    public static long CalculateUndercutPrice(
        UniversalisResponse? marketData,
        long initialPrice,
        long floorPrice,
        long undercutAmount,
        string? ownRetainerName = null)
    {
        if (marketData == null || marketData.Listings.Count == 0)
        {
            return initialPrice;
        }

        var validCompetitorPrice = long.MaxValue;
        var foundValidCompetitor = false;

        foreach (var listing in marketData.Listings)
        {
            if (!string.IsNullOrEmpty(ownRetainerName) &&
                listing.RetainerName.Equals(ownRetainerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Anti-Bait: Ignore listings below floor price
            if (listing.PricePerUnit < floorPrice)
            {
                Svc.Log.Debug($"[Universalis] Ignoring bait listing: {listing.PricePerUnit:N0} gil by '{listing.RetainerName}' (below floor {floorPrice:N0})");
                continue;
            }

            if (listing.PricePerUnit < validCompetitorPrice)
            {
                validCompetitorPrice = listing.PricePerUnit;
                foundValidCompetitor = true;
            }
        }

        if (!foundValidCompetitor)
        {
            return initialPrice;
        }

        var targetPrice = validCompetitorPrice - undercutAmount;
        if (targetPrice < floorPrice)
        {
            targetPrice = floorPrice;
        }

        return targetPrice;
    }
}
