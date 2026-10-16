using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名（同 BeastArt.cs）。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;
using LogType = MegaCrit.Sts2.Core.Logging.LogType;

namespace RayMod.Patches;

/// <summary>
/// ★★★ 2026-10-10：把机制图标（层数/沙地兽/弹药）**画在卡牌节点之上**，而不是烘进卡框贴图。
///
/// <para><b>为什么非要这么做</b>：用户先要求"放在右上角"，但实机发现**被标题缎带盖住**。
/// 根因是层次 —— 卡框（<c>CardModel.Frame</c>）是卡片场景里**最底下**的一层，
/// 缎带 <c>%TitleBanner</c>、卡图、文字都是独立节点、都画在卡框之上。
/// <b>烘在贴图里的东西永远在缎带下面</b>，只有**加一个真正的节点**才能压上去。</para>
///
/// <para><b>怎么接上去的</b>（反编译 <c>MegaCrit.Sts2.Core.Nodes.Cards.NCard</c> 得到的真实结构）：
/// <list type="bullet">
///   <item><c>NCard : Control, IPoolable</c>，<b>默认尺寸 300x422</b>（卡框贴图 598x844 是等比缩放显示的）；</item>
///   <item><c>public Node OverlayContainer</c> → 场景里的 <c>%OverlayContainer</c>，**现成的浮层容器**；</item>
///   <item><c>public CardModel? Model</c> + <c>public event Action&lt;CardModel?&gt; ModelChanged</c>
///         → 卡片是**对象池复用**的，所以每次换卡都要重新刷新图标。</item>
/// </list></para>
///
/// <para><b>为什么用反射而不是直接引用 NCard</b>：这是游戏内部类型，模组编译时能引用到，
/// 但用 <c>AccessTools.TypeByName</c> 找 + 反射读写属性，可以让**万一游戏改版**时这段
/// 只是静默不生效，而不是整个模组编译不过 / 启动就炸。</para>
///
/// <para>图标用的是模组自己的**原版 power 贴图**（<c>images/powers/*.png</c>），
/// 用户原话："不是牌子，其实就是把原power贴图贴上去"。</para>
/// </summary>
[HarmonyPatch]
internal static class CardTagIconOverlayPatch
{
    private static Type? _ncard;

    private static readonly StringName ReadyName = "_Ready";

    [HarmonyPrepare]
    private static bool Prepare()
    {
        _ncard = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Cards.NCard");
        return _ncard != null;
    }

    [HarmonyTargetMethod]
    private static MethodBase? TargetMethod() => _ncard == null ? null : AccessTools.Method(_ncard, "_Ready");

    [HarmonyPostfix]
    private static void Postfix(Node __instance) => CardTagIconOverlay.Attach(__instance);
}

/// <summary>
/// ★ 2026-10-10：**换卡刷新改走 <c>NCard.Model</c> 属性 setter 的 Postfix**，不再订阅 <c>ModelChanged</c> 事件。
///
/// <para><b>为什么必须换</b>：<c>NCard</c> 是对象池节点，<c>OnReturnedFromPool()</c> 会把
/// <c>_model = null;</c>（<c>NCard.cs:1494</c>）并把 <c>ModelChanged = null;</c>（<c>NCard.cs:1502</c>）
/// —— 我们的订阅被**游戏自己抹掉**；而 Godot 节点离开再回到场景树时 <c>_Ready</c> **不会重跑**，
/// 于是池化到位的手牌/弃牌卡图标会**永久冻结**（战斗里的手牌、弃牌堆正是池化的）。</para>
///
/// <para><b>为什么 setter 是正解</b>：<c>Model</c> setter 每次模型变化都会被游戏自己调用，
/// 不依赖任何订阅存活；而且 Postfix 的 <c>__instance</c> 上 <c>_model</c> 已经写完，
/// 回读属性拿到的**一定是新模型**（事件参数传的反而是旧模型，见 <c>NCard.cs:657</c>）。</para>
///
/// <para>与 <see cref="CardTagIconOverlayPatch"/> 一样：全部用反射拿 <c>set_Model</c>，
/// **不直接引用 <c>NCard</c> 类型** —— 游戏改版时只是静默不生效，不会编译不过 / 启动就炸。
/// <c>set_Model</c> 不存在时 <c>HarmonyPrepare</c> 返回 false，Harmony 直接跳过本类，**不抛异常**。</para>
/// </summary>
[HarmonyPatch]
internal static class CardTagIconOverlayModelPatch
{
    private static Type? _ncard;

    /// <summary>★ target 缓存：`HarmonyPrepare` 里只解析一次。</summary>
    private static MethodInfo? _setter;

    [HarmonyPrepare]
    private static bool Prepare()
    {
        try
        {
            _ncard = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Cards.NCard");
            _setter = _ncard == null ? null : AccessTools.PropertySetter(_ncard, "Model");

            // ★ 拿不到就整体跳过（返回 false 是 Harmony 的"跳过本类"语义，不是异常）。
            return _setter != null;
        }
        catch
        {
            return false;
        }
    }

    [HarmonyTargetMethod]
    private static MethodBase? TargetMethod() => _setter;

    [HarmonyPostfix]
    private static void Postfix(Node __instance) => CardTagIconOverlay.RefreshFor(__instance);
}

/// <summary>机制图标浮层的实现。</summary>
internal static class CardTagIconOverlay
{
    /// <summary>
    /// ★ 出问题时的总开关。**2026-10-10 起重新打开**：
    /// 坐标已由单行诊断日志实测校准（holder 挂在 NCard 自身、局部原点在卡片中心、
    /// `TopInset` 由"贴图系 y/2"推得），不再靠猜。
    /// <para>2026-10-10 二次关闭：<c>ZIndex = 4096</c> 会**穿透卡牌图鉴的放大查看器**（用户实测"放大查看时
    /// 其它卡牌的图标不会消失"）。已查证放大层 <c>NInspectCardScreen</c> 全链 z_index 都是 0、靠树序盖住网格
    /// （见 HANDOFF 铁律 48 同款）。浮层的 z 已同步改 0（见下），下次启用即为安全值。</para>
    /// <para>2026-10-10 第四次启用：z 已同步改为 <b>0</b>（见建 holder 处注释）—— 失败原因是 <c>ZIndex=4096</c> 会穿透
    /// <c>NInspectCardScreen</c> 放大查看器（该层全链 z_index 都是 0，靠树序盖住网格）。z=0 时 holder 是卡片的
    /// 最后一个子节点，按树序仍画在 <c>%Frame</c>/卡图之上，且不再穿透放大卡。</para>
    /// </summary>
    internal const bool Enabled = true;

    private static readonly Logger Log = new("RayMod.Art", LogType.Generic);

    private const string PowerDir = Entry.ResPath + "/images/powers";

    /// <summary>★ 2026-10-10：浮层节点名 —— 原本有 5 处字符串字面量（Attach 的幂等判断、RefreshFor、Dump、
    /// DeferLayoutCheck、诊断打印），全部收敛到这一个常量，**改动宿主位置时不会再漏掉某一处**。</summary>
    private const string HolderName = "RayModTagIcons";

