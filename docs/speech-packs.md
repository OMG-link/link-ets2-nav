# 自制预生成语音包

预生成语音包不绑定语音服务商。可以使用本地模型、云端 API 或录音，只要为清单中的文本提供对应音频，再导入即可。插件运行时读取本地文件，不调用云端 API。

仓库保存文本和工具，不保存生成的 WAV、模型或 API 密钥。默认中文清单位于 `tools/LinkEts2Nav.Speech/Texts/zh-CN.txt`，它同时是工具嵌入的默认清单；修改后需要重新构建工具。

## 导出待生成任务

在仓库根目录执行：

```powershell
dotnet run --project tools/LinkEts2Nav.Speech -- --export-jobs --output artifacts/speech/jobs.json
```

生成 JSON 的格式为：

```json
{
  "Schema": 1,
  "Kind": "fixed",
  "Items": [
    { "Text": "五百米后，左转。", "File": "<文本的SHA256大写十六进制>.wav" }
  ]
}
```

将 `Items` 交给所选语音库或脚本：逐条合成 `Text`，把 WAV 写到输入目录下的 `File`。也可以修改 `File`，对应已经生成的文件，例如 `recordings/turn-left.wav`。它必须是相对输入目录的路径，不能越出输入目录。

自定义文本可存为 UTF-8 文件，每行一个完整句子，导出时加 `--texts your-texts.txt`。空行忽略，重复文本（含 Unicode 规范化后重复）会报错。固定包最多 256 项；动态文本无需全部预生成，未匹配的完整句子仍由本地模型生成。

## 生成音频与导入

音频必须是 **WAV、24,000 Hz、单声道、16 位有符号整数 PCM**。MP3、浮点 WAV、其他采样率或立体声不会隐式转换。已有音频可用自行安装的 FFmpeg 转换，例如：

```powershell
ffmpeg -i source.mp3 -ar 24000 -ac 1 -c:a pcm_s16le artifacts/speech/input/converted.wav
```

让对应任务的 `File` 指向 `converted.wav`，然后导入：

```powershell
dotnet run --project tools/LinkEts2Nav.Speech -- --import-pack --jobs artifacts/speech/jobs.json --input artifacts/speech/input --output artifacts/speech/my-pack --model my-provider-model --voice my-voice
```

`model` 和 `voice` 是自定义来源标识，不需要使用 Qwen 名称。工具先检查清单和全部输入文件，再通过同一打包器写出内容命名的 WAV 与含 SHA256 校验和的 `manifest.json`。缺失文件、非规范音频、重复文本或非法路径会报错；输入目录内没有被清单引用的文件忽略。重复导入时复用校验通过且来源标识一致的产物。输出包只包含本次清单，改变清单后不自动保留已移除的条目。

输入和输出目录必须相互独立，不能相同或嵌套，防止导入过程覆盖尚未读取的输入。已有原始输入仍须保留到导入完成；即使输出缓存已存在，导入也检查清单中的每个输入。清单元数据也须满足运行时上限：固定包 256 KiB、城市包 2 MiB。更换模型或音色时修改对应标识，避免把旧缓存当作新音色。

## 城市名片段

城市名使用独立清单和 `CityNames` 子包。这个历史目录名也用于码头等地点的独立名称片段，不限制名称必须属于城市。可从自己的游戏安装导出城市定义原名，不需要全图解析：

```powershell
dotnet run --project tools/LinkEts2Nav.Probe -- --game-path "<ETS2安装目录>" --export-city-names artifacts/speech/city-names.txt
```

仅导出冰岛时加 `--country iceland`。默认读取本机安装的官方归档；如需按当前配置读取，使用 Probe 已有的 `--game-log` 选项。结果来自 `city_data.city_name`，不代表所有城市在当前存档中均已启用或可达，也不包含所有入城牌的简称。牌面使用别名时可手动补入相应名称。

