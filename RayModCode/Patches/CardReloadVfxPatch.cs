using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace RayMod.Patches;

/// <summary>
/// <b>「填装形态」卡牌动效的游戏入口解析</b>（2026-10-19）—— 全部按<b>类型名软解析</b>，
/// 找不到就整个补丁不挂（游戏改版时只是失去这段演出，不会把模组带崩）。
///
/// <para><b>★ 为什么是两个入口而不是一个</b>（本轮开工前查出来的，和方案 §4 的猜测相反）：
/// <list type="bullet">
///   <item><b>弃牌</b>：<c>CardPileCmd.GetTweenForCardsChangingPiles</c> 的默认分支
///         → <c>NCardFlyVfx.Create(card, pileType, isAddingToPile: true, trailPath)</c>
///         → <c>_Ready</c> 里 <c>TaskHelper.RunSafely(PlayAnim())</c>。</item>
///   <item><b>抽牌</b>：<c>case PileType.Hand:</c> 里 <c>AppendPileLerpTween</c>
///         <b>对 Hand 直接跳过位置补间</b>；真正的位移是 <c>NPlayerHand.Add(card)</c> 之后
///         由 <c>NHandCardHolder.AnimPosition()</c> 做的指数 lerp（速率 7/s）。
///         ⇒ 抽牌<b>根本不经过 <c>NCardFlyVfx</c></b>。</item>
/// </list></para>
///
/// <para><b>★ 为什么不接管 <c>PlayAnim</c> 协程</b>：它身上还挂着三件<b>必须兑现</b>的约定 ——
/// ① <c>SwooshAwayCompletion</c>（<c>CardCmd.cs:857</c> 在 <c>AddChildSafely</c> 之后<b>立刻</b>读它，
/// 不设就是永久 await）；② <c>_isAddingToPile</c> 时的 <c>Pile.InvokeCardAddFinished()</c>；
/// ③ 收尾的 <c>_card.QueueFreeSafely()</c>。
/// 我们只改<b>它读的那几个私有字段</b>（终点/拱高/时长/速度）＋事后叠自旋和弹壳，
/// 协程照常跑 ⇒ 三条约定一条都不用重写。</para>
///
/// <para><b>★ 为什么改字段放在 <c>_Ready</c> 的 Postfix 还来得及</b>：<c>_Ready</c> 末尾是
/// <c>TaskHelper.RunSafely(PlayAnim())</c>，而 <c>PlayAnim</c> 是 async —— 它同步跑到
/// 第一个 <c>await this.AwaitProcessFrame()</c> 就把控制权交回来了。而
/// <c>_speed</c>/<c>_accel</c>/<c>_arcDir</c>/<c>_duration</c>/<c>_endPos</c>
/// 全是在那个 await <b>之后</b>的循环体里读的 ⇒ Postfix 写进去的值从第一帧起就生效。
/// 另外 <c>_Ready</c> 的 Postfix 里节点<b>已经在树上</b>，才拿得到 viewport
/// （<c>Create</c> 的返回值那时还没进树，<c>GetViewport()</c> 是 null —— 这是个坑）。</para>
/// </summary>
internal static class CardReloadVfxTargets
{
    private const string FlyTypeName = "MegaCrit.Sts2.Core.Nodes.Vfx.NCardFlyVfx";
    private const string HandTypeName = "MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand";
    private const string MouseCardPlayTypeName = "MegaCrit.Sts2.Core.Nodes.Combat.NMouseCardPlay";

    internal static readonly MegaCrit.Sts2.Core.Logging.Logger Log =
        new("RayMod.ReloadForm", MegaCrit.Sts2.Core.Logging.LogType.Generic);

    /// <summary>等 <c>_Ready</c> 处理：哪些 fly 节点在 <c>Create</c> 时被认出是"弃牌"。</summary>
    internal static readonly HashSet<ulong> Pending = new();

    internal static Type? FlyType;

    internal static MethodBase? CreateFour;
    internal static MethodBase? Ready;
    internal static MethodBase? HandAdd;

    internal static FieldInfo? FCard;
    internal static FieldInfo? FStart;
    internal static FieldInfo? FEnd;
    internal static FieldInfo? FDuration;
    internal static FieldInfo? FSpeed;
    internal static FieldInfo? FAccel;
    internal static FieldInfo? FArcDir;
    internal static FieldInfo? FTrailPath;        // NCardFlyVfx._trailPath（★★ 卡牌拖尾的关键）

