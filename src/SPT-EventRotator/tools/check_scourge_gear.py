#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Scourge 装备「可解析性 / 可生成性」离线校验（不启动服务端、不改实机）。

用途
----
`db/bots/types/bossScourge.json` / `followerScourge.json` 的装备全部照抄
`danauraborealis/Scourge` 的 `*EventLab.json`。那两份数据引用 290 个 id，
其中一部分不在本机 `items.json`，而是由已装 mod 的 `db/CustomItems` /
`db/CustomClothing` 在运行期注入（实测 88 个来自 WTT-ContentBackport）。
"删掉解析不到的 id"会把武器/装备预设挖空 ⇒ `BotWeaponGenerator` 报
「required slot unable to be filled」→ 回落预设 → 预设也没有（克隆武器没有预设）
→ 空武器 → `Failed to generate bot`。所以本脚本只做「发现」，绝不改数据。

它逐项复刻 SPT 4.1.6 的失败路径（反编译 `_forensic_tmp/ilspy_full/...`）：
  1. 引用解析     : BotInventoryGenerator.GenerateEquipment → ItemHelper.GetItem 失败 = 该件被丢弃
  2. 装备槽类别   : 头盔槽塞耳机会被静默换上/丢掉（客户端表现错乱）
  3. 容器 Grids   : `ContainerDetails..ctor` 在 `Properties.Grids == null` 上 NRE
                    （2026-10-16 线上事故根因，本 mod 的容器守卫挡的就是这条）
  4. 外观 body    : `BotGenerator.SetBotAppearance` 直接 `templateTable.Customization[body]`
                    ⇒ body 不在表里必抛 KeyNotFoundException
  5. 武器必填槽   : `BotEquipmentModGenerator.ChooseModToPutIntoSlot` 填不满 required 槽
                    ⇒ `IsWeaponValid` 为 false ⇒ 回落 `GetPresetWeaponMods`
                    ⇒ 克隆武器无预设 ⇒ 空武器 ⇒ 后续 NRE
  6. 口径弹药     : `GetWeightedCompatibleAmmo` 无该口径且无 DefAmmo 时 NRE

用法
----
    python check_scourge_gear.py                       # 默认路径（本机实机数据库，只读）
    python check_scourge_gear.py --game "D:\\...\\SPT_Runtime" --mod "..\\"
    python check_scourge_gear.py --verbose              # 打印每个引用 id 的出处

退出码：0 = 全部通过；1 = 存在致命项（会在真实生成时导致 NRE / bot 刷不出）。
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from collections import Counter, OrderedDict

# Windows 控制台默认 GBK：✔/⚠/中文混排会 UnicodeEncodeError。统一按 UTF-8 输出。
try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_MOD = os.path.dirname(HERE)                       # fix/SPT-EventRotator
DEFAULT_GAME = r'D:\PureEFTv4.1\EFTv4.1\SPT_Runtime'

# ────────────────────────────────────────────────────────────────────────────
# 上游（仓库）引用、但本机任何来源都不存在的 3 个 id —— 已按「同槽位/同类别」替换。
# 本表同时是「缺 X / 替换 Y / 新增 Z」三个数字的唯一事实来源。
# 键 = (bot, 容器种类, 槽位, 上游 id)；值 = (替换 id, 理由)
# ────────────────────────────────────────────────────────────────────────────
REPLACEMENTS = [
    # 上游 boss `inventory.items.Pockets` 两项 106 发的堆叠物，本机全库（含 44 个 mod 的
    # CustomItems/CustomClothing）都不存在；按 boss 自己的口径（Caliber57x28，P90 弹匣）
    # 换成 5.7x28 弹药，数量不变。
    ('boss', 'items', 'Pockets', '69f07893e761ac1c200ae15b',
     '5cc80f38e4a949001152b560', '5.7x28 SS190（boss 武器口径；同槽位弹药堆）'),
    ('boss', 'items', 'Pockets', '6ac4cfbc99bdd7779608792f',
     '5cc80f53e4a949000e1ea4f8', '5.7x28 L191（同口径另一种弹；避免与上一项同键合并）'),
    # boss 安全箱里同一 id（24 发）：SS190 已在箱内 80 发，换另一种 5.7x28 避免合并。
    ('boss', 'items', 'SecuredContainer', '69f07893e761ac1c200ae15b',
     '5cc80f79e4a949033c7343b2', '5.7x28 SS198LF（同口径；SS190 已在同箱）'),
    # 小弟安全箱 407 发：小弟 7 把枪不含 5.7x28，按它的 5.56 口径换 M856A1。
    ('follower', 'items', 'SecuredContainer', '69f07893e761ac1c200ae15b',
     '59e6906286f7746c9f75e847', '5.56x45 M856A1（小弟 M4A1/M995 口径；箱内尚无此弹）'),
    # 上游 boss 语音 id：本机没有（连 WTT 的 CustomVoices 也没有）。
    # 小弟的语音是 Arena_Guard_1/2（本机 base customization），boss 取其一保持同阵营。
    ('boss', 'appearance', 'voice', '6acf6e6e35ee26cb5b0a5ab1',
     '646e46ca286188c5480a1a37', 'Arena_Guard_1（同阵营竞技场语音；本机 base customization）'),
]
UPSTREAM_MISSING = sorted({r[3] for r in REPLACEMENTS})

