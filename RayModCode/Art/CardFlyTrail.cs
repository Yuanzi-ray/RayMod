using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Logging;

// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;
using LogType = MegaCrit.Sts2.Core.Logging.LogType;

namespace RayMod.Art;

/// <summary>
/// ★★ <b>卡牌飞行的自绘拖尾 + 残影</b>（2026-10-19）。
///
/// <para><b>为什么不复用游戏那套 <c>NCardTrailVfx</c>（重要，别再走回头路）</b>：
/// 游戏的卡牌拖尾是每个角色一份 <c>res://scenes/vfx/card_trail_&lt;角色id&gt;.tscn</c>，
/// 而那份 <c>.tscn</c> 是靠 <c>[ext_resource type="Script" path="res://src/.../NCardTrailVfx.cs"]</c>
/// 把**游戏程序集里的 C# 脚本**挂上去的。
/// 我们从自己的 pck 里再写一份同样路径的引用时，<c>Instantiate&lt;NCardTrailVfx&gt;()</c> 拿到的是
/// 一个纯 <c>Node2D</c>（脚本没挂上）⇒ 强制转换失败 ⇒ <b>返回 null</b> ⇒
/// <c>NCardFlyVfx._Ready</c> 里那句 <c>if (_vfx != null)</c> 直接把拖尾跳过。
/// <b>表现就是"没有拖尾、也不报错"</b> —— 实测录制确认过（帧里只有裸卡在飞）。
/// ⇒ 结论：这条资源复刻路线不可用，必须<b>自己画</b>。</para>
///
/// <para><b>本类做什么</b>（三层，全部程序化，零贴图依赖）：
/// <list type="number">
///   <item><b>残影链</b>：沿飞行路径每隔 <see cref="GhostGap"/> 像素留一张卡面轮廓，
///         琥珀色加色、寿命 <see cref="GhostLife"/> 秒、边淡出边轻微放大 —— 这一层是"速度感"的主体；</item>
///   <item><b>光带</b>：一条跟着路径走的 <c>Line2D</c>，尾细头粗、尾部透明，加色混合；</item>
///   <item><b>起飞火星</b>：起点一簇一次性 <c>GpuParticles2D</c>（复用 <see cref="FieldFx.Sparks"/>）。</item>
/// </list></para>
///
/// <para><b>坐标系</b>：本节点自己<b>固定在世界原点</b>（不改 <c>Position</c>/<c>Rotation</c>），
/// 记的点全是全局坐标，赋给 <c>Line2D.Points</c> 前用 <see cref="Node2D.ToLocal"/> 转一次。
/// 这样父容器（战斗特效容器）有没有变换都不会错位。</para>
///
/// <para>⚠️ <b>生命周期</b>：卡牌由 <c>NCardFlyVfx</c> 负责释放，本节点不持有它；
/// 每帧检查卡牌是否还有效，失效即 <c>QueueFree</c>。绝不能让残影留在场上（铁律：手写特效必须自我回收）。</para>
/// </summary>
internal sealed partial class CardFlyTrail : Node2D
{
    private static readonly Logger Log = new("RayMod.CardTrail", LogType.Generic);

    // ───────────────────────── 调参（都在这里改）─────────────────────────

    /// <summary>光带总长度 = 卡牌在这段时间内走过的路（秒）。</summary>
    /// <remarks>
    /// ★ 2026-10-20：0.40 → <b>0.32</b>（用户：「常态减弱彗尾」）。
    /// 彗尾本身没有信息量，它只是把卡包起来；留太长会把卡图糊掉。
    /// </remarks>
    private const float TailLife = 0.32f;

    /// <summary>记点最小间距（像素）。太小会堆出几百个顶点。</summary>
    private const float MinPointDist = 10f;

