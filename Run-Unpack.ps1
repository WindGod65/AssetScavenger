[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$InputPath,

    [ValidateSet('auto', 'unity', 'unreal', 'unknown')]
    [string]$Engine = 'auto',

    [ValidateSet('scan', 'standard', 'full', 'unityProject')]
    [string]$Profile = 'standard',

    [ValidateSet('images', 'audio', 'models', 'text')]
    [string[]]$AssetKinds = @('images', 'audio', 'models', 'text'),

    [ValidateSet('obj', 'fbx', 'gltf')]
    [string[]]$ModelFormats = @('obj'),

    [string]$UnityVersion = '',
    [string]$UnrealAesKey = '',
    [string]$OutputRoot = '',
    [int]$MaxExportTasks = 0
)

$ErrorActionPreference = 'Stop'
$script:PipelineRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:ConfigPath = Join-Path $script:PipelineRoot 'config.json'
$script:Issues = New-Object System.Collections.Generic.List[string]
$script:Warnings = New-Object System.Collections.Generic.List[string]
$script:ToolRuns = New-Object System.Collections.Generic.List[object]
$script:WorkTotal = 1
$script:WorkIndex = 0
$script:WorkStartPercent = 20
$script:WorkEndPercent = 80

function Write-Step([string]$Message) {
    $caller = @(Get-PSCallStack)[1]
    $location = if ($caller -and $caller.FunctionName -and $caller.FunctionName -ne '<ScriptBlock>') { $caller.FunctionName } else { '主流程' }
    Write-Host ("[{0}] [脚本 {1}] {2}......." -f (Get-Date -Format 'HH:mm:ss'), $location, $Message)
}

function Write-ProgressStep {
    param([int]$Percent, [string]$Message, [string]$Location = '')
    $value = [Math]::Max(0, [Math]::Min(100, $Percent))
    if ([string]::IsNullOrWhiteSpace($Location)) {
        $caller = @(Get-PSCallStack)[1]
        $Location = if ($caller -and $caller.FunctionName -and $caller.FunctionName -ne '<ScriptBlock>') { $caller.FunctionName } else { '主流程' }
    }
    Write-Host ("[进度 {0:000}%] [{1}] [脚本 {2}] {3}......." -f $value, (Get-Date -Format 'HH:mm:ss'), $Location, $Message)
}

function Start-WorkItem([string]$Message) {
    $caller = @(Get-PSCallStack)[1]
    $location = if ($caller -and $caller.FunctionName) { $caller.FunctionName } else { '主流程' }
    $script:WorkIndex++
    $script:WorkStartPercent = 20 + [int][Math]::Floor((($script:WorkIndex - 1) * 60.0) / [Math]::Max(1, $script:WorkTotal))
    $script:WorkEndPercent = 20 + [int][Math]::Floor(($script:WorkIndex * 60.0) / [Math]::Max(1, $script:WorkTotal))
    Write-ProgressStep -Percent $script:WorkStartPercent -Message ("开始 {0}（任务 {1}/{2}）" -f $Message, $script:WorkIndex, $script:WorkTotal) -Location $location
}

function Complete-WorkItem([string]$Message) {
    $caller = @(Get-PSCallStack)[1]
    $location = if ($caller -and $caller.FunctionName) { $caller.FunctionName } else { '主流程' }
    Write-ProgressStep -Percent $script:WorkEndPercent -Message ("完成 {0}（任务 {1}/{2}）" -f $Message, $script:WorkIndex, $script:WorkTotal) -Location $location
}

function Get-SafeName([string]$Name) {
    $safe = $Name
    foreach ($character in [System.IO.Path]::GetInvalidFileNameChars()) {
        $safe = $safe.Replace([string]$character, '_')
    }
    if ([string]::IsNullOrWhiteSpace($safe)) { return 'unnamed' }
    return $safe.Trim()
}

function Resolve-ConfiguredPath([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    if ([System.IO.Path]::IsPathRooted($Value)) { return $Value }
    return Join-Path $script:PipelineRoot $Value
}

function Invoke-LoggedTool {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
        throw "找不到工具：$FilePath"
    }

    Start-WorkItem -Message $Name
    $oldLocation = Get-Location
    try {
        Set-Location -LiteralPath (Split-Path -Parent $LogPath)
        & $FilePath @Arguments 2>&1 | Tee-Object -FilePath $LogPath | ForEach-Object { Write-Host $_ }
        $exitCode = $LASTEXITCODE
    }
    finally {
        Set-Location -LiteralPath $oldLocation
    }

    $script:ToolRuns.Add([pscustomobject]@{
        name = $Name
        exitCode = $exitCode
        log = $LogPath
        arguments = ($Arguments -join ' ')
    })
    Complete-WorkItem -Message $Name
    if ($exitCode -ne 0) {
        $script:Issues.Add("$Name 返回退出码 $exitCode，详情见 $LogPath")
    }
    return $exitCode
}