    /// <summary>
    /// ★★★ 2026-10-10（Bug 2 = "进卡堆动画时四个标记飞出卡面"的修复）：浮层的宿主改成
    /// **卡面容器 <c>%CardContainer</c>**（= 反编译出来的 <c>NCard.Body</c>，<c>NCard.cs:610/704</c>），
    /// 不再是卡片根节点。
    ///
    /// <para><b>为什么</b>：<c>NCardFlyVfx.PlayAnim()</c>（`NCardFlyVfx.cs:282-283` / `:309`）把
    /// <c>_card.Body.Scale</c> 从 1 缩到 0.1 再缩到 0、并把 <c>Body.Modulate</c> 压黑 ——
    /// <b>动的是 <c>%CardContainer</c> 这个子节点，不是 <c>NCard</c> 自己</b>。
    /// 浮层之前挂在卡片根上，就是 <c>%CardContainer</c> 的**兄弟** ⇒
    /// **卡面缩到 10%、浮层还是原尺寸** ⇒ 四个标记被"留在原地"，看起来就是飞出了卡面。
    /// 挂进 <c>%CardContainer</c> 后，卡面缩多少、浮层就缩多少，**自动跟到 0**（什么都不用检测）。</para>
    ///
    /// <para><b>坐标为什么不变</b>：<c>%CardContainer</c> 实测在卡片局部 <b>(0,0)</b>、scale <b>(1,1)</b>、
    /// 无 material（`card.tscn` 里该节点只有 `unique_name_in_owner = true`），
    /// 而卡片局部原点在**卡片中心**（见下方 CardHalfW/CardHalfH 的实测日志）⇒
    /// holder 的 <c>Position = (-150,-211)</c> 相对 <c>%CardContainer</c> 与相对卡片根**是同一个点**，
    /// <b>下面那套"贴图系 ↔ holder 内"的换算一个字都不用改</b>。
    /// 为防万一（<c>%CardContainer</c> 将来被挪位）才在 Attach 里减掉一次它的 <c>Position</c>（实测为 0 = 空操作）。</para>
    /// </summary>
    private const string BodyPath = "%CardContainer";

    /// <summary>
    /// ★ 2026-10-10：同一节点的**普通相对路径**写法 —— `card.tscn` 里 `CardContainer` 就是卡根的第一个子节点
    /// （反编译侧 <c>NCard.cs:699</c> 也是用 <c>"CardContainer/CardSparkles"</c> 这个相对路径找东西）。
    /// 用途 = 具名查找（<c>%</c>，依赖节点 owner）万一失效时的兜底，**纯保险，正常路径用不到**。
    /// </summary>
    private const string BodyChildPath = "CardContainer";

    /// <summary>机制字母 → power 贴图（和 <c>draw_card_parts.py</c> 的 POWER_ICON 必须一致）。</summary>
    private static readonly (string Letter, string File)[] Icons =
    {
        ("d", "DepthPower.png"),
        ("a", "AmmoPower.png"),
        ("b", "SandBeastMarkPower.png"),
    };

    // 卡牌节点是 300x422（见反编译出来的 `NCard.defaultSize`）；下面都是这个坐标系里的值。
    // 换算到卡框贴图（598x844）：x * 1.993、y * 2.000。
    // ★★★ 2026-10-10 诊断日志给的**真实坐标**（别再猜了）：
    //   card: NCard  size=(0,0)          ← 卡片自己 Size 是 0，children 用负偏移排布
    //   %Frame: pos=(-150,-211) size=(300,422) pivot=(150,211)
    //   → **卡片局部坐标的原点在卡片【中心】**，卡面范围 = x[-150,150] × y[-211,211]。
    //   卡框贴图 598x844 ↔ 局部 300x422： texture_x = (local_x + 150) * 1.9932，
    //                                      texture_y = (local_y + 211) * 2.0
    private const float CardHalfW = 150f;    // 卡片局部半宽（= 卡框贴图 598/2/1.9932）
    private const float CardHalfH = 211f;    // 卡片局部半高（= 卡框贴图 844/2/2.0）
    private const float IconSize = 30f;      // ≈ 贴图系 60px

    // ★★★ 换算公式（已验算，下次直接用，不用再推）：
    //   holder 是卡片的直接子节点，Position = (-CardHalfW, -CardHalfH) = (-150, -211)，
    //   而卡片局部原点在【卡片中心】、范围 x[-150,150] × y[-211,211]，
    //   所以 holder 局部 (hx, hy) 对应卡片局部 (hx-150, hy-211)。
    //   贴图系 598x844 ↔ 卡片局部 300x422：比例 x = 598/300 = 1.99333，y = 844/422 = 2.0（精确）。
    //     holder内x = 贴图系x / 1.99333        （把卡片局部 x = 贴图系x/1.9932 - 150 代进 hx = cx + 150，-150 消掉）
    //     holder内y = 贴图系y / 2               （把卡片局部 y = 贴图系y/2 - 211 代进 hy = cy + 211，-211 消掉）
    // holder内x = 贴图系x / 1.99333 ； holder内y = 贴图系y / 2
    // （由 holder.Position = (-150,-211) 与卡片局部原点在中心推出，常数项相消）
    private const float IconLeft = 285f;     // holder 内左边 = 贴图系 x 568（贴紧卡牌最右缘那条竖带的正中）；
                                             // 中心 = 285 + 30/2 = 300 → 贴图系 598，即卡牌右缘，略可探出
    private const float TopInset = 92f;      // holder 内 y = 贴图系 y 184。缎带下边缘贴图系 y≈174（holder 内 87），
                                             // 竖带一直延伸到卡图外框底 贴图系 y≈534（holder 内 267）→ 92 在缎带之下
    // ★ 2026-10-10：34 → 40。图标 30px 旋转 -15° 后**外接高度 ≈ 36.7px**，比槽距 34 还大，
    //   相邻两枚会压边约 2.7px（实机上看着三枚叠在一起）。40 留出余量。
    private const float SlotStep = 40f;      // 三个图标 holder y = 92/132/172 → 贴图系 y = 184/264/344，全在竖带纵向范围内
    private const float TiltDeg = -15f;

