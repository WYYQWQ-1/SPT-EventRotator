# 第三方来源与许可（v1.3.12）

本 mod 本体是 **MIT**。但有**部分数据**来自第三方项目，按其原许可保留，**不从 MIT 重新授权**。

---

## 1. PPES — PastPresentEventSelector

- 仓库：https://github.com/fernandolirola/PastPresentEventSelector
- 许可：**MIT**（版权行 `Copyright (c) 2026 zezinho`）
- 我们用到的：
  - `blackout_2026`（5 条）、`tarkov_mystery`（66 条）、`terragroup_trail`（19 条）、
    `down_the_rabbit_hole`（6 条）、`arms_race`（4 条）等任务线的
    任务 id / 结构（前置链、条件、奖励）与英文文案
  - 部分机制的思路（事件限定物品塞走私者背包、感染者扩展的字段语义）
- 我们改了什么：逐条列在对应 `questlines/*.json` 的 `_devNote` 里。

---

## 2. Scourge（Archangel / Manimal1920）

- 仓库：https://github.com/danauraborealis/Scourge
- 许可：
  - **代码 `src/` = MIT**
  - **数据 `data/` = CC BY-NC-SA 4.0**（署名 Archangel，原文见其 `data/LICENSE`）
- 我们用到的（属于 **data/ 部分 ⇒ CC BY-NC-SA 4.0**）：
  - 5 件任务物品的物品模板与手册/价格条目：
    `6ac4cfbc99bdd7779608792f`（天灾的智能手机）、
    `6ac4d528d8aa0d51df01995c`（前体容器）、
    `6ac4e0e576842fabb501d5d0`（TerraGroup 员工手提箱）、
    `6ac4eccad8aa0d51df019974`（TerraGroup 员工包裹）、
    `6ac64a82d614fb7b2f0516a1`（装有货运数据的 SSD）
  - 7 条灾厄任务的任务定义（结构与英文文案）+ 3 张任务图标
  - 「实验室/沙盒/街区按坐标刷任务物品」的刷点数据（`questLoot.json`）
  - 灾厄小队的小队战术层（BigBrain 行为文件，抄自其 `ScourgeAi/`，属 **src/ ⇒ MIT**）
- **我们不含**：
  - 其音频包 `bossscourge_voice.bundle`（6.7 MB）
  - 其自定义 3D 模型/贴图资源
- 中文文案为本项目自行翻译（对方 `ch` 语种实际是英文）。

**CC BY-NC-SA 的约束**：非商业使用；如再分发这些数据，需署名并沿用相同许可。
若你只想用纯 MIT 版本，删除 `questlines/scourge_incursion.json` 里的
`items` / `itemMeta` / 上述 7 条 `quests` 即可（本 mod 其余功能不受影响）。

---

## 3. 其它

- SAIN（Solarint）：仅通过其 `SAIN.ServerInterop` 公开接口注册角色，未包含其代码。
- BigBrain（danauraborealis 等）：灾厄战术层依赖其层注入机制，未包含其代码。
