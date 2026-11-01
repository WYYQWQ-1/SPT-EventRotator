# -*- coding: utf-8 -*-
"""生成「任务线全部完成 → 解锁成就」的数据文件与占位图标。

用法：python tools/gen_achievements.py
  · 读 questlines/<line>.json 拿该线全部任务 id（halloween 例外：官方 10 条感染者任务写死在 LINES 里）
  · 写 achievements/<line>.json（1 个成就 / 文件，availableForFinish = 每条任务一个 Quest 条件）
  · 写 images/achievement/<Icon>.png（256×256 透明底占位图：圆环 + 中文名）
数据来源与字段取值见 achievements/README.txt。
"""
import hashlib
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_ACH = os.path.join(ROOT, "achievements")
OUT_IMG = os.path.join(ROOT, "images", "achievement")

# 官方 2025 万圣节事件 10 条「感染者」任务（SPT 本体 quests.json 内已有，本 mod 只做汉化）。
HALLOWEEN_OFFICIAL = [
    "670404a2ea1caa8f2e0be106",  # Dont Believe Your Eyes
    "67040b6c45eaf70db10dbec6",  # Burn It Down
    "67040c5b4ac6d9c18c0ade26",  # Gloves Off
    "67040c92bf4be8a4ef041a6c",  # Darkest Hour Is Just Before Dawn
    "67190f6c1b3f4964d90d71e9",  # Global Threat
    "67190f9c7b0991dc22064766",  # Watch the Watcher
    "67190febcce4a5fdf605d4f8",  # Not a Step Back
    "67191048eddf081d340d4c6e",  # Pressured by Circumstances
    "671910d5dbd4354ac10e9784",  # Conservation Area
    "6727ef2c6015b7cc540ea754",  # Contagious Beast
]

# line, 成就英文名, 中文名, 图标名, index, rarity, 描述(zh), 描述(en)
LINES = [
    ("halloween", "Sleepless Night", "不眠之夜", "ER_Halloween", 20001, "Rare",
     "完成 2025 万圣节事件的全部 10 条官方任务。",
     "Complete all 10 official quests of the 2025 Halloween event."),
    ("scourge_incursion", "Scourge Quelled", "天灾平息", "ER_ScourgeIncursion", 20002, "Legendary",
     "完成「灾祸入侵」任务线的全部 7 条任务。",
     "Complete all 7 quests of the Scourge Incursion quest line."),
    ("tarkov_mystery", "The Answer", "谜底", "ER_TarkovMystery", 20003, "Legendary",
     "完成「塔科夫之迷」任务线的全部 66 条任务。",
     "Complete all 66 quests of The Tarkov Mystery quest line."),
    ("arms_race", "King of Arms", "军火之王", "ER_ArmsRace", 20004, "Rare",
     "完成「军备竞赛」任务线的全部 4 条任务。",
     "Complete all 4 quests of the Arms Race quest line."),
    ("down_the_rabbit_hole", "All the Way Down", "坠入深渊", "ER_DownTheRabbitHole", 20005, "Legendary",
     "完成「坠入兔子洞」任务线的全部 6 条任务。",
     "Complete all 6 quests of the Down the Rabbit Hole quest line."),
    ("terragroup_trail", "Trailblazer", "追迹者", "ER_TerraGroupTrail", 20006, "Rare",
     "完成「TerraGroup 足迹」任务线的全部 19 条任务。",
     "Complete all 19 quests of the TerraGroup Trail quest line."),
    ("blackout_2026", "Eyes in the Dark", "黑暗中的眼睛", "ER_Blackout2026", 20007, "Rare",
     "完成「停电」任务线的全部 5 条任务。",
     "Complete all 5 quests of the Blackout quest line."),
    ("casus_belli", "Casus Belli", "开战理由", "ER_CasusBelli", 20008, "Legendary",
     "完成「开战理由」任务线的全部 7 条任务。",
     "Complete all 7 quests of the Casus Belli quest line."),
    ("wild_wild_woods", "Woods Walker", "荒野行者", "ER_WildWildWoods", 20009, "Common",
     "完成「荒野」任务线的全部 4 条任务。",
     "Complete all 4 quests of the Wild Wild Woods quest line."),
]


def mid(*parts):
    """确定性 24 位 hex（md5 前 24 位）——同一 line 每次生成同一 id，保证幂等/可复核。"""
    return hashlib.md5(("SPT-EventRotator/achievement/" + "/".join(parts)).encode("utf-8")).hexdigest()[:24]


