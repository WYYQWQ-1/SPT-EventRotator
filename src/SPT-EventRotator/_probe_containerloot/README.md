# `_probe_containerloot` —— `containerLootInject` 离线探针

链接**真身源码**（`../ContainerLootInjectDimension.cs` / `SharedReflect.cs` / `ItemNames.cs` / `EventDef.cs`），
在**实机 `staticContainers.json` 临时副本**上跑注入，验证注入量 / 每容器最多 1 件 / 尺寸 / tpl /
覆盖率 / 注入明细日志 / 显式 location / 还原逐字节 / 生成期父节点修复 / 日志格式 / 降级。
实机文件全程只读，副本退出即删。

## 运行

```bash
cd fix/SPT-EventRotator/_probe_containerloot
PROBE_TMP=$PWD/tmp dotnet run -c Release
# 默认读 D:\PureEFTv4.1\EFTv4.1\SPT_Runtime，也可传参：dotnet run -c Release -- <SPT_Runtime>
# 最近一次实跑记录：../_containerloot_probe_out.txt（67 项通过 / 0 失败）
```

## 断言（67 项）

| # | 内容 |
|---|---|
| ① | 注入量 ≈ 容器数 × chance；`chance=1` 每个都 +1；`chance=0` 完全不动 |
| ② | 每个容器最多 1 件注入（本体每容器只预置 1 件容器自身） |
| ③ | 注入物尺寸适配容器格子（`_props.Width/Height` vs `_props.Grids[].cellsH/cellsV`，允许旋转） |
| ④ | 容器 tpl / 注入物 tpl 都能在实机 `items.json` 找到 |
| ⑤ | 还原后 `.Value` 序列化与注入前逐字节相同（含落盘文件字节比较）+ 实机文件 SHA-256 不变 |
| ⑥ | 新 `_id` 唯一、`parentId == Template.Root`、`slotId` = 真实 grid 名、`upd.StackObjectsCount` 正确（普通 1 / `stacks` 配的按配置） |
| ⑦ | `FixUpGeneratedLoot`：模拟 SPT 重写根 `_id` 后，注入件 parentId 改指新 Root、`location` 合法 |
| ⑦b/c/d | v1 事故复现（预注入件丢失 ⇒ 当场补 1 件）/ slotId 跟着改名走 / 实机 FastCloner 克隆不丢件 |
| ⑧ | 日志逐字格式 `[容器注入] woods 医疗箱：命中 N/M 个容器，注入 K 件`（12 类各一行） |
| ⑨ | 尺寸不符：4x2 军工电池塞 2x2 夹克 ⇒ 注入 0、日志「跳过 N 个尺寸不符」 |
| ⑩ | 非法 tpl ⇒ 整类丢弃 + warning，不抛、不注入 |
| ⑪ | 连续两次 `.Value` 不累积（每局新鲜对象） |
| ⑫ | v2 诊断：`生成钩子已到达` / `本局容器 N 个，含注入物件的容器 M 个，改 parentId K 件` / 「登记了但生成结果里找不到」warning |
| ⑬ | ★v3：覆盖率 ≥90%（420/429）、`注入明细 …（共 N 件）`、`样例 容器tpl(…) → 物品`（≤5 对）、整局 420 个容器 location 全部合法不重叠、弹药 stack=40、格子塞满 ⇒ 保持 null + 日志「格子算不出空格 N」 |

## 辅助脚本（取证 / 生成配置）

| 文件 | 用途 |
|---|---|
| `inspect_data.py` / `inspect2.py` / `inspect3.py` | 核实 `staticContainers.json` / `staticAmmo.json` / `looseLoot.json` 结构 |
| `scan_containers.py` | 全地图容器 tpl 反查（24 种/woods 429 个） |
| `lookup_containers.py` | woods 24 种容器 tpl → 名称 / 格子 |
| `lookup_items.py` / `lookup_items2.py` / `lookup_doll.py` | 物品 tpl → 名称 / 尺寸（含"娃娃"候选） |
| `plan_whitelist.py` | 类别白名单 → 尺寸适配矩阵 |
| `report_table.py` | 输出「容器 tpl ↔ 注入物 tpl」名称对照表 + 各类别数量/期望 |
| `insert_events.py` | 把 `containerLootInject` 块插入 `events.json` 的 `wild_wild_woods`（保留 BOM/格式） |

> 名称本地化来自 `SPT_Data/database/locales/global/{en,ch}.json`；`items.json` 只有英文 `_name`。
> v3 的 12 类白名单是用 `../_extend_whitelist_v3.py`（`--apply`）写进 `events.json` 的：
> 只替换 `containerLootInject` 那个 JSON 值，其余字节原样保留；tpl/尺寸/StackMaxSize 全部离线校验过。
