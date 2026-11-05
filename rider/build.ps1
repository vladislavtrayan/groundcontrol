# Builds the GroundControl Rider plugin (out/groundcontrol-rider-<version>.jar) and installs it into Rider (restart Rider to load it).
# Compiles against Rider, or its SDK (riderRD from the JetBrains repository), with the JDK bundled with it, otherwise the one on PATH.
param(
    # Rider installation (or SDK) directory, the latest Rider in Program Files by default.
    [string] $Rider,

    # Plugin version, the one of plugin.xml by default.
    [string] $Version,

    # Builds without installing (e.g., in CI).
    [switch] $NoInstall
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not $Rider) {
    $Rider = (Get-ChildItem (Join-Path $Env:ProgramFiles "JetBrains") -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName "bin/rider64.exe") } |
        Sort-Object { (Get-Content (Join-Path $_.FullName "product-info.json") | ConvertFrom-Json).buildNumber } |
        Select-Object -Last 1).FullName
    if (-not $Rider) { throw "Rider is not found in Program Files, pass its directory by -Rider." }
}

$output = Join-Path $PSScriptRoot "out"
$classes = Join-Path $output "classes"
Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue

$javac = @("jbr/bin/javac.exe", "jbr/bin/javac") | ForEach-Object { Join-Path $Rider $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $javac) { $javac = "javac" }
$classPath = @("lib/*", "lib/modules/*") | ForEach-Object { Join-Path $Rider $_ }
& $javac --release 21 -nowarn -cp ($classPath -join [IO.Path]::PathSeparator) -d $classes `
    (Get-ChildItem (Join-Path $PSScriptRoot "src") -Recurse -Filter *.java).FullName
if ($LASTEXITCODE -ne 0) { throw "Compilation has failed." }

Copy-Item (Join-Path $PSScriptRoot "resources/*") $classes -Recurse
$descriptor = Join-Path $classes "META-INF/plugin.xml"
if ($Version) {
    (Get-Content $descriptor -Raw) -replace "<version>[^<]*</version>", "<version>$Version</version>" | Set-Content $descriptor -NoNewline
} else {
    $Version = ([xml](Get-Content $descriptor -Raw)).'idea-plugin'.version
}

# Jar entries are named with '/' (not '\' as by ZipFile.CreateFromDirectory of .NET Framework).
$jar = Join-Path $output "groundcontrol-rider-$Version.jar"
$zip = [System.IO.Compression.ZipFile]::Open($jar, "Create")
try {
    Get-ChildItem $classes -Recurse -File | ForEach-Object {
        $name = $_.FullName.Substring($classes.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $name) | Out-Null
    }
} finally {
    $zip.Dispose()
}
Write-Output "Built $jar."

if (-not $NoInstall) {
    $product = Get-Content (Join-Path $Rider "product-info.json") | ConvertFrom-Json
    $plugin = Join-Path $Env:APPDATA "JetBrains/$($product.dataDirectoryName)/plugins/groundcontrol"
    Remove-Item (Join-Path $plugin "lib") -Recurse -Force -ErrorAction SilentlyContinue
    New-Item (Join-Path $plugin "lib") -ItemType Directory -Force | Out-Null
    Copy-Item $jar (Join-Path $plugin "lib") -Force
    Write-Output "Installed into $plugin for Rider $($product.version), restart Rider to load it."
}