# 槽位 → 允许的 `_parent`（类别节点）。取自本机官方 `arenafighterevent.json` 各槽实测
# （该类别的 `_name` 见注释），运行时若类别 id 不在 items.json 则跳过该槽的类别校验。
SLOT_PARENT = {
    'Headwear': {'5a341c4086f77401f2541505'},            # Headwear
    'Earpiece': {'5645bcb74bdc2ded0b8b4578'},            # Headphones
    'FaceCover': {'5a341c4686f77469e155819e'},           # FaceCover
    'Eyewear': {'5448e5724bdc2ddf718b4568'},             # Visors
    'ArmorVest': {'5448e54d4bdc2dcc718b4568'},           # Armor
    'TacticalVest': {'5448e5284bdc2dcb718b4567'},        # Vest
    'Backpack': {'5448e53e4bdc2d60728b4567'},            # Backpack
    'Holster': {'5447b5cf4bdc2d65278b4567'},             # Pistol
    'Scabbard': {'5447e1d04bdc2dff2f8b4567'},            # Knife
    'Pockets': {'557596e64bdc2dc2118b4571'},             # Pockets
    'SecuredContainer': {'5448bf274bdc2dfc2f8b456a'},    # MobContainer
}
WEAPON_SLOTS = {'FirstPrimaryWeapon', 'SecondPrimaryWeapon'}
CONTAINER_SLOTS = {'Pockets', 'Backpack', 'TacticalVest', 'SecuredContainer'}
AMMO_SLOT_PREFIX = ('mod_magazine', 'patron_in_weapon', 'cartridges')

HEX24 = set('0123456789abcdef')
CUSTOM_DIRS_ITEM = ('customitems', 'customquestitems')
CUSTOM_DIRS_CUST = ('customclothing', 'customcustomization', 'customheads', 'customvoices')


def is_id(s):
    return isinstance(s, str) and len(s) == 24 and all(c in HEX24 for c in s.lower())


def load_json(path):
    with open(path, encoding='utf-8-sig') as f:
        return json.load(f)


def deep_merge(dst, src):
    for k, v in src.items():
        if isinstance(v, dict) and isinstance(dst.get(k), dict):
            deep_merge(dst[k], v)
        else:
            dst[k] = v
    return dst