    // ══════════════════════════════════════════════════════════════════════════
    // ★★★ 2026-10-10 追加（A 件）：玫瑰粉**兔子** `RoseMark` —— 「只属于莱伊」的标记。
    //
    // <para><b>★ 形态来源：从【用户给的参考图】里**提取**出来的那只兔子 ★</b>
    // <list type="bullet">
    //   <item><b>用户原话（判据，逐条）</b>：
    //         "我这里有个现成的，但别直接用" → "这不是咧嘴骷髅，这是兔子啊。我服了"
    //         → "不应该按照我的参考图做吗"。
    //         ⇒ 要的是**照参考图里那只兔子**（那只兔子的形象），
    //           **不是我另画一只"像兔子的东西"** —— 上一版我**自画的"兔头（外轮廓 + 排线）"
    //           已被用户否掉**，本轮的贴图**必须**从参考图取。</item>
    //   <item><b>"别直接用"怎么理解</b>：**不要整张 logo 抠下来贴上去** ——
    //         只取**兔子本体**，去掉文字（`TO THE` / `GRINNING` / `VALLEY` / `去咧嘴谷`）
    //         与深灰背景。⚠️ 不把整张 logo / 任何文字笔画贴上卡。</item>
    //   <item><b>参考图</b>：`C:\Users\MapleZero\.dsh\attachments\v1\objects\2a\2af6b3a4…`
    //         （369x471 PNG，深灰底 `(44,44,44)` + 白色线描）
    //         = `去咧嘴谷 / TO THE GRINNING VALLEY` 的 logo。兔子在**左上块**：
    //         两片长耳向右上扬起（耳内有斜排线）、圆头在左下（脸上竖向排线）、
    //         下巴一道露齿的弧；文字在兔子右侧与下方。</item>
    //   <item><b>兔子在参考图里的 bbox = (22,25)–(270,446)</b>（x>270 全是文字，已排除）；
    //         <b>没有混进文字笔画</b>，所以**抹除范围 = 空**（没有需要抹的地方）。</item>
    //   <item><b>可复现命令</b>（只读参考图 / 只写出成品贴图）：
    //         `python -B _tools\extract_rabbit_ref.py`（网格图 + bbox + 提取图）
    //         → `python -B _tools\build_rose_mark.py`（出 `rose_mark.png`）。
    //         带坐标网格的核对图：`_ref\card_parts\out\_ref_grid.png`；
    //         提取图（给用户确认"是不是这只"）：`_ref\card_parts\out\_ref_rabbit_extract_x3.png`。</item>
    // </list></para>
    //
    // <para><b>配色</b>（用户指定三段，暗部→深玫瑰）：暗部 `#783652` / 中间 `#AD4E6F` /
    // 亮部 `#CE789B`（近白高光 `#E9B7CB`）。做法 = 把提取出的**灰度当明度**映射到这条色阶。
    // ★ **保留参考图里的深色外轮廓**：兔子有黑边 ⇒ 明度极小 ⇒ 映射后仍落在**最暗段
    // `#783652`**，不会糊成一片粉。深灰底按"亮于底色"转成 alpha（透明 + 边缘抗锯齿，不留灰边），
    // 并对线宽做**轻度加粗**（缩到卡面尺寸前），否则 1px 线会消失。</para>
    //
    // <para><b>为什么非要走浮层</b>：卡框 / 钢带 / 木板都要过 `PoolFrameMaterial` 的
    // **replace-hue** shader（保明度、保饱和度、只换色相）。实测 `#AD4E6F`（sat 0.549）
    // 过完变成 `#7B939B`（灰青），和主题青过完的 `#8FA3A9` **肉眼分不出**。
    // 浮层是卡片的子节点、画在 `%Frame` 之上，**不过材质** ⇒ 这里是全卡唯一能让
    // **真粉色**出现在卡面正面的通路（立绘外框那条玫瑰线是另一条，在卡图区）。</para>
    //
    // <para><b>落位是量出来的，不是猜的</b>（`_tools\_panel_layout_probe.py` 的【6d】/【6f】，
    // 实机口径 = 机制图标是浮层、不在贴图上）：
    // <list type="bullet">
    //   <item>面板真范围 (46,470)–(547,782)，扣掉木板/比例尺/三个标高点/钻孔柱/四角角标/
    //         钢带/描述文字带后，**最大内接正方形 = 130x130 @(418,594)** ⇒ 正中心
    //         **贴图系 (482,658)** 是"离所有占用区最远"的那一点；</item>
    //   <item>该点与**每一个**占用矩形的取交都是 **0 命中**，自由掩码覆盖 **100%**
    //         （探针【6e】/【6f】实测）；</item>
    //   <item>★ 2026-10-10（v7 放大档）换成**贴图 65x110**（= 2.5x 档）后**再量一遍**
    //         （`_tools\_rose_tier_probe.py`，全自由掩码穷举 + 逐条取交）：
    //         以 (482,658) 为中心的**贴图系盒子 450..514 × 603..712** 与上面**每一条**禁区
    //         **逐条 0 命中**；2x（52x88）与 3x（78x132）同样 0 命中；
    //         且**三档都保持 (482,658) 原位、都不需要挪**（全自由掩码里分别存在
    //         11344 / 5403 / 2352 个全空位置）。
    //         最近邻余量（2.5x 档）：描述文字带下缘 y575 → 盒上缘 603 = **27px**
    //         （文字带 x 只到 499、盒子 x 从 450 起，x 向不重叠）；比例尺上缘 y766 → 盒下缘 712
    //         = **53px**；木板右缘 x294 → 盒左缘 450 = **155px**；
    //         标高点[skill] 383,674..417,732 在盒左侧 33px 外；四角角标全部在盒外。</item>
    //   <item>★ v7 同时修掉一个**层次缺陷**：上一版暗段 `#783652` 与高光 `#E9B7CB`
    //         **命中 0 像素**（成品只落在"中玫瑰→亮玫瑰"的窄区间）。原因是 v6 把明度
    //         硬压成 `0.35 + 0.65*lum` ⇒ 两端永远到不了。v7 改成**按笔画加权分位数
    //         （p2/p98）做直方图拉伸**，再映射到带两端平台的平滑色阶 ⇒ 四段全部有像素
    //         （2.5x 档实测：暗段家族 1090px / 中间 655 / 亮部 256 / 高光家族 532）。</item>
    //   <item>（用户提过备选 @(482,690)：实测同样 0 命中，盒子落在候选 `418,674..547,741`
    //         内；不采用，因为原位的上/下都还有余量，改动更小。）</item>
    //   <item>⚠️ 与三枚图标"同一竖列、图标下方"那个位置（贴图系 574,430）实测：
    //         **skill 牌上立绘外框有 104/169 不透明像素**（会压到外框下摆），
    //         故**不选**它 —— 用户要求"不压任何已占用区"。</item>
    //   <item>★★ 2026-10-10（<b>v8 = 最后一次位置微调</b>，用户拍板"做完这个卡面就结束"）：
    //         用户贴实机局部图、用**红箭头指着卡框右侧那条竖边**说：
    //         **「让它靠在这个边缘吧。做完这个卡面就结束」**
    //         ⇒ 标记从"面板右下空地中间" (482,658) 改成 **紧贴面板右边界**。
    //         实测（`_tools\_edge_probe_v8.py`：**逐行**量面板右边界 + 逐条取交，只读探针）：
    //         面板右边界在要用的那段 y = **x 547**（y604..713 共 110 行：547 为主，3 行 548，
    //         取众数 547）；新盒子 = **贴图系 483,604 .. 547,713**（65x110，
    //         右缘与边界**贴合偏差 0px** = 严丝合缝贴住，盒子四边都在面板真范围内）。
    //         与每一条禁区**逐条 0 命中**，最近邻余量：
    //         **右下角标 (507,742..536,771) 上缘 742 → 盒下缘 713 = 28px**、
    //         **描述文字带 (99,520..499,575) 下缘 575 → 盒上缘 604 = 28px**、
    //         比例尺 (396,766..499,773) 上缘 766 → 52px、右上角标 (507,480..536,509)
    //         下缘 509 → 94px、弩准星墨迹 (458,484..482,508) 右缘 482 → 95px、
    //         标高点[skill] 右缘 417 → 65px、木板右缘 294 → 188px、下缘钢带上缘 790 → 76px、
    //         面板上沿 y470 → 134px。
    //         ★ 贴右边时**唯一要当心的就是右上/右下两个角标**（它们 x 507..536 与盒子
    //         x 483..547 **重叠**）⇒ 按用户口径**不改"贴边"、只在 y 方向上下挪**：
    //         把兔子放进两个右侧角标之间的竖向空隙里（y604..713，上离文字带 28px、
    //         下离右下角标 28px，正落在中间）。</item>
    // </list></para>
    //
    // <para><b>坐标换算</b>（照上面的公式；★ v8 用的是**贴右边**的新落点）：
    // **中心** holder 内 x = 贴图系 x / 1.99333 = 515/1.99333 = **258.3616**；
    // holder 内 y = 贴图系 y / 2 = 658.5/2 = **329.25**（贴图系盒子 483,604..547,713 的几何中心）。
    // **尺寸**同样按这两个比例缩：贴图 65x110 ⇒ 节点 65/1.99333 x 110/2 = **32.6075 x 55.00**
    // （见 <see cref="RoseW"/> / <see cref="RoseH"/>；★ v8 **只挪位置、尺寸一个像素都没动**）。</para>
    // ══════════════════════════════════════════════════════════════════════════
    private const string RoseMarkName = "RoseMark";

    /// <summary>标记贴图路径 —— ★ 必须在 `res://RayMod/**` 下（铁律 21）。</summary>
    private const string RoseMarkPath = Entry.ResPath + "/images/cards/parts/rose_mark.png";

    /// <summary>
    /// ★ 2026-10-10（第五次改 = v7 **放大档**）：节点系**宽** = 贴图系 65 / 1.99333 = **32.6075**。
    /// <para>用户判据（原话）：**「做大一点不行吗。」** —— 上一版贴图 26x44（节点 13.0435x22）太小、
    /// **读不出是兔子**。根因：参考图那套线描只有 **1~3px** 宽，缩到 26px 宽必然糊；
    /// **放大后线条相对粗细就够了 ⇒ 不需要裁构图**。</para>
    /// <para>演变：16x16 正菱形 → 22x30 自画兔头（← 用户否掉）→ 26x44 参考图提取（← 用户嫌小）
    /// → ★ **65x110 参考图提取（v7，2.5x 档）**。标记**从来不是正方形**，
    /// 所以原来的单个 <c>RoseSize</c>（6f）早已**拆成 <c>RoseW</c> / <c>RoseH</c>**。</para>
    /// </summary>
    private const float RoseW = 32.6075f;