function Expand-ZipSafely {
    param([string]$ArchivePath, [string]$Destination)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $destinationFull = [System.IO.Path]::GetFullPath($Destination)
    if (-not $destinationFull.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $destinationFull += [System.IO.Path]::DirectorySeparatorChar
    }
    New-Item -ItemType Directory -Path $destinationFull -Force | Out-Null

    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in $archive.Entries) {
            $target = [System.IO.Path]::GetFullPath((Join-Path $destinationFull $entry.FullName))
            if (-not $target.StartsWith($destinationFull, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "APK/ZIP 包含不安全路径：$($entry.FullName)"
            }
            if ([string]::IsNullOrEmpty($entry.Name)) {
                New-Item -ItemType Directory -Path $target -Force | Out-Null
                continue
            }
            $parent = Split-Path -Parent $target
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
            if (Test-Path -LiteralPath $target) {
                throw "目标文件意外存在，为避免覆盖已停止：$target"
            }
            $inputStream = $entry.Open()
            try {
                $outputStream = [System.IO.File]::Open($target, [System.IO.FileMode]::CreateNew)
                try { $inputStream.CopyTo($outputStream) }
                finally { $outputStream.Dispose() }
            }
            finally { $inputStream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}

function Get-FilePrefix([string]$Path, [int]$Count = 16) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $buffer = New-Object byte[] $Count
        $read = $stream.Read($buffer, 0, $buffer.Length)
        if ($read -eq $buffer.Length) { return $buffer }
        return $buffer[0..([Math]::Max(0, $read - 1))]
    }
    finally { $stream.Dispose() }
}

function Test-PathContainsName([string]$Root, [string[]]$Names) {
    foreach ($name in $Names) {
        if (Get-ChildItem -LiteralPath $Root -Recurse -File -Filter $name -ErrorAction SilentlyContinue | Select-Object -First 1) {
            return $true
        }
    }
    return $false
}

function Get-DetectedEngine([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $extension = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()
        if ($extension -in @('.ab', '.bundle', '.unity3d', '.assets')) { return 'unity' }
        if ($extension -eq '.pak') { return 'unreal' }
        if ($extension -eq '.apk') {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
            try {
                $names = $zip.Entries | ForEach-Object { $_.FullName.ToLowerInvariant() }
                if ($names -match '(^|/)libunity\.so$' -or $names -match '(^|/)libil2cpp\.so$' -or $names -match '^assets/bin/data/') { return 'unity' }
                if ($names -match '(^|/)libue4\.so$' -or $names -match '(^|/)libunreal\.so$' -or $names -match '\.pak$') { return 'unreal' }
            }
            finally { $zip.Dispose() }
            return 'unknown'
        }
        $prefix = Get-FilePrefix -Path $Path -Count 8
        $ascii = [System.Text.Encoding]::ASCII.GetString($prefix)
        if ($ascii.StartsWith('UnityFS') -or $ascii.StartsWith('UnityWeb') -or $ascii.StartsWith('UnityRaw')) { return 'unity' }
        return 'unknown'
    }

    if (Test-PathContainsName $Path @('libunity.so', 'libil2cpp.so', 'globalgamemanagers')) { return 'unity' }
    if (Test-PathContainsName $Path @('libUE4.so', 'libUnreal.so')) { return 'unreal' }
    if (Get-ChildItem -LiteralPath $Path -Recurse -File -Filter '*.pak' -ErrorAction SilentlyContinue | Select-Object -First 1) { return 'unreal' }
    if (Get-ChildItem -LiteralPath $Path -Recurse -File -Include '*.ab', '*.bundle', '*.unity3d' -ErrorAction SilentlyContinue | Select-Object -First 1) { return 'unity' }
    return 'unknown'
}

function Find-UnityVersion([string]$Root) {
    $candidates = @()
    if (Test-Path -LiteralPath $Root -PathType Leaf) {
        $candidates = @((Get-Item -LiteralPath $Root))
    }
    else {
        $candidates = @(Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -in @('globalgamemanagers', 'data.unity3d', 'mainData') } |
            Select-Object -First 8)
    }
    foreach ($file in $candidates) {
        try {
            $stream = [System.IO.File]::OpenRead($file.FullName)
            try {
                $length = [Math]::Min(4194304, $stream.Length)
                $bytes = New-Object byte[] $length
                [void]$stream.Read($bytes, 0, $bytes.Length)
            }
            finally { $stream.Dispose() }
            $text = [System.Text.Encoding]::ASCII.GetString($bytes)
            $match = [regex]::Match($text, '20\d{2}\.\d+\.\d+[abfp]\d+')
            if ($match.Success) { return $match.Value }
        }
        catch { }
    }
    return ''
}

