namespace RepairPartsPro.Services;

public static class PriceConfidencePolicy
{
    public static bool IsReasonableQuoteChange(decimal previousPrice, decimal fetchedPrice)
    {
        if (previousPrice <= 0 || fetchedPrice <= 0)
        {
            return false;
        }

        var lowerBound = previousPrice * 0.60m;
        var upperBound = previousPrice * 1.60m;
        return fetchedPrice >= lowerBound && fetchedPrice <= upperBound;
    }
}
