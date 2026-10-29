# Local implementation reference

## Entrypoints

- GUI: `D:\手机解包游戏\安卓游戏解包流水线\资源清道夫 AssetScavenger.exe`
- Core: `D:\手机解包游戏\安卓游戏解包流水线\Run-Unpack.ps1`
- GUI source: `D:\手机解包游戏\安卓游戏解包流水线\gui-src`
- Rebuild: `D:\手机解包游戏\安卓游戏解包流水线\Build-GUI.ps1`
- Tool paths: `D:\手机解包游戏\安卓游戏解包流水线\config.json`

## Core parameters

`Run-Unpack.ps1` accepts `InputPath`, `Engine`, `Profile`, `AssetKinds`, `ModelFormats`, `UnityVersion`, `UnrealAesKey`, `OutputRoot`, and `MaxExportTasks`. `Profile` also accepts `unityProject`; `ModelFormats` accepts `obj`, `fbx`, and `gltf`.

Example selective export:

```powershell
& "D:\手机解包游戏\安卓游戏解包流水线\Run-Unpack.ps1" `
  -InputPath "D:\game\game.apk" `
  -OutputRoot "E:\game-exports" `
  -Profile standard `
  -AssetKinds audio,images,models `
  -ModelFormats obj,fbx,gltf
```

Do not place `OutputRoot` inside a directory used as `InputPath`; recursive scans can read generated outputs back as inputs.

## Output layout

- `00_report`: JSON/Markdown report and asset inventory
- `01_raw_apk`: safely expanded APK or source-path record
- `02_assets_raw`: direct/raw extraction
- `03_images`: converted images
- `04_audio`: converted audio
- `05_models`: models and related data
- `06_text`: text, configuration, shaders, and fonts
- `07_unresolved`: encryption, corruption, missing dependency, and unsupported-format evidence
- `08_unity_project/ExportedProject`: AssetRipper best-effort Unity project
- `logs`: full backend logs

## Tool responsibilities

- AssetStudioModCLI net472 is the supported Unity CLI on this machine. The net9 build is not usable because the matching runtime is absent.
- AssetRipper GUI is optional for project reconstruction and may lose type information when IL2CPP metadata is modified.
- `Umodel_materials.exe` is for Unreal, not Unity. AES handling requires a supplied key.

## GUI architecture

The EXE is a .NET Framework single-file host. It listens only on `127.0.0.1:18765`, serves an embedded offline page, uses a per-process request token for mutating API calls, and starts the PowerShell core with encoded arguments. It uses a TCP loopback server rather than `HttpListener`, so it does not require URL ACL or administrator setup.

After GUI changes, rebuild with `Build-GUI.ps1`, ensure the C# compiler exits successfully, and test the page plus one small AssetBundle before a full export.
