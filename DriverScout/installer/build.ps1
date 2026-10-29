# Сборка установщика DriverScout: публикация → подпись exe → упаковка Inno Setup (с подписью установщика).
# Если сертификат не задан (см. sign.ps1), собирается неподписанный установщик.
#   powershell -ExecutionPolicy Bypass -File installer\build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sign = Join-Path $PSScriptRoot 'sign.ps1'
$haveCert = [bool]($env:DRIVERSCOUT_CERT_PFX -or $env:DRIVERSCOUT_CERT_THUMBPRINT)

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Не найден Inno Setup 6 (ISCC.exe)' }

Write-Host '== Публикация'
dotnet publish "$root\DriverScout.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o "$root\publish" -nologo -v q
if ($LASTEXITCODE) { throw 'dotnet publish завершился с ошибкой' }

$isccArgs = @('/Q')
if ($haveCert) {
    Write-Host '== Подпись программы'
    & $sign "$root\publish\DriverScout.exe"
    # ISCC сам вызывает эту команду для установщика и деинсталлятора; $q — кавычка, $f — путь к файлу.
    $isccArgs += '/DSign', "/Sdsign=powershell.exe -NoProfile -ExecutionPolicy Bypass -File `$q$sign`$q `$f"
} else {
    Write-Warning 'Сертификат не задан — установщик будет без подписи'
}

Write-Host '== Упаковка'
& $iscc @isccArgs "$PSScriptRoot\DriverScout.iss"
if ($LASTEXITCODE) { throw 'Inno Setup завершился с ошибкой' }
Get-ChildItem "$PSScriptRoot\Output\*.exe" | Sort-Object LastWriteTime | Select-Object -Last 1 |
    ForEach-Object { Write-Host "Готово: $($_.FullName)" }