def quest_ids(line):
    if line == "halloween":
        return list(HALLOWEEN_OFFICIAL)
    p = os.path.join(ROOT, "questlines", line + ".json")
    with open(p, encoding="utf-8-sig") as f:
        data = json.load(f)
    return list(data["quests"].keys())


def build_one(line, en_name, zh_name, icon, index, rarity, zh_desc, en_desc, qids):
    aid = mid(line)
    conds = []
    for i, qid in enumerate(qids):
        assert len(qid) == 24 and all(c in "0123456789abcdefABCDEF" for c in qid), (line, qid)
        conds.append({
            "availableAfter": 0,
            "conditionType": "Quest",
            "dispersion": 0,
            "dynamicLocale": False,
            "globalQuestCounterId": "",
            "id": mid(line, "cond", str(i)),
            "index": i,
            "parentId": "",
            "status": [4],
            "target": qid,
            "visibilityConditions": [],
        })
    return aid, [{
        "id": aid,
        # ⚠ 名称/描述走本地化键（<aid> name / <aid> description），正文在 LocaleInject.cs。
        #   这两个字段在 SPT 的 Achievement 模型里没有对应属性 ⇒ 进 ExtensionData 原样回传，
        #   与 ISB-SpecialForces 的写法逐字一致（那边实机可用）。
        "name": aid + " name",
        "description": aid + " description",
        "imageUrl": "/files/achievement/" + icon + ".png",
        "assetPath": "",
        "rewards": [],
        "conditions": {"availableForFinish": conds, "fail": []},
        "instantComplete": False,
        "showNotificationsInGame": True,
        "showProgress": False,
        "prefab": "",
        "rarity": rarity,
        "hidden": False,
        "showConditions": False,
        "progressBarEnabled": False,
        "side": "Pmc",
        "index": index,
    }]


def png(path, text, sub):
    from PIL import Image, ImageDraw, ImageFont
    S = 256
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    # 圆环 + 内圈细线（占位图，能看出显示链路即可）
    d.ellipse([8, 8, S - 9, S - 9], outline=(214, 176, 92, 235), width=6)
    d.ellipse([24, 24, S - 25, S - 25], outline=(214, 176, 92, 90), width=2)
    # 四角装饰
    for (x0, y0, x1, y1) in [(8, 118, 8, 138), (248, 118, 248, 138), (118, 8, 138, 8), (118, 248, 138, 248)]:
        d.line([x0, y0, x1, y1], fill=(214, 176, 92, 235), width=6)
    f_big = ImageFont.truetype(r"C:\Windows\Fonts\msyhbd.ttc", 46)
    f_small = ImageFont.truetype(r"C:\Windows\Fonts\msyh.ttc", 17)

    def center(txt, font, y, fill):
        w = d.textbbox((0, 0), txt, font=font)[2]
        d.text(((S - w) / 2, y), txt, font=font, fill=fill)

    # 中文名按字拆行（每行最多 4 字）
    per = 4 if len(text) > 4 else len(text)
    rows = [text[i:i + per] for i in range(0, len(text), per)]
    base_y = 96 - (len(rows) - 1) * 28
    for i, row in enumerate(rows):
        center(row, f_big, base_y + i * 56, (245, 240, 235, 255))
    center(sub, f_small, 176, (214, 176, 92, 255))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.save(path)


def main():
    os.makedirs(OUT_ACH, exist_ok=True)
    os.makedirs(OUT_IMG, exist_ok=True)
    print("line".ljust(24), "aid".ljust(26), "conds", " icon")
    total = 0
    for line, en, zh, icon, index, rarity, zh_d, en_d in LINES:
        qids = quest_ids(line)
        aid, arr = build_one(line, en, zh, icon, index, rarity, zh_d, en_d, qids)
        p = os.path.join(OUT_ACH, line + ".json")
        with open(p, "w", encoding="utf-8", newline="\n") as f:
            json.dump(arr, f, ensure_ascii=False, indent=2)
            f.write("\n")
        png(os.path.join(OUT_IMG, icon + ".png"), zh, "EventRotator")
        total += 1
        print(line.ljust(24), aid.ljust(26), str(len(qids)).rjust(5), " " + icon + ".png")
    print("\n合计 %d 个成就，%d 条 Quest 条件" % (
        total, sum(len(quest_ids(l[0])) for l in LINES)))
    print("\n—— LocaleInject 用的 aid/名称 ——")
    for line, en, zh, icon, index, rarity, zh_d, en_d in LINES:
        print('        ("%s name", %-42s "%s"),' % (mid(line), json.dumps(en, ensure_ascii=False), zh))


if __name__ == "__main__":
    main()
