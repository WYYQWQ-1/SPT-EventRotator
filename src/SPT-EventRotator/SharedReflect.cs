using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace EventRotator;

/// <summary>
/// ★ task-34（⑧⑨ 三维度）新增：**共用反射工具**。
///
/// 为什么单独开一个文件：本文件是**纯新增**，不碰 `ExtraDimensions.cs`（他人正在改）。
/// 三个新维度（藏身处需求 / 松散战利品权重 / 商人 assort 追加）都用这里的方法，
/// 避免各自抄一份、各自踩同一个坑。
///
/// ★★ 本项目最致命的通用坑型（多次踩过）：**JSON 名 ≠ CLR 属性名**。
///   · `BarterScheme.Template` 的 JSON 名是 `_tpl`
///   · `TraderAssort.LoyalLevelItems` 的 JSON 名是 `loyal_level_items`
///   · `Item.Template` 的 JSON 名是 `_tpl`
///   ⇒ 所以这里**全部走大小写不敏感**查找（`IgnoreCase`），且属性找不到时**回落到字段**。
/// </summary>
internal static class R
{
    public const BindingFlags Any =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;

    // ═══════════════════════════════════════════════════════════════════════════
    // ★★★ v1.0.1【性能】成员查找缓存 —— **语义零变化**，只把 `GetProperty`/`GetField` 缓存起来
    //
    //   实测痛点：`LooseLootWeightDimension` 每个刷点要调 3~6 次 `Prop`（`:105/143/144/148/161-163`）
    //   ⇒ 1768 刷点 × 12 tpl ≈ **1~3 万次未缓存反射/局**（`softcore` 活动）✗
    //   `Any` 是最慢的 flag 组合（含 `NonPublic|Static|IgnoreCase`）。
    //
    //   ⚠ **为什么是"两个缓存"而不是"一个成员缓存"**（本改动唯一容易踩错的地方）：
    //     原实现是 **属性优先、属性 getter 抛异常时继续回落到字段**：
    //       GetProperty → (有且非索引器) → GetValue ──抛异常──▶ 继续 GetField → GetValue
    //     若改成"只查一次成员、命中属性就不再试字段"，**属性抛异常时就不再回落字段** ⇒ **行为变了** ✗
    //     ⇒ 所以**属性**与**字段**各一个缓存，查找顺序与回落逻辑**逐字保留** ✔
    //
    //   ⚠ **键只需 `(Type, name)`**：`Any` 是 **const**（全库唯一 flag 组合）
    //     ⇒ 不存在"同一 Type 多种 flag 组合"的情形 ⇒ 无需加 flag 维 ✔
    //   ⚠ **命中 null 也要缓存**：`ConcurrentDictionary` 不允许 null value ⇒ 用哨兵 `NoMember` ✔
    //     （键空间实测极小：`name` 全字面量 ~20 个 × ~10 Type ⇒ ≤200 组，不会膨胀 ✔）
    //   ⚠ **异常行为不变**：查找期异常照旧吞掉；getter/setter 异常照旧吞掉 ✔
    // ═══════════════════════════════════════════════════════════════════════════
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), object> _propCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), object> _fieldCache = new();
    private static readonly object _NoMember = new();   // 哨兵：表示"该 Type+name 没有这个成员"

    // ═══════════════════════════════════════════════════════════════════════════
    // ★★★ v1.0.1【性能·第五轮】带 flags 的成员查找缓存
    //
    //   背景：全库有 **30 份**私有的 `private static object Prop(object, string)` 拷贝
    //   （AirdropDimension / BotEquipmentDimension / ItemsDimension / ExtraDimensions /
    //     GlobalsDimension / HideoutTraderDimension / LabsFleaDimension / MapGateDimension /
    //     PbsPoolWriter / PriceDimension / TollDimension / TransitDimension /
    //     WeatherSeasonDimension / BossWavePatch / QuestAcceptGatePatch / QuestListFilterPatch …）
    //   其中约 18 份**完全没缓存**（每次裸 `GetProperty` + `GetField`，且多数带 `IgnoreCase` =
    //   最慢的 flag 组合）。四轮审计只覆盖了 `R.Prop` 这一份，其余都被漏掉。
    //
    //   这里提供一个**按 (Type, name, flags) 键**的缓存，语义与各处的原实现逐字一致：
    //     · 先查属性；属性**存在**就只用它（getter 抛异常 ⇒ 吞掉并返回 null，**不回落字段**）
    //     · 属性不存在 ⇒ 再查字段（getter 抛异常 ⇒ 吞掉并返回 null）
    //   ⚠ 键必须带 flags：各文件的 flags 不一致（`Instance` / `+Public` / `+NonPublic` /
    //     `+IgnoreCase` 各有组合）⇒ 不能共用 `_propCache`/`_fieldCache` 那一对。
    //   ⚠ 命不中也缓存（哨兵 `_NoMember`），键空间 = 字面量 name × 少量 Type × 少量 flags 组合。
    // ═══════════════════════════════════════════════════════════════════════════
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string, int), (object, object)> _flagCache = new();

    // ═══════════════════════════════════════════════════════════════════════════
    // ★★★ v1.0.1【性能·第六轮】三个「带缓存」的元数据访问器
    //
    //   剩余各维度（Airdrop / BotEquipment / Globals / ExtraDimensions / HideoutTrader /
    //   LabsFlea / Price / Toll / EventScavSuppress）各有一份私有的 `Prop()`，形态不一但
    //   共同点是：**每次调用都重新 `GetProperty` / `GetField`**，其中一部分还要
    //   `GetFields(Instance|NonPublic)` 后线性扫描 —— 而 `GetFields` **每次都会分配一个新数组**。
    //
    //   这里提供三个按 `(Type, name, flags)` 缓存的访问器，让各处可以做**纯机械替换**，
    //   原有控制流（几段查找、什么顺序、失败后干什么）**一个字都不用改**。
    // ═══════════════════════════════════════════════════════════════════════════
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string, int), object> _gCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, int), FieldInfo[]> _gfsCache = new();

    /// <summary>★ 缓存版 <c>Type.GetProperty(name, flags)</c>（找不到也缓存）。</summary>
    public static PropertyInfo GP(Type t, string n, BindingFlags flags)
    {
        if (t == null || string.IsNullOrEmpty(n)) return null;
        var key = (t, n, (int)flags);
        if (_gCache.TryGetValue(key, out var hit)) return ReferenceEquals(hit, _NoMember) ? null : (PropertyInfo)hit;
        PropertyInfo found = null;
        try { found = t.GetProperty(n, flags); } catch { }
        _gCache[key] = (object)found ?? _NoMember;
        return found;
    }

    /// <summary>★ 缓存版 <c>Type.GetField(name, flags)</c>（找不到也缓存）。</summary>
    public static FieldInfo GF(Type t, string n, BindingFlags flags)
    {
        if (t == null || string.IsNullOrEmpty(n)) return null;
        var key = (t, n, (int)flags ^ 0x40000000);   // 与属性键错开（异或一个高位标志）
        if (_gCache.TryGetValue(key, out var hit)) return ReferenceEquals(hit, _NoMember) ? null : (FieldInfo)hit;
        FieldInfo found = null;
        try { found = t.GetField(n, flags); } catch { }
        _gCache[key] = (object)found ?? _NoMember;
        return found;
    }

    /// <summary>★ 缓存版 <c>Type.GetFields(flags)</c> —— 原来每次调用都会**分配一个新数组**。</summary>
    public static FieldInfo[] GFs(Type t, BindingFlags flags)
    {
        if (t == null) return Array.Empty<FieldInfo>();
        var key = (t, (int)flags);
        if (_gfsCache.TryGetValue(key, out var hit)) return hit;
        FieldInfo[] found;
        try { found = t.GetFields(flags); } catch { found = Array.Empty<FieldInfo>(); }
        _gfsCache[key] = found;
        return found;
    }

    /// <summary>★ v1.0.1【性能】按 flags 读实例成员：属性优先（存在则只用属性），否则回落字段。
    /// 与各维度里原有的私有 `Prop()` **语义逐字相同**，只是把两次成员查找缓存起来。</summary>
    public static object PropWithFlags(object o, string name, BindingFlags flags)
    {
        if (o == null || string.IsNullOrEmpty(name)) return null;
        var t = o.GetType();
        var key = (t, name, (int)flags);
        object pi, fi;
        if (_flagCache.TryGetValue(key, out var hit))
        {
            (pi, fi) = hit;
        }
        else
        {
            pi = _NoMember; fi = _NoMember;
            try { var p = t.GetProperty(name, flags); if (p != null) pi = p; } catch { }
            try { var f = t.GetField(name, flags); if (f != null) fi = f; } catch { }
            _flagCache[key] = (pi, fi);
        }
        // ★ 语义必须与各维度原来的私有 Prop() 逐字一致：
        //   属性**存在但 GetValue 抛异常** ⇒ **继续回落字段**（不是直接返回 null）。
        if (!ReferenceEquals(pi, _NoMember))
        {
            try { return ((PropertyInfo)pi).GetValue(o); } catch { }   // 抛异常 ⇒ 继续往下试字段
        }
        if (!ReferenceEquals(fi, _NoMember))
        {
            try { return ((FieldInfo)fi).GetValue(o); } catch { }
        }
        return null;
    }

    private static object FindProp(Type t, string name)
    {
        if (_propCache.TryGetValue((t, name), out var hit)) return hit;
        object found = _NoMember;
        try { var p = t.GetProperty(name, Any); if (p != null) found = p; } catch { }
        _propCache[(t, name)] = found;
        return found;
    }

    private static object FindField(Type t, string name)
    {
        if (_fieldCache.TryGetValue((t, name), out var hit)) return hit;
        object found = _NoMember;
        try { var f = t.GetField(name, Any); if (f != null) found = f; } catch { }
        _fieldCache[(t, name)] = found;
        return found;
    }

    /// <summary>读实例成员：属性（精确→不敏感）→ 字段（精确→不敏感）。找不到返回 null。
    /// ★ v1.0.1：成员查找走缓存；**查找顺序 / 索引器判定 / 异常吞掉 / 属性抛异常后回落字段** 全部**逐字保留**。</summary>
    public static object Prop(object o, string name)
    {
        if (o == null || string.IsNullOrEmpty(name)) return null;
        try
        {
            var t = o.GetType();
            if (FindProp(t, name) is PropertyInfo p && p.GetIndexParameters().Length == 0)
            {
                try { return p.GetValue(o); } catch { }     // ★ 抛了继续试字段（与原实现逐字一致）
            }
            if (FindField(t, name) is FieldInfo f) { try { return f.GetValue(o); } catch { } }
        }
        catch { }
        return null;
    }

    /// <summary>写实例成员（自动按目标类型 Coerce）。返回是否写成功。</summary>
    public static bool SetProp(object o, string name, object v)
    {
        if (o == null || string.IsNullOrEmpty(name)) return false;
        try
        {
            var t = o.GetType();
            // ★ v1.0.1：复用 `Prop` 的同两个缓存（属性/字段各一）。
            //   ⚠ **SetProp 的回落语义与 Prop 不同**，必须原样保留：
            //     · 属性**存在但不可写**（`!CanWrite`）或带索引器 ⇒ **继续回落字段** ✔
            //     · 属性**可写但 SetValue 抛异常** ⇒ **不回落字段**，直接落到本方法外层 `catch` ⇒ 返回 false ✔
            //       （与 `Prop` 相反 —— `Prop` 里属性 getter 抛异常是**局部 catch 后继续试字段**。）
            //   因为**只缓存"查找结果"、不缓存 setter 调用** ⇒ 上述两条分支**一个字都没动** ✔
            if (FindProp(t, name) is PropertyInfo p && p.CanWrite && p.GetIndexParameters().Length == 0)
            {
                p.SetValue(o, Coerce(v, p.PropertyType));
                return true;
            }
            if (FindField(t, name) is FieldInfo f)
            {
                f.SetValue(o, Coerce(v, f.FieldType));
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 装箱类型转换。★ 必须能造 **MongoId**：SPT 的物品 id 是 MongoId（不是 string），
    /// 直接给 string 会抛 `The value "…" is not of type "…MongoId"`。
    /// </summary>
    public static object Coerce(object v, Type t)
    {
        if (t == null) return v;
        if (v == null) return null;
        try
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;
            if (u == typeof(object)) return v;
            if (u.IsInstanceOfType(v)) return v;
            if (u == typeof(double)) return Convert.ToDouble(v);
            if (u == typeof(float)) return (float)Convert.ToDouble(v);
            if (u == typeof(int)) return (int)Math.Round(Convert.ToDouble(v));
            if (u == typeof(long)) return (long)Math.Round(Convert.ToDouble(v));
            if (u == typeof(bool)) return Convert.ToBoolean(v);
            if (u == typeof(string)) return v.ToString();
            if (u.IsEnum) return Enum.Parse(u, v.ToString(), true);
            // ★ MongoId：优先 string 构造器，其次 static Parse
            if (u.Name == "MongoId" || u.FullName == "SPTarkov.Server.Core.Models.Common.MongoId")
            {
                var ctor = u.GetConstructor(new[] { typeof(string) });
                if (ctor != null) return ctor.Invoke(new object[] { v.ToString() });
                var parse = u.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, null,
                                        new[] { typeof(string) }, null);
                if (parse != null) return parse.Invoke(null, new object[] { v.ToString() });
            }
            return Convert.ChangeType(v, u);
        }
        catch { return v; }
    }

    /// <summary>
    /// 用 JSON 造一个强类型 SPT 模型对象（★ 最稳的构造方式）。
    ///
    /// 为什么不用 <c>Activator.CreateInstance</c>：SPT 的模型有 <c>required</c> 成员
    /// （例 `Item.Id` 是 `required MongoId`），无条件构造会得到 `Id` 为空的非法对象。
    ///
    /// ★★ 关键坑（本次实测踩到并修正）：**默认的 `System.Text.Json` 读不了 `MongoId`** ——
    ///   `MongoId` 是个不可变 struct，只有 `MongoId(string)` 构造器与隐式转换，**没有默认转换器**，
    ///   所以裸跑 `JsonSerializer.Deserialize` 会抛：
    ///     `JsonException: The JSON value could not be converted to …MongoId`
    ///   修法 = 注册 **SPT 自己的** `StringToMongoIdConverter`
    ///   （`SPTarkov.Server.Core.Utils.Json.Converters/StringToMongoIdConverter.cs`，
    ///     SPT 在 `ConfigLoader.cs:36-50` 也正是把它加进 options 的，官方同款）。
    ///   通过**反射按全名**加载该类：这样即使 SPT 将来改了命名空间，也只是"转换器没注册上"，
    ///   由外层 try/catch 降级，**不会因为硬依赖而编译不过或运行崩溃**。
    /// </summary>
    public static object FromJson(Type t, string json, Action<string> warn = null)
    {
        if (t == null || string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(json, t, JsonOpts);
        }
        catch (Exception ex)
        {
            warn?.Invoke($"JSON 构造 {t.Name} 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把任意对象序列化成 JSON（★ 与 <see cref="FromJson"/> **同一套 options**，故可安全往返）。
    ///
    /// 用途：`LooseLootInjectDimension` 要**克隆** SPT 的 `Spawnpoint`（record + `required` 成员，
    /// 且 `Position` 是 `readonly struct Vector3`）—— 手工反射复制既长又容易漏字段，
    /// 而"序列化→反序列化"会造出一份**真正独立的新对象图**，且只依赖上面这两个已实证的转换器。
    ///
    /// ⚠ 只用于**内存里的对象复制**，不写盘；会展开 `null` 字段（体积比原 JSON 大，但正确性优先）。
    /// </summary>
    public static string ToJson(object o, Action<string> warn = null)
    {
        if (o == null) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Serialize(o, o.GetType(), JsonOpts);
        }
        catch (Exception ex)
        {
            warn?.Invoke($"JSON 序列化 {o.GetType().Name} 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>带 SPT MongoId 转换器的 JSON 选项（惰性建一次，失败则退回默认）。</summary>
    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = BuildJsonOpts();

    private static System.Text.Json.JsonSerializerOptions BuildJsonOpts()
    {
        var o = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        try
        {
            // ① SPT 自家的字符串 → MongoId 转换器（按全名反射，避免硬依赖）
            var tConv = Type.GetType("SPTarkov.Server.Core.Utils.Json.Converters.StringToMongoIdConverter, SPTarkov.Server.Core", false);
            if (tConv != null)
            {
                var inst = Activator.CreateInstance(tConv) as System.Text.Json.Serialization.JsonConverter;
                if (inst != null) o.Converters.Add(inst);
            }
            // ② 枚举转换器（官方 EftEnumConverterFactory）：让 side:"Usec" / type:"Item" 这类字符串能进枚举
            var tEnum = Type.GetType("SPTarkov.Server.Core.Utils.Json.Converters.EftEnumConverterFactory, SPTarkov.Server.Core", false);
            if (tEnum != null)
            {
                var inst = Activator.CreateInstance(tEnum) as System.Text.Json.Serialization.JsonConverter;
                if (inst != null) o.Converters.Add(inst);
            }
        }
        catch { }
        return o;
    }

    /// <summary>强类型 <c>List&lt;T&gt;</c>（T 来自某个已有集合的元素类型）。</summary>
    public static object NewListOf(Type elemType)
    {
        try { return Activator.CreateInstance(typeof(List<>).MakeGenericType(elemType)); }
        catch { return null; }
    }

    /// <summary>
    /// 取一个字典/表的**实参化值类型**（用于造正确的集合）。
    /// </summary>
    public static Type DictValueType(object dict)
    {
        try
        {
            var t = dict?.GetType();
            if (t != null && t.IsGenericType && t.GetGenericArguments().Length == 2)
                return t.GetGenericArguments()[1];
        }
        catch { }
        return null;
    }

    /// <summary>取一个字典的**实参化键类型**。</summary>
    public static Type DictKeyType(object dict)
    {
        try
        {
            var t = dict?.GetType();
            if (t != null && t.IsGenericType && t.GetGenericArguments().Length == 2)
                return t.GetGenericArguments()[0];
        }
        catch { }
        return typeof(string);
    }

    /// <summary>
    /// ★★ 关键：**MongoId 键的正确插入方式**。
    /// `Dictionary&lt;MongoId, …&gt;` 用 string 当键会抛
    /// `ArgumentException: The value "…" is not of type "…MongoId"` —— 必须经 <see cref="Coerce"/> 转换。
    /// </summary>
    public static bool DictSet(IDictionary d, string key, object val)
    {
        if (d == null || string.IsNullOrEmpty(key)) return false;
        try
        {
            d[Coerce(key, DictKeyType(d))] = val;
            return true;
        }
        catch { return false; }
    }

    /// <summary>按**字符串等价**查字典里是否已有该键（兼容 MongoId / string 两种键类型）。</summary>
    public static bool DictHas(IDictionary d, string key)
    {
        if (d == null || string.IsNullOrEmpty(key)) return false;
        try
        {
            foreach (var k in d.Keys)
                if (string.Equals(k?.ToString(), key, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>按字符串等价取字典值。</summary>
    public static object DictGet(IDictionary d, string key)
    {
        if (d == null || string.IsNullOrEmpty(key)) return null;
        try
        {
            foreach (var k in d.Keys)
                if (string.Equals(k?.ToString(), key, StringComparison.OrdinalIgnoreCase)) return d[k];
        }
        catch { }
        return null;
    }

    /// <summary>按字符串等价删字典项。</summary>
    public static bool DictRemove(IDictionary d, string key)
    {
        if (d == null || string.IsNullOrEmpty(key)) return false;
        try
        {
            object hit = null;
            foreach (var k in d.Keys)
                if (string.Equals(k?.ToString(), key, StringComparison.OrdinalIgnoreCase)) { hit = k; break; }
            if (hit == null) return false;
            d.Remove(hit);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 取集合的元素类型：优先泛型实参（`List&lt;T&gt;` → T），其次看第一个元素的实际类型。
    /// 用于"按已有集合的元素类型造新元素"。
    /// </summary>
    public static Type ElemType(IList list)
    {
        if (list == null) return null;
        try
        {
            var t = list.GetType();
            if (t.IsGenericType && t.GetGenericArguments().Length == 1) return t.GetGenericArguments()[0];
            if (list.Count > 0) return list[0]?.GetType();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 从 `LocationTable` 拿"地图名 → Location"字典。
    /// 官方 API <c>GetDictionary()</c> 优先（本 mod 已实证可用），内部缓存字段兜底。
    /// </summary>
    public static IDictionary LocationDict(object locationTable)
    {
        if (locationTable == null) return null;
        try
        {
            var m = locationTable.GetType().GetMethod("GetDictionary", Type.EmptyTypes);
            if (m != null)
            {
                var d = m.Invoke(locationTable, null) as IDictionary;
                if (d != null) return d;
            }
        }
        catch { }
        try
        {
            var cache = Prop(locationTable, "_locationDictionaryCache") ?? Prop(locationTable, "locationDictionaryCache");
            return cache as IDictionary;
        }
        catch { return null; }
    }
}