    /// <summary>光带头部宽度（像素）。尾部靠 <see cref="Curve"/> 收到 18%。</summary>
    /// <remarks>
    /// ★ 2026-10-19 第二轮：58 → <b>34</b>。58 的时候光带等宽、边缘硬，
    /// 读起来像一根"橙色管子"；收细之后配合下面那层宽光晕才有"光"的质感。
    /// ★ 2026-10-20：42 → <b>26</b>（用户：「常态减弱彗尾」）。同时下面三处 alpha 也压了 ——
    /// 减的不是"有没有"，是"抢不抢戏"。
    /// </remarks>
    private const float RibbonWidth = 26f;

    /// <summary>光晕层宽度（比光带宽得多、透明度低得多，负责"柔边"）。</summary>
    private const float HaloWidth = 76f;

    /// <summary>光晕层不透明度。</summary>
    /// <remarks>
    /// ★ 2026-10-20：0.20 → <b>0.10</b>。118px 宽 × 0.20 的加色光晕在暗色战斗背景上
    /// 会糊成一坨"橙雾"，把卡和残影一起盖掉；收到一半之后残影的线框才立得住。
    /// </remarks>
    private const float HaloAlpha = 0.10f;

    /// <summary>光带颜色：<b>取自卡图 <c>images/cards/LoadingPosture.png</c> 的实测主色</b>。</summary>
    /// <remarks>
    /// 卡图实测调色板（<c>image_palette</c> 量的，不是猜的）：
    /// <c>#f4cc2b</c> 金黄 / <c>#f9da97</c> 奶金 / <c>#e3ad46</c> 琥珀金 / <c>#fff6db</c> 暖白 /
    /// 背景 <c>#0b2d4d</c> 深海军蓝（占 49%）。
    /// ⚠️ 之前用的 <c>(1, 0.72, 0.32)</c> = <c>#FFB852</c> <b>偏橘偏棕</b>，和卡图不是一个色系
    /// —— 这就是"廉价感"的来源之一（调研文档量化过：我们 vs 自己卡图契合度只有 0.187）。
    /// </remarks>
    private static readonly Color RibbonTint = new(0.957f, 0.800f, 0.169f);   // #f4cc2b

    /// <summary>奶金：光带中段的过渡色（<c>#f9da97</c>）。</summary>
    private static readonly Color RibbonMid = new(0.976f, 0.855f, 0.592f);

    /// <summary>暖白：光带头部最亮处（<c>#fff6db</c>）。</summary>
    private static readonly Color RibbonHot = new(1f, 0.965f, 0.859f);

    /// <summary>隔多少像素掉一簇黄铜碎屑（只在「填装形态」下开）。</summary>
    private const float BrassGap = 120f;

    /// <summary>一簇碎屑的粒子数（+ 下面把单颗放大，所以数量减到 3）。</summary>
    private const int BrassAmount = 3;

    /// <summary>单颗碎屑的缩放区间。★ 从 0.10~0.30 提到 0.18~0.55：原来太小，读不出"弹壳"。</summary>
    private const float BrassScaleMin = 0.18f;
    private const float BrassScaleMax = 0.55f;

    /// <summary>最多掉几簇（弃一整手牌时防爆）。</summary>
    private const int BrassMaxBursts = 12;


    /// <summary>每隔多少像素留一张残影。</summary>
    /// <remarks>
    /// 72 → 58（第三轮 72 太稀，读不出"拖影"）→ <b>38</b>（2026-10-20 用户：「增强线框拖尾」）。
    /// 残影是<b>线框</b>，覆盖面积只有几个像素宽，所以加密既不会过曝也不会糊卡，
    /// 反而会连成一条"全息轨道"。
    /// </remarks>
    private const float GhostGap = 38f;

    /// <summary>残影寿命（秒）。0.23 → <b>0.30</b>：配合加密，让相邻残影有时间重叠成链。</summary>
    private const float GhostLife = 0.30f;

    /// <summary>同时存在的残影上限（弃一整手牌时防爆）。11 → <b>18</b>。</summary>
    private const int GhostMaxAlive = 18;

