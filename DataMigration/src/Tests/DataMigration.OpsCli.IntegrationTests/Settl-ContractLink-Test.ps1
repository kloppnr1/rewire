# Settl-ContractLink-Test.ps1
#
# Manuelt test-script for Settl contract MP link ADD + DELETE.
# Bruges til at verificere at:
#   1. POST /api/v1/contracts/{id}/meteringPoints opretter et link korrekt
#   2. DELETE /api/v1/contracts/{id}/meteringPoints/{linkId}?TransactionId= sletter det
#
# Credentials hentes fra user-secrets (dotnet user-secrets list --project src/DataMigration.OpsCli).
# Kør fra repo-rod.
#
# Parametre (tilpas efter behov):
#   $ContractId        — Settl contract GUID at teste på
#   $MeteringPointId   — Settl MP GUID der linkes
#   $MpIdentification  — National MP identification (18-cifret el. kort vand-instnum)
#   $StartUtc          — Link-startdato som UTC ISO-8601 (dansk midnat → UTC: 2023-09-30T22:00:00Z)
#   $EndUtc            — Link-slutdato som UTC. Open-ended sentinel: "9999-12-31T23:59:58.999Z"

param(
    [string]$ContractId       = "b336feaf-f6f4-4df3-9083-c26d7fb144e2",
    [string]$MeteringPointId  = "2292fff9-a3be-4818-9e92-0e96a487974b",
    [string]$MpIdentification = "571313115200113330",
    [string]$StartUtc         = "2023-09-30T22:00:00Z",
    [string]$EndUtc           = "9999-12-31T23:59:58.999Z",
    [string]$Env              = "featuretest"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---- Credentials (fra user-secrets) ----
$secrets = dotnet user-secrets list --project src/DataMigration.OpsCli 2>&1
$getSecret = { param($key) ($secrets | Select-String "^$key = (.+)$").Matches[0].Groups[1].Value }

$baseUrl  = ($secrets | Select-String "Environments:${Env}:BaseUrl = (.+)$").Matches[0].Groups[1].Value
$subKey   = & $getSecret "SettlApi:Environments:${Env}:SubscriptionKey"
$clientId = & $getSecret "SettlApi:Environments:${Env}:Credentials:ClientId"
$secret   = & $getSecret "SettlApi:Environments:${Env}:Credentials:ClientSecret"
$user     = & $getSecret "SettlApi:Environments:${Env}:Credentials:Username"
$pass     = & $getSecret "SettlApi:Environments:${Env}:Credentials:Password"
$scope    = & $getSecret "SettlApi:Environments:${Env}:Credentials:Scope"

Write-Host "Env: $Env | BaseUrl: $baseUrl"

# ---- Token ----
$authB64   = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${clientId}:${secret}"))
$tokenResp = Invoke-RestMethod -Uri "$baseUrl/security/connect/token" -Method Post `
    -Headers @{ "Ocp-Apim-Subscription-Key" = $subKey; "Authorization" = "Basic $authB64" } `
    -ContentType "application/x-www-form-urlencoded" `
    -Body "grant_type=password&username=$user&password=$pass&scope=$([Uri]::EscapeDataString($scope))"
$token   = $tokenResp.access_token
$headers = @{ "Authorization" = "Bearer $token"; "Ocp-Apim-Subscription-Key" = $subKey; "Content-Type" = "application/json" }
Write-Host "Token OK (length $($token.Length))"

# ---- ADD ----
$txId   = [Guid]::NewGuid().ToString()
$linkId = [Guid]::NewGuid().ToString()
$body = [ordered]@{
    TransactionId = $txId
    ContractMeteringPoints = @(@{
        Id                       = $linkId
        MeteringPointId          = $MeteringPointId
        MeteringPointIdentification = $MpIdentification
        Start                    = $StartUtc
        End                      = $EndUtc
    })
} | ConvertTo-Json -Depth 5

$addUrl = "$baseUrl/internal-billing/api/v1/contracts/$ContractId/meteringPoints"
Write-Host "`nPOST $addUrl"
Write-Host "Body: $body"
Invoke-RestMethod -Uri $addUrl -Method Post -Headers $headers -Body $body | Out-Null
Write-Host "ADD OK — link $linkId"

# ---- DELETE (PascalCase TransactionId som query param) ----
$delTxId = [Guid]::NewGuid().ToString()
$delUrl  = "$baseUrl/internal-billing/api/v1/contracts/$ContractId/meteringPoints/$linkId`?TransactionId=$delTxId"
Write-Host "`nDELETE $delUrl"
Invoke-RestMethod -Uri $delUrl -Method Delete -Headers $headers | Out-Null
Write-Host "DELETE OK"

# ---- Verificer at linket er væk ----
$getUrl  = "$baseUrl/internal-billing/api/v1/contracts/$ContractId/meteringPoints"
$allMps  = Invoke-RestMethod -Uri $getUrl -Method Get -Headers $headers
$deleted = $allMps.ContractMeteringPoints | Where-Object { $_.Id -eq $linkId }
if ($deleted) {
    Write-Host "FEJL: Link $linkId eksisterer stadig efter DELETE!" -ForegroundColor Red
} else {
    Write-Host "BEKRAEFTET: Link $linkId er slettet." -ForegroundColor Green
}
