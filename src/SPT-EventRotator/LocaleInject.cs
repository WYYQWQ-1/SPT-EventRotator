using System;
using System.Collections.Generic;
using SPTarkov.Server.Core.Models.Spt.Tables;

namespace EventRotator;

/// <summary>
/// ★★★ 自定义角色**显示名**的服务端注入（`ScavRole/*` 两条根键，en + ch）。
///
/// <para><b>为什么需要</b>：客户端 bot 的显示名**不是**由 bot 类型名决定的，而是
/// `EFT.WildSpawnTypeExtension._spawnTypeSettings[enum].ScavRoleKey` 指向的**本地化键**决定的
/// （Assembly-CSharp:187363-187365 里 `arenaFighterEvent` 的键是 `"ScavRole/ArenaFighterEvent"`，
/// 它的值在 `SPT_Data/database/locales/global/*.json` 里是 Smuggler/劫掠者 —— 所以复用
/// arenaFighterEvent 的小弟会被显示成 Raider/Smuggler）。要让本 mod 的两个角色有自己的名字，
/// 就必须给它们**自己的键**并在 locale 表里补上对应文案。</para>
///
/// <para><b>为什么走 <c>LazyLoad.AddTransformer</c></b>：locale 表是
/// `LocaleTable.Global` = `Dictionary&lt;string, LazyLoad&lt;GlobalLocaleDictionary&gt;&gt;`，
/// 真正的字典是**懒加载**的；`LazyLoad.AddTransformer(Func&lt;T,T&gt;)`（LazyLoad.cs:50）是 SPT
/// 官方留给"往懒加载表里补东西"的口子（本 mod 的「比特币改名」在
/// <see cref="ExtraDimensions.ApplyRenameBitcoin"/> 里用的就是同一手法，任务文案注入
/// <see cref="QuestLineDimension"/> 也用它）。直接 `d[key] = value` 只写本次已物化的字典，
/// 后续重新加载会丢，所以必须挂 transformer。</para>
///
/// <para>⚠ 只**新增**这几条键（13790/13791 + 第 11 项的 13792/13793/13794），不读也不改任何已有词条（不碰 `ScavRole/ArenaFighterEvent`、
/// `ScavRole/Follower`、任务文案等）。⚠ 未实机验证（本环境不能启动游戏）。</para>
/// </summary>
internal static class LocaleInject
{
    public static Action<string> Warn;
    public static Action<string> Info;

    private static bool _done;