    /// <summary>残影颜色（奶金，比光带淡一点）。</summary>
    private static readonly Color GhostTint = new(0.976f, 0.855f, 0.592f);

    // ───────────────────────── 状态 ─────────────────────────

    private Control _card = null!;

    /// <summary>卡面中心尺寸（<c>NCard.Size</c> 常是 (0,0)，所以从 <c>Body</c> 取）。</summary>
    private Vector2 _cardSize = new(300f, 422f);

    private Line2D _ribbon = null!;

    /// <summary>宽而淡的光晕层（柔边）。</summary>
    private Line2D? _halo;

    private readonly List<Vector2> _pts = new();     // 全局坐标
    private readonly List<float> _age = new();       // 与 _pts 平行

    private readonly List<Ghost> _ghosts = new();

    private Vector2 _lastPt;
    private bool _hasLast;
    private float _travel;
    private float _sinceGhost;
    private float _t;
    private bool _sparksDone;

    /// <summary>
    /// 全场景活着的残影。<b>★ 用列表而不是计数器（这是一个真 bug 的修复）</b>：
    /// 残影是 trail 的子节点，trail 被 <c>QueueFree</c> 时残影会跟着一起消失，
    /// 但一个 <c>static int</c> 计数器<b>减不回来</b> ⇒ 累计到上限之后<b>再也不生成残影</b>。
    /// 用户反馈的"线框有时候莫名其妙消失"就是它（跑一段时间后彻底静默）。
    /// 现在每次生成前先清掉已失效的引用，计数器天然自愈。
    /// </summary>
    private static readonly List<Ghost> AllGhosts = new();

    /// <summary>
    /// <b>「填装形态」专属：沿飞行路径往下掉黄铜碎屑</b>（对应卡图里满天飞舞、脚下堆成山的子弹弹壳）。
    /// 基础形态为 false —— 那时只有干净的一颗"子弹"。
    /// </summary>
    internal bool BrassRain;

    private float _sinceBrass;
    private int _brassBursts;

    /// <summary>
    /// 给一张正在飞的卡挂上拖尾。
    /// <paramref name="host"/> 应该是**比卡牌活得久**的节点（战斗特效容器）。
    /// </summary>
    internal static void Attach(Control card, Node host, bool brassRain = false)
    {
        try
        {
            if (card == null || !GodotObject.IsInstanceValid(card))
            {
                return;
            }

            if (host == null || !GodotObject.IsInstanceValid(host) || !host.IsInsideTree())
            {
                return;
            }

            var trail = new CardFlyTrail { BrassRain = brassRain };
            host.AddChild(trail);
            trail.Bind(card);
        }
        catch (Exception e)
        {
            Log.Warn($"[卡牌拖尾] 挂载失败（这张卡按原样飞）：{e.GetType().Name}: {e.Message}");
        }
    }

