# 编译 Windows 原生 helper（cu-helper.exe）
#
# 为什么用 .NET Framework 4.8 + Roslyn，而不是 dotnet SDK 项目：
#   1. 目标机器无 NuGet 访问，dotnet restore 会失败；
#      而 UIAutomationClient / System.Web.Extensions 都是 .NET Framework 自带程序集，零包依赖。
#   2. Windows 自带 csc.exe 只支持 C# 5，写不了现代语法；
#      改用 dotnet SDK 内置的 Roslyn（csc.dll）即可用最新 C# 语法编译 .NET Framework 目标。
#
# 用法: pwsh -File native/windows/build.ps1 [-Configuration Release]

param(
    [string]$Configuration = "Release",
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $here "bin"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# --- 定位 Roslyn 编译器 ---
$roslyn = $null
$sdkRoot = "C:\Program Files\dotnet\sdk"
if (Test-Path $sdkRoot) {
    $roslyn = Get-ChildItem $sdkRoot -Directory |
        Sort-Object { [version]($_.Name -replace '-.*$', '') } -Descending |
        ForEach-Object { Join-Path $_.FullName "Roslyn\bincore\csc.dll" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
}
if (-not $roslyn) { throw "找不到 Roslyn 编译器 (dotnet SDK)。请安装 .NET SDK 或改用 csc.exe 回退路径。" }

# --- 定位 .NET Framework 引用程序集 ---
$fw = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$wpf = Join-Path $fw "WPF"
foreach ($dll in @("mscorlib.dll", "System.dll", "System.Core.dll", "System.Drawing.dll", "System.Web.Extensions.dll")) {
    if (-not (Test-Path (Join-Path $fw $dll))) { throw "缺少 .NET Framework 程序集: $dll" }
}
foreach ($dll in @("UIAutomationClient.dll", "UIAutomationTypes.dll", "WindowsBase.dll")) {
    if (-not (Test-Path (Join-Path $wpf $dll))) { throw "缺少 WPF/UIAutomation 程序集: $dll（需要安装 .NET Framework 4.x）" }
}

# --- 源文件（顺序无关）---
$sources = Get-ChildItem $here -Filter *.cs -File | Sort-Object Name | ForEach-Object { $_.FullName }
if ($sources.Count -eq 0) { throw "没有找到 .cs 源文件" }

$exe = Join-Path $outDir "cu-helper.exe"
# 注意：不能用 $args（PowerShell 保留的自动变量）
$cscArgs = @(
    $roslyn,
    "/nologo",
    "/target:exe",
    "/out:$exe",
    "/platform:x64",
    "/langversion:latest",
    "/codepage:65001",          # 源码为 UTF-8（含中文注释），显式指定代码页避免乱码
    "/optimize+",
    "/nostdlib+",
    "/r:$fw\mscorlib.dll",
    "/r:$fw\System.dll",
    "/r:$fw\System.Core.dll",
    "/r:$fw\System.Drawing.dll",
    "/r:$fw\System.Web.Extensions.dll",
    "/r:$wpf\UIAutomationClient.dll",
    "/r:$wpf\UIAutomationTypes.dll",
    "/r:$wpf\WindowsBase.dll"
)
if ($Configuration -eq "Debug") { $cscArgs += "/define:DEBUG" }
$cscArgs += $sources

if (-not $Quiet) { Write-Host "编译 $($sources.Count) 个源文件 -> $exe" }
$output = & dotnet @cscArgs 2>&1
$exit = $LASTEXITCODE
$output | Where-Object { $_ -match "error|warning" } | Select-Object -First 40 | ForEach-Object { Write-Host $_ }
if ($exit -ne 0) { throw "编译失败 (exit $exit)" }
if (-not $Quiet) {
    $size = (Get-Item $exe).Length
    Write-Host ("编译成功: {0} ({1:N0} 字节)" -f $exe, $size)
}