function Test-Il2CppMetadata([string]$Root) {
    if (Test-Path -LiteralPath $Root -PathType Leaf) { return }
    $metadataFiles = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter 'global-metadata.dat' -ErrorAction SilentlyContinue)
    foreach ($metadata in $metadataFiles) {
        try {
            $head = Get-FilePrefix -Path $metadata.FullName -Count 4
            $validMagic = ($head.Length -eq 4 -and $head[0] -eq 0xAF -and $head[1] -eq 0x1B -and $head[2] -eq 0xB1 -and $head[3] -eq 0xFA)
            if (-not $validMagic) {
                $script:Warnings.Add("IL2CPP metadata 不是标准格式，脚本/MonoBehaviour 类型恢复可能受限；这不会阻止普通 PNG、WAV 和 Mesh 导出：$($metadata.FullName)")
            }
        }
        catch {
            $script:Issues.Add("无法检查 IL2CPP metadata：$($metadata.FullName)；$($_.Exception.Message)")
        }
    }
}

function Invoke-AssetStudio {
    param([string]$Mode, [string]$Types, [string]$Output, [string]$LogName, [string[]]$Extra = @())
    $arguments = @($script:SourcePath, '-m', $Mode, '-o', $Output, '--log-level', 'info', '--log-output', 'both', '--max-export-tasks', [string]$script:MaxTasks)
    if (-not [string]::IsNullOrWhiteSpace($Types)) { $arguments += @('-t', $Types) }
    if (-not [string]::IsNullOrWhiteSpace($script:DetectedUnityVersion)) { $arguments += @('--unity-version', $script:DetectedUnityVersion) }
    $arguments += $Extra
    return Invoke-LoggedTool -FilePath $script:AssetStudioCli -Arguments $arguments -LogPath (Join-Path $script:Logs "$LogName.log") -Name "AssetStudioMod $LogName"
}

function Export-UnityAssets {
    Test-Il2CppMetadata -Root $script:SourcePath
    if ($Profile -eq 'unityProject') {
        Export-UnityProject
        return
    }
    [void](Invoke-AssetStudio -Mode 'info' -Types '' -Output $script:ReportDir -LogName 'unity_info' -Extra @('--export-asset-list', 'xml'))
    if ($Profile -eq 'scan') { return }

    if ($Profile -eq 'full') {
        [void](Invoke-AssetStudio -Mode 'extract' -Types '' -Output $script:RawAssets -LogName 'unity_extract_raw')
    }
    if ($AssetKinds -contains 'images') {
        [void](Invoke-AssetStudio -Mode 'export' -Types 'tex2d,sprite' -Output $script:Images -LogName 'unity_images' -Extra @('-g', 'fileName', '-f', 'assetName_pathID', '--image-format', $script:Config.imageFormat))
    }
    if ($AssetKinds -contains 'audio') {
        [void](Invoke-AssetStudio -Mode 'export' -Types 'audio' -Output $script:Audio -LogName 'unity_audio' -Extra @('-g', 'fileName', '-f', 'assetName_pathID', '--audio-format', $script:Config.audioFormat))
    }
    if ($AssetKinds -contains 'text') {
        [void](Invoke-AssetStudio -Mode 'export' -Types 'textAsset,monoBehaviour,font,shader' -Output $script:Text -LogName 'unity_text' -Extra @('-g', 'fileName', '-f', 'assetName_pathID'))
    }
    if ($AssetKinds -contains 'models') {
        # SplitObjects/FBX builds a complete scene tree and can abort when a game uses node names
        # that contain Windows-invalid path characters. Direct Mesh export avoids that tree and
        # produces portable OBJ files while preserving AssetName + PathID uniqueness.
        $selectedModelFormats = @($ModelFormats | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique)
        $objRoot = if ($selectedModelFormats -contains 'obj') { Join-Path $script:Models 'OBJ' } else { Join-Path $script:RawAssets '_model_obj_intermediate' }
        New-Item -ItemType Directory -Path $objRoot -Force | Out-Null
        [void](Invoke-AssetStudio -Mode 'export' -Types 'mesh' -Output $objRoot -LogName 'unity_models_obj' -Extra @('-g', 'fileName', '-f', 'assetName_pathID'))

        $convertedFormats = @($selectedModelFormats | Where-Object { $_ -in @('fbx', 'gltf') })
        if ($convertedFormats.Count -gt 0) {
            $converter = Join-Path $script:PipelineRoot 'tools\MeshFormatConverter\MeshFormatConverter.exe'
            if (Test-Path -LiteralPath $converter -PathType Leaf) {
                [void](Invoke-LoggedTool -FilePath $converter -Arguments @('--batch', $objRoot, $script:Models, ($convertedFormats -join ',')) -LogPath (Join-Path $script:Logs 'unity_models_convert.log') -Name 'Mesh 格式转换')
            }
            else {
                $script:Issues.Add("缺少模型格式转换器：$converter")
            }
        }
    }
}

