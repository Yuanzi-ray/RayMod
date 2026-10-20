using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using RayMod.Ammo;
using RayMod.Hud;
using RayMod.Powers;

// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;
using LogType = MegaCrit.Sts2.Core.Logging.LogType;

namespace RayMod.Art;

/// <summary>
/// ★★ 2026-10-19 <b>「填装形态」表现层</b> —— 用户原话：
/// <i>"填装形态下，卡牌不再是简单飞回弃牌堆或者简单抽上来。并且需要有个常驻特效"</i>。
///
/// <para><b>本文件只做表现，不碰任何数值/机制</b>（<see cref="LoadingPosturePower"/> 的规则在 Powers 里）。</para>
///
/// <para><b>★ 开工前查清的两条路（和方案 §4 的猜测不一样，别再按"同一套动画带方向参数"去想）</b>：
/// <list type="bullet">
///   <item><b>弃牌</b> 走 <c>CardPileCmd.GetTweenForCardsChangingPiles</c> 的默认分支 →
///         <c>NCardFlyVfx.Create(cardNode, newPileType, isAddingToPile: true, …)</c> →
///         <c>PlayAnim()</c> 一条<b>贝塞尔 + 切向跟随旋转 + 缩小淡出</b>的协程。
///         我们要改的是：终点（甩出屏幕）、拱高（抛物线）、速度（更快）、自旋（Body）。</item>
///   <item><b>抽牌</b> <b>完全不经过 <c>NCardFlyVfx</c></b> —— <c>case PileType.Hand:</c> 里
///         <c>AppendPileLerpTween</c> 对 Hand <b>直接跳过位置补间</b>，真正的位移是
///         <c>NPlayerHand.Add</c> 把 holder 摆在抽牌堆位置，再由
///         <c>NHandCardHolder.AnimPosition()</c> <b>指数 lerp（7/s）</b> 拉进手牌区。
///         ⇒ 抽牌侧只要在 <c>Add</c> 之后把起始位换到屏幕下方就行，不用改任何协程。</item>
/// </list></para>
///
/// <para><b>★ 门控只有一个出口</b>：<see cref="ReloadArmed"/> —— 由
/// <see cref="AttachAmmoBelt"/> 置位、由弹链自己的 0.5s 自查清位。
/// 于是"判据"只有一处（铁律 50），三个补丁都只问它。</para>
///
/// <para>⚠️ <b>联机</b>：<c>GetTweenForCardsChangingPiles</c> 对<b>非本地玩家</b>的卡
/// （目标是 Draw/Hand/Discard/Deck）走的是"原地淡出"分支，<b>根本不会创建 <c>NCardFlyVfx</c></b>；
/// 而 <c>NPlayerHand</c> 本身就是本地玩家的手牌。⇒ 这两条路天然只覆盖本地玩家，
/// 额外的门控只在挂弹链那一处（<see cref="IsLocalPlayer"/>）。</para>
/// </summary>
internal partial class CardFieldVfx
{
    private static readonly Logger ReloadLog = new("RayMod.ReloadForm", LogType.Generic);

    // ═══════════════════════════════════════════════════════════════════════
    //  门控：一个静态出口
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>身上挂着「填装形态」= 卡牌动效与常驻弹链都开着。<b>三个补丁只问这一个。</b></summary>
    internal static bool ReloadArmed;

    /// <summary>弹链的主人（本地玩家）。音效归属要它。</summary>
    private static Player? _reloadOwner;

    /// <summary>
    /// ★★★ <b>「改造卡牌轨迹」总开关 —— 2026-10-19 已关掉。</b>
    ///
    /// <para><b>为什么关</b>（用户实测："抽上来和回到弃牌堆的动画丑"）：</para>
    /// <para>我原来做的是<b>换掉游戏的飞行轨迹</b>（终点甩出屏幕 + 加拱高 + 提速 + 叠加自旋 + 抛弹壳）。
    /// 复盘下来这是错的方向 —— <b>游戏自己的那段贝塞尔 + 切向跟随旋转 + 缩小淡出本来就是调好的</b>，
    /// 我去改它只会让卡"乱转、瞬间缩成小点、飞出画面"，丑的是我的改动。</para>
    ///
    /// <para><b>真正的病根在别处</b>：每个角色的卡牌飞行都配了一份拖尾场景
    /// <c>res://scenes/vfx/card_trail_&lt;角色id&gt;.tscn</c>（两条加色 Line2D 拖尾 + 两套粒子 +
    /// 两个卡牌剪影残影），而本 mod 的角色<b>从来没有这一份</b> ⇒
    /// <c>NCardTrailVfx.Create</c> 里 <c>GetScene(null).Instantiate()</c> 抛 NRE ⇒
    /// <c>NCardFlyVfx._Ready</c> 中断在那一行 ⇒ <c>PlayAnim()</c> 压根没跑。
    /// 也就是说<b>莱伊的卡牌飞行一直是坏的</b>，和「填装形态」无关。</para>
    ///
    /// <para>⇒ 修法是补上 <c>RayMod\vfx\card_trail_ray.tscn</c> 并在
    /// <c>NCardFlyVfx.Create</c> 的 Postfix 里改私有字段 <c>_trailPath</c> 指过去
    /// （见 <c>Patches\CardReloadVfxPatch</c>）。轨迹本身恢复原版。</para>
    ///
    /// <para><b>★ 关于拖尾的一个<u>已推翻</u>的假设，别再重犯</b>：我一度以为"莱伊没有
    /// <c>card_trail_&lt;角色id&gt;.tscn</c> ⇒ <c>NCardTrailVfx.Create</c> 抛 NRE ⇒
    /// <c>PlayAnim</c> 不跑 ⇒ 卡牌飞行一直是坏的"。<b>这是错的</b>，两条反证：
    /// ① <c>godot.log</c> 里没有任何异常（同一次会话里 Godot 侧的 ERROR 是记得住的）；
    /// ② <c>RayModCharacter.PlaceholderCharacterId => "ironclad"</c>，RitsuLib 用 placeholder
    ///    补齐角色资产 ⇒ 莱伊的拖尾**借的是铁甲战士那份**，路径是存在的。
    /// 所以"没有卡牌飞行动画"的真正原因是<b>我改坏了 <c>PlayAnim</c> 读的那几个字段</b>，
    /// 不是缺场景。补 <c>RayMod\vfx\card_trail_ray.tscn</c> 是<b>把借来的红/暗色换成莱伊的琥珀色</b>，
    /// 属于观感升级，不是修 bug。</para>
    ///
    /// <para>真要再做"轨迹差异化"，请在这个开关后面重写，<b>别再动 <c>PlayAnim</c> 的曲线</b>；
    /// 参数可以直接照抄 SilentFX（创意工坊 3817850774）的实测值 —— 它是唯一一个真重做了抽/弃牌动画的模组：
    /// 抽牌 <c>toss_time=0.46s / toss_hop=230px / toss_gap=0.07s</c>（贝塞尔拱起甩进手）；
    /// 弃牌 <c>lunge=260px / spring=9.3 / damp=0.68 / gap=0.045s / flight=0.15s</c>（弹簧外冲再被拽回）；
    /// 残影 <c>gap=24px / alpha=0.40 / 尺寸 ×0.92×0.80</c>；
    /// 缓动 起飞 <c>OUT_BACK</c> → 主体 <c>CUBIC/OUT</c> → 落位 <c>CIRC/IN</c>。</para>
    /// </summary>
    internal const bool AlterCardMotion = true;