    /// <summary>要补的键 → (en, ch) 文案（与客户端 <c>BossScourgeClient</c> 的 ScavRoleKey 一一对应）。</summary>
    private static readonly (string Key, string En, string Ch)[] Entries =
    {
        ("ScavRole/ScourgeBoss",     "Scourge",          "灾祸"),
        ("ScavRole/ScourgeFollower", "Scourge's guard",   "灾祸小队护卫"),
        // ★ 2026-10-29【第 11 项 · Cleanup Crew / Pillager】
        //   键风格与本体一致（本体 arenaFighterEvent → `ScavRole/ArenaFighterEvent` = Smuggler / 劫掠者）；
        //   客户端把枚举 13792/13793 的 ScavRoleKey 指到这两个键（客户端那条线负责）。
        // ★ 2026-10-30 用户要求改名：清洁队 → 清场小队
        ("ScavRole/CleanupCrew",     "Cleanup Crew",     "清场小队"),
        ("ScavRole/Pillager",        "Pillager",         "劫掠者"),
        // ★ 第 11 项追加：寻血猎犬（契约：bloodhound / 13794 / ScavRole/Bloodhound）
        ("ScavRole/Bloodhound",      "Bloodhound",       "寻血猎犬"),
        // ★ 第 9 项（2024 Arms Race 军备竞赛）两个军火商。契约（与客户端那条线钉死）：
        //   gus     → `ScavRole/Gus`     en=Gus      ch=古斯
        //   basmach → `ScavRole/Basmach` en=Basmach  ch=巴斯马赫
        //   英文名取自 PPES `Factions/FactionCatalog.cs:35-38` 的 NameEn（逐字）。
        ("ScavRole/Gus",             "Gus",              "古斯"),
        ("ScavRole/Basmach",         "Basmach",          "巴斯马赫"),

        // ★ 2026-10-31【万圣节 2025 窗口汉化】两条官方万圣节任务（本机 ch.json 里是英文未译）
        //   Don't Believe Your Eyes（Jaeger） / Global Threat（Peacekeeper，条件=杀 5 种感染者）
        ("670404a2ea1caa8f2e0be106 name", "Don’t Believe Your Eyes", "别相信你的眼睛"),
        ("670404a2ea1caa8f2e0be106 description", "Hello. Tell me, are you feeling healthy? There's been talk of some kind of disease, either from the Lab or somewhere else. I even had a “witness” come in, clearly gone mental. \n\nHe told me about the living dead who bleed themselves and hunt people. I tell you what, I've heard many such tales before.\n\nYet the man's grayed hair and that look in his eyes... He's definitely seen something sinister. Maybe some looters mutilated a corpse for fun, or some poor bastard spilled acid on himself. \n\nIn any case, I think you should look into that story. You know how panic spreads around this place.", "你好。先问一句——你觉得自己还健康吗？最近有些关于某种病的传言，说是从实验室里出来的，也有人说不是。甚至有个“目击者”找上我，一看就精神不太正常。\n\n他跟我讲活死人，说自己割自己、还会追着人咬。说实话，这种故事我以前听过不少。\n\n可他那头灰白的头发，还有那个眼神……他肯定是见了什么邪门东西。也可能是哪个拾荒贼为了好玩糟蹋了尸体，或者哪个倒霉蛋把酸液泼自己身上了。\n\n不管怎样，我觉得你该去查查这事。你也知道这地方的恐慌传得有多快。"),
        ("670404a2ea1caa8f2e0be106 successMessageText", "I cannot believe it! So the madman was telling the truth? If it's connected to TerraGroup and their experiments, it's bad news.\n\nIt seems that someone has opened the Lab from the inside, disrupted all the security, and now anyone can get in and out of there.\n\nTake care of yourself and get ready for some serious footwork. Tarkov is in great danger.", "真不敢相信！那个疯子说的居然是真的？如果这事跟 TerraGroup 和他们的实验有关，那可不是好消息。\n\n看起来有人从内部打开了实验室，把安保全废了，现在谁都能进出。\n\n照顾好自己，准备好跑一趟远路。塔科夫现在很危险。"),
        ("67190f6c1b3f4964d90d71e9 name", "Global Threat", "全球性威胁"),
        ("67190f6c1b3f4964d90d71e9 description", "I'm getting reports from the terminal near Azure Coast, my people say there are more and more infected every day. Even if one sick person makes it into the harbor area, chaos will ensue. \n\nPeople will panic, try to use water transportation to leave. In that case, the disaster could go beyond the borders of Norvinsk region and even Russia.\n\nThe port security needs help. If you help reduce the number of infected over there, I'll make sure you get a reward from my Western partners.", "我一直在收蔚蓝海岸附近码头的报告，我的人说感染者一天比一天多。只要有一个病人混进港区，场面就会彻底失控。\n\n人们会恐慌，会想办法搭船离开。到那时候，这场灾难就不只是诺文斯克地区的事了，甚至会蔓延出俄罗斯。\n\n港口安保需要帮手。你去那边帮忙压一压感染者的数量，我保证你能从我西方的合伙人那里拿到报酬。"),
        ("67190f6c1b3f4964d90d71e9 successMessageText", "You did it! The terminal is still secure, which means we've managed to keep the disaster within Tarkov's borders. As agreed, here's the reward from my partners.", "干得漂亮！码头还守得住，说明我们总算把这场灾难压在了塔科夫境内。按约定，这是我合伙人的报酬。"),

        // ★ 2026-10-30【第 9 项汉化】2024 军备竞赛的 3 条**官方**任务
        //   本机 game ch.json 里这 18 个键**存在但值是英文**（BSG/SPT 未译）⇒ 玩家看到英文。
        //   这里按官方英文原文翻译补上中文；en 侧写回原文（逐字不变，只是确保键存在）。
        //   Import Control（Mechanic 发布，注意官方 QuestName 本身拼错成 "Import ontrol"，这里按 UI 显示用正确拼写）
        ("668bcccc167d507eb01a268b name", "Import Control", "进口管制"),
        ("668bcccc167d507eb01a268b description",
            "Hello, I'll bring you up to speed. Apparently Peacekeeper and comrade Prapor are trying to re-divide the weapon market. They're both dumping to win customers from each other. Rings a bell, doesn't it? History always repeats itself, and we are in yet another page of the book with this arms race...\n\nWell, whatever, no point in rambling now, plus this situation could prove beneficial to me. I was just about to restock my supply of imported weapons. I heard that even the Scavs at Polikhim have tuned western guns now! Can you get me some of those? I don't think anyone will mourn these bandits.",
            "你好，我先给你说说情况。看样子和平卫士和普拉波同志正打算重新瓜分军火市场，两边都在压价抢客户。听着耳熟吧？历史总在重演，我们这次又翻到了军备竞赛这一页……\n\n不过这局面说不定对我有利。我刚好要补一批进口货，听说现在连波利欣那边的 Scav 都换上改装过的西式枪械了。你能给我弄几支来吗？我想没人会为这些匪徒掉眼泪。"),
        ("668bcccc167d507eb01a268b successMessageText",
            "Hmm, that's odd... I thought Peacekeeper only sold quality product. The condition of these guns would make many people consider switching to another seller.",
            "嗯，这就不对劲了……我记得和平卫士只卖好东西。这批枪的成色，换了别人早就去找下家了。"),

        //   What's Your Evidence?（Peacekeeper）
        ("668bccf963acb16d63707043 name", "What’s Your Evidence?", "证据呢？"),
        ("668bccf963acb16d63707043 description",
            "Hello, my friend. You've likely already heard rumors about my merchandise, yes? My guess is this Russian prick just can't stand the fact that the customers are choosing me over him. Or perhaps there's a new player trying to join the competition. \n\nIn any case, this reputational damage must be dealt with immediately. Bring me some of this \"poor-quality merchandise\", and I'll look into it.",
            "你好，朋友。那些关于我货的传言你大概也听说了吧？我猜就是那个俄国佬受不了客户都跑来买我的。也可能是有人想挤进这个行当。\n\n不管哪种，名声这东西砸得趁早修。你给我弄点那些“劣质货”回来，我来看看是谁在搞鬼。"),
        ("668bccf963acb16d63707043 successMessageText",
            "What the kurwa is this... That's not my product! Well, thank you for your work anyway. Now I have several leads, but we need to figure out where these weapons came from.",
            "这他妈是什么玩意儿……这根本不是我的货！算了，还是谢谢你的辛苦。现在我手上有几条线索，但得先搞清楚这些枪是从哪流出来的。"),

        //   Caught Red-Handed（Peacekeeper）
        ("668bcd1b194be70f18427a00 name", "Caught Red-Handed", "抓个正着"),
        ("668bcd1b194be70f18427a00 description",
            "Oh yes yes my friend, you are just in time! I conducted an investigation and found the source of the faulty weapons. Turns out Prapor had nothing to do with this. The imported guns in this condition and quantity are only available to those ex-USEC bandits. It seems they've decided to join the market themselves. \n\nHowever, they can't sell their goods on their own for obvious reasons, meaning they must have accomplices... You need to set up surveillance at their base so we can find their dealers.",
            "好，正好，朋友，你来得太及时了！我查了一圈，找到这批劣质枪的来源了。跟普拉波没关系——这个成色和数量的进口货，只有那帮前 USEC 叛徒才拿得到。看来他们是打算自己下场做生意了。\n\n不过他们明面上没法自己卖货，那背后一定有人帮着分销……你去他们据点附近架好监视，我们得把这些经销商揪出来。"),
        ("668bcd1b194be70f18427a00 successMessageText",
            "Great! Now I'm gonna track down the dealers who have been discrediting me. After that, I'll contact you for your next job.",
            "很好！接下来我去收拾那些毁我名声的经销商。之后我会再找你安排下一件事。"),

        // ══════════════════════════════════════════════════════════════════════════════════
        // ★ 2026-10-31【万圣节 2025 窗口 · 全量汉化】官方 10 条感染者任务
        //   已有 2 条（670404a2 / 67190f6c）不动，只补它们的条件文案；其余 8 条名字/描述/完成语 + 条件文案全补。
        //   ⚠ 条件文案的 key 是**裸 24 位 condition id**（实测 en.json：`<condId>`，不是 `<condId> description`）。
        // ══════════════════════════════════════════════════════════════════════════════════
        //   Burn It Down（67040b6c45eaf70db10dbec6）
        ("67040b6c45eaf70db10dbec6 name", "Burn It Down", "烧光它们"),
        ("67040b6c45eaf70db10dbec6 description", "So I went to see this \"friend\" of ours. She says she needs time. That's exactly what we don't have right now! The contagion is spreading quickly, the sick have already been seen above ground.\n\nWe must forget about the old problems for a while and put all our energies into killing the scourge. Take plenty of ammunition and don't let them surround you. \n\nUntil we have a cure, our only option is to kill as many as possible.", "我去见了我们那位“朋友”。她说她需要时间——可我们现在最缺的就是时间！传染扩散得很快，地面上已经有人看到那些病人了。\n\n先把手头那些旧账放一放，把所有力气都用在清剿这群祸害上。多带点弹药，别让它们把你围住。\n\n在解药出来之前，我们唯一能做的就是尽可能多杀。"),
        ("67040b6c45eaf70db10dbec6 successMessageText", "Oh, that's you. Are you absolutely certain you're not infected? They say the first symptom is itching and fever.\n\nBut if you've killed so many of them and haven't gotten sick yourself, then I'm sure you're still able to help. You can go for now, and I'll make some lemon tea with honey. To keep my immune system strong, you know?", "哦，是你啊。你确定自己没被感染？他们说最先出现的症状是发痒和发烧。\n\n不过你杀了那么多，自己却没病倒，那我相信你还能帮上忙。先去吧，我给你泡杯加蜂蜜的柠檬茶。你知道的，得让免疫系统撑着点。"),
        ("67040b6c45eaf70db10dbec9", "Eliminate the infected on any location", "在任意地点消灭感染者"),
        //   Gloves Off（67040c5b4ac6d9c18c0ade26）
        ("67040c5b4ac6d9c18c0ade26 name", "Gloves Off", "动真格的"),
        ("67040c5b4ac6d9c18c0ade26 description", "So, any progress on creating a cure for the infection? I hope Therapist is truly engaged in this and not just looking for a good buyer for this diary.\n\nWe still can't cure Tarkov once and for all, so let's at least clean it up while we can. I've heard that the easiest way to kill these creatures is to shoot them in the head. In that sense, it's still the same as with humans. I'd take something more powerful to make sure their faces get shredded for sure. Right, take a shotgun with you!\n\nAlright, time for work. Get in the fight and help this city. Oh, and grab some of my honey tea to help your body fight off the sickness.", "解药那边有进展了吗？我希望医生是真的在管这事，而不是在给这本日记找什么好买家。\n\n塔科夫这病我们是没法一下根治了，那至少趁还能动手的时候把这里清干净。我听说杀这些东西最省事的办法就是打头——这点倒是和人一样。不过保险起见，带点更猛的家伙，确保能把它们的脸打烂。对，带把霰弹枪去！\n\n行了，该干活了。上阵吧，帮帮这座城市。哦，再带点我的蜂蜜茶，让身子顶得住病。"),
        ("67040c5b4ac6d9c18c0ade26 successMessageText", "Well done, kid! We're short of manpower right now, but together we're at least buying time to create a cure. \n\nAnd if we lose, at least we'll leave it cleaner. Just have to do what has to be done.", "干得好，小子！现在人手紧，但至少我们一起为解药争取了时间。\n\n就算最后输了，好歹也能留个干净点的地方。该做的，总得有人做。"),
        ("67040c5b4ac6d9c18c0ade29", "Eliminate the infected with a headshot while using a shotgun on any location", "在任意地点使用霰弹枪爆头消灭感染者"),
        //   Darkest Hour Is Just Before Dawn（67040c92bf4be8a4ef041a6c）
        ("67040c92bf4be8a4ef041a6c name", "Darkest Hour Is Just Before Dawn", "黎明前最黑暗"),
        ("67040c92bf4be8a4ef041a6c description", "You doing alright? Even though I can hardly stand on my feet, I went to see my mushroom picker friend and give him some of my tea. \n\nAnd you know what, the old man's keeping strong and healthy! That means we can't get discouraged either. The infected are everywhere now, but there are survivors too.\n\nSo, it's time to roll up our sleeves and do some dirty work. Go to every corner of Tarkov and eradicate the monsters - we'll do our best to save the few who haven't been infected yet.", "你还好吗？我自己都快站不住了，还是去看了我那采蘑菇的老朋友，给他送了点茶。\n\n你猜怎么着——那老头身子骨还硬朗着呢！所以我们更不能泄气。现在到处都是感染者，可也还有活下来的人。\n\n那就卷起袖子干点脏活吧。跑遍塔科夫的每一个角落，把这些怪物清掉——还没被感染的那几个人，我们尽量保住。"),
        ("67040c92bf4be8a4ef041a6c successMessageText", "Glad you're still with us. It's getting better, even if it's only for a short while. \n\nWe can take a short break and regain our strength for now, as long as it's calm. Hopefully the medic hag will give us some results soon.", "很高兴你还活着。情况在好转，哪怕只是暂时的。\n\n只要还平静，我们就趁机歇口气、回回力气。希望那个女军医很快能拿出点成果来。"),
        ("67040c92bf4be8a4ef041a6f", "Eliminate the infected on Ground Zero", "在中心区消灭感染者"),
        ("670696ab3907c9db5b4612e9", "Eliminate the infected on Streets of Tarkov", "在塔科夫街区消灭感染者"),
        ("6706a2cccd98cc18afe9cbb0", "Eliminate the infected in The Lab", "在实验室消灭感染者"),
        //   Watch the Watcher（67190f9c7b0991dc22064766）
        ("67190f9c7b0991dc22064766 name", "Watch the Watcher", "监视监视者"),
        ("67190f9c7b0991dc22064766 description", "Greetings. I'm not sure if you have a good understanding of the balance of power in Tarkov, but currently it doesn't matter.\n\nOne of the key figures in this city is at the Lighthouse. You may not know him, but he sure knows all about you. \n\nThe person is well protected, but his guards could use some help. Due to the huge losses, I cannot provide any of my own manpower resources, but this could be a lucrative job for you.\n\nWho knows, maybe you'll get some credit for it later. Either way, this loss will cost Tarkov too much if you don't do anything about it.", "你好。我不确定你对塔科夫的势力格局了解多少，不过现在这不重要。\n\n这座城市里有个关键人物，就在灯塔。你可能不认识他，但他肯定把你的事摸得一清二楚。\n\n那人身边的防护不差，但他的卫队现在需要帮手。损失太大，我自己一个人也派不出来，不过这活对你来说应该很划算。\n\n谁知道呢，也许以后你还能因此落个人情。总之，这要是丢了，塔科夫可承受不起。"),
        ("67190f9c7b0991dc22064766 successMessageText", "I've been told that the Lighthouse area is calmer and will not be needing our support for some time. You protected an important person in a difficult time, and you deserve your reward.", "听说灯塔那边平静下来了，暂时不需要我们支援。你在难的时候护住了一个重要人物，这报酬你应得。"),
        ("67190f9c7b0991dc22064769", "Eliminate the infected on Lighthouse", "在灯塔消灭感染者"),
        //   Not a Step Back!（67190febcce4a5fdf605d4f8）
        ("67190febcce4a5fdf605d4f8 name", "Not a Step Back!", "一步不退！"),
        ("67190febcce4a5fdf605d4f8 description", "Times are hard, but you are a real warrior! Who else but you to defend our country from these monsters?\n\nI keep receiving reports of numerous enemy breakthroughs, and the least defended location is the nature reserve. You'll have to move into the area and kill as many of those freaks as you can.\n\nAnd yes, bring tons of ammo and stay on the high ground, you'll need it.\n\nAnd remember to stay at a distance, shoot them on the approach! Don't let them get too close to you.", "日子是难，可你是真正的战士！除了你，还有谁来把国家从这些怪物手里守住？\n\n我这边不断收到防线被突破的报告，最薄的地方是自然保护区。你得过去一趟，能杀多少杂种就杀多少。\n\n对了，多带弹药，尽量占高处——你会用得上的。\n\n还有，记住拉开距离，趁它们冲过来的时候打！别让它们近身。"),
        ("67190febcce4a5fdf605d4f8 successMessageText", "So many? Man, normally we'd just artillery the fuck out of them...\n\nBut you gave them a hell of a beating. If any of my soldiers managed to hide somewhere, now's the time to get out. I'm gonna go check the frequency.", "这么多？换平时我们直接一轮炮火覆盖过去了……\n\n不过你把这帮东西揍惨了。要是我还有弟兄藏在哪，现在就是脱身的时候。我去守着频率。"),
        ("67190febcce4a5fdf605d4fb", "Eliminate the infected from over 70 meters away on Reserve", "在储备站从 70 米外消灭感染者"),
        //   Pressured by Circumstances（67191048eddf081d340d4c6e）
        ("67191048eddf081d340d4c6e name", "Pressured by Circumstances", "形势所迫"),
        ("67191048eddf081d340d4c6e description", "It's tough everywhere right now, I know. Have you ever been fascinated by this human ability to adapt to any condition?\n\nI think it's what made man the pinnacle of evolution. But to feel like you're slowly getting used to the crowds of infected people in your hometown... It makes me sick.\n\nThe demand for firearms has increased tremendously, and I'm thinking that if this epidemic drags on, there will be only handguns left in the stockpiles. Can you test to see how effective they are in the current environment?", "我知道，现在哪儿都难。你有没有觉得，人这种什么环境都能适应的本事挺有意思？\n\n我猜就是这本事让人走到了进化的顶上。可一想到你正慢慢习惯家乡街头那一群群感染者……我就犯恶心。\n\n枪的需求涨得离谱。我在想，这病再拖下去，仓库里怕是只剩手枪了。你能去试试这种东西在当前环境下到底顶不顶用吗？"),
        ("67191048eddf081d340d4c6e successMessageText", "It turns out you can survive even with a weapon like this, if you have the skills. But I'm not sure there's enough shooters of your caliber in the city. \n\nI hope we don't need your proven tactics. But it's best to prepare for the worst-case scenario.", "看来只要有本事，拿这种家伙也能活下来。不过城里像你这样的枪手，我怀疑凑不出几个。\n\n希望我们不必用上你这套验证过的打法。但最坏的打算，还是得提前做。"),
        ("67191048eddf081d340d4c71", "Eliminate the infected while using pistols or revolvers on Factory", "在工厂使用手枪或左轮消灭感染者"),
        //   Conservation Area（671910d5dbd4354ac10e9784）
        ("671910d5dbd4354ac10e9784 name", "Conservation Area", "自然保护区"),
        ("671910d5dbd4354ac10e9784 description", "Hello again. Still feeling strong? Well, keep it that way. We're not going to get much rest any time soon.\n\nWhatever's going on with the treatment of this disease, there's still a lot of sick people all over the city! That's why I once again ask for your support.\n\nThe infected have been seen in our forest, and we can't let that go unnoticed. What if this virus stays in the soil? Is this what we'd leave behind?\n\nWe can't let them roam the nature reserve, we have to drive them away. Try hunting them at night and see how they behave... It might make things easier.", "又见面了。身子还硬朗吧？那就继续保持。接下来很长一段时间，我们都别想歇。\n\n这病的治疗不管有没有进展，城里还是到处都有病人！所以我又来找你帮忙了。\n\n有人在我们这片森林里看到了感染者，这事不能不管。万一病毒留在了土里呢？我们难道要留下这么个烂摊子？\n\n不能让它们在自然保护区里乱逛，得把它们赶出去。你试试夜里去猎它们，看看它们有什么习性……说不定能省点事。"),
        ("671910d5dbd4354ac10e9784 successMessageText", "So the infected are deadly at night, too? Even the fiercest beast needs rest too, I thought...\n\nAfter this, I don't know if we can truly cure them. It's getting harder and harder to see them as human beings with each passing day...", "原来感染者夜里也一样要命？我还以为再凶的野兽也得歇一歇……\n\n经了这些事，我不知道我们还能不能真正治好他们。一天天过去，越来越难再把它们当人看了……"),
        ("671910d5dbd4354ac10e9787", "Eliminate the infected on night-time Woods", "在夜间森林消灭感染者"),
        //   Contagious Beast（6727ef2c6015b7cc540ea754）
        ("6727ef2c6015b7cc540ea754 name", "Contagious Beast", "传染的野兽"),
        ("6727ef2c6015b7cc540ea754 description", "I never thought we'd have so much on our plate. Did you hear that the infected have gotten to Tagilla? He was already furious before, and now even more so. With such an active carrier of contagion, we'll soon run out of healthy people.\n\nHe must be stopped. Sure, there are tons of infected everywhere now, but none of them are as dangerous as Tagilla. So I'm asking you to put everything on hold and find the bastard. And don't leave until his body stops twitching.\n\nAfter everything that's happened, it wouldn't surprise me if Tagilla survives several magazines and then gets up as if nothing had happened... And hey, be careful out there.", "真没想到我们手上会堆这么多事。你听说了吗，塔吉拉也染上了。他本来就够疯的，现在更疯了。有这么个能到处跑的传染源，健康人很快就不够用了。\n\n必须把他拦下来。现在到处都是感染者，可没有一个像塔吉拉这么危险。所以我想请你把手头的事都放一放，去把那个混蛋找出来。他身子还在抽，你就别走。\n\n经过这么多事，就算塔吉拉挨了几个弹匣还能像没事人一样爬起来，我也不会意外……还有，在外头小心点。"),
        ("6727ef2c6015b7cc540ea754 successMessageText", "You made sure this demon is out of commission, yes? Well, let's hope we've dealt with this threat for good.\n\nConserve your ammo and check back soon, it's important we all work together right now.", "你确定那个恶魔已经趴下了吧？那就好，希望这个威胁到此为止。\n\n省着点弹药，过阵子再来找我——现在大家得拧成一股绳。"),
        ("6727f18974e5bdcd3778dc54", "Locate and neutralize infected Tagilla", "找到并消灭染病的塔吉拉"),
        ("6727f338415cfa0beb19c5be", "Survive and extract", "活着撤离"),
        //   （已有任务补条件文案：670404a2ea1caa8f2e0be106）
        ("671b9af6038f464a253ff93c", "Locate the source of rumors of the living dead in The Lab", "在实验室里找到活死人传言的源头"),
        ("671b9d4ef32cf95e65e0d54b", "Find a way inside the quarantined section in The Lab", "找到进入实验室隔离区的方法"),
        //   （已有任务补条件文案：67190f6c1b3f4964d90d71e9）
        ("67190f6c1b3f4964d90d71ec", "Eliminate the infected on Shoreline", "在海岸线消灭感染者"),
    };