function Invoke-AssetRipperPost {
    param([string]$Uri, [string]$Path, [string]$Stage)
    Start-WorkItem -Message $Stage
    $client = New-Object System.Net.WebClient
    try {
        $values = New-Object System.Collections.Specialized.NameValueCollection
        $values['Path'] = $Path
        $task = $client.UploadValuesTaskAsync([Uri]$Uri, 'POST', $values)
        $lastPercent = -1
        while (-not $task.Wait(1000)) {
            $percent = -1
            if (Test-Path -LiteralPath $script:AssetRipperLog -PathType Leaf) {
                $progressLine = Get-Content -LiteralPath $script:AssetRipperLog -Tail 80 -ErrorAction SilentlyContinue |
                    Select-String -Pattern '\((\d+)/(\d+)\)' | Select-Object -Last 1
                if ($progressLine -and $progressLine.Matches.Count -gt 0) {
                    $current = [double]$progressLine.Matches[0].Groups[1].Value
                    $total = [double]$progressLine.Matches[0].Groups[2].Value
                    if ($total -gt 0) { $percent = [Math]::Min(100, [Math]::Floor($current * 100 / $total)) }
                }
            }
            if ($percent -ge 0 -and $percent -ne $lastPercent) {
                $mappedPercent = $script:WorkStartPercent + [int][Math]::Floor(($percent / 100.0) * ($script:WorkEndPercent - $script:WorkStartPercent))
                Write-ProgressStep -Percent $mappedPercent -Message ("{0}：AssetRipper 内部进度 {1}%" -f $Stage, $percent) -Location 'Invoke-AssetRipperPost'
                $lastPercent = $percent
            }
            elseif ([int]((Get-Date) - $script:AssetRipperStageStarted).TotalSeconds % 10 -eq 0) {
                Write-Step ("AssetRipper 正在处理，已等待 {0:n0} 秒" -f ((Get-Date) - $script:AssetRipperStageStarted).TotalSeconds)
            }
        }
        if ($task.IsFaulted) { throw $task.Exception.GetBaseException() }
        Complete-WorkItem -Message $Stage
    }
    finally { $client.Dispose() }
}