    /// <summary>
    /// <b>弃牌 = 子弹掉落</b>（用户 2026-10-19 定的方向）：
    /// <b>崩出去 → 被抛起 → 加速下坠 → 落进弃牌堆</b>。
    ///
    /// <para><b>做法刻意保守</b>：<b>不动终点</b>（仍然飞向弃牌堆，语义正确），只把
    /// <c>NCardFlyVfx._arcDir</c> 拉大 —— 那是贝塞尔控制点的抬高量，控制点越高，
    /// 卡就越"先抛起来再落下"。而 <c>PlayAnim</c> 的旋转是<b>切向跟随</b>的
    /// （<c>_card.Rotation = LerpAngle(…, 切线角 + 90°, …)</c>），所以抛物线本身就会让卡
    /// <b>自动翻面下坠</b>，不需要我另外叠自旋。</para>
    ///
    /// <para>⚠️ 上一次翻车就是因为<b>改了终点</b>（甩到屏幕外）+ 叠了 34° 自旋 + 提速两倍，
    /// 结果卡"乱转、半路缩成小点、飞出画面"。这次一条都不碰。</para>
    ///
    /// <para><b>★★ 2026-10-20 大改（用户：「填装模式时，卡牌飞入弃牌堆的方式和正常有所不同
    /// （比如更直？或者模拟子弹落地？）」）→ 判据变了：</b>
    /// 从"<b>拱得更高</b>"改成"<b>贴地平射 + 砸地</b>"。
    /// 原版弃牌的拱高是 <c>500~900px</c>（<c>Rng.Chaotic.NextFloat(100,400) + 500</c>），
    /// 读起来是"飘过去"；这里压到 <c>0.17×屏高 ≈ 122px</c>，卡几乎是一条直线被崩进牌堆，
    /// 落地再补一圈地面冲击波 + 尘（见 <see cref="EjectLanding"/>）。
    /// 手牌在屏幕底部中央、弃牌堆在左下角，两点高度几乎一样 ⇒ 拱高压低就等于"平射"，
    /// <b>和原版一眼能分出是两套手感</b>。</para>
    /// </summary>
    /// <remarks>
    /// ★ 0.34（≈367px，比原版矮但仍是一道弧）→ <b>0.17</b>（≈122px，读作平射）。
    /// </remarks>
    private const float EjectArcFrac = 0.17f;

    /// <summary>掉落时长（秒，实际落地时间还要除以 <see cref="EjectSpeed"/>）。</summary>
    /// <remarks>
    /// ★ 2026-10-20：1.15 → <b>0.70</b>。弹壳被抽出来是"崩"出来的，不是"飘"过去的；
    /// 原版 1.0~1.75s 的时长本身就是"飘"的主要来源。
    /// </remarks>
    private const float EjectDuration = 0.70f;

    /// <summary>初速（原版 1.1~1.25）。保持 1.0 附近，让 <see cref="EjectDuration"/> 直接就是秒数。</summary>
    private const float EjectSpeed = 1.00f;

    /// <summary>加速度（原版 2.0~2.5）。取原版下界：要保留下坠的加速感。</summary>
    private const float EjectAccel = 2.00f;

    /// <summary>
    /// 曾经想用的「角色自有拖尾场景」。<b>该路线已废弃</b>（见 <c>Patches\CardReloadVfxPatch</c>
    /// 里 <c>UseVanillaTrailScene</c> 的注释：从自己的 pck 引用游戏程序集的 C# 脚本，
    /// <c>Instantiate&lt;NCardTrailVfx&gt;()</c> 拿不到类型 ⇒ 返回 null ⇒ 拖尾静默消失）。
    /// 保留这个常量只是给自检探针与文档引用，<b>不再写进 <c>_trailPath</c></b>。
    /// </summary>
    internal const string RayCardTrailPath = "res://RayMod/vfx/card_trail_ray.tscn";

    /// <summary>
    /// <b>自绘卡牌拖尾的总开关</b>（<see cref="CardFlyTrail"/>：残影链 + 光带 + 火星）。
    ///
    /// <para>★ 与「填装形态」<b>无关</b> —— 这是角色基础的观感质量，装着 mod 玩莱伊就该有。</para>
    /// </summary>
    internal static bool CardTrailOn => Enabled && !TestModeIsOn();

    /// <summary>
    /// 「填装形态」此刻是否生效 —— 供 <see cref="CardFlyTrail"/> 选<b>基础</b>变体还是
    /// <b>填装形态</b>变体（金色 + 黄铜碎屑雨）。直接复用唯一判据 <see cref="ReloadLive"/>。
    /// </summary>
    internal static bool ReloadActive => ReloadLive();

    /// <summary>弃牌要不要走「退壳」。</summary>
    internal static bool ShouldEject() => AlterCardMotion && ReloadLive();

    /// <summary>抽牌要不要走「上膛」。</summary>
    internal static bool ShouldChamber() => AlterCardMotion && ReloadLive();

