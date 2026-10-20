using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Logging;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Art;

/// <summary>
/// ★★★ <b>「得见光芒」的场上特效（游戏内实现）</b>—— 2026-10-18 <b>整体重做</b>。
///
/// <para><b>两张牌面的话</b>：用户的原话是"太素了"，以及后来的立意
/// <b>"从黑暗中去到光亮处"</b>。这两句话决定了整个结构：</para>
/// <list type="bullet">
///   <item>"太素了" ⇒ 病根不在参数，在工具箱。只有"一个烤好的形状放大缩小"这一种武器，
///         再怎么调都出不来。改用 <see cref="FieldFx"/> 的程序化原语
///         （多边形 + 着色器 + GPU 粒子），并且<b>不再依赖任何 vfx 贴图</b>。</item>
///   <item>"从黑暗到光亮" ⇒ 这条弧线必须由<b>屏幕</b>来演，不能只靠场上几个光点。
///         所以有一整套 <see cref="LightStage"/>：黑暗压下来 →
///         被光凿开一个洞 → 洞一路涨到超出画面把黑暗推走 → 中间穿过一次全屏过曝白光 →
///         世界回来时还带着金色余温。</item>
/// </list>
///
/// <para><b>编排（四拍）</b>：</para>
/// <list type="number">
///   <item><b>熄灭·落柱</b>（莱伊）：0.10 秒内世界被抽空，只剩一道光柱从她脚下拔地而起
///         （内部有向上流动的条纹），柱底炸开一圈地面光环。</item>
///   <item><b>凿开·收束</b>（敌人）：黑幕上开出一个洞（在光汇聚的地方）并随收束张开 ——
///         <b>黑暗不是变淡，是被撑开</b>。18 颗光点拖着锥形彗尾沿弧线吸进去，
///         敌人脚下浮起旋转的法阵，周围光尘上飘。</item>
///   <item><b>顶·脉动</b>：三拍。光球每拍收缩、两个四角星芒反向旋出、一圈光环外扩、
///         洞口也跟着搏动 —— 光一下一下地顶黑暗。旧版这段是空的（只有一次呼吸）。</item>
///   <item><b>浸入·引爆</b>：<b>全屏过曝到纯白</b>（看不见东西，因为你在光里）→
///         白光退去、黑暗的洞一路推出画面 → 中心闪光 / 屏幕折射 / 放射速度线 /
///         双色余烬 / 两道冲击波 → 金色余温慢慢散掉。</item>
/// </list>
///
/// <para><b>保留的既有成果</b>（实机踩坑换来的，一个字都没动）：
/// 收束提前到出牌那一拍、炸开锚在敌人、坐标早到时暂存补发、
/// 拿不到坐标绝不使用 (0,0)、全屏面片是兄弟节点所以要在 <c>_ExitTree</c> 收掉。</para>
/// </summary>
internal partial class CardFieldVfx
{
    /// <summary>总开关（出问题就置 false）。</summary>
    internal const bool SeeTheLightEnabled = true;

    /// <summary>
    /// 预览像素 → 世界单位（预览画布 2559px 宽 ≈ 战场约 1500 世界单位）。
    ///
    /// <para>⚠️ <b>「得见光芒」重做后自己已经不用它了</b>（新特效全按世界单位直接摆），
    /// 但 <c>CardFieldVfx.BulletMode.cs</c> 还在用同一个换算系数，所以保留。</para>
    /// </summary>
    private const float WorldPerPreviewPx = 0.586f;

    // ── 时间轴（秒）── 卡牌会靠这些常量倒推"伤害还要等多久"，不能随便改数值 ──
    private const float SeeGatherEnd = 0.72f;      // 收束结束（光点全吸进中心那一点）
    private const float SeeHoldEnd = 0.98f;        // 停留段的呼吸周期
    private const float SeeBurstTotal = 0.46f;     // 炸开这一段
    private const float SeeGatherMaxWait = 2.6f;   // ★ 兜底（正常用不到）
    private const float SeeHoldShort = 0.30f;      // 收束完之后那"很短的一拍"
    private const float SeeRush = 1.6f;            // 收到"伤害已结算"时把收束轻微加速

    /// <summary>
    /// ★ <b>打伤害之前要等的时间</b>（秒）= 收束 + 停一拍 − 一点点。
    ///
    /// <para>用户 2026-10-12 定的：<b>"伤害应该在特效炸开的瞬间结算"</b>。所以不是"让特效等伤害"，
    /// 而是反过来 —— <b>让伤害等特效收束完</b>：卡自己的 <c>OnPlay</c> 在 <c>DamageCmd</c> 之前
    /// <c>await</c> <see cref="SeeLightHitWait"/> 秒。</para>
    ///
    /// <para>⚠️ 比时间轴<b>少 <see cref="SeeHitLead"/> 秒</b>：伤害钩子必须比 <c>BeginBurst()</c>
    /// <b>早几帧</b>把敌人坐标送到 —— 同一帧里"先炸、后收到坐标"就会炸在容器原点（画布左上角）。</para>
    /// </summary>
    internal const float SeeHitLead = 0.06f;

    /// <summary>伤害是否等特效（置 false ⇒ 立刻结算）。</summary>
    internal const bool SeeHitWaitsForEffect = true;

    /// <summary>
    /// ★★ <b>黑幕的最终不透明度</b>。
    ///
    /// <para>2026-10-12 迭代：0.55（太浅）→ 0.70（用户："周围还是不够暗"）→ <b>1.00 全黑</b>。</para>
    ///
    /// <para>⚠️ <b>1.0 的含义</b>：整块战斗场景（莱伊、敌人、地形、游戏自己的 VFX）会**完全消失**，
    /// 画面上只剩我们的特效 + UI（手牌/血条在别的 CanvasLayer，不受影响）。</para>
    ///
    /// <para>★ 2026-10-18 重做后它<b>只决定"黑"有多实</b>，不再负责淡出 ——
    /// 黑暗是靠<b>洞口张开</b>退走的（见 <see cref="FieldFxShaders.DarkVeil"/>），
    /// 不是靠不透明度归零。</para>
    /// </summary>
    internal const float SeeDimAlpha = 1.00f;

    /// <summary>
    /// <b>黑暗压下来的时长</b>（秒）。0.18 → <b>0.10</b>：要"被抽走光"的突然感，
    /// 慢慢变暗读起来只是"调了亮度"，不是"光没了"。
    /// </summary>
    private const float SeeDimIn = 0.10f;

    // ── ★ 「从黑暗到光亮」的四个量（单位：以画面高度为 1 的归一化半径）──

    /// <summary>
    /// 收束结束时洞口开到多大。<b>0.30 → 0.16</b>：
    /// 用离线试验台实测过，0.28~0.30 配 soft 0.24 出来的只是**柔和暗角**——
    /// 中间 70% 还是亮的，读起来完全不是"世界被黑暗吞掉"。洞必须小，黑暗才占得住画面。
    /// </summary>
    private const float SeeHoleGather = 0.16f;

    /// <summary>每拍脉动时洞口再涨多少 —— 光一下一下地顶黑暗。</summary>
    private const float SeeHolePulse = 0.030f;

    /// <summary>
    /// 洞口的软边宽度。<b>0.24 → 0.10</b>：软边一宽，交界就糊成渐变，
    /// 看上去像"暗角"而不是"光与暗的边界"。窄边才有推开的力。
    /// </summary>
    private const float SeeHoleSoft = 0.10f;

    /// <summary>莱伊身上那个常开的洞的半径 —— "角色不要黑"。</summary>
    private const float SeeHoleSelf = 0.15f;

    /// <summary>出牌后多久"黑暗被击碎"（秒）。之前是纯黑，之后洞炸开。</summary>
    private const float SeeShatterAt = 0.35f;   // 0.10 → 0.35：全黑要停够久才看得出来

    /// <summary>黑暗裂开的时长（秒）。0.10 太快，一闪而过；0.30 才看得见"裂"。</summary>
    private const float SeeShatterOpen = 0.30f;

    // ── ★ 引爆后"光把黑暗推走"这一段 ──
    //
    // ⚠️⚠️ 这里有一条**必须遵守的时序**：光锋扫过（黑暗被吃）**必须**发生在全白脉冲**之前**。
    //   第一版把两者叠在一起 —— 白光铺在最上层、不透明度 1，正好盖住了洞口 0.30→2.20 的全程，
    //   于是"黑暗被推开"这个核心画面**一次都没露过面**，用户当场指出。
    //   现在的顺序：0~0.50s 光锋扫出去（黑暗可见地被吃掉）→ 0.52s 全白一瞬（到达）→ 0.58s 起退。

    /// <summary>光锋最终推到多远（2.4 远超屏幕对角线 ⇒ 黑暗被完全推出画面）。</summary>
    private const float SeeFrontEnd = 2.40f;

    /// <summary>光锋扫完全程的时间（秒）。<c>InOutCubic</c>：起步慢，好让爆开的星芒先被看见。</summary>
    private const float SeeFrontTime = 0.50f;

    /// <summary>光锋内那层白的起始浓度。<b>必须 &lt; 1</b>，否则锋内死白、爆开的特效全被盖掉。</summary>
    // ⚠️ 0.55 → 0.30：过曝白光挂在光谱环**上面**，太厚就把环整片盖掉了。
    //    光谱要看得见，白光必须让位。
    private const float SeeFloodFill = 0.55f;   // ★ 回到第一次的 0.55

    /// <summary>
    /// 光锋扫完之后，锋内那层白冷却到多少。
    ///
    /// <para>⚠️ 不能恒为 <see cref="SeeFloodFill"/>：那样整个画面会在 55% 白的雾里糊住
    /// 大半秒（0.25s 扫完 → 1.00s 才退干净），爆开的星芒、光环、余烬全被压低对比度，
    /// 读起来是"糊"不是"炸"。让锋后的亮区随时间冷却，才像光扩散后逐渐淡去。</para>
    /// </summary>
    private const float SeeFloodFillEnd = 0.24f;

    /// <summary>全白脉冲的时刻（相对引爆，秒）—— 安排在光锋已经扫出画面之后。</summary>
    private const float SeePulseAtBurst = 0.52f;

    /// <summary>全白脉冲的半宽（秒）。</summary>
    private const float SeePulseHalfBurst = 0.055f;

    /// <summary>白光开始退的时刻与退完的时间（秒）。</summary>
    private const float SeeFloodFadeStart = 0.58f;   // ★ 回到第一次的 0.58
    private const float SeeFloodFade = 0.42f;

    /// <summary>金色余温的峰值不透明度（很低：只是"空气还亮着"，不能糊掉画面）。</summary>
    private const float SeeWarmAlpha = 0.18f;

    /// <summary>头顶高度 = 生物 <c>Hitbox</c> 高度 × 这个系数（0.45 ≈ 头部；见 <see cref="HeadUpOf"/>）。</summary>
    internal const float SeeHeadFrac = 0.45f;

    /// <summary>读不到 <c>Hitbox</c> 时的兜底头顶高度（世界单位）。</summary>
    internal const float SeeHeadUpFallback = 130f;

    /// <summary>
    /// ★ <b>早到的"引爆坐标"先存这里</b>（伤害钩子比出牌钩子先到的情形）。
    /// 旧版就是在这种情形下<b>静默丢弃</b>坐标 → 炸开拿到 (0,0) → 整套炸在容器原点（画布左上角）。
    /// </summary>
    private static Vector2 _pendingEnemy;
    private static bool _hasPendingEnemy;

    /// <summary>
    /// <b>「得见光芒」打伤害前要等多久</b>（秒，0 = 不等）—— 由卡自己的 <c>OnPlay</c> 在
    /// <c>DamageCmd</c> 之前 <c>await</c>。从"这套特效已经收束了多久"倒推，所以出牌钩子
    /// 晚到一点也不会等过头（时序若仍有偏差，改的是这个函数，不是卡）。
    /// </summary>
    internal static float SeeLightHitWait()
    {
        if (!Enabled || !SeeTheLightEnabled || !SeeHitWaitsForEffect || TestModeIsOn())
        {
            return 0f;
        }

        float target = SeeGatherEnd + SeeHoldShort - SeeHitLead;

        if (_activeCustom != null && GodotObject.IsInstanceValid(_activeCustom))
        {
            // 上一套已经在炸 / 已经演完 → 不拖住伤害
            return _activeCustom.Gathering ? Mathf.Max(0f, target - _activeCustom.Elapsed) : 0f;
        }

        return target;      // 起手节点还没建（理论上不会）：按整段等
    }

    /// <summary>
    /// 全屏光暗舞台：<b>黑幕（带洞）+ 金色余温 + 过曝白光</b> 三张铺满容器的面片。
    /// 都是**兄弟节点**（不是特效的子节点），所以要自己负责回收 —— 见 <c>SeeLightVfx._ExitTree</c>。
    /// </summary>
    internal sealed class LightStage
    {
        public ColorRect? Veil;     // 最底下：把世界压黑，但留一个会张开的洞
        public ColorRect? Glow;     // 最上面偏下：白光退去后的金色余温（加色）
        public ColorRect? Flood;    // 最上面：引爆那一瞬的全屏过曝（替换成白）
        public ColorRect? Impact;   // 最最上面：径向拉爆 + 色散 + 暗角的冲击后处理
        public ColorRect? Pinch;    // 再上面：只在敌人身上做局部扭曲
    }

