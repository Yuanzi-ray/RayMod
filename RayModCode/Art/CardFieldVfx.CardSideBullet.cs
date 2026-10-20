using System;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using RayMod.Ammo;

namespace RayMod.Art;

/// <summary>
/// <b>「拿起卡牌，一颗子弹飞过来停在旁边」</b>（2026-10-20 用户原话：
/// "提示一下如果你拿起卡牌。那么会有一颗子弹飞到卡牌旁边。直到你放下或者打出"）。
///
/// <para><b>为什么这一条值得做</b>：这是把「无限」从"她身上有个特效"变成
/// <b>"你每拿起一张牌，弹药自己就到手边"</b> —— 状态的表现落到了<b>玩家的操作</b>上，
/// 而不是只挂在她身上等你看。而且它依然是<b>实体子弹</b>，不是光。</para>
///
/// <para><b>生命周期</b>（严格对应那句话的三个动词）：</para>
/// <list type="bullet">
/// <item><b>拿起</b>：拖拽的第一帧从她胸口位置生成一颗子弹，0.20 秒飞到牌的右侧（<c>OutCubic</c>），
///   弹头<b>始终指向牌</b>（读作"要压进去了"）。</item>
/// <item><b>停在旁边</b>：就位后 <b>每帧硬贴</b>在牌的右侧（不 lerp —— 手在动，lag 会读成"没跟上"）。</item>
/// <item><b>放下 / 打出</b>：拖拽结束 ⇒ 钩子不再被调用 ⇒ 由节点自己 <c>超时 0.12s</c> 判定"松手了"，
///   0.16 秒内滑进牌里并淡出；牌被打出时卡节点直接被回收 ⇒ 子弹同一帧消失（读作"跟牌一起打出去了"）。</item>
/// </list>
///
/// <para><b>只在「无限」那回合出现</b>：判据是 <see cref="InfiniteOwner"/>（世界层那条弹链
/// 还在 ⇔ 本回合上限被打开）+ <c>AmmoCmd.IsOverCapThisTurn</c>。两个都查不到就不出现 ——
/// 这一条是加戏，绝不能因为有 bug 而让普通回合凭空多出一颗子弹。</para>
///
/// <para>⚠️ 只<b>加</b>节点，绝不碰牌 / holder 的任何属性（拖拽判定是核心交互）。</para>
/// </summary>
internal partial class CardFieldVfx
{
    /// <summary>绕牌轨道椭圆的半轴（<b>牌局部坐标</b>）：牌是 300×422，所以用椭圆贴着牌轮廓绕，
    /// 正圆会在牌的两侧飘太远、在上下又切进去。</summary>
    private const float CardBulletOrbitA = 188f;
    private const float CardBulletOrbitB = 240f;

    /// <summary>停靠角度（椭圆参数角）：≈ -17° ⇒ 右边缘、略偏上。</summary>
    private const float CardBulletParkAngle = -0.30f;

    /// <summary>子弹尺寸（特写，比弹链上那几颗大一圈 —— 要看得清弹头和底缘）。</summary>
    private const float CardBulletLen = 34f;
    private const float CardBulletWide = 11f;

    /// <summary>从她胸口飞到轨道入口的时长。</summary>
    private const float CardBulletFly = 0.16f;

    /// <summary>绕牌圈数（实际扫 1.4~2.4 圈，见 <c>_sweep</c> 的说明）。</summary>
    private const float CardBulletLoops = 2.4f;

    /// <summary>绕这两圈的时长（太快看不清是"绕"，太慢等不了）。</summary>
    private const float CardBulletOrbit = 0.70f;

    /// <summary>多久没被 tick 就当"松手了"。</summary>
    private const float CardBulletIdle = 0.12f;

    /// <summary>退场（滑进牌里 + 淡出）的时长。</summary>
    private const float CardBulletLeave = 0.16f;

    private static CardSideBullet? _cardSideBullet;

