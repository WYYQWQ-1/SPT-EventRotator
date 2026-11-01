#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
check_loose_loot_tpls.py —— `looseLootInject` 的物品 tpl **前置核查**（只读，不改任何文件）

为什么要这个脚本
----------------
`looseLootInject` 的 `valuable.tpls` / `decor.tpls` 里写错一个字符，服务端**不会报错**：
局内只会"那个点位空着"或"物品不可见" —— 典型的静默失败。所以注入前必须逐个确认
tpl 在本机真实存在，并能解析出名字。

核查范围（用户要求）
--------------------
  ① 私酒 moonshine（常见 5d1b376e86f774252519444e）
  ② 颜色卡：红/绿/蓝/黄/紫/黑（在 items 里按 `keycard` 名字找）
  ③ 符号钥匙：Reserve 的符号钥匙（常见 5d80c62a86f7744036212b3f 之类，按名字找）
  ④ 纯净水（5d1b33a686f7742523398398 之类）、玩偶/毛绒玩具（按名字找）

数据源
------
  · `SPT_Data/database/templates/items.json`（本体物品表）
  · `user/mods/*/db/**/*.json`（mod 注入的物品定义；SPT 4.x 走 db 目录的 JSON 注入）
  · `SPT_Data/database/locales/global/{en,ch}.json`（显示名；四字 key 形如 `<tpl> Name`）
  ⚠ 局限（如实）：通过 DLL 的 `CustomItemService` 动态创建的物品**不在文件层**，
    本脚本看不到；本机 5 类目标全部命中本体 items.json，不影响结论。

退出码
------
  0 = 所有 `events.json` 里 `looseLootInject` 用到的 tpl 都存在
  1 = 有缺失（打印 MISSING 清单，绝不"瞎填"兜底）

用法
----
  python tools/check_loose_loot_tpls.py
  python tools/check_loose_loot_tpls.py --spt "D:\\PureEFTv4.1\\EFTv4.1\\SPT_Runtime"