    internal static PropertyInfo? PCardNode;          // NCardHolder.CardNode
    internal static PropertyInfo? PTargetPosition;    // NHandCardHolder.TargetPosition

    /// <summary>
    /// <c>NHandCardHolder.AnimPosition(CancellationTokenSource)</c> —— <b>私有 async</b>。
    /// 手牌位移就是它驱动的（指数 lerp，速率 7/s）。
    /// 我们做螺旋时必须在这一张牌上把它<b>关掉</b>，否则它是个一阶低通，
    /// 会把螺旋的横向摆动整个滤平（实测：0.46s 一圈的切向速度 ≈1600px/s，
    /// 经过 7/s 的低通后振幅只剩一半左右、相位还被扭转 ⇒ <b>看不出螺旋</b>）。
    /// </summary>
    internal static MethodBase? HolderAnimPosition;

    /// <summary>
    /// <c>NMouseCardPlay.LerpToMouse(NHandCardHolder)</c> —— <b>拖拽期间每帧跑一次</b>的那个方法。
    /// <para>挂它做「拖动卡牌时线框留残影」（<see cref="RayMod.Art.CardFieldVfx.SpawnDragGhost"/>）。</para>
    /// <para>★ 它是 <c>async Task</c>，编译成状态机 ⇒ <b>不会被 JIT 内联</b>，
    /// 所以不受铁律 73 影响。这是本轮挑它的主要理由 ——
    /// 相比之下 <c>NCardFlyVfx.Create</c> 那种 12 行的小同步方法就被内联过（日志里出现过"走兜底认路"）。</para>
    /// </summary>
    internal static MethodBase? DragLerp;

    /// <summary>手牌 holder 的类型名（<c>Add</c> 的返回类型解析失败时的兜底）。</summary>
    private const string HolderTypeName = "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder";

    /// <summary>弃牌的兜底判据：<c>_card.Model.Pile.Type</c> 是不是 Discard。</summary>
    internal static PropertyInfo? PModel;             // NCard.Model
    internal static PropertyInfo? PPile;              // CardModel.Pile
    internal static PropertyInfo? PPileType;          // CardPile.Type

