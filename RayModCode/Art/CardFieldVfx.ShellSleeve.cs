using System;
using System.Collections.Generic;
using Godot;

namespace RayMod.Art;

/// <summary>
/// ★★ 2026-10-20 <b>「填装形态」卡牌本体特效 —— 弹壳壳层（Shell Sleeve）</b>
///
/// <para>用户原话（m01818）：<i>"先修一下填装形态。卡牌特效我感觉还是不够足"</i>。</para>
///
/// <para><b>为什么"不够足" —— 查到了根因，不是参数太小，是<b>整条路都绕开了卡牌本体</b></b>：
/// 到这一版为止，填装形态对卡牌做的全部事情是
/// ① 换掉 <c>NCardFlyVfx</c> 的终点/拱高/时长（<b>只改轨迹</b>）、
/// ② 把 holder 摆到屏幕下方再让 <c>AnimPosition</c> 拉回（<b>只改起点</b>）、
/// ③ 在卡的<b>外面</b>挂光带 / 残影 / 火星（<see cref="CardFlyTrail"/>）。
/// ⇒ <b>卡牌自己的样子从头到尾一个像素都没变过</b>。位移和身外之物都加了，
/// 但玩家眼睛盯的是卡——卡没反应，"就不够足"。</para>
///
/// <para><b>★ 第二条根因（这条更要命，也是以前"加了看不见"的可能解释之一）</b>：
/// <c>NCardFlyVfx.PlayAnim()</c> 的循环里每帧写
/// <code>_card.Body.Modulate = White.Lerp(Black, clamp(time*3/_duration));
/// _card.Body.Scale    = One * Lerp(1f, 0.1f, clamp(time*3/_duration));</code>
/// 而 <c>time</c> 从 0 开始按 <c>_speed(≈1.02)</c> 累加 ⇒ <b>飞了 1/3 路程
/// （<c>_duration/3 ≈ 0.38s</c>）卡面就已经全黑、缩到 10%</b>。
/// 后面 2/3 的路程里，卡片本体等于一个黑点。
/// ⇒ <b>任何画在卡身上的东西，只要挂在 <c>Body</c> 里面，都会跟着一起变黑消失。</b></para>
///
/// <para><b>所以本类的挂载位置是卡牌节点本身（<c>Body</c> 的<b>兄弟</b>），不是 <c>Body</c> 里面</b>：
/// <list type="bullet">
///   <item>继承 <c>NCard</c> 的位置与旋转 ⇒ 飞行/旋转完全跟随，不用自己算轨迹；</item>
///   <item><b>不</b>受 <c>Body.Modulate</c> 影响 ⇒ 卡面黑掉之后壳层依然发亮，
///         读起来就是"卡片褪成暗影、被一枚金色弹壳框着飞走"——这正是"退壳"该有的样子；</item>
///   <item><c>Body.Scale</c> 用一个<b>带下限的镜像</b>（见 <see cref="MinMirror"/>）跟一下，
///         免得卡片缩到 0.1 时壳层还傻大。</item>
/// </list></para>
///
/// <para><b>纯表现，不碰数值；不碰 <c>PlayAnim</c> 的任何契约</b>
/// （<c>SwooshAwayCompletion</c> / <c>InvokeCardAddFinished</c> / <c>QueueFreeSafely</c> 全由原版照旧执行）。</para>
///
/// <para>⚠️ <b>联机</b>：两个入口都只从已经门控过的调用点进来
/// （<c>EjectVisuals</c> 由「填装形态」门控的 <c>_Ready</c> Postfix 调，
/// <c>StartChamberKick</c> 由本地手牌 <c>NPlayerHand.Add</c> 的 Postfix 调）⇒ 天然只覆盖本地玩家（铁律 81/84）。</para>
/// </summary>
internal partial class CardFieldVfx
{
    // ═══════════════════════════════════════════════════════════════════════
    //  调色板 —— 直接取自她自己的卡图（image_palette 实测量到的四个主色）
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>金黄 <c>#F4CC2B</c> —— 卡图弹壳堆的主色。</summary>
    private static readonly Color SleeveGold = new(0.957f, 0.800f, 0.169f);

    /// <summary>奶金 <c>#F9DA97</c> —— 卡图那条缠体螺旋光带。</summary>
    private static readonly Color SleeveMilk = new(0.976f, 0.855f, 0.592f);

    /// <summary>暖白 <c>#FFF6DB</c> —— 高光与"底火"。</summary>
    private static readonly Color SleeveHot = new(1.000f, 0.965f, 0.859f);