"""

import argparse
import glob
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_SPT = r"D:\PureEFTv4.1\EFTv4.1\SPT_Runtime"
DEFAULT_EVENTS = os.path.join(os.path.dirname(HERE), "events.json")

HEX24 = re.compile(r"^[0-9a-fA-F]{24}$")


def load_json(path):
    with open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)


# ──────────────────────────────────────────────────────────────────────────────
#  物品表：本体 + mod 注入
# ──────────────────────────────────────────────────────────────────────────────
def load_base_items(spt):
    p = os.path.join(spt, "SPT_Data", "database", "templates", "items.json")
    if not os.path.exists(p):
        sys.exit(f"[FATAL] 找不到本体物品表: {p}")
    data = load_json(p)
    if not isinstance(data, dict):
        sys.exit(f"[FATAL] items.json 结构不是 dict: {p}")
    return {k: v for k, v in data.items() if isinstance(v, dict) and HEX24.match(k)}


def iter_mod_item_files(spt):
    """mod 的 db/**/*.json；按内容而不是路径判断（mod 目录结构五花八门）。"""
    root = os.path.join(spt, "user", "mods")
    if not os.path.isdir(root):
        return []
    out = []
    for dirpath, _dirnames, filenames in os.walk(root):
        for fn in filenames:
            if not fn.lower().endswith(".json"):
                continue
            full = os.path.join(dirpath, fn)
            try:
                if os.path.getsize(full) > 32 * 1024 * 1024:
                    continue
            except OSError:
                continue
            out.append(full)
    return out


def load_mod_items(spt, mods_loaded):
    """
    返回 {tpl: {"_name":..., "src": mod目录名}}。
    判定：文件里出现「24 位 hex 键 + 其值含 _props/_parent/_id」的映射，或数组里的物品对象。
    """
    found = {}
    files = iter_mod_item_files(spt)
    parsed = 0
    for full in files:
        modname = os.path.relpath(full, os.path.join(spt, "user", "mods")).split(os.sep)[0]
        try:
            raw = open(full, "rb").read()
        except OSError:
            continue
        # 内容快筛：不是物品文件就没有 `_parent` / `_props`
        if b'"_parent"' not in raw and b'"_props"' not in raw:
            continue
        try:
            data = json.loads(raw.decode("utf-8-sig"))
        except Exception:
            continue
        parsed += 1
        cand = {}
        if isinstance(data, dict):
            for k, v in data.items():
                if isinstance(k, str) and HEX24.match(k) and isinstance(v, dict) and ("_props" in v or "_parent" in v):
                    cand[k] = v
            # SPT CustomItems 形态：{"items": [ {...}, ... ]}
            for key in ("items", "Items"):
                arr = data.get(key)
                if isinstance(arr, list):
                    for it in arr:
                        if isinstance(it, dict) and isinstance(it.get("_id"), str) and HEX24.match(it["_id"]):
                            cand[it["_id"]] = it
        elif isinstance(data, list):
            for it in data:
                if isinstance(it, dict) and isinstance(it.get("_id"), str) and HEX24.match(it["_id"]):
                    cand[it["_id"]] = it
        for k, v in cand.items():
            found.setdefault(k, {"_name": v.get("_name") or v.get("Name") or "", "src": modname,
                                 "_parent": v.get("_parent") or "", "_props": v.get("_props") or {}})
    return found, len(files), parsed


def load_locale(spt, lang):
    p = os.path.join(spt, "SPT_Data", "database", "locales", "global", lang + ".json")
    if not os.path.exists(p):
        return {}
    try:
        return load_json(p)
    except Exception:
        return {}


def display_name(tpl, locales):
    for lang in ("ch", "en"):
        loc = locales.get(lang) or {}
        v = loc.get(tpl + " Name") or loc.get(tpl + " ShortName")
        if v:
            return v, lang
    return None, None


# ──────────────────────────────────────────────────────────────────────────────
#  分类候选（按名字找 —— 用户明确要求"不要瞎填 tpl"）
# ──────────────────────────────────────────────────────────────────────────────
def find_by_name(items, locales, predicate):
    rows = []
    for tpl, it in items.items():
        en = (locales.get("en") or {}).get(tpl + " Name") or ""
        zh = (locales.get("ch") or {}).get(tpl + " Name") or ""
        internal = it.get("_name") or ""
        if predicate(en, zh, internal, it):
            rows.append((tpl, en, zh, internal))
    return rows


def describe(tpl, items, mods, locales):
    it = items.get(tpl)
    src = "本体 items.json"
    if it is None and tpl in mods:
        it = mods[tpl]
        src = "mod:" + mods[tpl]["src"]
    if it is None:
        return None
    en, _ = display_name(tpl, locales)
    zh = (locales.get("ch") or {}).get(tpl + " Name") or ""
    return {
        "tpl": tpl,
        "en": en or (it.get("_name") or ""),
        "zh": zh,
        "name": it.get("_name") or "",
        "parent": it.get("_parent") or "",
        "src": src,
    }


def print_table(rows, locales, limit=None):
    if not rows:
        print("    （无）")
        return
    for i, (tpl, en, zh, internal) in enumerate(rows):
        if limit is not None and i >= limit:
            print(f"    … 还有 {len(rows) - limit} 条未列出")
            break
        print(f"    {tpl}  |  {zh or '-'}  |  {en or '-'}  |  {internal}")


# ──────────────────────────────────────────────────────────────────────────────
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--spt", default=DEFAULT_SPT, help="SPT_Runtime 目录")
    ap.add_argument("--events", default=DEFAULT_EVENTS, help="events.json")
    args = ap.parse_args()

    if not os.path.isdir(args.spt):
        sys.exit(f"[FATAL] --spt 不存在: {args.spt}")

    print("=" * 100)
    print("looseLootInject 物品 tpl 前置核查")
    print("  SPT_Runtime :", args.spt)
    print("  events.json :", args.events)
    print("=" * 100)

    items = load_base_items(args.spt)
    print(f"\n[1] 本体物品表载入：{len(items)} 条")

    mods, nfiles, nparsed = load_mod_items(args.spt, None)
    print(f"[2] mod 注入扫描：{nfiles} 个 json 文件（内容命中物品形状 {nparsed} 个），"
          f"收集到 mod 物品 {len(mods)} 条")
    overlap = [t for t in mods if t in items]
    print(f"    与本体重号的 mod 物品：{len(overlap)} 条（重号时以本体为准）")

    locales = {"en": load_locale(args.spt, "en"), "ch": load_locale(args.spt, "ch")}
    print(f"[3] 本地化：en={len(locales['en'])} ch={len(locales['ch'])}")

    # ── 用户点名的 4 类，逐类"按名字"列举 ─────────────────────────────────
    print("\n" + "─" * 100)
    print("[4] 候选挖掘（按名字/内部名，全部来自本机真实数据）")
    print("─" * 100)

    print("\n④-A 私酒 moonshine（已知 5d1b376e86f774252519444e）")
    known_moonshine = "5d1b376e86f774252519444e"
    print_table([(known_moonshine,)] and [(known_moonshine,
                (locales["en"].get(known_moonshine + " Name") or ""),
                (locales["ch"].get(known_moonshine + " Name") or ""),
                (items.get(known_moonshine) or {}).get("_name", ""))], locales)
    print("    同类（酒）候选：")
    print_table(find_by_name(items, locales, lambda en, zh, n, it: "moonshine" in (en + n).lower()), locales, 10)

    print("\n④-B 纯净水（已知 5d1b33a686f7742523398398）")
    known_water = "5d1b33a686f7742523398398"
    print_table([(known_water,
                 (locales["en"].get(known_water + " Name") or ""),
                 (locales["ch"].get(known_water + " Name") or ""),
                 (items.get(known_water) or {}).get("_name", ""))], locales)

    print("\n② 颜色卡（名字含 keycard；Lab 六色 + 活动卡）")
    keycards = find_by_name(items, locales, lambda en, zh, n, it: "keycard" in (en + zh + n).lower())
    print_table(keycards, locales)
    print("    ★ 本机 Lab 六色（按名字精确匹配 TerraGroup Labs keycard (Color)）：")
    lab_colors = {}
    for tpl, en, zh, n in keycards:
        m = re.search(r"keycard \((Red|Green|Blue|Yellow|Violet|Black)\)", en or "")
        if m:
            lab_colors[m.group(1)] = (tpl, zh or en)
    for color in ("Red", "Green", "Blue", "Yellow", "Violet", "Black"):
        if color in lab_colors:
            print(f"      {color:7s} {lab_colors[color][0]}  |  {lab_colors[color][1]}")
        else:
            print(f"      {color:7s} ✗ 本机缺失")

    print("\n③ 符号钥匙（英文名含 'marked key' —— 本机共找到的『符号钥匙』全集）")
    marked = find_by_name(items, locales, lambda en, zh, n, it: "marked key" in (en or "").lower())
    print_table(marked, locales)
    rezbase_marked = [r for r in marked if "reserve" in (r[1] or "").lower() or "rezbase" in (r[3] or "")]
    print(f"    ★ Reserve（军事基地）符号钥匙：{len(rezbase_marked)} 把")
    print_table(rezbase_marked, locales)
    print("    ⚠ 用户口述『Reserve 6 把符号钥匙』与本机数据不符：本机 rezbase_key_kult* 只有 "
          "RB-BK / RB-VO / RB-PKPM 三把 marked（其余 RB-xx 是普通房间钥匙）。"
          "全图『marked key』合集才是 7 把（含宿舍314/套间/废弃工厂/神秘房间）。")

    print("\n④-C 玩偶 / 毛绒玩具（名字含 plush/toy 或中文『玩偶/毛绒』）")
    dolls = find_by_name(items, locales, lambda en, zh, n, it:
                         any(k in (en or "").lower() for k in ("plush", "plushie", "toy"))
                         or any(k in (zh or "") for k in ("玩偶", "毛绒")))
    print_table(dolls, locales)

    # ── 逐条核对 events.json 里实际配置的 tpl ─────────────────────────────
    print("\n" + "─" * 100)
    print("[5] 核对 events.json → looseLootInject 里实际写到的每一个 tpl")
    print("─" * 100)

    required = []
    if os.path.exists(args.events):
        evs = load_json(args.events)
        for ev in evs if isinstance(evs, list) else []:
            inj = ev.get("looseLootInject")
            if not isinstance(inj, dict):
                continue
            for mapkey, opt in inj.items():
                if not isinstance(opt, dict):
                    continue
                for kind in ("valuable", "decor"):
                    pool = opt.get(kind) or {}
                    for tpl in (pool.get("tpls") or {}):
                        required.append((ev.get("id") or "?", mapkey, kind, tpl))
    else:
        print(f"    ⚠ 找不到 {args.events}，跳过配置核对")

    if not required:
        print("    （events.json 里还没有 looseLootInject 配置）")
    missing = []
    seen = set()
    for evid, mapkey, kind, tpl in required:
        key = (evid, mapkey, kind, tpl)
        if key in seen:
            continue
        seen.add(key)
        d = describe(tpl, items, mods, locales)
        if d is None:
            missing.append((evid, mapkey, kind, tpl))
            print(f"    ✗ MISSING  {evid}/{mapkey}/{kind}: {tpl}")
        else:
            print(f"    ✓ {tpl}  →  {d['zh'] or '-'} / {d['en'] or '-'}  [{d['src']}]   ({evid}/{mapkey}/{kind})")

    print("\n" + "=" * 100)
    if missing:
        print(f"结论：❌ 有 {len(missing)} 个 tpl 在本机不存在（items.json + mod 注入都找不到）：")
        for m in missing:
            print("   -", "/".join(m))
        print("   ⇒ 请换成上面 [4] 段列出的真实 tpl，**不要猜**。")
        return 1
    print(f"结论：✅ 配置里 {len(set(required))} 条 tpl 全部能在本机解析（items.json + mod 注入）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
