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

城市名使用独立清单和 `CityNames` 子包。可从自己的游戏安装导出城市定义原名，不需要全图解析：

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

## 可选 Qwen 适配器

原有 Qwen 命令继续可用，仅显式执行时联网并可能计费。密钥放在被忽略的 `.api-keys` 中：

```powershell
dotnet run --project tools/LinkEts2Nav.Speech -- --generate-pack --keys .api-keys --output artifacts/speech/my-pack
dotnet run --project tools/LinkEts2Nav.Speech -- --generate-city-names --keys .api-keys --output artifacts/speech/my-pack/CityNames --texts artifacts/speech/city-names.txt
```

`--generate-pack` 也支持 `--texts UTF8_FILE`。Qwen 的城市生成命令会合并保留已有名称以支持追加；通用导入则严格采用本次任务清单。

## 使用语音包

开发宿主可选择任意来源包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 -Action Run -SpeechPackPath artifacts/speech/my-pack
```

也可以在宿主的 `Settings/LinkEts2Nav.Speech.json` 中将 `packDirectory` 指向自制包目录。路径相对宿主目录解析；本地动态合成仍需要原有模型配置。只安装城市子包也可使用。`dev.ps1` 未指定参数时保留原有本地 Qwen 目录作为兼容默认值，并不限制语音包来源。

缓存文件中的历史 `qwen-` 前缀只是旧命名，不影响其他服务商生成的包。请按所用语音库的许可自行决定音频能否分发。
