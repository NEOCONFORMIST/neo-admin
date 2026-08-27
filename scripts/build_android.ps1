[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $projectRoot "android/NEO.Admin.Android/NEO.Admin.Android.csproj"
$output = Join-Path $projectRoot "dist/android"
$signingDirectory = Join-Path `
    ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
    "NEO ADMIN/android-signing"
$keyStore = Join-Path $signingDirectory "neo-admin-release.p12"
$passwordFile = Join-Path $signingDirectory "neo-admin-release.password"

if (-not (Test-Path -LiteralPath $keyStore)) {
    New-Item -ItemType Directory -Path $signingDirectory -Force | Out-Null
    $password = [Convert]::ToHexString(
        [Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    Set-Content -LiteralPath $passwordFile -Value $password -Encoding ascii -NoNewline

    $keyTool = @(
        (Get-Command keytool.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
        "C:/Program Files/Android/jdk/jdk-8.0.302.8-hotspot/jdk8u302-b08/bin/keytool.exe",
        "C:/Program Files/Android/Android Studio/jbr/bin/keytool.exe"
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if (-not $keyTool) {
        throw "Java keytool was not found. Install a JDK before building the Android APK."
    }

    & $keyTool `
        -genkeypair `
        -keystore $keyStore `
        -storetype PKCS12 `
        -storepass $password `
        -keypass $password `
        -alias neo-admin `
        -keyalg RSA `
        -keysize 2048 `
        -validity 10950 `
        -dname "CN=NEO ADMIN, OU=Android, O=NEOCONFORMIST, C=US"
    if ($LASTEXITCODE -ne 0) {
        throw "Android signing key generation failed with exit code $LASTEXITCODE"
    }
}

if (-not (Test-Path -LiteralPath $passwordFile)) {
    throw "The Android signing password is missing: $passwordFile"
}
$password = (Get-Content -LiteralPath $passwordFile -Raw).Trim()

# A prior debug build can leave a newer *-Signed.apk in the shared output
# directory. Clean first so the release certificate is always applied.
dotnet clean $project `
    -c $Configuration `
    -f net9.0-android
if ($LASTEXITCODE -ne 0) {
    throw "Android clean failed with exit code $LASTEXITCODE"
}

dotnet publish $project `
    -c $Configuration `
    -f net9.0-android `
    -p:AndroidPackageFormat=apk `
    -p:AndroidKeyStore=true `
    -p:AndroidSigningKeyStore=$keyStore `
    -p:AndroidSigningKeyAlias=neo-admin `
    -p:AndroidSigningStorePass=$password `
    -p:AndroidSigningKeyPass=$password
if ($LASTEXITCODE -ne 0) {
    throw "Android publish failed with exit code $LASTEXITCODE"
}

$publishDirectory = Join-Path $projectRoot `
    "android/NEO.Admin.Android/bin/$Configuration/net9.0-android/publish"
$apk = Get-ChildItem -LiteralPath $publishDirectory -Filter "*-Signed.apk" -File |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $apk) {
    throw "The signed Android APK was not produced in $publishDirectory"
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
$destination = Join-Path $output "NEO-ADMIN-Android.apk"
Copy-Item -LiteralPath $apk.FullName -Destination $destination -Force

Write-Host "Android APK: $destination"
Write-Host "Signing key: $keyStore"