    private void Bind(Control card)
    {
        _card = card;
        _cardSize = ReadCardSize(card);

        // 光带：尾细头粗 + 尾部透明头部实
        var curve = new Curve();
        curve.AddPoint(new Vector2(0f, 0.18f));
        curve.AddPoint(new Vector2(0.55f, 0.62f));
        curve.AddPoint(new Vector2(1f, 1f));

        var gradient = new Gradient
        {
            Offsets = [0f, 0.35f, 0.8f, 1f],
            Colors =
            [
                new Color(RibbonTint.R, RibbonTint.G, RibbonTint.B, 0f),
                new Color(RibbonTint.R, RibbonTint.G, RibbonTint.B, 0.16f),
                new Color(RibbonMid.R, RibbonMid.G, RibbonMid.B, 0.42f),
                new Color(RibbonHot.R, RibbonHot.G, RibbonHot.B, 0.66f),
            ],
        };

        _ribbon = new Line2D
        {
            Name = "Ribbon",
            Width = RibbonWidth,
            // Godot 4 的 Line2D.width_curve 直接吃 Curve（不是 CurveTexture）——
            // 写成 curve.CreateCurveTexture() 会编译不过（CS1061）。
            WidthCurve = curve,
            Gradient = gradient,
            JointMode = Line2D.LineJointMode.Round,
            BeginCapMode = Line2D.LineCapMode.Round,
            EndCapMode = Line2D.LineCapMode.Round,
            Antialiased = true,
            Material = FieldFx.Additive,
            ZIndex = -1,          // 画在卡牌下面
        };

        AddChild(_ribbon);

        // ── 光晕层：比光带宽得多、淡得多，负责把"管子"化成"光" ──
        //    ⚠️ 必须用**独立的 Line2D**（各自有自己的 Points），不能靠 Modulate 做出来 ——
        //    同一条 Line2D 的亮度是一整套渐变，没法同时"又细又宽"。
        var haloGradient = new Gradient
        {
            Offsets = [0f, 0.45f, 1f],
            Colors =
            [
                new Color(RibbonTint.R, RibbonTint.G, RibbonTint.B, 0f),
                new Color(RibbonMid.R, RibbonMid.G, RibbonMid.B, HaloAlpha),
                new Color(RibbonHot.R, RibbonHot.G, RibbonHot.B, HaloAlpha * 1.6f),
            ],
        };

        _halo = new Line2D
        {
            Name = "Halo",
            Width = HaloWidth,
            WidthCurve = curve,
            Gradient = haloGradient,
            JointMode = Line2D.LineJointMode.Round,
            BeginCapMode = Line2D.LineCapMode.Round,
            EndCapMode = Line2D.LineCapMode.Round,
            Antialiased = true,
            Material = FieldFx.Additive,
            ZIndex = -2,          // 比光带更靠里
        };

        AddChild(_halo);

        SetProcess(true);        // ★ 显式开启（别赌"重写了 _Process 就会自动开"，巨眼那轮踩过）
        Log.Info($"[卡牌拖尾] 已挂上（卡面 {_cardSize.X:F0}×{_cardSize.Y:F0}）");
    }

    public override void _Process(double delta)
    {
        try
        {
            if (_card == null || !GodotObject.IsInstanceValid(_card) || !_card.IsInsideTree())
            {
                QueueFree();
                return;
            }

            float dt = Mathf.Min((float)delta, 0.05f);
            _t += dt;

            Vector2 head = _card.GlobalPosition;

            // ── 记点 + 老化 ──
            if (!_hasLast || head.DistanceTo(_lastPt) >= MinPointDist)
            {
                _pts.Add(head);
                _age.Add(0f);

                if (_hasLast)
                {
                    float seg = head.DistanceTo(_lastPt);
                    _travel += seg;
                    _sinceGhost += seg;
                    _sinceBrass += seg;
                }

                _lastPt = head;
                _hasLast = true;
            }

            // 头部永远贴着卡的当前位置（否则光带会滞后一段，看起来"断"）
            if (_pts.Count > 0)
            {
                _pts[^1] = head;
            }

            for (int i = 0; i < _age.Count; i++)
            {
                _age[i] += dt;
            }

            while (_age.Count > 0 && _age[0] > TailLife)
            {
                _age.RemoveAt(0);
                _pts.RemoveAt(0);
            }

            // ── 光带 + 光晕（共用同一组点）──
            var local = _pts.Count >= 2 ? new Vector2[_pts.Count] : null;

            if (local != null)
            {
                for (int i = 0; i < _pts.Count; i++)
                {
                    local[i] = ToLocal(_pts[i]);
                }
            }

            ApplyPoints(_ribbon, local);
            ApplyPoints(_halo, local);

            // ── 残影 ──
            if (_sinceGhost >= GhostGap)
            {
                _sinceGhost = 0f;
                SpawnGhost(head, _card.Rotation + BodyRotationOf(_card), BodyScaleOf(_card));
            }

            // ── 「填装形态」专属：沿路径掉黄铜碎屑 ──
            if (BrassRain && _sinceBrass >= BrassGap && _brassBursts < BrassMaxBursts)
            {
                _sinceBrass = 0f;
                _brassBursts++;
                SpawnBrass(head);
            }

            UpdateGhosts(dt);

            // ── 起飞火星（只做一次）──
            if (!_sparksDone)
            {
                _sparksDone = true;
                FieldFx.Sparks(GetParent() ?? this, GhostTint, 18, 90f, 320f, 0.45f,
                    gravity: 620f, scaleMin: 0.10f, scaleMax: 0.34f);
            }

            // 兜底：飞行通常 < 1s，2.5s 还在就说明卡已经没了或逻辑出错
            if (_t > 2.5f)
            {
                QueueFree();
            }
        }
        catch
        {
            QueueFree();
        }
    }

