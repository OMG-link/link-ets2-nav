# Link ETS2 Nav

面向 Euro Truck Simulator 2 的导航与测速探头预警插件，基于 ETS2LA 框架开发。

项目尚在开发中，目前未提供可用的导航、语音播报或测速探头预警功能。

## 安装与使用

本插件由外部程序 ETS2LA 加载，不通过游戏内的模组管理器安装。

有可用构建后，将 `LinkEts2Nav.dll` 放入 ETS2LA 的 `Plugins/` 目录，在插件管理器中启用 **Link ETS2 Nav**。ETS2LA 及其游戏通信 DLL 需要与游戏版本匹配。

插件本身不包含自动驾驶或车道保持功能。工程内对开发宿主的界面和插件加载限制不会随单独的插件 DLL 分发。

## 开发

源码构建、调试与开发宿主配置见 [开发指南](docs/development.md)。

## 致谢与许可

- [ETS2LA](https://github.com/ETS2LA/ETS2LA)：插件宿主及游戏数据接口，采用 GPL-3.0。
- [TruckLib](https://github.com/sk-zk/TruckLib)：地图及资源解析库，保留其原有许可。
- [官方插件模板](https://github.com/ETS2LA/example-plugin)：采用 MIT，原许可证见 [LICENSE](LICENSE)。

发布时须遵守所包含代码与资源各自的许可证；模板许可不代表所有依赖使用相同许可。本项目与 SCS Software 无隶属关系。
