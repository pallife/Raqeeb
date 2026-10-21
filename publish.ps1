# يبني رقيب كملف exe واحد مستقل + مساعد القفل ويضعهما في dist\
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$out = Join-Path $PSScriptRoot 'dist'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish src\Raqeeb\Raqeeb.csproj -c Release -o $out "-p:PathMap=$PSScriptRoot=/raqeeb"
if ($LASTEXITCODE -ne 0) { exit 1 }
dotnet publish src\Raqeeb.Helper\Raqeeb.Helper.csproj -c Release -r win-x64 --self-contained true `
    "-p:PathMap=$PSScriptRoot=/raqeeb" -p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial -p:EnableCompressionInSingleFile=true -o (Join-Path $PSScriptRoot '_build_helper')
if ($LASTEXITCODE -ne 0) { exit 1 }
Copy-Item '_build_helper\RaqeebHelper.exe' $out -Force
Remove-Item '_build_helper' -Recurse -Force
Copy-Item '.env.example' $out -Force
Get-ChildItem $out | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