    /// <summary>把一组点喂给某条 Line2D；点为 null 时隐藏它。</summary>
    private static void ApplyPoints(Line2D? line, Vector2[]? points)
    {
        if (line == null || !GodotObject.IsInstanceValid(line))
        {
            return;
        }

        if (points == null)
        {
            line.Visible = false;
            return;
        }

        line.Points = points;
        line.Visible = true;
    }

    // ───────────────────────── 黄铜碎屑 ─────────────────────────

    /// <summary>
    /// 在 <paramref name="at"/> 掉一小簇黄铜碎屑。
    ///
    /// <para>用的是 <see cref="FieldFx.Sparks"/>（<c>GpuParticles2D</c> + 加色 + 程序化软光点）——
    /// <b>刻意不做成"一枚硬边小方块弹壳"</b>：上一版那个硬边弹壳被你判定为"黄噪点"，
    /// 而卡图上满地黄铜是<b>成片、发光、有颗粒感</b>的，用粒子簇才对。</para>
    /// </summary>
    private void SpawnBrass(Vector2 at)
    {
        try
        {
            Node? parent = GetParent();

            if (parent == null || !GodotObject.IsInstanceValid(parent))
            {
                return;
            }

            var burst = new Node2D { Name = "Brass" };
            parent.AddChild(burst);
            burst.GlobalPosition = at;

            Color[] ramp =
            [
                new Color(1f, 0.965f, 0.859f),      // #fff6db 暖白
                new Color(0.976f, 0.855f, 0.592f),  // #f9da97 奶金
                new Color(0.890f, 0.678f, 0.275f),  // #e3ad46 琥珀金
            ];

            GpuParticles2D sparks = FieldFx.Sparks(
                burst,
                new Color(0.957f, 0.800f, 0.169f),
                amount: BrassAmount,
                speedMin: 60f,
                speedMax: 240f,
                lifetime: 0.55f,
                gravity: 980f,                       // 往下掉 —— 这就是"弹壳落地"的读感
                scaleMin: BrassScaleMin,
                scaleMax: BrassScaleMax,
                oneShot: true,
                ramp: ramp);

            FieldFx.Ignite(sparks);
            FieldFx.FreeAfter(burst, 1.2);
        }
        catch
        {
            // 碎屑失败不影响主体
        }
    }

    // ───────────────────────── 残影 ─────────────────────────

