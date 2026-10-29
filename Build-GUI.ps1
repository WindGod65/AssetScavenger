[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $root 'gui-src\PipelineWebHost.cs'
$html = Join-Path $root 'gui-src\index.html'
$coreScript = Join-Path $root 'Run-Unpack.ps1'
$converterSource = Join-Path $root 'converter-src\MeshFormatConverter.cs'
$converterDir = Join-Path $root 'tools\MeshFormatConverter'
$converterOutput = Join-Path $converterDir 'MeshFormatConverter.exe'
$assimpSource = Join-Path $converterDir 'assimp-vc143-mt.dll'
$output = Join-Path $root '资源清道夫 AssetScavenger.exe'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'

foreach ($required in @($source, $html, $coreScript, $converterSource, $assimpSource, $compiler)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "缺少构建文件：$required" }
}

# Windows PowerShell 5.1 requires an UTF-8 BOM to parse non-ASCII scripts reliably.
$coreBytes = [IO.File]::ReadAllBytes($coreScript)
$hasUtf8Bom = $coreBytes.Length -ge 3 -and $coreBytes[0] -eq 0xEF -and $coreBytes[1] -eq 0xBB -and $coreBytes[2] -eq 0xBF
if (-not $hasUtf8Bom) {
    $withBom = New-Object byte[] ($coreBytes.Length + 3)
    $withBom[0] = 0xEF; $withBom[1] = 0xBB; $withBom[2] = 0xBF
    [Array]::Copy($coreBytes, 0, $withBom, 3, $coreBytes.Length)
    [IO.File]::WriteAllBytes($coreScript, $withBom)
}

$compilerArguments = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+',
    ('/out:' + $output),
    ('/resource:' + $html + ',PipelineWeb.IndexHtml'),
    ('/reference:' + (Join-Path $framework 'System.dll')),
    ('/reference:' + (Join-Path $framework 'System.Core.dll')),
    ('/reference:' + (Join-Path $framework 'System.Drawing.dll')),
    ('/reference:' + (Join-Path $framework 'System.Windows.Forms.dll')),
    ('/reference:' + (Join-Path $framework 'System.Web.Extensions.dll')),
    $source
)
& $compiler $compilerArguments

if ($LASTEXITCODE -ne 0) { throw "C# 编译失败，退出码：$LASTEXITCODE" }

New-Item -ItemType Directory -Path $converterDir -Force | Out-Null
$converterArguments = @(
    '/nologo', '/target:exe', '/platform:x64', '/optimize+',
    ('/out:' + $converterOutput),
    ('/reference:' + (Join-Path $framework 'System.dll')),
    ('/reference:' + (Join-Path $framework 'System.Core.dll')),
    $converterSource
)
& $compiler $converterArguments
if ($LASTEXITCODE -ne 0) { throw "模型格式转换器编译失败，退出码：$LASTEXITCODE" }

Write-Host "构建完成：$output" -ForegroundColor Green
Get-Item -LiteralPath $output | Select-Object FullName,Length,LastWriteTime
Get-Item -LiteralPath $converterOutput | Select-Object FullName,Length,LastWriteTime