    /// <summary>
    /// 三个补丁的唯一判据。
    ///
    /// <para>★ <b>除了静态旗标，每次还真的去问一次能力</b>：旗标只是"省掉一次空判"的快路，
    /// 真正说了算的是 <c>GetPower&lt;LoadingPosturePower&gt;()</c>。
    /// 这样即便某个收场路径漏了（能力被顶掉、战斗异常结束、AfterRemoved 没走到），
    /// 也不会出现"填装形态早没了，卡牌还在退壳"的脏状态。</para>
    ///
    /// <para>调用频率 = 每张牌飞行 / 每次进手牌一次，不在 <c>_Process</c> 里，所以这点开销可以忽略。</para>
    /// </summary>
    private static bool ReloadLive()
    {
        if (!ReloadArmed || !Enabled || TestModeIsOn() || _reloadOwner == null)
        {
            Diag("gate", $"ReloadLive=假（armed={ReloadArmed} enabled={Enabled} test={TestModeIsOn()} owner={_reloadOwner != null}）");
            return false;
        }

        try
        {
            bool has = _reloadOwner.Creature != null
                       && _reloadOwner.Creature.GetPower<LoadingPosturePower>() != null;

            Diag("gate", $"ReloadLive={has}（armed=真 enabled=真 owner=有 能力={has}）");
            return has;
        }
        catch (Exception e)
        {
            // 拿不到（战斗已结束 / 生物节点已释放）⇒ 当作没了。
            Diag("gate", $"ReloadLive=假（查能力时抛了：{e.GetType().Name}）");
            return false;
        }
    }

    /// <summary>
    /// 「上膛」起推的那一记火星：在卡片的**起点**炸一小簇暖金粒子。
    /// 与弃牌的 <see cref="EjectVisuals"/> 对偶 —— 一个在起点（弹被顶进膛），一个在起点（弹被崩出膛）。
    /// </summary>
    private static void SpawnChamberPuff(Control holder)
    {
        try
        {
            Node? host = holder?.GetParent();

            if (host == null || !GodotObject.IsInstanceValid(host) || !host.IsInsideTree())
            {
                return;
            }

            var puff = new Node2D { Name = "ChamberPuff" };
            host.AddChild(puff);
            puff.GlobalPosition = holder!.GlobalPosition;

            GpuParticles2D sparks = FieldFx.Sparks(
                puff,
                new Color(1f, 0.90f, 0.62f),
                amount: 10,
                speedMin: 60f,
                speedMax: 210f,
                lifetime: 0.30f,
                gravity: 520f,
                scaleMin: 0.06f,
                scaleMax: 0.20f);

            FieldFx.Ignite(sparks);
            FieldFx.FreeAfter(puff, 0.9);
        }
        catch
        {
            // 火星失败不影响位移
        }
    }

    // ── 诊断（前若干次打印，之后静音；排障用，长期留着对下次改版有用）──

    private static readonly Dictionary<string, int> DiagCounts = new();

