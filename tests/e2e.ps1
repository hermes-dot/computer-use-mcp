#requires -Version 7.0
<#
端到端集成测试：用一个结构已知的 WinForms 靶程序验证 computer use 全链路。

覆盖：
  T1 窗口发现          list_windows 能按标题找到靶窗口
  T2 无障碍快照        get_window_state 返回含关键控件的 UIA 树 + 截图文件
  T3 按元素点击        click(elementIndex) 真的改变了界面状态（status:idle -> status:clicked:1）
  T4 文本输入          type_text 写入文本框（读回 ValuePattern 校验）
  T5 直接赋值          set_value 改写文本框内容
  T6 按键和弦          press_key 发送 Tab 移动焦点
  T7 次要动作          perform_secondary_action(Toggle) 切换复选框
  T8 错误契约          无效元素索引 / 过期 screenshotId 返回结构化错误
  T9 坐标点击          用截图坐标点击按钮（验证缩放换算与遮挡校验）

用法：pwsh -File tests/e2e.ps1
#>
param(
    [switch]$KeepOpen
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$helperExe = Join-Path $repoRoot "native\windows\bin\cu-helper.exe"
$fixtureDir = Join-Path $PSScriptRoot "fixture"
$fixtureExe = Join-Path $fixtureDir "bin\test-target.exe"

# 截图落盘目录必须在工作区内：受沙箱约束的宿主只允许写到白名单目录，
# 而 helper 支持用 CU_HELPER_CACHE_DIR 覆盖默认位置。
$env:CU_HELPER_CACHE_DIR = Join-Path $repoRoot ".tmp\cu-cache"

$script:Passed = 0
$script:Failed = 0
$script:Failures = @()

function Write-Section([string]$text) {
    Write-Host ""
    Write-Host "== $text" -ForegroundColor Cyan
}

function Assert-That([string]$name, [bool]$condition, [string]$detail = "") {
    if ($condition) {
        $script:Passed++
        Write-Host "  PASS  $name" -ForegroundColor Green
    }
    else {
        $script:Failed++
        $script:Failures += $name
        Write-Host "  FAIL  $name" -ForegroundColor Red
        if ($detail) { Write-Host "        $detail" -ForegroundColor DarkGray }
    }
}

# ---------------------------------------------------------------- 编译

function Get-RoslynPath {
    $sdkRoot = "C:\Program Files\dotnet\sdk"
    if (-not (Test-Path $sdkRoot)) { throw "找不到 dotnet SDK" }
    return Get-ChildItem $sdkRoot -Directory |
        Sort-Object { [version]($_.Name -replace '-.*$', '') } -Descending |
        ForEach-Object { Join-Path $_.FullName "Roslyn\bincore\csc.dll" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
}

function Build-Fixture {
    $roslyn = Get-RoslynPath
    $fw = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
    $outDir = Split-Path -Parent $fixtureExe
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $cscArgs = @(
        $roslyn, "/nologo", "/target:winexe", "/out:$fixtureExe", "/platform:x64",
        "/langversion:latest", "/codepage:65001", "/nostdlib+",
        "/r:$fw\mscorlib.dll", "/r:$fw\System.dll", "/r:$fw\System.Core.dll",
        "/r:$fw\System.Drawing.dll", "/r:$fw\System.Windows.Forms.dll",
        (Join-Path $fixtureDir "TestTarget.cs")
    )
    $output = & dotnet @cscArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | Select-Object -First 20 | ForEach-Object { Write-Host $_ }
        throw "测试靶程序编译失败"
    }
}

# ---------------------------------------------------------------- helper 客户端

class HelperClient {
    [System.Diagnostics.Process]$Process
    [System.IO.StreamWriter]$Writer
    [System.IO.StreamReader]$Reader
    [int]$NextId = 1

    HelperClient([string]$exe) {
        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = $exe
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $this.Process = [System.Diagnostics.Process]::Start($psi)
        # 显式 UTF-8（无 BOM）：helper 侧同样按 UTF-8 读写，中文窗口标题才不会乱码。
        $utf8 = [System.Text.UTF8Encoding]::new($false)
        $this.Writer = [System.IO.StreamWriter]::new($this.Process.StandardInput.BaseStream, $utf8)
        $this.Writer.AutoFlush = $true
        $this.Reader = [System.IO.StreamReader]::new($this.Process.StandardOutput.BaseStream, $utf8)
    }

    [object] Call([string]$method, [hashtable]$params) {
        $id = $this.NextId
        $this.NextId++
        if ($null -eq $params) { $params = @{} }
        $request = @{ id = $id; method = $method; params = $params } | ConvertTo-Json -Compress -Depth 8
        $this.Writer.WriteLine($request)
        $line = $this.Reader.ReadLine()
        if ($null -eq $line) { throw "helper 无响应（进程已退出？）" }
        return $line | ConvertFrom-Json
    }

    [void] Close() {
        try { $this.Call("shutdown", @{}) | Out-Null } catch { }
        try { if (-not $this.Process.HasExited) { $this.Process.Kill() } } catch { }
        try { $this.Writer.Dispose() } catch { }
        try { $this.Reader.Dispose() } catch { }
    }
}

# ---------------------------------------------------------------- 树解析辅助

# 从格式化树里找出含指定名字的那一行，返回 @{ Index; Line }
function Find-TreeLine([string]$tree, [string]$needle) {
    foreach ($line in ($tree -split "`n")) {
        if ($line -match "^\s*(\d+)\s" -and $line.Contains($needle)) {
            return @{ Index = [int]$Matches[1]; Line = $line.Trim() }
        }
    }
    return $null
}

function Get-TreeLineByIndex([string]$tree, [int]$index) {
    foreach ($line in ($tree -split "`n")) {
        if ($line -match "^\s*(\d+)\s" -and [int]$Matches[1] -eq $index) { return $line.Trim() }
    }
    return $null
}

# ---------------------------------------------------------------- 主流程

Write-Host "computer-use MCP 端到端测试" -ForegroundColor White
Write-Host "helper: $helperExe"

if (-not (Test-Path $helperExe)) { throw "找不到 $helperExe，请先运行 native/windows/build.ps1" }

Write-Section "编译测试靶程序"
Build-Fixture
Write-Host "  ok -> $fixtureExe"

$fixture = $null
$client = $null
try {
    Write-Section "启动靶程序"
    # 用 CreateProcess 直接启动，而不是 Start-Process：
    # 后者走 ShellExecute，在部分受限/沙箱环境下会被拒绝或挂起。
    $fixture = [System.Diagnostics.Process]::Start([System.Diagnostics.ProcessStartInfo]::new($fixtureExe))
    $targetHwnd = 0
    $client = [HelperClient]::new($helperExe)

    # 等待窗口出现（最多 10 秒）
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $listing = $client.Call("list_windows", @{ detail = $true })
        $match = $listing.result.windows | Where-Object { $_.title -eq "CU Helper Test Target" } | Select-Object -First 1
        if ($match) { $targetHwnd = [long]$match.id; break }
    }

    Write-Section "T1 窗口发现"
    Assert-That "list_windows 找到靶窗口" ($targetHwnd -ne 0) "hwnd=$targetHwnd"
    if ($targetHwnd -eq 0) { throw "靶窗口未出现，后续测试无法继续" }
    Assert-That "窗口 app 标识是进程完整路径" ($match.app -like "*test-target.exe") "app=$($match.app)"
    Assert-That "窗口几何信息可用" ($match.width -gt 100 -and $match.height -gt 100) "size=$($match.width)x$($match.height)"

    Write-Section "T2 无障碍快照 + 截图"
    $state = $client.Call("get_window_state", @{ hwnd = $targetHwnd; includeScreenshot = $true; includeText = $true })
    Assert-That "get_window_state 成功" ($state.ok -eq $true) ($state.error.message)
    if (-not $state.ok) { throw "get_window_state 失败，后续断言都依赖它：$($state.error.code) $($state.error.message)" }
    $tree = $state.result.accessibility.tree
    Assert-That "无障碍树非空" ($tree.Length -gt 0)
    Assert-That "树中包含 clickButton" ($null -ne (Find-TreeLine $tree "clickButton"))
    Assert-That "树中包含 inputBox" ($null -ne (Find-TreeLine $tree "inputBox"))
    Assert-That "树中包含 toggleCheck" ($null -ne (Find-TreeLine $tree "toggleCheck"))
    Assert-That "初始状态可见 status:idle" ($tree.Contains("status:idle")) "tree=$($tree.Substring(0, [Math]::Min(300, $tree.Length)))"

    $shot = $state.result.screenshots[0]
    Assert-That "返回了截图记录" ($null -ne $shot -and $shot.id)
    Assert-That "截图文件已落盘" (Test-Path $shot.path) "path=$($shot.path)"
    Assert-That "截图尺寸合理" ($shot.width -gt 100 -and $shot.height -gt 100) "size=$($shot.width)x$($shot.height)"
    Assert-That "截图缩放比例已上报" ($shot.scale -gt 0 -and $shot.scale -le 1.0) "scale=$($shot.scale)"

    Write-Section "T3 按元素索引点击"
    $button = Find-TreeLine $tree "clickButton"
    $click = $client.Call("click", @{ hwnd = $targetHwnd; elementIndex = $button.Index })
    Assert-That "click 返回成功" ($click.ok -eq $true) ($click.error.message)
    Start-Sleep -Milliseconds 300
    $after = $client.Call("get_window_state", @{ hwnd = $targetHwnd; includeScreenshot = $false; includeText = $true })
    Assert-That "点击改变了界面状态" ($after.result.accessibility.tree.Contains("status:clicked:1")) `
        "tree=$($after.result.accessibility.tree.Substring(0, [Math]::Min(300, $after.result.accessibility.tree.Length)))"

    Write-Section "T4 文本输入"
    $inputLine = Find-TreeLine $after.result.accessibility.tree "inputBox"
    $typed = $client.Call("type_text", @{ hwnd = $targetHwnd; elementIndex = $inputLine.Index; text = "hello 世界 123" })
    Assert-That "type_text 返回成功" ($typed.ok -eq $true) ($typed.error.message)
    Start-Sleep -Milliseconds 300
    $afterType = $client.Call("get_window_state", @{ hwnd = $targetHwnd; includeScreenshot = $false; includeText = $true })
    $inputAfter = Get-TreeLineByIndex $afterType.result.accessibility.tree $inputLine.Index
    Assert-That "文本框内容被写入（含中文）" ($inputAfter -and $inputAfter.Contains("hello 世界 123")) "line=$inputAfter"

    Write-Section "T5 直接赋值 set_value"
    $setResult = $client.Call("set_value", @{ hwnd = $targetHwnd; elementIndex = $inputLine.Index; value = "replaced" })
    Assert-That "set_value 返回成功" ($setResult.ok -eq $true) ($setResult.error.message)
    Start-Sleep -Milliseconds 200
    $afterSet = $client.Call("get_window_state", @{ hwnd = $targetHwnd; includeScreenshot = $false; includeText = $true })
    $inputSet = Get-TreeLineByIndex $afterSet.result.accessibility.tree $inputLine.Index
    Assert-That "set_value 生效" ($inputSet -and $inputSet.Contains("replaced")) "line=$inputSet"

    Write-Section "T6 按键和弦"
    $keyResult = $client.Call("press_key", @{ hwnd = $targetHwnd; key = "Tab" })
    Assert-That "press_key 返回成功" ($keyResult.ok -eq $true) ($keyResult.error.message)
    $keyResult2 = $client.Call("press_key", @{ hwnd = $targetHwnd; key = "Control_L+a" })
    Assert-That "组合键解析并发送成功" ($keyResult2.ok -eq $true) ($keyResult2.error.message)

    Write-Section "T7 次要动作 perform_secondary_action"
    $toggleLine = Find-TreeLine $afterSet.result.accessibility.tree "toggleCheck"
    $toggleResult = $client.Call("perform_secondary_action", @{ hwnd = $targetHwnd; elementIndex = $toggleLine.Index; action = "Toggle" })
    Assert-That "Toggle 动作返回成功" ($toggleResult.ok -eq $true) ($toggleResult.error.message)
    Start-Sleep -Milliseconds 300
    $afterToggle = $client.Call("get_window_state", @{ hwnd = $targetHwnd; includeScreenshot = $false; includeText = $true })
    Assert-That "复选框状态被切换" ($afterToggle.result.accessibility.tree.Contains("status:toggled:")) `
        "tree=$($afterToggle.result.accessibility.tree.Substring(0, [Math]::Min(300, $afterToggle.result.accessibility.tree.Length)))"

    Write-Section "T8 错误契约"
    $badIndex = $client.Call("click", @{ hwnd = $targetHwnd; elementIndex = 99999 })
    Assert-That "越界元素索引返回结构化错误" ($badIndex.ok -eq $false -and $badIndex.error.code -eq "stale_element_index") "code=$($badIndex.error.code)"
    $badShot = $client.Call("click", @{ hwnd = $targetHwnd; x = 10; y = 10; screenshotId = "shot-does-not-exist" })
    Assert-That "过期 screenshotId 返回结构化错误" ($badShot.ok -eq $false -and $badShot.error.code -eq "stale_screenshot") "code=$($badShot.error.code)"
    $badWindow = $client.Call("click", @{ hwnd = 123456789; elementIndex = 0 })
    Assert-That "失效窗口返回结构化错误" ($badWindow.ok -eq $false -and $badWindow.error.code -eq "window_gone") "code=$($badWindow.error.code)"

    Write-Section "T9 坐标点击（含缩放换算）"
    $fresh = $client.Call("get_window_state", @{ hwnd = $targetHwnd; includeScreenshot = $true; includeText = $true })
    $freshShot = $fresh.result.screenshots[0]
    $freshTree = $fresh.result.accessibility.tree
    $btn = Find-TreeLine $freshTree "clickButton"

    # 先用 element_bounds 拿到按钮的真实屏幕矩形，再换算成"截图图像坐标"，
    # 这样验证的是完整的坐标链路：UIA 矩形 -> 窗口内物理坐标 -> 图像坐标(乘 scale) -> SendInput。
    $bounds = $client.Call("element_bounds", @{ hwnd = $targetHwnd; elementIndex = $btn.Index })
    Assert-That "element_bounds 返回非空矩形" ($bounds.ok -eq $true -and -not $bounds.result.empty) `
        "empty=$($bounds.result.empty) code=$($bounds.error.code)"

    if ($bounds.ok -and -not $bounds.result.empty) {
        $centerScreenX = [int]$bounds.result.screenX + [int]([int]$bounds.result.width / 2)
        $centerScreenY = [int]$bounds.result.screenY + [int]([int]$bounds.result.height / 2)
        $scale = [double]$freshShot.scale
        $imageX = [int][Math]::Round(($centerScreenX - [int]$bounds.result.windowLeft) * $scale)
        $imageY = [int][Math]::Round(($centerScreenY - [int]$bounds.result.windowTop) * $scale)

        $coordClick = $client.Call("click", @{ hwnd = $targetHwnd; x = $imageX; y = $imageY; screenshotId = $freshShot.id })
        Assert-That "坐标点击返回成功" ($coordClick.ok -eq $true) "code=$($coordClick.error.code) msg=$($coordClick.error.message)"
        if ($coordClick.ok) {
            Start-Sleep -Milliseconds 300
            $afterCoord = $client.Call("get_window_state", @{ hwnd = $targetHwnd; includeScreenshot = $false; includeText = $true })
            Assert-That "坐标点击命中按钮（状态计数增加）" ($afterCoord.result.accessibility.tree -match "status:clicked:\d+") `
                "tree=$($afterCoord.result.accessibility.tree.Substring(0, [Math]::Min(300, $afterCoord.result.accessibility.tree.Length)))"
        }
    }

    Write-Section "T10 越界与遮挡校验"
    # 目标点若不在窗口范围内、或落在别的窗口上，必须报结构化错误，而不是静默点错地方。
    $outOfRange = $client.Call("click", @{ hwnd = $targetHwnd; x = ([int]$freshShot.width + 400); y = 40; screenshotId = $freshShot.id })
    Assert-That "窗口外坐标被拒绝" ($outOfRange.ok -eq $false -and $outOfRange.error.code -eq "point_outside_window") `
        "code=$($outOfRange.error.code)"
}
finally {
    if ($client) { $client.Close() }
    if ($fixture -and -not $KeepOpen) { try { Stop-Process -Id $fixture.Id -Force -ErrorAction SilentlyContinue } catch { } }
}

Write-Host ""
Write-Host "----------------------------------------"
if ($script:Failed -eq 0) {
    Write-Host "全部通过：$($script:Passed) 项" -ForegroundColor Green
    exit 0
}
else {
    Write-Host "通过 $($script:Passed) 项，失败 $($script:Failed) 项：" -ForegroundColor Red
    foreach ($f in $script:Failures) { Write-Host "  - $f" -ForegroundColor Red }
    exit 1
}
