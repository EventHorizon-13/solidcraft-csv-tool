$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$assetDir = Join-Path $repoRoot 'src/SolidCraftCsvTool/Assets'
New-Item -ItemType Directory -Path $assetDir -Force | Out-Null

$fontUrl = 'https://raw.githubusercontent.com/notofonts/noto-cjk/f8d157532fbfaeda587e826d4cd5b21a49186f7c/Sans/Variable/TTF/Subset/NotoSansSC-VF.ttf'
$licenseUrl = 'https://raw.githubusercontent.com/notofonts/noto-cjk/f8d157532fbfaeda587e826d4cd5b21a49186f7c/Sans/LICENSE'
$tempDir = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$variableFont = Join-Path $tempDir 'NotoSansSC-VF.ttf'
$staticFont = Join-Path $assetDir 'NotoSansSC.ttf'

Invoke-WebRequest -Uri $fontUrl -OutFile $variableFont
python -m fontTools.varLib.instancer $variableFont wght=400 --static --output $staticFont
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $staticFont)) { throw 'Could not create the static Noto Sans SC font.' }
Invoke-WebRequest -Uri $licenseUrl -OutFile (Join-Path $assetDir 'OFL.txt')
