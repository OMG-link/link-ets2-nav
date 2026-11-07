# 开发指南

## 环境

Windows x64、Git、.NET 10 SDK、Bun。推荐使用 VS Code 和 C# Dev Kit。

```powershell
winget install --id Microsoft.DotNet.SDK.10 --exact --source winget
code --install-extension ms-dotnettools.csdevkit
```

安装后重新打开终端，使用 `git --version`、`dotnet --list-sdks` 和 `bun --version` 检查命令是否可用。

## 构建和运行

在仓库根目录执行：

```powershell
git submodule update --init --recursive --depth 1
.\scripts\dev.ps1 -Action Check
.\scripts\dev.ps1 -Action Restore
.\scripts\dev.ps1 -Action Build
.\scripts\dev.ps1 -Action Run
```

`Deploy` 构建并复制插件到开发宿主的 `Plugins/`；`Run` 在此基础上启动宿主。VS Code 的 `Nav:` 任务提供相同入口，F5 构建部署后启动调试。重新部署前卸载正在运行的插件，避免文件被锁定。

## 工程结构

- `Plugins/LinkEts2Nav/`：插件源码，ID 为 `link.ets2.nav`。
- `ETS2LA/`：固定提交的框架子模块，包含 TruckLib 子模块。
- `scripts/dev.ps1`：环境检查、恢复依赖、构建部署与运行。
- `Directory.Build.targets`、`dev-host/prepare-local-host.ps1`、`dev-host/ui/`：本地开发宿主覆盖层。
- `tests/LinkEts2Nav.UnitTests/`：xUnit 单元测试项目，为导航算法与预警规则预留。
- `tests/DevHost.IntegrationTests/`：xUnit 宿主集成测试，检查插件白名单、安装限制和页面渲染。

## 开发宿主

本地覆盖层屏蔽远程插件目录及安装，仅加载 `LinkEts2Nav.dll`，并替换首页、启动页和插件目录页。辅助驾驶的快捷键注册、状态操作和游戏控制输出已禁用；地图、遥测、路线和预警音频仍可使用。不删除用户已有插件或设置；它不是安全沙箱，也不代表禁用了宿主的全部联网功能。

覆盖源码生成在忽略提交的 `artifacts/host-overrides/`，不直接修改子模块实现。更新上游后如源码锚点检查失败，应核对接口再适配。只发布插件 DLL 时不携带这些宿主限制。

```powershell
dotnet test LinkEts2Nav.slnx
# 或使用开发脚本与 VS Code 的 Nav: Test 任务
.\scripts\dev.ps1 -Action Test
```

开发宿主输出目录为 `ETS2LA/ETS2LA/bin/Debug/net10.0`。子模块的浅克隆可能使开发版本号与官方发行号不同；需要完整历史时可在对应子模块运行 `git fetch --unshallow`。

## 文档约定

README 面向用户，保留功能、安装、使用和限制说明。开发指南只记录可复用的操作方法，不包含个人环境状态或执行历史。

## 测试约定

使用 xUnit.net v3 3.2.2，通过 VSTest 适配器运行，可使用 `dotnet test` 与 VS Code 测试面板；依赖由 NuGet 恢复，无需系统级安装。测试项目不随插件部署。

宿主集成测试禁止并行执行，诱饵文件仅创建于测试输出目录并在结束时清理。插件测试产物由 MSBuild 自动复制，不需要手工指定宿主路径。页面测试使用 Blazor HtmlRenderer。

单元测试覆盖预警状态机、路线与道路匹配、遥测时效、真实资源格式的合成样本以及官方归档选择。测试不依赖本机安装游戏，不播放真实音频。

## 上游诊断

根目录 `Directory.Build.targets` 默认屏蔽 ETS2LA 子模块项目中已确认的编译警告，以及四条已知上游依赖漏洞公告的诊断。插件和测试源码的编译诊断、新增漏洞公告仍然保留。屏蔽不会修复漏洞，也不改变实际依赖版本。

需要审查完整诊断时，执行以下命令；恢复依赖和构建都必须传入开关，避免使用此前缓存的审计结果：

```powershell
dotnet restore LinkEts2Nav.slnx -p:NavShowUpstreamWarnings=true
dotnet build LinkEts2Nav.slnx --no-restore --no-incremental -p:NavShowUpstreamWarnings=true
```

恢复默认显示策略后，运行 `dotnet restore LinkEts2Nav.slnx` 刷新依赖诊断。上游更新时应重新审查并移除不再需要的屏蔽项。
## 地图资源检查

`tools/LinkEts2Nav.Probe/` 是开发用只读工具，不随插件发布。它读取本机游戏归档，不启动游戏，不修改存档；JSON 输出可重定向到忽略的 `artifacts/`。普通扫描必须提供世界坐标。