```powershell
dotnet run --project tools/LinkEts2Nav.Speech -- --export-jobs --kind city-names --texts artifacts/speech/city-names.txt --output artifacts/speech/city-jobs.json
# 用自己的语音库生成 city-jobs.json 中的音频到 city-input 后：
dotnet run --project tools/LinkEts2Nav.Speech -- --import-pack --jobs artifacts/speech/city-jobs.json --input artifacts/speech/city-input --output artifacts/speech/my-pack/CityNames --model my-provider-model --voice my-voice
```

城市包最多 4096 项，每个名称最多 128 个字符，保留原始 Unicode 拼写。插件启动时校验清单，只将已准备的名称插入入城提示；名称片段与本地中文前后段拼接并缓存。未准备的名称仍播普通“进入城区”，不将外语名称交给可能跳词的本地模型。

### 选择名称并检查缺失音频

优先准备运行时会使用的名称：城市定义原名、入城牌的唯一可读名称、货场显示名和轮渡港口显示名。牌面简称与全名、大小写不同的拼写分别保留，因为运行时按原文查找片段。不要直接合成所有指路牌文字：其中还包括道路编号、海拔、距离和设施说明。里程牌目标名可留作后续导航功能的名称来源，不必提前全部生成。

全图名称导出中的 `names.txt` 同样需要检查：一块入城牌有多个文本对象时，导出器会保留全部文字，而运行时只有唯一可读名称才会使用城市名。未解析的本地化标记、排版标记或 `\\xNN` 转义文本应先修复来源，不能当成待朗读名称。

将检查后的名称分别存为 UTF-8 文本清单，可合并清单并检查已安装的名称包：

```powershell
dotnet run --project tools/LinkEts2Nav.Speech -- --prepare-name-jobs --texts reviewed-cities.txt --texts reviewed-companies.txt --texts reviewed-ports.txt --pack artifacts/speech/my-pack/CityNames --output artifacts/speech/missing-name-jobs.json
```

重复的名称按 Unicode NFC 合并，输出旁的 `.coverage.json` 保留输入文件名及每个名称的 `ready`、`missing` 或 `invalid` 状态。只有文件名、校验和及 WAV 格式都正确的已有片段才算就绪；命令不调用语音服务，也不修改现有音频包。未指定 `--pack` 时导出全部名称任务。已有名称与新名称合计不能超过 4096 项；超出时应缩小安装包范围，不能将多批任务直接合并成超限包。

任务清单只包含缺失或损坏的片段。没有缺项时 `Items` 为空，无需生成。用任意服务生成后，导入此任务会创建只含这些片段的新包；更新原包前，应将其与原包中需要保留的有效片段一起整理为完整导入清单。Qwen 的追加命令则继续使用完整名称文本清单，已校验通过且来源相同的片段会复用。

近距离码头提示将出发港、目的港片段按航行顺序与中文句段拼接。两个名称都准备好时，可播“进入xxx码头，乘坐前往yyy的轮渡”；片段缺失或损坏时，回退“进入码头，按导航方向乘坐轮渡”，不会把未准备的外语名交给本地模型。远距离预告只报“进入码头”。码头名称可以作为自定义 UTF-8 名称清单导出、生成和导入；城市定义导出命令不包含所有码头。

## 可选 Qwen 适配器

### 从完整句中提取模板片段

模板的固定部分应放在完整句中生成，再按字级时间戳提取，以保留上下文语调。默认方案位于 `tools/LinkEts2Nav.Speech/Texts/contextual-fragments.json`：每项给出片段文本、完整原句、填入的真实词汇及原文范围。例如“米处，有测速照相，限速”从“五百米处，有测速照相，限速八十。”中提取。

先导出直接生成的完整文案，以及模板使用的完整原句：