    /// <summary>
    /// ★★★ <b>搭"从黑暗到光亮"的舞台</b>——「得见光芒」的立意就是<b>从黑暗中去到光亮处</b>，
    /// 所以这条弧线必须由<b>屏幕</b>来演，不能只靠场上那几个光点。
    ///
    /// <para><b>三张面片的分工</b>（缺一不可）：</para>
    /// <list type="number">
    ///   <item><b>黑幕 <see cref="FieldFxShaders.DarkVeil"/></b> —— 铺在最底下（index 0），
    ///         只压暗**它下面的**战斗场景；我们的特效是加色叠在它上面的，因此反倒更亮更跳。
    ///         它不是"淡入淡出的黑"，而是**带一个会张开的洞**：黑暗被光从一点推开。</item>
    ///   <item><b>余温 <see cref="FieldFxShaders.LightFloodAdd"/></b> —— 白光退去后，
    ///         金色以极低不透明度弥漫在空气里再慢慢散掉，世界"还亮着"。</item>
    ///   <item><b>过曝 <see cref="FieldFxShaders.LightFlood"/></b> —— 铺在最上面，
    ///         引爆那一刻把画面**替换成纯白**（不是加亮，是看不见）。</item>
    /// </list>
    ///
    /// <para>⚠️ 它**不碰 UI**（卡牌 / 血条 / 能量球在别的 CanvasLayer）。</para>
    /// <para>⚠️ 三张都必须 <c>MouseFilter = Ignore</c>，否则整块画面点不动。</para>
    /// </summary>
    internal static LightStage? CreateLightStage(Node container)
    {
        try
        {
            var stage = new LightStage();

            stage.Veil = MakeFullScreenQuad(container, "SeeLightVeil",
                FieldFxShaders.DarkVeil, bottom: true);
            stage.Glow = MakeFullScreenQuad(container, "SeeLightGlow",
                FieldFxShaders.LightFloodAdd, bottom: false);
            stage.Flood = MakeFullScreenQuad(container, "SeeLightFlood",
                FieldFxShaders.LightFlood, bottom: false);
            stage.Impact = MakeFullScreenQuad(container, "SeeLightImpact",
                FieldFxShaders.Impact, bottom: false);
            stage.Pinch = MakeFullScreenQuad(container, "SeeLightPinch",
                FieldFxShaders.Pinch, bottom: false);

            return stage;
        }
        catch (Exception e)
        {
            LogAlways($"[得见光芒] 光暗舞台创建失败：{e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>铺满容器的一张着色器面片；<paramref name="bottom"/> 决定排到最底还是最上。</summary>
    private static ColorRect MakeFullScreenQuad(Node container, string name, Shader shader, bool bottom)
    {
        var quad = new ColorRect
        {
            Name = name,
            Color = Colors.White,                            // 着色器自己写 COLOR，这里只占位
            MouseFilter = Control.MouseFilterEnum.Ignore,    // ★ 不许挡住点击
            Material = new ShaderMaterial { Shader = shader },
        };

        container.AddChild(quad);

        // ── ★★ 铺满：**锚点 + 显式把四个 offset 置 0**，两步都要 ──
        //    只调 SetAnchorsPreset 时 offset 可能保留旧值；而且见下面的兜底。
        quad.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        quad.OffsetLeft = 0f;
        quad.OffsetTop = 0f;
        quad.OffsetRight = 0f;
        quad.OffsetBottom = 0f;

        // ── ★★ 兜底：容器自己可能**没有满屏的布局矩形** ──
        //    `CombatVfxContainer` 是"世界坐标的 VFX 容器"，它承载的是 Node2D 系的特效，
        //    本身不参与 UI 布局 ⇒ 它的 Size 很可能是 (0,0) 或很小。
        //    那种情况下 FullRect 锚点算出来的面片也是 0 尺寸 ——
        //    **整块黑幕/白光静默地什么都不画，且不报任何错**（用户 2026-10-18："黑暗没表现出来"）。
        //    所以这里显式量一次：容器没尺寸就直接按视口大小铺。
        Vector2 containerSize = container is Control c ? c.Size : Vector2.Zero;
        Vector2 viewportSize = (container is CanvasItem ci ? ci.GetViewportRect().Size : Vector2.Zero);
        bool forced = false;

        if (containerSize.X < 16f || containerSize.Y < 16f || viewportSize == Vector2.Zero)
        {
            if (viewportSize != Vector2.Zero && container is CanvasItem holder)
            {
                // 把"视口原点"换算到容器的局部坐标系，再从那里铺一屏
                Vector2 originLocal = holder.GetGlobalTransform().AffineInverse() * Vector2.Zero;
                quad.Position = originLocal;
                quad.Size = viewportSize;
                forced = true;
            }
        }

        container.MoveChild(quad, bottom ? 0 : container.GetChildCount() - 1);
        FieldFx.SetParam(quad, "alpha", 0f);

        // ── 铁律 57 自检：shader 编译失败时 Godot **静默忽略整个材质、不报错**，
        //    唯一可查的信号是 uniform 列表为空。把它变成一条明确日志。
        FieldFx.ReportShaderHealth(shader, name);

        LogAlways($"[得见光芒] 全屏面片 {name}：容器尺寸={containerSize}，" +
                  $"视口={viewportSize}，{(forced ? "★ 容器无布局矩形 → 已按视口强制铺满" : "按容器锚点铺满")}");

        return quad;
    }

    /// <summary>
    /// <b>莱伊"头顶"离胸口多高</b>（世界单位）—— 光柱的起点。
    ///
    /// <para>口径照抄游戏自己"往生物上方放东西"的写法（<c>NSpeechBubbleVfx</c> 用
    /// <c>Hitbox.Size.Y × 0.5 × 0.75</c> 把气泡放到头顶之上），我们取
    /// <c>Hitbox.Size.Y × <see cref="SeeHeadFrac"/></c>（略低于那个值 = 正好落在头部）。
    /// 读不到 <c>Hitbox</c> 就退回 <see cref="SeeHeadUpFallback"/>。</para>
    /// </summary>
    private static float HeadUpOf(object? self)
    {
        try
        {
            object? node = CreatureNodeOf(self);
            object? hitbox = node?.GetType().GetProperty("Hitbox")?.GetValue(node);
            object? size = hitbox?.GetType().GetProperty("Size")?.GetValue(hitbox);

            if (size is Vector2 s && s.Y > 1f)
            {
                return s.Y * SeeHeadFrac;
            }
        }
        catch (Exception)
        {
            // 读不到就退回兜底值，不打扰战斗
        }

        return SeeHeadUpFallback;
    }

    /// <summary>
    /// <b>出牌那一刻起手</b>：在**莱伊**身上建一个节点（落柱 + 收束 + 指向敌人的光束）并记下来。
    /// 引爆由 <see cref="ExplodeActiveCustom"/> 在**真正造成伤害**那一刻触发，参数是敌人的世界坐标。
    /// </summary>
    /// <param name="target">本次出牌的目标生物（<c>CardPlay.Target</c>）—— 光和收束都指向它。</param>
    internal static SeeLightVfx? SpawnSeeLightGather(object? self, object? target = null)
    {
        if (!Enabled || !SeeTheLightEnabled || self == null || TestModeIsOn())
        {
            return null;
        }

        // ★ 复用守卫：只有"还在收束、没开始炸"的那一个才复用（避免一次出牌建两层）；
        //   已在炸的节点说明是上一张牌的残留，直接不管它。
        // ⚠️ 原来只在 Gathering（还在收束）时复用 —— 于是"上一发还没播完又来一发"时
        //    会并排跑两个节点，第二个的尘土/烟雾晚一拍出现，看起来就是莫名其妙又飘一次。
        //    现在：只要还活着就复用，不重复建。
        if (_activeCustom != null && GodotObject.IsInstanceValid(_activeCustom))
        {
            return _activeCustom;
        }

        try
        {
            Node? container = VfxParentForEffects();

            if (container == null)
            {
                LogAlways("[得见光芒] 拿不到 CombatVfxContainer，跳过");
                return null;
            }

            Vector2 here = ChestPos(self);

            if (here == Vector2.Zero)
            {
                LogAlways("[得见光芒] 起手失败：自身世界坐标为 (0,0)");
                return null;
            }

            // ★ 光柱的朝向：**出牌那一刻**就取敌人坐标（原版 HandOfGreed 证明这张房间注册表
            //   在 OnPlay 之前就查得到）。拿不到就先竖直，等坐标补上。
            Vector2 foe = target == null ? Vector2.Zero : ChestPos(target);
            float headUp = HeadUpOf(self);

            var vfx = new SeeLightVfx();
            vfx.Configure(here, headUp, foe);
            container.AddChild(vfx);
            vfx.AttachStage(CreateLightStage(container));   // ★ 黑幕(带洞) + 过曝白光 + 金色余温

            // ★ 伤害钩子比出牌钩子先到（旧版就是这里把坐标丢了 → 炸在画布左上角）：起手时补上
            if (_hasPendingEnemy)
            {
                vfx.FeedEnemy(_pendingEnemy);
                _hasPendingEnemy = false;
                LogAlways($"[得见光芒] 起手补上早到的敌人坐标 {_pendingEnemy}");
            }

            LogAlways($"[得见光芒·重做版] 起手：锚在莱伊 {here}，头顶 +{headUp:F0}，" +
                      $"{SeeLightVfx.MoteCount} 颗彗星光点 + 光柱 + 法阵，" +
                      $"指向 {(foe == Vector2.Zero ? "??（先竖直）" : foe.ToString())}");

            return vfx;
        }
        catch (Exception e)
        {
            LogAlways($"[得见光芒] 起手失败：{e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// <b>伤害结算那一瞬引爆</b>：把敌人坐标交给在途节点（它就是炸开的圆心）。
    ///
    /// <para>⚠️ 节点还没建时<b>绝不静默丢弃</b>：坐标存进 <see cref="_pendingEnemy"/>，起手时补上。
    /// 旧版就是在这里"悄悄 return" ⇒ 炸开只能拿到 (0,0) ⇒ 整套炸到容器原点（画布左上角）。</para>
    /// </summary>
    internal static void ExplodeActiveCustom(object? target)
    {
        try
        {
            Vector2 enemy = target == null ? Vector2.Zero : ChestPos(target);

            if (_activeCustom == null || !GodotObject.IsInstanceValid(_activeCustom))
            {
                if (enemy != Vector2.Zero)
                {
                    _pendingEnemy = enemy;
                    _hasPendingEnemy = true;
                    LogAlways($"[得见光芒] 引爆信号早到（起手节点还没建）→ 先存坐标 {enemy}");
                }
                else
                {
                    LogAlways("[得见光芒] 引爆信号到了，但既没有在途节点、也拿不到敌人坐标 → 本次不炸");
                }

                return;
            }

            if (enemy == Vector2.Zero)
            {
                LogAlways("[得见光芒] 引爆时拿不到敌人坐标 → 就地在莱伊身上炸");
            }

            _activeCustom.RequestExplode(enemy);
            LogAlways($"[得见光芒] 伤害已结算 → 打到 {enemy} 并引爆");
        }
        catch (Exception e)
        {
            LogAlways($"[得见光芒] 引爆失败：{e.GetType().Name}: {e.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  整套「得见光芒」
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>整套「得见光芒」。演完自己 <c>QueueFree</c>。</summary>
    internal sealed partial class SeeLightVfx : Node2D
    {
        /// <summary>光点数量。</summary>
        internal const int MoteCount = 18;

        /// <summary>
        /// 炸完之后还要活多久（秒）—— 让冲击波、折射、余烬、<b>金色余温</b>都能演完再回收。
        ///
        /// <para>⚠️ 余温要到引爆后 1.42s 才完全散掉，所以这里必须 ≥ 0.96，
        /// 否则节点先被回收、余温被硬切在半亮状态（屏幕会突然"掉一格亮度"）。</para>
        /// </summary>
        private const float BurstTail = 1.05f;   // 尘土已移除，回到正常寿命

        /// <summary>竖直光柱开关（见 <c>BuildColumn</c> 的说明：卡图上没有这个元素，默认关）。</summary>
        private const bool SeeColumnVisible = true;    // ★ 恢复（认可那版是有的）

        // ── 光束基础宽度（爆开时按倍数撑开，所以必须记住基准值）──
        private const float BeamSoftW = 28f;
        private const float BeamMidW = 12f;
        private const float BeamCoreW = 3.5f;

        /// <summary>收束阶段光束只有基准宽度的这么细 —— "一开始很细一根"。</summary>
        private const float BeamThinMul = 0.32f;

        // ── 配色 ★★ 直接取自卡图 SeeTheLight.png 的实测色值 ──
        //
        // 卡图统计（image_palette）：红 #d81b1b 37%、黑 #101010 24%、橙 #f07a1b 11%、
        // 棕 #7a4a21 8.5%、白 #f5f5f5 8.3%、暗红 #8f1d1d 8.3%、黄 #f2d024 0.8%，
        // **蓝 1.1% / 青 0.1%**（那是画面里的极少量边缘，不是设计意图）。
        //
        // ⚠️⚠️ 所以这里**绝对不能有冷色**。第一版我按 P3 学了套"冰蓝撞色"，
        //   结果和这张红调卡图完全打架 —— 用户的原话是"感觉有点变成魔法了"。
        //   正确的关系是：**白热核心 → 奶油 → 橙 → 红**，一路往下烧，落在卡图的红底里。
        private static readonly Color CWhite = new(0.98f, 0.98f, 0.96f);   // 星爆核心
        private static readonly Color CCream = new(1.00f, 0.93f, 0.72f);   // 卡图那圈光环的本色
        private static readonly Color CGold = new(1.00f, 0.84f, 0.42f);
        private static readonly Color COrange = new(0.96f, 0.52f, 0.15f);
        private static readonly Color CRed = new(0.82f, 0.13f, 0.10f);     // 最外层，落回卡图红底

        // ── 结构 ──
        private sealed class Mote
        {
            public Sprite2D Halo = null!;       // 柔光晕（程序化软光点）
            public Sprite2D Pin = null!;        // ★ 白热钉核：没有它，光点只是一坨褐色软斑
            public Polygon2D Core = null!;      // 硬核（抗锯齿多边形）
            public Polygon2D Trail = null!;     // 锥形彗尾
            public Vector2 P0, Ctrl, P1;        // 路径：二次贝塞尔（相对 _spinRoot）
            public float T0, T1;                // 出生 / 到达时刻（秒）
            public float Size;                  // 到达时的硬核半径
            public float HaloR;                 // 光晕半径
            public float Gain;                  // 亮度
        }

        private readonly List<Mote> _motes = new();

        private Node2D _spinRoot = null!;       // 收束层（敌人身上；带随机旋转）
        private Node2D _columnRoot = null!;     // 竖直光柱（莱伊脚下；世界朝向，不转）
        private Node2D _aimRoot = null!;        // 光束支座（莱伊头顶，转向敌人）
        private Node2D _burstRoot = null!;      // 炸开层（敌人身上）

        private ColorRect _column = null!;      // 光柱本体（Pillar 着色器）
        private ColorRect _columnBase = null!;  // 柱底的地面光环（Ring 着色器）
        private ColorRect _orbitInner = null!;  // 卡图那圈奶油色亮环（空中、倾斜）
        private ColorRect _orbitOuter = null!;  // 外圈更淡的橙，负责光晕体积

        private Line2D _beamSoft = null!, _beamMid = null!, _beamCore = null!;

        private Sprite2D _centerGlow = null!;   // 白热炽核：把所有射线交汇处的硬边"烧掉"
        private Sprite2D _centerHalo = null!;
        private Sprite2D _centerHaloWide = null!;
        private Polygon2D _centerCore = null!;
        private Polygon2D _flareA = null!;
        private Polygon2D _flareB = null!;

        /// <summary>脉动的三拍时刻（秒，绝对值）。</summary>
        private static readonly float[] PulseAt =
        [
            SeeGatherEnd + 0.05f,
            SeeGatherEnd + 0.16f,
            SeeGatherEnd + 0.27f,
        ];

        /// <summary>脉动三角窗的半宽（秒）。</summary>
        private const float PulseHalf = 0.085f;

        private Vector2 _worldPos;
        private Vector2 _enemyPos;
        private float _t;
        private float _burstT;
        private float _freeT;
        private bool _explode;
        private bool _bursting;
        private bool _hasEnemy;
        private float _waitEnemy;
        private float _headUp = SeeHeadUpFallback;
        private Vector2 _aimTarget;
        private bool _hasAim;
        private bool _spinFading;

        /// <summary>到点后最多再等这么久；仍没有就落在莱伊身上（绝不用容器原点）。</summary>
        private const float EnemyGrace = 0.15f;

        /// <summary>还没拿到敌人坐标时光束先用这个长度（竖直朝上；正常看不到）。</summary>
        private const float SeeBeamFallbackLen = 520f;

        /// <summary>还在收束/停留阶段（可被复用、可被引爆）。</summary>
        internal bool Gathering => !_bursting;

        /// <summary>本套已经走了多久（秒）—— 供 <c>SeeLightHitWait()</c> 倒推"伤害还要等多久"。</summary>
        internal float Elapsed => _t;

        /// <summary>本场播放的随机朝向（弧度）。</summary>
        internal float Spin { get; private set; }

        // ───────────────────────── 构建 ─────────────────────────

        internal void Configure(Vector2 worldPos, float headUp, Vector2 foePos)
        {
            _worldPos = worldPos;
            _headUp = headUp > 1f ? headUp : SeeHeadUpFallback;
            _aimTarget = foePos;
            _hasAim = foePos != Vector2.Zero;
            Spin = GD.Randf() * Mathf.Tau;

            var rng = new Random((int)(Spin * 100000f));

            _spinRoot = new Node2D { Name = "SpinRoot", Rotation = Spin };
            _columnRoot = new Node2D { Name = "ColumnRoot" };
            _aimRoot = new Node2D { Name = "AimRoot" };
            _burstRoot = new Node2D { Name = "BurstRoot", Visible = false, Rotation = Spin };
            AddChild(_spinRoot);
            AddChild(_columnRoot);
            AddChild(_burstRoot);
            AddChild(_aimRoot);

            BuildColumn();
            BuildBeam();
            BuildGather(rng);
            BuildCenterOrb();

            UpdateAim();
            ReanchorGather();
        }

        /// <summary>
        /// ★ <b>竖直光柱</b>（莱伊脚下）—— 旧版完全没有这个元素。
        ///
        /// <para>做法：<c>Pillar</c> 着色器四边形，<b>支点放在底边中点</b>，
        /// 于是 <c>Scale.y</c> 从 0 长到 1 就是"从地面拔地而起"；
        /// 着色器内部那层 `sin(UV.y*freq + TIME*speed)` 会让柱体里有能量向上流动。</para>
        /// </summary>
        private void BuildColumn()
        {
            // 胸口 → 脚底 ≈ chest + hitbox/2；而 _headUp = hitbox*0.45 ⇒ 脚底 ≈ _headUp/0.45*0.5
            float feetY = _headUp / SeeHeadFrac * 0.5f;
            _columnRoot.Position = new Vector2(0f, feetY);

            const float width = 118f;

            // ★★ 2026-10-18：**竖直光柱关掉**。
            //   卡图上根本不存在竖直光柱（它是"八芒星爆 + 一圈光环 + 红天 + 黑剪影"），
            //   而且它是个矩形面片，不管怎么收窄都读作"一块板"。用户两次说"光柱不好看"。
            //   莱伊身边的"她在发光"这一拍，交给柱底那圈地面光环 + 指向敌人的光束去表达。
            //   想恢复：把 SeeColumnVisible 改回 true。

            float height = feetY + _headUp * 1.35f;   // 2.6 → 1.35：光柱调低，别顶到画面上沿

            _column = new ColorRect
            {
                Visible = SeeColumnVisible,
                Size = new Vector2(width, height),
                Position = new Vector2(-width / 2f, -height),
                PivotOffset = new Vector2(width / 2f, height),   // ★ 底边中点
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Color = Colors.White,
                Scale = new Vector2(1f, 0f),
                Material = new ShaderMaterial { Shader = FieldFxShaders.Pillar },
            };
            _columnRoot.AddChild(_column);
            FieldFx.SetParam(_column, "tint", CGold);
            FieldFx.SetParam(_column, "alpha", 0f);
            FieldFx.SetParam(_column, "streak_amt", 0.30f);
            FieldFx.SetParam(_column, "taper", 0.42f);      // 顶端收到 42% 宽，成锥形光柱

            // 柱底的地面光环（压扁 = 透视）
            _columnBase = FieldFx.ShaderQuad(_columnRoot, FieldFxShaders.Ring, 460f, 0.28f);
            FieldFx.SetParam(_columnBase, "tint", COrange);
            FieldFx.SetParam(_columnBase, "alpha", 0f);
        }

        /// <summary>
        /// ★ <b>光柱的两条时间轴</b>——<b>必须在 <c>_Ready</c> 里启动，不能在 <see cref="Configure"/> 里</b>。
        ///
        /// <para>⚠️ <b>踩坑点</b>：<see cref="FieldFx.Animate"/> 内部是
        /// <c>node.CreateTween()</c>，而它要求节点<b>已经在场景树里</b>；
        /// 而 <c>Configure()</c> 是在 <c>AddChild(vfx)</c> <b>之前</b>调的
        /// （见 <c>SpawnSeeLightGather</c>，因为要先 <c>Configure</c> 再挂上去）。
        /// 在 <c>Configure</c> 里开 tween 会拿不到 SceneTree ⇒ 光柱整段不出现。</para>
        /// </summary>
        private void StartColumnTimeline()
        {
            if (_column == null || !GodotObject.IsInstanceValid(_column))
            {
                return;
            }

            // 拔地而起 → 保持 → 后段收掉（一条时间轴跑完整个生命周期）
            float life = SeeGatherEnd + SeeHoldShort + SeeBurstTotal;

            FieldFx.Animate(_column, life, t =>
            {
                float rise = FieldFxEase.OutExpo(Mathf.Min(1f, t * 7f));
                float fade = 1f - FieldFxEase.InCubic(Mathf.Max(0f, (t - 0.62f) / 0.38f));

                _column.Scale = new Vector2(Mathf.Lerp(0.35f, 1f, rise), rise);
                // 0.95 → 0.62：光柱不该是一块实心色板，该是"空气被照亮"
                FieldFx.SetParam(_column, "alpha", 0.62f * fade);
            });

            FieldFx.Animate(_columnBase, life * 0.55f, t =>
            {
                float e = FieldFxEase.OutExpo(t);
                FieldFx.SetParam(_columnBase, "radius", Mathf.Lerp(0.06f, 0.9f, e));
                FieldFx.SetParam(_columnBase, "width", Mathf.Lerp(0.06f, 0.012f, e));
                FieldFx.SetParam(_columnBase, "alpha", 0.85f * (1f - FieldFxEase.InCubic(t)));
            });
        }

        /// <summary>
        /// ★ <b>从莱伊头顶指向敌人的光束</b>——<b>三层叠加</b>才是"有体积的光"，
        /// 单层亮线看着像塑料条。
        ///
        /// <para>外层：宽、暗、微微偏琥珀；中层：金色；内芯：窄、纯白、接近全亮。
        /// 三层都是加色混合，重叠处自然过曝成白 —— 这就是"发光"的物理来源。</para>
        /// </summary>
        private void BuildBeam()
        {
            _beamSoft = MakeBeamLine(BeamSoftW, new Color(COrange, 0f));
            _beamMid = MakeBeamLine(BeamMidW, new Color(CGold, 0f));
            _beamCore = MakeBeamLine(BeamCoreW, new Color(CWhite, 0f));
        }

        private Line2D MakeBeamLine(float width, Color color)
        {
            var line = new Line2D
            {
                Points = [Vector2.Zero, new Vector2(SeeBeamFallbackLen, 0f)],
                Width = width,
                DefaultColor = color,
                Material = AddMaterial(),
                JointMode = Line2D.LineJointMode.Round,
            };
            _aimRoot.AddChild(line);
            return line;
        }

        /// <summary>
        /// ★★ <b>光汇聚点外面那一圈"光环"</b>——这是<b>对着卡图改的</b>。
        ///
        /// <para><b>卡图上是什么</b>（<c>SeeTheLight.png</c> 我直接看过）：莱伊举着手，
        /// 指尖上方是一颗八芒白光爆，外面套着<b>一圈干净、发亮、倾斜的椭圆光环</b>，
        /// 背景是纯红的天、四周是黑色剪影。<b>完全没有法阵 / 符文 / 刻度。</b></para>
        ///
        /// <para>⚠️⚠️ <b>第一版这里用的是 <c>MagicCircle</c></b>（48 格刻度环 + 虚线环 + 六边形，
        /// 躺在地上）—— 那是标准的"法师施法"，和卡图差了十万八千里，
        /// 用户当场指出"感觉有点变成魔法了"。现在换成卡图那一圈：
        /// <b>干净的环、浮在空中、倾斜</b>，用 <see cref="FieldFxShaders.Ring"/>
        /// （只有硬环 + 柔光，没有任何刻度）。</para>
        ///
        /// <para>两圈：内圈是卡图的奶油色主线，外圈更宽更暗、偏橙，负责把光"垫"出体积。</para>
        /// </summary>
        private void BuildOrbit()
        {
            // 内圈：卡图里那圈亮环（厚、亮、奶油色）
            // 780 → 560：之前那道椭圆横跨半个屏幕，读作"瞄准环"而不是"光在汇聚"。
            _orbitInner = FieldFx.ShaderQuad(_spinRoot, FieldFxShaders.Ring, 560f, 0.26f);
            FieldFx.SetParam(_orbitInner, "tint", CCream);
            FieldFx.SetParam(_orbitInner, "tint2", COrange);
            FieldFx.SetParam(_orbitInner, "alpha", 0f);
            FieldFx.SetParam(_orbitInner, "glow_amt", 0.55f);
            FieldFx.SetParam(_orbitInner, "radius2", 0.0f);   // ★ 关掉第二道细环：双环像几何图形
            FieldFx.SetParam(_orbitInner, "gap_amt", 0.0f);
            _orbitInner.Rotation = -0.10f;      // 稍微倾斜 —— 卡图那圈是斜的，不是正椭圆

            // 外圈：更大更淡，把光晕撑开
            _orbitOuter = FieldFx.ShaderQuad(_spinRoot, FieldFxShaders.Ring, 1040f, 0.34f);
            FieldFx.SetParam(_orbitOuter, "tint", COrange);
            FieldFx.SetParam(_orbitOuter, "tint2", CRed);
            FieldFx.SetParam(_orbitOuter, "alpha", 0f);
            FieldFx.SetParam(_orbitOuter, "glow_amt", 0.40f);
            _orbitOuter.Rotation = 0.05f;
        }

        /// <summary>
        /// ★★ <b>18 颗彗星光点</b>：每颗 = 柔光晕 + 硬核 + 锥形拖尾。
        ///
        /// <para>拖尾不是"记最近几帧的位置"，而是<b>沿同一条贝塞尔解析采样</b>——
        /// 因为路径是已知的（P0 → Ctrl → P1），尾巴就是 <c>P(u-δ) … P(u)</c> 这几个点。
        /// 好处：无状态、不会因为掉帧变短、也不需要每帧维护历史缓冲。</para>
        /// </summary>
        private void BuildGather(Random rng)
        {
            BuildOrbit();

            for (int i = 0; i < MoteCount; i++)
            {
                float ang = Mathf.Tau * i / MoteCount + Range(rng, -0.25f, 0.25f);

                // ★ 半径对齐旧版（旧版 = Range(780,1150) 预览像素 × 0.586 = 457~674 世界单位）。
                //   旧版这些数值是对着设计稿逐帧比过的，不要凭感觉放大。
                float radius = Range(rng, 470f, 780f);
                float squash = Range(rng, 0.40f, 0.68f);

                // 起点：敌人周围一个扁圆
                Vector2 p0 = new Vector2(Mathf.Cos(ang) * radius, Mathf.Sin(ang) * radius * squash);

                // 控制点：向切向甩出去 ⇒ 走一条弧线而不是直线冲刺
                Vector2 mid = p0 * 0.5f;
                Vector2 tangent = new Vector2(-Mathf.Sin(ang), Mathf.Cos(ang)) * squash;
                Vector2 ctrl = mid + tangent * Range(rng, -260f, 260f) * (rng.Next(2) == 0 ? -1f : 1f)
                               + p0.Normalized() * Range(rng, -140f, 220f);

                float t0 = Range(rng, 0.02f, 0.22f);
                float t1 = t0 + Range(rng, 0.34f, 0.48f);

                Sprite2D halo = MakeSoftDot(_spinRoot, 1f, Colors.Transparent);
                Sprite2D pin = MakeSoftDot(_spinRoot, 1f, Colors.Transparent);    // 白热钉核

                // 硬核用**单位圆**再靠 Scale 定尺寸 —— 这样每颗粒子的大小直接是 m.Size，
                // 不用回头去改多边形顶点（改顶点会连带触发 Reshape 的 uniforms 上传）。
                Polygon2D core = FieldFx.Poly(
                    _spinRoot, FieldFx.Circle(1f, 10), new Color(CWhite, 0f), additive: true);

                Polygon2D trail = FieldFx.Poly(
                    _spinRoot, FieldFx.Circle(1f, 6), new Color(CGold, 0f), additive: true);
                trail.Visible = false;

                _motes.Add(new Mote
                {
                    Halo = halo,
                    Pin = pin,
                    Core = core,
                    Trail = trail,
                    P0 = p0,
                    Ctrl = ctrl,
                    P1 = Vector2.Zero,                       // 终点 = 敌人胸口
                    T0 = t0,
                    T1 = Mathf.Min(t1, SeeGatherEnd * 0.92f), // 保证在收束结束前全部到位

                    // ★ 半径对齐旧版：旧版硬核是 46~74 世界单位的**直径**（≈ 半径 23~37），
                    //   光晕是再 2.1 倍（≈ 半径 48~78）。
                    Size = Range(rng, 22f, 36f),
                    // ★ 晕收小（48~80 → 34~58）：晕一大，整颗光点就摊成一个棕褐色软斑，
                    //   读不出"灼热的核心"。核心与晕的比例才是"光点"的关键。
                    HaloR = Range(rng, 34f, 58f),
                    Gain = Range(rng, 1.1f, 1.5f),
                });
            }
        }

        /// <summary>
        /// ★★ <b>中心光球</b>：柔光晕 + 更外一圈大光晕 + 硬核 + 两个反向旋转的四角星芒。
        ///
        /// <para>两个星芒的初始 <c>alpha 是 0</c>——它们<b>只在脉动那几拍才亮</b>。
        /// 这正是"停顿段不空"的关键：平时几乎看不见，一拍亮起来，
        /// 观众读到的是"它在蓄力"，而不是"卡住了"。</para>
        /// </summary>
        /// <para><b>★ 2026-10-18 对着卡图修正</b>：原来这里是<b>两个反向旋转的四角星</b>——
        /// "反向旋转"是法阵 / 魔法印章的典型语汇，正是"感觉有点变成魔法了"的来源之一。
        /// 卡图上是<b>一颗静态的八芒白光爆</b>（细长射线，不转），所以改成：
        /// 主星芒是 8 芒、<b>不旋转</b>；副星芒更大更淡、同样不旋转，只负责把光晕撑开。
        /// 它们仍然只在脉动那几拍才亮起来 —— 那是"蓄力"，和"魔法"无关。</para>
        /// </summary>
        private void BuildCenterOrb()
        {
            _centerGlow = MakeSoftDot(_spinRoot, 1f, Colors.Transparent);   // ★ 白热炽核（最内层）
            _centerHalo = MakeSoftDot(_spinRoot, 1f, Colors.Transparent);
            _centerHaloWide = MakeSoftDot(_spinRoot, 1f, Colors.Transparent);

            _centerCore = FieldFx.Poly(_spinRoot, FieldFx.Circle(26f, 14), new Color(CWhite, 0f), additive: true);

            // ★ 8 芒、inner 很小 ⇒ 细长射线，和卡图一致；**不旋转**
            // ★★ 关键：给射线做"沿长度衰减"的顶点色。
            //    纯色多边形读起来是纸片；白 → 暖 → 透明 才像光。
            // 300/470 → 120/190：射线太长会和光环叠成"准星"，短而亮才对（卡图就是这样）
            _flareA = FieldFx.Poly(_spinRoot, FieldFx.Star(120f, 8, 0.10f), Colors.White, additive: true);
            _flareB = FieldFx.Poly(_spinRoot, FieldFx.Star(190f, 8, 0.07f), Colors.White, additive: true);
            _flareB.Rotation = Mathf.Pi / 8f;   // 错开 22.5° ⇒ 两圈射线交错成 16 芒的丰富感

            FieldFx.SetStarGradient(_flareA, new Color(CWhite, 1.00f), new Color(COrange, 0.00f));
            FieldFx.SetStarGradient(_flareB, new Color(CCream, 0.75f), new Color(CRed, 0.00f));

            // ★ 收束段那两个星芒也挂边缘色散 —— 光的外缘先开始"散"，才是真的光
            FieldFx.SetParam(_flareA, "fringe", 0.55f);
            FieldFx.SetParam(_flareB, "fringe", 0.55f);
            FieldFx.SetParam(_flareA, "fringe_width", 7.0f);
            FieldFx.SetParam(_flareB, "fringe_width", 7.0f);
        }

        /// <summary>一个程序化软光点精灵（<see cref="FieldFx.SoftDot"/> 是 32×32，靠缩放定尺寸）。</summary>
        private static Sprite2D MakeSoftDot(Node parent, float worldRadius, Color color)
        {
            var sp = new Sprite2D
            {
                Texture = FieldFx.SoftDot,
                Material = FieldFx.Additive,
                Modulate = color,
                Scale = Vector2.One * (worldRadius / 16f),   // SoftDot 半径 = 16px
            };
            parent.AddChild(sp);
            return sp;
        }

        // ───────────────────────── 定位 ─────────────────────────

        /// <summary>
        /// ★ <b>收束的落点搬到敌人身上</b>（用户 2026-10-12："光点不应该往敌人身上聚集吗"）。
        /// 节点锚点仍是莱伊（光柱要从她头上起算），所以这里是**相对位移**。
        /// </summary>
        private void ReanchorGather()
        {
            if (_spinRoot != null && GodotObject.IsInstanceValid(_spinRoot))
            {
                _spinRoot.Position = _hasAim ? _aimTarget - _worldPos : Vector2.Zero;
            }
        }

        /// <summary>
        /// <b>把光束摆成"从莱伊头上指向敌人"</b>：支座坐在她头顶，绕 +X 转到敌人方向，
        /// 长度取"头顶到敌人"的距离。拿不到敌人坐标时竖直朝上（兜底长度）。
        /// </summary>
        private void UpdateAim()
        {
            if (_aimRoot == null || !GodotObject.IsInstanceValid(_aimRoot))
            {
                return;
            }

            _aimRoot.Position = new Vector2(0f, -_headUp);

            if (!_hasAim)
            {
                _aimRoot.Rotation = -Mathf.Pi / 2f;         // 竖直朝上（光束沿 +X，-90° 才是朝上）
                SetBeamLength(SeeBeamFallbackLen);
                return;
            }

            Vector2 head = _worldPos + new Vector2(0f, -_headUp);
            Vector2 d = _aimTarget - head;

            _aimRoot.Rotation = d.Angle();
            SetBeamLength(Mathf.Max(d.Length(), 1f));
        }

        private void SetBeamLength(float len)
        {
            Vector2[] pts = [Vector2.Zero, new Vector2(len, 0f)];

            foreach (Line2D? line in new[] { _beamSoft, _beamMid, _beamCore })
            {
                if (line != null && GodotObject.IsInstanceValid(line))
                {
                    line.Points = pts;
                }
            }
        }

        /// <summary>收到"引爆"指令（由命中钩子调用），参数是敌人的世界坐标。</summary>
        internal void RequestExplode(Vector2 enemyPos) => FeedEnemy(enemyPos);

        /// <summary>
        /// <b>记下"炸开的圆心"</b>（也是"坐标早到、起手时补上"那条路用的入口）。
        /// ⚠️ (0,0) 一律当"没拿到"—— 不设 flag，让 <c>_Process</c> 的去抖那一段兜底。
        /// </summary>
        internal void FeedEnemy(Vector2 enemyPos)
        {
            if (enemyPos == Vector2.Zero)
            {
                return;
            }

            _enemyPos = enemyPos;
            _hasEnemy = true;
            _explode = true;

            // ★ 光束若还没瞄到敌人（起手时拿不到坐标），这一刻补上
            if (!_hasAim)
            {
                _aimTarget = enemyPos;
                _hasAim = true;
                ReanchorGather();
                UpdateAim();
            }
        }

        public override void _Ready()
        {
            GlobalPosition = _worldPos;
            SetProcess(true);
            StartColumnTimeline();

            // ★ 编排：出牌 → 全黑 → 黑暗被击碎（壳片飞散）→ 再接上收束/爆开
            FieldFx.After(this, SeeShatterAt, "dark shatter", SpawnDarkShards);      // ★ tween 要求节点已在树里，所以放在这里而不是 Configure
        }

        // ═════════════ 「从黑暗到光亮」：全屏光暗舞台 ═════════════

        /// <summary>黑幕（带洞，铺在最底下）。</summary>
        private ColorRect? _veil;

        /// <summary>引爆那一瞬的全屏过曝白光（铺在最上面）。</summary>
        private ColorRect? _flood;

        /// <summary>白光退去后弥漫在空气里的金色余温（加色）。</summary>
        private ColorRect? _glow;

        /// <summary>冲击后处理（径向拉爆 + 色散 + 暗角），铺在最最上层。</summary>
        private ColorRect? _impact;

        /// <summary>只在敌人身上生效的局部扭曲。</summary>
        private ColorRect? _pinch;

        internal void AttachStage(LightStage? stage)
        {
            _veil = stage?.Veil;
            _flood = stage?.Flood;
            _glow = stage?.Glow;
            _impact = stage?.Impact;
            _pinch = stage?.Pinch;
        }

        /// <summary>
        /// ★ <b>节点离开场景树时收掉三张全屏面片</b> —— 它们是**兄弟节点**、不是子节点，
        /// 本节点被 <c>QueueFree</c>（正常播完 / 异常兜底）都不会带走它们。
        /// 一旦漏收：黑幕会让屏幕永远变黑、白光会让屏幕永远全白 —— 都是灾难。
        /// 所以放在 <c>_ExitTree</c>（所有路径都会走到）。
        /// </summary>
        public override void _ExitTree()
        {
            FreeOverlay(ref _veil);
            FreeOverlay(ref _flood);
            FreeOverlay(ref _glow);
            FreeOverlay(ref _impact);
            FreeOverlay(ref _pinch);
            RestoreTimeScale();      // ★ 顿帧绝不允许留在压慢状态

            base._ExitTree();
        }

        private static void FreeOverlay(ref ColorRect? quad)
        {
            if (quad != null && GodotObject.IsInstanceValid(quad))
            {
                quad.QueueFree();
            }

            quad = null;
        }

        /// <summary>
        /// ★★★ <b>黑幕：黑暗压下来 → 被光凿开 → 被彻底推出画面</b>。
        ///
        /// <para>这是"从黑暗中去到光亮处"的<b>主叙述</b>，比场上那些光点更重要。
        /// 注意它<b>全程不透明度都是 1</b>：黑暗从来没有"变淡"，
        /// 只是它的<b>洞</b>在长大，直到整个洞超出画面。</para>
        /// </summary>
        private void UpdateVeil()
        {
            if (_veil == null || !GodotObject.IsInstanceValid(_veil))
            {
                return;
            }

            // 压黑：极快（"光被抽走"要有突然感）
            float alpha = SeeDimAlpha * Mathf.Clamp(_t / Mathf.Max(1e-3f, SeeDimIn), 0f, 1f);
            FieldFx.SetParam(_veil, "alpha", alpha);

            // ★ 双洞：
            //   center2/hole2 = 莱伊身上，**整段常开** —— "角色不要黑"
            //   center/hole   = 敌人，出牌后由"黑暗被击碎"那一拍张开
            FieldFx.SetParam(_veil, "center2", ScreenUvOf(_worldPos));
            FieldFx.SetParam(_veil, "hole2", SeeHoleSelf);
            FieldFx.SetParam(_veil, "aspect", AspectRatio());

            // ★★ 裂纹：在"全黑"那段里从中心向外爬，光从缝隙里先渗出来；
            //    之后黑暗才碎开（洞张开 + 壳片飞散）。
            float crackT = Mathf.Clamp(_t / Mathf.Max(0.05f, SeeShatterAt), 0f, 1f);
            float crackReach = _bursting
                ? 1.5f
                : Mathf.Lerp(0.08f, 0.80f, FieldFxEase.InCubic(crackT));

            FieldFx.SetParam(_veil, "crack", 0.55f + 0.45f * crackT);
            FieldFx.SetParam(_veil, "crack_reach", crackReach);
            FieldFx.SetParam(_veil, "crack_scale", 5.2f);    // 3.4 → 5.2：碎块再小一档（"有点大"）
            FieldFx.SetParam(_veil, "crack_width", 0.016f);  // 裂纹粗细（块小了要相应加粗一点）
            FieldFx.SetParam(_veil, "crack_tint", CCream);

            bool enemyKnown = _hasAim || _hasEnemy;
            Vector2 focus = _hasAim ? _aimTarget : (_hasEnemy ? _enemyPos : _worldPos);
            FieldFx.SetParam(_veil, "center", ScreenUvOf(focus));

            // ★ 软边必须窄：宽了整块黑幕就糊成一个"暗角"，不再是"光与暗的边界"
            FieldFx.SetParam(_veil, "soft", SeeHoleSoft);

            float hole;

            if (!_bursting)
            {
                // ★★ 编排：出牌 → **全黑**（敌人那边 hole=0）→ 到 SeeShatterAt 那一刻
                //    黑暗被击碎（洞在 0.10s 内炸开），之后才是原来的收束
                float shatter = Mathf.Clamp((_t - SeeShatterAt) / SeeShatterOpen, 0f, 1f);
                float grow = FieldFxEase.OutExpo(shatter);
                float pulse = FieldFxEase.Pulse(_t, PulseHalf, PulseAt);
                hole = Mathf.Lerp(0f, SeeHoleGather, grow) + SeeHolePulse * pulse;
            }
            else
            {
                // ★ 引爆后：洞 = 光锋的半径。两者**必须用同一个数**，
                //   否则黑幕的边界和白光的锋面会各走各的、出现两道边。
                hole = BurstFront();
            }

            FieldFx.SetParam(_veil, "hole", hole);
        }

        /// <summary>
        /// ★★ <b>光锋半径</b>：引爆后那道"推着黑暗走"的光的前沿，单位与黑幕的 <c>hole</c> 相同
        /// （画面高度 = 1）。
        ///
        /// <para>用 <c>InOutCubic</c> 而不是 <c>OutExpo</c>：OutExpo 前 0.1 秒就冲到 90%，
        /// 黑暗会"啪"一下消失、根本看不到被推的过程；<c>InOutCubic</c> 起步慢，
        /// 前 0.15 秒里黑暗还占着大半个屏幕，爆开的星芒先被看清，然后光锋才扫出去。</para>
        /// </summary>
        private float BurstFront()
        {
            float u = Mathf.Clamp(_burstT / Mathf.Max(1e-3f, SeeFrontTime), 0f, 1f);
            return Mathf.Lerp(SeeHoleGather, SeeFrontEnd, FieldFxEase.InOutCubic(u));
        }

        /// <summary>
        /// ★★ <b>引爆的全屏光——两道独立的工序，不能叠在一起</b>：
        /// <list type="number">
        ///   <item><b>光锋扫过</b>（0 → 0.50s）：锋面比四周白，锋内是一层 <c>fill</c> 浓度的白。
        ///         <b>这一段的重点是"看得见黑暗在退"</b> —— 锋外仍是全黑，所以那道白边就是
        ///         光与暗的交界线在往外推。锋内 <c>fill</c> 只有 0.55，
        ///         爆开的星芒/光环依旧透得出来。</item>
        ///   <item><b>全白一瞬</b>（0.52s）：等锋面已经扫出画面，<b>才</b>把 <c>fill</c> 顶到 1 ——
        ///         这一下才是"看不见东西，因为你在光里"。<b>顺序反了就会把黑暗整段盖住</b>
        ///         （第一版就是这么错的）。</item>
        ///   <item><b>退</b>（0.58 → 1.00s）：白降下去，金色余温接上来。</item>
        /// </list>
        /// </summary>
        // ═════════════ 顿帧（hitstop）═════════════

        /// <summary>顿帧期间的时间倍率：0.06 ≈ 几乎静止。</summary>
        private const float HitStopScale = 0.06f;

        /// <summary>顿帧持续（秒，<b>真实时间</b>）。0.05~0.09 是"重"的甜区，再长会卡。</summary>
        private const float HitStopSeconds = 0.07f;

        /// <summary>我们当前是否压着时间倍率（用于可靠还原）。</summary>
        private static bool _timeScaled;

        /// <summary>
        /// ★★★ <b>顿帧</b>——命中那一瞬把全局时间压到 <see cref="HitStopScale"/>，
        /// 停 <see cref="HitStopSeconds"/> 再放开。
        ///
        /// <para><b>为什么这一条比堆十层形状都有效</b>：格斗 / 动作游戏的"重"几乎全部来自顿帧——
        /// 世界停住，观众才"感觉到"这一下打实了；没有它，再华丽的特效也只是"滑过去"。</para>
        ///
        /// <para>⚠️⚠️ <b>这是全局状态，压住不还就等于把整局游戏变慢</b>（灾难级）。三重保险：</para>
        /// <list type="number">
        ///   <item>还原用的定时器带 <c>ignoreTimeScale: true</c>——
        ///         <b>时间被压慢时普通定时器也会变慢</b>，那样永远还不回去；</item>
        ///   <item><c>_ExitTree</c> 里无条件 <see cref="RestoreTimeScale"/>（异常释放也走这里）；</item>
        ///   <item><c>_timeScaled</c> 做幂等守卫，重复调用不会叠加。</item>
        /// </list>
        /// </summary>
        private void BeginHitStop()
        {
            try
            {
                if (_timeScaled)
                {
                    return;
                }

                Engine.TimeScale = HitStopScale;
                _timeScaled = true;

                SceneTree? tree = GetTree();

                if (tree == null)
                {
                    RestoreTimeScale();
                    return;
                }

                // ★ ignoreTimeScale: true —— 必须用真实时间还原
                tree.CreateTimer(HitStopSeconds, true, false, true).Timeout += RestoreTimeScale;
            }
            catch (Exception)
            {
                RestoreTimeScale();
            }
        }

        /// <summary>把全局时间倍率还原（幂等，随时可以调用）。</summary>
        private static void RestoreTimeScale()
        {
            try
            {
                if (_timeScaled)
                {
                    Engine.TimeScale = 1.0;
                    _timeScaled = false;
                }
            }
            catch (Exception)
            {
                // 还原失败也不能再抛
            }
        }

        private void UpdateOverlays()
        {
            Vector2 focus = _hasAim ? _aimTarget : _worldPos;
            Vector2 center = ScreenUvOf(focus);
            float aspect = AspectRatio();

            if (_flood != null && GodotObject.IsInstanceValid(_flood))
            {
                // 锋面半径与黑幕的洞同步（由 UpdateVeil 用同一个 BurstFront() 驱动）
                float reach = BurstFront();

                // 全白一瞬（锋面已出画面之后）
                float pulse = FieldFxEase.Pulse(_burstT, SeePulseHalfBurst, SeePulseAtBurst);
                float fill = Mathf.Max(SeeFloodFill, pulse);

                // 退白
                float fade = 1f - FieldFxEase.OutCubic(Mathf.Clamp(
                    (_burstT - SeeFloodFadeStart) / Mathf.Max(1e-3f, SeeFloodFade), 0f, 1f));

                // 起手瞬间就已是 1（引爆那一帧不能有"渐变进来"的空档）；
                // 未引爆时一律 0 —— 收束阶段屏幕上绝不能有白光。
                float alpha = _bursting ? fade : 0f;

                FieldFx.SetParam(_flood, "alpha", alpha);
                FieldFx.SetParam(_flood, "reach", reach);
                FieldFx.SetParam(_flood, "fill", fill);
                FieldFx.SetParam(_flood, "soft", 0.20f);
                FieldFx.SetParam(_flood, "tint", CWhite);
                FieldFx.SetParam(_flood, "center", center);
                FieldFx.SetParam(_flood, "aspect", aspect);
            }

            if (_impact != null && GodotObject.IsInstanceValid(_impact))
            {
                // ★ 冲击后处理：只有前 0.26 秒，快速衰减。
                //   放久了画面会糊成一片（缩放模糊 + 色散都是"读起来很爽、看久了想吐"的东西）。
                float k = _bursting ? Mathf.Clamp(1f - _burstT / 0.26f, 0f, 1f) : 0f;   // ★ 回到 0.26s
                float e = k * k;                       // 平方衰减：收得干脆

                FieldFx.SetParam(_impact, "alpha", k > 0.001f ? 1f : 0f);
                // ⚠️ 2.9 → 1.25：6 档缩放模糊一旦过强，整幅画面被糊平，
                //    光谱色边就被"平均"掉了 —— 上一轮"光谱没了"就是这个原因。
                FieldFx.SetParam(_impact, "zoom", Mathf.Lerp(0f, 2.9f, e));     // ★ "从非光谱角度"上一版的值
                // ★ 色散 0.0075 → 0.0135：用户认可的"光谱感"主要来自这两个数。
                //   再往上调，中心会开始糊，所以是配着 zoom 一起加的。
                // ★★ 0.0135 → 0.030：色散要**足够大**才看得见。
                //    而且它只对"有色中间调"生效 —— 纯白和纯黑都拆不出颜色，
                //    所以真正让它显现的是下面那圈光谱环，两者必须同时存在。
                FieldFx.SetParam(_impact, "split", Mathf.Lerp(0f, 0.024f, e));   // 0.0135 → 0.024：色散更明显  // ★ "从非光谱角度"上一版的值
                FieldFx.SetParam(_impact, "vignette", Mathf.Lerp(0f, 0.55f, e));
                FieldFx.SetParam(_impact, "center", center);
            }

            if (_pinch != null && GodotObject.IsInstanceValid(_pinch))
            {
                // ★ 只在敌人身上拧一下：先被吸进去（负），再回弹。
                //   时长只有 0.18 秒 —— 扭曲放久了会晕；而且它必须和顿帧同时发生，
                //   否则"画面被拧"和"世界停住"错开，就变成两件事。
                float k = _bursting ? Mathf.Clamp(1f - _burstT / 0.18f, 0f, 1f) : 0f;

                // 前 35% 向内吸、之后回弹到 0（正弦一拱）
                float suck = Mathf.Sin(Mathf.Clamp(_burstT / 0.18f, 0f, 1f) * Mathf.Pi);

                FieldFx.SetParam(_pinch, "alpha", 0f);   // ★ 局部扭曲关掉（用户认可那版没有它）
                FieldFx.SetParam(_pinch, "radius", 0.135f);
                // -0.020 太弱（屏幕上只有几个像素，看不见）→ -0.075 ≈ 整屏 7.5%，明显被吸进去
                FieldFx.SetParam(_pinch, "strength", -0.075f * suck * k);
                FieldFx.SetParam(_pinch, "center", center);
                FieldFx.SetParam(_pinch, "aspect", aspect);
            }

            if (_glow != null && GodotObject.IsInstanceValid(_glow))
            {
                // 余温：压在白光退到一半之后才起来（不然看不出来），再慢慢散掉
                float rise = Mathf.Clamp((_burstT - 0.65f) / 0.32f, 0f, 1f);
                float fade = 1f - Mathf.Clamp((_burstT - 1.00f) / 0.42f, 0f, 1f);

                FieldFx.SetParam(_glow, "alpha", SeeWarmAlpha * rise * fade);
                FieldFx.SetParam(_glow, "reach", 2.60f);
                FieldFx.SetParam(_glow, "fill", 1.0f);
                FieldFx.SetParam(_glow, "soft", 2.60f);      // 整个画面均匀，不要有可见的圆边
                FieldFx.SetParam(_glow, "tint", CGold);
                FieldFx.SetParam(_glow, "center", center);
                FieldFx.SetParam(_glow, "aspect", aspect);
            }
        }

        /// <summary>
        /// <b>把世界坐标换到"全屏面片里的归一化 UV"</b>（黑幕/白光/余温共用）。
        ///
        /// <para>⚠️ 用 <c>GetGlobalTransform()</c>（画布空间）而<b>不是</b>
        /// <c>GetGlobalTransformWithCanvas()</c> —— 后者已经含了相机变换，
        /// 而 <see cref="ChestPos"/> 给的是画布空间坐标，混用会让洞心整体偏掉。</para>
        ///
        /// <para>⚠️ <b>算出来不在画面内就退回正中</b>：宁可洞在中央，
        /// 也不要出现"洞跑到屏幕角落、其余全黑"的坏画面。</para>
        /// </summary>
        private Vector2 ScreenUvOf(Vector2 worldPos)
        {
            var fallback = new Vector2(0.5f, 0.5f);

            if (_veil == null || !GodotObject.IsInstanceValid(_veil))
            {
                return fallback;
            }

            try
            {
                Vector2 size = _veil.Size;

                if (size.X < 16f || size.Y < 16f)
                {
                    return fallback;
                }

                Vector2 local = _veil.GetGlobalTransform().AffineInverse() * worldPos;
                Vector2 uv = local / size;

                if (uv.X < 0.02f || uv.X > 0.98f || uv.Y < 0.02f || uv.Y > 0.98f)
                {
                    LogAlways($"[得见光芒] 洞心换算落在画面外（uv={uv}）→ 退回画面正中");
                    return fallback;
                }

                return uv;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        /// <summary>画面纵横比（着色器用它把洞和光斑修成圆）。</summary>
        private float AspectRatio()
        {
            try
            {
                Vector2 size = _veil != null && GodotObject.IsInstanceValid(_veil)
                    ? _veil.Size
                    : GetViewportRect().Size;

                return size.Y > 8f ? Mathf.Clamp(size.X / size.Y, 0.5f, 4f) : 1.78f;
            }
            catch (Exception)
            {
                return 1.78f;
            }
        }

        // ───────────────────────── 主循环 ─────────────────────────

        public override void _Process(double delta)
        {
            try
            {
                UpdateVeil();        // 黑暗压下来 → 被凿开 → 被推出画面
                UpdateOverlays();    // 过曝白光 + 金色余温

                if (_bursting)
                {
                    _burstT += (float)delta;
                    _freeT += (float)delta;

                    // ★ 活到"炸开 + 尾巴"再回收：旧版在 SeeBurstTotal 就删，
                    //   冲击波/折射/余烬会被硬切断。
                    if (_freeT >= SeeBurstTotal + BurstTail)
                    {
                        QueueFree();
                    }

                    return;
                }

                // ★ 时间轴是**固定的**（不再死等伤害）：收束 → 停一拍 → 炸开；
                //   期间若收到"伤害已结算"，只把收束轻微加速，让炸开更贴近结算那一帧。
                _t += (float)delta * (_explode ? SeeRush : 1f);

                if (_t >= SeeGatherEnd)
                {
                    float held = _t - SeeGatherEnd;

                    if (held >= SeeHoldShort || _t >= SeeGatherMaxWait)
                    {
                        // ★ 到点了但"炸开圆心"还没到手：最多再等 EnemyGrace 秒；
                        //   仍然没有就落在莱伊身上 —— 绝不用 (0,0)（那会炸到画布左上角，踩过两次）。
                        if (!_hasEnemy)
                        {
                            _waitEnemy += (float)delta;

                            if (_waitEnemy < EnemyGrace)
                            {
                                RenderScene(1f, held / Mathf.Max(1e-3f, SeeHoldShort));
                                return;
                            }

                            LogAlways("[得见光芒] 到点仍没收到引爆坐标 → 就地在莱伊身上炸");
                            _enemyPos = _worldPos;
                            _hasEnemy = true;
                        }

                        BeginBurst();
                        return;
                    }

                    RenderScene(1f, held / Mathf.Max(1e-3f, SeeHoldShort));
                    return;
                }

                RenderScene(_t / SeeGatherEnd, 0f);
            }
            catch (Exception)
            {
                QueueFree();                        // 特效出错绝不能拖累战斗
            }
        }

        /// <summary>
        /// <b>收束 / 停留两个阶段共用的逐帧渲染</b>。
        /// </summary>
        /// <param name="k">收束进度 0~1（1 = 光点全部到位）。</param>
        /// <param name="hold">停留段进度 0~1（0 = 刚开始停）。</param>
        private void RenderScene(float k, float hold)
        {
            RenderMotes();
            RenderCenter();

            // 光束：收束前半段从无到有长出来，之后保持，停留段里喘一口气
            float beamIn = FieldFxEase.OutExpo(Mathf.Clamp(_t / 0.16f, 0f, 1f));
            float beamBreathe = 0.86f + 0.14f * Mathf.Sin(_t * 11f);

            SetBeamAlpha(_beamSoft, 0.34f * beamIn * beamBreathe);
            SetBeamAlpha(_beamMid, 0.72f * beamIn * beamBreathe);
            SetBeamAlpha(_beamCore, 1.00f * beamIn * beamBreathe);

            // ★ "一开始很细一根"：收束段光束只有基准宽度的 32%
            SetBeamWidth(BeamThinMul);

            // ★ 光环：起手张开（OutExpo），停留段随脉动一起鼓一下 ——
            //   卡图那圈是**静态**的，所以这里不旋转，只让它的粗细和亮度呼吸。
            float orbitIn = FieldFxEase.OutExpo(Mathf.Clamp(_t / 0.42f, 0f, 1f));
            float orbitPulse = FieldFxEase.Pulse(_t, PulseHalf, PulseAt);
            float holdT = Mathf.Clamp(hold, 0f, 1f);
            float orbitFade = 1f - 0.55f * holdT;

            // ★ 半径收小、线加厚：细而大的环是"准星"，厚而小的环才是"光"
            float ringRadius = Mathf.Lerp(0.55f, 0.74f, orbitIn);
            float ringWidth = 0.075f + 0.040f * orbitPulse;

            SetOrbit(_orbitInner, 0.95f * orbitIn * orbitFade * (0.85f + 0.15f * orbitPulse),
                ringRadius, ringWidth);
            // ⚠️ 外圈必须**很淡**：两圈同等显眼就变成"原子模型"，卡图上只有一圈。
            //    这里它只负责在环外铺一层极淡的暖光，看得出有层次、看不出有第二个环。
            SetOrbit(_orbitOuter, 0.10f * orbitIn * orbitFade,
                ringRadius + 0.055f, ringWidth * 1.9f);
        }

        /// <summary>摆一圈光环：<paramref name="radius"/> / <paramref name="width"/> 都是着色器的归一化参数。</summary>
        private static void SetOrbit(ColorRect? ring, float alpha, float radius, float width)
        {
            if (ring == null || !GodotObject.IsInstanceValid(ring))
            {
                return;
            }

            FieldFx.SetParam(ring, "alpha", Mathf.Clamp(alpha, 0f, 1f));
            FieldFx.SetParam(ring, "radius", radius);
            FieldFx.SetParam(ring, "width", width);
        }

        private void SetBeamAlpha(Line2D line, float a)
        {
            if (GodotObject.IsInstanceValid(line))
            {
                Color c = line.DefaultColor;
                c.A = Mathf.Clamp(a, 0f, 1f);
                line.DefaultColor = c;
            }
        }

        /// <summary>逐帧摆 18 颗光点（含解析彗尾）。</summary>
        private void RenderMotes()
        {
            foreach (Mote m in _motes)
            {
                if (_t < m.T0)
                {
                    HideMote(m);
                    continue;
                }

                float span = Mathf.Max(1e-3f, m.T1 - m.T0);
                float u = (_t - m.T0) / span;

                if (u >= 1f)
                {
                    HideMote(m);
                    continue;
                }

                // ★ 用 InOutCubic 而不是线性：起步还在远处慢慢飘、越靠近越快被吸进去
                float e = FieldFxEase.InOutCubic(u);
                Vector2 p = FieldFxEase.Bezier(m.P0, m.Ctrl, m.P1, e);

                // 越接近中心越小越亮（被压缩进去）
                float shrink = Mathf.Lerp(1f, 0.42f, FieldFxEase.OutCubic(u));
                float bright = Mathf.Lerp(0.55f, 1.6f, FieldFxEase.InCubic(u)) * m.Gain;

                // ★ 三层：大柔光晕（暖橙）→ 中硬核（白多边形）→ 小钉核（纯白，最亮）。
                //   只有晕 + 硬核时它仍是一坨褐色软斑，缺的就是这颗钉核。
                m.Halo.Visible = true;
                m.Halo.Position = p;
                m.Halo.Scale = Vector2.One * (m.HaloR * shrink / 16f);
                m.Halo.Modulate = new Color(COrange, Mathf.Clamp(0.45f * u + 0.10f, 0f, 1f) * Mathf.Min(1f, bright));

                m.Pin.Visible = true;
                m.Pin.Position = p;
                m.Pin.Scale = Vector2.One * (m.Size * 0.34f * shrink / 16f);
                m.Pin.Modulate = new Color(CWhite, Mathf.Min(1f, bright));

                m.Core.Visible = true;
                m.Core.Position = p;
                m.Core.Scale = Vector2.One * (m.Size * shrink);
                m.Core.Color = new Color(CWhite, Mathf.Min(1f, bright * 0.85f));

                // ── 彗尾：沿同一条贝塞尔往回采样 ──
                float tailSpan = 0.30f * Mathf.Pow(1f - u, 0.45f);   // 快到终点时尾巴收短
                var pts = new Vector2[6];

                for (int i = 0; i < pts.Length; i++)
                {
                    float back = tailSpan * (1f - i / (float)(pts.Length - 1));
                    pts[i] = FieldFxEase.Bezier(m.P0, m.Ctrl, m.P1, Mathf.Max(0f, e - back));
                }

                m.Trail.Visible = true;
                m.Trail.Position = Vector2.Zero;
                FieldFx.Reshape(m.Trail, FieldFx.StrokePts(pts, m.Size * 1.5f * shrink, taper: true));
                m.Trail.Color = new Color(COrange, Mathf.Clamp(0.55f * u, 0f, 0.85f));
            }
        }

        private static void HideMote(Mote m)
        {
            m.Pin.Visible = false;
            m.Halo.Visible = false;
            m.Core.Visible = false;
            m.Trail.Visible = false;
        }

        /// <summary>中心光球 + 三拍脉动。</summary>
        private void RenderCenter()
        {
            // 到场进度：光点全到位时光球成型
            float form = FieldFxEase.OutBack(Mathf.Clamp(_t / (SeeGatherEnd * 0.75f), 0f, 1f));

            // ★ 脉动（三角窗）：每一拍给一个 0→1→0 的鼓包
            float pulse = FieldFxEase.Pulse(_t, PulseHalf, PulseAt);

            // 停留段整体收一点，让"蓄满"的感觉更强
            float settle = Mathf.Lerp(1f, 0.86f, Mathf.Clamp((_t - SeeGatherEnd) / Mathf.Max(1e-3f, SeeHoldShort), 0f, 1f));

            float scale = form * settle * (1f + 0.30f * pulse);
            _centerCore.Scale = Vector2.One * Mathf.Max(0.001f, scale);
            _centerCore.Color = new Color(CWhite, Mathf.Clamp(0.35f + 0.65f * form, 0f, 1f));

            // 白热炽核：始终纯白、随脉动放大，负责把八芒交汇处的硬边灼掉
            _centerGlow.Scale = Vector2.One * (26f * scale * (1f + 0.55f * pulse) / 16f);
            _centerGlow.Modulate = new Color(CWhite, Mathf.Clamp(0.55f + 0.45f * form, 0f, 1f));

            _centerHalo.Scale = Vector2.One * (58f * scale / 16f);
            _centerHalo.Modulate = new Color(CGold, 0.42f + 0.45f * pulse);

            _centerHaloWide.Scale = Vector2.One * (150f * scale / 16f);
            _centerHaloWide.Modulate = new Color(COrange, 0.16f + 0.22f * pulse);

            // ★ 星芒**不旋转**（卡图上是静态的八芒）。只在脉动那几拍亮起来。
            //   ⚠️ 别把这里改回"反向旋转"—— 那是魔法印章的语汇，用户明确说不要。
            //   ⚠️ 颜色走的是顶点色（沿射线衰减），所以脉动只能用 Modulate：
            //      再写 Color 会和顶点色相乘、整颗黑掉。
            _flareA.Visible = form > 0.05f;
            _flareB.Visible = form > 0.05f;
            _flareA.Scale = Vector2.One * Mathf.Max(0.001f, form * (0.45f + 0.60f * pulse));
            _flareB.Scale = Vector2.One * Mathf.Max(0.001f, form * (0.40f + 0.55f * pulse));
            _flareA.Modulate = new Color(1f, 1f, 1f, 0.95f * pulse);
            _flareB.Modulate = new Color(1f, 1f, 1f, 0.60f * pulse);
        }

        // ───────────────────────── 引爆 ─────────────────────────

        /// <summary>
        /// <b>切到炸开</b>：收束层柔和收掉、在敌人身上铺开一整套爆开效果。
        ///
        /// <para>★ 和旧版最大的区别：这里不再用 <c>_burstT</c> 逐帧手算每一个元素，
        /// 而是给每个子效果<b>各自开一条 <see cref="FieldFx.Animate"/> 时间轴</b> +
        /// 用 <see cref="FieldFx.After"/> 错开派发。子效果之间互不干扰，
        /// 加一个元素不需要动其它元素的代码。</para>
        /// </summary>
        private void BeginBurst()
        {
            // ⚠️⚠️ 幂等守卫：BeginBurst 会被"收束完成"和"等到敌人坐标"两条路调用。
            //    没有这一行，整套子效果（星爆/碎屑/尘土/烟雾/冲击波）会被生成两遍 ——
            //    表现就是"第一轮结束之后又飘一次"。
            if (_bursting)
            {
                return;
            }

            _bursting = true;
            _burstT = 0f;
            _freeT = 0f;

            if (!_hasEnemy)                         // 兜底：任何路径都不许拿 (0,0) 当圆心
            {
                _enemyPos = _worldPos;
                _hasEnemy = true;
            }

            // ── 收束层柔和淡出（硬切会闪一下）──
            if (!_spinFading)
            {
                _spinFading = true;
                FieldFx.Animate(_spinRoot, 0.11, t =>
                {
                    _spinRoot.Modulate = new Color(1f, 1f, 1f, 1f - t);
                });
                FieldFx.After(_spinRoot, 0.12, "gather hide", () =>
                {
                    if (GodotObject.IsInstanceValid(_spinRoot))
                    {
                        _spinRoot.Visible = false;
                    }
                });
            }

            // ── 炸开层：挪到敌人胸口 ──
            _burstRoot.Position = _enemyPos - GlobalPosition;
            _burstRoot.Visible = true;

            // 光束在这一下之后很快收掉
            AnimateBeamBurst();

            SpawnFlashAndRings();
            SpawnDebris();

            // 目标头顶的小狙击镜（子弹模式也会用它）
            SpawnSniperScope(_burstRoot, new Vector2(0f, -_headUp * 1.25f), 64f);
            SpawnSpectrum();
            SpawnRefract();
            SpawnStarBurst();
            SpawnSpeedLines();
            SpawnEmbers();
            SpawnShockwaves();
            SpawnAfterglow();
        }

        /// <summary>中心闪光 + 两层扩张光环。</summary>
        private void SpawnFlashAndRings()
        {
            // ① 中心闪光：极快、极亮
            ColorRect flash = FieldFx.ShaderQuad(_burstRoot, FieldFxShaders.Flash, 900f);
            FieldFx.SetParam(flash, "tint", CWhite);
            FieldFx.SetParam(flash, "alpha", 0f);

            FieldFx.Animate(flash, 0.30, t =>
            {
                FieldFx.SetParam(flash, "alpha", 1f - FieldFxEase.OutCubic(t));
                flash.Scale = Vector2.One * Mathf.Lerp(0.45f, 1.30f, FieldFxEase.OutExpo(t));
            });

            // ② 外层大环（暖金）+ ③ 内层快环（白）
            SpawnRing(1750f, 0.62f, 34f, CGold, 0.95f, 0.00f);
            SpawnRing(1080f, 0.44f, 18f, CWhite, 0.70f, 0.05f);
        }

        /// <summary>一个扩张的环（<c>Ring</c> 着色器，<b>正圆</b>——用户明确要求过）。</summary>
        private void SpawnRing(float worldRadius, float duration, float width, Color tint,
            float peakAlpha, float delay)
        {
            ColorRect ring = FieldFx.ShaderQuad(_burstRoot, FieldFxShaders.Ring, worldRadius * 2f);
            FieldFx.SetParam(ring, "tint", tint);
            FieldFx.SetParam(ring, "alpha", 0f);
            // ⚠️⚠️ 这里**不要**写 Scale = 0.001f。
            //    那是"从 0 长出来"的写法，但这个环的尺寸由着色器的 radius 参数控制，
            //    quad 本身必须保持满尺寸 —— 曾把光谱环/冲击波/折射整段缩到千分之一，
            //    结果它们一次都没显示过（2026-10-18）。

            float total = duration + delay;

            FieldFx.Animate(ring, total, t =>
            {
                float u = FieldFxEase.Segment(t, delay / total, duration / total);

                if (u <= 0f)
                {
                    FieldFx.SetParam(ring, "alpha", 0f);
                    return;
                }

                float e = FieldFxEase.OutExpo(u);
                FieldFx.SetParam(ring, "radius", Mathf.Lerp(0.05f, 0.92f, e));
                FieldFx.SetParam(ring, "width", Mathf.Lerp(width * 0.0016f, 0.006f, e));
                FieldFx.SetParam(ring, "alpha", peakAlpha * (1f - FieldFxEase.InCubic(u)));
            });

            FieldFx.FreeAfter(ring, total + 0.1);
        }

        /// <summary>
        /// ★★★ <b>光谱层</b>——"帅"的两件套：一圈全色相的能量环 + 一道变形镜头横光。
        ///
        /// <para><b>光谱环</b>：从爆点向外扫一圈、沿圆周走完整个色相。比单色环信息量大得多，
        /// 一眼就是"能量 / 棱镜"，并且和 <see cref="FieldFxShaders.Impact"/> 的色散互相加强
        /// —— 环本身有色相、色散把背景的色拉开，两层一起看就是"整个空间在折射"。</para>
        ///
        /// <para><b>变形横光</b>（anamorphic streak）：一条极细极长的水平白光穿过爆点，
        /// 亮核在中间、两端收尖。这是电影镜头里"强光源"的标志性形状，
        /// 加上这一笔，同样的爆点立刻从"游戏特效"变成"镜头拍的"。</para>
        /// </summary>
        private void SpawnSpectrum()
        {
            // ── ① 光谱环：快速扩张、边扩边淡、色相缓慢转 ──
            ColorRect ring = FieldFx.ShaderQuad(_burstRoot, FieldFxShaders.Spectrum, 2400f);
            FieldFx.SetParam(ring, "alpha", 0f);
            FieldFx.SetParam(ring, "sat", 1.00f);      // ★ 0.72 → 1.00：饱和度拉满，色相才跳
            FieldFx.SetParam(ring, "glow_amt", 1.10f);
            // ⚠️⚠️ 这里**不要**写 Scale = 0.001f。
            //    那是"从 0 长出来"的写法，但这个环的尺寸由着色器的 radius 参数控制，
            //    quad 本身必须保持满尺寸 —— 曾把光谱环/冲击波/折射整段缩到千分之一，
            //    结果它们一次都没显示过（2026-10-18）。

            // 光谱环拉长到 0.85s、扩得更大 —— 它现在是"光谱感"的主载体
            FieldFx.Animate(ring, 0.85, t =>
            {
                float e = FieldFxEase.OutExpo(t);
                FieldFx.SetParam(ring, "radius", Mathf.Lerp(0.08f, 1.15f, e));
                FieldFx.SetParam(ring, "width", Mathf.Lerp(0.085f, 0.010f, e));
                FieldFx.SetParam(ring, "alpha", 0.0f * (1f - FieldFxEase.InCubic(t)));   // ★ 光谱环整条关掉（彩虹扇来源，第一次没有它）
                FieldFx.SetParam(ring, "spin", t * 0.75f);
            });

            FieldFx.FreeAfter(ring, 0.95);

            // ── ② 变形横光：左右两道锥形带 ──
            var streak = new List<Polygon2D>();

            foreach (float dirSign in new[] { -1f, 1f })
            {
                Vector2[] spine =
                [
                    new Vector2(dirSign * 40f, 0f),
                    new Vector2(dirSign * 620f, 0f),
                    new Vector2(dirSign * 1500f, 0f),
                ];

                Polygon2D poly = FieldFx.Poly(_burstRoot,
                    FieldFx.StrokePts(spine, 26f, taper: true),
                    new Color(CCream, 0f), additive: true);
                streak.Add(poly);
            }

            FieldFx.Animate(_burstRoot, 0.40, t =>
            {
                float grow = FieldFxEase.OutExpo(t);
                float fade = 1f - FieldFxEase.InCubic(t);

                foreach (Polygon2D poly in streak)
                {
                    if (GodotObject.IsInstanceValid(poly))
                    {
                        // 横向拉伸 = 光"甩出去"
                        poly.Scale = new Vector2(Mathf.Max(0.001f, Mathf.Lerp(0.08f, 1f, grow)), 1f);
                        poly.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(0.85f * fade, 0f, 1f));
                    }
                }
            });

            foreach (Polygon2D poly in streak)
            {
                FieldFx.FreeAfter(poly, 0.5);
            }
        }

        /// <summary>
        /// ★★★ <b>碎屑</b>——整个特效里<b>唯一的实心物体</b>。
        ///
        /// <para><b>为什么它能单独把观感拉上去</b>：前面所有东西都是"光"——
        /// 加色混合、没有实体、不挡任何东西。人眼对纯光效的"重量"是麻木的；
        /// 一旦出现<b>带自旋和重力的黑色碎块</b>，画面上第一次有了"被打碎的东西"，
        /// 光与实体对比，冲击才落地。</para>
        ///
        /// <para>卡图上就是这层：莱伊周围飘着碎石和黑色剪影。所以这里用<b>近黑的实心多边形</b>
        /// （普通混合，不是加色）+ 一点暖色描边，飞出去时带空气阻力减速、受重力下坠、边转边淡出。</para>
        /// </summary>
        private void SpawnDebris()
        {
            var rng = new Random((int)(_enemyPos.Y * 31f + _enemyPos.X * 17f));
            const int count = 34;
            const float life = 0.90f;

            var shards = new List<(Polygon2D Poly, Vector2 Vel, float Spin, float Size)>();

            for (int i = 0; i < count; i++)
            {
                float ang = Mathf.Tau * i / count + Range(rng, -0.28f, 0.28f);
                float speed = Range(rng, 260f, 820f);
                float size = Range(rng, 12f, 40f);

                // 不规则多边形：随机 3~5 个角、每个角的半径再抖一点 —— 规则形状一眼是图形
                int corners = 3 + rng.Next(3);
                var pts = new Vector2[corners];

                for (int c = 0; c < corners; c++)
                {
                    float a = Mathf.Tau * c / corners + Range(rng, -0.35f, 0.35f);
                    float r = size * Range(rng, 0.55f, 1.35f);
                    pts[c] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                }

                // 普通混合（不是加色）—— 必须是"实心挡光"的东西，才叫碎块
                // ⚠️⚠️ 碎屑**不能是纯黑**：爆开时背景是全黑幕，黑碎片 = 黑上黑，看不见。
                //    提到暗暖灰，再挂上光谱边缘（fringe）—— 读作"被光照到的碎片轮廓"。
                Polygon2D poly = FieldFx.Poly(_burstRoot, pts,
                    new Color(0.13f, 0.09f, 0.08f, 0f), additive: false);
                FieldFx.SetParam(poly, "fringe", 0.90f);
                FieldFx.SetParam(poly, "fringe_width", 4.0f);

                shards.Add((poly, Vector2.Right.Rotated(ang) * speed,
                    Range(rng, -9f, 9f), size));
            }

            FieldFx.Animate(_burstRoot, life, t =>
            {
                float time = t * life;
                // 重力 + 空气阻力：起步快、越飞越慢、后半段开始下坠
                float drag = 1f - 0.55f * Mathf.Clamp(time / life, 0f, 1f);

                foreach ((Polygon2D poly, Vector2 vel, float spin, float size) in shards)
                {
                    if (!GodotObject.IsInstanceValid(poly))
                    {
                        continue;
                    }

                    poly.Position = vel * time * drag + new Vector2(0f, 620f * time * time);
                    poly.Rotation = spin * time;

                    // 前 60% 全实、后 40% 淡出（碎块不能突然消失）
                    float a = 1f - Mathf.Clamp((time - life * 0.60f) / (life * 0.40f), 0f, 1f);

                    // 越小的碎块越早看不见 —— 大块留在画面上，视觉上有"残留"
                    a *= Mathf.Lerp(0.45f, 1f, Mathf.Clamp(size / 30f, 0f, 1f));
                    poly.Color = new Color(0.13f, 0.09f, 0.08f, Mathf.Clamp(a, 0f, 1f));
                }
            });

            foreach ((Polygon2D poly, Vector2 _, float _, float _) in shards)
            {
                FieldFx.FreeAfter(poly, life + 0.1);
            }
        }

        /// <summary>
        /// ★★★ <b>击碎一小块黑暗</b>——爆开时，黑暗本身被砸掉几片，飞出去。
        ///
        /// <para><b>为什么是"拱片"不是"碎石"</b>：黑暗在画面上是一层<b>壳</b>，
        /// 所以剥落的应该是<b>弧形的壳片</b>（外弧 + 内弧围成的月牙），
        /// 而不是一堆小石子。每片的朝向沿圆周切向排开，读起来就是"一圈壳碎开了"。</para>
        ///
        /// <para><b>为什么必须有彩色边</b>：碎块本体是暗的，在全黑的画面上会隐形。
        /// 抗锯齿着色器的 <c>fringe</c> 给外缘镀一圈光谱 —— 于是它读作
        /// "被光从背后照透的黑暗碎片"，这正是用户要的那个意象。</para>
        ///
        /// <para>比 <c>SpawnDebris</c> 更大、更少、更慢、活得更久：
        /// 碎石是"碎屑"，这个是"整块黑暗被敲下来"，分量完全不同。</para>
        /// </summary>
        private void SpawnDarkShards()
        {
            var rng = new Random((int)(_enemyPos.X * 47f + _enemyPos.Y * 29f));
            const int count = 12;
            const float life = 1.70f;
            const int seg = 6;

            var shards = new List<(Polygon2D Poly, Vector2 BasePos, Vector2 Dir, float Speed, float Spin)>();

            for (int i = 0; i < count; i++)
            {
                float a0 = Mathf.Tau * i / count + Range(rng, -0.10f, 0.10f);
                float span = Mathf.Tau / count * Range(rng, 0.70f, 0.94f);   // 弧片占的角度
                float rOut = Range(rng, 300f, 470f);
                float thick = Range(rng, 28f, 58f);

                // 以角度 0 为中心建弧（顶点是局部的），再靠节点 Rotation 摆到应有的方位
                float half = span * 0.5f;
                var pts = new List<Vector2>();

                for (int k = 0; k <= seg; k++)
                {
                    float a = -half + span * k / seg;
                    pts.Add(new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rOut);
                }

                for (int k = seg; k >= 0; k--)
                {
                    float a = -half + span * k / seg;
                    // 内侧半径再抖一下，边缘就不是规整圆弧了
                    float ri = rOut - thick * Range(rng, 0.75f, 1.25f);
                    pts.Add(new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * ri);
                }

                float ac = a0 + span * 0.5f;
                Vector2 dir = new Vector2(Mathf.Cos(ac), Mathf.Sin(ac));

                // 暗暖灰（纯黑在黑暗上会隐形）
                Polygon2D poly = FieldFx.Poly(_burstRoot, pts.ToArray(),
                    new Color(0.11f, 0.075f, 0.07f, 0f), additive: false);
                FieldFx.SetParam(poly, "fringe", 1.0f);          // ★ 光谱边缘 = 被光从背后照透
                FieldFx.SetParam(poly, "fringe_width", 5.0f);

                poly.Position = dir * (rOut - thick * 0.5f);
                poly.Rotation = ac;

                shards.Add((poly, poly.Position, dir,
                    Range(rng, 110f, 290f), Range(rng, -2.6f, 2.6f)));
            }

            FieldFx.Animate(_burstRoot, life, t =>
            {
                float time = t * life;
                float drag = 1f - 0.50f * Mathf.Clamp(time / life, 0f, 1f);

                foreach ((Polygon2D poly, Vector2 basePos, Vector2 dir, float speed, float spin) in shards)
                {
                    if (!GodotObject.IsInstanceValid(poly))
                    {
                        continue;
                    }

                    poly.Position = basePos + dir * speed * time * drag
                                    + new Vector2(0f, 260f * time * time);
                    poly.Rotation += 0f;   // 旋转由 spin 控制（见下）


                    float a = 1f - Mathf.Clamp((time - life * 0.68f) / (life * 0.32f), 0f, 1f);
                    poly.Color = new Color(0.11f, 0.075f, 0.07f, Mathf.Clamp(a, 0f, 1f));
                }
            });

            foreach ((Polygon2D poly, Vector2 _, Vector2 _, float _, float _) in shards)
            {
                FieldFx.FreeAfter(poly, life + 0.15);
            }
        }

        /// <summary>
        /// ★★ <b>泛光</b>——三层递减的大软光叠在爆点上。
        ///
        /// <para><b>为什么不写真 bloom</b>：真正的 bloom 是"亮部提取 → 多次降采样模糊 → 加色叠回"，
        /// 要么开 SubViewport 要么读屏幕纹理做多档采样。我们今天的坑全在"读屏幕纹理"这条路上
        /// （折射/后处理/扭曲都出过问题），而爆点的亮部本来就集中在一点 ——
        /// <b>用几层大半径软光叠加，观感几乎一致，且完全不会出错</b>。</para>
        ///
        /// <para>三层半径 1500 / 2600 / 4000，alpha 递减；寿命 1.0 秒，比星爆（0.46s）长，
        /// 所以炸开之后空气里的光还在慢慢散 —— 这就是"泛"。</para>
        /// </summary>
        private void SpawnBloomHalo()
        {
            (float Size, float Alpha, float Life)[] layers =
            [
                (1500f, 0.30f, 0.85f),
                (2600f, 0.16f, 1.00f),
                (4000f, 0.08f, 1.15f),
            ];

            foreach ((float size, float alpha, float life) in layers)
            {
                ColorRect halo = FieldFx.ShaderQuad(_burstRoot, FieldFxShaders.Flash, size);
                FieldFx.SetParam(halo, "tint", CGold);
                FieldFx.SetParam(halo, "alpha", 0f);
                FieldFx.SetParam(halo, "falloff", 2.6f);
                halo.Scale = Vector2.One * 0.35f;

                FieldFx.Animate(halo, life, t =>
                {
                    FieldFx.SetParam(halo, "alpha", alpha * FieldFxEase.Envelope(t, 0.10f, 0.45f));
                    halo.Scale = Vector2.One * Mathf.Lerp(0.35f, 1.05f, FieldFxEase.OutExpo(t));
                });

                FieldFx.FreeAfter(halo, life + 0.1);
            }
        }

        /// <summary>按倍数设置三层光束的宽度。</summary>
        private void SetBeamWidth(float mul)
        {
            if (GodotObject.IsInstanceValid(_beamSoft)) { _beamSoft.Width = BeamSoftW * mul; }
            if (GodotObject.IsInstanceValid(_beamMid)) { _beamMid.Width = BeamMidW * mul; }
            if (GodotObject.IsInstanceValid(_beamCore)) { _beamCore.Width = BeamCoreW * mul; }
        }

        /// <summary>
        /// ★★★ <b>爆开瞬间的光束：极速膨胀再收细</b>（朋友的提议）。
        ///
        /// <para><b>为什么这一下很有力量</b>：光束一直是"细的一根"，
        /// 在命中那一瞬间突然撑到 10 倍粗 —— 观众的预期被打破，
        /// 而"能量一瞬间灌进去"的读法就成立了。之后收回细线，是"打完了"。</para>
        ///
        /// <para>时间：膨胀 0.14 秒（OutExpo 所以是"炸"出来的），
        /// 总长 0.42 秒全部收细并淡掉。</para>
        /// </summary>
        private void AnimateBeamBurst()
        {
            const float total = 0.42f;
            const float boomTime = 0.14f;

            FieldFx.Animate(this, total, t =>
            {
                float time = t * total;

                // 0 → boomTime 之间从 10 倍回落到 1 倍
                float k = Mathf.Clamp(1f - time / boomTime, 0f, 1f);
                float mul = 1f + 9f * k * k;

                SetBeamWidth(mul);

                float a = 1f - Mathf.Clamp((time - boomTime * 0.7f) / (total - boomTime * 0.7f), 0f, 1f);

                SetBeamAlpha(_beamSoft, 0.55f * a);
                SetBeamAlpha(_beamMid, 0.90f * a);
                SetBeamAlpha(_beamCore, 1.00f * a);

                if (time >= total - 0.02f)
                {
                    SetBeamAlpha(_beamSoft, 0f);
                    SetBeamAlpha(_beamMid, 0f);
                    SetBeamAlpha(_beamCore, 0f);
                }
            });
        }

                        /// <summary>
        /// ★★ <b>地面上被掀起来的尘土</b>（朋友的提议）。
        ///
        /// <para><b>和"从爆点升起的烟"完全不同——四个空间参数都要改</b>：</para>
        /// <list type="bullet">
        ///   <item><b>发射位置在脚底地面</b>，不是爆点中心。
        ///         所以粒子节点挂在 <c>(0, _headUp*1.05)</c>，那是敌人的脚下；</item>
        ///   <item><b>发射形状是扁平的横向带</b>（Box，外扩 150 × 高 8），
        ///         所以尘土沿着一条地线铺开，不是一个球；</item>
        ///   <item><b>方向主要朝外朝上</b>（Direction 朝上 + Spread 110°），
        ///         读作"被冲击波从地面掀起来"；</item>
        ///   <item><b>正重力</b>（+45）——尘土扬起来之后会<b>落回地面</b>。
        ///         之前做烟用的是负重力往上飘，那是烟，不是土。</item>
        /// </list>
        ///
        /// <para>混合方式仍是<b>普通混合</b>（加色只会发光，做不出遮挡感）。
        /// 颜色偏沙土（0.80, 0.66, 0.44），不用灰 —— 地面扬起来的是土。</para>
        /// </summary>
        private void SpawnSandDust()
        {
            FieldFx.After(_burstRoot, 0.16, "ground dust", () =>
            {
                // ★ 发射器放在"脚下"，不是爆点
                var ground = new Node2D
                {
                    Name = "GroundDust",
                    Position = new Vector2(0f, _headUp * 1.05f),
                };
                _burstRoot.AddChild(ground);

                GpuParticles2D dust = FieldFx.Sparks(
                    ground,
                    new Color(0.80f, 0.66f, 0.44f),        // 沙土色
                    44,
                    90f, 340f,                             // 沿地面往外甩
                    1.80f,
                    45f,                                   // ★ 正重力：扬起来再落回地面
                    7f, 18f,                               // 大而软的团
                    oneShot: true,
                    ramp:
                    [
                        new Color(0.84f, 0.72f, 0.52f, 0.30f),
                        new Color(0.68f, 0.56f, 0.38f, 0.20f),
                        new Color(0.52f, 0.44f, 0.32f, 0.00f),
                    ]);

                dust.Material = FieldFx.Normal;            // ★ 普通混合：遮挡感
                dust.Explosiveness = 0.80f;                // 掀起来是一下子，不是慢慢渗

                if (dust.ProcessMaterial is ParticleProcessMaterial pm)
                {
                    // 扁平横向带：沿地面铺开
                    pm.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box;
                    pm.EmissionBoxExtents = new Vector3(150f, 8f, 0f);

                    // 朝上 + 110° 张角 ⇒ 主要往上和往两侧，不是朝下
                    pm.Direction = new Vector3(0f, -1f, 0f);
                    pm.Spread = 110f;

                    pm.DampingMin = 6f;                    // 很快减速，土不带惯性
                    pm.DampingMax = 12f;
                }

                FieldFx.Ignite(dust);
                FieldFx.FreeAfter(ground, 2.4);
            });
        }

        /// <summary>
        /// ★★★ <b>狙击镜</b>——被打中的目标头顶冒出的瞄准环。
        ///
        /// <para><b>配方来自 P3Makoto 的 <c>P3Fx.Reticle</c></b>（参考 mod，纯视觉、无玩法改动），
        /// 四层结构是我看了它的实现之后照着原理做的：</para>
        /// <list type="number">
        ///   <item><b>3 段外弧</b>（各 1.4 rad，120° 分布，粗 3）—— 3.2 秒慢转一圈；</item>
        ///   <item><b>4 段内弧</b>（0.68 倍半径，0.9 rad，细 1.6）—— 2.2 秒<b>反向</b>转；
        ///         正反两圈反向转，静止的环才不会像贴纸；</item>
        ///   <item><b>3 个小三角</b>（1.18 倍半径，朝外）—— 加色白，整体 0.92↔1.0 呼吸；</item>
        ///   <item><b>4 个角括号</b>（L 形折线，1.4 倍半径）—— 从 1.4 收到 1.0，像"镜头合焦"。</item>
        /// </list>
        ///
        /// <para>入场 0.3 秒：整体缩放 1.7→1.0、旋转 −0.5→0、`min(1, t*3)` 淡入。
        /// <b>关键全在"断开 + 反向转 + 括号收拢"</b>，完整圆和单个环都没有这个味道。</para>
        ///
        /// <para>⚠️ 它是<b>纯装饰</b>：不挡输入、不加色到底层，0.9 秒后自己消失。</para>
        /// </summary>
        /// <param name="at">世界坐标（目标"头顶"）—— 调用方自己加上头顶高度。</param>
        /// <param name="radius">外弧半径（世界单位）。小一点更像"小狙击镜"。</param>
        internal static void SpawnSniperScope(Node parent, Vector2 at, float radius = 64f,
            Color? tint = null)
        {
            try
            {
                Node2D root = FieldFx.Root(parent as Control, at, "SniperScope");

                // 母节点可能不是 Control（我们自己的 Node2D 就绪树），退回直接挂
                if (root.GetParent() == null)
                {
                    parent.AddChild(root);
                    root.GlobalPosition = at;
                }

                Color c = tint ?? new Color(1.0f, 0.90f, 0.70f);

                // ── ① 外弧 ×3（慢转）──
                var outer = new Node2D { Name = "Outer" };
                root.AddChild(outer);

                for (int i = 0; i < 3; i++)
                {
                    float a = Mathf.Tau * i / 3f;
                    FieldFx.Line(outer, FieldFx.Arc(radius, a, a + 1.4f), new Color(c, 0.90f), 3f);
                }

                // ── ② 内弧 ×4（反向转）──
                var inner = new Node2D { Name = "Inner" };
                root.AddChild(inner);

                for (int i = 0; i < 4; i++)
                {
                    float a = Mathf.Tau * i / 4f + 0.3f;
                    FieldFx.Line(inner, FieldFx.Arc(radius * 0.68f, a, a + 0.9f, 10),
                        new Color(1f, 0.97f, 0.88f, 0.80f), 1.6f);
                }

                // ── ③ 外指小三角 ×3（呼吸）──
                var spikes = new Node2D { Name = "Spikes" };
                root.AddChild(spikes);

                for (int i = 0; i < 3; i++)
                {
                    var holder = new Node2D
                    {
                        Rotation = Mathf.Tau * i / 3f - Mathf.Pi / 2f,
                    };
                    spikes.AddChild(holder);

                    float r = radius * 1.18f;
                    Vector2[] tri =
                    [
                        new Vector2(r, 0f),
                        new Vector2(r + radius * 0.16f, -radius * 0.08f),
                        new Vector2(r + radius * 0.16f, radius * 0.08f),
                    ];

                    FieldFx.Poly(holder, tri, new Color(1f, 1f, 1f, 0.95f), additive: true);
                }

                // ── ④ 角括号 ×4（合焦感）──
                var brackets = new Node2D { Name = "Brackets" };
                root.AddChild(brackets);

                float br = radius * 1.4f;
                float bl = radius * 0.32f;

                for (int i = 0; i < 4; i++)
                {
                    var holder = new Node2D { Rotation = Mathf.Pi / 2f * i };
                    brackets.AddChild(holder);

                    Vector2[] corner =
                    [
                        new Vector2(br - bl, -br),
                        new Vector2(br, -br),
                        new Vector2(br, -br + bl),
                    ];

                    FieldFx.Line(holder, corner, new Color(c, 0.85f), 2.5f);
                }

                // ── 入场：缩放 1.7→1.0、旋转 −0.5→0、淡入 ──
                root.Modulate = new Color(1f, 1f, 1f, 0f);

                FieldFx.Animate(root, 0.30, t =>
                {
                    float e = FieldFxEase.OutExpo(t);
                    root.Scale = Vector2.One * Mathf.Lerp(1.7f, 1f, e);
                    root.Rotation = Mathf.Lerp(-0.5f, 0f, e);
                    root.Modulate = new Color(1f, 1f, 1f, Mathf.Min(1f, t * 3f));
                    brackets.Scale = Vector2.One * Mathf.Lerp(1.4f, 1f,
                        FieldFxEase.OutExpo(FieldFxEase.Segment(t, 0.15f, 0.85f)));
                });

                // ── 正反两圈持续反向转 ──
                FieldFx.Animate(root, 3.20, t => { outer.Rotation = t * Mathf.Tau; });
                FieldFx.Animate(root, 2.20, t => { inner.Rotation = -t * Mathf.Tau; });

                // ── 三角呼吸 ──
                FieldFx.Animate(spikes, 0.90, t =>
                {
                    spikes.Scale = Vector2.One * Mathf.Lerp(0.92f, 1f, FieldFxEase.InOutSine(t * 2f % 1f));
                });

                FieldFx.FreeAfter(root, 0.95);
            }
            catch (Exception e)
            {
                LogAlways($"[狙击镜] 生成失败：{e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// ★★ <b>屏幕折射</b>——能量波扫过时背景被真实地推歪。
        /// <b>这是整套里最"贵"的一下</b>，也是纯贴图方案永远做不到的。
        /// </summary>
        private void SpawnRefract()
        {
            ColorRect rf = FieldFx.ShaderQuad(_burstRoot, FieldFxShaders.Refract, 2100f);
            FieldFx.SetParam(rf, "tint", CGold);
            FieldFx.SetParam(rf, "strength", 0.030f);
            FieldFx.SetParam(rf, "alpha", 0f);
            // ⚠️⚠️ 这里**不要**写 Scale = 0.001f。
            //    那是"从 0 长出来"的写法，但这个环的尺寸由着色器的 radius 参数控制，
            //    quad 本身必须保持满尺寸 —— 曾把光谱环/冲击波/折射整段缩到千分之一，
            //    结果它们一次都没显示过（2026-10-18）。

            // 折射必须画在所有东西上面：抬到本节点的最后一个子节点
            MoveChild(rf.GetParent(), GetChildCount() - 1);

            FieldFx.Animate(rf, 0.52, t =>
            {
                float e = FieldFxEase.OutExpo(t);
                FieldFx.SetParam(rf, "radius", Mathf.Lerp(0.05f, 0.88f, e));
                FieldFx.SetParam(rf, "width", Mathf.Lerp(0.20f, 0.05f, e));
                FieldFx.SetParam(rf, "alpha", 1f - FieldFxEase.InCubic(t));
            });

            FieldFx.FreeAfter(rf, 0.62);
        }

                        /// <summary>
        /// ★★★ <b>星芒炸开</b>——回到 2026-10-18 用户认可的那一版形态：<b>三层星形多边形</b>。
        ///
        /// <para><b>为什么不用针芒了</b>：我后来为了"细针"把星形换成了 24 根锥形带，
        /// 结果（a）星形那版的形状层次更有整体感，（b）针芒还因为 alpha 写错一度全隐。
        /// 用户明确说"第一次加光谱那版效果好"，那一版的星爆就是下面这三层。</para>
        ///
        /// <para>三层各用不同的缓动 ⇒ 由内向外逐层展开：长射线最慢（OutQuint）、
        /// 中射线中速、密芯最快。顶点色让射线沿长度从白衰减到透明。</para>
        /// </summary>
        private void SpawnStarBurst()
        {
            var rng = new Random((int)(_enemyPos.X * 13f + _enemyPos.Y * 7f));

            // ① 底光
            ColorRect glow = FieldFx.ShaderQuad(_burstRoot, FieldFxShaders.Flash, 3200f);
            FieldFx.SetParam(glow, "tint", COrange);
            FieldFx.SetParam(glow, "alpha", 0f);
            glow.Scale = Vector2.One * 0.001f;

            FieldFx.Animate(glow, 0.50, t =>
            {
                FieldFx.SetParam(glow, "alpha", 0.52f * (1f - FieldFxEase.InCubic(t)));
                glow.Scale = Vector2.One * Mathf.Lerp(0.20f, 1f, FieldFxEase.OutExpo(t));
            });

            FieldFx.FreeAfter(glow, 0.6);

            // ★★★ 泛光（bloom）：三层递减的大软光叠在爆点上，寿命比星爆长。
            //    真 bloom 要"亮部提取 + 模糊"，代价大、又要读屏幕纹理（今天已经在这上面踩过坑）。
            //    我们的亮部本来就在中心一点，所以用三层大半径软光近似，观感几乎一样、风险为零。
            SpawnBloomHalo();

            // ②③④ 三层星形（长 / 中 / 密）
            Polygon2D longRays = FieldFx.Poly(_burstRoot,
                FieldFx.StarVaried(1900f, 8, 0.010f, rng, 0.45f, 3.0f), Colors.White, true);
            Polygon2D midRays = FieldFx.Poly(_burstRoot,
                FieldFx.StarVaried(1150f, 8, 0.040f, rng, 0.32f, 22.5f), Colors.White, true);
            Polygon2D denseRays = FieldFx.Poly(_burstRoot,
                FieldFx.StarVaried(560f, 16, 0.050f, rng, 0.28f, 8.0f), Colors.White, true);

            FieldFx.SetStarGradient(longRays, new Color(CCream, 0.55f), new Color(CRed, 0.00f));
            FieldFx.SetStarGradient(midRays, new Color(CCream, 0.90f), new Color(COrange, 0.00f));
            FieldFx.SetStarGradient(denseRays, new Color(CWhite, 1.00f), new Color(CWhite, 0.00f));

            // ★ 边缘色散：彩色只出现在射线的外缘，本体仍是白的
            FieldFx.SetParam(longRays, "fringe", 0.70f);
            FieldFx.SetParam(longRays, "fringe_width", 9.0f);
            FieldFx.SetParam(midRays, "fringe", 0.55f);
            FieldFx.SetParam(midRays, "fringe_width", 7.0f);
            FieldFx.SetParam(denseRays, "fringe", 0.40f);
            FieldFx.SetParam(denseRays, "fringe_width", 5.0f);

            FieldFx.Animate(_burstRoot, 0.46, t =>
            {
                float fade = 1f - FieldFxEase.InCubic(t);

                longRays.Scale = Vector2.One * Mathf.Max(0.001f, Mathf.Lerp(0.05f, 1f, FieldFxEase.OutQuint(t)));
                midRays.Scale = Vector2.One * Mathf.Max(0.001f, Mathf.Lerp(0.10f, 1f, FieldFxEase.OutExpo(t)));
                denseRays.Scale = Vector2.One * Mathf.Max(0.001f, Mathf.Lerp(0.18f, 1f, FieldFxEase.OutExpo(t)));

                longRays.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(0.80f * fade * fade, 0f, 1f));
                midRays.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(0.95f * fade, 0f, 1f));
                denseRays.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(1.00f * fade, 0f, 1f));
            });

            FieldFx.FreeAfter(longRays, 0.56);
            FieldFx.FreeAfter(midRays, 0.56);
            FieldFx.FreeAfter(denseRays, 0.56);

            // ⑤ 炽核
            Sprite2D core = MakeSoftDot(_burstRoot, 150f, new Color(CWhite, 0f));
            core.Position = Vector2.Zero;

            FieldFx.Animate(core, 0.30, t =>
            {
                core.Scale = Vector2.One * (150f * Mathf.Lerp(0.35f, 1.5f, FieldFxEase.OutExpo(t)) / 16f);
                core.Modulate = new Color(CWhite, 1f - FieldFxEase.InCubic(t));
            });

            FieldFx.FreeAfter(core, 0.42);
        }

        /// <summary>
        /// <b>一根针芒</b>：从中心沿 <paramref name="ang"/> 方向出发、
        /// 宽度从 <paramref name="width"/> 收到 0 的锥形带。
        ///
        /// <para>⚠️ 这必须用 <see cref="FieldFx.StrokePts"/>（加粗折线）而不是星形多边形 ——
        /// 星形在远处会宽成一条带子，见 <c>SpawnStarBurst</c> 的说明。
        /// 顶点色再做"根部亮、尖端透明"，针才有"刺出去就消失"的感觉。</para>
        /// </summary>
        private Polygon2D MakeNeedle(float ang, float length, float width, Color tint, string label)
        {
            Vector2 dir = Vector2.Right.Rotated(ang);

            // 三段：根部粗、中段收、尖端 0（配合 taper 就成了针）
            Vector2[] spine =
            [
                dir * (length * 0.06f),
                dir * (length * 0.45f),
                dir * length,
            ];

            // ⚠️⚠️ 这里**必须是 alpha = 1 的底色**。
            //   最终 alpha = Color.a × Modulate.a，底子写 0 的话，
            //   动画里怎么调 Modulate 都还是 0 —— 24 根针芒曾经整段完全不可见（2026-10-18）。
            //    亮度由调用方用 Modulate 控制。
            Polygon2D poly = FieldFx.Poly(_burstRoot,
                FieldFx.StrokePts(spine, width, taper: true), new Color(tint, 1f), additive: true);
            // ★★ 开边缘色散：针的外缘那一圈会出现彩虹，本体仍是白的 ——
            //    这就是用户要的"彩色只出现在光边缘"。
            FieldFx.SetParam(poly, "fringe", 0.0f);
            FieldFx.SetParam(poly, "fringe_width", 6.0f);

            poly.Name = label;
            return poly;
        }

        /// <summary>
        /// ★ <b>放射速度线</b>——14 条锥形线从中向外抽出去。
        /// 贴图上做这个要么是画好的（方向固定、不能跟随机旋转），要么就做不了；
        /// 代码算顶点的话方向随便改、粗细渐变也是真的。
        /// </summary>
        private void SpawnSpeedLines()
        {
            var lines = new List<(Polygon2D Poly, float Ang, float Delay)>();
            var rng = new Random((int)(_enemyPos.X * 31f + _enemyPos.Y * 17f));

            for (int i = 0; i < 14; i++)
            {
                float ang = Mathf.Tau * i / 14f + Range(rng, -0.12f, 0.12f);
                Polygon2D poly = FieldFx.Poly(_burstRoot, FieldFx.Circle(1f, 6), new Color(CGold, 0f), true);
                lines.Add((poly, ang, Range(rng, 0f, 0.06f)));
            }

            FieldFx.Animate(_burstRoot, 0.40, t =>
            {
                foreach ((Polygon2D poly, float ang, float delay) in lines)
                {
                    if (!GodotObject.IsInstanceValid(poly))
                    {
                        continue;
                    }

                    float u = FieldFxEase.Segment(t, delay, 0.30f);

                    if (u <= 0f)
                    {
                        poly.Color = new Color(CGold, 0f);
                        continue;
                    }

                    float e = FieldFxEase.OutExpo(u);
                    float inner = Mathf.Lerp(70f, 260f, e);
                    float outer = Mathf.Lerp(180f, 980f, e);
                    Vector2 dir = Vector2.Right.Rotated(ang);

                    Vector2[] pts =
                    [
                        dir * inner,
                        dir * (inner + (outer - inner) * 0.55f),
                        dir * outer,
                    ];

                    FieldFx.Reshape(poly, FieldFx.StrokePts(pts, Mathf.Lerp(28f, 6f, e), taper: true));
                    poly.Color = new Color(CGold, 0.85f * (1f - FieldFxEase.InCubic(u)));
                }
            });
        }

        /// <summary>余烬 + 光尘（真 GPU 粒子，带颜色渐变行）。</summary>
        private void SpawnEmbers()
        {
            GpuParticles2D embers = FieldFx.Sparks(
                _burstRoot, CGold, 46, 260f, 900f, 0.95f, 240f, 0.22f, 0.55f, oneShot: true,
                ramp: [CWhite, CGold, COrange, CRed]);
            FieldFx.Ignite(embers);

            GpuParticles2D dust = FieldFx.Sparks(
                _burstRoot, CCream, 26, 90f, 380f, 1.30f, -60f, 0.10f, 0.26f, oneShot: true,
                ramp: [CWhite, CCream, CGold]);
            FieldFx.Ignite(dust);

            FieldFx.FreeAfter(embers, 1.6);
            FieldFx.FreeAfter(dust, 2.0);
        }

        /// <summary>敌人脚下的两道冲击波（正圆、压扁一点点做透视 —— 但用户要求过"要正圆"）。</summary>
        private void SpawnShockwaves()
        {
            var holder = new Node2D
            {
                Name = "Shock",
                Position = new Vector2(0f, _headUp * 1.05f),   // 脚底
            };
            _burstRoot.AddChild(holder);

            SpawnRingAt(holder, 1250f, 0.52f, 26f, CGold, 0.80f, 0.00f);
            SpawnRingAt(holder, 780f, 0.38f, 13f, CWhite, 0.55f, 0.07f);

            FieldFx.FreeAfter(holder, 0.85);
        }

        private void SpawnRingAt(Node parent, float worldRadius, float duration, float width,
            Color tint, float peakAlpha, float delay)
        {
            ColorRect ring = FieldFx.ShaderQuad(parent, FieldFxShaders.Ring, worldRadius * 2f);
            FieldFx.SetParam(ring, "tint", tint);
            FieldFx.SetParam(ring, "alpha", 0f);

            float total = duration + delay;

            FieldFx.Animate(ring, total, t =>
            {
                float u = FieldFxEase.Segment(t, delay / total, duration / total);

                if (u <= 0f)
                {
                    FieldFx.SetParam(ring, "alpha", 0f);
                    return;
                }

                float e = FieldFxEase.OutExpo(u);
                FieldFx.SetParam(ring, "radius", Mathf.Lerp(0.05f, 0.92f, e));
                FieldFx.SetParam(ring, "width", Mathf.Lerp(width * 0.0018f, 0.005f, e));
                FieldFx.SetParam(ring, "alpha", peakAlpha * (1f - FieldFxEase.InCubic(u)));
            });
        }

        /// <summary>余韵：0.35 秒后在敌人身上再闪一小下 + 一圈细环，避免"啪一下就没了"。</summary>
        private void SpawnAfterglow()
        {
            FieldFx.After(_burstRoot, 0.34, "afterglow", () =>
            {
                ColorRect glow = FieldFx.ShaderQuad(_burstRoot, FieldFxShaders.Flash, 620f);
                FieldFx.SetParam(glow, "tint", COrange);
                FieldFx.SetParam(glow, "alpha", 0f);

                FieldFx.Animate(glow, 0.40, t =>
                {
                    FieldFx.SetParam(glow, "alpha", 0.45f * FieldFxEase.Envelope(t, 0.10f, 0.35f));
                });

                FieldFx.FreeAfter(glow, 0.5);

                ColorRect ring = FieldFx.ShaderQuad(_burstRoot, FieldFxShaders.Ring, 1400f);
                FieldFx.SetParam(ring, "tint", CGold);
                FieldFx.SetParam(ring, "alpha", 0f);

                FieldFx.Animate(ring, 0.55, t =>
                {
                    float e = FieldFxEase.OutExpo(t);
                    FieldFx.SetParam(ring, "alpha", 0.35f * (1f - FieldFxEase.InCubic(t)));
                    FieldFx.SetParam(ring, "radius", Mathf.Lerp(0.30f, 0.80f, e));
                    FieldFx.SetParam(ring, "width", 0.008f);
                });

                FieldFx.FreeAfter(ring, 0.65);
            });
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  共用小工具（BulletMode / Feranmuts 等也在用，不能删）
    // ══════════════════════════════════════════════════════════════════════════

    private static float Range(Random rng, float a, float b) => a + (float)rng.NextDouble() * (b - a);

    private static Color Tint(float gain, float alpha)
    {
        Color c = SentinelMode ? SentinelColor : Colors.White;

        // ★★ 2026-10-12 **真 bug（用户报"光点暗、炸开的粒子不对"的根因之一）**：
        //   这里原来把每个通道 Min(1f, …) **夹在 1.0** —— 而基色是**纯白**，
        //   于是**所有 gain ≥ 1 的调用全部等于白点、一个都没生效**：
        //   预览里 gain=1.45 的余烬、1.85 的中心光粒、1.80 的硬核闪光，
        //   在游戏里统统是 1.0 ⇒ 整段特效比设计稿暗 30%~45%（加色混合下就是"发灰、看不见"）。
        //   现在**不夹**（Color 允许 >1）：着色器里乘完再乘贴图，写进 LDR 帧缓冲时才被截断，
        //   这正是"加色发光"该有的行为。
        return new Color(c.R * gain, c.G * gain, c.B * gain, Mathf.Clamp(alpha, 0f, 1f));
    }

    private static CanvasItemMaterial AddMaterial() => new()
    {
        BlendMode = SentinelMode ? CanvasItemMaterial.BlendModeEnum.Mix : CanvasItemMaterial.BlendModeEnum.Add,
    };

    private static Sprite2D MakeSprite(Texture2D? tex, Node parent, bool hidden = false)
    {
        var sp = new Sprite2D
        {
            Texture = tex,
            Visible = !hidden,
            Modulate = Colors.Transparent,
            Material = AddMaterial(),
        };
        parent.AddChild(sp);
        return sp;
    }
}
