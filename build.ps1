param([string]$Version = '1.0.0-dev')
$ErrorActionPreference = 'Stop'
$BuildDate = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTimeOffset]::UtcNow, 'China Standard Time').ToString('yyyy-MM-dd HH:mm:ss')
dotnet publish src/SolidCraftCsvTool/SolidCraftCsvTool.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false "-p:Version=$Version" "-p:BuildDate=$BuildDate" -o dist/app
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item README.md dist/app/README.md
Copy-Item THIRD_PARTY_NOTICES.md dist/app/THIRD_PARTY_NOTICES.md
Write-Host 'Windows EXE: dist/app/SolidCraftCsvTool.exe'
