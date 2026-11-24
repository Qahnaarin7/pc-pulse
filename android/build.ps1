param(
    [Parameter(Mandatory)] [string] $JavaHome,
    [Parameter(Mandatory)] [string] $AndroidJar,
    [Parameter(Mandatory)] [string] $BuildTools,
    [string] $Output = (Join-Path $PSScriptRoot 'build\PC-Pulse-Android.apk')
)

$ErrorActionPreference = 'Stop'
$JavaHome = (Resolve-Path -LiteralPath $JavaHome).Path
$AndroidJar = (Resolve-Path -LiteralPath $AndroidJar).Path
$BuildTools = (Resolve-Path -LiteralPath $BuildTools).Path
$Output = [System.IO.Path]::GetFullPath($Output)
$build = Join-Path $PSScriptRoot 'build'
$compiled = Join-Path $build 'resources.zip'
$baseApk = Join-Path $build 'base.apk'
$unsigned = Join-Path $build 'unsigned.apk'
$aligned = Join-Path $build 'aligned.apk'
$classes = Join-Path $build 'classes'
$generated = Join-Path $build 'generated'
$dex = Join-Path $build 'dex'
$keystore = Join-Path $build 'development.p12'
$java = Join-Path $JavaHome 'bin\java.exe'
$javac = Join-Path $JavaHome 'bin\javac.exe'
$keytool = Join-Path $JavaHome 'bin\keytool.exe'
$aapt2 = Join-Path $BuildTools 'aapt2.exe'

function Invoke-BuildTool([string] $Tool, [string[]] $Arguments) {
    & $Tool @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Tool exited with code $LASTEXITCODE"
    }
}

New-Item -ItemType Directory -Force $classes, $generated, $dex, (Split-Path -Parent $Output) | Out-Null
foreach ($directory in $classes, $generated, $dex) {
    Get-ChildItem -LiteralPath $directory -File -Recurse | Remove-Item -Force
}
foreach ($package in $compiled, $baseApk, $unsigned, $aligned) {
    if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Force }
}

Invoke-BuildTool $aapt2 @('compile', '--dir', (Join-Path $PSScriptRoot 'res'), '-o', $compiled)
Invoke-BuildTool $aapt2 @('link', '-I', $AndroidJar, '--manifest',
    (Join-Path $PSScriptRoot 'AndroidManifest.xml'), '--java', $generated, '-o', $baseApk, $compiled)

$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src'), $generated -Filter '*.java' -File -Recurse |
    Select-Object -ExpandProperty FullName)
Invoke-BuildTool $javac (@('--release', '8', '-encoding', 'UTF-8', '-classpath', $AndroidJar, '-d', $classes) + $sources)
$bytecode = @(Get-ChildItem -LiteralPath $classes -Filter '*.class' -File -Recurse |
    Select-Object -ExpandProperty FullName)
Invoke-BuildTool $java (@('-cp', (Join-Path $BuildTools 'lib\d8.jar'), 'com.android.tools.r8.D8',
    '--release', '--min-api', '26', '--lib', $AndroidJar, '--output', $dex) + $bytecode)

Copy-Item -LiteralPath $baseApk -Destination $unsigned
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$apk = [System.IO.Compression.ZipFile]::Open($unsigned, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    foreach ($file in (Get-ChildItem -LiteralPath $dex -Filter '*.dex' -File)) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($apk, $file.FullName, $file.Name) | Out-Null
    }
} finally {
    $apk.Dispose()
}

Invoke-BuildTool (Join-Path $BuildTools 'zipalign.exe') @('-p', '-f', '4', $unsigned, $aligned)
if (!(Test-Path -LiteralPath $keystore)) {
    Invoke-BuildTool $keytool @('-genkeypair', '-keystore', $keystore, '-storetype', 'PKCS12',
        '-storepass', 'android', '-keypass', 'android', '-alias', 'pcpulse-development', '-keyalg',
        'RSA', '-keysize', '2048', '-validity', '10000', '-dname', 'CN=PC Pulse Development')
}
$signer = Join-Path $BuildTools 'lib\apksigner.jar'
Invoke-BuildTool $java @('-jar', $signer, 'sign', '--ks', $keystore, '--ks-key-alias', 'pcpulse-development',
    '--ks-pass', 'pass:android', '--key-pass', 'pass:android', '--v4-signing-enabled', 'false',
    '--out', $Output, $aligned)
Invoke-BuildTool $java @('-jar', $signer, 'verify', '--verbose', $Output)
Write-Output $Output