function Export-UnityProject {
    if (-not (Test-Path -LiteralPath $script:AssetRipper -PathType Leaf)) {
        $script:Issues.Add("找不到 AssetRipper：$($script:AssetRipper)")
        return
    }
    if (-not (Test-Path -LiteralPath $script:SourcePath)) {
        $script:Issues.Add("Unity 工程重建输入不存在：$($script:SourcePath)")
        return
    }

    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    $assetRipperLog = Join-Path $script:Logs 'assetripper_unity_project.log'
    $script:AssetRipperLog = $assetRipperLog
    $serverUrl = "http://127.0.0.1:$port"
    $argumentString = "--port $port --headless --log-path `"$assetRipperLog`""
    Write-Step "启动 AssetRipper Unity 工程重建服务（端口 $port）"
    $process = Start-Process -FilePath $script:AssetRipper -ArgumentList $argumentString -WorkingDirectory (Split-Path -Parent $script:AssetRipper) -WindowStyle Hidden -PassThru
    try {
        $ready = $false
        for ($attempt = 0; $attempt -lt 120; $attempt++) {
            if ($process.HasExited) { throw "AssetRipper 提前退出，退出码 $($process.ExitCode)" }
            try {
                $response = Invoke-WebRequest -UseBasicParsing -Uri ($serverUrl + '/') -TimeoutSec 2
                if ($response.StatusCode -eq 200) { $ready = $true; break }
            }
            catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $ready) { throw 'AssetRipper 本地服务在 60 秒内没有启动。' }

        $script:AssetRipperStageStarted = Get-Date
        $loadEndpoint = if (Test-Path -LiteralPath $script:SourcePath -PathType Container) { '/LoadFolder' } else { '/LoadFile' }
        Invoke-AssetRipperPost -Uri ($serverUrl + $loadEndpoint) -Path $script:SourcePath -Stage 'AssetRipper 正在载入 Unity 游戏文件'
        $script:AssetRipperStageStarted = Get-Date
        Invoke-AssetRipperPost -Uri ($serverUrl + '/Export/UnityProject') -Path $script:UnityProject -Stage 'AssetRipper 正在导出可编辑 Unity 工程'
    }
    catch {
        $script:Issues.Add("AssetRipper Unity 工程导出失败：$($_.Exception.Message)")
    }
    finally {
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            [void]$process.WaitForExit(5000)
        }
    }

    $projectCandidate = if (Test-Path -LiteralPath (Join-Path $script:UnityProject 'ExportedProject\Assets') -PathType Container) {
        Join-Path $script:UnityProject 'ExportedProject'
    }
    elseif (Test-Path -LiteralPath (Join-Path $script:UnityProject 'Assets') -PathType Container) {
        $script:UnityProject
    }
    else { '' }
    if ($projectCandidate) {
        $script:UnityProjectPath = $projectCandidate
        $script:UnityProjectFiles = @(Get-ChildItem -LiteralPath $projectCandidate -Recurse -File -ErrorAction SilentlyContinue).Count
        if (-not (Test-Path -LiteralPath (Join-Path $projectCandidate 'ProjectSettings') -PathType Container)) {
            $script:Issues.Add('AssetRipper 已导出 Assets，但缺少 ProjectSettings；工程结构不完整。')
        }
        $script:Warnings.Add('Unity 工程是尽力重建结果。缺失脚本、受保护的 IL2CPP metadata、渠道 SDK 和服务器逻辑仍可能导致工程无法直接运行。')
    }
    else {
        $script:Issues.Add('AssetRipper 未生成 Assets 目录，Unity 工程导出没有完成。')
    }
}

function Copy-CategorizedUnrealFiles([string]$ExportRoot) {
    $mapping = @{
        '.png' = $script:Images; '.tga' = $script:Images; '.dds' = $script:Images; '.bmp' = $script:Images
        '.wav' = $script:Audio; '.ogg' = $script:Audio; '.mp3' = $script:Audio; '.wem' = $script:Audio
        '.fbx' = $script:Models; '.gltf' = $script:Models; '.glb' = $script:Models; '.psk' = $script:Models; '.pskx' = $script:Models; '.psa' = $script:Models
        '.txt' = $script:Text; '.json' = $script:Text; '.xml' = $script:Text; '.ini' = $script:Text; '.csv' = $script:Text
    }
    foreach ($file in Get-ChildItem -LiteralPath $ExportRoot -Recurse -File -ErrorAction SilentlyContinue) {
        $extension = $file.Extension.ToLowerInvariant()
        if (-not $mapping.ContainsKey($extension)) { continue }
        $relative = $file.FullName.Substring($ExportRoot.TrimEnd('\').Length).TrimStart('\')
        $destination = Join-Path $mapping[$extension] $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        if (-not (Test-Path -LiteralPath $destination)) {
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
}

function Export-UnrealAssets {
    $pakFiles = @()
    if (Test-Path -LiteralPath $script:SourcePath -PathType Leaf) { $pakFiles = @((Get-Item -LiteralPath $script:SourcePath)) }
    else { $pakFiles = @(Get-ChildItem -LiteralPath $script:SourcePath -Recurse -File -Filter '*.pak' -ErrorAction SilentlyContinue) }
    if ($pakFiles.Count -eq 0) {
        $script:Issues.Add('识别为 Unreal，但未找到 .pak 文件。')
        return
    }

    $gameRoot = if (Test-Path -LiteralPath $script:SourcePath -PathType Leaf) { Split-Path -Parent $script:SourcePath } else { $script:SourcePath }
    $common = @("-path=$gameRoot")
    if (-not [string]::IsNullOrWhiteSpace($UnrealAesKey)) { $common += "-aes=$UnrealAesKey" }
    [void](Invoke-LoggedTool -FilePath $script:Umodel -Arguments ($common + @('-list', '*')) -LogPath (Join-Path $script:Logs 'unreal_list.log') -Name 'UModel 预检')
    if ($Profile -eq 'scan') { return }

    $unrealOut = Join-Path $script:RawAssets 'umodel_export'
    New-Item -ItemType Directory -Path $unrealOut -Force | Out-Null
    $args = $common + @('-export', '-sounds', '-gltf', '-png', '-nooverwrite', "-out=$unrealOut", '*')
    [void](Invoke-LoggedTool -FilePath $script:Umodel -Arguments $args -LogPath (Join-Path $script:Logs 'unreal_export.log') -Name 'UModel 导出')
    Copy-CategorizedUnrealFiles -ExportRoot $unrealOut

    $combinedLog = (Get-Content -LiteralPath (Join-Path $script:Logs 'unreal_export.log') -Raw -ErrorAction SilentlyContinue)
    if ($combinedLog -match '(?i)encrypted|aes key|encryption') {
        $script:Issues.Add('UModel 日志显示 PAK 可能已加密；需要游戏对应且合法获得的 AES key。')
    }
}

function Test-ExportedFiles {
    $results = New-Object System.Collections.Generic.List[object]
    $files = @(Get-ChildItem -LiteralPath @($script:Images, $script:Audio, $script:Models, $script:Text) -Recurse -File -ErrorAction SilentlyContinue)
    foreach ($file in $files) {
        $status = 'ok'
        $detail = '非空文件'
        try {
            if ($file.Length -eq 0) { throw '文件大小为 0' }
            switch ($file.Extension.ToLowerInvariant()) {
                '.png' {
                    $head = Get-FilePrefix $file.FullName 8
                    $expected = @(0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A)
                    for ($i = 0; $i -lt 8; $i++) { if ($head[$i] -ne $expected[$i]) { throw 'PNG 文件头无效' } }
                    $detail = 'PNG 文件头有效'
                }
                '.wav' {
                    $head = Get-FilePrefix $file.FullName 44
                    if ([System.Text.Encoding]::ASCII.GetString($head,0,4) -ne 'RIFF' -or [System.Text.Encoding]::ASCII.GetString($head,8,4) -ne 'WAVE') { throw 'WAV 文件头无效' }
                    $channels = [BitConverter]::ToUInt16($head,22)
                    $rate = [BitConverter]::ToUInt32($head,24)
                    $bits = [BitConverter]::ToUInt16($head,34)
                    $detail = "WAV $channels 声道 / $rate Hz / $bits bit"
                }
                '.json' {
                    [void](Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json)
                    $detail = 'JSON 可解析'
                }
                '.xml' {
                    [xml](Get-Content -LiteralPath $file.FullName -Raw) | Out-Null
                    $detail = 'XML 可解析'
                }
                '.fbx' {
                    $head = Get-FilePrefix $file.FullName 23
                    $prefix = [System.Text.Encoding]::ASCII.GetString($head)
                    if (-not ($prefix.StartsWith('Kaydara FBX Binary') -or $prefix.StartsWith('; FBX'))) { throw 'FBX 头部无法识别' }
                    $detail = 'FBX 头部有效'
                }
                '.gltf' {
                    $gltf = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
                    foreach ($buffer in @($gltf.buffers)) {
                        if ($buffer.uri -and -not ([string]$buffer.uri).StartsWith('data:')) {
                            $bufferPath = Join-Path $file.DirectoryName ([string]$buffer.uri)
                            if (-not (Test-Path -LiteralPath $bufferPath -PathType Leaf)) { throw "glTF 缺少缓冲区文件：$($buffer.uri)" }
                        }
                    }
                    $detail = 'glTF JSON 可解析，外部缓冲区存在'
                }
                '.glb' {
                    $head = Get-FilePrefix $file.FullName 4
                    if ([System.Text.Encoding]::ASCII.GetString($head) -ne 'glTF') { throw 'GLB 文件头无效' }
                    $detail = 'GLB 文件头有效'
                }
            }
        }
        catch {
            $status = 'failed'
            $detail = $_.Exception.Message
            $script:Issues.Add("格式验证失败：$($file.FullName)；$detail")
        }
        $results.Add([pscustomobject]@{ path = $file.FullName; extension = $file.Extension; bytes = $file.Length; status = $status; detail = $detail })
    }
    return $results.ToArray()
}

if (-not (Test-Path -LiteralPath $script:ConfigPath -PathType Leaf)) { throw "找不到配置文件：$script:ConfigPath" }
$script:Config = Get-Content -LiteralPath $script:ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$resolvedInput = (Resolve-Path -LiteralPath $InputPath).Path
$script:AssetStudioCli = Resolve-ConfiguredPath $script:Config.assetStudioCli
$script:Umodel = Resolve-ConfiguredPath $script:Config.umodel
$script:AssetRipper = Resolve-ConfiguredPath $script:Config.assetRipperGui
$script:MaxTasks = if ($MaxExportTasks -gt 0) { $MaxExportTasks } else { [int]$script:Config.defaultMaxExportTasks }

$baseName = if (Test-Path -LiteralPath $resolvedInput -PathType Leaf) { [System.IO.Path]::GetFileNameWithoutExtension($resolvedInput) } else { Split-Path -Leaf $resolvedInput }
$baseName = Get-SafeName $baseName
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $script:PipelineRoot 'jobs' }
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$jobName = '{0}_{1}' -f $baseName, (Get-Date -Format 'yyyyMMdd_HHmmss')
$script:JobRoot = Join-Path $OutputRoot $jobName
$suffix = 1
while (Test-Path -LiteralPath $script:JobRoot) {
    $script:JobRoot = Join-Path $OutputRoot ("{0}_{1}" -f $jobName, $suffix)
    $suffix++
}

$script:ReportDir = Join-Path $script:JobRoot '00_report'
$rawApk = Join-Path $script:JobRoot '01_raw_apk'
$script:RawAssets = Join-Path $script:JobRoot '02_assets_raw'
$script:Images = Join-Path $script:JobRoot '03_images'
$script:Audio = Join-Path $script:JobRoot '04_audio'
$script:Models = Join-Path $script:JobRoot '05_models'
$script:Text = Join-Path $script:JobRoot '06_text'
$unresolved = Join-Path $script:JobRoot '07_unresolved'
$script:UnityProject = Join-Path $script:JobRoot '08_unity_project'
$script:Logs = Join-Path $script:JobRoot 'logs'
@($script:ReportDir, $rawApk, $script:RawAssets, $script:Images, $script:Audio, $script:Models, $script:Text, $unresolved, $script:Logs) |
    ForEach-Object { New-Item -ItemType Directory -Path $_ -Force | Out-Null }

$started = Get-Date
Write-ProgressStep -Percent 2 -Message "已创建任务目录：$($script:JobRoot)"
Write-ProgressStep -Percent 4 -Message "正在准备输入：$resolvedInput"
if ([System.IO.Path]::GetExtension($resolvedInput).ToLowerInvariant() -eq '.apk') {
    Write-Step '安全展开 APK（阻止路径穿越，不覆盖现有文件）'
    Expand-ZipSafely -ArchivePath $resolvedInput -Destination $rawApk
    $script:SourcePath = $rawApk
    Write-ProgressStep -Percent 10 -Message 'APK 安全展开完成'
}
else {
    $script:SourcePath = $resolvedInput
    Set-Content -LiteralPath (Join-Path $rawApk 'SOURCE_PATH.txt') -Value $resolvedInput -Encoding UTF8
    Write-ProgressStep -Percent 10 -Message '输入路径登记完成，无需展开 APK'
}

Write-ProgressStep -Percent 12 -Message '正在识别游戏引擎和版本'
$detected = if ($Engine -eq 'auto') { Get-DetectedEngine $script:SourcePath } else { $Engine }
$script:DetectedUnityVersion = $UnityVersion
if ($detected -eq 'unity' -and [string]::IsNullOrWhiteSpace($script:DetectedUnityVersion)) {
    $script:DetectedUnityVersion = Find-UnityVersion $script:SourcePath
}
if ($detected -eq 'unity' -and [string]::IsNullOrWhiteSpace($script:DetectedUnityVersion)) {
    $script:DetectedUnityVersion = [string]$script:Config.defaultUnityVersion
}
Write-ProgressStep -Percent 15 -Message "识别引擎：$detected"
if ($script:DetectedUnityVersion) { Write-Step "Unity 版本：$($script:DetectedUnityVersion)" }

if ($detected -eq 'unity') {
    if ($Profile -eq 'unityProject') {
        $script:WorkTotal = 2
    }
    else {
        $script:WorkTotal = 1 + $(if ($Profile -eq 'full') { 1 } else { 0 }) + $(if ($Profile -eq 'scan') { 0 } else { @($AssetKinds).Count })
        if ($Profile -ne 'scan' -and $AssetKinds -contains 'models' -and @($ModelFormats | Where-Object { $_ -in @('fbx', 'gltf') }).Count -gt 0) { $script:WorkTotal++ }
    }
}
elseif ($detected -eq 'unreal') {
    $script:WorkTotal = if ($Profile -eq 'scan') { 1 } else { 2 }
}
$script:WorkTotal = [Math]::Max(1, $script:WorkTotal)
Write-Step ("计划执行 {0} 个主要处理任务" -f $script:WorkTotal)

try {
    switch ($detected) {
        'unity' { Export-UnityAssets }
        'unreal' { Export-UnrealAssets }
        default {
            $script:Issues.Add('未识别出 Unity 或 Unreal。已保留输入信息，没有盲目调用错误的解包器。')
        }
    }
}
catch {
    $script:Issues.Add("流水线异常：$($_.Exception.Message)")
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $script:Logs 'pipeline_exception.log') -Encoding UTF8
}

Write-ProgressStep -Percent 84 -Message '正在汇总工具日志并检查错误'
foreach ($log in Get-ChildItem -LiteralPath $script:Logs -File -Filter '*.log' -ErrorAction SilentlyContinue) {
    $matches = Select-String -LiteralPath $log.FullName -Pattern '(?i)\[error\]|\[failed\]|exception|encrypted|missing|unsupported|can.not find|magic check|failed\s*=\s*[1-9]' -ErrorAction SilentlyContinue | Select-Object -First 50
    foreach ($match in $matches) {
        $script:Issues.Add("$($log.Name): $($match.Line.Trim())")
    }
}

Write-ProgressStep -Percent 89 -Message '正在验证已导出文件格式和完整性'
$validation = @(Test-ExportedFiles)
$failedValidation = @($validation | Where-Object status -eq 'failed')
$counts = [ordered]@{
    images = @(Get-ChildItem -LiteralPath $script:Images -Recurse -File -ErrorAction SilentlyContinue).Count
    audio = @(Get-ChildItem -LiteralPath $script:Audio -Recurse -File -ErrorAction SilentlyContinue).Count
    models = @(Get-ChildItem -LiteralPath $script:Models -Recurse -File -ErrorAction SilentlyContinue).Count
    text = @(Get-ChildItem -LiteralPath $script:Text -Recurse -File -ErrorAction SilentlyContinue).Count
    validationPassed = @($validation | Where-Object status -eq 'ok').Count
    validationFailed = $failedValidation.Count
    unresolved = @($script:Issues | Select-Object -Unique).Count
    unityProjectFiles = [int]$script:UnityProjectFiles
}

$uniqueIssues = @($script:Issues | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
$uniqueWarnings = @($script:Warnings | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
Write-ProgressStep -Percent 94 -Message '正在生成 JSON、Markdown 和问题报告'
ConvertTo-Json -InputObject $validation -Depth 5 | Set-Content -LiteralPath (Join-Path $script:ReportDir 'validation.json') -Encoding UTF8
if ($uniqueIssues.Count -gt 0) {
    $uniqueIssues | Set-Content -LiteralPath (Join-Path $unresolved 'issues.txt') -Encoding UTF8
}
else {
    Set-Content -LiteralPath (Join-Path $unresolved 'issues.txt') -Value '无未解决问题。' -Encoding UTF8
}
$uniqueWarnings | Set-Content -LiteralPath (Join-Path $script:ReportDir 'warnings.txt') -Encoding UTF8
$report = [ordered]@{
    input = $resolvedInput
    sourceUsed = $script:SourcePath
    output = $script:JobRoot
    engine = $detected
    unityVersion = $script:DetectedUnityVersion
    profile = $Profile
    assetKinds = $AssetKinds
    modelFormats = $ModelFormats
    unityProjectPath = $script:UnityProjectPath
    started = $started.ToString('o')
    finished = (Get-Date).ToString('o')
    counts = $counts
    toolRuns = $script:ToolRuns.ToArray()
    issues = $uniqueIssues
    warnings = $uniqueWarnings
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $script:ReportDir 'report.json') -Encoding UTF8

$markdown = @(
    '# 解包报告', '',
    ('- 输入：`{0}`' -f $resolvedInput), ('- 引擎：`{0}`' -f $detected), ('- Unity 版本：`{0}`' -f $script:DetectedUnityVersion), ('- 模式：`{0}`' -f $Profile), '',
    '## 导出统计', '',
    "- 图片：$($counts.images)", "- 音频：$($counts.audio)", "- 模型：$($counts.models)", "- 文本：$($counts.text)",
    "- Unity 工程文件：$($counts.unityProjectFiles)", "- 验证通过：$($counts.validationPassed)", "- 验证失败：$($counts.validationFailed)", "- 未解决记录：$($counts.unresolved)", "- 注意事项：$($uniqueWarnings.Count)", '',
    '## 说明', '',
    '导出数量为 0 不一定表示源包没有该资源，也可能是资源加密、依赖缺失、工具不支持或当前筛选不包含该类型。详细信息见 `07_unresolved/issues.txt` 和 `logs`。'
)
$markdown | Set-Content -LiteralPath (Join-Path $script:ReportDir 'report.md') -Encoding UTF8

Write-ProgressStep -Percent 100 -Message "全部完成：图片 $($counts.images)，音频 $($counts.audio)，模型 $($counts.models)，文本 $($counts.text)，验证失败 $($counts.validationFailed)"
Write-Host "报告：$(Join-Path $script:ReportDir 'report.md')" -ForegroundColor Green
Write-Output $script:JobRoot