    /// <summary>★ 2026-10-10（v7）：节点系**高** = 贴图系 110 / 2.0 = **55.0**（换算见上方注释）。</summary>
    private const float RoseH = 55f;

    /// <summary>
    /// ★ 2026-10-10（v8 **贴右边**）：holder 内中心 x = 贴图系 **515** / 1.99333 = **258.3616**。
    /// <para>用户判据（原话）：**「让它靠在这个边缘吧。做完这个卡面就结束」** —— 用户红箭头指的是
    /// **卡框右侧那条竖边** ⇒ 标记要**紧贴面板右边界**（贴图系 x=547），
    /// 不再浮在面板右下的空地中间（v7 的 482 ⇒ 241.81 已废）。</para>
    /// </summary>
    private const float RoseNodeX = 258.3616f;

    /// <summary>
    /// ★ 2026-10-10（v8 **贴右边**）：holder 内中心 y = 贴图系 **658.5** / 2 = **329.25**。
    /// <para>贴右边后盒子 = 贴图系 **604..713**（几何中心 658.5）—— 为**避开右侧两个角标**
    /// 而在 y 方向挪到竖向空隙正中：上离描述文字带 28px、下离右下角标 28px（都实测）。</para>
    /// </summary>
    private const float RoseNodeY = 329.25f;

    private static bool _logged;

    /// <summary>★ 诊断计数器：**只在第一张卡上做深诊断**，之后所有卡都早退。</summary>
    private static int _dumps;

    /// <summary>★ 诊断计数器：触发节点类型**每次都打**（`WarnOnce` 会限流，正好吞掉关键那次），只限前 6 次。</summary>
    private static int _typeDumps;

    /// <summary>★ 诊断计数器：布局后复查只排一次。</summary>
    private static bool _layoutChecked;

    /// <summary>
    /// ★ 2026-10-10 `<c>[图标表]</c>` 一次性诊断：**已经打过的卡牌类名** —— 同一张卡只打一次。
    /// <para>用 <see cref="StringComparer.Ordinal"/>（不是默认的字符串比较器）：类名是标识符，
    /// 只认逐字节相等，避免任何区域性相关的"看起来一样却算不同"。</para>
    /// </summary>
    private static readonly HashSet<string> TagDumped = new(StringComparer.Ordinal);

    /// <summary>★ `<c>[图标表]</c>` 诊断计数器：只打前 120 条（92 张卡 + 富余），打满即停。</summary>
    private static int _tagDumps;

    /// <summary>★ `<c>[图标表]</c>` 条数上限。</summary>
    private const int TagDumpLimit = 120;