    /// <summary>前 <paramref name="limit"/> 次打印，之后不再打（避免刷屏）。</summary>
    private static void Diag(string key, string message, int limit = 6)
    {
        DiagCounts.TryGetValue(key, out int n);

        if (n >= limit)
        {
            return;
        }

        DiagCounts[key] = n + 1;
        ReloadLog.Info($"[填装形态·诊断] {message}（{key} 第 {n + 1}/{limit} 条）");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  A-1 · 弃牌 = 退壳（参数由 <c>Patches\CardReloadVfxPatch</c> 读走写进 NCardFlyVfx 的私有字段）
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>拱高：换算成 <c>NCardFlyVfx._arcDir</c> 的值（正值 = 控制点在弦上方 ⇒ 先抛起再落下）。</summary>
    internal static float EjectArcDir(Vector2 viewport) => viewport.Y * EjectArcFrac;

    /// <summary>退壳的时长/速度参数，由 <c>_Ready</c> 的 Postfix 写回私有字段。</summary>
    internal static void EjectTiming(out float duration, out float speed, out float accel)
    {
        duration = EjectDuration;
        speed = EjectSpeed;
        accel = EjectAccel;
    }

    /// <summary>
    /// <b>「子弹掉落」的画面</b>：只在起飞点放一簇火星（"崩出去"的那一下）。
    ///
    /// <para>⚠️ 这里<b>不再</b>叠自旋、也<b>不再</b>抛弹壳 —— 上一版那两样是"丑"的主要来源：
    /// 自旋会和 <c>PlayAnim</c> 的切向跟随旋转相加（卡会乱转），弹壳是硬边小方块（像黄噪点）。
    /// 抛物线本身已经让卡翻面下坠，光带和残影交给 <see cref="CardFlyTrail"/>。</para>
    /// </summary>
    internal static void EjectVisuals(Control card, Node host, Vector2 startGlobal)
    {
        try
        {
            if (host == null || !GodotObject.IsInstanceValid(host) || !host.IsInsideTree())
            {
                return;
            }

            // ★★ 2026-10-20「不够足」的补丁：**卡牌本体的样子也要变**。
            //   火星只画在卡的起点（一个点），光带/残影画在卡的外面（身外之物），
            //   卡自己从头到尾没变过 ⇒ 玩家盯着卡就"看不到特效"。
            //   壳层挂在卡牌节点上（Body 的兄弟，不是 Body 里面），跟着卡飞完全程。
            //   ⚠️ 放在 try 的**最前面**：后面火星那一段万一抛异常，这一层不能被连坐吞掉。
            SleeveOnEject(card);

            var puff = new Node2D { Name = "EjectPuff" };
            host.AddChild(puff);
            puff.GlobalPosition = startGlobal;

            GpuParticles2D sparks = FieldFx.Sparks(
                puff,
                new Color(1f, 0.82f, 0.48f),
                amount: 16,
                speedMin: 110f,
                speedMax: 400f,
                lifetime: 0.34f,
                gravity: 900f,
                scaleMin: 0.08f,
                scaleMax: 0.26f);

            FieldFx.Ignite(sparks);          // ★ 必须用 Ignite 点火（建完别自己设 Emitting）
            FieldFx.FreeAfter(puff, 1.0);
        }
        catch (Exception e)
        {
            ReloadLog.Warn($"[填装形态·掉落] 起飞火星失败（不影响飞行）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// <b>落地那一拍</b>：「子弹落地」的"落"字全靠这里。
    ///
    /// <para>为什么必须单独做：<c>NCardFlyVfx</c> 飞完就把卡 <c>QueueFree</c> 掉，
    /// <b>没有任何"到达"的表现</b>（原版是卡淡成黑点然后消失，玩家根本看不到"进牌堆"）。
    /// 所以这里挂一个独立的锚点节点，<b>它不随卡消失</b>，到点自己在弃牌堆位置播一圈冲击波 + 尘。</para>
    ///
    /// <para>⚠️ 锚点必须挂在 <paramref name="host"/>（飞行特效的父节点 = 战斗特效容器），
    /// <b>不能挂在卡上</b> —— 卡在 <paramref name="delay"/> 之后就已经被回收了。</para>
    /// </summary>
    /// <param name="host">活到落地之后的父节点。</param>
    /// <param name="endGlobal">弃牌堆的全局坐标（<c>NCardFlyVfx._endPos</c>，未改）。</param>
    /// <param name="delay">从起飞算起到落地还有多少秒。</param>
    internal static void EjectLanding(Node host, Vector2 endGlobal, float delay)
    {
        try
        {
            if (host == null || !GodotObject.IsInstanceValid(host) || !host.IsInsideTree())
            {
                return;
            }

            var anchor = new Node2D { Name = "EjectLandAnchor" };
            host.AddChild(anchor);
            anchor.GlobalPosition = endGlobal;
            FieldFx.FreeAfter(anchor, delay + LandRingTime + 1.0);

            FieldFx.After(anchor, delay, "[填装形态·落地]", () =>
            {
                try
                {
                    if (!GodotObject.IsInstanceValid(anchor) || !anchor.IsInsideTree())
                    {
                        return;
                    }

                    // ① 地面冲击波：压扁的椭圆环（squashY < 1 才有"躺在地上"的透视感），
                    //    从很小猛地涨开再淡掉 —— 一帧的信息量就够读数。
                    Line2D ring = FieldFx.Line(
                        anchor,
                        FieldFx.Circle(52f, 30, LandSquashY),
                        new Color(SleeveBrass.R, SleeveBrass.G, SleeveBrass.B, LandRingAlpha),
                        LandRingWidth,
                        closed: true);

                    FieldFx.Animate(ring, LandRingTime, k =>
                    {
                        ring.Scale = Vector2.One * Mathf.Lerp(0.35f, 1.65f, FieldFxEase.OutCubic(k));
                        ring.Modulate = new Color(1f, 1f, 1f, 1f - k);
                    });

                    FieldFx.FreeAfter(ring, LandRingTime + 0.05);

                    // ② 被砸起来的尘/火星：速度低、重力大 ⇒ 是"落"不是"喷"。
                    GpuParticles2D dust = FieldFx.Sparks(
                        anchor,
                        new Color(0.86f, 0.72f, 0.40f),
                        amount: 12,
                        speedMin: 60f,
                        speedMax: 210f,
                        lifetime: 0.42f,
                        gravity: 520f,
                        scaleMin: 0.10f,
                        scaleMax: 0.32f);

                    FieldFx.Ignite(dust);
                }
                catch
                {
                    // 落地效果失败绝不外抛（这时候卡已经飞完，抛出去会炸在定时器里）
                }
            });
        }
        catch (Exception e)
        {
            ReloadLog.Warn($"[填装形态·落地] 挂载失败（不影响飞行）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>落地冲击波的压扁系数（<c>1</c> = 正圆，<c>0.42</c> = 躺在地上）。</summary>
    private const float LandSquashY = 0.42f;

    /// <summary>落地冲击波的不透明度 / 线宽 / 时长。</summary>
    private const float LandRingAlpha = 0.55f;
    private const float LandRingWidth = 3.0f;
    private const float LandRingTime = 0.34f;

    // ═══════════════════════════════════════════════════════════════════════
    //  A-2 · 抽牌 = 压入枪管
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>从下方多远直着顶上来（× 屏高）。</summary>
    /// <remarks>
    /// ★ <b>「压入枪管」的关键是"短"</b>：0.38（≈410px）时卡是从屏幕外升上来的，
    /// 读起来是"滑进来"而不是"压进去"。
    /// ★ 2026-10-19：0.13 → <b>0.20</b>（≈216px）。用户要"加强上膛的动感"——
    /// 0.13 那版位移只有 140px，几乎看不出在动；216px 既能看出行程，又不至于从屏幕外飞进来。
    /// </remarks>
    private const float ChamberRiseFrac = 0.20f;

    /// <summary>
    /// 左右外扩：离手牌区中心越远，起点越往外，多张同时抽才会像"扇形上膛"而不是一排平移。
    /// <para>⚠️ <c>targetLocal.X</c> 本来就是<b>以手牌区中心为 0</b> 的 ——
    /// <c>HandPosHelper.GetPosition</c> 的坐标表是 (-610 … +610) 的对称数组，
    /// 别再减一次屏幕中线（那会把整列推到右边去）。</para>
    /// </summary>
    private const float ChamberSplayFrac = 0.10f;

    /// <summary>
    /// 把刚进手牌的 holder 摆到"弹仓"起点（屏幕下方）。
    /// <para>由 <c>NPlayerHand.Add</c> 的 Postfix 调，<b>在</b> 游戏已经
    /// <c>SetTargetPosition(手牌位)</c> <b>之后</b> —— 而 <c>AnimPosition</c> 是"从当前位 lerp 到目标位"，
    /// 所以只要改当前位，剩下的交给游戏自己推。</para>
    /// <returns>true = 已摆好（调用方接着挂落位一击）。</returns>
    /// </summary>
    internal static bool ChamberPlace(Control holder, Vector2 targetLocal)
    {
        try
        {
            if (holder == null || !GodotObject.IsInstanceValid(holder))
            {
                return false;
            }

            Vector2 vp = holder.GetViewportRect().Size;

            if (vp.X < 1f || vp.Y < 1f)
            {
                return false;
            }

            float splay = targetLocal.X * ChamberSplayFrac;

            holder.Position = targetLocal + new Vector2(splay, vp.Y * ChamberRiseFrac);
            _chamberHandled.Add(holder.GetInstanceId());

            // ★★ 螺旋进场（用户选的方案 b）：**不抢 Position，只把 AnimPosition 的目标
            //    沿一条螺旋曲线推移**（详见 CardChamberSpiral 的类注释）。
            CardChamberSpiral.Begin(holder, holder.Position, targetLocal);

            // ★ 抽牌也给一条光带（否则"压入枪管"只有位移没有速度感）。
            //   宿主用 holder 的父节点（手牌容器），它比这一张卡活得久。
            CardFlyTrail.Attach(holder, holder.GetParent(), ReloadLive());

            // ★★ 起推的一记火星（用户："加强上膛的动感"）。
            //    等价于弃牌那记"崩出去"的火花 —— 有起点的爆点，位移才读得出是"被顶上去"，
            //    而不是"卡自己挪了位置"。放在起点（holder 当前位置）而不是终点。
            SpawnChamberPuff(holder);

            Diag("chamber", $"上膛摆位：target={targetLocal} → 起点={holder.Position}（屏高 {vp.Y:F0}）");
            return true;
        }
        catch (Exception e)
        {
            ReloadLog.Warn($"[填装形态·上膛] 摆位失败（这一张按原版动画）：{e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    // ── ★ 抽牌侧兜底：**每帧轮询手牌区**，发现"没被补丁处理过"的新 holder 就补一次 ──
    //
    //  为什么必须留这一层：`NPlayerHand.Add` 只有 12 行，正是**铁律 73**（调用方 JIT 内联
    //  ⇒ Harmony 补丁静默不执行）的高危目标。补丁能用时它是精确入口；补丁一旦失效，
    //  这一层轮询仍然把卡摆到屏幕下方。两条路靠 `_chamberHandled` 去重，不会重复摆。

    private static readonly HashSet<ulong> _chamberHandled = new();
    private static int _pollHolderCount = -1;
    private static bool _pollSeeded;
    private static PropertyInfo? _targetPositionProp;
    private static PropertyInfo? _cardNodeProp;
    private static bool _propResolved;

    internal static void PollNewHolders()
    {
        if (!ReloadArmed)
        {
            return;
        }

        try
        {
            Control? container = NPlayerHand.Instance?.CardHolderContainer;

            if (container == null || !GodotObject.IsInstanceValid(container) || !container.IsInsideTree())
            {
                return;
            }

            int count = container.GetChildCount();

            if (count == _pollHolderCount)
            {
                return;      // 这一帧没有增减，别白扫
            }

            _pollHolderCount = count;

            if (count <= 0)
            {
                return;
            }

            // ★★ 第一次轮询只**登记**、不摆位。
            //
            //  否则一打出「填装形态」，手上已有的那 5 张会被当成"新牌"全部踹到屏幕外再升回来
            //  —— 这个副作用实际很好看（像"整匣上膛"），但它是**碰巧**的，而且第二次打出
            //  填装形态会再来一次，玩家会以为卡牌出 bug。所以做成明确的：只处理之后进来的牌。
            if (!_pollSeeded)
            {
                _pollSeeded = true;

                for (int i = 0; i < count; i++)
                {
                    if (container.GetChild(i) is Node existing)
                    {
                        _chamberHandled.Add(existing.GetInstanceId());
                    }
                }

                Diag("poll", $"轮询首次运行：登记已有 {count} 张手牌（不摆位）");
                return;
            }

            for (int i = 0; i < count; i++)
            {
                if (container.GetChild(i) is not Control holder)
                {
                    continue;
                }

                ulong id = holder.GetInstanceId();

                if (_chamberHandled.Contains(id))
                {
                    continue;    // 补丁已经处理过
                }

                if (!ShouldChamber())
                {
                    return;
                }

                ResolveHolderProps(holder);

                if (_targetPositionProp?.GetValue(holder) is not Vector2 target)
                {
                    return;
                }

                Diag("poll", $"轮询发现补丁没处理的新 holder（#{id}，手牌 {count} 张）⇒ 兜底摆位");

                if (ChamberPlace(holder, target))
                {
                    StartChamberKick(holder, _cardNodeProp?.GetValue(holder) as Control, target);
                }
            }
        }
        catch
        {
            // 兜底轮询失败绝不能影响战斗
        }
    }

    private static void ResolveHolderProps(Control holder)
    {
        if (_propResolved)
        {
            return;
        }

        _propResolved = true;

        try
        {
            _targetPositionProp = holder.GetType().GetProperty("TargetPosition");
            _cardNodeProp = holder.GetType().GetProperty("CardNode");
        }
        catch
        {
            // 拿不到就只是少一个兜底
        }
    }

    /// <summary>落位那一记「咔」的时机节流（5 张一起抽不要响 5 声）。</summary>
    private static ulong _lastKickSfxMs;

    /// <summary>
    /// 挂一个"到点了就咔一下"的观察者。<b>不碰 holder 的任何逐帧属性</b> ——
    /// 判定用两条路：① 已经走到离目标 <see cref="ArrivePx"/> 以内（≈0.5s，视觉上已经坐进卡位）；
    /// ② 位置连续两帧不动（<c>AnimPosition</c> 收敛时会精确吸附然后退出）。<b>先到先算。</b>
    ///
    /// <para>★ 为什么不能只等 ②：<c>AnimPosition</c> 是指数 lerp（7/s），
    /// 最后那 1% 也要 <c>ln(100)/7 ≈ 0.66s</c> ⇒ 只等"完全停住"的话，
    /// 「咔」会晚到近一秒，读起来和抽牌脱节。用 ① 把时机提前到卡片视觉到位的瞬间。</para>
    /// </summary>
    internal static void StartChamberKick(Control holder, Control? card, Vector2 targetLocal)
    {
        try
        {
            Node? parent = holder?.GetParent();

            if (holder == null || parent == null || !GodotObject.IsInstanceValid(parent) || !parent.IsInsideTree())
            {
                return;
            }

            var kick = new ChamberKick { Holder = holder, Card = card, Target = targetLocal };
            parent.AddChild(kick);
            kick.Arm(holder.Position);

            // ★★ 卡牌本体这一层的上膛演出：壳层从 0 淡入（卡还在往弹仓里顶的时候壳先亮起来），
            //    落位「咔」由 ChamberKick.Fire() 里的 SleeveSnap 收掉。
            SleeveOnChamber(card);
        }
        catch (Exception e)
        {
            ReloadLog.Warn($"[填装形态·上膛] 落位观察者挂载失败：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>离目标多近就算"坐进卡位"（像素）。</summary>
    private const float ArrivePx = 14f;

    /// <summary>落位「咔」的参数。</summary>
    private const float KickScale = 0.88f;          // 压到的倍率（原来 0.92，压得不够）
    private const float KickOvershoot = 1.05f;      // ★ 过冲：真枪上膛也有回弹，这一下才是"咔"
    private const float KickFlashGain = 1.80f;      // 暖金闪倍率（原来 1.45）
    private const float KickFlashTime = 0.12f;
    private const float SeamTime = 0.13f;           // 弹膛闭锁光缝张开时长
    private const float SeatRingTime = 0.20f;       // 座位金环扩散时长

    private sealed partial class ChamberKick : Node
    {
        internal Control? Holder;
        internal Control? Card;
        internal Vector2 Target;

        private Vector2 _last;
        private bool _moved;
        private int _still;
        private float _t;

        internal void Arm(Vector2 now)
        {
            _last = now;
            SetProcess(true);
        }

        public override void _Process(double delta)
        {
            try
            {
                if (Holder == null || !GodotObject.IsInstanceValid(Holder) || !Holder.IsInsideTree())
                {
                    QueueFree();
                    return;
                }

                _t += (float)delta;

                // 兜底：2.5 秒还没落位就别杵着了（比如这张卡中途被抽走/打出）
                if (_t > 2.5f)
                {
                    QueueFree();
                    return;
                }

                Vector2 p = Holder.Position;

                // ① 视觉上已经坐进卡位（比"完全停住"早约 0.4s，见 ArrivePx 的注释）
                if (_t > 0.10f && p.DistanceTo(Target) <= ArrivePx)
                {
                    Fire();
                    QueueFree();
                    return;
                }

                if (p.DistanceTo(_last) > 0.05f)
                {
                    _moved = true;
                    _still = 0;
                    _last = p;
                    return;
                }

                // 前 0.08 秒还没开始动（AnimPosition 第一帧之前）不算落位
                if (!_moved || _t < 0.08f)
                {
                    _last = p;
                    return;
                }

                if (++_still < 2)
                {
                    _last = p;
                    return;
                }

                Fire();
                QueueFree();
            }
            catch
            {
                QueueFree();
            }
        }

        private void Fire()
        {
            PlayKickSfx();

            if (Card == null || !GodotObject.IsInstanceValid(Card))
            {
                return;
            }

            Vector2 size = VisualSizeOf(Card);

            // ① 三段式缩放：**1 → 0.88（快压）→ 1.05（过冲）→ 1.0（回稳）**
            //    原来是"1→0.92→1"两段，只有"缩一下"，没有过冲 ⇒ 读不出"机械卡到位"。
            //    过冲那一下是"咔"的视觉来源（真枪上膛也有回弹）。
            const float T1 = 0.028f;   // 压缩
            const float T2 = 0.055f;   // 过冲
            const float T3 = 0.070f;   // 回稳
            float total = T1 + T2 + T3;

            FieldFx.Animate(Card, total, t =>
            {
                if (!GodotObject.IsInstanceValid(Card))
                {
                    return;
                }

                float time = t * total;
                float k;

                if (time < T1)
                {
                    k = Mathf.Lerp(1f, KickScale, FieldFxEase.InCubic(time / T1));
                }
                else if (time < T1 + T2)
                {
                    k = Mathf.Lerp(KickScale, KickOvershoot, FieldFxEase.OutCubic((time - T1) / T2));
                }
                else
                {
                    k = Mathf.Lerp(KickOvershoot, 1f, FieldFxEase.OutCubic((time - T1 - T2) / T3));
                }

                Card.Scale = Vector2.One * k;
            });

            // ② 暖金闪（弹膛里那一瞬的火光）—— 用卡图的 #f9da97 而不是纯白
            FieldFx.Animate(Card, KickFlashTime, t =>
            {
                if (!GodotObject.IsInstanceValid(Card))
                {
                    return;
                }

                float k = 1f - FieldFxEase.OutCubic(t);
                Card.Modulate = new Color(
                    1f + (KickFlashGain - 1f) * k,
                    1f + (KickFlashGain - 0.86f) * k,
                    1f + (KickFlashGain - 1.30f) * k,
                    1f);
            });

            // ③ 弹膛闭锁光缝：卡片下 1/4 处一条亮线，横向张开并淡出
            SpawnSeam(Card, size);

            // ④ 座位处扩散一圈金环（"卡进膛"的落点反馈）
            SpawnSeatRing(Card, size);

            // ⑤ 收壳：壳层爆白 + 往中心收拢 → "弹壳被抛掉、子弹坐进膛"。
            //    和 ①的缩放三段式、②的暖金闪、③的光缝、④的金环**同一帧起手**，才是"咔"的那一拍。
            SleeveSnap(Card);
        }

        /// <summary>
        /// 取卡面的**视觉尺寸**。
        /// ⚠️ <c>NCard.Size</c> 实测是 <c>(0,0)</c>（诊断日志 `card size=(0, 0)`），
        /// 所以必须走 <c>Body</c>（= <c>%CardContainer</c>）。旧代码直接用 <c>card.Size</c>，
        /// 于是 <c>if (size.Y &lt; 8f) return;</c> 把整道扫光**静默跳过**了 ——
        /// 这就是"上膛质感不够"的一部分原因（那道反光从来没出现过）。
        /// </summary>
        private static Vector2 VisualSizeOf(Control card)
        {
            try
            {
                if (_bodyProp == null)
                {
                    _bodyProp = card.GetType().GetProperty("Body");
                }

                if (_bodyProp?.GetValue(card) is Control body
                    && GodotObject.IsInstanceValid(body)
                    && body.Size.X > 8f && body.Size.Y > 8f)
                {
                    return body.Size;
                }
            }
            catch
            {
                // 取不到就走兜底
            }

            return card.Size.X > 8f && card.Size.Y > 8f ? card.Size : new Vector2(300f, 422f);
        }

        private static System.Reflection.PropertyInfo? _bodyProp;

        /// <summary>弹膛闭锁光缝：卡片下 1/4 处，一条从中心向两侧张开的亮线。</summary>
        private static void SpawnSeam(Control card, Vector2 size)
        {
            try
            {
                float y = size.Y * 0.26f;      // 卡片下 1/4（局部坐标原点在视觉中心）
                float h = Mathf.Max(2f, size.Y * 0.008f);

                var seam = new ColorRect
                {
                    Name = "ChamberSeam",
                    Position = new Vector2(0f, y),
                    Size = new Vector2(size.X, h),
                    Color = new Color(1f, 0.96f, 0.84f, 0.95f),
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };

                card.AddChild(seam);

                FieldFx.Animate(seam, SeamTime, t =>
                {
                    if (!GodotObject.IsInstanceValid(seam))
                    {
                        return;
                    }

                    // 横向张开：从中间一小段撑满，再淡出
                    float w = size.X * Mathf.Lerp(0.15f, 1.05f, FieldFxEase.OutCubic(t));
                    seam.Size = new Vector2(w, h);
                    seam.Position = new Vector2((size.X - w) * 0.5f, y);

                    Color c = seam.Color;
                    c.A = 0.95f * (1f - FieldFxEase.InCubic(t));
                    seam.Color = c;
                });

                FieldFx.FreeAfter(seam, SeamTime + 0.03);
            }
            catch
            {
                // 光缝失败不影响别的
            }
        }

        /// <summary>座位金环：卡片下缘一圈快速扩散的金色细环。</summary>
        private static void SpawnSeatRing(Control card, Vector2 size)
        {
            try
            {
                Node? parent = card.GetParent();

                if (parent == null || !GodotObject.IsInstanceValid(parent))
                {
                    return;
                }

                var holder = new Node2D { Name = "ChamberSeatRing" };
                parent.AddChild(holder);
                holder.GlobalPosition = card.GlobalPosition + new Vector2(0f, size.Y * 0.40f);

                Line2D ring = FieldFx.Line(
                    holder,
                    FieldFx.Circle(1f, 40, 0.34f),     // 半径 1，先靠缩放展开；Y 压扁成椭圆
                    new Color(1f, 0.94f, 0.72f, 0.85f),
                    width: 3.0f,
                    closed: true);

                FieldFx.Animate(ring, SeatRingTime, t =>
                {
                    if (!GodotObject.IsInstanceValid(ring))
                    {
                        return;
                    }

                    float r = Mathf.Lerp(10f, size.X * 0.62f, FieldFxEase.OutCubic(t));
                    ring.Scale = new Vector2(r, r);

                    Color c = ring.DefaultColor;
                    c.A = 0.85f * (1f - FieldFxEase.InCubic(t));
                    ring.DefaultColor = c;
                });

                FieldFx.FreeAfter(holder, SeatRingTime + 0.05);
            }
            catch
            {
                // 环失败不影响别的
            }
        }

        private static void PlayKickSfx()
        {
            try
            {
                ulong now = Time.GetTicksMsec();

                if (now - _lastKickSfxMs < 200)
                {
                    return;
                }

                _lastKickSfxMs = now;

                // `p_atk_sndshotgun_nreload` —— 语义就是"装填"，和弹药增加共用（Sfx 枚举注释写明）。
                Audio.SfxPlayer.Play(Audio.Sfx.Reload, _reloadOwner);
            }
            catch
            {
                // 音效失败绝不外抛
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  B · 常驻特效：弹药弹链（Ammo Belt）
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>导轨长度 = 弹药铭牌宽 × 这个系数。</summary>
    private const float BeltLengthFrac = 1.60f;

    /// <summary>导轨粗细（像素）。</summary>
    private const float BeltHeight = 3f;

    /// <summary>导轨整体透明度（很淡 —— 常驻件不能抢视线）。</summary>
    private const float BeltAlpha = 0.18f;

    /// <summary>弹链块数。</summary>
    private const int BeltLinkCount = 5;

    /// <summary>弹链块边长。</summary>
    private const float BeltLinkSize = 6f;

    /// <summary>弹链块透明度。</summary>
    private const float BeltLinkAlpha = 0.35f;

    /// <summary>弹链横移速度（像素/秒）。很慢 —— 常驻件不做周期性大动作。</summary>
    private const float BeltSpeed = 20f;

    /// <summary>消耗弹药时导轨闪多久。</summary>
    private const float BeltFlashTime = 0.12f;

    /// <summary>导轨离铭牌的空隙。</summary>
    private const float BeltGap = 2f;

    private static AmmoBelt? _ammoBelt;

    /// <summary>
    /// 挂上常驻弹链（由 <see cref="LoadingPosturePower.AfterApplied"/> 调，和「巨兽」巨眼同一套路）。
    ///
    /// <para>★ 只给<b>本地玩家</b>挂：弹链是<b>屏幕坐标</b>的 HUD 常驻件，队友打出「填装形态」
    /// 在我屏幕上升出一条弹链没有任何意义。单机下 <c>IsMe</c> 恒真 ⇒ 行为零变化。</para>
    /// </summary>
    internal static void AttachAmmoBelt(Player? owner)
    {
        if (!Enabled || owner == null || TestModeIsOn())
        {
            return;
        }

        if (!IsLocalPlayer(owner))
        {
            return;
        }

        _reloadOwner = owner;
        ReloadArmed = true;

        try
        {
            if (_ammoBelt != null && GodotObject.IsInstanceValid(_ammoBelt))
            {
                _ammoBelt.Reconfigure(owner);
                return;
            }

            // ★ 挂在**能量计容器**上（和层数/弹药计数器同一个宿主），而不是挂在计数器自己身上：
            //   `ResourceHudCounter.Attach()` 在缩放变了 / 换宿主时会 `QueueFree` 旧节点重建，
            //   挂它下面就会被顺手删掉。挂在容器上则整场战斗都在，位置每帧从
            //   `ResourceHud.Ammo.Root` **读**过来 ⇒ 铭牌重建/滑入滑出都跟得住。
            Control? container = NCombatRoom.Instance?.Ui?.EnergyCounterContainer;

            if (container == null)
            {
                ReloadLog.Warn("[填装形态·弹链] 拿不到 EnergyCounterContainer ⇒ 这一场没有常驻弹链");
                return;
            }

            var belt = new AmmoBelt();
            container.AddChild(belt);
            belt.Reconfigure(owner);
            _ammoBelt = belt;

            ReloadLog.Info("[填装形态·弹链] 已挂上常驻弹链（填装形态生效中）");
        }
        catch (Exception e)
        {
            ReloadLog.Warn($"[填装形态·弹链] 挂载失败：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>收场（能力掉了 / 战斗结束）。即便不调，弹链自己也会在 0.5s 内自查收掉。</summary>
    internal static void RetireAmmoBelt()
    {
        ReloadArmed = false;

        if (_ammoBelt != null && GodotObject.IsInstanceValid(_ammoBelt))
        {
            _ammoBelt.Retire();
        }

        _ammoBelt = null;
    }

    /// <summary>
    /// 常驻弹链。<b>一条很淡的暖色导轨 + 几个缓慢横移的亮块 + 耗弹时闪一下并把最近那块吸走。</b>
    ///
    /// <para><b>为什么贴在弹药铭牌【上方】而不是方案里写的"下方"</b>：实测铭牌是**竖排**的
    /// （<see cref="ResourceHudCounter"/> 的 <c>y = … - _slot * rowStep</c>），
    /// <b>slot 1 = 弹药在最上面，slot 0 = 层数就在它正下方</b> —— 放下面会直接压在层数铭牌上。
    /// 上方才是空的。</para>
    /// </summary>
    private sealed partial class AmmoBelt : Control
    {
        private Player _owner = null!;

        private float _t;
        private float _poll;
        private float _fade;
        private float _flash;
        private float _suck;      // 1 → 0：把最近那块弹链吸向弹药图标的进度
        private bool _retiring;
        private bool _subscribed;
        private bool _loggedFollow;

        private float _w;         // 导轨长
        private float _h = BeltHeight;

        internal void Reconfigure(Player owner)
        {
            _owner = owner;
            _retiring = false;

            Name = "RayModAmmoBelt";
            MouseFilter = MouseFilterEnum.Ignore;   // 纯装饰，别吃鼠标事件
            SetProcess(true);                       // ★ 显式开启（别赌"重写了 _Process 就会自动开"，巨眼那轮踩过）

            if (!_subscribed)
            {
                _subscribed = true;
                AmmoEvents.Register(OnAmmoChanged);
            }
        }

        internal void Retire() => _retiring = true;

        private void OnAmmoChanged(Player player, int oldValue, int newValue)
        {
            // 只在**消耗**时反应（装填/获得不闪 —— 获得时铭牌自己会亮，别撞车）
            if (newValue >= oldValue || player != _owner)
            {
                return;
            }

            _flash = 1f;
            _suck = 1f;
        }

        public override void _Process(double delta)
        {
            try
            {
                float dt = Mathf.Min((float)delta, 0.05f);
                _t += dt;
                _poll += dt;

                if (_poll >= 0.5f)
                {
                    _poll = 0f;

                    // 自查：能力掉了 / 战斗结束了 / 设置里关了特效 ⇒ 收场
                    bool stillOn;

                    try
                    {
                        stillOn = Enabled && _owner.Creature.GetPower<LoadingPosturePower>() != null;
                    }
                    catch
                    {
                        stillOn = false;
                    }

                    if (!stillOn)
                    {
                        _retiring = true;
                    }
                }

                _fade = Mathf.Clamp(_fade + (_retiring ? -dt / 0.45f : dt / 0.60f), 0f, 1f);

                if (_retiring && _fade <= 0.001f)
                {
                    if (_subscribed)
                    {
                        _subscribed = false;
                        AmmoEvents.Unregister(OnAmmoChanged);
                    }

                    ReloadArmed = false;
                    _ammoBelt = null;
                    QueueFree();
                    return;
                }

                _flash = Mathf.Max(0f, _flash - dt / BeltFlashTime);
                _suck = Mathf.Max(0f, _suck - dt / (BeltFlashTime * 1.6f));

                // ★ 抽牌侧兜底轮询（见 PollNewHolders 的注释：补丁被 JIT 内联掉也能工作）
                PollNewHolders();

                FollowPlate();
                QueueRedraw();
            }
            catch
            {
                // 常驻件绝不能拖累战斗
                if (_subscribed)
                {
                    _subscribed = false;
                    AmmoEvents.Unregister(OnAmmoChanged);
                }

                ReloadArmed = false;
                _ammoBelt = null;
                QueueFree();
            }
        }

        /// <summary>每帧从弹药铭牌读位置（铁律：不要自己再算一遍坐标）。</summary>
        private void FollowPlate()
        {
            Control? plate = ResourceHud.Ammo.Root;

            if (plate == null || !GodotObject.IsInstanceValid(plate) || !plate.IsInsideTree() || plate.Size.X < 1f)
            {
                Visible = false;
                return;
            }

            _w = plate.Size.X * BeltLengthFrac;
            Size = new Vector2(_w, _h);
            PivotOffset = Vector2.Zero;

            // ★ 贴在铭牌【上方】：横向居中于铭牌，纵向在铭牌顶边往下让出 BeltGap
            GlobalPosition = plate.GlobalPosition + new Vector2(
                (plate.Size.X - _w) * 0.5f,
                -_h - BeltGap);

            Visible = true;

            if (!_loggedFollow)
            {
                _loggedFollow = true;
                ReloadLog.Info($"[填装形态·弹链] 已贴着弹药铭牌（宽 {_w:F0}px，铭牌 {plate.Size.X:F0}px）");
            }
        }

        public override void _Draw()
        {
            if (_w < 4f)
            {
                return;
            }

            float a = _fade;
            float flash = FieldFxEase.OutCubic(_flash);
            float cy = _h * 0.5f;

            // ── 底：一条两端消失的暖色导轨（用 sin(πu) 做两端淡出，不需要渐变贴图）──
            const int Segments = 40;
            float step = _w / Segments;
            float railGain = BeltAlpha * (1f + 2.2f * flash);

            for (int i = 0; i < Segments; i++)
            {
                float u = (i + 0.5f) / Segments;
                float seg = Mathf.Sin(Mathf.Pi * u);

                if (seg <= 0.001f)
                {
                    continue;
                }

                DrawRect(
                    new Rect2(i * step, 0f, step + 0.6f, _h),
                    new Color(1f, 0.84f, 0.55f, railGain * seg * a));
            }

            // ── 中：几块缓慢横移的亮块，到两端淡出后从另一端回来 ──
            float linkGain = BeltLinkAlpha * (1f + 1.6f * flash);

            // 消耗弹药时，把最靠近中心的那一块吸向弹药图标
            int suckIdx = -1;
            float suckPhase = 0f;

            if (_suck > 0f)
            {
                float best = 2f;

                for (int i = 0; i < BeltLinkCount; i++)
                {
                    float ph = Mathf.PosMod(_t * BeltSpeed / _w + (float)i / BeltLinkCount, 1f);
                    float d = Mathf.Abs(ph - 0.5f);

                    if (d < best)
                    {
                        best = d;
                        suckIdx = i;
                        suckPhase = ph;
                    }
                }
            }

            for (int i = 0; i < BeltLinkCount; i++)
            {
                float phase = Mathf.PosMod(_t * BeltSpeed / _w + (float)i / BeltLinkCount, 1f);
                float x = phase * _w;
                float y = cy;
                float linkAlpha = linkGain * Mathf.Sin(Mathf.Pi * phase) * a;
                float size = BeltLinkSize;
                float half;

                if (i == suckIdx)
                {
                    // k：0 → 1 被吸走
                    float k = 1f - _suck;
                    x = Mathf.Lerp(suckPhase * _w, _w * 0.5f, k);
                    y = Mathf.Lerp(cy, -_h - BeltGap - size, k);
                    size = Mathf.Lerp(BeltLinkSize, BeltLinkSize * 0.45f, k);
                    linkAlpha *= 1f - k;
                }

                if (linkAlpha <= 0.004f)
                {
                    continue;
                }

                half = size * 0.5f;
                DrawRect(new Rect2(x - half, y - half, size, size),
                         new Color(1f, 0.94f, 0.80f, linkAlpha));
            }
        }
    }
}