    /// <summary>
    /// 拖拽期间每帧调一次（由 <c>CardDragGhostPatch</c> 的 Postfix 接线）。
    /// <b>闸门按"便宜的先做"排序</b>：纯字段 → 节点有效性 → 无限状态缓存 → 才建节点。
    /// </summary>
    internal static void TickCardBullet(Control? card)
    {
        try
        {
            if (!Enabled)
            {
                DismissCardBullet();
                return;
            }

            if (card == null || !GodotObject.IsInstanceValid(card))
            {
                DismissCardBullet();
                return;
            }

            Player? owner = InfiniteOwner();

            if (owner == null || !OverCapCached(owner))
            {
                DismissCardBullet();
                return;
            }

            CardSideBullet? node = _cardSideBullet;

            if (node != null && (!GodotObject.IsInstanceValid(node) || node.Card != card))
            {
                // 换了一张牌 / 旧节点已经废了 ⇒ 直接换新的，不要试图复用（牌的尺寸可能不同）
                if (GodotObject.IsInstanceValid(node))
                {
                    node.QueueFree();
                }

                node = null;
                _cardSideBullet = null;
            }

            if (node == null)
            {
                node = SpawnCardBullet(card, owner);

                if (node == null)
                {
                    return;
                }
            }

            node.Touch();
        }
        catch (Exception e)
        {
            LogOnce($"[无限·子弹跟随] 失败：{e.GetType().Name}: {e.Message}");
            DismissCardBullet();
        }
    }

    /// <summary>立刻让子弹退场（总开关关了 / 状态没了 / 出异常时的统一出口）。</summary>
    private static void DismissCardBullet()
    {
        CardSideBullet? node = _cardSideBullet;

        if (node == null)
        {
            return;
        }

        _cardSideBullet = null;

        if (GodotObject.IsInstanceValid(node))
        {
            node.Leave();
        }
    }

    private static CardSideBullet? SpawnCardBullet(Control card, Player owner)
    {
        if (VfxParentForEffects() is not Node parent)
        {
            LogOnce("[无限·子弹跟随] 拿不到战斗特效容器，跳过");
            return null;
        }

        var node = new CardSideBullet
        {
            Name = "RayModCardSideBullet",
            Card = card,
            Owner_ = owner,
            ZIndex = 64,
            Material = FieldFx.Additive,
        };

        parent.AddChild(node);

        // 从她胸口飞过来（拿不到她就算从牌的位置长出来，至少不错位）
        object? creature = owner.Creature;
        Vector2 from = creature != null ? ChestPos(creature) : CardBulletWorldPos(card);

        node.Arm(from);
        _cardSideBullet = node;

        return node;
    }

    /// <summary>椭圆轨道上的点（牌局部坐标）。<paramref name="scale"/> = 1 时就落在停靠点那圈上。</summary>
    private static Vector2 OrbitPoint(float angle, float scale)
        => new(Mathf.Cos(angle) * CardBulletOrbitA * scale, Mathf.Sin(angle) * CardBulletOrbitB * scale);

    /// <summary>牌边上那个"停车位"的<b>世界坐标</b>。</summary>
    private static Vector2 CardBulletWorldPos(Control card)
    {
        // 牌节点的局部原点就在<b>卡面正中心</b>（%Frame = pos(-150,-211) size(300,422)），
        // 所以偏移量直接用"牌局部坐标"写，变换一次就到位，不用再叠半张卡。
        return card.GetGlobalTransform() * OrbitPoint(CardBulletParkAngle, 1f);
    }

    /// <summary>
    /// 世界层那条弹链还在 ⇔ 本回合「无限」生效。它由 <c>SpawnInfinityBurst</c> 建、
    /// 上限恢复时自己退场，所以它本身就是"这一回合"的时钟。
    /// </summary>
    private static Player? InfiniteOwner()
    {
        try
        {
            InfinityWorld? world = _infinityWorld;
            return world != null && GodotObject.IsInstanceValid(world) ? world.Owner_ : null;
        }
        catch
        {
            return null;
        }
    }

    // 超上限状态每 0.2s 查一次就够（拖拽期间每帧查等于每帧读一次存档状态）
    private static ulong _cardBulletQueryMs;
    private static bool _cardBulletOverCap = true;