```powershell
dotnet run --project tools/LinkEts2Nav.Speech -- --export-context-jobs --output artifacts/speech/context-plan
dotnet run --project tools/LinkEts2Nav.Speech -- --generate-pack --keys .api-keys --texts artifacts/speech/context-plan/direct-texts.txt --output artifacts/speech/direct
dotnet run --project tools/LinkEts2Nav.Speech -- --generate-context-pack --keys .api-keys --output artifacts/speech/context
```

后两条命令会调用云端服务并可能计费；应顺序运行，避免触发并发限流。上下文生成开启 Qwen 的 `word_timestamp_enabled`，完整原句 WAV、原句清单及不含密钥的响应内容保存在输出的 `carriers` 子目录。工具按返回的实际文字和时间戳找到字边界，验证源音频校验和后换算为 24 kHz 样本范围；不会根据字数或音频长度比例猜测切点。

输出的 `context-provenance.json` 保存原句、填词、文字范围、源 WAV 校验和、样本切点和模型音色。时间戳缺失、文字不能逐字对应、切点跨越一个时间戳词，或服务把原句分成多个独立句子时，会保留完整源音频并报错，不生成推测的片段。提供方的索引可能按朗读字计数并将标点附在相邻字上，因此实际原文位置通过返回文字的严格顺序匹配确定。

修改切点处理或方案后，可使用保留的源音频离线重新裁切，不再调用 API：

```powershell
dotnet run --project tools/LinkEts2Nav.Speech -- --slice-context-pack --carriers artifacts/speech/context/carriers --output artifacts/speech/context
```

三个上下文命令都支持 `--plan FILE` 使用自定义方案。离线裁切总会更新片段及校验和，不能让旧半句音频或旧切点命中缓存。直接生成包与片段包分别只包含各自的文本，安装前使用默认完整任务清单与已有导入命令，将两者整理为一个完整固定包。

时间戳协议见 [Qwen 客户端事件](https://help.aliyun.com/zh/model-studio/qwen-audio-tts-client-events)及[服务端事件](https://help.aliyun.com/zh/model-studio/qwen-audio-tts-server-events)。

原有 Qwen 命令继续可用，仅显式执行时联网并可能计费。密钥放在被忽略的 `.api-keys` 中：

```powershell
dotnet run --project tools/LinkEts2Nav.Speech -- --generate-pack --keys .api-keys --output artifacts/speech/my-pack
dotnet run --project tools/LinkEts2Nav.Speech -- --generate-city-names --keys .api-keys --output artifacts/speech/my-pack/CityNames --texts artifacts/speech/city-names.txt
```

`--generate-pack` 也支持 `--texts UTF8_FILE`。Qwen 的城市生成命令会合并保留已有名称以支持追加；通用导入则严格采用本次任务清单。

## 使用语音包

插件页面 `/link-ets2-nav` 的“预生成语音包目录”可以直接选择本机已有包。填写目录后点击“保存并应用”，页面先验证清单、文件校验和和音频格式，成功后保存到现有语音设置。相对路径以宿主目录为基准；页面同时显示实际生效的绝对目录。

运行中的插件会切换语音工作进程、固定音频缓存及名称片段，不重启地图扫描或导航会话；正在播放的片段允许播完，后续使用新包。插件或语音尚未启用时，设置在下次启用后生效。保存不会复制音频、联网生成或改动本地模型等其他设置；移动或删除所选目录后应重新选择有效包。

开发宿主可选择任意来源包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 -Action Run -SpeechPackPath artifacts/speech/my-pack
```

也可以在宿主的 `Settings/LinkEts2Nav.Speech.json` 中将 `packDirectory` 指向自制包目录。路径相对宿主目录解析；本地动态合成仍需要原有模型配置。只安装城市子包也可使用。`dev.ps1` 未指定参数时保留原有本地 Qwen 目录作为兼容默认值，并不限制语音包来源。

缓存文件中的历史 `qwen-` 前缀只是旧命名，不影响其他服务商生成的包。请按所用语音库的许可自行决定音频能否分发。
