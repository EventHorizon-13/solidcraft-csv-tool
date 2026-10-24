$ErrorActionPreference = 'Stop'
dotnet publish src/SolidCraftCsvTool/SolidCraftCsvTool.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -o dist/app
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item README.md dist/app/README.md
Write-Host 'Windows EXE: dist/app/SolidCraftCsvTool.exe'
