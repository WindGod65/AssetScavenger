---
name: android-game-unpack-pipeline
description: Inspect, unpack, export, validate, or troubleshoot Android game resources from APKs, Unity AssetBundles, Unity game folders, Unreal PAKs, and unpacked Android directories using the local pipeline. Use when the user wants reusable PNG, WAV, model, or text assets, an engine/resource inventory, or improvements to this pipeline; do not use for modifying game binaries or bypassing access controls.
---

# Android Game Unpack Pipeline

Use the maintained local tool at `D:\手机解包游戏\AssetScavenger`.

## Choose the operation

- Prefer `资源清道夫 AssetScavenger.exe` when the user wants an interactive workflow, progress display, native file pickers, or a custom export directory.
- Use `Run-Unpack.ps1` directly for automation, diagnosis, repeatable tests, or selective exports.
- Start with `-Profile scan` for an unknown or very large game. Use `standard` for converted resources. Use `full` only when raw bundle extraction is actually needed and adequate disk space is confirmed.
- Use `-Profile unityProject` only for Unity input when the user explicitly wants an AssetRipper-reconstructed project. Treat `08_unity_project/ExportedProject` as a best-effort editable project, not recovered original source or proof that the game will run.
- Route a public web-game URL through `-InputUrl <url> -Engine web`; use `-BuildPlayableWeb` only when the user wants a local mirror and accepts extra downloads. Route local SWF, HTML, web-game ZIP files and H5 game directories through `-Engine web`. Inspect `00_report/web_game_analysis.json`, `09_web_game/*/game_meta.json`, and `failed_resources.json` for detection and download failures.
- URL mode may follow only public HTTP/HTTPS references found in the page. Do not import cookies or bypass login, payment, CAPTCHA, encryption, anti-bot checks, or other access controls. Treat Referer as ordinary public-request compatibility, not authorization bypass.
- A playable mirror must run through its local HTTP launcher, not `file://`. Explain that asynchronous loads, cross-origin APIs, server dependencies, Flash/Ruffle compatibility, and platform SDKs may still prevent offline play.
- Treat extracted JavaScript, ActionScript and sourcemaps as published client code, not proof that original TypeScript/C# or server-side gameplay logic was recovered. Never bypass authentication, payment, encryption, or platform access controls.
- Export large games by resource type rather than selecting everything at once. Keep the output directory outside the input game directory.

## Preserve useful data

- Never overwrite or delete the input APK, bundle, PAK, game directory, or earlier job output unless the user explicitly requests an exact cleanup target.
- Use a new output root or the timestamped job directory created by the pipeline.
- Keep `assetName_pathID` naming for Unity exports to reduce collisions.
- Treat `07_unresolved`, tool logs, and format validation as part of the result. Do not report success merely because a process exited.

## Engine routing

- Unity: use AssetStudioModCLI for inventory and ordinary PNG/WAV/text/OBJ Mesh export. Use the bundled MeshFormatConverter/Assimp path for selected FBX or glTF 2.0 output. Prefer direct Mesh export plus conversion over SplitObjects when object names contain Windows-invalid path characters. Use AssetRipper only when Unity project, prefab, scene, or type reconstruction is needed.
- Unreal: use UModel/UE Viewer. An encrypted PAK requires the correct legally obtained AES key; do not invent or claim to recover a key.
- Unknown input: safely expand archives and report evidence instead of forcing the wrong engine tool.

Standard compression is not the same as encryption. Modified IL2CPP metadata can block script/type recovery while textures and audio remain exportable. State those limitations precisely.

## Verify the outcome

Check the generated `00_report/report.json`, `00_report/validation.json`, and `07_unresolved/issues.txt`. For a targeted smoke test, confirm at least one expected converted asset has a valid signature and nonzero size. For large jobs, report counts and unresolved categories rather than opening every file.

When extending or diagnosing the implementation, read [references/tooling.md](references/tooling.md).
