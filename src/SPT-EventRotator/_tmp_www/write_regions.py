# -*- coding: utf-8 -*-
"""把 13 个 region 写进 fix/SPT-EventRotator/events.json 的 wild_wild_woods.looseLootInject.woods.regions
   —— 纯文本插入（其余字节不动），插入前后都做 JSON 解析校验。"""
import io, json, sys, shutil, datetime

PATH = 'events.json'
REGIONS = [
    ("Batya 营地",   283.65, -443.73, 15, 25),
    ("三联房",       -1.13,  -74.74,  8,  10),
    ("紫卡房",      -24.69,   46.58,  5,   8),
    ("配件小屋",    405.0,   232.7,  12, 20),
    ("医疗救护车",  -95.5,   217.9,   6, 10),
    ("USEC营地A",  -177.9,   252.5,  18, 20),
    ("USEC营地B",  -193.5,   220.2,  18, 20),
    ("旧伐木场A",  -500.7,  -174.4,  15, 18),
    ("旧伐木场B",  -543.4,  -211.9,  18, 20),
    ("新村",       -474.1,  -380.2,  12, 25),
    ("SCAV小屋",    225.4,  -705.4,  10, 18),
    ("伐木场大区",    39,      -3,    70, 35),
    ("新区",       -654.1,   141.5,  48, 30),
]

def build():
    out = []
    for (name, cx, cz, r, cap) in REGIONS:
        out.append({
            "name": name,
            "min": {"x": round(cx - r, 2), "z": round(cz - r, 2)},
            "max": {"x": round(cx + r, 2), "z": round(cz + r, 2)},
            "valuableBoost": 1.0,
            "maxPoints": cap,
        })
    return out

def match_brace(t, open_idx):
    depth, in_str, esc = 0, False, False
    for i in range(open_idx, len(t)):
        c = t[i]
        if in_str:
            if esc: esc = False
            elif c == '\\': esc = True
            elif c == '"': in_str = False
            continue
        if c == '"': in_str = True
        elif c == '{': depth += 1
        elif c == '}':
            depth -= 1
            if depth == 0: return i
    raise RuntimeError('unbalanced')

raw = io.open(PATH, encoding='utf-8-sig').read()
bom = io.open(PATH, 'rb').read(3) == b'\xef\xbb\xbf'
json.loads(raw)

ev_i = raw.find('"wild_wild_woods"')
lli = raw.find('"looseLootInject"', ev_i)
decor_k = raw.find('"decor"', lli)
assert ev_i > 0 and lli > 0 and decor_k > 0
decor_open = raw.find('{', decor_k)
decor_close = match_brace(raw, decor_open)

if '"regions"' in raw[lli:raw.find('"globals"', lli)]:
    print('已经存在 regions，退出（幂等）'); sys.exit(0)

txt = json.dumps(build(), ensure_ascii=False, indent=2)
block = ',\n        "regions": ' + txt.replace('\n', '\n        ')

new_raw = raw[:decor_close + 1] + block + raw[decor_close + 1:]
after = json.loads(new_raw)   # 解析校验

ev = [e for e in after if e.get('id') == 'wild_wild_woods'][0]
regs = ev['looseLootInject']['woods']['regions']
assert len(regs) == 13, len(regs)
for (name, cx, cz, r, cap), g in zip(REGIONS, regs):
    assert g['name'] == name and g['maxPoints'] == cap
    assert abs((g['min']['x'] + g['max']['x']) / 2 - cx) < 1e-6
    assert abs((g['min']['z'] + g['max']['z']) / 2 - cz) < 1e-6
    assert abs((g['max']['x'] - g['min']['x']) / 2 - r) < 1e-6
    assert g['valuableBoost'] == 1.0

stamp = datetime.datetime.now().strftime('%Y%m%d_%H%M%S')
shutil.copyfile(PATH, PATH + '.bak_before_regions_' + stamp)
io.open(PATH, 'w', encoding='utf-8-sig' if bom else 'utf-8', newline='').write(new_raw)
print('OK 写入 13 个 region；备份 =', PATH + '.bak_before_regions_' + stamp)
print('旧长度', len(raw), '→ 新长度', len(new_raw))
