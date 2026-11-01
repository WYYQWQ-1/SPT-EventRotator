# -*- coding: utf-8 -*-
import json, io, math

LL = r'D:\PureEFTv4.1\EFTv4.1\SPT_Runtime\SPT_Data\database\locations\woods\looseLoot.json'
d = json.load(io.open(LL, encoding='utf-8-sig'))
sps = d['spawnpoints']

REG = [
    ("Batya 营地", 283.65, -443.73, 15, 25),
    ("三联房", -1.13, -74.74, 8, 10),
    ("紫卡房", -24.69, 46.58, 5, 8),
    ("配件小屋", 405.0, 232.7, 12, 20),
    ("医疗救护车", -95.5, 217.9, 6, 10),
    ("USEC营地A", -177.9, 252.5, 18, 20),
    ("USEC营地B", -193.5, 220.2, 18, 20),
    ("旧伐木场A", -500.7, -174.4, 15, 18),
    ("旧伐木场B", -543.4, -211.9, 18, 20),
    ("新村", -474.1, -380.2, 12, 25),
    ("SCAV小屋", 225.4, -705.4, 10, 18),
    ("伐木场大区", 39, -3, 70, 35),
    ("新区", -654.1, 141.5, 48, 30),
]

pts = []
for s in sps:
    t = s.get('template') or {}
    p = t.get('Position') or {}
    if t.get('IsAlwaysSpawn'):
        continue
    if str(t.get('Id', '')).lower().startswith('christmas'):
        continue
    x, z = p.get('x'), p.get('z')
    if x is None or z is None:
        continue
    pts.append((x, z))
print('eligible points:', len(pts), 'of', len(sps))

# raw containment
hits = {r[0]: [] for r in REG}
for (x, z) in pts:
    for (n, cx, cz, rad, cap) in REG:
        if abs(x - cx) <= rad and abs(z - cz) <= rad:
            hits[n].append(math.hypot(x - cx, z - cz))

claim_region = {}
for (x, z) in pts:
    cands = []
    for i, (n, cx, cz, rad, cap) in enumerate(REG):
        if abs(x - cx) <= rad and abs(z - cz) <= rad:
            cands.append((cap, rad * rad, i, n, math.hypot(x - cx, z - cz)))
    if cands:
        cands.sort()
        claim_region[(x, z)] = cands[0]

repl = {r[0]: 0 for r in REG}
by_region = {r[0]: [] for r in REG}
for key, (cap, area, i, n, dist) in claim_region.items():
    by_region[n].append(dist)
for n in by_region:
    cap = dict((r[0], r[4]) for r in REG)[n]
    by_region[n].sort()
    repl[n] = min(len(by_region[n]), cap)

print(f"{'region':<14}{'hit':>5}{'claim':>7}{'cap':>5}{'repl':>6}")
tot = 0
for (n, cx, cz, rad, cap) in REG:
    tot += repl[n]
    print(f"{n:<14}{len(hits[n]):>5}{len(by_region[n]):>7}{cap:>5}{repl[n]:>6}")
print('total repl:', tot, ' base', len(sps), ' pct %.2f%%' % (100.0 * tot / len(sps)))
