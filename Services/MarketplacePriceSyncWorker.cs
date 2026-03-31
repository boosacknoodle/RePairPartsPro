using Microsoft.Extensions.Options;

namespace RepairPartsPro.Services;

public sealed class MarketplacePriceSyncWorker(
    VendorDataStore dataStore,
    IMarketplaceQuoteFetcher quoteFetcher,
    IOptions<PriceSyncOptions> options,
    ILogger<MarketplacePriceSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = options.Value;
        if (!cfg.Enabled)
        {
            logger.LogInformation("Price sync worker is disabled.");
            return;
        }

        var intervalMinutes = Math.Clamp(cfg.IntervalMinutes, 1, 1440);
        var batchSize = Math.Clamp(cfg.BatchSize, 20, 2000);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(batchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Scheduled marketplace price sync failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
        }
    }

    private async Task SyncOnceAsync(int batchSize, CancellationToken cancellationToken)
    {
        var offers = await dataStore.GetTopOffersAsync(batchSize, cancellationToken);
        if (offers.Count == 0)
        {
            return;
        }

        var updated = 0;
        foreach (var offer in offers)
        {
            var liveQuote = await quoteFetcher.TryFetchQuoteAsync(offer, cancellationToken);
            if (liveQuote is null)
            {
                continue;
            }

            if (!PriceConfidencePolicy.IsReasonableQuoteChange(offer.Price, liveQuote.Price))
            {
                await dataStore.SaveCertificationAsync(offer.Id, false, "Worker rejected low-confidence quote", DateTime.UtcNow, cancellationToken);
                continue;
            }

            await dataStore.UpdateOfferQuoteAsync(offer.Id, liveQuote, cancellationToken);
            updated++;
        }

        logger.LogInformation("Scheduled price sync updated {Updated} of {Total} offers", updated, offers.Count);
    }
}