    private static bool OverCapCached(Player owner)
    {
        ulong now = Time.GetTicksMsec();

        if (now - _cardBulletQueryMs >= 200UL)
        {
            _cardBulletQueryMs = now;

            try
            {
                _cardBulletOverCap = AmmoCmd.IsOverCapThisTurn(owner);
            }
            catch
            {
                // 查不到就按"还在"：世界层那条弹链才是真正的时钟，
                // 它自己会在上限恢复后退场，不该因为这里的一次异常就提前收掉。
                _cardBulletOverCap = true;
            }
        }

        return _cardBulletOverCap;
    }

    /// <summary>
    /// 画一颗<b>实体子弹</b>（局部 +X 为弹头方向）：弹壳身 + 底缘 + 抽壳槽 + 铜弹头 + 高光。
    /// 纯实心多边形，零贴图 ⇒ 任何分辨率、任何缩放下都是锐的。
    ///
    /// <para>抽出来做 static 是为了让 <see cref="CardSideBullet"/> 复用同一套画法 ——
    /// 弹链上的子弹和牌边这颗必须是<b>同一种子弹</b>，否则"实体元素"这套语汇就散了。</para>
    /// </summary>
    private static void DrawCartridge(CanvasItem canvas, float len, float wide, float alpha, float glint)
    {
        if (alpha <= 0.004f)
        {
            return;
        }

        float tipLen = len * 0.30f;
        float caseLen = len - tipLen;

        // 弹壳身
        canvas.DrawRect(new Rect2(-len * 0.5f, -wide * 0.5f, caseLen, wide),
                        new Color(InfCaseR, InfCaseG, InfCaseB, alpha));

        // 底缘（稍宽稍暗）
        canvas.DrawRect(new Rect2(-len * 0.5f, -wide * 0.58f, wide * 0.34f, wide * 1.16f),
                        new Color(InfInkR, InfInkG, InfInkB, alpha * 0.90f));

        // 抽壳槽（金属感全靠这道暗线）
        canvas.DrawLine(new Vector2(-len * 0.5f + wide * 0.55f, -wide * 0.5f),
                        new Vector2(-len * 0.5f + wide * 0.55f, wide * 0.5f),
                        new Color(InfInkR, InfInkG, InfInkB, alpha * 0.55f),
                        Mathf.Max(1f, wide * 0.16f), true);

        // 铜弹头
        Vector2[] tip =
        {
            new(-len * 0.5f + caseLen, -wide * 0.5f),
            new(len * 0.5f, 0f),
            new(-len * 0.5f + caseLen, wide * 0.5f),
        };

        canvas.DrawColoredPolygon(tip, new Color(InfTipR, InfTipG, InfTipB, alpha));

        // 上沿高光
        canvas.DrawLine(new Vector2(-len * 0.5f + caseLen, -wide * 0.42f),
                        new Vector2(len * 0.5f - wide * 0.16f, -wide * 0.06f),
                        new Color(InfCoreR, InfCoreG, InfCoreB, alpha * 0.55f),
                        Mathf.Max(1f, wide * 0.14f), true);

        if (glint > 0.01f)
        {
            float k = glint * alpha;

            canvas.DrawRect(new Rect2(-len * 0.5f, -wide * 0.5f, caseLen, wide),
                            new Color(InfMidR, InfMidG, InfMidB, 0.55f * k));
            canvas.DrawColoredPolygon(tip, new Color(InfCoreR, InfCoreG, InfCoreB, 0.45f * k));
        }
    }

    /// <summary>
    /// 牌边那颗子弹。三层状态：<b>飞过来</b> → <b>停在牌边（硬跟随）</b> → <b>退场</b>。
    ///
    /// <para>节点挂在<b>战斗特效容器</b>里（不是挂在牌上）：飞行段需要一个独立的世界坐标，
    /// 而挂到牌上就再也"飞"不起来了。就位之后再靠每帧写 <c>GlobalPosition</c> 贴住牌。</para>
    /// </summary>
    private sealed partial class CardSideBullet : Node2D
    {
        internal Control? Card;
        internal Player? Owner_;

        private Vector2 _from;
        private float _t;
        private float _lastTouch;
        private float _leave = -1f;
        private Vector2 _leaveBase = Vector2.One;
        private bool _armed;

        /// <summary>轨道入口的参数角（按"她的方向"算，于是子弹永远从她那一侧切进轨道）。</summary>
        private float _a0;