# ───────────────────────────── 本机 id 宇宙 ─────────────────────────────
class Universe:
    """base items.json + base customization.json + 各 mod 的注入定义 + 本 mod 任务线注入物品。"""

    def __init__(self, game, mod, log):
        self.game = game
        self.mod = mod
        self.log = log
        tpl = os.path.join(game, 'SPT_Data', 'database', 'templates')
        self.base = load_json(os.path.join(tpl, 'items.json'))
        self.base_cust = load_json(os.path.join(tpl, 'customization.json'))
        # id -> {'kind','src','clone','parent','overrides','name'}
        self.defs = {}
        for i, t in self.base.items():
            self.defs[i] = {'kind': 'item', 'src': 'templates/items.json', 'clone': None,
                            'parent': t.get('_parent'), 'overrides': None,
                            'name': (t.get('_props') or {}).get('Name') or t.get('_name')}
        for i, c in self.base_cust.items():
            self.defs[i] = {'kind': 'customization', 'src': 'templates/customization.json',
                            'clone': None, 'parent': None, 'overrides': None,
                            'name': c.get('_name')}
        self._scan_mods()
        self._scan_questlines()
        self._load_locales()
        self._eff_cache = {}

    # ---- 扫描 mods：db/CustomItems（克隆定义）与 db/CustomClothing/CustomVoices/... ----
    def _scan_mods(self):
        mods_dir = os.path.join(self.game, 'user', 'mods')
        if not os.path.isdir(mods_dir):
            self.log('⚠ 找不到 user/mods，只能按 base 数据判断（会误报）')
            return
        for mname in sorted(os.listdir(mods_dir)):
            dbdir = os.path.join(mods_dir, mname, 'db')
            if not os.path.isdir(dbdir):
                continue
            for root, _dirs, files in os.walk(dbdir):
                parts = [p.lower() for p in os.path.relpath(root, dbdir).split(os.sep)]
                is_item = any(p in CUSTOM_DIRS_ITEM for p in parts)
                is_cust = any(p in CUSTOM_DIRS_CUST for p in parts)
                if not (is_item or is_cust):
                    continue
                for fn in files:
                    if not fn.lower().endswith('.json') or '.bak' in fn.lower():
                        continue
                    path = os.path.join(root, fn)
                    rel = os.path.relpath(path, mods_dir)
                    try:
                        data = load_json(path)
                    except Exception:
                        self.log('⚠ 解析失败（跳过）: ' + rel)
                        continue
                    if is_item:
                        self._add_clone_defs(data, rel)
                    else:
                        self._add_cust_defs(data, rel)

    def _add_clone_defs(self, data, rel):
        if not isinstance(data, dict):
            return
        for i, v in data.items():
            if not is_id(i) or not isinstance(v, dict):
                continue
            clone = v.get('itemTplToClone')
            if not clone:
                continue
            self.defs[i] = {'kind': 'item', 'src': rel, 'clone': clone,
                            'parent': v.get('parentId'), 'overrides': v.get('overrideProperties') or {},
                            'name': v.get('newName') or self.defs.get(clone, {}).get('name')}

    def _add_cust_defs(self, data, rel):
        def walk(o, field=''):
            if isinstance(o, dict):
                nm = None
                locs = o.get('locales')
                if isinstance(locs, dict):
                    for lang in ('ch', 'en'):
                        v = locs.get(lang)
                        if isinstance(v, dict) and v.get('name'):
                            nm = v['name']
                            break
                for k, v in o.items():
                    if k.lower().endswith('id') and is_id(v) and 'trader' not in k.lower():
                        self.defs.setdefault(v, {'kind': 'customization', 'src': rel, 'clone': None,
                                                 'parent': None, 'overrides': None,
                                                 'name': nm or o.get('name'), 'field': field or k})
                    walk(v, k)
            elif isinstance(o, list):
                for v in o:
                    walk(v, field)
        walk(data)

    # ---- 本 mod 的 questlines/*.json `items` 段（QuestLineDimension 注入 TemplateTable.Items）----
    def _scan_questlines(self):
        qdir = os.path.join(self.mod, 'questlines')
        if not os.path.isdir(qdir):
            return
        for fn in sorted(os.listdir(qdir)):
            if not fn.lower().endswith('.json'):
                continue
            path = os.path.join(qdir, fn)
            try:
                data = load_json(path)
            except Exception:
                self.log('⚠ 解析失败（跳过）: questlines/' + fn)
                continue
            items = data.get('items') if isinstance(data, dict) else None
            if not isinstance(items, dict):
                continue
            for i, t in items.items():
                if not is_id(i):
                    continue
                self.defs[i] = {'kind': 'item', 'src': 'questlines/' + fn, 'clone': None,
                                'parent': (t or {}).get('_parent'), 'overrides': None,
                                'name': ((t or {}).get('_props') or {}).get('Name')}

    # ---- 本地化名（items.json 里 5.7x28 的 `_props.Name` 全是 patron_556x45_M855 的脏数据，
    #      所以审计表一律优先用 locales/global/ch.json 的 "<id> Name"）----
    def _load_locales(self):
        self.loc = {}
        base = os.path.join(self.game, 'SPT_Data', 'database', 'locales', 'global')
        for lang in ('en', 'ch'):                 # ch 后加载 ⇒ 覆盖 en
            p = os.path.join(base, lang + '.json')
            if not os.path.exists(p):
                continue
            try:
                for k, v in load_json(p).items():
                    if k.endswith(' Name') or k.endswith(' ShortName'):
                        self.loc[k] = v
            except Exception:
                pass
        mods_dir = os.path.join(self.game, 'user', 'mods')
        if not os.path.isdir(mods_dir):
            return
        added = 0
        for mname in sorted(os.listdir(mods_dir)):
            d = os.path.join(mods_dir, mname, 'db', 'CustomLocales')
            if not os.path.isdir(d):
                continue
            for fn in ('en.json', 'ch.json'):
                p = os.path.join(d, fn)
                if not os.path.exists(p):
                    continue
                try:
                    data = load_json(p)
                except Exception:
                    continue
                if not isinstance(data, dict):
                    continue
                for k, v in data.items():
                    if (k.endswith(' Name') or k.endswith(' ShortName')) and isinstance(v, str):
                        self.loc.setdefault(k, v)      # 不覆盖 base
                        added += 1
        if added:
            self.log('  本地化补充 %d 条（各 mod db/CustomLocales 的 en/ch）' % added)

    # ---- 有效模板（克隆项 = 被克隆模板 + overrideProperties 覆盖）----
    def template(self, tpl):
        if tpl in self._eff_cache:
            return self._eff_cache[tpl]
        d = self.defs.get(tpl)
        out = None
        if d:
            if d['clone']:
                src = self.template(d['clone'])
                if src is not None:
                    out = json.loads(json.dumps(src))          # 深拷贝
                    out['_id'] = tpl
                    out['_parent'] = d.get('parent') or out.get('_parent')
                    out.setdefault('_props', {})
                    deep_merge(out['_props'], d.get('overrides') or {})
                    if d.get('name'):
                        out['_name'] = d['name']
                else:
                    # 克隆源也不在 => 只有 override 里的信息，仍可判断部分字段
                    out = {'_id': tpl, '_parent': d.get('parent') or '',
                           '_props': json.loads(json.dumps(d.get('overrides') or {}))}
            elif d['kind'] == 'item':
                out = self.base.get(tpl)
            else:
                out = self.base_cust.get(tpl)
        self._eff_cache[tpl] = out
        return out

    def parent_of(self, tpl):
        t = self.template(tpl)
        return (t or {}).get('_parent')

    def resolves(self, tpl):
        return tpl in self.defs

    def name_of(self, tpl):
        t = self.template(tpl) or {}
        p = (t.get('_props') or {})
        return (self.loc.get(tpl + ' Name')
                or self.loc.get(tpl + ' ShortName')
                or p.get('Name')
                or t.get('_name')
                or (self.defs.get(tpl) or {}).get('name')
                or '')

    def source_of(self, tpl):
        return (self.defs.get(tpl) or {}).get('src', '<none>')

    def is_weapon(self, tpl):
        p = (self.template(tpl) or {}).get('_props') or {}
        return bool(p.get('Chambers') is not None or p.get('weapClass'))


