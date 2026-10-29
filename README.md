# 资源清道夫 / AssetScavenger

*Your game, as open source.*  
你的游戏，在我面前如同开源。

这套脚本接收 APK、已解包的游戏目录、Unity AssetBundle，或 Unreal `.pak` / 游戏目录，自动识别引擎并尽可能导出成可直接使用的资源。

## 图形界面（推荐）

双击根目录中的：

`资源清道夫 AssetScavenger.exe`

程序会在系统托盘常驻，并自动打开仅监听本机的网页：

`http://127.0.0.1:18765/`

首次进入会提示：

- 选择 APK / `.ab` / `.bundle` / `.unity3d` / `.assets` / `.pak` 文件；
- 或选择已经解包的 Android、Unity、Unreal 游戏目录。

页面可设置输入文件、独立导出位置、引擎、扫描/标准/完整/重建 Unity 工程模式、资源类型、Unity 版本与并行数，并实时显示目标磁盘可用空间、阶段、百分比、完整日志、导出统计和未解决问题。日志会以 `[进度 042%] [时间] [脚本位置] 当前动作.......` 的形式持续打印，并保留底层工具的原始输出。除只扫描外，开始前必须确认容量警告。关闭浏览器不会中止任务；双击托盘图标可重新打开。需要中止时使用页面的“停止任务”，已导出的文件会保留。

“重建 Unity 工程”模式使用 AssetRipper 在 `08_unity_project\ExportedProject` 生成可由 Unity Hub/Editor 打开的项目结构。它会尽量恢复 Assets、ProjectSettings、Packages、Prefab、材质和脚本桩，但不保证原游戏可直接运行。

大型游戏可能产生数万到数十万个文件，导出容量可能超过安装包数倍。不要把导出位置选在输入游戏目录内部；建议使用空间充足的独立目录，并先按音频、图片、模型、文本分批导出。

如果修改了 `gui-src` 中的网页或 C# 代码，可以运行 `Build-GUI.ps1` 重新生成 EXE。本机无需安装额外 SDK。

## 快速使用

在 PowerShell 中运行：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
& "D:\手机解包游戏\安卓游戏解包流水线\Run-Unpack.ps1" `
  -InputPath "D:\手机解包游戏\某个游戏\game.apk"
```

只扫描，不导出：

```powershell
& ".\Run-Unpack.ps1" -InputPath "D:\游戏目录" -Profile scan
```

只导出音频和图片：

```powershell
& ".\Run-Unpack.ps1" -InputPath "D:\游戏目录" -AssetKinds audio,images
```

Unity 版本无法自动识别时可指定：

```powershell
& ".\Run-Unpack.ps1" -InputPath "D:\游戏目录" -UnityVersion "2022.3.62f2"
```

专门重建 Unity 工程：

```powershell
& ".\Run-Unpack.ps1" `
  -InputPath "D:\游戏目录或AssetBundle" `
  -Profile unityProject
```

完成后用 Unity Hub 打开任务目录中的 `08_unity_project\ExportedProject`。优先使用报告识别出的 Unity 版本；工程可编辑不等于原游戏可直接运行。

元气骑士目录较大，建议先按资源类型分批导出，先取音频：

```powershell
& ".\Run-Unpack.ps1" `
  -InputPath "D:\手机解包游戏\元气骑士\元气骑士_REA解包" `
  -UnityVersion "2022.3.62f2" `
  -AssetKinds audio
```

确认剩余磁盘空间后，再用 `-AssetKinds images,text,models` 导出其余类型。`-Profile full` 还会额外保存 Bundle 的底层提取结果，最占空间；普通使用保留默认 `standard` 即可。

Unreal 加密 PAK 必须由你提供合法获得的 AES key：

```powershell
& ".\Run-Unpack.ps1" -InputPath "D:\游戏目录" -UnrealAesKey "0x..."
```

## 输入类型

- Android `.apk`
- 已解包的 APK / Android 游戏目录
- Unity 游戏目录
- Unity `.ab`、`.bundle`、`.unity3d`、`.assets`
- Unreal 游戏目录或 `.pak`

## 本地技能

可复用 Codex Skill 位于：

`skill\android-game-unpack-pipeline\SKILL.md`

它记录了引擎路由、磁盘空间策略、输出验证、工具职责和本机实现入口。

## 每个任务的输出

- `00_report`：总报告、资产清单和格式验证结果
- `01_raw_apk`：APK 安全展开结果；目录输入则记录原始路径，不重复复制整个目录
- `02_assets_raw`：底层工具直接提取的原始资源
- `03_images`：PNG 等图片
- `04_audio`：WAV 或原始音频
- `05_models`：FBX / glTF / OBJ 等模型
- `06_text`：TextAsset、JSON、XML、Shader、字体等
- `07_unresolved`：不支持、疑似加密、损坏和缺依赖问题
- `08_unity_project\ExportedProject`：AssetRipper 尽力重建的 Unity 工程
- `logs`：各工具完整日志

脚本每次创建一个带时间戳的新任务目录，不覆盖过去的导出结果。Unity 导出文件名追加 PathID，降低同名资源互相覆盖的风险。

## 三个工具的职责

- **AssetStudioModCLI**：Unity 批量扫描和导出，图片转 PNG、音频转 WAV、文本提取，并先把 Mesh 稳定导出为 OBJ。OBJ 路径不依赖完整场景树，可兼容节点名含 Windows 非法路径字符的游戏。
- **MeshFormatConverter / Assimp**：将有效 OBJ 批量转换为用户选择的 FBX 和 glTF 2.0；输出仍保留 AssetName、PathID 和相对目录。
- **AssetRipper**：“重建 Unity 工程”模式的自动后端。适合需要 Prefab、场景和 Unity 工程结构时使用；普通图片、音频和模型批量导出仍优先使用标准模式。
- **UModel / UE Viewer**：只处理 Unreal Engine，不处理 Unity。可导出贴图、音频、骨骼模型和动画。

## 关于“全部解密”

标准 Unity Bundle、常见压缩和未加密 Unreal 包可以自动处理。游戏厂商自定义加密、受保护的 AES PAK、修改过的 IL2CPP metadata、缺失的 CAB 依赖或专有编码，不能靠一套通用脚本保证破解。流水线会保留原文件并把这些情况写进 `07_unresolved`，不会把失败误报为成功。

## 元气骑士已验证结果

- Unity 2022.3.62f2，IL2CPP。
- 2,534 个 Unity 文件可加载，识别 284,142 个可导出资产。
- 包含 16,768 个 Texture2D、118,059 个 Sprite、2,982 个 AudioClip、494 个 Mesh 和 1,410 个 TextAsset。
- 测试音频已成功转成标准 WAV：单声道、44.1 kHz、16-bit。
- `global-metadata.dat` 的 magic 异常会影响脚本类型和完整工程恢复，但不等于贴图、声音等 AssetBundle 都被加密。
