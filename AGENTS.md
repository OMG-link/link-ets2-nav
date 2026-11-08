# Agent 协作指南

## 本地配置

开始工作前，如仓库根目录存在 `AGENTS.local.md`，请读取并遵循其中的本地补充约定；该文件为可选项，不属于版本控制内容。

## 项目边界

- `Plugins/LinkEts2Nav/` 是插件实现。
- 优先复用 ETS2LA 已解析的数据和公开能力；仅补读宿主已过滤或未公开的数据。替代已有能力前，先验证接口、数据覆盖及线程安全，避免重复维护地图解析和通用算法。
- `ETS2LA/` 是固定提交的上游子模块；不要无意更新其版本或直接改动上游实现。
- 开发宿主定制集中在 `dev-host/`，由根目录 `Directory.Build.targets` 接入构建。
- `artifacts/`、`bin/`、`obj/` 是生成内容，不应作为源码修改或提交。

## 验证

- 构建：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 -Action Build`。
- 测试：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts/dev.ps1 -Action Test`。
- 日常可用 `-Action Test -TestScope Unit|Integration` 缩小项目范围，用 `-TestFilter` 指定相关用例；依赖未变时可加 `-NoRestore`。提交前运行默认全量测试入口。
- `-NoBuild` 仅用于源码未变时重跑已有同配置测试产物，不得用于验证新代码。
- 集成测试位于 `tests/DevHost.IntegrationTests/`；单元测试位于 `tests/LinkEts2Nav.UnitTests/`。
- 不启动游戏或安装游戏通信 DLL，除非任务明确需要。

## 公共文档

- README 面向最终用户，描述功能、安装、使用和限制。
- 可复用的开发操作方法放在 `docs/development.md`。
- 文档应与实现保持一致，不包含个人环境路径、账户信息或凭据。
