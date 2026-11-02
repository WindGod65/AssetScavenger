# Local implementation reference

## Entrypoints

- GUI: `D:\手机解包游戏\AssetScavenger\资源清道夫 AssetScavenger.exe`
- Core: `D:\手机解包游戏\AssetScavenger\Run-Unpack.ps1`
- GUI source: `D:\手机解包游戏\AssetScavenger\gui-src`
- Rebuild: `D:\手机解包游戏\AssetScavenger\Build-GUI.ps1`
- Tool paths: `D:\手机解包游戏\AssetScavenger\config.json`

## Core parameters

`Run-Unpack.ps1` accepts `InputPath` or `InputUrl`, plus `BuildPlayableWeb`, `Engine`, `Profile`, `AssetKinds`, `ModelFormats`, `UnityVersion`, `UnrealAesKey`, `OutputRoot`, and `MaxExportTasks`. `Engine` accepts `web` for a public URL or local SWF/H5 package; `Profile` also accepts `unityProject`; `ModelFormats` accepts `obj`, `fbx`, and `gltf`.

Example selective export:

```powershell
& "D:\手机解包游戏\AssetScavenger\Run-Unpack.ps1" `
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
- `09_web_game`: extracted web package and FFDec results
- `logs`: full backend logs

## Tool responsibilities

- AssetStudioModCLI net472 is the supported Unity CLI on this machine. The net9 build is not usable because the matching runtime is absent.
- AssetRipper GUI is optional for project reconstruction and may lose type information when IL2CPP metadata is modified.
- `Umodel_materials.exe` is for Unreal, not Unity. AES handling requires a supplied key.
- URL web-game mode uses the built-in bounded public-resource mirror. `BuildPlayableWeb` adds `Start-LocalGame.bat`; `failed_resources.json` records HTTP and discovery failures. JPEXS FFDec remains required for ActionScript/resource export from SWF, and Ruffle is needed to play SWF in modern browsers.
- The 4399 Flash route reads `_strGamePath` from `/flash/<id>.htm`, prefixes `https://sda.4399.com/4399swf`, and skips unrelated portal assets. `tools/WebDownloader/fetch.py` is the Python/OpenSSL fallback when Windows SChannel/WebClient cannot establish HTTPS.
- AssetRipper, UModel, FFDec 18.0.0, Microsoft OpenJDK 17, and the Python/OpenSSL web fallback are bundled under `tools/AssetRipper`, `tools/UModel`, `tools/FFDec`, `tools/JavaRuntime`, and `tools/Python`. All configured tool paths are relative to the pipeline root, so no desktop, system-wide Java, or system Python installation is required.

## GUI architecture

The EXE is a .NET Framework single-file host. It listens only on `127.0.0.1:18765`, serves an embedded offline page, uses a per-process request token for mutating API calls, and starts the PowerShell core with encoded arguments. It uses a TCP loopback server rather than `HttpListener`, so it does not require URL ACL or administrator setup.

After GUI changes, rebuild with `Build-GUI.ps1`, ensure the C# compiler exits successfully, and test the page plus one small AssetBundle before a full export.
