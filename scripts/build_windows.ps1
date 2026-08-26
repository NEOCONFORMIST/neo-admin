$ErrorActionPreference = "Stop"

$Project = Join-Path $PSScriptRoot "..\windows\NEO.Admin\NEO.Admin.csproj"
$ProtocolTest = Join-Path $PSScriptRoot "..\tests\FirstOwnerProtocolTest.csproj"
$Output = Join-Path $PSScriptRoot "..\dist\windows-x64"
$PublishedSettings = Join-Path $Output "appsettings.json"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET 8 SDK was not found. Install it from Microsoft, then rerun this script."
}

New-Item -ItemType Directory -Force -Path $Output | Out-Null
$preservedSettings = if (Test-Path -LiteralPath $PublishedSettings) {
    [System.IO.File]::ReadAllBytes($PublishedSettings)
} else {
    $null
}

dotnet run --project $ProtocolTest -c Release
if ($LASTEXITCODE -ne 0) {
    throw "Windows first-owner protocol self-test failed"
}
try {
    dotnet publish $Project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $Output
    if ($LASTEXITCODE -ne 0) {
        throw "Windows app publish failed"
    }
}
finally {
    if ($null -ne $preservedSettings) {
        [System.IO.File]::WriteAllBytes(
            $PublishedSettings,
            $preservedSettings)
    }
}

Write-Host ""
Write-Host "Windows app published to: $Output"
Write-Host "Edit appsettings.json before starting NEO ADMIN.exe"