```powershell
dotnet run --project tools/LinkEts2Nav.Probe -- --game-path "<game-directory>" --x -39000 --z -30000 --radius 1000
# 检查指定模型的真实测速挂点
dotnet run --project tools/LinkEts2Nav.Probe -- --game-path "<game-directory>" --inspect-model /model/speedcamera/speedcamera_hw_au.pmg
```

默认扫描已安装官方归档，输出的 `profileResolved` 为 false，不代表当前存档。传入 `--game-log "<game.log.txt>"` 后才按实际挂载日志解析当前地图；会拒绝无法确认兼容的地图或探头资源覆盖。模组以最近一次显式活动列表为准；模组管理界面的临时预览挂载不算启用，也不会重置地图缓存。用 `--help` 查看完整参数。

启用插件后，开发宿主首页的“测速探头诊断”链接进入 `/link-ets2-nav`。该页面随插件 DLL 提供，在标准宿主中也可通过对应路由访问。架构及预警规则见 [预警设计](architecture.md)。
## 运行日志

插件日志统一带 `[LinkEts2Nav]` 前缀。加载日志不代表插件已经启用：在插件管理器中启用后，还应看到“插件已启用”和“后台扫描已启动”。日志包含等待遥测、暂停、数据过期、地图挂载、扫描结果、校准配置、发现目标、判定变化及预警播放请求。

扫描与归档读取在独立后台任务中执行，不阻塞已准备地图上的预警判定；刷新期间诊断状态显示“使用已准备快照”。首次扫描仍需等待完成，未加载区域中的目标不会凭空推断。刷新失败保留已有快照并间隔重试，传送或地图来源变化则立即清除旧快照。

相同状态和扫描结果每 30 秒汇总一次；探头距离变化不会逐帧刷屏，方向或判定改变则立即记录。目标离开诊断列表不等于已经过测速位置。日志中的播放请求也不等于音频设备已实际发声，音频失败会另有警告。直线距离回退时，目标判定日志附带“已知路线长度下限”；这是缺失几何情况下的保守下限，不是完整沿路距离，预警不会早于该下限允许的阶段。

从仓库根目录观察日志：

```powershell
Get-Content .\ETS2LA\ETS2LA\bin\Debug\net10.0\ets2la.log -Tail 30 -Wait
```

插件运行日志附带 UTC 时间；宿主控制台另有本地时间。上游文件日志约每 5 秒刷新一次，因此文件输出可能比控制台稍晚。重新启动宿主会重新创建日志文件，需要保留时请先复制到本地忽略的 `artifacts/`。

## 模组兼容性检查

兼容性检查按实际资源文件判断：车流生成规则、AI 车道规则和信号灯时序允许共存；收费站配置仅在解析确认全部单位为收费站或非测速模型挂点、且没有外部包含时放行。真正覆盖道路、模型、标志或测速挂点的文件仍会暂停扫描，日志会列出完整模组路径和冲突资源。

## 预警音频与试听

测速诊断页的“试听预警音”按钮仅测试声音链路，不计作探头预警，也不需要启用扫描。可先保持游戏暂停，启用插件并等待日志出现“附近地图已准备”，再开始行驶，减少首次资源加载导致的预警延迟。

在宿主输出目录的 `Settings/LinkEts2Nav.Audio.json` 中选择本地音频：

```json
{
  "filePath": "Assets/LinkEts2Nav/community-radar-preview.wav"
}
```

相对路径以宿主输出目录为基准，也可使用绝对路径。配置在每次播放请求时读取，修改 JSON 无需重启；更新 DLL 仍需重启宿主。支持 WAV、OGG、MP3、FLAC；FMOD `.bank` 须先解码成普通音频。配置缺失时使用内置提示音，配置或文件错误会记录原因并尝试回退。

借用第三方声音进行本地测试时，音频和提取工具应放在忽略目录，不能随插件 DLL 或代码提交分发。试听和实际预警都会记录选用的音频文件；“已提交播放”只表示已排队，设备或异步解码错误需结合宿主音频日志判断。

## 离线语音合成

可选的 `tools/LinkEts2Nav.Speech/` 是常驻 .NET 合成子进程，使用 sherpa-onnx 2.0.1、CPU 推理和 Kokoro multi-lang v1.1-zh。当前预设音色为 `zf_067`（该模型 Speaker 36），默认使用 2 个线程。开发脚本自动构建并将 Windows x64 worker 及原生依赖部署到宿主的 `Tools/LinkEts2Nav.Speech/`；无需安装 Python、CUDA 或系统语音服务。

