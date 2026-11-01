# SPT-EventRotator（Kirisame dish）

SPT 4.x 的**活动轮换 / 季节活动接管** mod。

把《逃离塔科夫》官方历次活动（万圣节、元旦、愚人节、邪教之夜、灾厄小队等）
重新组织成可轮换的玩法：想开哪个开哪个，不该刷的怪不会乱刷。

## 功能

- **活动轮换**：按 7 天轮换或手动强制指定当前活动
- **季节窗接管**：接管官方季节活动窗口，避免官方季节与自选活动互相覆盖
- **季节首领管理**：非季节活动时清理孤儿季节首领；季节活动时保留该族首领
- **自定义首领/小队**：灾厄小队、八影会谈、狂野森林、战争之因等
- **空投物品**：按来源标记追加/撤回，多个活动互不误删
- **任务门控**：活动没开时，对应活动任务在服务端层拒绝接取
- **网页/F12 面板**：可视化管理活动与开关
- **兼容处理**：ABPS 重建缓存补条目、第三方刷怪模组保护

## 目录结构

```
src/SPT-EventRotator          服务端（net10.0）
src/SPT-EventRotator.Client   客户端插件（BepInEx / netstandard2.1）
```

## 编译

需要 .NET SDK 10 与本地 SPT 4.1.x 运行目录（工程里引用的是 `D:\PureEFTv4.1\EFTv4.1`，
请按自己的路径修改 `.csproj` 里的 HintPath）。

```
dotnet build src/SPT-EventRotator -c Release
dotnet build src/SPT-EventRotator.Client -c Release
```

## 安装（玩家）

解压到游戏根目录，完全关闭服务端后重启。包内含：

```
SPT_Runtime/user/mods/SPT-EventRotator/
SPT_Runtime/user/patchers/com.kirisame.eventrotator/
BepInEx/plugins/SPT-EventRotator.Client/
BepInEx/patchers/SPT-EventRotator.Patcher.dll
```

> 想保留自己的设置时，安装前先删掉包里的 `config.json`。

## 卸载

以下四项一起删除，缺一项会导致进不去游戏：

```
SPT_Runtime/user/mods/SPT-EventRotator
SPT_Runtime/user/patchers/com.kirisame.eventrotator
BepInEx/plugins/SPT-EventRotator.Client
BepInEx/patchers/SPT-EventRotator.Patcher.dll
```

## 已知问题

- 复仇小鹿语音：数据已改为扎里亚奇语音，但部分客户端不出声（缺语音资源）
- 万圣节僵尸活动期间：实验室强制停电、非感染者对玩家中立（官方设定）
- 部分 UI（竞技场裁判头像）走客户端资产，mod 无法修改

## 许可

MIT
