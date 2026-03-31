namespace RepairPartsPro.Services;

public sealed class CertificationPolicyOptions
{
    public bool ApiOnlyMode { get; set; } = true;
    public bool RequireOfficialApiSource { get; set; } = true;
    public string[] OfficialApiSourceTypes { get; set; } =
    [
        "EbayBrowseApi",
        "AmazonPaApi",
        "NeweggEndpointApi",
        "TigerDirectEndpointApi",
        "MicroCenterEndpointApi"
    ];
}