    /// <summary>
    /// ★ 2026-11-01【任务线全部完成 → 解锁成就】，2026-11-01 晚按**官方命名**重构。
    ///
    /// <para>键规则与本体逐字一致：`&lt;成就id&gt; name` / `&lt;成就id&gt; description`
    /// （实测本机 ch.json 里就是 `670feb95a4e71050310cc14b name` / `… description`）。</para>
    ///
    /// <para>⚠ 成就 id **必须**与 `achievements/&lt;任务线&gt;.json` 里的 `id` 一字不差
    /// （常规 id = md5("SPT-EventRotator/achievement/&lt;key&gt;") 前 24 位；分支线用 `&lt;线&gt;/&lt;分支&gt;`）。
    /// 两侧不同步 ⇒ 游戏内显示成原始键名。</para>
    ///
    /// <para>本表 **不写** 下面 2 条的文案，因为它们复用了**官方成就 id**，本机 `locales/global/{en,ch}.json`
    /// 里官方原文就在（`6634cae870af846d2868dada` = Local Strong Man / 街头大力士，
    /// `6634ca69ee506a5c3e61be56` = INVOLVED IN PEACE / 和 平 精 英）⇒ 写一遍只会跟官方原文漂移。</para>
    /// </summary>
    private static readonly (string Key, string En, string Ch)[] AchievementEntries =
    {
        // ★ 2026-10-31【用户口径 B】万圣节 / 军备竞赛：官方那两条是计数器诱饵（10000m 外爆头 10000 个）
        //   ⇒ SPT 里点不亮；保留我们的可解锁版，与官方那条并存。
        ("d8182329bda2689d10c17a36 name", "Sleepless Night", "不眠之夜"),
        ("d8182329bda2689d10c17a36 description",
            "Complete all 10 quests of the 2024 Halloween infected quest line.",
            "完成 2024 万圣节感染者任务线的全部 10 条任务。"),
        ("4650e2d1bf4f8f5439372395 name", "Arms Dealer", "军火之王"),
        ("4650e2d1bf4f8f5439372395 description",
            "Complete the Arms Race quest line.",
            "完成军备竞赛任务线。"),
        //   scourge_incursion ← 灾厄侦探 / Scourging（官方 21 条之一；本机 locale 无此条 ⇒ 自写中英）
        ("9916813aaa06477bebd3ec68 name", "Scourging", "灾厄侦探"),
        ("9916813aaa06477bebd3ec68 description",
            "Complete the “Scourge Incursion” quest line and find out what Ref is searching for in the Lab.",
            "完成“灾厄来袭”任务线，探明裁判在实验室的搜寻目标。"),
        //   blackout_2026 ← 隐入黑暗 / Going Dark
        ("2d69a80dd65d7b6f900e74fe name", "Going Dark", "隐入黑暗"),
        ("2d69a80dd65d7b6f900e74fe description",
            "Fight the Black Division units and complete the “Blackout” event quest line.",
            "与黑色军团部队交战，并完成“大停电”活动任务线。"),
        //   wild_wild_woods ← 我即法律 / I Am the Law
        ("45a29b95a264f7c587976ace name", "I Am the Law", "我即法律"),
        ("45a29b95a264f7c587976ace description",
            "Help Jaeger restore order in the nature reserve and complete the “Wild Wild Woods” quest line.",
            "帮助 Jaeger 恢复自然保护区的秩序，并完成“狂野森林”任务线。"),
        //   casus_belli / morituri ← 赴死之人向你致敬 / Morituri te salutant（裁判线）
        ("5f0b2e8a78040e3de6b80dfc name", "Morituri te salutant", "赴死之人向你致敬"),
        ("5f0b2e8a78040e3de6b80dfc description",
            "Help Ref save the Arena’s new season launch and complete the “Casus Belli” quest line.",
            "帮助裁判挽救竞技场新赛季的启动安排，并完成“战争之因”活动任务线。"),
        //   casus_belli / habet ← 得吃！ / Habet, hoc habet!（Skier 线）
        ("6ec893c9edd3a45f1b3f200b name", "Habet, hoc habet!", "得吃！"),
        ("6ec893c9edd3a45f1b3f200b description",
            "Help Skier sabotage the Arena’s new season launch and complete the “Casus Belli” quest line.",
            "帮助 Skier 搅乱竞技场新赛季的启动安排，并完成“战争之因”活动任务线。"),
        //   down_the_rabbit_hole ← 官方**没有**这个活动的成就 ⇒ 保留自建，名字/描述按官方口径（活动名 + 「完成…任务线」）
        ("b3ca5233a09710897058ecf7 name", "Down the Rabbit Hole", "兔子洞之下"),
        ("b3ca5233a09710897058ecf7 description",
            "Complete the “Down the Rabbit Hole” quest line.",
            "完成“兔子洞”任务线。"),
        //   terragroup_trail ← 同上，官方无对应成就
        ("f13bf981563cf9294e8ffb90 name", "The TerraGroup Trail", "TerraGroup 足迹"),
        ("f13bf981563cf9294e8ffb90 description",
            "Complete the “TerraGroup Trail” quest line.",
            "完成“TerraGroup 足迹”任务线。"),
    };

