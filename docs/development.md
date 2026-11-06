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

单元测试项目目前仅建立结构，没有占位测试；新增导航算法时再加入真实用例。不要用空断言或重复实现的测试制造通过数量。

## 上游诊断

根目录 `Directory.Build.targets` 默认屏蔽 ETS2LA 子模块项目中已确认的编译警告，以及四条已知上游依赖漏洞公告的诊断。插件和测试源码的编译诊断、新增漏洞公告仍然保留。屏蔽不会修复漏洞，也不改变实际依赖版本。

需要审查完整诊断时，执行以下命令；恢复依赖和构建都必须传入开关，避免使用此前缓存的审计结果：

```powershell
dotnet restore LinkEts2Nav.slnx -p:NavShowUpstreamWarnings=true
dotnet build LinkEts2Nav.slnx --no-restore --no-incremental -p:NavShowUpstreamWarnings=true
```

恢复默认显示策略后，运行 `dotnet restore LinkEts2Nav.slnx` 刷新依赖诊断。上游更新时应重新审查并移除不再需要的屏蔽项。
