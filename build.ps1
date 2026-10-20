$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
Write-Host "Qlyptra — сборка Windows x64" -ForegroundColor Cyan


$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host "Не найден .NET SDK. Установи .NET 10 SDK и запусти build.bat снова." -ForegroundColor Red
    Read-Host "Нажми Enter для выхода"
    exit 1
}

# The Yandex downloader is Python-based. Build a portable venv inside dist so the
# resulting distribution can run on this PC without a separate global pip install.
$pythonLauncher = Get-Command py -ErrorAction SilentlyContinue
$pythonCommand = $null
$pythonPrefix = @()
if ($pythonLauncher) {
    $pythonCommand = $pythonLauncher.Source
    $pythonPrefix = @('-3')
} else {
    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python) { $pythonCommand = $python.Source }
}
if (-not $pythonCommand) {
    Write-Host "Не найден Python 3.9+. Установи Python 3.12 x64 с python.org, включив Python Launcher, и запусти сборку ещё раз." -ForegroundColor Red
    Read-Host "Нажми Enter для выхода"
    exit 1
}

try {
    if (Test-Path .\release) { Remove-Item .\release -Recurse -Force }
    New-Item -ItemType Directory -Path .\release | Out-Null

    Write-Host "`n[1/4] Публикация приложения .NET..." -ForegroundColor Cyan
    dotnet publish .\Qlyptra.csproj -c Release -r win-x64 --self-contained true `
      -p:PublishSingleFile=true `
      -p:IncludeNativeLibrariesForSelfExtract=true `
      -p:IncludeAllContentForSelfExtract=true `
      -p:EnableCompressionInSingleFile=false `
      -p:DebugType=None -p:DebugSymbols=false `
      -o .\release
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }

    # Служебные файлы — в bin, в корне только Qlyptra.exe
    $binDir = Join-Path $PSScriptRoot 'release\bin'
    New-Item -ItemType Directory -Path $binDir -Force | Out-Null
    # перенести всё кроме exe из корня publish в bin (нативные dll single-file обычно внутри exe)
    Get-ChildItem -Path (Join-Path $PSScriptRoot 'release') -File | Where-Object { $_.Name -ne 'Qlyptra.exe' } | ForEach-Object {
        Move-Item $_.FullName $binDir -Force
    }
    Get-ChildItem -Path (Join-Path $PSScriptRoot 'release') -Directory | Where-Object { $_.Name -ne 'bin' } | ForEach-Object {
        Move-Item $_.FullName $binDir -Force
    }

    Write-Host "`n[2/4] Подготовка изолированного окружения загрузчика Яндекс Музыки..." -ForegroundColor Cyan
    & $pythonCommand @pythonPrefix --version
    if ($LASTEXITCODE -ne 0) { throw "Не удалось запустить Python 3." }
    $pythonVersionText = (& $pythonCommand @pythonPrefix -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')").Trim()
    if ($LASTEXITCODE -ne 0 -or [version]$pythonVersionText -lt [version]'3.9') { throw "Нужен Python версии 3.9 или новее. Рекомендуется Python 3.12 x64." }
    & $pythonCommand @pythonPrefix -m venv .\release\bin\ymd-env
    if ($LASTEXITCODE -ne 0) { throw "Не удалось создать Python-окружение в release\bin\ymd-env." }
    $venvPython = Join-Path $PSScriptRoot 'release\bin\ymd-env\Scripts\python.exe'
    & $venvPython -m pip install --disable-pip-version-check --upgrade pip
    if ($LASTEXITCODE -ne 0) { throw "Не удалось обновить pip." }
    & $venvPython -m pip install --disable-pip-version-check --upgrade `
      "https://github.com/llistochek/yandex-music-downloader/archive/refs/heads/main.zip" imageio-ffmpeg
    if ($LASTEXITCODE -ne 0) { throw "Не удалось загрузить зависимости. Проверь подключение к GitHub и PyPI, затем запусти build.bat снова." }
    Copy-Item .\tools\yandex_downloader_worker.py .\release\bin\yandex_downloader_worker.py -Force

    Write-Host "`n[3/4] Подготовка FFmpeg и лицензии загрузчика..." -ForegroundColor Cyan
    $ffmpegSource = (& $venvPython -c "import imageio_ffmpeg; print(imageio_ffmpeg.get_ffmpeg_exe())").Trim()
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $ffmpegSource)) { throw "imageio-ffmpeg установлен, но ffmpeg.exe найти не удалось." }
    $encoderList = (& $ffmpegSource -hide_banner -encoders 2>&1 | Out-String)
    if ($encoderList -match 'libmp3lame') {
        Copy-Item $ffmpegSource .\release\bin\ffmpeg.exe -Force
    } else {
        Write-Host "В FFmpeg из imageio-ffmpeg нет MP3-кодировщика; загружаю Windows Essentials-сборку с libmp3lame..." -ForegroundColor Yellow
        $licenseFolder = Join-Path $PSScriptRoot 'release\bin\THIRD_PARTY_LICENSES'
        New-Item -ItemType Directory -Path $licenseFolder -Force | Out-Null
        $ffmpegWork = Join-Path $PSScriptRoot '.build-ffmpeg'
        $ffmpegZip = Join-Path $PSScriptRoot '.build-ffmpeg.zip'
        if (Test-Path $ffmpegWork) { Remove-Item $ffmpegWork -Recurse -Force }
        if (Test-Path $ffmpegZip) { Remove-Item $ffmpegZip -Force }
        Invoke-WebRequest -Uri 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $ffmpegZip
        Expand-Archive -Path $ffmpegZip -DestinationPath $ffmpegWork -Force
        $fallbackFfmpeg = Get-ChildItem $ffmpegWork -Filter 'ffmpeg.exe' -Recurse | Select-Object -First 1
        if (-not $fallbackFfmpeg) { throw "В FFmpeg Essentials архиве не найден ffmpeg.exe." }
        $fallbackEncoders = (& $fallbackFfmpeg.FullName -hide_banner -encoders 2>&1 | Out-String)
        if ($fallbackEncoders -notmatch 'libmp3lame') { throw "Скачанный FFmpeg не содержит кодировщик libmp3lame." }
        Copy-Item $fallbackFfmpeg.FullName .\release\bin\ffmpeg.exe -Force
        $ffmpegLicense = Get-ChildItem $ffmpegWork -Filter 'LICENSE.txt' -Recurse | Select-Object -First 1
        if ($ffmpegLicense) { Copy-Item $ffmpegLicense.FullName (Join-Path $licenseFolder 'ffmpeg-LICENSE.txt') -Force }
        Remove-Item $ffmpegZip -Force -ErrorAction SilentlyContinue
        Remove-Item $ffmpegWork -Recurse -Force -ErrorAction SilentlyContinue
    }
    $licenseFolder = Join-Path $PSScriptRoot 'release\bin\THIRD_PARTY_LICENSES'
    New-Item -ItemType Directory -Path $licenseFolder -Force | Out-Null
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/llistochek/yandex-music-downloader/main/LICENSE' -OutFile (Join-Path $licenseFolder 'yandex-music-downloader-LICENSE.txt')
    @"