        /// <summary>
        /// 绕圈要扫过的总角度。
        ///
        /// <para>★ 这里有个必须算清的细节：绕完必须<b>正好停在停车角</b>，否则相位 ③ 会从
        /// "绕完的位置"瞬移到停车位，看着像跳了一下。所以扫角 = 从入口角到停车角的角度差
        /// ＋ (圈数-1)整圈。取 <c>Loops = 2.4</c> ⇒ 实际扫 1.4~2.4 圈：无论子弹从她那一侧
        /// 哪个方向切进来，都至少绕一圈半、并且精确落在停车位。</para>
        /// </summary>
        private float _sweep;

        /// <summary>伪 3D 深度：0 = 在牌后面，1 = 在牌前面（只有绕圈时用）。</summary>
        private float _depth = 1f;

        internal void Arm(Vector2 from)
        {
            _from = from;
            _t = 0f;
            _lastTouch = 0f;
            _armed = true;

            // 入口角：把"她"反变换到牌局部坐标，再除椭圆半轴换成参数角。
            // 不这么做的话，入口点可能是牌的正下方 —— 子弹会从底下钻出来绕，读不出"从她那儿飞来"。
            try
            {
                if (Card != null && GodotObject.IsInstanceValid(Card))
                {
                    Vector2 localFrom = Card!.GetGlobalTransform().AffineInverse() * from;
                    _a0 = Mathf.Atan2(localFrom.Y / CardBulletOrbitB, localFrom.X / CardBulletOrbitA);
                }
            }
            catch
            {
                _a0 = Mathf.Pi;   // 兜底：从左边进
            }

            // 收尾必须精确落在停车角（否则相位③会瞬移一下）
            _sweep = Mathf.PosMod(CardBulletParkAngle - _a0, Mathf.Tau)
                     + Mathf.Tau * (CardBulletLoops - 1f);

            GlobalPosition = from;
            SetProcess(true);
            QueueRedraw();
        }

        /// <summary>拖拽还在继续 ⇒ 续命（松手后没人再调它，靠 <see cref="CardBulletIdle"/> 超时退场）。</summary>
        internal void Touch()
        {
            _lastTouch = _t;
        }

        /// <summary>外部要求退场（总开关关闭 / 状态消失）。</summary>
        internal void Leave()
        {
            if (_leave < 0f)
            {
                _leave = 0f;
                _leaveBase = Scale;   // 从当前大小接着收，避免中途松手时尺寸突跳
            }
        }

