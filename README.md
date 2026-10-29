# 资源清道夫 / AssetScavenger

*Your game, as open source.*  
你的游戏，在我面前如同开源。

这套脚本接收 APK、已解包的游戏目录、Unity AssetBundle、Unreal `.pak`，以及网页小游戏 URL、本地 SWF / HTML5 包，自动识别引擎并尽可能导出成可直接使用的资源。

## 图形界面（推荐）

双击根目录中的：

`资源清道夫 AssetScavenger.exe`

程序会在系统托盘常驻，并自动打开仅监听本机的网页：

`http://127.0.0.1:18765/`

首次进入是类型主页，可分别进入：

- Android APK；
- Unity 游戏与 AssetBundle；
- Unreal 游戏与 PAK；
- 网页小游戏 / 4399（只粘贴公开 URL，或选择 SWF、ZIP、HTML、目录）。

页面可设置输入文件、独立导出位置、引擎、扫描/标准/完整/重建 Unity 工程模式、资源类型、Unity 版本与并行数，并实时显示目标磁盘可用空间、阶段、百分比、完整日志、导出统计和未解决问题。日志会以 `[进度 042%] [时间] [脚本位置] 当前动作.......` 的形式持续打印，并保留底层工具的原始输出。除只扫描外，开始前必须确认容量警告。关闭浏览器不会中止任务；双击托盘图标可重新打开。需要中止时使用页面的“停止任务”，已导出的文件会保留。

“重建 Unity 工程”模式使用 AssetRipper 在 `08_unity_project\ExportedProject` 生成可由 Unity Hub/Editor 打开的项目结构。它会尽量恢复 Assets、ProjectSettings、Packages、Prefab、材质和脚本桩，但不保证原游戏可直接运行。

大型游戏可能产生数万到数十万个文件，导出容量可能超过安装包数倍。不要把导出位置选在输入游戏目录内部；建议使用空间充足的独立目录，并先按音频、图片、模型、文本分批导出。

如果修改了 `gui-src` 中的网页或 C# 代码，可以运行 `Build-GUI.ps1` 重新生成 EXE。本机无需安装额外 SDK。

## 快速使用

在 PowerShell 中运行：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
& "D:\手机解包游戏\AssetScavenger\Run-Unpack.ps1" `
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
- Flash `.swf`
- HTML5 网页游戏 `.zip`、`.html` 或目录
- Cocos Creator、LayaAir、Egret、Construct、Phaser、PixiJS、Unity WebGL 发布包

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
- `09_web_game`：网页小游戏原始展开结果、FFDec 导出和完整源包副本
- `logs`：各工具完整日志

脚本每次创建一个带时间戳的新任务目录，不覆盖过去的导出结果。Unity 导出文件名追加 PathID，降低同名资源互相覆盖的风险。

## 三个工具的职责

- **AssetStudioModCLI**：Unity 批量扫描和导出，图片转 PNG、音频转 WAV、文本提取，并先把 Mesh 稳定导出为 OBJ。OBJ 路径不依赖完整场景树，可兼容节点名含 Windows 非法路径字符的游戏。
- **MeshFormatConverter / Assimp**：将有效 OBJ 批量转换为用户选择的 FBX 和 glTF 2.0；输出仍保留 AssetName、PathID 和相对目录。
- **AssetRipper**：“重建 Unity 工程”模式的自动后端。适合需要 Prefab、场景和 Unity 工程结构时使用；普通图片、音频和模型批量导出仍优先使用标准模式。
- **UModel / UE Viewer**：只处理 Unreal Engine，不处理 Unity。可导出贴图、音频、骨骼模型和动画。
- **JPEXS FFDec**：处理 Flash SWF，导出 ActionScript、图片、声音、形状与字体。默认路径为 `tools\FFDec\ffdec.bat`。因当前 GitHub 二进制下载通道超时，便携包没有随本次构建写入；从[官方 Release](https://github.com/jindrapetrik/jpexs-decompiler/releases)解压到该目录后即可自动启用，流水线会在缺失时明确报出未解决问题。

## 网页小游戏与玩法逻辑

网页小游戏页允许纯小白直接粘贴一个公开游戏页面 URL；不知道 SWF 在哪里也没关系。内置下载器会从入口页面寻找公开引用的 iframe、SWF、脚本、样式、图片、音频和 Unity WebGL 数据，并在 `09_web_game\downloaded` 中建立镜像。也可以继续选择已经合法取得的本地文件。

打开“生成本地可玩版本”后，输出位于 `09_web_game\playable`，双击其中的 `Start-LocalGame.bat` 会通过本机 HTTP 服务启动。复杂异步加载、跨域接口、登录、旧插件、服务端依赖或 Ruffle 不兼容仍可能导致离线版不能运行；失败 URL 会写入 `failed_resources.json`，页面的“本次失败与限制”会显示原因和建议。

命令行 URL 示例：

```powershell
& ".\Run-Unpack.ps1" `
  -InputUrl "https://www.4399.com/flash/..." `
  -Engine web `
  -BuildPlayableWeb
```

