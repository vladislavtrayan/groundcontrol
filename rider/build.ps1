# Builds the GroundControl Rider plugin with the JDK bundled with Rider and installs it into Rider (restart Rider to load it).
param(
    # Rider installation directory, the latest one in Program Files by default.
    [string] $Rider = (Get-ChildItem "$Env:ProgramFiles\JetBrains" -Directory |
        Where-Object { Test-Path "$($_.FullName)\bin\rider64.exe" } |
        Sort-Object { (Get-Content "$($_.FullName)\product-info.json" | ConvertFrom-Json).buildNumber } |
        Select-Object -Last 1).FullName
)

$ErrorActionPreference = "Stop"
$product = Get-Content "$Rider\product-info.json" | ConvertFrom-Json
$output = "$PSScriptRoot\out"
$jar = "$output\groundcontrol.jar"

Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue
& "$Rider\jbr\bin\javac.exe" --release 21 -nowarn -cp "$Rider\lib\*;$Rider\lib\modules\*" -d "$output\classes" `
    (Get-ChildItem "$PSScriptRoot\src" -Recurse -Filter *.java).FullName
if ($LASTEXITCODE -ne 0) { throw "Compilation has failed." }

Copy-Item "$PSScriptRoot\resources\*" "$output\classes" -Recurse
# Jar entries are named with '/' (not '\' as by ZipFile.CreateFromDirectory of .NET Framework).
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($jar, "Create")
try {
    Get-ChildItem "$output\classes" -Recurse -File | ForEach-Object {
        $name = $_.FullName.Substring("$output\classes\".Length).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $name) | Out-Null
    }
} finally {
    $zip.Dispose()
}

$plugin = "$Env:APPDATA\JetBrains\$($product.dataDirectoryName)\plugins\groundcontrol"
New-Item "$plugin\lib" -ItemType Directory -Force | Out-Null
Copy-Item $jar "$plugin\lib" -Force
Write-Output "Installed into $plugin for Rider $($product.version), restart Rider to load it."
