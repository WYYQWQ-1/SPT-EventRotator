# -*- coding: utf-8 -*-
"""把 events.json 里 wild_wild_woods 的 containerLootInject.woods.categories 扩成 12 类。
只替换「containerLootInject」这个 JSON 值，其余字节原样保留（含 BOM / 2 空格缩进）。"""
import json, re, shutil, sys, time

PATH = "events.json"
raw = open(PATH, encoding="utf-8-sig").read()
bom = open(PATH, "rb").read(3) == b"\xef\xbb\xbf"


def find_value_end(s, start):
    """s[start] == '{'；返回匹配的 '}' 下标（跳过字符串与转义）。"""
    assert s[start] == "{"
    d = 0
    k = start
    while k < len(s):
        c = s[k]
        if c == '"':
            k += 1
            while k < len(s):
                if s[k] == "\\":
                    k += 2
                    continue
                if s[k] == '"':
                    break
                k += 1
        elif c == "{":
            d += 1
        elif c == "}":
            d -= 1
            if d == 0:
                return k
        k += 1
    raise RuntimeError("unbalanced")


key_at = raw.index('"containerLootInject"')
brace = raw.index("{", key_at)
end = find_value_end(raw, brace)
old_text = raw[brace:end + 1]

d = json.loads(old_text)
woods = d["woods"]
cats = woods["categories"]
print("现有类别：", list(cats.keys()))

PLAN = json.load(open(r"C:\Users\32674\OneDrive\Documents\deepseek-harness\default-workspace\_tmp_containercover\plan.json", encoding="utf-8"))
for k, cfg in PLAN.items():
    if k in cats:
        print("⚠ 已存在，跳过：" + k)
        continue
    cat = {"containers": cfg["containers"], "items": cfg["items"]}
    if cfg.get("stacks"):
        cat["stacks"] = cfg["stacks"]
    cats[k] = cat
print("新类别顺序：", list(cats.keys()))

# ── 手写序列化，保持原文件的缩进风格 ──
I = "  "


def dump_cat(name, cat, ind):
    out = []
    out.append('%s"%s": {' % (ind, name))
    out.append('%s"containers": [' % (ind + I))
    cs = cat["containers"]
    for i, c in enumerate(cs):
        out.append('%s"%s"%s' % (ind + I + I, c, "," if i < len(cs) - 1 else ""))
    out.append("%s]," % (ind + I))
    out.append('%s"items": {' % (ind + I))
    its = list(cat["items"].items())
    for i, (t, w) in enumerate(its):
        out.append('%s"%s": %d%s' % (ind + I + I, t, w, "," if i < len(its) - 1 else ""))
    has_stacks = bool(cat.get("stacks"))
    out.append("%s}%s" % (ind + I, "," if has_stacks else ""))
    if has_stacks:
        out.append('%s"stacks": {' % (ind + I))
        st = list(cat["stacks"].items())
        for i, (t, n) in enumerate(st):
            out.append('%s"%s": %d%s' % (ind + I + I, t, n, "," if i < len(st) - 1 else ""))
        out.append("%s}" % (ind + I))
    out.append("%s}" % ind)
    return out


lines = ['{']
lines.append('  "woods": {')
chance = woods.get("chance", 1.0)
chance_s = ("%.1f" % chance) if isinstance(chance, float) else str(chance)
lines.append('    "chance": %s,' % chance_s)
lines.append('    "categories": {')
cl = list(cats.items())
for i, (name, cat) in enumerate(cl):
    block = dump_cat(name, cat, "      ")
    if i < len(cl) - 1:
        block[-1] = block[-1] + ","
    lines.extend(block)
lines.append('    }')
lines.append('  }')
lines.append('}')

# 缩进：原文件里 containerLootInject 这行的前缀是 4 空格；第一行"{"" 跟着 key 一起，后续行加前缀
prefix = raw[:key_at]
indent = re.search(r"( *)$", prefix).group(1)
new_text = lines[0] + "".join("\n" + indent + l for l in lines[1:])

# 逐行查看
print("\n--- 新块预览（前 20 行 / 后 6 行）---")
prev = new_text.splitlines()
print("\n".join(prev[:20]))
print("...")
print("\n".join(prev[-6:]))
print("行数：", len(prev))

# 校验：能反序列化，且 categories 数量正确
chk = json.loads(new_text)
assert chk["woods"]["categories"].keys() == d["woods"]["categories"].keys(), "keys mismatch"

if "--apply" not in sys.argv:
    print("\n（dry-run；加 --apply 才写盘）")
    sys.exit(0)

bak = PATH + ".bak_before_whitelist_v3_" + time.strftime("%Y%m%d_%H%M%S")
shutil.copy2(PATH, bak)
out = raw[:brace] + new_text + raw[end + 1:]
open(PATH, "w", encoding="utf-8-sig" if bom else "utf-8").write(out)
print("\n已写盘；备份：", bak)