Qlyptra uses the following third-party components in the dist folder:
- yandex-music-downloader by llistochek (MIT): https://github.com/llistochek/yandex-music-downloader
- FFmpeg from imageio-ffmpeg or Gyan.dev Essentials builds (see upstream license materials): https://github.com/imageio/imageio-ffmpeg and https://www.gyan.dev/ffmpeg/builds/
See the upstream project websites for their respective license and build details.
"@ | Set-Content -Encoding UTF8 (Join-Path $licenseFolder 'THIRD_PARTY_NOTICES.txt')

    Write-Host "`n[4/4] Проверка выходных файлов..." -ForegroundColor Cyan
    foreach ($required in @('.\release\Qlyptra.exe', '.\release\bin\ffmpeg.exe', '.\release\bin\yandex_downloader_worker.py', '.\release\bin\ymd-env\Scripts\python.exe')) {
        if (-not (Test-Path $required)) { throw "После сборки отсутствует обязательный файл: $required" }
    }
    Write-Host "`nСборка завершена." -ForegroundColor Green
    Write-Host "Запуск: $PSScriptRoot\release\Qlyptra.exe" -ForegroundColor Green
    Write-Host "Важно: чтобы загрузка треков работала, сохраняй папку release целиком: Qlyptra.exe и папку bin." -ForegroundColor Yellow
    Write-Host "Изображение интерфейса: Assets\app-art.png (необязательно; добавь его перед сборкой)." -ForegroundColor Yellow
} catch {
    Write-Host "`nОшибка сборки: $_" -ForegroundColor Red
    Read-Host "Скопируй сообщение об ошибке и отправь его для исправления. Нажми Enter для выхода"
    exit 1
}
Read-Host "Нажми Enter для выхода"