    /// <summary>琥珀 <c>#E3AD46</c> —— 黄铜（底火座 / 壳底）。</summary>
    private static readonly Color SleeveBrass = new(0.890f, 0.678f, 0.275f);

    /// <summary>
    /// 暗褐 <c>#52350F</c> —— 壳底凹槽 / 帽内刻痕。
    /// <para>「看得出来」不只靠加亮，也靠<b>明暗对比</b>：一整片均质金色读起来只是一块色斑，
    /// 切两道暗槽才会被眼睛解析成"有结构的金属件"。</para>
    /// </summary>
    private static readonly Color SleeveInk = new(0.322f, 0.208f, 0.059f);

    // ═══════════════════════════════════════════════════════════════════════
    //  入口
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>退壳（弃牌）</b>：壳层一上来就是满强度，跟着卡片飞完全程，末段收拢淡出。
    /// <para>由 <see cref="EjectVisuals"/> 调 —— 那个位置已经在 <c>NCardFlyVfx._Ready</c> 的
    /// Postfix 里，卡片已经挂进飞行节点、在树里了，所以能取到 <c>%CardContainer</c>。</para>
    /// </summary>
    internal static void SleeveOnEject(Control card)
    {
        try
        {
            CardShellSleeve? sleeve = Attach(card, CardShellSleeve.Mode.Eject);

            if (sleeve != null)
            {
                sleeve.Life = EjectDuration;
                sleeve.Glow = 0.95f;      // 满强度起手（退壳要"崩"出来，不能淡入）
                sleeve.FadeAt = 0.72f;    // 后 28% 才开始淡
                sleeve.CollapseFrom = 0.62f;
                sleeve.CollapseTo = 0.55f;
            }
        }
        catch (Exception e)
        {
            ReloadLog.Warn($"[填装形态·壳层] 退壳挂载失败（只影响这一张的外观）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// <b>上膛（抽牌）</b>：壳层从 0 淡入 —— 卡还在往弹仓里顶的时候壳先亮起来，
    /// 落位「咔」那一下由 <see cref="SleeveSnap"/> 把它<b>收拢爆掉</b>（= 弹壳被抛掉，子弹坐进膛）。
    /// </summary>
    internal static void SleeveOnChamber(Control? card)
    {
        try
        {
            if (card == null || !GodotObject.IsInstanceValid(card))
            {
                return;
            }

            CardShellSleeve? sleeve = Attach(card, CardShellSleeve.Mode.Chamber);

            if (sleeve != null)
            {
                // ★★ 2026-10-20：Life 3.0 → 1.05。
                //   3 秒的"兜底寿命"在实际画面里就是 bug：只要「咔」那一下没触发
                //   （ChamberKick 判到位失败、或者 holder 被别的东西卡住），
                //   一张已经坐进手牌的牌会**顶着发光壳带呼吸 3 秒**，看起来就是一排卡
                //   底部挂着白光条（用户截图里 1/2/5 号卡就是这个）。
                //   现在改成：淡入 0.3s → 最多亮 0.55s → **到 ChamberAutoSnap 自己收掉**，
                //   收不掉再靠 Life 兜底。总之它绝不可能长时间挂在手牌上。
                sleeve.Life = ChamberLife;
                sleeve.Glow = 0f;
                sleeve.FadeInTime = 0.30f;
                sleeve.TargetGlow = 0.92f;
            }
        }
        catch (Exception e)
        {
            ReloadLog.Warn($"[填装形态·壳层] 上膛挂载失败（只影响这一张的外观）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>上膛壳层的兜底寿命（秒）。正常路径由 <see cref="SleeveSnap"/> 在落位那一帧收掉。</summary>
    private const float ChamberLife = 1.05f;

    /// <summary>
    /// 上膛壳层<b>自己收自己</b>的时间点（秒）。
    /// <para>为什么需要它：「咔」是靠 <c>ChamberKick</c> 观察到达触发的，属于<b>第二只手</b>；
    /// 只要那只手失灵（holder 被卡住 / 掉落 / 换场景），壳层就会挂着一张亮带留在手牌上。
    /// 所以壳层必须能<b>自愈</b>：到这里还没收到 <see cref="SleeveSnap"/>，就自己走收拢。</para>
    /// </summary>
    private const float ChamberAutoSnap = 0.62f;

    /// <summary>
    /// 落位「咔」：把这张卡上的上膛壳层<b>收拢 + 爆白 + 消失</b>。
    /// <para>由 <c>ChamberKick.Fire()</c> 在缩放三段式和金环之后调 —— 三件事同一帧起手，
    /// 才是"咔"的那一拍。</para>
    /// </summary>
    internal static void SleeveSnap(Control? card)
    {
        try
        {
            if (card == null || _sleeves.Count == 0)
            {
                return;
            }

            for (int i = _sleeves.Count - 1; i >= 0; i--)
            {
                CardShellSleeve s = _sleeves[i];

                if (s == null || !GodotObject.IsInstanceValid(s))
                {
                    _sleeves.RemoveAt(i);
                    continue;
                }

                if (!ReferenceEquals(s.Card, card))
                {
                    continue;
                }

                s.Snap();
                return;
            }
        }
        catch
        {
            // 壳层爆掉失败绝不影响落位动作本体
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  挂载
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>所有还活着的壳层（用于 <see cref="SleeveSnap"/> 反查 + 上一张没死完时的去重）。</summary>
    private static readonly List<CardShellSleeve> _sleeves = new();

    private static CardShellSleeve? Attach(Control card, CardShellSleeve.Mode mode)
    {
        if (card == null || !GodotObject.IsInstanceValid(card) || !card.IsInsideTree())
        {
            return null;
        }

        // 同一张卡上已经有壳层（比如 PollNewHolders 和补丁两条路都摸到同一个 holder）
        // ⇒ 直接复用，别叠成两层（叠加会把 alpha 翻倍，又会过曝）。
        for (int i = _sleeves.Count - 1; i >= 0; i--)
        {
            CardShellSleeve old = _sleeves[i];

            if (old == null || !GodotObject.IsInstanceValid(old))
            {
                _sleeves.RemoveAt(i);
                continue;
            }

            if (ReferenceEquals(old.Card, card))
            {
                return old;
            }
        }

        // ★ 卡面的宿主：`%CardContainer`（= 反编译侧的 `NCard.Body`）。
        //   走具名查找，和 `NCard._Ready` 里 `GetNode<Control>("%CardContainer")` 是同一条路；
        //   取不到就退回相对路径 "CardContainer"（`card.tscn` 里它就是第一个子节点）。
        Control? body = card.GetNodeOrNull("%CardContainer") as Control
                        ?? card.GetNodeOrNull("CardContainer") as Control;

        if (body == null)
        {
            // 拿不到 %CardContainer 也**不放弃** —— 几何改由 %Frame / 默认值决定，
            // 只是失去"镜像卡面缩放"这一层（MirrorBodyScale 会空过）。
            Diag("sleeve-nobody", "壳层：找不到 %CardContainer（退回 %Frame 几何，仍会绘制）");
        }

        var sleeve = new CardShellSleeve
        {
            Name = "RayModShellSleeve",
            Card = card,
            Body = body,
            Mode_ = mode,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        // ★★★ 坐标系的坑（**必须**照这一段做，别再想当然按"左上角原点"算）：
        //   `NCard.Size` 实测是 (0,0)，子节点全部用**负偏移**排布 ⇒
        //   **卡片局部坐标的原点在卡片【中心】**，卡面范围 = x[-150,150] × y[-211,211]
        //   （`%Frame: pos=(-150,-211) size=(300,422) pivot=(150,211)`，见
        //     `Patches\CardTagIconOverlay.cs:168-171` 的实测日志）。
        //   ⇒ 壳层如果按 (0,0)+Size 摆，会整体往右下偏出**半张卡**。
        //   正确做法：直接量 `%Frame`（它就是卡片轮廓本身）相对卡片的原点，
        //   量不到再退回"以卡片中心对称"的默认几何。
        Vector2 size = new(300f, 422f);
        Vector2 origin = new(-150f, -211f);

        try
        {
            if (card.GetNodeOrNull("%Frame") is Control frame
                && GodotObject.IsInstanceValid(frame)
                && frame.Size.X > 8f && frame.Size.Y > 8f)
            {
                size = frame.Size;
                // 把 %Frame 的全局左上角换算回卡片局部空间（用 AffineInverse 才不怕卡片当时有旋转）
                origin = card.GetGlobalTransform().AffineInverse() * frame.GlobalPosition;
            }
        }
        catch
        {
            // 量不到就用默认几何（300×422、以卡片中心对称）
        }

        sleeve.Position = origin;
        sleeve.Size = size;
        sleeve.PivotOffset = size * 0.5f;   // 缩放中心 = 卡面中心（不然镜像缩放会往左上角塌）

        card.AddChild(sleeve);
        sleeve.SetProcess(true);
        _sleeves.Add(sleeve);

        Diag("sleeve-on", $"壳层挂上：{mode} 卡面 {size.X:F0}×{size.Y:F0}");
        return sleeve;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  壳层本体
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 一枚画在卡面上的「弹壳框」：金边 + 外发光 + 壳底亮带 + 底火 + 弹头光弧 + 流动高光。
    /// <para>纯 <c>_Draw()</c> 矢量绘制，<b>零贴图依赖</b> —— 和本 mod 其它特效一个路数，
    /// 不新增任何 pck 资产，也就不会出现 "Asset not cached" 那种教训（见
    /// <see cref="RayCardTrailPath"/> 的注释）。</para>
    /// </summary>
    private sealed partial class CardShellSleeve : Control
    {
        internal enum Mode
        {
            /// <summary>弃牌：跟着卡飞完全程，末段收拢淡出。</summary>
            Eject,

            /// <summary>抽牌：淡入 → 停在卡上 → 落位「咔」时被 <see cref="Snap"/> 收掉。</summary>
            Chamber,
        }

        // ── 几何常量 ──

        /// <summary>金边向内缩多少（像素）。留一点边，读起来才像"套在卡外面"。</summary>
        private const float Inset = 7f;

        /// <summary>
        /// 圆角半径（px）。卡面本身是圆角的，<b>硬边矩形会戳出四个角</b> ——
        /// 这是上一版被判「廉价」的来源之一。所有轮廓都沿这个半径走。
        /// </summary>
        private const float CornerRadius = 13f;

        /// <summary>金边粗细。</summary>
        private const float BorderWidth = 3.2f;

        /// <summary>
        /// <c>Body.Scale</c> 的镜像下限。
        /// <para>原版飞行末段把卡面缩到 0.1（见类注释第二条根因）。壳层如果 1:1 跟，就会跟着缩成一个点；
        /// 完全不管又会变成一个空金框。取 0.55 当下限：卡片缩到 0.1 时壳层还剩 0.55，
        /// 视觉上是"卡片褪进弹壳里了"。</para>
        /// </summary>
        private const float MinMirror = 0.55f;

        /// <summary>
        /// <b>顶部弹头帽占比</b>（2026-10-19 第二轮"做得看得出来"）。
        /// <para>上一版壳层全是 1~3px 的细线、alpha 0.3~0.9 —— 卡在 300×422 上只缩到手掌大，
        /// 飞行又只有 1 秒左右，玩家根本来不及看到"几根金线"（用户实测："我看不太出来"）。
        /// 现在改成<b>一整块实心金</b>压住卡的上沿 —— 面积大到不可能看漏。</para>
        /// </summary>
        private const float CapFrac = 0.135f;

        /// <summary><b>底部壳底占比</b> —— 实心黄铜带，弹壳底缘。</summary>
        private const float BandFrac = 0.085f;

        /// <summary>主边框粗细（上一版 3.2，太细）。</summary>
        private const float RimWidth = 5.0f;

        /// <summary>底火圆半径占卡宽的比例。</summary>
        private const float PrimerFrac = 0.062f;

        /// <summary>竖向高光条的滑动速度（每秒多少个来回）。</summary>
        private const float FlowSpeed = 0.85f;

        /// <summary>收拢动作时长（秒）—— 上膛「咔」和退壳末段共用。</summary>
        private const float CollapseTime = 0.13f;

        // ── 状态 ──

        internal Control? Card;
        internal Control? Body;
        internal Mode Mode_;

        /// <summary>兜底寿命：超过就自己走人（防止某条路没收到收场信号而留下一个常驻金框）。</summary>
        internal float Life = 1.2f;

        /// <summary>起始亮度（0..1）。</summary>
        internal float Glow;

        /// <summary>上膛用的目标亮度（淡入终点）。</summary>
        internal float TargetGlow = 0.92f;

        /// <summary>上膛用的淡入时长（秒）；0 = 不退，直接满亮起手。</summary>
        internal float FadeInTime;

        /// <summary>退壳：从寿命的百分之几开始淡出。</summary>
        internal float FadeAt = 0.72f;

        /// <summary>退壳：从寿命的百分之几开始收拢。</summary>
        internal float CollapseFrom = 0.62f;

        /// <summary>退壳：收拢到的程度（0 = 不收）。</summary>
        internal float CollapseTo;

        private float _t;
        private float _glow;
        private float _target;
        private float _flow;
        private float _collapse;
        private float _snapT = -1f;      // ≥0 = 正在被 SleeveSnap 收掉
        private bool _dead;

        // ── 生命周期 ──

        public override void _Ready()
        {
            _glow = Glow;
            _target = FadeInTime > 0.01f ? TargetGlow : Glow;
        }

        /// <summary>落位「咔」：先爆一下白，再整体往中心收拢，收完就走。</summary>
        internal void Snap()
        {
            if (_dead)
            {
                return;
            }

            _snapT = 0f;
            _target = 1f;
        }

        public override void _Process(double delta)
        {
            try
            {
                if (_dead)
                {
                    return;
                }

                if (Card == null || !GodotObject.IsInstanceValid(Card)
                    || Body == null || !GodotObject.IsInstanceValid(Body))
                {
                    Retire();
                    return;
                }

                float dt = (float)delta;
                _t += dt;

                if (Mode_ == Mode.Eject)
                {
                    TickEject(dt);
                }
                else
                {
                    TickChamber(dt);
                }

                _flow = Mathf.PosMod(_flow + dt * FlowSpeed, 1f);
                MirrorBodyScale();
                QueueRedraw();
            }
            catch
            {
                Retire();
            }
        }

        private void TickEject(float dt)
        {
            // 亮度：满强度 → FadeAt 之后线性淡出
            float fadeStart = Life * FadeAt;
            _glow = _t <= fadeStart
                ? _target
                : Mathf.Max(0f, _target * (1f - (_t - fadeStart) / Mathf.Max(0.05f, Life - fadeStart)));

            // 收拢：末段慢慢把壳收紧（"壳被抛掉、越飞越紧"）
            float from = Life * CollapseFrom;
            _collapse = _t <= from
                ? 0f
                : Mathf.Lerp(0f, CollapseTo, FieldFxEase.OutCubic(
                    Mathf.Clamp((_t - from) / Mathf.Max(0.05f, Life - from), 0f, 1f)));

            if (_t >= Life + 0.05f)
            {
                Retire();
            }
        }

        private void TickChamber(float dt)
        {
            if (_snapT >= 0f)
            {
                // 收壳阶段：0 → 1 走完 CollapseTime
                _snapT += dt;
                float k = Mathf.Clamp(_snapT / CollapseTime, 0f, 1f);

                // 先爆白（前 25%），再整体暗下去
                _glow = k < 0.25f
                    ? Mathf.Lerp(_target, 1f, FieldFxEase.OutCubic(k / 0.25f))
                    : Mathf.Lerp(1f, 0f, FieldFxEase.InCubic((k - 0.25f) / 0.75f));

                _collapse = FieldFxEase.InCubic(k) * 0.95f;

                if (k >= 1f)
                {
                    Retire();
                }

                return;
            }

            // ★ 自愈：到点还没等到 SleeveSnap（「咔」那一下没触发）⇒ 自己走收拢。
            if (_t >= ChamberAutoSnap)
            {
                Snap();
                return;
            }

            // 淡入阶段
            if (FadeInTime > 0.01f && _t < FadeInTime)
            {
                _glow = Mathf.Lerp(Glow, TargetGlow, FieldFxEase.OutCubic(_t / FadeInTime));
            }
            else
            {
                _glow = TargetGlow;

                // 坐着不动的时候给一点点呼吸，避免看起来像一张静态贴图
                _glow *= 0.94f + 0.06f * Mathf.Sin(_t * 6f);
            }

            if (_t >= Life)
            {
                Retire();
            }
        }

        /// <summary>
        /// 镜像卡面的缩放，但<b>带下限</b>（见 <see cref="MinMirror"/>）。
        /// <para>之所以只镜像缩放、<b>不</b>镜像 <c>Modulate</c>：见类注释第二条根因 ——
        /// 卡面会黑掉，壳层必须不黑，否则白搭。</para>
        /// </summary>
        private void MirrorBodyScale()
        {
            if (Body == null || !GodotObject.IsInstanceValid(Body))
            {
                return;
            }

            Vector2 bs = Body.Scale;
            float k = Mathf.Max(MinMirror, Mathf.Min(Mathf.Abs(bs.X), Mathf.Abs(bs.Y)));
            Scale = Vector2.One * k;
        }

        private void Retire()
        {
            if (_dead)
            {
                return;
            }

            _dead = true;
            _sleeves.Remove(this);
            QueueFree();
        }

        public override void _ExitTree()
        {
            _dead = true;
            _sleeves.Remove(this);
        }

        // ── 绘制 ──

        public override void _Draw()
        {
            Vector2 s = Size;

            if (s.X < 8f || s.Y < 8f)
            {
                return;
            }

            float g = _glow;

            if (g <= 0.02f)
            {
                return;
            }

            // ★★★ 2026-10-20：两种模式画的**不是同一个东西**。
            //
            //   · 退壳（Eject）：卡被当成一枚弹壳抛出去 ⇒ 要读出"弹壳"的身份 ——
            //     壳底亮带 + 抽壳槽 + 底火 + 底沿亮线，这些是"壳"的记号。
            //   · 上膛（Chamber）：卡是被顶进弹膛的子弹 ⇒ 读的是"被顶进去"，
            //     只要**顶帽 + 边框 + 顶沿亮弧**；壳底那一套画上去只会变成
            //     "牌底部横着一条白光带"（用户截图里 1/2/5 号卡就是这个）。
            //
            //   之前两种模式共用同一套画法，于是抽牌那 0.5 秒里每张牌屁股上都挂一条亮带。
            bool isEject = Mode_ == Mode.Eject;

            // 边缘暖光在"上膛"时也要压下去：牌最后是停在手上的，不能一直泛黄。
            float edgeAlpha = isEject ? 0.30f : 0.16f;

            float cx = s.X * 0.5f;
            float cy = s.Y * 0.5f;
            float sx = 1f - _collapse * 0.82f;
            float sy = 1f - _collapse * 0.42f;

            float x0 = Inset;
            float y0 = Inset;
            float x1 = s.X - Inset;
            float y1 = s.Y - Inset;
            float inW = x1 - x0;
            float inH = y1 - y0;
            float cr = Mathf.Min(CornerRadius, Mathf.Min(inW, inH) * 0.5f);

            // 所有坐标都过这一层映射，收拢动画才能整体生效。
            Vector2 P(float x, float y) => new(cx + (x - cx) * sx, cy + (y - cy) * sy);

            // ───────────────────────────────────────────────────────────────
            //  ★★★ 2026-10-19 第三轮：去掉"廉价感"。
            //
            //  上一版被用户判「覆盖卡牌的金光有点廉价」，原因很具体，不是"不够大"：
            //    ① **整卡一层 0.20 平涂金色** —— 平涂没有明暗层次，读起来是塑料薄膜不是金属；
            //       而且把卡图整个洗掉，第一眼看到的是"蒙了层黄"。
            //    ② **所有元素都是硬边轴对齐矩形** —— 卡本身是圆角的，方框压上去四个角直接戳出来。
            //    ③ **实心 0.88 的金条** —— 一块贴纸，越亮越假。
            //  上一轮为了"看得出来"把面积和 alpha 一起加大，方向就错了：那是把廉价放大。
            //
            //  这一版只改一件事：**用渐变代替平涂，用圆角轮廓代替矩形**。
            //    · 删掉整卡平涂 ⇒ 换成四条"边缘内侧暖光"渐变（卡图保持清晰，只有边缘亮）
            //    · 外发光 / 主边框 / 帽顶亮弧 / 底沿亮线 全部沿**圆角路径**走（不再戳角）
            //    · 帽、底改成"一侧浓一侧透"的竖直渐变 ⇒ 读成"被光照到的金属面"而不是油漆块
            //    · 明暗对比靠 Ink 的细刻痕，不靠堆面积
            //  ───────────────────────────────────────────────────────────────

            // ① 外发光：3 圈沿圆角走的轮廓，越外越淡
            for (int i = 3; i >= 1; i--)
            {
                float pad = i * 5.5f;
                DrawPolyline(Outline(pad), new Color(SleeveGold, 0.12f * g / i), 3.6f, true);
            }

            // ② 边缘内侧暖光 —— 替代原来的整卡平涂。
            //    四条带子互不重叠（左右两条夹在上下两条之间），否则四角会叠出两块亮斑。
            float vig = Mathf.Min(inW, inH) * 0.17f;
            var edge = new Color(SleeveBrass, edgeAlpha * g);
            var none = new Color(SleeveBrass, 0f);

            Quad4(P(x0, y0), P(x1, y0), P(x1, y0 + vig), P(x0, y0 + vig), edge, edge, none, none);
            Quad4(P(x0, y1 - vig), P(x1, y1 - vig), P(x1, y1), P(x0, y1), none, none, edge, edge);
            Quad4(P(x0, y0 + vig), P(x0 + vig, y0 + vig), P(x0 + vig, y1 - vig), P(x0, y1 - vig),
                edge, none, none, edge);
            Quad4(P(x1 - vig, y0 + vig), P(x1, y0 + vig), P(x1, y1 - vig), P(x1 - vig, y1 - vig),
                none, edge, edge, none);

            // ③ 顶部金属帽：上浓下透的竖直渐变（不是一整块实心金）
            float capH = Mathf.Max(30f, s.Y * CapFrac);

            Quad4(P(x0, y0), P(x1, y0), P(x1, y0 + capH), P(x0, y0 + capH),
                new Color(SleeveGold, 0.58f * g), new Color(SleeveGold, 0.58f * g),
                new Color(SleeveGold, 0.02f * g), new Color(SleeveGold, 0.02f * g));

            // 帽内上沿的暖白顶光（只占帽高的 42%，越往下越没有）
            float hipH = capH * 0.42f;

            Quad4(P(x0 + cr, y0), P(x1 - cr, y0), P(x1 - cr, y0 + hipH), P(x0 + cr, y0 + hipH),
                new Color(SleeveHot, 0.26f * g), new Color(SleeveHot, 0.26f * g),
                new Color(SleeveHot, 0f), new Color(SleeveHot, 0f));

            // 帽顶沿的亮弧（子弹的圆头）—— 沿圆角路径的上半圈走
            DrawPolyline(TopEdge(), new Color(SleeveHot, 0.92f * g), 2.6f, true);

            // 帽内两道暗刻痕（明暗对比的来源，把"一片金"拆成"有结构的零件"）
            for (int i = 0; i < 2; i++)
            {
                float gx = x0 + inW * (i == 0 ? 0.34f : 0.66f);
                DrawLine(P(gx, y0 + capH * 0.22f), P(gx, y0 + capH * 0.78f),
                    new Color(SleeveInk, 0.30f * g), 2.0f, true);
            }

            // ④ 底部壳底：上透下浓的竖直渐变（**只有退壳画**，见 isEject 的注释）
            float bandH = Mathf.Max(34f, s.Y * BandFrac);
            float bandY = y1 - bandH;

            if (isEject)
            {
                Quad4(P(x0, bandY), P(x1, bandY), P(x1, y1), P(x0, y1),
                    new Color(SleeveBrass, 0.04f * g), new Color(SleeveBrass, 0.04f * g),
                    new Color(SleeveBrass, 0.66f * g), new Color(SleeveBrass, 0.66f * g));

                // 抽壳槽：两道横向暗线（沿用原设计，但压细压暗，别抢卡图）
                DrawLine(P(x0 + cr, bandY + bandH * 0.34f), P(x1 - cr, bandY + bandH * 0.34f),
                    new Color(SleeveInk, 0.38f * g), 2.0f, true);
                DrawLine(P(x0 + cr, bandY + bandH * 0.62f), P(x1 - cr, bandY + bandH * 0.62f),
                    new Color(SleeveInk, 0.26f * g), 1.6f, true);

                // 底沿亮线（沿圆角下半圈）
                DrawPolyline(BottomEdge(), new Color(SleeveHot, 0.60f * g), 2.4f, true);

                // ⑤ 底火：暗底座 + 黄铜盘 + 偏左上的一点高光（偏置才像金属球面）
                float pr = Mathf.Max(6f, Mathf.Min(inW * PrimerFrac, bandH * 0.34f));
                Vector2 primer = P(s.X * 0.5f, y1 - bandH * 0.42f);
                float prs = pr * sx;

                DrawCircle(primer, prs, new Color(SleeveInk, 0.62f * g));
                DrawCircle(primer, prs * 0.74f, new Color(SleeveBrass, 0.90f * g));
                DrawCircle(primer + new Vector2(-prs * 0.24f, -prs * 0.24f), prs * 0.28f,
                    new Color(SleeveHot, 0.85f * g));
            }

            // ⑥ 主边框：圆角轮廓。整圈先暗一点，再把上半圈提亮 —— 这就是"有方向的光"。
            DrawPolyline(Outline(0f), new Color(SleeveGold, 0.55f * g), 3.2f, true);
            DrawPolyline(TopEdge(), new Color(SleeveGold, 0.95f * g), 3.2f, true);

            // ⑦ 内里一圈极细的暖白边（金属的二次高光棱，不是第二个框）
            DrawPolyline(Outline(-4.5f), new Color(SleeveMilk, 0.20f * g), 1.3f, true);

            // ⑧ 箍环：卡高 46% 处的一道亮线 + 紧跟其下的暗线
            float clampY = s.Y * 0.46f;

            if (clampY > y0 + capH + 14f && clampY < bandY - 14f)
            {
                DrawLine(P(x0 + cr, clampY), P(x1 - cr, clampY), new Color(SleeveHot, 0.42f * g), 2.2f, true);
                DrawLine(P(x0 + cr, clampY + 3f), P(x1 - cr, clampY + 3f),
                    new Color(SleeveInk, 0.30f * g), 1.6f, true);
            }

            // ⑨ 流动高光：一条很淡的横向光带在筒身里缓慢上下（避免整块壳层"死"掉）
            float stripeH = s.Y * 0.22f;
            float travel = Mathf.Max(1f, bandY - (y0 + capH) - stripeH);
            float top = y0 + capH + Mathf.PosMod(_flow, 1f) * travel + stripeH;

            if (top > y0 + capH + 6f && top - stripeH < bandY - 6f)
            {
                Quad4(P(x0 + 6f, top - stripeH), P(x1 - 6f, top - stripeH), P(x1 - 6f, top), P(x0 + 6f, top),
                    new Color(SleeveHot, 0f), new Color(SleeveHot, 0f),
                    new Color(SleeveHot, 0.10f * g), new Color(SleeveHot, 0.10f * g));
            }

            // ── 局部工具 ──

            // 圆角矩形路径（未映射，最后统一过 P）
            Vector2[] RoundPath(float ax0, float ay0, float ax1, float ay1, float rad, int seg)
            {
                rad = Mathf.Clamp(rad, 0.5f, Mathf.Min((ax1 - ax0) * 0.5f, (ay1 - ay0) * 0.5f));

                var pts = new List<Vector2>((seg + 1) * 4);

                void Corner(float ccx, float ccy, float a0)
                {
                    for (int i = 0; i <= seg; i++)
                    {
                        float a = a0 + Mathf.Pi * 0.5f * (i / (float)seg);
                        pts.Add(new Vector2(ccx + Mathf.Cos(a) * rad, ccy + Mathf.Sin(a) * rad));
                    }
                }

                Corner(ax1 - rad, ay0 + rad, -Mathf.Pi * 0.5f);   // 右上
                Corner(ax1 - rad, ay1 - rad, 0f);                 // 右下
                Corner(ax0 + rad, ay1 - rad, Mathf.Pi * 0.5f);    // 左下
                Corner(ax0 + rad, ay0 + rad, Mathf.Pi);           // 左上

                return pts.ToArray();
            }

            // 外扩 pad 的整圈闭合路径（首尾同点，供 DrawPolyline 画闭合环）
            Vector2[] Outline(float pad)
            {
                Vector2[] raw = RoundPath(x0 - pad, y0 - pad, x1 + pad, y1 + pad, cr + pad, 5);
                var mapped = new Vector2[raw.Length + 1];

                for (int i = 0; i < raw.Length; i++)
                {
                    mapped[i] = P(raw[i].X, raw[i].Y);
                }

                mapped[raw.Length] = mapped[0];

                return mapped;
            }

            // 上半圈（左上圆角 → 顶边 → 右上圆角），用来给"受光的上沿"提亮
            Vector2[] TopEdge()
            {
                var pts = new List<Vector2>(16);

                for (int i = 0; i <= 6; i++)
                {
                    float a = Mathf.Pi + Mathf.Pi * 0.5f * (i / 6f);
                    pts.Add(P(x0 + cr + Mathf.Cos(a) * cr, y0 + cr + Mathf.Sin(a) * cr));
                }

                for (int i = 0; i <= 6; i++)
                {
                    float a = -Mathf.Pi * 0.5f + Mathf.Pi * 0.5f * (i / 6f);
                    pts.Add(P(x1 - cr + Mathf.Cos(a) * cr, y0 + cr + Mathf.Sin(a) * cr));
                }

                return pts.ToArray();
            }

            // 下半圈
            Vector2[] BottomEdge()
            {
                var pts = new List<Vector2>(16);

                for (int i = 0; i <= 6; i++)
                {
                    float a = Mathf.Pi * 0.5f * (i / 6f);
                    pts.Add(P(x1 - cr + Mathf.Cos(a) * cr, y1 - cr + Mathf.Sin(a) * cr));
                }

                for (int i = 0; i <= 6; i++)
                {
                    float a = Mathf.Pi * 0.5f + Mathf.Pi * 0.5f * (i / 6f);
                    pts.Add(P(x0 + cr + Mathf.Cos(a) * cr, y1 - cr + Mathf.Sin(a) * cr));
                }

                return pts.ToArray();
            }

            // 四边形 + 逐顶点渐变（默认的"渐变"手段：DrawPolygon 能吃每点一个 Color）
            void Quad4(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color ca, Color cb, Color cc, Color cd)
            {
                DrawPolygon(new[] { a, b, c, d }, new[] { ca, cb, cc, cd });
            }
        }
    }
}
