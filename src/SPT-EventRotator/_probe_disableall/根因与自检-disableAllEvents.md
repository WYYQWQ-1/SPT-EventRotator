# 「关闭所有活动后仍生效」根因 + 硬开关 `disableAllEvents` 交付说明（v2.7.0）

> 审计对象：`fix\SPT-EventRotator\`（服务端）。真实日志取自用户实机
> `D:\PureEFTv4.1\EFTv4.1\SPT_Runtime\user\logs\spt\spt20261030.log`（服务端日志，4.4 MB）
> 与实机 mod 目录 `...\user\mods\SPT-EventRotator\force.txt` / `当前生效活动.txt`。

---

## 一、根因（按因果排序）

### 根因 1（成立，最主要）：「关掉所有活动」= 只清 `force.txt`，而自动层会立刻把活动补回来

- 面板上取消所有勾选 / 点「清空」走的是 `ConfigPage.razor ClearForce()`
  → `ConfigWeb.SetForce(new List<string>())`（`ConfigWeb.cs:528-556`）——**只写 `force.txt` 为空**，
  然后 `EventEngine.ReloadSettingsFromDisk()`。
- `force.txt` 只是 `SelectActive` 的**第 ① 层**（`EventEngine.cs:676` 起）。清空之后流程**继续往下走**
  ②日期窗 → ②b 季节活动阶段（`EventEngine.cs:875`）→ ③主轮换（`Rotation.cs:489`）
  → ④奖励轮换（`RewardRotation.cs:89`）。
- 实机日志（`spt20261030.log`，06:30 那次启动，force.txt 为空）：
  ```
  06:30:21.923 ★奖励类轮换已启用：每局随机开 1~2 个『奖励类』活动
  06:30:21.979 ★季节活动阶段已到（start）→ 自动开启『圣诞 2022』（本季 WINTER，进度 15%）
  06:30:21.986 [奖励轮换] 今日奖励活动（1/1 个）：labs_free_generic
  06:30:27.542 ready. active='santa_2022+christmas+newyear+christmas_january+labs_free_generic'
  ```
  探针用**同一份 events.json + config.json** 复现出**逐字相同的集合**（见自检输出 ①）。

### 根因 2（成立）：面板保存后不触发 Restore，DB/维度层要等到下一局进图才回退

- `RestoreThenApply()`（唯一做"先还原再套用"的方法）只挂在 `EventEngine.OnRefresh`（`Plugin.cs:341`），
  而 `OnRefresh` 全项目**只有一个触发点**：`Patches/BossWavePatch.cs:2323`（进图）。
- `ReloadSettingsFromDisk()`（面板保存/清空强制后调用的那个）只做 `LoadConfig + SelectActive + 写状态文件`，
  **不调 `OnRefresh`** ⇒ `_restoreActions` 不回放、DB 层（首领/敌对性/红骷髅/BossZone）不动。
- ⇒ 面板已经显示新集合（甚至"无活动"），游戏里还是上一轮活动的首领/掉落，直到下一次进图。

### 根因 3（成立，但只影响"任务进度"）：`当前生效活动.txt` 不重放活动，只做任务轮换基线

- `ReadActiveIdsFromStatusFile()`（`Plugin.cs:2022`）**只被** `ApplyQuestRotationReset()` 用来判断
  "哪些活动进/出了"以重置任务进度；**不会**据此重新套用活动。
  ⇒ "重启后按上一轮集合把活动套回去"这一条**不成立**（但它会清任务进度，容易被误认为"活动还在"）。

### 根因 4（成立）：季节阶段在无活动时仍自动接管（与根因 1 同源，独立一层）

- `SeasonRotation.EventSeasonDueNow()`（`SeasonRotation.cs:171`）在 `mode=rotate` 时只要"季初已到"就返回季节名，
  `EventEngine.cs:875` 立刻 `Actives.Add(该季节变体)` + 同族展开（`santa_2022 + christmas + newyear + christmas_january`）。
- 该路径**完全不看** force.txt / 面板勾选（只看 `rotation.exclude`）。

### 根因 5（成立）：还原不彻底 —— 「无活动分支」漏调原生季节还原 + SPT 季节 API 无回滚

- `RestoreThenApply()` 的 `evs.Count == 0` 分支（`Plugin.cs:857`）**直接 return**，
  而 `PrepareHalloweenNative(false)` / `PrepareChristmasNative(false)`（`Plugin.cs:998-999`）只写在
  **有活动**的那条路径末尾 ⇒ 关掉活动后：
  `seasonalevents.json` 内存里的 `zombieSettings.enabled`、实验室感染、`globals.EventType`、
  圣诞老人 `ItemsToDrop`、Khorovod 概率**保持开启**。
- 另外 `TryForceSeasonal(null)` 只能 `EnableSeasonalEvents(null)`（还原"按日期自动判定"），
  **SPT 的 `ForceSeasonalEvent` 本身没有回滚 API**（`Plugin.cs:3126-3148` 注释实证）⇒
  已注入的圣诞物资 transformer / gifter / 事件首领 / 商人头像会持续到换季。

### 根因 6（成立，表现层）：UI 与实际不同步

- `WriteActiveStatus()`（`Plugin.cs:4244`）在"重算"后就写盘/写 HTTP 快照，但维度层没还原
  （根因 2）⇒ 面板说"没有活动/换成了 X"，服务端 DB 还是旧活动。

### 排除项（不成立）

- `force.txt` 优先级**不是**这里的问题：用户已经把 `force.txt` 清空（探针 ① 也复现"清空后照样有活动"）；
  只有在它**有值**时才是最高优先级（`EventEngine.cs:676`，`FORCED event(s)` 日志可证）。
- 日期窗本次**没有**参与：日志明写
  `[日期窗] dateWindowRule=seasonOffOnly · seasonRotation.mode=rotate ⇒ 日期窗不参与判定`。

---

## 二、硬开关实现

- 配置键：`config.json → "disableAllEvents": false`（顶层 bool，默认 false）
  - 中文：**不启用任何活动** —— 开启后无论如何都不套用任何活动（含 `force.txt`、轮换、季节自动接管、日期窗、奖励轮换），
    服务端先做一次完整还原，然后停在裸 SPT。
  - English: **Disable all events** —— no event is ever applied; everything previously applied is restored once.
- 读取：`EventEngine.LoadConfig`（`EventEngine.cs:129-138`）；老配置由 `MergeMissingKeys` 自动补键
  （`Plugin.cs:450` 默认模板）。
- 闸门 ①②③：
  - `EventEngine.SelectActive` **最先**判（`EventEngine.cs:655-672`）：清空 `Actives`、清空 `ForcedId`、直接 return；
  - `EventEngine.RefreshFromForce` **连 force.txt 都不读**（`EventEngine.cs:451-460`）；
  - `EventEngine.ReloadSettingsFromDisk` 检测状态翻转 ⇒ 立刻 `OnRefresh()`（= 一次「完整还原 + 按新状态套用」，`EventEngine.cs:507-521`）。
- 完整还原：`Plugin.HardRestoreForDisableAllEvents()`（`Plugin.cs:1187-1257`），入口在 `RestoreThenApply`
  的 `Restore()/ClearStaleStaticState()` 之后（`Plugin.cs:779`）：
  ① `RestoreDbLayer()`+`MarkDbRestored()`+`SetForceNight(0)`（DB 首领/概率/红骷髅/zone/hostility/僵尸敌对/NonWave，**无条件**）
  ② `PrepareHalloweenNative(false)`+`PrepareChristmasNative(false)`（`zombieSettings.enabled`/EventType/礼物/Khorovod/召唤）
  ③ `ExternalBridges.ApplyBlackout(false)`（停电概率回 `.bak-eventrotator` 原值）
  ④ 天气 `OverrideSeason → null` + `EnableSeasonalEvents(null)`（**绕开** rotate 接管）
  ⑤ `ApplyQuestGate(空集合)`（活动任务全部收窗）+ `WriteActiveStatus(空)`（面板/状态文件显示"不启用任何活动"）
- 日志（用户指定逐字）：
  - `[EventRotator] ⛔ disableAllEvents=true ⇒ 不启用任何活动（已还原上次套用的全部改动）`
  - `[EventRotator] disableAllEvents=false ⇒ 恢复正常轮换逻辑`
- 面板：`Web/ConfigPage.razor`「基础」页最顶部红色卡片开关（`ConfigPage.razor:74-88`）+ 中英文说明（`ConfigPage.razor:684`）；
  白名单 `ConfigWeb.BoolKeys`（`ConfigWeb.cs:70`）；`SetForce` 在该开关下会明确提示"已写入但不采用"（`ConfigWeb.cs:550-557`）。

## 三、自检

- 编译：`dotnet build -c Release` ⇒ **0 警告 0 错误**（主工程与探针工程都是）。
- 探针：`_probe_disableall/`（真跑 Release 产物 DLL + 真实 SPT 模型类型，全部在临时目录，不动实机）
  ```powershell
  cd fix\SPT-EventRotator\_probe_disableall
  dotnet build -c Release ; .\bin\Release\net10.0\ProbeDisableAll.exe
  ```
  ⇒ **29 项全过 / 0 失败**，完整输出见 `_probe_disableall\自检输出-disableAllEvents.txt`。
