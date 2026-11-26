param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '未找到 .NET SDK。请先安装 .NET 10 SDK：https://dotnet.microsoft.com/download/dotnet/10.0'
}
$sdks = @(& dotnet --list-sdks)
if ($LASTEXITCODE -ne 0 -or -not ($sdks | Where-Object { $_ -match '^(1[0-9]|[2-9][0-9])\.' })) {
    throw '构建需要 .NET 10 SDK 或更新版本。仅安装运行时无法编译。'
}

$projectPath = Join-Path $PSScriptRoot 'BwgTopBar\BwgTopBar.csproj'
$outputPath = Join-Path $PSScriptRoot ('publish\' + $Runtime)
& dotnet publish $projectPath -c Release -r $Runtime --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=None /p:DebugSymbols=false -o $outputPath
if ($LASTEXITCODE -ne 0) {
    throw '构建失败。请检查上方错误，以及 SDK 和 NuGet 网络连接。'
}
Write-Host ('构建完成：' + (Join-Path $outputPath 'BwgTopBar.exe'))