# ───────────────────────────── 校验 ─────────────────────────────
class Checker:
    def __init__(self, u, log):
        self.u = u
        self.log = log
        self.fatal = []
        self.warn = []
        self.refs = Counter()
        self.slot_table = {}

    def fatal_(self, bot, what, detail=''):
        self.fatal.append((bot, what, detail))

    def warn_(self, bot, what, detail=''):
        self.warn.append((bot, what, detail))

    # ---- 1) 引用解析 ----
    def check_refs(self, bot, data, verbose=False):
        inv = data['inventory']
        pools = []                                        # (类别, 槽位, id)
        for slot, m in inv['equipment'].items():
            pools += [('equipment', slot, i) for i in m]
        for slot, m in inv['items'].items():
            pools += [('items', slot, i) for i in m]
        for cal, m in (inv.get('Ammo') or {}).items():
            pools += [('ammo', cal, i) for i in m]
        for parent, slots in (inv.get('mods') or {}).items():
            pools.append(('mods', '(base)', parent))
            for slot, lst in slots.items():
                pools += [('mods', slot, i) for i in lst]
        for slot, m in data['appearance'].items():
            pools += [('appearance', slot, i) for i in m]

        uniq = OrderedDict()
        for kind, slot, i in pools:
            uniq.setdefault(i, []).append('%s.%s' % (kind, slot))
        srcs = Counter()
        for i in uniq:
            if not self.u.resolves(i):
                self.fatal_(bot, '引用无法解析', '%s  被引用于 %s' % (i, ','.join(uniq[i][:4])))
            else:
                s = self.u.source_of(i)
                key = ('mod ' + s.split(os.sep)[0]) if s.startswith(('WTT', 'ISB', 'Manimal', 'CSGas', 'Croupier', 'BlackDiv')) else s
                srcs[key] += 1
                if verbose:
                    self.log('  %-24s %s  <- %s  [%s]' % ('/'.join(uniq[i][:3]), i, self.u.name_of(i), s))
        self.refs[bot] = {'unique': len(uniq), 'sources': srcs}
        return uniq

    # ---- 2) 装备槽类别 + 3) 容器 Grids ----
    def check_equipment(self, bot, data):
        inv = data['inventory']
        for slot, m in inv['equipment'].items():
            want = SLOT_PARENT.get(slot)
            if want:
                # 只校验实际存在的类别 id，避免把没见过的类别误判成错
                want = {w for w in want if w in self.u.base}
            for tpl in m:
                if not self.u.resolves(tpl):
                    continue
                t = self.u.template(tpl) or {}
                props = t.get('_props') or {}
                if slot in WEAPON_SLOTS:
                    if not self.u.is_weapon(tpl):
                        self.fatal_(bot, '装备槽类别不符', '%s 放进 %s，但不是武器' % (tpl, slot))
                    continue
                if slot in CONTAINER_SLOTS:
                    grids = props.get('Grids')
                    if not grids:
                        self.fatal_(bot, '容器槽物品无 Grids',
                                    '%s(%s) 在 %s 槽 ⇒ SPT ContainerDetails 会 NRE' % (tpl, self.u.name_of(tpl), slot))
                if slot in SLOT_PARENT and want:
                    par = t.get('_parent')
                    if par not in want:
                        self.fatal_(bot, '装备槽类别不符',
                                    '%s(%s) 的 _parent=%s，%s 槽要求 %s' %
                                    (tpl, self.u.name_of(tpl), par, slot, '|'.join(sorted(want))))
        # 容器槽（Pockets/TacticalVest/Backpack/SecuredContainer）必须给出可解析的容器本体
        for slot in ('Pockets', 'TacticalVest', 'SecuredContainer'):
            chance = (data['chances']['equipment'] or {}).get(slot, 0)
            pool = inv['equipment'].get(slot) or {}
            if chance and chance > 0 and not pool:
                self.fatal_(bot, '槽位概率>0 但池为空', '%s chance=%s' % (slot, chance))

    # ---- 3b) 装备（头盔/护甲/弹挂）自己的必填配件槽 ----
    # 复刻 BotEquipmentModGenerator.GenerateModsForEquipment：
    #   池子里**一条都没有** 且槽是 `_required` ⇒ `mongoId` 保持 null ⇒ 第 180 行
    #   `itemHelper.GetItem(mongoId.Value)` 对 null 取 .Value ⇒ InvalidOperationException
    #   ⇒ `Failed to generate bot`。这是"把缺失 id 删掉"最典型的炸法（护甲软甲/头盔耳罩/护裆）。
    def check_equipment_mods(self, bot, data):
        inv = data['inventory']
        mods = inv.get('mods') or {}
        chances = data['chances'].get('equipmentMods') or {}
        for slot, m in inv['equipment'].items():
            for tpl in m:
                if not self.u.resolves(tpl):
                    continue
                self._walk_equip_slots(bot, slot, tpl, mods, chances, 0, set())

    def _walk_equip_slots(self, bot, eslot, tpl, mods, chances, depth, seen):
        if depth > 4 or tpl in seen:
            return
        seen.add(tpl)
        t = self.u.template(tpl) or {}
        props = t.get('_props') or {}
        slots = props.get('Slots') or []
        pool = mods.get(tpl) or {}
        # `patron_in_weapon*` 不在 Slots 里而是在 Chambers 里，属于正常写法（弹药走 GetAmmoContainers 分支）
        tpl_slot_names = {(s.get('_name') or '').lower() for s in slots}
        tpl_slot_names |= {((c or {}).get('_name') or '').lower() for c in (props.get('Chambers') or [])}
        for s in slots:
            sname = s.get('_name') or ''
            required = bool(s.get('_required'))
            if not sname or not required:
                continue
            cands = pool.get(sname) or []
            resolved = [c for c in cands if self.u.resolves(c)]
            if not cands:
                fallback = [x for x in self._slot_filter(s) if self.u.resolves(x)]
                if fallback:
                    self.warn_(bot, '装备必填槽池为空（靠 filter 兜底）',
                               '%s.%s(%s) 池=[]，filter 有 %d 个可解析 id' %
                               (eslot, tpl, sname, len(fallback)))
                else:
                    self.fatal_(bot, '装备必填槽池为空且无兜底',
                                '%s.%s(%s) 的必填槽 %s：池=[]、filter 也无 ⇒ '
                                'SPT GenerateModsForEquipment 会用 null 取 .Value → InvalidOperationException'
                                % (eslot, tpl, self.u.name_of(tpl), sname))
            elif not resolved:
                self.warn_(bot, '装备必填槽候选全缺失（退化到 filter）',
                           '%s.%s(%s).%s 池=%s' % (eslot, tpl, self.u.name_of(tpl), sname, cands))
            for c in resolved:
                self._walk_equip_slots(bot, eslot, c, mods, chances, depth + 1, seen)
        for k in pool:
            if k.lower() not in tpl_slot_names:
                self.warn_(bot, '装备 mods 池里有模板不存在的槽（SPT 只记 error，不崩）',
                           '%s(%s) 池槽 %s' % (tpl, self.u.name_of(tpl), k))

    # ---- 2b) 用户点名的 10 个装备槽：非空 / 或「仓库 chance=0 的有意空槽」----
    NAMED_SLOTS = ('FirstPrimaryWeapon', 'ArmorVest', 'Headwear', 'TacticalVest', 'Backpack',
                   'Earpiece', 'FaceCover', 'Eyewear', 'Holster', 'Scabbard')

    def check_named_slots(self, bot, data):
        inv = data['inventory']
        chances = data['chances']['equipment'] or {}
        rows = []
        for slot in self.NAMED_SLOTS:
            pool = inv['equipment'].get(slot) or {}
            chance = chances.get(slot, 0)
            if pool:
                rows.append((slot, '非空 %d 项' % len(pool), ''))
            elif not chance:
                rows.append((slot, '空', '仓库 chances=0 ⇒ 有意不刷该槽（不是缺件）'))
            else:
                rows.append((slot, '空', '★ chance=%s 但池为空' % chance))
                self.fatal_(bot, '概率>0 的装备槽为空', '%s chance=%s' % (slot, chance))
        self.slot_table[bot] = rows

    # ---- 4) 外观 body 必须在 customization 表里 ----
    def check_appearance(self, bot, data):
        ap = data['appearance']
        body = list((ap.get('body') or {}).keys())
        if not body:
            self.fatal_(bot, '外观 body 为空', 'SetBotAppearance 会直接索引 Customization[body]')
        for b in body:
            d = self.u.defs.get(b)
            if not d:
                self.fatal_(bot, '外观 body 不存在', '%s 不在 customization 表 ⇒ KeyNotFoundException' % b)
            elif d['kind'] != 'customization':
                self.warn_(bot, '外观 id 类型可疑', '%s 来自 %s（不是 customization）' % (b, d['src']))
        for slot in ('head', 'feet', 'hands', 'voice'):
            for i in (ap.get(slot) or {}):
                if not self.u.resolves(i):
                    self.fatal_(bot, '外观 %s 无法解析' % slot, i)
                elif self.u.defs[i]['kind'] != 'customization':
                    self.warn_(bot, '外观 id 类型可疑', '%s(%s) 来自 %s' % (slot, i, self.u.defs[i]['src']))

    # ---- 5) 武器必填槽 + 6) 口径弹药 ----
    def check_weapons(self, bot, data):
        inv = data['inventory']
        mods = inv.get('mods') or {}
        ammo = inv.get('Ammo') or {}
        equip = inv['equipment']
        for slot in ('FirstPrimaryWeapon', 'SecondPrimaryWeapon', 'Holster'):
            for tpl in (equip.get(slot) or {}):
                if not self.u.resolves(tpl):
                    continue
                t = self.u.template(tpl)
                self._check_ammo(bot, slot, tpl, t, ammo)
                self._check_required_slots(bot, slot, tpl, mods, depth=0, seen=set())

    def _check_ammo(self, bot, slot, tpl, t, ammo):
        props = (t or {}).get('_props') or {}
        caliber = props.get('Caliber') or props.get('ammoCaliber')
        if not caliber and props.get('Chambers'):
            f = (props['Chambers'][0].get('_props') or {}).get('filters') or []
            cal_ids = f[0].get('Filter') if f else None
        if not caliber:
            # 从默认弹或首个弹匣推口径：查 Ammo 里哪条口径包含有效弹药即可
            candidates = []
            for cal, pool in ammo.items():
                if any(self.u.resolves(a) for a in pool):
                    candidates.append(cal)
            if not candidates:
                self.fatal_(bot, '武器无可用口径弹药', '%s(%s) 且 Ammo 表为空' % (tpl, slot))
            return
        pool = ammo.get(caliber) or {}
        if not any(self.u.resolves(a) for a in pool):
            if not (props.get('DefAmmo') or props.get('defAmmo')):
                self.fatal_(bot, '口径无弹药且无 DefAmmo',
                            '%s 的口径 %s 在 Ammo 表里没有可解析弹药' % (tpl, caliber))
            else:
                self.warn_(bot, '口径无弹药（有 DefAmmo 兜底）', '%s -> %s' % (tpl, caliber))

    def _slot_filter(self, slot):
        # Slot._props.filters[*].Filter 的并集
        out = set()
        for f in ((slot.get('_props') or {}).get('filters') or []):
            for x in (f.get('Filter') or []):
                out.add(x.lower())
        return out

    def _check_required_slots(self, bot, wslot, parent_tpl, mods, depth, seen):
        if depth > 6 or parent_tpl in seen:
            return
        seen.add(parent_tpl)
        t = self.u.template(parent_tpl) or {}
        props = t.get('_props') or {}
        slots = props.get('Slots') or []
        pool = mods.get(parent_tpl) or {}
        for s in slots:
            sname = s.get('_name') or ''
            required = bool(s.get('_required'))
            if not sname:
                continue
            cands = pool.get(sname) or []
            allowed = self._slot_filter(s)
            if sname.startswith(AMMO_SLOT_PREFIX):
                # 弹药容器：SPT 必填（mod_magazine 走 AmmoTpl / 默认弹匣）
                valid = [c for c in cands if self.u.resolves(c)]
                if not valid:
                    if required:
                        self.warn_(bot, '弹药槽候选缺失（走默认弹匣兜底）',
                                   '%s(%s).%s 池=%s' % (parent_tpl, self.u.name_of(parent_tpl), sname, cands))
                for c in valid:
                    self._check_required_slots(bot, wslot, c, mods, depth + 1, seen)
                continue

            # SPT 的真实口径（BotEquipmentModGenerator.GetModPoolForSlot）：
            #   直接拿 bot json 的池子，**不按 slot filter 过滤**；只要模板在库里就会装上。
            #   slot filter 只在「池子空 ⇒ GetRandomModTplFromItemDb 兜底」时才被用到。
            resolved = [c for c in cands if self.u.resolves(c)]
            ok_filter = [c for c in resolved
                         if not allowed or self.u.parent_of(c) in allowed or c.lower() in allowed]
            if resolved and not ok_filter:
                self.warn_(bot, '槽 filter 不含池内候选（SPT 不校验，仍会装上；客户端可能显示异常）',
                           '%s.%s 池=%s' % (parent_tpl, sname, resolved))
            if not resolved and required:
                fallback = [x for x in allowed if self.u.resolves(x) and not self.u.is_weapon(x)]
                if not fallback:
                    self.fatal_(bot, '武器必填槽无任何候选',
                                '%s(%s) 的必填槽 %s：装备池=%s，filter 兜底也为空'
                                '（该武器若无 ItemPreset ⇒ 空武器 ⇒ Failed to generate bot）'
                                % (parent_tpl, self.u.name_of(parent_tpl), sname, cands))
                else:
                    self.warn_(bot, '必填槽池内候选全缺失，靠 DB filter 兜底（会换成官方同类别件）',
                               '%s.%s 池=%s' % (parent_tpl, sname, cands))
            for c in (ok_filter or resolved):
                self._check_required_slots(bot, wslot, c, mods, depth + 1, seen)
        # 弹匣/膛内弹：Chambers 需要口径弹药（已在 _check_ammo 里按武器口径检查）

    # ---- 汇总 ----
    def report(self, bots, uniq_by_bot):
        print('')
        print('=' * 78)
        print('汇总')
        print('=' * 78)
        total_ref = 0
        for bot in bots:
            r = self.refs[bot]
            total_ref += r['unique']
            print('  %-18s 引用 %3d 个 id（去重后）' % (bot, r['unique']))
            for src, n in sorted(r['sources'].items(), key=lambda kv: -kv[1]):
                print('      %-5d  %s' % (n, src))
        miss = [i for i in UPSTREAM_MISSING if not self.u.resolves(i)]
        print('')
        print('  缺失（上游引用、本机 base+mods 都没有）: %d' % len(miss))
        for i in miss:
            print('      - %s  %s' % (i, ','.join(sorted({r[1] + '/' + r[2] for r in REPLACEMENTS if r[3] == i}))))
        print('  替换（已换掉的引用条目）            : %d' % len(REPLACEMENTS))
        for r in REPLACEMENTS:
            print('      - %s %s/%s: %s -> %s  (%s)' % (r[0], r[1], r[2], r[3], r[4], r[5]))
        print('  新增（需从仓库搬进来的物品定义）    : 0（该仓库不带 CustomItems/CustomClothing，'
              '88 个 id 由本机 WTT-ContentBackport 提供）')
        print('')
        print('  用户点名的 10 个装备槽：')
        for bot in bots:
            print('    [%s]' % bot)
            for slot, status, note in self.slot_table.get(bot, []):
                print('      %-22s %-12s %s' % (slot, status, note))
        if self.warn:
            print('  警告 %d 条：' % len(self.warn))
            for b, w, d in self.warn:
                print('      ⚠ [%s] %s  %s' % (b, w, d))
        if self.fatal:
            print('  致命 %d 条：' % len(self.fatal))
            for b, w, d in self.fatal:
                print('      ✘ [%s] %s  %s' % (b, w, d))
        print('')
        print('  结果: %s' % ('✔ 通过（不会因装备/外观缺件导致 NRE）' if not self.fatal
                              else '✘ 未通过（共 %d 条致命项）' % len(self.fatal)))
        return 0 if not self.fatal else 1


