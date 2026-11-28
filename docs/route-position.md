# 导航定位

用于排查玩家在路线上却没有提示、走在旁路却被算作在路线内、或同一地点的提示进度突然跳变。

先用同一帧确认三件事。游戏给出的节点能否连成通路，车辆的位置与朝向能否区分这条通路和旁边的路，以及匹配出的沿程与提示使用的沿程是否来自同一条几何线。分别记录这三项结果，便于找到第一处失去证据的地方。[路线查询](../Plugins/LinkEts2Nav/Map/RoadPathSource.cs) · [导航调用顺序](../Plugins/LinkEts2Nav/Runtime/NavigationRuntime.cs)

一对入口和出口可以容纳多条车道，也可能是绕行后回到同一个入口。用原生车道的曲线链核对车辆实际走法，同时核对提示规划使用的代表线。游戏显示的剩余公里数受地图比例影响，比较长度时先查清两边各自的单位。[原生通路几何](../Plugins/LinkEts2Nav/Map/PrefabPathGeometry.cs) · [沿程投影](../Plugins/LinkEts2Nav/Core/DirectedPathMatcher.cs) · [行程估算](../Plugins/LinkEts2Nav/Map/RouteJourneyEstimator.cs)

环岛的几个出口可能共用一段车道。按各出口分别计算坡度时，同一段车道会得到略有差异的高度。用所属地图对象和原生曲线编号核对它们是否共用车道，并用车辆高度核对所在层级。[车道位置查询](../Plugins/LinkEts2Nav/Map/PrefabLanePositionIndex.cs)

复现时保留车辆实际行驶的车道、朝向以及前中后几个位置。用同一目的地搭配旁路上的真实位置检查误报，再检查驶过接口后提示是否正常推进。玩家行驶方向能提供的证据，与固定测速点只有坐标时能提供的证据不同。[地图回放工具](../tools/LinkEts2Nav.Probe/RandomRouteAudit.cs) · [平行道路与高架桥用例](../tests/LinkEts2Nav.UnitTests/RoadPathSourceTests.cs)

车辆从环岛内圈换到外圈，后面路口的里程也会改变。连续经过两个环岛时，检查第二个环岛的实际播报顺序，并核对已经过的出口是否重复提醒。[导航事件规划](../Plugins/LinkEts2Nav/Core/Guidance/NavigationEventPlanner.cs)