        public override void _Process(double delta)
        {
            try
            {
                float dt = Mathf.Min((float)delta, 0.05f);
                _t += dt;

                if (!_armed)
                {
                    QueueFree();
                    return;
                }

                // 牌没了 = 打出去了 ⇒ 子弹跟着消失（读作"跟牌一起打出去"）
                if (Card == null || !GodotObject.IsInstanceValid(Card))
                {
                    QueueFree();
                    return;
                }

                if (_leave < 0f && _t - _lastTouch > CardBulletIdle)
                {
                    Leave();
                }

                if (_leave < 0f)
                {
                    Transform2D xf = Card!.GetGlobalTransform();

                    // 统一朝向目标：<b>弹头始终指向牌心</b>。
                    // ★ 为什么不用切线方向：绕圈用切线时，绕完停在停车位那一帧要从"切线"
                    //   瞬间翻成"指向牌"（约 100°），鼠标上看到的就是弹头"啪"地跳一下 ——
                    //   用户报的"停下来的位置存在瞬移"就是这个。改成永远指向牌心之后，
                    //   绕圈的最后一帧和停靠帧朝向完全一致，天然连续。
                    Vector2 toCenter = Card.GlobalPosition - GlobalPosition;
                    float radial = toCenter.LengthSquared() > 1f ? toCenter.Angle() : Rotation;

                    if (_t < CardBulletFly)
                    {
                        // ① 起飞：从她胸口 → 轨道入口（OutCubic）
                        float k = FieldFxEase.OutCubic(Mathf.Clamp(_t / CardBulletFly, 0f, 1f));
                        Vector2 entry = xf * OrbitPoint(_a0, 1.18f);
                        Vector2 flyDir = entry - _from;

                        GlobalPosition = _from.Lerp(entry, k);
                        _depth = 1f;
                        Scale = Vector2.One;

                        // 起飞段前 55% 沿速度方向，后 45% 平滑转到"指向牌心"
                        // （不然这里也会有一记 90° 的翻转）
                        float blend = Mathf.Clamp((k - 0.55f) / 0.45f, 0f, 1f);

                        if (flyDir.LengthSquared() > 4f)
                        {
                            Rotation = Mathf.LerpAngle(flyDir.Angle(), radial, FieldFxEase.InOutSine(blend));
                        }
                    }
                    else if (_t < CardBulletFly + CardBulletOrbit)
                    {
                        // ② ★ 绕牌两圈（用户："飞过来不明显，可以绕卡牌两圈"）
                        //    角度用 InOutSine 缓入缓出；半径 1.18 → 1.00 ⇒ 一边转一边贴上去，
                        //    扫角已保证精确落在停车角（见 _sweep），所以收尾无跳变。
                        float k = FieldFxEase.InOutSine(
                            Mathf.Clamp((_t - CardBulletFly) / CardBulletOrbit, 0f, 1f));

                        float ang = _a0 + _sweep * k;
                        float scale = Mathf.Lerp(1.18f, 1.00f, k);

                        Vector2 local = OrbitPoint(ang, scale);
                        GlobalPosition = xf * local;

                        // 伪 3D：转到牌"后面"那一半（局部 x 为负）时缩小 + 压暗，
                        // 否则子弹会平平地从卡图上划过去，读不出"绕着牌转"。
                        _depth = Mathf.Clamp((local.X / CardBulletOrbitA + 1f) * 0.5f, 0f, 1f);
                        Scale = Vector2.One * Mathf.Lerp(0.80f, 1f, _depth);

                        Rotation = radial;
                    }
                    else
                    {
                        // ③ 就位。用<b>阻尼跟随</b>而不是直接赋值：
                        //    即使绕圈的落点和停车位有几分像素的算法差，也会在 ~0.1 秒内平滑吃掉，
                        //    绝不会出现一帧瞬移。拖动时这点滞后看不出来（时间常数 0.04 秒）。
                        Vector2 park = xf * OrbitPoint(CardBulletParkAngle, 1f);
                        GlobalPosition = GlobalPosition.Lerp(park,
                            Mathf.Min(1f, (float)delta * 24f));
                        _depth = 1f;
                        Scale = Vector2.One;

                        Rotation = Mathf.LerpAngle(Rotation, radial, Mathf.Min(1f, (float)delta * 24f));
                    }
                }
                else
                {
                    // ③ 退场：滑进牌里 + 淡出
                    _leave += dt;

                    float k = FieldFxEase.InCubic(Mathf.Clamp(_leave / CardBulletLeave, 0f, 1f));
                    GlobalPosition = GlobalPosition.Lerp(Card!.GlobalPosition, 0.35f * k);
                    Scale = _leaveBase * Mathf.Lerp(1f, 0.55f, k);

                    if (_leave >= CardBulletLeave)
                    {
                        QueueFree();
                        return;
                    }
                }

                QueueRedraw();
            }
            catch (Exception e)
            {
                LogOnce($"[无限·子弹跟随] _Process 失败：{e.GetType().Name}: {e.Message}");
                QueueFree();
            }
        }

        public override void _Draw()
        {
            float alpha = Mathf.Lerp(0.55f, 1f, _depth);

            if (_leave >= 0f)
            {
                alpha *= 1f - Mathf.Clamp(_leave / CardBulletLeave, 0f, 1f);
            }

            // ★ 飞行 + 绕圈途中泛暖金（"被弹药推过来"），停稳后恢复纯金属色（只留一点环境反光）。
            float arriving = _t / (CardBulletFly + CardBulletOrbit);
            float glint = arriving < 1f ? 0.85f * (1f - arriving * 0.62f) : 0.12f;

            DrawCartridge(this, CardBulletLen, CardBulletWide, alpha, glint);
        }

        public override void _ExitTree()
        {
            if (_cardSideBullet == this)
            {
                _cardSideBullet = null;
            }
        }
    }
}