    /// <summary>
    /// <c>_Ready</c> 里判断"这张卡的目标堆是不是弃牌堆"。
    /// ★ 这是 <c>Create</c> 补丁的**兜底**：如果 <c>Create</c> 被 JIT 内联掉了（铁律 73），
    /// <c>Pending</c> 永远是空的，但这条判据仍然成立 ⇒ 退壳照样生效。
    /// </summary>
    internal static bool CardTargetsDiscard(object instance)
    {
        try
        {
            object? card = FCard?.GetValue(instance);

            if (card == null)
            {
                return false;
            }

            PModel ??= card.GetType().GetProperty("Model");

            object? model = PModel?.GetValue(card);

            if (model == null)
            {
                return false;
            }

            PPile ??= model.GetType().GetProperty("Pile");

            object? pile = PPile.GetValue(model);

            if (pile == null)
            {
                return false;
            }

            PPileType ??= pile.GetType().GetProperty("Type");

            return PPileType?.GetValue(pile)?.ToString() == "Discard";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启动时探一下拖尾场景能不能加载（不能的话卡牌飞行会静默没有动画）。</summary>
    private static bool LoadTrailSceneProbe()
    {
        try
        {
            var scene = Godot.ResourceLoader.Load<Godot.PackedScene>(
                RayMod.Art.CardFieldVfx.RayCardTrailPath);

            return scene != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 解析「拖拽残影」那一个入口（<c>NMouseCardPlay.LerpToMouse</c>）。
    /// <para>★ 单独一条、单独 try —— 它解析失败只应该让拖拽残影不挂，
    /// <b>绝不能连累</b>退壳/上膛（那三条才是「填装形态」的主体）。</para>
    /// </summary>
    private static void ResolveDrag()
    {
        try
        {
            // CardNode 属性：正常由 NPlayerHand.Add 的返回类型拿到，这里给一份独立兜底。
            if (PCardNode == null)
            {
                Type? holderType = HarmonyLib.AccessTools.TypeByName(HolderTypeName);

                if (holderType != null)
                {
                    PCardNode = HarmonyLib.AccessTools.Property(holderType, "CardNode");
                }
            }

            Type? mouseType = HarmonyLib.AccessTools.TypeByName(MouseCardPlayTypeName);

            if (mouseType == null)
            {
                Log.Info("[拖拽残影] 找不到 NMouseCardPlay ⇒ 这个功能不挂（游戏改版？其余不受影响）");
                return;
            }

            foreach (MethodBase m in HarmonyLib.AccessTools.GetDeclaredMethods(mouseType))
            {
                if (m.Name != "LerpToMouse")
                {
                    continue;
                }

                ParameterInfo[] ps = m.GetParameters();

                // LerpToMouse(NHandCardHolder) —— 按【参数类型名】挑，不按名字硬找
                // （同名重载会抛 AmbiguousMatchException，交接文档 §2.4 的坑）。
                if (ps.Length == 1 && ps[0].ParameterType.Name == "NHandCardHolder")
                {
                    DragLerp = m;
                    break;
                }
            }

            Log.Info($"[拖拽残影] 软解析：LerpToMouse={DragLerp != null} "
                     + $"CardNode={PCardNode != null} ⇒ 挂载={DragLerp != null && PCardNode != null}");
        }
        catch (Exception e)
        {
            DragLerp = null;
            Log.Warn($"[拖拽残影] 软解析失败（这个功能不挂，其余不受影响）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>前 <paramref name="limit"/> 次打印，之后静音。</summary>
    internal static void Diag(string key, string message, int limit = 6)
    {
        DiagCounts.TryGetValue(key, out int n);

        if (n >= limit)
        {
            return;
        }

        DiagCounts[key] = n + 1;
        Log.Info($"[填装形态·诊断] {message}（{key} {n + 1}/{limit}）");
    }

    private static readonly Dictionary<string, int> DiagCounts = new();

    private static bool _resolved;
    private static bool _ok;

    /// <summary>解析一次，之后复用。<b>任何一步失败都返回 false ⇒ 补丁不挂。</b></summary>
    internal static bool Resolve()
    {
        if (_resolved)
        {
            return _ok;
        }

        _resolved = true;

        try
        {
            // ★ 先解析拖拽入口（和填装形态无关，独立成一条 —— 失败也不该拖累下面的退壳/上膛）。
            ResolveDrag();

            FlyType = HarmonyLib.AccessTools.TypeByName(FlyTypeName);

            if (FlyType == null)
            {
                Log.Info("[填装形态] 找不到 NCardFlyVfx ⇒ 退壳/上膛都不挂（游戏改版？）");
                return false;
            }

            // Create 有两个重载（4 参 / 3 参）。★ 按【参数个数 + 最后一个是 string】挑，
            // 不能按名字挑 —— 同名重载会抛 AmbiguousMatchException（交接文档 §2.4 的坑）。
            foreach (MethodBase m in HarmonyLib.AccessTools.GetDeclaredMethods(FlyType))
            {
                if (!m.IsStatic || m.Name != "Create")
                {
                    continue;
                }

                ParameterInfo[] ps = m.GetParameters();

                if (ps.Length == 4 && ps[3].ParameterType == typeof(string))
                {
                    CreateFour = m;
                    break;
                }
            }

            Ready = HarmonyLib.AccessTools.Method(FlyType, "_Ready", Type.EmptyTypes);

            FCard = HarmonyLib.AccessTools.Field(FlyType, "_card");
            FStart = HarmonyLib.AccessTools.Field(FlyType, "_startPos");
            FEnd = HarmonyLib.AccessTools.Field(FlyType, "_endPos");
            FDuration = HarmonyLib.AccessTools.Field(FlyType, "_duration");
            FSpeed = HarmonyLib.AccessTools.Field(FlyType, "_speed");
            FAccel = HarmonyLib.AccessTools.Field(FlyType, "_accel");
            FArcDir = HarmonyLib.AccessTools.Field(FlyType, "_arcDir");
            FTrailPath = HarmonyLib.AccessTools.Field(FlyType, "_trailPath");

            // ── 抽牌侧 ──
            Type? handType = HarmonyLib.AccessTools.TypeByName(HandTypeName);

            if (handType != null)
            {
                foreach (MethodBase m in HarmonyLib.AccessTools.GetDeclaredMethods(handType))
                {
                    if (m.Name != "Add")
                    {
                        continue;
                    }

                    ParameterInfo[] ps = m.GetParameters();

                    // Add(NCard card, int index = -1)
                    if (ps.Length == 2 && ps[0].ParameterType.Name == "NCard" && ps[1].ParameterType == typeof(int))
                    {
                        HandAdd = m;

                        Type? holderType = m is MethodInfo mi ? mi.ReturnType : null;

                        if (holderType != null)
                        {
                            PTargetPosition = HarmonyLib.AccessTools.Property(holderType, "TargetPosition");
                            PCardNode = HarmonyLib.AccessTools.Property(holderType, "CardNode");

                            // AnimPosition(CancellationTokenSource) —— 按【参数类型名】挑，
                            // 不按名字硬找（同名重载会抛 AmbiguousMatchException，交接文档 §2.4）。
                            for (Type? cur = holderType; cur != null && HolderAnimPosition == null; cur = cur.BaseType)
                            {
                                foreach (MethodBase mb in cur.GetMethods(
                                             BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                                {
                                    ParameterInfo[] aps = mb.GetParameters();

                                    if (mb.Name == "AnimPosition" && aps.Length == 1
                                        && aps[0].ParameterType.Name.Contains("CancellationTokenSource"))
                                    {
                                        HolderAnimPosition = mb;
                                        break;
                                    }
                                }
                            }
                        }

                        break;
                    }
                }
            }

            _ok = CreateFour != null && Ready != null
                  && FCard != null && FStart != null && FEnd != null
                  && FDuration != null && FSpeed != null && FAccel != null && FArcDir != null
                  && HandAdd != null && PTargetPosition != null && PCardNode != null;
            Log.Info("[填装形态] 软解析："
                     + $"Create4={CreateFour != null} _Ready={Ready != null} "
                     + $"字段 end/arc/dur/speed/accel={FEnd != null}/{FArcDir != null}/{FDuration != null}/{FSpeed != null}/{FAccel != null} "
                     + $"HandAdd={HandAdd != null} TargetPosition={PTargetPosition != null} CardNode={PCardNode != null} "
                     + $"AnimPosition={HolderAnimPosition != null} DragLerp={DragLerp != null} "
                     + $"⇒ 挂载={_ok}");

            // ★ 卡牌拖尾场景自检（**和填装形态无关，是整个 mod 的卡牌飞行**）：
            //   这个场景加载不了的话，NCardTrailVfx.Create 就会抛 NRE，
            //   表现是"卡牌飞行没有动画"，而日志里只有一行 Asset not cached —— 极难排查。
            Log.Info($"[填装形态] _trailPath 字段={(FTrailPath != null ? "有" : "❌没有")}，"
                     + $"拖尾场景 {RayMod.Art.CardFieldVfx.RayCardTrailPath} = "
                     + (LoadTrailSceneProbe() ? "✅ 可加载" : "❌ 加载失败（卡牌飞行会没有动画）"));
        }
        catch (Exception e)
        {
            Log.Warn($"[填装形态] 软解析失败（退壳/上膛都不挂）：{e.GetType().Name}: {e.Message}");
            _ok = false;
        }

        return _ok;
    }
}

/// <summary>
/// <b>A-1 ①</b>：<c>NCardFlyVfx.Create(NCard, PileType, bool, string)</c> 的 Postfix ——
/// <b>只做认路</b>：把"目标是弃牌堆"的那几次登记下来，交给 <see cref="CardEjectTimingPatch"/> 处理。
///
/// <para>⚠️ 这里<b>不碰任何坐标</b>：<c>Create</c> 的返回值此刻还没进场景树，
/// <c>GetViewport()</c> 会返回 null，算不了屏幕尺寸。</para>
/// </summary>
[HarmonyPatch]
internal static class CardEjectClassifyPatch
{
    [HarmonyPrepare]
    private static bool Prepare() => CardReloadVfxTargets.Resolve();

    [HarmonyTargetMethod]
    private static MethodBase? Target() => CardReloadVfxTargets.CreateFour;

    [HarmonyPostfix]
    private static void Postfix(object __result, object __0, object __1, object __3)
    {
        try
        {
            if (__result is not Node node)
            {
                return;
            }

            // ★★★ 卡牌拖尾修正（**和「填装形态」无关，这是给整个 mod 补的**）
            //
            //  原版每个角色都有一份 res://scenes/vfx/card_trail_<角色id>.tscn；
            //  本 mod 的角色没有 ⇒ NCardTrailVfx.Create 拿到 null 直接抛 NRE ⇒
            //  NCardFlyVfx._Ready 中断 ⇒ PlayAnim 根本不跑 ⇒ 卡牌飞行"没动画"。
            //  这里把私有字段 _trailPath 改指到我们自带的那份（只对本地莱伊的卡）。
            ApplyRayCardTrail(__result, __0, __3);

            CardReloadVfxTargets.Diag("create", $"NCardFlyVfx.Create 被调到：pile={__1}");

            // PileType 是游戏枚举 —— 按【名字】比而不是按序数比，改序号也不会错。
            if (__1?.ToString() != "Discard")
            {
                return;
            }

            if (!RayMod.Art.CardFieldVfx.ShouldEject())
            {
                return;
            }

            // 登记表理论上很短（每次 Create 后紧接着就 _Ready），加个上限纯属保险。
            if (CardReloadVfxTargets.Pending.Count > 64)
            {
                CardReloadVfxTargets.Pending.Clear();
            }

            CardReloadVfxTargets.Pending.Add(node.GetInstanceId());
        }
        catch
        {
            // 表现层失败绝不外抛
        }
    }

    /// <summary>
    /// ★★ <b>2026-10-19 已停用</b>：把 <c>_trailPath</c> 换成 <c>res://RayMod/vfx/card_trail_ray.tscn</c> 这条路
    /// <b>是错的，反而造成了回归</b>。
    ///
    /// <para><b>为什么错</b>：游戏的拖尾场景是靠
    /// <c>[ext_resource type="Script" path="res://src/.../NCardTrailVfx.cs"]</c>
    /// 引用<b>游戏程序集</b>里的脚本挂上去的。我们从自己的 pck 写同样路径的引用时，
    /// <c>Instantiate&lt;NCardTrailVfx&gt;()</c> 只能拿到纯 <c>Node2D</c>（脚本没挂上）⇒ 转换失败
    /// ⇒ <b>返回 null</b> ⇒ <c>NCardFlyVfx._Ready</c> 的 <c>if (_vfx != null)</c> 直接跳过拖尾。
    /// <b>既不报错、也没有拖尾</b> —— 录制验证过：帧里只有裸卡在飞。</para>
    ///
    /// <para>而且在换之前，莱伊借的是 <c>res://scenes/vfx/card_trail_ironclad.tscn</c>
    /// （日志实证），<b>那份本来是好的</b>。我这一换等于<b>把能用的拖尾换成了没有</b>。</para>
    ///
    /// <para>⇒ 现在：<b>不碰游戏的 trail 路径</b>（让铁甲战士那份继续生效），
    /// 额外用 <see cref="RayMod.Art.CardFlyTrail"/> 自己画一套残影 + 光带叠上去。</para>
    /// </summary>
    private const bool UseVanillaTrailScene = true;

    /// <summary>
    /// 拖尾路径替换（默认停用，见 <see cref="UseVanillaTrailScene"/>）。
    /// </summary>
    private static void ApplyRayCardTrail(object flyNode, object card, object originalTrailPath)
    {
        if (UseVanillaTrailScene)
        {
            return;      // 保持原样：沿用角色自己的（莱伊 = 借铁甲战士那份）
        }

        try
        {
            if (CardReloadVfxTargets.FTrailPath == null)
            {
                return;
            }

            if (!RayMod.Art.CardFieldVfx.IsMyCard(card))
            {
                return;
            }

            string ours = RayMod.Art.CardFieldVfx.RayCardTrailPath;

            CardReloadVfxTargets.Diag("trail",
                $"卡牌拖尾替换：原路径='{originalTrailPath}' → '{ours}'");

            CardReloadVfxTargets.FTrailPath.SetValue(flyNode, ours);
        }
        catch
        {
            // 换不了就沿用原路径，不影响别的
        }
    }
}

/// <summary>
/// <b>卡牌飞行的自绘拖尾</b>：给本地莱伊的每张飞行卡挂一个
/// <see cref="RayMod.Art.CardFlyTrail"/>（残影链 + 光带 + 起飞火星）。
///
/// <para>挂在 <c>NCardFlyVfx._Ready</c> 的 <b>Postfix</b> 上 —— 此时节点已在场景树里、
/// <c>_card</c> 字段已就位，可以安全读 <c>GlobalPosition</c>。</para>
///
/// <para>⚠️ 本补丁<b>只加东西</b>，不改 <c>PlayAnim</c> 读的任何字段（这是上一次翻车的根源：
/// 动轨迹/时序会让卡"乱转、半路缩成小点、飞出画面"）。</para>
/// </summary>
[HarmonyPatch]
internal static class CardFlyTrailPatch
{
    [HarmonyPrepare]
    private static bool Prepare() => CardReloadVfxTargets.Resolve();

    [HarmonyTargetMethod]
    private static MethodBase? Target() => CardReloadVfxTargets.Ready;

    [HarmonyPostfix]
    private static void Postfix(object __instance)
    {
        try
        {
            if (__instance is not Node node || CardReloadVfxTargets.FCard == null)
            {
                return;
            }

            if (!RayMod.Art.CardFieldVfx.CardTrailOn)
            {
                return;
            }

            if (CardReloadVfxTargets.FCard.GetValue(__instance) is not Control card)
            {
                return;
            }

            if (!RayMod.Art.CardFieldVfx.IsMyCard(card))
            {
                return;
            }

            // 宿主必须是**比卡牌活得久**的节点：卡牌被 NCardFlyVfx 释放后，拖尾还要多活一小会儿淡出。
            // 卡的父节点（战斗特效容器）正合适。
            // brassRain = 「填装形态」专属的金色碎屑雨（对应卡图里满天黄铜子弹）。
            RayMod.Art.CardFlyTrail.Attach(card, node.GetParent(), RayMod.Art.CardFieldVfx.ReloadActive);
        }
        catch
        {
            // 表现层失败绝不外抛
        }
    }
}

/// <summary>
/// <b>A-1 ②</b>：<c>NCardFlyVfx._Ready</c> 的 Postfix —— 把终点甩到屏幕外、改拱高与时序，
/// 再叠自旋 + 抛弹壳。
/// </summary>
[HarmonyPatch]
internal static class CardEjectTimingPatch
{
    [HarmonyPrepare]
    private static bool Prepare() => CardReloadVfxTargets.Resolve();

    [HarmonyTargetMethod]
    private static MethodBase? Target() => CardReloadVfxTargets.Ready;

    [HarmonyPostfix]
    private static void Postfix(object __instance)
    {
        try
        {
            if (__instance is not Node node)
            {
                return;
            }

            if (!CardReloadVfxTargets.Pending.Remove(node.GetInstanceId()))
            {
                // ★ 兜底：Create 补丁若被 JIT 内联掉（铁律 73），Pending 永远是空的。
                //   这时改用"卡自己的目标堆"来认路 —— 判据等价，只是慢一点。
                if (!CardReloadVfxTargets.CardTargetsDiscard(__instance))
                {
                    return;
                }

                CardReloadVfxTargets.Log.Info("[填装形态·诊断] _Ready 走兜底认路"
                                              + "（Create 补丁没生效 ⇒ 大概率被 JIT 内联了）");
            }

            if (!RayMod.Art.CardFieldVfx.ShouldEject())
            {
                return;
            }

            // ★ 此刻节点已经在场景树里，才拿得到 viewport。
            Vector2 viewport = node.GetViewport()?.GetVisibleRect().Size ?? Vector2.Zero;

            if (viewport.X < 1f || viewport.Y < 1f)
            {
                return;
            }

            var start = (Vector2)CardReloadVfxTargets.FStart!.GetValue(__instance)!;

            RayMod.Art.CardFieldVfx.EjectTiming(out float duration, out float speed, out float accel);

            // ★★ 「子弹掉落」= **只改拱高和时序，不动终点**。
            //    终点仍然是弃牌堆（语义正确），拱高拉大 ⇒ 卡先被抛起来，再加速落进牌堆；
            //    而 PlayAnim 的旋转是切向跟随的，抛物线本身就会让它翻面下坠，
            //    **不需要**我们另外叠自旋（那正是上一版"乱转"的原因）。
            float arc = RayMod.Art.CardFieldVfx.EjectArcDir(viewport);

            CardReloadVfxTargets.FDuration!.SetValue(__instance, duration);
            CardReloadVfxTargets.FSpeed!.SetValue(__instance, speed);
            CardReloadVfxTargets.FAccel!.SetValue(__instance, accel);
            CardReloadVfxTargets.FArcDir!.SetValue(__instance, arc);

            CardReloadVfxTargets.Diag("eject",
                $"掉落生效：起点={start} 拱高={arc:F0}（屏 {viewport.X:F0}x{viewport.Y:F0}，"
                + $"时长 {duration:F2}s 初速 {speed:F2} 加速 {accel:F2}）终点未改（仍为弃牌堆）");

            if (CardReloadVfxTargets.FCard!.GetValue(__instance) is not Control cardNode)
            {
                return;
            }

            RayMod.Art.CardFieldVfx.EjectVisuals(cardNode, node.GetParent(), start);

            // ★★ 2026-10-20「子弹落地」：原版飞完 <c>QueueFree</c> 掉卡，<b>没有到达的表现</b>。
            //    挂一个独立锚点在弃牌堆位置（它就是 _endPos，我们没改过），到点自己爆一圈冲击波。
            //    锚点必须挂在 <c>node.GetParent()</c>（战斗特效容器）—— 卡在落地前就已经被回收了。
            if (CardReloadVfxTargets.FEnd?.GetValue(__instance) is Vector2 end)
            {
                float landDelay = duration / Mathf.Max(0.2f, speed);
                RayMod.Art.CardFieldVfx.EjectLanding(node.GetParent(), end, landDelay);
            }
        }
        catch
        {
            // 表现层失败绝不外抛
        }
    }
}

/// <summary>
/// <b>拖拽手牌时的线框残影</b>（2026-10-19）。
///
/// <para>挂 <c>NMouseCardPlay.LerpToMouse(NHandCardHolder)</c> 的 <b>Postfix</b>。
/// 那个方法是拖拽期间的<b>每帧循环体</b>：
/// <code>do { await LerpToMouse(Holder); } while (!IsCardInPlayZone() &amp;&amp; !cancelled);</code>
/// 所以 Postfix 等价于"拖动期间每帧回调一次"。</para>
///
/// <para>★ 为什么挑它：它是 <c>async Task</c> ⇒ 编译成状态机 ⇒ <b>不会被 JIT 内联</b>，
/// 不受铁律 73 影响。而 <c>NCardFlyVfx.Create</c> 那种小同步方法就被内联过
/// （日志里出现过「_Ready 走兜底认路（Create 补丁没生效 ⇒ 大概率被 JIT 内联了）」）。</para>
///
/// <para>⚠️ 只<b>加</b>节点，不碰卡牌/持有者的任何属性 —— 拖拽手感是游戏的核心交互，
/// 一旦改了 <c>SetTargetPosition</c> 就会影响"拖到哪算打出去"的判定。</para>
/// </summary>
[HarmonyPatch]
internal static class CardDragGhostPatch
{
    [HarmonyPrepare]
    private static bool Prepare()
        => CardReloadVfxTargets.Resolve()
           && CardReloadVfxTargets.DragLerp != null
           && CardReloadVfxTargets.PCardNode != null;

    [HarmonyTargetMethod]
    private static MethodBase? Target() => CardReloadVfxTargets.DragLerp;

    [HarmonyPostfix]
    private static void Postfix(object __0)
    {
        try
        {
            // ★ 「灵异录像带」那回合手牌是歪的；被拖起来的那张必须交还给游戏摆，
            //   否则我们的"喂角度/喂落位"会和拖拽互相拉扯 ⇒ 不停抽搐。
            RayMod.Art.CardFieldVfx.NoteDragging(
                CardReloadVfxTargets.PCardNode!.GetValue(__0) as Control);

            // 和飞行残影同一个总闸：出牌特效关掉时这里也一起消失。
            if (!RayMod.Art.CardFieldVfx.CardTrailOn)
            {
                return;
            }

            RayMod.Art.CardFieldVfx.SpawnDragGhost(
                CardReloadVfxTargets.PCardNode!.GetValue(__0) as Control);

            // ★ 2026-10-20 用户："如果你拿起卡牌。那么会有一颗子弹飞到卡牌旁边。
            //   直到你放下或者打出" ⇒ 「无限」那回合拖牌时，一颗实体子弹会跟到牌边。
            //   同一个钩子（拖拽每帧一次）：松手 = 这里不再被调用 ⇒ 由节点自己超时退场。
            RayMod.Art.CardFieldVfx.TickCardBullet(
                CardReloadVfxTargets.PCardNode!.GetValue(__0) as Control);
        }
        catch
        {
            // 表现层失败绝不外抛
        }
    }
}

/// <summary>
/// <b>螺旋期间，把这一张牌的 <c>AnimPosition</c> 关掉。</b>
///
/// <para><b>为什么必须关</b>：<c>AnimPosition</c> 是速率 7/s 的<b>一阶低通</b>
/// （<c>Position = Position.Lerp(_targetPosition, dt * 7)</c>）。
/// 螺旋的切向速度约 1600px/s，经过它之后振幅被压掉一半以上、相位还被扭转 ——
/// 实测就是"完全看不出在转"（用户反馈："没看到螺旋效果"）。
/// 所以螺旋那一小段时间必须由我们<b>直接写 <c>Position</c></b>。</para>
///
/// <para>⚠️ 关掉之后 <c>AnimPosition</c> 里那段 <c>Hitbox.SetEnabled(true)</c> 也不会跑，
/// 所以 <see cref="RayMod.Art.CardChamberSpiral"/> 收尾时必须自己补上，
/// 并且从 <c>Driven</c> 集合里移除、再调一次 <c>SetTargetPosition</c> 让游戏接着管。</para>
/// </summary>
[HarmonyPatch]
internal static class CardSpiralSuppressPatch
{
    [HarmonyPrepare]
    private static bool Prepare() => CardReloadVfxTargets.Resolve();

    [HarmonyTargetMethod]
    private static MethodBase? Target() => CardReloadVfxTargets.HolderAnimPosition;

    [HarmonyPrefix]
    private static bool Prefix(object __instance)
    {
        try
        {
            if (__instance is not Godot.Node node)
            {
                return true;
            }

            ulong id = node.GetInstanceId();

            if (!RayMod.Art.CardChamberSpiral.Driven.TryGetValue(id, out var spiral))
            {
                return true;
            }

            if (spiral != null && spiral.StillDriving())
            {
                return false;      // 确实是"正在被螺旋驱动"的那一个 ⇒ 跳过游戏的 lerp
            }

            // ★★ 自愈：登记还在、驱动器已经死了 ⇒ 说明漏了清理。
            //   Godot 的 InstanceId 会被复用，留着这一条会让**下一个**拿到同 id 的 holder
            //   一出生就被压制，牌永远停在出生点（"牌渲染不出来"）。
            RayMod.Art.CardChamberSpiral.Driven.Remove(id);
            return true;
        }
        catch
        {
            return true;      // 判不了就放行，让游戏照常动
        }
    }
}

/// <summary>
/// <b>A-2</b>：<c>NPlayerHand.Add(NCard, int)</c> 的 Postfix —— 把刚进手牌的 holder 摆到
/// "弹仓"（屏幕下方），剩下的位移交给游戏自己的 <c>AnimPosition</c> 指数 lerp 推上来；
/// 再挂一个观察者，等它停稳那一刻打一记「咔」。
///
/// <para>⚠️ 必须在 <b>Postfix</b>（不能是 Prefix）：<c>Add</c> 内部是
/// <c>AddCardHolder</c> → <c>RefreshLayout</c> → <c>SetTargetPosition(手牌位)</c>，
/// 之后还要 <c>GlobalPosition = globalPosition</c> 再 <c>RefreshLayout()</c> 一次。
/// 只有等这些<b>全都跑完</b>，我们写的起始位才不会被覆盖。</para>
/// </summary>
[HarmonyPatch]
internal static class CardChamberPatch
{
    [HarmonyPrepare]
    private static bool Prepare() => CardReloadVfxTargets.Resolve();

    [HarmonyTargetMethod]
    private static MethodBase? Target() => CardReloadVfxTargets.HandAdd;

    [HarmonyPostfix]
    private static void Postfix(object __result)
    {
        try
        {
            CardReloadVfxTargets.Diag("handadd", $"NPlayerHand.Add 被调到：result={__result?.GetType().Name ?? "null"}");

            if (__result is not Control holder)
            {
                return;
            }

            if (!RayMod.Art.CardFieldVfx.ShouldChamber())
            {
                return;
            }

            if (CardReloadVfxTargets.PTargetPosition?.GetValue(__result) is not Vector2 target)
            {
                return;
            }

            if (!RayMod.Art.CardFieldVfx.ChamberPlace(holder, target))
            {
                return;
            }

            var card = CardReloadVfxTargets.PCardNode?.GetValue(__result) as Control;

            RayMod.Art.CardFieldVfx.StartChamberKick(holder, card, target);
        }
        catch
        {
            // 表现层失败绝不外抛
        }
    }
}
