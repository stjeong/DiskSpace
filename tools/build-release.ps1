<#
.SYNOPSIS
    Builds the release packages of DiskSpace: a zip (self-contained single-file exe) and a signed MSIX per runtime.

.DESCRIPTION
    Output files (in -OutDir):
        DiskSpace-<version>-<rid>.zip
        DiskSpace-<version>-<rid>.msix
        DiskSpace-<version>.cer          (only when a self-signed certificate was generated)

    The MSIX is signed with -CertificatePath (.pfx). Without one, a throw-away self-signed certificate is
    created; its public part (.cer) must then be trusted before the MSIX can be installed.
    The package Publisher is always taken from the certificate subject, so the two always match.

    Requires the Windows 10/11 SDK (makeappx.exe, signtool.exe) and the .NET SDK.

.EXAMPLE
    ./tools/build-release.ps1
    ./tools/build-release.ps1 -Version 1.2.0 -CertificatePath my.pfx -CertificatePassword secret
#>
param(
    [string]$Version,                          # x.y.z; defaults to <Version> in the csproj
    [string[]]$Runtimes = @('win-x64', 'win-arm64'),
    [string]$OutDir = 'artifacts',
    [string]$CertificatePath,                  # .pfx used to sign the MSIX
    [string]$CertificatePassword,
    [string]$TimestampUrl,                     # e.g. http://timestamp.digicert.com (optional)
    [string]$PublisherDisplayName = 'DiskSpace'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'src/DiskSpace/DiskSpace.csproj'
$icon = Join-Path $root 'src/DiskSpace/app.ico'

if (-not $Version) {
    $Version = ([xml](Get-Content $proj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')." }
$msixVersion = "$Version.0" # MSIX needs four parts

if (-not [IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $root $OutDir }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) "DiskSpace-build-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $work | Out-Null

function Find-SdkTool([string]$name) {
    $bin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $tool = Get-ChildItem $bin -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^10\.[\d.]+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$name" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
    if (-not $tool) { throw "$name not found. Install the Windows 10/11 SDK." }
    return $tool
}

function Invoke-Tool([string]$exe, [string[]]$toolArgs) {
    & $exe @toolArgs
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($exe)) failed with exit code $LASTEXITCODE." }
}

# Largest PNG frame of app.ico (make_icon.ps1 writes PNG-compressed entries), resized to $size x $size.
function Save-Logo([string]$path, [int]$size) {
    $bytes = [IO.File]::ReadAllBytes($icon)
    $count = [BitConverter]::ToUInt16($bytes, 4)
    $best = $null; $bestDim = -1
    for ($i = 0; $i -lt $count; $i++) {
        $e = 6 + 16 * $i
        $dim = if ($bytes[$e] -eq 0) { 256 } else { $bytes[$e] }
        if ($dim -gt $bestDim) {
            $bestDim = $dim
            $best = @{ Length = [BitConverter]::ToInt32($bytes, $e + 8); Offset = [BitConverter]::ToInt32($bytes, $e + 12) }
        }
    }
    $ms = New-Object IO.MemoryStream (, $bytes[$best.Offset..($best.Offset + $best.Length - 1)])
    $src = [Drawing.Image]::FromStream($ms)
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.SmoothingMode = 'AntiAlias'
    $g.DrawImage($src, 0, 0, $size, $size)
    $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose(); $src.Dispose(); $ms.Dispose()
}

try {
    $makeappx = Find-SdkTool 'makeappx.exe'
    $signtool = Find-SdkTool 'signtool.exe'

    # ---- signing certificate ---------------------------------------------------------------
    if ($CertificatePath) {
        $cert = New-Object Security.Cryptography.X509Certificates.X509Certificate2 $CertificatePath, $CertificatePassword
    }
    else {
        Write-Host 'No certificate given: creating a self-signed one.'
        $cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=DiskSpace Self-Signed' `
            -KeyUsage DigitalSignature -FriendlyName 'DiskSpace MSIX signing' `
            -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(3) `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
        $CertificatePassword = [guid]::NewGuid().ToString('N')
        $CertificatePath = Join-Path $work 'selfsigned.pfx'
        Export-PfxCertificate -Cert $cert -FilePath $CertificatePath `
            -Password (ConvertTo-SecureString $CertificatePassword -AsPlainText -Force) | Out-Null
        Export-Certificate -Cert $cert -FilePath (Join-Path $OutDir "DiskSpace-$Version.cer") | Out-Null
        Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
    }
    $publisher = [Security.SecurityElement]::Escape($cert.Subject)
    $displayPublisher = [Security.SecurityElement]::Escape($PublisherDisplayName)

    foreach ($rid in $Runtimes) {
        $arch = $rid -replace '^win-', ''
        Write-Host "==== $rid ===="

        # ---- publish: self-contained single-file exe --------------------------------------
        $pub = Join-Path $work "publish-$rid"
        Invoke-Tool 'dotnet' @('publish', $proj, '-c', 'Release', '-r', $rid, '--self-contained', 'true', '-o', $pub,
            '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none', "-p:Version=$Version")

        # ---- zip --------------------------------------------------------------------------
        $zip = Join-Path $OutDir "DiskSpace-$Version-$rid.zip"
        if (Test-Path $zip) { Remove-Item $zip }
        Compress-Archive -Path (Join-Path $pub '*') -DestinationPath $zip

        # ---- msix -------------------------------------------------------------------------
        $layout = Join-Path $work "msix-$rid"
        New-Item -ItemType Directory -Force (Join-Path $layout 'Assets') | Out-Null
        Copy-Item (Join-Path $pub '*') $layout -Recurse
        Save-Logo (Join-Path $layout 'Assets\Square44x44Logo.png') 44
        Save-Logo (Join-Path $layout 'Assets\Square150x150Logo.png') 150
        Save-Logo (Join-Path $layout 'Assets\StoreLogo.png') 50

        @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
         IgnorableNamespaces="uap rescap">
  <Identity Name="DiskSpace" Publisher="$publisher" Version="$msixVersion" ProcessorArchitecture="$arch" />
  <Properties>
    <DisplayName>DiskSpace</DisplayName>
    <PublisherDisplayName>$displayPublisher</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Resources>
    <Resource Language="ko-KR" />
  </Resources>
  <Applications>
    <Application Id="DiskSpace" Executable="DiskSpace.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="DiskSpace"
                          Description="Treemap view of drive and folder space usage"
                          BackgroundColor="transparent"
                          Square150x150Logo="Assets\Square150x150Logo.png"
                          Square44x44Logo="Assets\Square44x44Logo.png" />
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
"@ | Set-Content (Join-Path $layout 'AppxManifest.xml') -Encoding UTF8

        $msix = Join-Path $OutDir "DiskSpace-$Version-$rid.msix"
        Invoke-Tool $makeappx @('pack', '/d', $layout, '/p', $msix, '/o', '/h', 'SHA256')

        $signArgs = @('sign', '/fd', 'SHA256', '/f', $CertificatePath, '/p', $CertificatePassword)
        if ($TimestampUrl) { $signArgs += @('/tr', $TimestampUrl, '/td', 'SHA256') }
        Invoke-Tool $signtool ($signArgs + $msix)
    }

    Write-Host "`nPackages in ${OutDir}:"
    Get-ChildItem $OutDir -File | ForEach-Object { Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