    private void SpawnGhost(Vector2 at, float rotation, Vector2 scale)
    {
        // ★ 防御（2026-10-20，用户反馈"线框轨迹也有到处乱飞的现象"）：
        //   卡牌在过渡态（刚被回收 / 布局还没写 / 变换是单位阵）时读出来的坐标可能是
        //   原点或者极大值，画出来就是"屏幕角落莫名一道线框"。
        //   这里做三道最便宜的闸：有限值 → 不在天外 → 缩放不离谱。
        //   （只拦明显不可能是卡牌的值，正常飞行全程都过得去。）
        if (!float.IsFinite(at.X) || !float.IsFinite(at.Y))
        {
            return;
        }

        if (Mathf.Max(scale.X, scale.Y) > 3.0f)
        {
            return;
        }

        if (GetViewport() is Viewport vp)
        {
            Rect2 visible = vp.GetVisibleRect();

            if (!visible.Grow(visible.Size.Length() * 0.5f).HasPoint(at))
            {
                return;
            }
        }

        // ★ 先清掉已经失效的（trail 被释放时残影会一起没，引用就悬空了）。
        //   这一步是"线框再也不出现"那个 bug 的修复核心 —— 不清理就会越积越多直到永远跳过生成。
        AllGhosts.RemoveAll(g => g == null || !GodotObject.IsInstanceValid(g));

        if (AllGhosts.Count >= GhostMaxAlive)
        {
            return;
        }

        try
        {
            var ghost = new Ghost
            {
                Size = _cardSize,
                Tint = GhostTint,
                Life = GhostLife,
            };

            AddChild(ghost);
            ghost.GlobalPosition = at;
            ghost.Rotation = rotation;
            ghost.Scale = scale;

            _ghosts.Add(ghost);
            AllGhosts.Add(ghost);
        }
        catch
        {
            // 残影失败不影响别的
        }
    }

    private void UpdateGhosts(float dt)
    {
        for (int i = _ghosts.Count - 1; i >= 0; i--)
        {
            Ghost g = _ghosts[i];

            if (!GodotObject.IsInstanceValid(g))
            {
                _ghosts.RemoveAt(i);
                continue;
            }

            if (g.Tick(dt))
            {
                AllGhosts.Remove(g);
                g.QueueFree();
                _ghosts.RemoveAt(i);
            }
        }
    }

    /// <summary>一张卡面轮廓残影：淡填充 + 亮边，加色混合，边淡出边轻微放大。</summary>
    private sealed partial class Ghost : Node2D
    {
        internal Vector2 Size = new(300f, 422f);
        internal Color Tint = Colors.White;
        internal float Life = 0.26f;

        private float _t;

        public override void _Ready()
        {
            Material = FieldFx.Additive;
            ZIndex = -1;
            SetProcess(false);      // 由父节点统一 Tick，避免每张残影各开一个 _Process
        }

        /// <summary>推进一帧；返回 true 表示该回收了。</summary>
        internal bool Tick(float dt)
        {
            _t += dt;

            // 边淡出边轻微放大（"被风吹散"的读感）
            Scale *= Vector2.One * (1f + 0.06f * dt / Mathf.Max(Life, 0.01f));

            QueueRedraw();
            return _t / Life >= 1f;
        }

