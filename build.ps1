$ErrorActionPreference = 'Stop'
python -m pip install --disable-pip-version-check fonttools==4.62.0
if ($LASTEXITCODE -ne 0) { throw 'Could not install the font preparation tool.' }
& "$PSScriptRoot/tools/prepare-font.ps1"
dotnet publish src/SolidCraftCsvTool/SolidCraftCsvTool.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -o dist/app
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
New-Item -ItemType Directory -Path dist/app/Fonts -Force | Out-Null
Copy-Item src/SolidCraftCsvTool/Assets/NotoSansSC.ttf dist/app/Fonts/NotoSansSC.ttf
Copy-Item src/SolidCraftCsvTool/Assets/OFL.txt dist/app/Fonts/OFL.txt
Copy-Item README.md dist/app/README.md
Copy-Item THIRD_PARTY_NOTICES.md dist/app/THIRD_PARTY_NOTICES.md
Write-Host 'Windows EXE: dist/app/SolidCraftCsvTool.exe'