    /// <summary>由 <c>QuestLinePreload.OnLoadAsync</c>（OnLoadOrder.Preload 档）调用；幂等。</summary>
    public static void Apply(LocaleTable localeTable)
    {
        if (_done) return;
        _done = true;
        try
        {
            if (localeTable?.Global == null)
            {
                Warn?.Invoke("[EventRotator][显示名] 拿不到 LocaleTable.Global ⇒ ScavRole 文案未注入（客户端会显示原版名）");
                return;
            }

            int added = 0;
            foreach (var lang in new[] { "en", "ch" })
            {
                if (!localeTable.Global.TryGetValue(lang, out var lazy) || lazy == null)
                {
                    Warn?.Invoke($"[EventRotator][显示名] locale 表里没有语种「{lang}」⇒ 该语种的 ScavRole 文案没加");
                    continue;
                }

                var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var e in Entries)
                    pairs[e.Key] = lang == "ch" ? e.Ch : e.En;

                lazy.AddTransformer(d =>
                {
                    if (d == null) return d;
                    foreach (var p in pairs) d[p.Key] = p.Value;
                    return d;
                });
                added += pairs.Count;
            }

            Info?.Invoke("[EventRotator][显示名] 已注入 " + added + " 条 ScavRole 文案（"
                       + string.Join(" / ", Array.ConvertAll(Entries, e => e.Key)) + "），en + ch");

            // ★ 任务线成就的名称 / 描述：同样的 AddTransformer 手法，单独一批（不动上面 ScavRole 那条日志）
            int achAdded = 0;
            foreach (var lang in new[] { "en", "ch" })
            {
                if (!localeTable.Global.TryGetValue(lang, out var lazy2) || lazy2 == null)
                {
                    Warn?.Invoke($"[成就] locale 表里没有语种「{lang}」⇒ 该语种的成就名称/描述没加");
                    continue;
                }

                var pairs2 = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var e in AchievementEntries)
                    pairs2[e.Key] = lang == "ch" ? e.Ch : e.En;

                lazy2.AddTransformer(d =>
                {
                    if (d == null) return d;
                    foreach (var p in pairs2) d[p.Key] = p.Value;
                    return d;
                });
                achAdded += pairs2.Count;
            }

            Info?.Invoke("[成就] 名称/描述文案已注入 " + achAdded + " 条（"
                       + (AchievementEntries.Length / 2) + " 条成就 × 名称+描述 × en+ch；"
                       + "另 2 条（街头大力士 / 和平精英）复用官方 locale 未覆盖）");
        }
        catch (Exception ex)
        {
            Warn?.Invoke("[EventRotator][显示名] ScavRole 文案注入失败（已忽略）：" + ex.Message);
        }
    }
}