        public override void _Draw()
        {
            float k = Mathf.Clamp(_t / Life, 0f, 1f);

            // ★ 1.6 次方淡出：比线性掉得快（老残影不拖泥带水），但比平方(2.0)掉得慢
            //   ——第三轮用平方时残影"刚出现就没了"，这也是"太不明显"的一部分。
            float fade = Mathf.Pow(1f - k, 1.6f);

            var half = Size * 0.5f;

            // ★ 2026-10-19 第二轮：从"线框"改成"实心卡面"。
            //
            //  原版只画描边（三条递减的矩形边框），两个问题：
            //   ① 读起来像"设计稿辅助线"，不像残影（用户："线框"）；
            //   ② 加色**细线**在亮背景（浅色地板/雪地）上几乎看不见 —— 这也是"莫名其妙消失"之一。
            //  实心面 + 加色在明暗背景上都稳，而且更像"卡过去留下的影子"。
            //
            //  层次（由外到内，全部加色）：
            //   ① 外扩一圈很淡的光晕（模拟柔边）
            //   ② 主填充（奶金，随寿命淡出）
            //   ③ 内部再压一档更亮的核心（让卡面有厚度）
            //   ④ 一圈细亮边（保留"卡"的轮廓，但不再是主体）

            // ★★ 2026-10-19 定稿方向：**保留线框感**（用户："我其实挺喜欢那个线框的感觉"）。
            //
            //  所以不去做"实心卡面"，而是把**轮廓**做亮、做粗、做成双线全息感。
            //  这样既不会像实心填充那样叠成白块（线框本身覆盖面积小，叠加远不到 1），
            //  又比原来那版（描边 0.26、几乎看不见）清楚得多。
            //
            //  层次：
            //   ① 极淡的"面"（0.03）—— 只为了让轮廓读起来是一张卡而不是一个字框
            //   ② 外轮廓（0.85，宽 1.8）—— 主体
            //   ③ 内缩一圈的第二条轮廓（0.34，宽 1.0）—— 全息/技术感来源
            //  ★ 2026-10-20（用户：「常态减弱彗尾、增强线框拖尾」）：
            //    光带整体压到 26px / alpha 0.16~0.66，这里反过来把线框立起来 ——
            //    外轮廓 0.85→1.00、宽 1.8→2.4；内轮廓 0.34→0.50、宽 1.0→1.4。
            //    线框是**细线**，加亮加粗只增加"清楚度"，不会像实心面那样叠成白块。
            var surface = Tint;
            surface.A = 0.05f * fade;
            DrawRect(new Rect2(-half, Size), surface);

            var outer = Tint;
            outer.A = 1.00f * fade;
            DrawRect(new Rect2(-half, Size), outer, filled: false, width: 2.4f);

            var inner = RibbonHot;
            inner.A = 0.50f * fade;
            DrawRect(new Rect2(-half + new Vector2(4f, 4f), Size - new Vector2(8f, 8f)),
                inner, filled: false, width: 1.4f);
        }
    }

    // ───────────────────────── 取卡面几何（反射一次，缓存）─────────────────────────

    private static System.Reflection.PropertyInfo? _bodyProp;
    private static bool _bodyResolved;

    private static Control? BodyOf(Control card)
    {
        try
        {
            if (!_bodyResolved)
            {
                _bodyResolved = true;
                _bodyProp = card.GetType().GetProperty("Body");
            }

            return _bodyProp?.GetValue(card) as Control;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 卡面尺寸。<c>NCard.Size</c> 在实测里是 <c>(0,0)</c>（诊断日志 `card size=(0, 0)`），
    /// 所以优先取 <c>Body</c>（= <c>%CardContainer</c>）的尺寸，再退回 <c>NCard.defaultSize</c>。
    /// </summary>
    private static Vector2 ReadCardSize(Control card)
    {
        Control? body = BodyOf(card);

        if (body != null && body.Size.X > 8f && body.Size.Y > 8f)
        {
            return body.Size;
        }

        if (card.Size.X > 8f && card.Size.Y > 8f)
        {
            return card.Size;
        }

        return new Vector2(300f, 422f);
    }

    private static float BodyRotationOf(Control card)
    {
        Control? body = BodyOf(card);
        return body != null && GodotObject.IsInstanceValid(body) ? body.Rotation : 0f;
    }

    private static Vector2 BodyScaleOf(Control card)
    {
        Control? body = BodyOf(card);
        return body != null && GodotObject.IsInstanceValid(body) ? body.Scale : Vector2.One;
    }

    // ── 给 CardFieldVfx.DragGhost（拖拽残影）复用的三个入口 ──
    //
    //  拖拽残影和飞行残影必须是同一套几何口径，否则同一个 mod 里两种残影大小对不上。

    /// <summary>卡面可视尺寸（反射取 <c>Body</c>，<c>NCard.Size</c> 实测是 0,0）。</summary>
    internal static Vector2 CardVisualSize(Control card) => ReadCardSize(card);

    /// <summary>卡面自身的旋转（不含父节点）。</summary>
    internal static float CardBodyRotation(Control card) => BodyRotationOf(card);

    /// <summary>卡面自身的缩放（不含父节点）。</summary>
    internal static Vector2 CardBodyScale(Control card) => BodyScaleOf(card);
}