工具只读取未登录状态下公开可访问的客户端内容，不使用 Cookie，不绕过支付、鉴权、验证码、加密或平台保护。

- Flash：FFDec 可恢复 ActionScript 或 P-code，并导出内嵌图片、声音等资源。
- Cocos / Laya / Egret / Construct / Phaser：会保留发布包里的 JavaScript、JSON、图集、音频及 sourcemap，并标记疑似玩法脚本。
- Unity WebGL：可以识别 `.data` / `.wasm` 发布结构并提取资源数据，但 WASM 不是原始 C# 源码。
- 如果发布时已经压缩、混淆或删除 sourcemap，只能得到发布后的客户端代码；服务器端玩法、账号、支付和数据库逻辑不在网页包中。

参考资料：[4399 开放平台文档](https://open.4399.cn/docs/)、[4399 H5 小游戏 SDK 示例](https://github.com/4399littlegame/littleGameSdk)、[JPEXS FFDec](https://github.com/jindrapetrik/jpexs-decompiler)、[Cocos Creator 构建选项](https://docs.cocos.com/creator/3.8/manual/en/editor/publish/build-options.html)、[LayaAir](https://github.com/layabox/LayaAir)、[Egret](https://github.com/egret-labs/egret-core)、[UnityDataTools Web 构建格式](https://github.com/Unity-Technologies/UnityDataTools/blob/main/Documentation/playerbuild-format.md)。

设计参考还包括 [Flash Collector](https://github.com/Cnotech/flash-collector)、[FlashGameDownloader](https://github.com/icefairy999/FlashGameDownloader)、[HtmlGameDownloader](https://github.com/PlatoMml/HtmlGameDownloader)、[Ruffle](https://github.com/ruffle-rs/ruffle) 和 [Flashpoint fpcurator](https://github.com/FlashpointProject/fpcurator)。前三者没有作为源代码直接复制进本项目：Flash Collector 更适合作为独立 GUI，另两个仓库的许可证信息需要单独核实。当前内置实现保持透明、可审计，后续可把这些工具作为用户自行安装的可选后端。

如果要“复刻”一个网页游戏，通常还需要：合法可用的美术/音频、客户端脚本或反编译伪代码、引擎和版本、关卡/数值配置、输入与 UI、存档，以及对缺失服务器接口的重新实现。导出的发布脚本只是参考材料，不保证是原始工程源码。

## 关于“全部解密”

标准 Unity Bundle、常见压缩和未加密 Unreal 包可以自动处理。游戏厂商自定义加密、受保护的 AES PAK、修改过的 IL2CPP metadata、缺失的 CAB 依赖或专有编码，不能靠一套通用脚本保证破解。流水线会保留原文件并把这些情况写进 `07_unresolved`，不会把失败误报为成功。

## 元气骑士已验证结果

- Unity 2022.3.62f2，IL2CPP。
- 2,534 个 Unity 文件可加载，识别 284,142 个可导出资产。
- 包含 16,768 个 Texture2D、118,059 个 Sprite、2,982 个 AudioClip、494 个 Mesh 和 1,410 个 TextAsset。
- 测试音频已成功转成标准 WAV：单声道、44.1 kHz、16-bit。
- `global-metadata.dat` 的 magic 异常会影响脚本类型和完整工程恢复，但不等于贴图、声音等 AssetBundle 都被加密。