    /// <summary>
    /// ★★★ 2026-10-10 重写：**每个字段一条 `Log.Warn()`**，单行、不含 `\n`。
    ///
    /// <para><b>为什么不能再用多行字符串</b>：上一版把整块拼成一个多行字符串一次性交给
    /// `Log.Warn`，实机日志里**只有表头两行**，`ancestor[]` / `%Frame` / holder 那几行**全部丢失**。
    /// → 这款游戏的日志器**不可靠地处理多行字符串**，所以必须一行一条。</para>
    ///
    /// <para><b>限流</b>：只在**第一次 `_Ready`** 时跑（`_dumps >= 1` 直接早退），
    /// 并且**不再用 `CallDeferred` 打第二遍** —— 布局后的复查挪到
    /// <see cref="DeferLayoutCheck"/>，只打一两行关键数字。</para>
    ///
    /// <para>这段**只读**：不改任何节点（不改位置、不加子节点）。</para>
    /// </summary>
    internal static void Dump(Node card, string when)
    {
        if (_dumps >= 1)
        {
            return;
        }

        _dumps++;

        try
        {
            Log.Warn($"[图标诊断] ===== 卡片坐标体系 [{when}]（每字段一行）=====");

            // 1. 触发节点本身是什么（全限定类型名）
            if (card is Control cc)
            {
                Log.Warn($"[图标诊断] card type={card.GetType().FullName} isControl=True gpos={cc.GlobalPosition}");
            }
            else
            {
                Log.Warn($"[图标诊断] card type={card.GetType().FullName} isControl=False gpos=<非 Control>");
            }

            // 2. 卡片自己的 Size/Position/Scale（Size 是 0 也照打）
            if (card is Control c0)
            {
                Log.Warn($"[图标诊断] card size={c0.Size} pos={c0.Position} scale={c0.Scale}");
            }
            else
            {
                Log.Warn("[图标诊断] card size=<非 Control> pos=<非 Control> scale=<非 Control>");
            }

            // 3. 往上最多 6 层祖先，每层一条；不是 Control 就不打 pos/size/gpos
            Node?[] chain = new Node?[6];
            int n = 0;

            for (Node? cur = card; cur != null && n < 6; cur = cur.GetParent())
            {
                chain[n++] = cur;
            }

            for (int i = 0; i < n; i++)
            {
                Node a = chain[i]!;
                string head = $"[图标诊断] ancestor[{i}] type={a.GetType().FullName}";

                if (a is Control ac)
                {
                    Log.Warn($"{head} pos={ac.Position} size={ac.Size} scale={ac.Scale} gpos={ac.GlobalPosition}");
                }
                else
                {
                    Log.Warn($"{head} pos=<非Control> size=<非Control> scale=<非Control> gpos=<非Control>");
                }
            }

            // 4. 具名子节点，每个一条
            //    ★ 2026-10-10：加了 `%CardContainer`（= `NCard.Body`，浮层现在的宿主）——
            //      这一行是**"换宿主没有挪动浮层"的实机证据**：期望 `pos=(0,0) scale=(1,1)`，
            //      若它非 0，写进日志的数字就是 Attach 里那个 `- host.Position` 补偿的依据。
            foreach (string name in new[] { "%CardContainer", "%Frame", "%PortraitBorder", "%TitleBanner", "%OverlayContainer" })
            {
                Node? sn = card.GetNodeOrNull(name);

                if (sn == null)
                {
                    Log.Warn($"[图标诊断] {name} <不存在>");
                }
                else if (sn is Control sc)
                {
                    Log.Warn($"[图标诊断] {name} type={sn.GetType().FullName} pos={sc.Position} gpos={sc.GlobalPosition}"
                             + $" size={sc.Size} scale={sc.Scale} pivot={sc.PivotOffset}");
                }
                else
                {
                    Log.Warn($"[图标诊断] {name} type={sn.GetType().FullName} <非 Control>");
                }
            }

            // 5. %Frame / %PortraitBorder 的全局矩形（算坐标的硬数据：右下 = gpos + size）
            foreach (string name in new[] { "%Frame", "%PortraitBorder" })
            {
                if (card.GetNodeOrNull(name) is Control rc)
                {
                    Log.Warn($"[图标诊断] {name} 全局矩形 gpos={rc.GlobalPosition} 右下={rc.GlobalPosition + rc.Size}");
                }
                else
                {
                    Log.Warn($"[图标诊断] {name} 全局矩形 <非 Control 或不存在>");
                }
            }

            // 6. holder + 每个 Tag_*
            //    ★ 2026-10-10：查找改走 FindHolder（宿主已从卡片根换成 `%CardContainer`）。
            Control? holder = FindHolder(card);

            if (holder == null)
            {
                Log.Warn("[图标诊断] holder <未挂载>（%CardContainer 下与卡片根下都没有）");
            }
            else
            {
                Log.Warn($"[图标诊断] holder parent={holder.GetParent()?.Name} gpos={holder.GlobalPosition}"
                         + $" size={holder.Size} scale={GlobalScaleOf(holder)} clip={holder.ClipContents}");

                foreach (Node child in holder.GetChildren())
                {
                    string cname = child.Name.ToString();

                    if (!cname.StartsWith("Tag_", StringComparison.Ordinal)
                        && !string.Equals(cname, RoseMarkName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (child is Control ic)
                    {
                        Log.Warn($"[图标诊断] {cname} gpos={ic.GlobalPosition} size={ic.Size}"
                                 + $" scale={GlobalScaleOf(ic)} grot={GlobalRotationOf(ic)} visible={ic.Visible}");
                    }
                    else
                    {
                        Log.Warn($"[图标诊断] {cname} <非 Control>");
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[图标诊断] 失败：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// ★ "布局完成后"的单行复查：`_Ready` 时布局还没算完，数字可能是 0，
    /// **这一条才是判断"+150/−150 那个偏移对不对"的关键**。
    /// 只打一行、只跑一次（第一张卡），异常只记一条。
    /// </summary>
    private static void DeferLayoutCheck(Node card)
    {
        if (_layoutChecked)
        {
            return;
        }

        _layoutChecked = true;

        Callable.From(() =>
        {
            try
            {
                string frame = card.GetNodeOrNull("%Frame") is Control f
                    ? $"gpos={f.GlobalPosition} 右下={f.GlobalPosition + f.Size}"
                    : "<无>";
                // ★ 2026-10-10：查找改走 FindHolder（宿主已换成 `%CardContainer`）。
                string holder = FindHolder(card) is Control h
                    ? $"gpos={h.GlobalPosition} 右下={h.GlobalPosition + h.Size} parent={h.GetParent()?.Name}"
                    : "<未挂载>";

                Log.Warn($"[图标诊断] 布局后 frame 全局矩形 {frame} holder 全局矩形 {holder}");
            }
            catch (Exception e)
            {
                Log.Warn($"[图标诊断] 布局后复查失败：{e.GetType().Name}: {e.Message}");
            }
        }).CallDeferred();
    }

    /// <summary>
    /// ★ 逐级向上累乘 `Scale` = 这个节点相对画布的总缩放。
    ///
    /// <para>为什么要手算：这个 Godot 版本的 `Control` **没有 `GlobalScale` / `GlobalRotation`**
    /// （实测编译报 CS1061），所以改用累乘。</para>
    ///
    /// <para>缩放挂在祖先 `NGridCardHolder` 那层（实测 0.8），所以必须**一路乘到顶**，
    /// 只看节点自己的 `Scale` 会漏掉祖先的缩放 —— 那正是前四次"放大时图标乱飞"的根因。</para>
    /// </summary>
    private static Vector2 GlobalScaleOf(Node n)
    {
        Vector2 s = Vector2.One;

        for (Node? cur = n; cur != null; cur = cur.GetParent())
        {
            if (cur is Control c)
            {
                s *= c.Scale;
            }
            else if (cur is Node2D n2)
            {
                s *= n2.Scale;
            }
        }

        return s;
    }

    /// <summary>逐级累加 `Rotation`（弧度）= 相对画布的总旋转。`PivotOffset` 只影响原点，不影响转角。</summary>
    private static float GlobalRotationOf(Node n)
    {
        float r = 0f;

        for (Node? cur = n; cur != null; cur = cur.GetParent())
        {
            if (cur is Control c)
            {
                r += c.Rotation;
            }
            else if (cur is Node2D n2)
            {
                r += n2.Rotation;
            }
        }

        return r;
    }

    /// <summary>
    /// ★ 2026-10-10：**浮层的宿主容器** —— 优先 <c>%CardContainer</c>（= <c>NCard.Body</c>），
    /// 拿不到就退回卡片根（行为回到本次修改之前，只是 Bug 2 会重现，**不会崩**）。
    ///
    /// <para>全部走 <c>Node.GetNodeOrNull</c>（<c>%</c> 具名查找 + 普通相对路径），
    /// **不直接引用 <c>NCard</c> 类型**：与全文件一致的"游戏改版只静默降级"策略。</para>
    /// </summary>
    private static Control? FindHost(Node card)
    {
        try
        {
            // ① 具名查找（和 `NCard._Ready` 里 `GetNode<Control>("%CardContainer")` 完全同一条路，
            //    已由游戏自己证明可用）。★ 具名查找走的是节点的 owner，**卡片被 Reparent 到飞行容器后依然有效**。
            if (card.GetNodeOrNull(BodyPath) is Control body)
            {
                return body;
            }

            // ② 万一具名查找失效（owner 丢失之类），退回**普通相对路径** —— 卡片场景里它就是根的第一个子节点。
            if (card.GetNodeOrNull(BodyChildPath) is Control byPath)
            {
                return byPath;
            }
        }
        catch
        {
            // 走下面的兜底
        }

        return card as Control;
    }

    /// <summary>
    /// ★ 2026-10-10：按**两个可能的位置**找浮层 —— 新位置（<c>%CardContainer</c> 下）优先，
    /// 旧位置（卡片根下）兜底。
    ///
    /// <para><b>为什么必须两处都找</b>：<c>Attach</c> 的幂等早退靠它判断"浮层是否已经建过" ——
    /// 若只认新位置，万一宿主退回卡片根，第二次 <c>_Ready</c> 就会**再建一个 holder**
    /// （用户实测过的症状：图标互相重叠）。</para>
    /// </summary>
    private static Control? FindHolder(Node card)
    {
        try
        {
            if (card.GetNodeOrNull(BodyPath) is Control body && body.GetNodeOrNull(HolderName) is Control inBody)
            {
                return inBody;
            }

            // ★ 普通相对路径兜底（不依赖 owner / 具名解析）—— 建 holder 时挂的就是这个位置。
            if (card.GetNodeOrNull(BodyChildPath + "/" + HolderName) is Control byPath)
            {
                return byPath;
            }

            if (card.GetNodeOrNull(HolderName) is Control onCard)
            {
                return onCard;
            }
        }
        catch
        {
            // 找不到就是没有 —— 调用方按"未挂载"处理
        }

        return null;
    }

    /// <summary>
    /// ★★★ 2026-10-10（Bug 1 = "所有的卡（非莱伊）卡上都有那个兔头"的修复）：
    /// **这张卡是不是本 mod 自己的卡**。
    ///
    /// <para><b>判据（两条，取或）</b>：
    /// <list type="number">
    ///   <item>卡类型的**程序集** == 本文件所在程序集（<c>model.GetType().Assembly == typeof(CardTagIconOverlay).Assembly</c>）
    ///         —— 莱伊的 92 张卡全在 <c>RayMod.Cards</c>、和本文件同一个 <c>RayMod.dll</c>，
    ///         而游戏原版的卡全在 <c>sts2.dll</c> ⇒ **对"本 mod 的所有卡"都成立、对别人的卡一刀切掉**；</item>
    ///   <item>卡类型**命名空间**以 <c>RayMod.</c> 开头（本 mod 的卡 = <c>RayMod.Cards</c>，实测 94 个 .cs 全在
    ///         <c>RayMod.*</c> 下）—— 程序集那条万一被 mod 加载器/源生成器拆包时的冗余保险。</item>
    /// </list></para>
    ///
    /// <para><b>为什么不用 <c>CardTagMap.Tags.ContainsKey(类名)</c></b>：那张表里"没有机制标签的卡"
    /// （打击 <c>RayModStrike</c> / 防御 <c>RayModDefend</c> …）是**空串条目**，
    /// 一旦拿它当"是不是莱伊的卡"的判据，这些卡会连**兔头**一起丢掉 ——
    /// 而兔头是"这是莱伊的卡"的标记，**必须留着**（用户明确要过）。</para>
    ///
    /// <para><b>为什么这两条判据一定和游戏实际显示的模型类型对得上</b>：三枚图标本身是按
    /// <c>model.GetType().Name</c> 查 <c>CardTagMap</c> 点亮的，**实机已经正确显示** ⇒
    /// 证明显示中的 <c>Model</c> 运行时类型就是卡的具体类（不是包装类/代理类），
    /// 所以同样的 <c>GetType()</c> 拿程序集/命名空间**必然**判得出本家。</para>
    ///
    /// <para><b>安全</b>：<c>model == null</c>（对象池回收态，<c>_model = null</c>）⇒ 返回 false ⇒ 兔头隐藏，
    /// 正是想要的。任何异常一律 false（宁可不显示，也不在别人的卡上乱显示）。</para>
    /// </summary>
    private static bool IsRayModCard(CardModel? model)
    {
        if (model == null)
        {
            return false;
        }

        try
        {
            Type t = model.GetType();

            if (t.Assembly == typeof(CardTagIconOverlay).Assembly)
            {
                return true;
            }

            string? ns = t.Namespace;
            return ns != null && ns.StartsWith("RayMod.", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// ★★★ 2026-10-10（Bug 1 修复）：**兔头的可见性跟着【这张卡实例】走**。
    ///
    /// <para>兔头是**无条件添加**的（不查 <c>CardTagMap</c>），所以之前每一张 <c>NCard</c> 上都有它 ——
    /// 而游戏的 <c>NCard</c> 对**所有角色**的卡都用（Harmony 打的是 <c>NCard._Ready</c>）
    /// ⇒ 别人的卡上全长出了兔头。这里补上一句：**不是莱伊的卡 ⇒ 兔头一起隐藏**。</para>
    ///
    /// <para>宿主是 <c>RefreshFor</c>（<c>NCard.Model</c> setter 的 Postfix）—— 卡片是对象池复用的，
    /// 每次 <c>set_Model</c> 都会重跑 ⇒ **同一张卡节点换成别人的卡时兔头也会正确消失**，
    /// 不会留下上一张卡的残留。</para>
    ///
    /// <para>只碰 <c>RoseMarkName</c> 这一个子节点的 <c>Visible</c>：三枚图标的判据一个字没动。</para>
    /// </summary>
    private static void ApplyRose(Control holder, CardModel? model)
    {
        try
        {
            if (holder.GetNodeOrNull(RoseMarkName) is CanvasItem rose)
            {
                rose.Visible = IsRayModCard(model);
            }
        }
        catch (Exception e)
        {
            WarnOnce($"[图标浮层] 兔头可见性刷新失败（只记一次）：{e.GetType().Name}: {e.Message}");
        }
    }

    internal static void Attach(Node card)
    {
        try
        {
            // ★★★ 改动 1（2026-10-10）：**触发 `_Ready` 的到底是哪种节点** —— 这是本轮的关键证据。
            //   一直有个未验证的怀疑：放大查看器可能不是 `NCard` 实例（游戏里有 `NTransformPreview`
            //   / `NUpgradePreview` / `NEnchantPreview` 等预览节点）。如果是这样，用户在放大视图里
            //   看到的"到处乱飞"的图标，其实是网格里那张**小卡**自己画的，和放大视图无关。
            //   这里**每次都打** —— `WarnOnce` 会限流，而限流正好会把关键的那一次吞掉；
            //   只用独立计数器 `_typeDumps` 限制前 6 次，够看清到底有几种类型、各是什么。
            if (_typeDumps < 6)
            {
                _typeDumps++;
                string gpos = card is Control tc
                    ? tc.GlobalPosition.ToString()
                    : card is Node2D t2 ? t2.GlobalPosition.ToString() : "<无>";
                Log.Warn($"[图标诊断] 触发节点 = {card.GetType().FullName} ｜ 是 Control = {card is Control} ｜ gpos = {gpos}");
            }

            // ★ 诊断永远先跑（只读，不影响显示）；浮层本体按 Enabled 决定要不要建。
            //   ⚠ 逐字段的深诊断只在 Dump 内部的第一张卡上跑；布局后的复查交给 DeferLayoutCheck
            //     （只排一次、只打一行），**不再用 CallDeferred 打第二遍多行 dump**。
            Dump(card, "Ready");
            DeferLayoutCheck(card);

            // ★ 幂等：`_Ready` 可能被触发多次 / 节点被重挂；重复建 holder 会让图标互相重叠（用户实测症状之一）。
            //   ★ 2026-10-10：查找改走 FindHolder（**新位置 + 旧位置都找**）—— 宿主从卡片根换成 `%CardContainer`
            //   之后，幂等判断绝不能只认新位置，否则宿主一旦兜底回卡片根就会**重复建 holder**。
            //   ★ 确认（用户点名要核的一条）：这次改动**不会漏刷新** ——
            //     ① 早退只发生在"holder 已存在"时，那种情况下 holder 是上一次 Attach 建的，
            //        而那次 Attach 末尾已经跑过 `Refresh(...)`（当时就按卡刷过一遍）；
            //     ② 之后**每一次换卡**（对象池复用）都走 `NCard.Model` setter 的 Postfix → `RefreshFor`，
            //        那条路径完全不经过 Attach 的早退，**与 holder 的新旧位置无关**。
            if (FindHolder(card) != null)
            {
                return;
            }

            if (!Enabled)
            {
                return;
            }

            Type t = card.GetType();
            PropertyInfo? modelProp = t.GetProperty("Model");

            if (modelProp == null)
            {
                WarnOnce("[图标浮层] NCard 结构不符（缺 Model）—— 跳过");
                return;
            }

            // ★★★ 2026-10-10 第四次修复：**holder 不再挂 OverlayContainer，改挂到"卡面容器"这个祖先上**。
            //   用户实测："**放大某些图就会有图标乱飞的现象**" —— 这是"变换没跟着走"的典型症状：
            //   挂在 `OverlayContainer` 下时，代码**手工把卡片的位置和尺寸抄了过来**，
            //   抄出来的坐标又不像素对齐 ⇒ 卡片一被放大（查看器/悬停预览），偏差同比例放大 → 图标乱飞。
            //   ⇒ 结论：**绝不手工抄变换，让节点自己继承**。
            //
            //   ★★★ 2026-10-10（Bug 2 修复）**第二次调整宿主**：`card.AddChild` → `host.AddChild`，
            //   其中 `host = FindHost(card) = %CardContainer`（= 反编译出来的 `NCard.Body`）。
            //   用户实测："**卡进入卡堆的动画会让卡上的兔头标识和那三个标识飞出卡面**" ——
            //   根因（反编译确证，`NCardFlyVfx.cs:282-283` 与 `:309`）：
            //       动画缩的是 `_card.Body.Scale`（1 → 0.1 → 0）并把 `Body.Modulate` 压黑，
            //       **动的是 `%CardContainer` 这个子节点，不是 `NCard` 自己**；
            //       浮层原先挂在 `NCard` 根上 = `%CardContainer` 的**兄弟** ⇒ 卡面缩到 10%/0、
            //       浮层仍按原尺寸留在原地 ⇒ 四个标记看上去"飞出卡面"。
            //   挂进 `%CardContainer` 后：**卡面缩多少、浮层缩多少（一路跟到 0 = 自然消失）**，
            //   且 `Body.Modulate` 的压黑也会一起继承 —— 与卡面完全同步，**不需要任何"动画中"检测/每帧轮询**。
            //   ⚠️ 换宿主**不改任何坐标**：`%CardContainer` 实测在卡片局部 (0,0)、scale (1,1)（见 BodyPath 注释），
            //      所以下面 `Position = (-CardHalfW, -CardHalfH)` 与那套"贴图系 ↔ holder 内"换算**原样成立**。
            //   ⚠️ `ZIndex` 仍然必须是 0：`z_as_relative` 默认 true ⇒ 最终 z = 自己 0 + 父级 0 = 0，两种宿主完全等价。
            //   ⚠️ `ClipContents` 仍然必须是 false：见下面的理由（**与 Bug 2 无关，别把它改回 true** ——
            //      clip 只裁 holder 自己那 300x422 的矩形，动画期间卡面缩小时这个矩形**并不会跟着缩**，
            //      所以改成 true 既修不了 Bug 2、又会把"图标探出卡牌右缘"的效果裁掉）。
            Control? host = FindHost(card);
            if (host == null)
            {
                WarnOnce("[图标浮层] 找不到宿主容器（%CardContainer 与卡片根都拿不到）—— 跳过");
                return;
            }

            var holder = new Control
            {
                Name = HolderName,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                //   ★ 必须是 0：放大查看器 `NInspectCardScreen`（挂 `%InspectionContainer`，在 `game.tscn` 里排在
                //     `RootSceneContainer` 之后）整条链 z_index 都是 0，**靠树序**盖住网格卡。我们的 holder 是
                //     `host.AddChild` 的最后一个子节点，z=0 时按树序仍然画在 `%Frame`/卡图之上，视觉效果不变；
                //     而放大卡（z 同样是 0、树序更后）能正常盖住网格卡的图标。
                //   ⚠️ 不要用 1：战斗中手牌 `NHandCardHolder` 用 `ZIndex = isHovered ? 1 : 0`，
                //     会叠加成 2 导致手牌悬停时层序意外。
                //   （上一版是 4096 —— 见上方 Enabled 的说明：z 高会穿透放大查看器。）
                ZIndex = 0,
                // ★ 卡片局部原点在【中心】，所以是负偏移。
                //   ★ 2026-10-10：宿主若不在卡片局部原点（实测 (0,0)），这里把它的偏移**预先减掉**，
                //     保证浮层的**全局落点与挂卡片根时逐像素一致**（实测 0 ⇒ 本行是空操作，换算一字未改）。
                Position = new Vector2(-CardHalfW, -CardHalfH) - host.Position,
                Size = new Vector2(CardHalfW * 2f, CardHalfH * 2f),
                ClipContents = false,       // ★ 唯一的例外：卡片上其它装饰都裁，只有这组图标故意不裁 ——
                                            //   三枚图标要"超出卡牌右缘"（IconLeft=285 + 30 宽 → 到 holder 内 315，
                                            //   即贴图系 628 > 598），ClipContents=true 会把超出 300 宽的部分裁掉。
            };
            host.AddChild(holder);

            var rects = new TextureRect[Icons.Length];
            for (int i = 0; i < Icons.Length; i++)
            {
                var r = new TextureRect
                {
                    Name = "Tag_" + Icons[i].Letter,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    Visible = false,
                    Position = new Vector2(IconLeft, TopInset + i * SlotStep),
                    Size = new Vector2(IconSize, IconSize),
                    PivotOffset = new Vector2(IconSize * 0.5f, IconSize * 0.5f),
                    Rotation = Mathf.DegToRad(TiltDeg),
                };
                r.Texture = ResourceLoader.Load<Texture2D>($"{PowerDir}/{Icons[i].File}", null,
                    ResourceLoader.CacheMode.Reuse);
                holder.AddChild(r);
                rects[i] = r;
            }

            // ★★★ 2026-10-10 追加（A 件）：玫瑰粉**兔子** `RoseMark` —— **只属于莱伊的元素**。
            //   ★ 形态 = **从用户给的参考图里提取的那只兔子**（不是重画，见上方 RoseMarkPath 段落）。
            //   ★★★ 2026-10-10（Bug 1 修复）：兔头**不再"永远为 true"** —— 它的可见性由
            //      `ApplyRose`（判据 `IsRayModCard`）决定，**只对莱伊自己的卡显示**。
            //      所以这里建的时候**先给 false**：万一后面任何一步抛异常，也不会在别人的卡上留一只兔头。
            //      （真正点亮在 Attach 末尾 / `RefreshFor` 里的 `ApplyRose`。）
            //   ⚠️ 它**不进 `rects` 数组**、也不参与 `CardTagMap` 标签判据 —— 三枚图标那套逻辑一个字没动。
            //   ⚠️ 全部 try/catch：贴图缺失 / 路径不对 ⇒ 只记一次日志，**绝不影响游戏**。
            //   ⚠️ 不要在这里改 `Enabled` / `ZIndex` / `ClipContents` / 三枚图标的位置与贴图。
            try
            {
                var rose = new TextureRect
                {
                    Name = RoseMarkName,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    Visible = false,   // ★ Bug 1 修复：默认隐藏，由 ApplyRose 按"是不是莱伊的卡"点亮
                    Size = new Vector2(RoseW, RoseH),
                    // 中心对齐到 (RoseNodeX, RoseNodeY)：Position 是左上角，所以各自减半边长。
                    // ★ 非正方形 ⇒ **两轴分别减半**（RoseW/2、RoseH/2），别再用同一个数。
                    Position = new Vector2(RoseNodeX - RoseW * 0.5f, RoseNodeY - RoseH * 0.5f),
                    // ★ 贴图里画的**就是那只兔子**（耳朵朝上、头在左下），**不要再旋转任何角度**——
                    //   两版前那颗正菱形曾经踩过"多转 45° 就变成正方形"的坑（用户明确提醒过）。
                    Rotation = 0f,
                };
                rose.Texture = ResourceLoader.Load<Texture2D>(RoseMarkPath, null,
                    ResourceLoader.CacheMode.Reuse);
                holder.AddChild(rose);

                if (rose.Texture == null)
                {
                    WarnOnce($"[图标浮层] RoseMark 贴图没加载到（{RoseMarkPath}）—— 只有兔子标记缺失");
                }
            }
            catch (Exception e)
            {
                WarnOnce($"[图标浮层] RoseMark 失败（只记一次）：{e.GetType().Name}: {e.Message}");
            }

            // ★ 2026-10-10（Bug 1）：宿主一并传进去 —— Refresh 里除了三枚图标，现在还要按
            //   "是不是莱伊的卡"点亮/隐藏兔头（`ApplyRose`），两条路径共用同一个入口，不会走岔。
            Refresh(rects, modelProp.GetValue(card) as CardModel, holder);

            // 2026-10-10：已改为用 `NCard.Model` 的 setter Postfix 刷新（见 `RefreshFor`）——
            // 事件订阅会被对象池回收清空（`NCard.cs:1502` `ModelChanged = null;`），不可靠，故移除。
        }
        catch (Exception e)
        {
            // 浮层失败绝不能影响游戏（同"美术失败不影响结算"的思路）
            WarnOnce($"[图标浮层] 失败（只记一次）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// ★ 2026-10-10：按【卡片实例】刷新图标 —— 由 <c>NCard.Model</c> 属性 setter 的 Postfix 调用。
    ///
    /// <para>为什么不再依赖 <c>ModelChanged</c> 事件：游戏在对象池回收时会把事件订阅清空
    /// （<c>NCard.cs:1502</c> <c>ModelChanged = null;</c>），而 Godot 节点离开再回到场景树时
    /// <c>_Ready</c> 不会重跑 ⇒ 池化到位的手牌/弃牌卡图标会永久冻结。</para>
    ///
    /// <para>属性 setter 每次模型变化都会被调用，且 Postfix 拿到的就是**新模型**（事件参数传的是旧模型，
    /// 见 <c>NCard.cs:657</c>）。所以这条路径天然避开两个坑。</para>
    /// </summary>
    internal static void RefreshFor(Node card)
    {
        try
        {
            // ★ 2026-10-10：查找走 FindHolder（新位置 `%CardContainer` 下优先、卡片根兜底）。
            Control? holder = FindHolder(card);

            if (holder == null)
            {
                return;   // 浮层还没挂（_Ready 尚未跑）或没启用 —— 什么都不做
            }

            // 回读属性而不是用事件参数：setter 里 _model 已更新，属性即当前卡。
            PropertyInfo? modelProp = card.GetType().GetProperty("Model");
            CardModel? model = modelProp?.GetValue(card) as CardModel;

            // 按 holder 下的具名子节点找图标（Tag_d / Tag_a / Tag_b），顺序无关，避免误配贴图。
            TextureRect?[] rects = new TextureRect?[Icons.Length];
            bool any = false;
            for (int i = 0; i < Icons.Length; i++)
            {
                rects[i] = holder.GetNodeOrNull("Tag_" + Icons[i].Letter) as TextureRect;
                any |= rects[i] != null;
            }

            // ★★★ 2026-10-10（Bug 1 修复）：**兔头按"这张卡是不是莱伊的"点亮/隐藏**。
            //   放在 `!any` 早退**之前**：万一某张卡只建出了 holder 而三枚图标不全（建到一半失败），
            //   兔头也必须跟着这张卡刷新，不能留着上一张卡的状态。
            //   ★ 这条路径 = `NCard.Model` setter 的 Postfix ⇒ **每次换卡（对象池复用）都会重跑**，
            //     同一张卡节点被换成别人的卡时兔头会自动消失。
            ApplyRose(holder, model);

            if (!any)
            {
                return;
            }

            // ★★ 2026-10-15 撞名门：`CardTagMap` 按**简单类名**查，而原版有同名卡
            //    （铁甲战士 `Feed` / 静默猎手 `Darkness`）⇒ 不挡的话，玩别的角色时
            //    那两张**原版**牌的卡面上会长出莱伊的机制图标（❌ 别人的卡上出现我们的标签）。
            //    判据统一在 `Cards\CardIdentity.cs`。
            string? mapped = Cards.CardIdentity.NameForMapLookup(model);

            string tags = mapped != null && CardTagMap.Tags.TryGetValue(mapped, out string? s)
                ? s
                : string.Empty;

            // ★ 2026-10-10：一次性诊断 —— 把"类名 → 当前标签组合 → 中文名"打进 godot.log。
            //   只加日志：下面的 Visible 逻辑一个字没动。
            DumpTagRow(model, tags);

            for (int i = 0; i < rects.Length; i++)
            {
                if (rects[i] != null)
                {
                    rects[i]!.Visible = tags.Contains(Icons[i].Letter, StringComparison.Ordinal);
                }
            }
        }
        catch (Exception e)
        {
            // 美术失败绝不能影响游戏（同全项目铁律）
            WarnOnce($"[图标浮层] 按卡刷新失败（只记一次）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// ★★★ 2026-10-10 一次性诊断：**每个卡牌类名打一行"当前显示的机制图标组合"**，写进 <c>godot.log</c>。
    ///
    /// <para><b>为什么需要</b>：用户实机反馈"图标刷新好了，但依旧存在属性不对应"。
    /// 让用户回忆 92 张卡不现实 —— 改成让日志把"类名 → 标签组合 → 中文名"列出来，
    /// 用户扫一眼报出不对的卡名即可定位是 <c>CardTagMap.g.cs</c> 的哪个条目错了。</para>
    ///
    /// <para><b>格式</b>（逐字，**单行**）：<c>[图标表] 类名=Infinite 标签=a 中文名=无限</c>。
    /// 单行是硬要求 —— 本项目踩过"多行字符串进不了这款游戏的日志"的坑（见 <see cref="Dump"/>）。</para>
    ///
    /// <para><b>中文名从哪来</b>：不问"类名 → 本地化键"的规律（那是猜），而是**直接走游戏自己的解析路径** ——
    /// <c>CardModel.TitleLocString</c> 就是 <c>new LocString("cards", Id.Entry + ".title")</c>
    /// （反编译 <c>CardModel.cs:108</c>），所以 <c>model.Id.Entry</c> 必定就是本地化键前缀，
    /// 再用 <c>LocString.GetIfExists</c> + <c>GetRawText()</c> 取中文。取不到就降级打 <c>?</c> 并补上键名。</para>
    ///
    /// <para><b>限量</b>：同一类名只打一次（<see cref="TagDumped"/>），总条数上限 120（<see cref="_tagDumps"/>）。</para>
    ///
    /// <para><b>安全</b>：只读、只写日志 —— 不动任何节点、不碰 <c>Visible</c>；
    /// 任何异常都被吞掉（最多记一次），**绝不影响游戏**。</para>
    /// </summary>
    private static void DumpTagRow(CardModel? model, string tags)
    {
        try
        {
            if (_tagDumps >= TagDumpLimit)
            {
                return;
            }

            string cls = model == null ? "<null>" : model.GetType().Name;

            // ★ 去重：手牌/弃牌堆是对象池，同一张卡会被反复设 Model，不去重会刷屏。
            if (!TagDumped.Add(cls))
            {
                return;
            }

            _tagDumps++;

            string shown = tags.Length == 0 ? "(无)" : tags;

            // ★ 2026-10-10（Bug 1）：多打一个字段 `兔头=是/否` —— 这是"兔头只对莱伊的卡显示"的**实机证据**：
            //   扫日志时应当看到 **莱伊的 92 张卡（含打击/防御这种"标签=(无)"的）全是 兔头=是**，
            //   而别人的卡（标签一定是 (无)）全是 **兔头=否**。改这行的依据：全仓库没有任何脚本解析这几行日志。
            string rose = IsRayModCard(model) ? "是" : "否";

            if (model == null)
            {
                Log.Warn($"[图标表] 类名=<null> 标签={shown} 兔头={rose} 中文名=?");
                return;
            }

            // Id.Entry 就是本地化键前缀（形如 RAY_MOD_CARD_INFINITE），键 = Entry + ".title"。
            string locKey = model.Id.Entry;
            string title = "?";

            try
            {
                // GetRawText 取 JSON 原文（卡名没有占位符，不需要 SmartFormat）；表/键不存在时它会抛，所以先 GetIfExists。
                title = LocString.GetIfExists("cards", locKey + ".title")?.GetRawText() ?? "?";
            }
            catch
            {
                title = "?";   // 本地化还没就绪 / 表缺失 —— 降级，不影响下面这行日志
            }

            if (string.IsNullOrEmpty(title))
            {
                title = "?";
            }

            // ★ 单行！取不到中文名时补打键名 —— 键名本身就是"该去哪儿找这个名字"的答案。
            Log.Warn(title == "?"
                ? $"[图标表] 类名={cls} 标签={shown} 兔头={rose} 中文名=? 键={locKey}"
                : $"[图标表] 类名={cls} 标签={shown} 兔头={rose} 中文名={title}");
        }
        catch (Exception e)
        {
            WarnOnce($"[图标表] 诊断失败（只记一次）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>按卡牌的机制标签点亮/熄灭图标（★ 2026-10-10：外加兔头 —— 见 <see cref="ApplyRose"/>）。</summary>
    private static void Refresh(TextureRect[] rects, CardModel? model, Control holder)
    {
        try
        {
            // ★ 2026-10-10（Bug 1）：兔头与三枚图标**同一个入口**刷新 —— 二者的判据各自独立：
            //   三枚看 `CardTagMap` 的机制标签（一个字没动），兔头看"是不是莱伊的卡"（`IsRayModCard`）。
            ApplyRose(holder, model);

            // ★★ 2026-10-15 撞名门：同上 —— 只认我们自己命名空间下的卡。
            string? mapped = Cards.CardIdentity.NameForMapLookup(model);

            string tags = mapped != null && CardTagMap.Tags.TryGetValue(mapped, out string? s)
                ? s
                : string.Empty;

            for (int i = 0; i < rects.Length; i++)
            {
                rects[i].Visible = tags.Contains(Icons[i].Letter, StringComparison.Ordinal);
            }
        }
        catch (Exception e)
        {
            WarnOnce($"[图标浮层] 刷新失败（只记一次）：{e.GetType().Name}: {e.Message}");
        }
    }

    private static void WarnOnce(string message)
    {
        if (_logged)
        {
            return;
        }

        _logged = true;
        Log.Warn(message);
    }
}
