param(
    [Parameter(Mandatory = $false)]
    [string]$SecretKey = $env:STRIPE_SECRET_KEY,

    [Parameter(Mandatory = $false)]
    [string]$WebhookUrl = "",

    [Parameter(Mandatory = $false)]
    [switch]$CreateWebhook
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($SecretKey)) {
    Write-Host "Missing Stripe secret key." -ForegroundColor Red
    Write-Host "Set STRIPE_SECRET_KEY environment variable or pass -SecretKey." -ForegroundColor Yellow
    exit 1
}

$headers = @{ Authorization = "Bearer $SecretKey" }

function New-StripeProduct {
    param([string]$Name)
    return Invoke-RestMethod `
        -Method Post `
        -Uri "https://api.stripe.com/v1/products" `
        -Headers $headers `
        -ContentType "application/x-www-form-urlencoded" `
        -Body @{ name = $Name }
}

function New-StripeRecurringPrice {
    param(
        [string]$ProductId,
        [int]$UnitAmount,
        [string]$Currency = "usd"
    )

    return Invoke-RestMethod `
        -Method Post `
        -Uri "https://api.stripe.com/v1/prices" `
        -Headers $headers `
        -ContentType "application/x-www-form-urlencoded" `
        -Body @{
            product = $ProductId
            unit_amount = $UnitAmount
            currency = $Currency
            "recurring[interval]" = "month"
        }
}

function New-StripeWebhook {
    param([string]$Url)

    return Invoke-RestMethod `
        -Method Post `
        -Uri "https://api.stripe.com/v1/webhook_endpoints" `
        -Headers $headers `
        -ContentType "application/x-www-form-urlencoded" `
        -Body @{
            url = $Url
            "enabled_events[]" = @(
                "checkout.session.completed",
                "customer.subscription.deleted"
            )
        }
}

Write-Host "Creating Stripe products and prices..." -ForegroundColor Cyan

$basicProduct = New-StripeProduct -Name "RepairPartsPro Basic"
$proProduct = New-StripeProduct -Name "RepairPartsPro Pro"

$basicPrice = New-StripeRecurringPrice -ProductId $basicProduct.id -UnitAmount 1099
$proPrice = New-StripeRecurringPrice -ProductId $proProduct.id -UnitAmount 2499

$webhookSecret = ""
if ($CreateWebhook) {
    if ([string]::IsNullOrWhiteSpace($WebhookUrl)) {
        Write-Host "-CreateWebhook was set but -WebhookUrl is empty." -ForegroundColor Red
        exit 1
    }

    Write-Host "Creating Stripe webhook endpoint..." -ForegroundColor Cyan
    $webhook = New-StripeWebhook -Url $WebhookUrl
    $webhookSecret = $webhook.secret
}

Write-Host ""
Write-Host "Stripe setup complete." -ForegroundColor Green
Write-Host ""

$result = [ordered]@{
    Stripe = [ordered]@{
        SecretKey = "<set in user-secrets or env var>"
        PublishableKey = "<pk_... from Stripe dashboard>"
        WebhookSecret = $webhookSecret
        BasicPriceId = $basicPrice.id
        ProPriceId = $proPrice.id
    }
}

$result | ConvertTo-Json -Depth 4

Write-Host ""
Write-Host "Next:" -ForegroundColor Yellow
Write-Host "1) Put BasicPriceId/ProPriceId into appsettings or user-secrets"
if ($CreateWebhook) {
    Write-Host "2) Put WebhookSecret into appsettings or user-secrets"
} else {
    Write-Host "2) Create webhook in Stripe dashboard (or rerun with -CreateWebhook -WebhookUrl)"
}
Write-Host "3) Restart the app"
