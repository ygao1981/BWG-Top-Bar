# 在本机已安装并登录 GitHub CLI 的 Windows PowerShell 中运行。
# 本脚本只用于创建全新仓库。当前项目仓库已经存在，不要运行此脚本。
[CmdletBinding()]
param(
    [ValidateSet('private', 'public')]
    [string]$Visibility = 'private'
)
$ErrorActionPreference = 'Stop'
$repo = 'ygao1981/BWG-Top-Bar'
Set-Location $PSScriptRoot

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw '未找到 GitHub CLI。请先安装 https://cli.github.com/，再执行 gh auth login。'
}
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw '未找到 Git。请先安装 Git for Windows。'
}
& gh auth status | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'GitHub CLI 尚未登录。请先执行 gh auth login，然后重试。'
}
& gh repo view $repo --json name 1>$null 2>$null
if ($LASTEXITCODE -eq 0) {
    throw "仓库 $repo 已存在。为避免覆盖，脚本未自动推送。"
}
if (-not (Test-Path .git)) {
    & git init -b main
    if ($LASTEXITCODE -ne 0) { throw 'git init 失败' }
    & git add README.md BwgTopBar build-win.ps1 Windows验证清单.md .gitignore .github publish-github.ps1
    if ($LASTEXITCODE -ne 0) { throw 'git add 失败' }
    & git commit -m 'Initial import: BWG Top Bar 0.2'
    if ($LASTEXITCODE -ne 0) { throw 'git commit 失败；请检查 Git 用户名和邮箱配置' }
}
$flag = if ($Visibility -eq 'public') { '--public' } else { '--private' }
& gh repo create $repo $flag --source . --remote origin --push --description 'Windows single-line floating bar for BandwagonHost KiwiVM traffic and VPS resources'
if ($LASTEXITCODE -ne 0) { throw '仓库创建或推送失败，请检查 GitHub 登录和权限。' }
Write-Host "已推送：https://github.com/$repo"