模型需另行下载并解压：[官方模型包](https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-multi-lang-v1_1.tar.bz2)。模型权重、音色、词典和生成音频不提交到代码仓库。模型与运行库各自保留原有许可证；[Kokoro 模型说明](https://huggingface.co/hexgrad/Kokoro-82M-v1.1-zh)和[sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx)提供来源及许可证。

在宿主输出目录创建 `Settings/LinkEts2Nav.Speech.json`：

```json
{
  "enabled": true,
  "modelDirectory": "Models/kokoro-multi-lang-v1_1",
  "workerPath": "Tools/LinkEts2Nav.Speech/LinkEts2Nav.Speech.dll",
  "cacheDirectory": "Cache/LinkEts2Nav/Speech",
  "voice": "zf_067",
  "threads": 2
}
```

相对路径均以宿主输出目录为基准，也可配置绝对路径。配置缺失或 `enabled=false` 时继续使用已有音频。TTS 配置在启用插件时读取；修改后停用再启用，更新 DLL 后需重启宿主。模型目录必须包含 `model.onnx`、`voices.bin`、`tokens.txt`、`lexicon-zh.txt` 和 `lexicon-us-en.txt`。

默认文案为“五百米处，有测速照相。”和“二百米处，有测速照相。”；只有本次接近首次确认目标时距离已小于 200 米，才只播放“二百米内，有测速照相。”。正常经过 200 米阈值时，即使采样距离略低于 200 米，仍使用“二百米处”。迟发现但仍超过 200 米时使用“前方有测速照相。”。实验限速解析能确认结果时附加“限速五十”等文案；未知时仍不附加。遥测中的当前位置限速只用于实测核对，不能直接代替前方探头限速。

确认目标在前方后，立即按各阶段预计触发顺序生成 500 米、200 米等完整短句。当前使用同一种文案的目标共用同一缓存文件。缓存目录中的哈希文件名由内容和模型指纹决定，重启后仍可复用；文件损坏会重新合成。启用时预载并校验预生成音频包，首次发现目标即可直接播放对应文案。初次本地合成可能需要数秒，触发时未准备好则立即回退无距离文案的内置提示音，不补播迟到语音；静态试听配置不充当 TTS 失败时的回退，避免把固定“500 米”文案用于近距离目标。日志会显示规划后的合成开始、文件就绪、实际播放请求和回退原因。

诊断页的“试听预警音”仍试听 `LinkEts2Nav.Audio.json` 指定的静态回退音频；实际 500/200 米阶段使用对应的 TTS 缓存。暂停或无有效路线时不生成新的目标计划，不因缓存生成完成而自动播放。

可单独运行 worker 检查模型，无需启动游戏或宿主：

```powershell
dotnet run --project tools/LinkEts2Nav.Speech -- --model-dir "<model-directory>" --cache-dir "<cache-directory>" --voice zf_067 --threads 2
```

worker 从标准输入按行读取 `{"id":"example","text":"五百米处，有测速照相。"}`，标准输出返回 `id/path/cacheHit/error` JSON。EOF 正常退出，合成诊断只写标准错误。调用时应使用结构化参数和 JSON 序列化，不能把动态文本拼进 shell 命令。

## 远端预生成与本地拼接

Qwen 音频包只在开发者主动运行生成命令时联网；插件运行时不会读取 API 密钥，也不会调用远端服务。使用 `qwen-audio-3.1-tts-flash`、`longanhuan_v3.1` 和 [Qwen WebSocket 协议](https://help.aliyun.com/en/model-studio/qwen-audio-tts-websocket-api)。生成会产生服务商计费。

在仓库根目录的 `.api-keys` 中写入 `qwen=你的密钥`。该文件被 Git 忽略，不复制到宿主或发布目录。随后执行：

```powershell
dotnet run --project tools/LinkEts2Nav.Speech -- --generate-pack --keys .api-keys --output artifacts/speech/qwen-longanhuan-v3.1
```

命令生成 108 个音频文件：500 米处、200 米处、200 米内和前方四种整句，各有无限速版本及限速 30、40、50、60、70、80、90、100 的版本，共 36 句，加上未校准探头接近提醒整句及 66 条基础导航指令；另有五个模板片段：“米处，有测速照相。”“米处，有测速照相，限速”“米内，有测速照相。”“米内，有测速照相，限速”“前方有测速照相，限速”。限速播报不带“公里”。每条成功后保存清单和 SHA-256；重复执行复用已验证的文件，不重复调用服务。生成异常时停止，修复后可重新执行。更换模型、音色或合成参数时应生成到新的目录，避免混用不同音频包。

`Deploy`/`Run` 会将上述本地生成目录复制到宿主 `Assets/LinkEts2Nav/SpeechPack/`。也可手动复制整包，并在 `LinkEts2Nav.Speech.json` 中设置 `packDirectory`；默认值即该相对路径。音频和清单存放在忽略的产物目录，不随源码提交。自行分发音频包前须确认生成服务的相关授权。

匹配完整文案时优先使用音频包。遇到特殊数字，例如“五百米处，有测速照相，限速六十五。”，则由本地 Kokoro 分别生成“五百”和“六十五”，再依次拼接远端模板片段，保存完整 WAV。两种来源统一为 24 kHz、单声道、16 位 PCM；拼接的是采样数据，保留原有停顿，音色接缝可能明显。没有可用模板、文案不匹配或音频包损坏时回退本地整句合成。缓存键包含模板及本地数字音频内容，内容变更后不会误用旧拼接音频。

实验解析器提供的探头限速会选择对应文案；无法解析时使用不含限速版本。带限速语音未准备好时优先使用同阶段、同距离含义的无数字语音，避免退回提示音。可通过 worker JSON 输入离线测试特殊限速文案，无需启动游戏：

```powershell
dotnet run --project tools/LinkEts2Nav.Speech -- --model-dir "<model-directory>" --cache-dir "artifacts/speech/cache" --pack-dir "artifacts/speech/qwen-longanhuan-v3.1"
```

输入 `{"id":"special","text":"五百米处，有测速照相，限速六十五。"}`，返回完整缓存 WAV 路径；重复输入复用本地数字和完整拼接缓存。工具不会自动播放音频。


运动判定采样保存在宿主输出目录的 `Cache/LinkEts2Nav/Diagnostics/motion.jsonl`，上一段为 `motion.previous.jsonl`。每段约 8 MiB，最多保留两段；重启宿主也会轮换。采样记录每次预警判定实际使用的位置、朝向、时间、路线状态、资源标识、区块、已校准探头和判定结果。路线节点内容未改变时省略 `Route.Nodes`，回放时沿用同文件上一条非空节点列表；每段首条记录始终包含节点。`Sequence` 跳号表示异步写入队列满时丢失了采样，不能当作连续轨迹。文件写入失败只关闭采样，不中断预警。它用于重放插件判定输入，不是游戏录像；重建道路几何仍需对应版本的游戏地图资源。这些文件含本机安装路径和行驶位置，位于忽略的产物目录，不随源码提交。

未校准且挂点部件有效的探头按挂点位置的直线距离在 500 米内提醒一次，不使用导航方向判定，也不附加限速或 200 米阶段。首次发现已在 500 米内时立即提醒。预生成包包含完整固定文案；扫描刷新或目标临时消失不重置去重状态，经过附近并离开至少 50 米后再次接近可重新提醒。暂停、过期车辆数据及倒车期间不触发。

限速解析按道路／预制件的实际连通关系传播国家信息：普通共享节点连接分量，国界节点将两侧分开，并分别使用对应地图条目的国家 ID；ID 对应名称取自当前挂载的国家定义。城市区域只能给实际位于区域内的道路提供国家依据，不作为整份附近地图的默认国家。多个国家出现在同一批区块时不会影响互不相连的道路。没有可达依据、国家 ID 未知或证据冲突时继续返回未知；该索引在后台扫描已有区块时构建，不额外读取整张地图。

基础导航沿用 SDK 当前路线，以连续道路路径的弧长计算操作距离，按路口入口／出口拓扑及外部道路切向区分转弯、分流和直行。无岔路的自然弯不提示；无法确认的近路口会阻断后续指示，避免越过未知路口提前播报远处操作。复杂环行连接不按净转角猜测，当前不提供环岛出口编号或车道建议。

导航按 500／200／50 米梯度提示。首次实际播报在两级之间时，向下取整到 50 米的倍数立即播报并跳过紧接的较近级别，例如 275 米播报 250 米并跳过 200 米，之后只提示 50 米；79 米只播报一次 50 米。首次不足 50 米时使用“保持直行”“即将右转”等即时指示。正常跨过阈值的采样使用标准距离，明显漏过阈值后恢复匹配则按临时距离处理，避免连续两句含义相近。提示规划不消耗阶段，临时匹配丢失不会重置已播报状态。语音合成使用共享队列的独立通道：测速优先准备，暂停导航计划不会取消测速计划；两者共用按文本缓存的文件。新音频包包含 66 条导航固定句（50 至 500 米每 50 米一档，加上即时动作，六种操作），日常运行无需联网。诊断页显示下一操作、沿路距离和最近指令；motion.jsonl 也记录每帧导航判定。启用／停用整个插件控制这些功能，未设置游戏路线时仍可使用测速预警。