namespace RepairPartsPro.Services;

public sealed class MarketplaceApiOptions
{
    public bool DisableHtmlFallbackInProduction { get; set; } = true;
    public EbayApiOptions Ebay { get; set; } = new();
    public AmazonApiOptions Amazon { get; set; } = new();
    public MarketplaceEndpointApiOptions Newegg { get; set; } = new();
    public MarketplaceEndpointApiOptions TigerDirect { get; set; } = new();
    public MarketplaceEndpointApiOptions MicroCenter { get; set; } = new();
}

public sealed class EbayApiOptions
{
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Environment { get; set; } = "Production";
}

public sealed class AmazonApiOptions
{
    public bool Enabled { get; set; }
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string PartnerTag { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string Host { get; set; } = "webservices.amazon.com";
}

public sealed class MarketplaceEndpointApiOptions
{
    public bool Enabled { get; set; }
    public string EndpointTemplate { get; set; } = string.Empty;
    public string ApiKeyHeader { get; set; } = "X-Api-Key";
    public string ApiKey { get; set; } = string.Empty;
}
