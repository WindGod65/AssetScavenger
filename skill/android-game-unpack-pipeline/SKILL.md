---
name: android-game-unpack-pipeline
description: Acquire, unpack, export, validate, or troubleshoot Android and public web-game resources, including APK, Unity bundles, Unreal PAK, and SWF through the local AssetScavenger pipeline. Use for usable image/audio/model exports and source/timeline evidence handoff for replication; not for bypassing access controls or claiming complete game restoration from extraction alone.
---

# Android Game Unpack Pipeline

Use the maintained local tool at `D:\手机解包游戏\AssetScavenger`.

## Required completion contract

Complete every applicable stage of the requested extraction: identify input and dependencies → inspect → acquire/unpack → selectively export/convert → validate usable outputs → report failures → hand off evidence. Do not silently skip stages, treat an exit code or raw file count as success, or make the user discover missing resource categories one at a time. Mark a stage not applicable only with an input-format or task-scope reason.

For SWF intended for a full FE replica, read [references/replica-handoff.md](references/replica-handoff.md) before exporting and before declaring the extraction ready. Source scripts, timelines, matrices, masks and filters are part of the evidence handoff even when they must stay outside the FE Assets directory. Extraction completion and complete replica acceptance are separate gates.

For a pipeline repair, preserve existing user options and verify the affected input/export route plus the failure path. Do not run unrelated huge exports or add tests that merely repeat an assignment.

## Choose the operation

- Prefer `资源清道夫 AssetScavenger.exe` when the user wants an interactive workflow, progress display, native file pickers, or a custom export directory.
- Use `Run-Unpack.ps1` directly for automation, diagnosis, repeatable tests, or selective exports.
- Start with `-Profile scan` for an unknown or very large game. Use `standard` for converted resources. Use `full` only when raw bundle extraction is actually needed and adequate disk space is confirmed.
- Use `-Profile unityProject` only for Unity input when the user explicitly wants an AssetRipper-reconstructed project. Treat `08_unity_project/ExportedProject` as a best-effort editable project, not recovered original source or proof that the game will run.
- Route a public web-game URL through `-InputUrl <url> -Engine web`; use `-BuildPlayableWeb` only when the user wants a local mirror and accepts extra downloads. Route local SWF, HTML, web-game ZIP files and H5 game directories through `-Engine web`. Inspect `00_report/web_game_analysis.json`, `09_web_game/*/game_meta.json`, and `failed_resources.json` for detection and download failures.
- URL mode may follow only public HTTP/HTTPS references found in the page. Do not import cookies or bypass login, payment, CAPTCHA, encryption, anti-bot checks, or other access controls. Treat Referer as ordinary public-request compatibility, not authorization bypass.
- For `www.4399.com/flash/<id>.htm`, extract only `_strGamePath` and resolve it against `https://sda.4399.com/4399swf`; do not recursively mirror the 4399 portal, recommendations, or advertisements. If Windows WebClient TLS fails, use the bundled Python 3/OpenSSL fallback at `tools/WebDownloader/fetch.py`.
- When automatic SWF unpacking is requested, pass `-AutoUnpackSwf`, keep the original SWF, and use the bundled FFDec selective export for `script,image,shape,morphshape,button,sound,font,text,symbolClass,binaryData`, plus FLA CS6 and XFL CS6. Put each SWF in its own `<name>_解包` folder and write `解包清单.json`. Preserve Sprite timelines through FLA/XFL instead of rendering every frame by default.
- When PNG copies are requested, pass `-ConvertFlashSvgToPng` and use FFDec `shape:png,button:png` into `png_visuals` while retaining the SVG originals. Keep MorphShape as SVG/FLA/XFL because FFDec 18 has no direct MorphShape PNG export.
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

A single valid file is a route smoke test, not acceptance of a full export. For a full extraction, account for every discovered category requested by the user and its conversion outcome. Inspect representative images, animation parts, transparency/filters, and audio from each distinct conversion path; report skipped, failed, unsupported, encrypted or dependency-missing categories separately. Do not report all content usable when validation has not established that.

For SWF handoff, verify the main game rather than a loader/ad; retain AS and XFL/FLA evidence; map repeated parts and dependencies; check Shape/MorphShape and audio as well as bitmap images. An image count such as 188 does not establish the full game inventory. Do not import source/reference/intermediate files or duplicate whole-frame atlases into FE by default.

When the user requests both extraction and a complete replica, continue from the validated handoff into the agreed replica workflow. Do not stop at resource export or switch the deliverable to a playable SWF wrapper. The local replica instructions are at `D:\Skill合集\swf-to-fe-replica\SKILL.md`; discover and read the actual location when working on another machine.

When extending or diagnosing the implementation, read [references/tooling.md](references/tooling.md).