def main(argv=None):
    ap = argparse.ArgumentParser(description='Scourge 装备离线校验')
    ap.add_argument('--game', default=DEFAULT_GAME, help='SPT_Runtime 目录（默认实机，只读）')
    ap.add_argument('--mod', default=DEFAULT_MOD, help='本 mod 目录（默认脚本上一级）')
    ap.add_argument('--verbose', action='store_true', help='逐个 id 打印出处')
    args = ap.parse_args(argv)

    print('SPT_Runtime : %s' % args.game)
    print('mod         : %s' % args.mod)
    u = Universe(args.game, args.mod, lambda m: print(m))
    print('id 宇宙     : base items %d + base customization %d + 注入/自定义定义 %d'
          % (len(u.base), len(u.base_cust),
             len([d for d in u.defs.values() if 'templates' not in d['src']])))

    chk = Checker(u, lambda m: print(m))
    bots = []
    uniq = {}
    for role in ('boss', 'follower'):
        path = os.path.join(args.mod, 'db', 'bots', 'types', role + 'Scourge.json')
        if not os.path.exists(path):
            print('✘ 找不到 %s' % path)
            return 1
        data = load_json(path)
        bots.append(role + 'Scourge')
        print('')
        print('─' * 78)
        print('█ %s   (%s)' % (role + 'Scourge', os.path.relpath(path, args.mod)))
        print('─' * 78)
        uniq[role + 'Scourge'] = chk.check_refs(role + 'Scourge', data, args.verbose)
        chk.check_equipment(role + 'Scourge', data)
        chk.check_equipment_mods(role + 'Scourge', data)
        chk.check_named_slots(role + 'Scourge', data)
        chk.check_appearance(role + 'Scourge', data)
        chk.check_weapons(role + 'Scourge', data)
        # 已替换项必须真的落到文件里、且原 id 不再出现
        raw = open(path, encoding='utf-8-sig').read()
        for botk, kind, slot, old, new, _why in REPLACEMENTS:
            if botk != role:
                continue
            container = data[kind] if kind == 'appearance' else data['inventory'][kind]
            pool = container.get(slot) or {}
            if old in raw:
                chk.fatal_(role + 'Scourge', '上游缺件仍在文件里', old)
            if new not in pool:
                chk.fatal_(role + 'Scourge', '替换目标未写入', '%s.%s -> %s 不在池里' % (kind, slot, new))
            if not u.resolves(new):
                chk.fatal_(role + 'Scourge', '替换目标本机也不存在', new)
        # 槽位非空（概率>0 的槽）
        for slot, chance in (data['chances']['equipment'] or {}).items():
            if chance and chance > 0 and not (data['inventory']['equipment'].get(slot) or {}):
                chk.fatal_(role + 'Scourge', '概率>0 的装备槽为空', '%s chance=%s' % (slot, chance))

    return chk.report(bots, uniq)


if __name__ == '__main__':
    sys.exit(main())
