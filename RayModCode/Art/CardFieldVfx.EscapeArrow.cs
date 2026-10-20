using System;
using Godot;

namespace RayMod.Art;

/// <summary>
/// <b>「脱身矢」（<see cref="RayMod.Cards.EscapeArrow"/>）的场上表现</b>（2026-10-20）。
///
/// <para><b>用户口径（逐字）</b>：<i>"做脱身矢。击中敌人时，将莱伊模型移出。之后返回。
/// 敌人也要被打出轻微位移。还可以参考一下卡图"</i>。所以这一套要做四件事，
/// 缺任何一件就退化成"又一个命中爆闪"：</para>
/// <list type="number">
///   <item>一根<b>银蓝鱼叉</b>真的从她那边飞到敌人身上（卡图的绝对主视觉）；</item>
///   <item>命中点<b>白色四角星爆 + 深红放射</b>（卡图的背景就是深红放射速度线 + 一团纯白星爆）；</item>
///   <item><b>她被"甩"出去再回来</b>——卡图里她整个人是坐在后滑姿态上的，
///         所以这里做的是"急速后滑 + 缩小淡出 + 脚下扬尘"，0.62 秒后弹回原位；</item>
///   <item>敌人挨一下<b>真实位移</b>（不是原来 <c>HitReact</c> 那种原地抖，
///         而是先被推出去 30px 再弹回来）。</item>
/// </list>
///
/// <para><b>调色板全部量自卡图</b>
/// （<c>D:\study\sts2mod\Raymod\RayMod\RayMod\images\cards\EscapeArrow.png</c>，
/// <c>image_palette</c> 实测：红 60.2% / 粉 16.1% / 蓝 11% / 青 5.4%，
/// 主色 <c>#EC3448</c>、暗底 <c>#4D0613</c>、星爆 <c>#FAFCFD</c>、
/// 冷光 <c>#CFF3FD</c>）。这里一个暖金都没用 —— 那是狙击/霰弹枪的色系，
/// 这张牌在卡面上就是<b>冷银 + 血红</b>，混色会立刻显出"不是一套"。</para>
///
/// <para><b>为什么挂在伤害钩子上而不是出牌上</b>：同 <c>CardFieldVfx.Snipe</c> 的说明 ——
/// 出牌只是"决定要打"，真正的伤害在若干帧后落地，而且
/// <c>Hook.AfterDamageGiven</c> 才会同时给出 <c>dealer</c> / <c>target</c> / <c>cardSource</c>。
/// 这张牌是单段单目标，所以只有一击。</para>
///
/// <para>⚠️ 零贴图（<c>FieldFx</c> 原语 + 自绘多边形），理由见 <c>CardFlyTrail</c> 里
/// "Asset not cached: res://...tscn" 那段记录。</para>
/// </summary>
internal partial class CardFieldVfx
{
    // ── 调色板（全部取自卡图）────────────────────────────────────────────
    private static readonly Color EscWhite = new(1.000f, 0.992f, 0.996f);        // #FAFCFD 星爆 / 箭头高光
    private static readonly Color EscIce = new(0.812f, 0.953f, 0.992f);          // #CFF3FD 冷白拖尾 / 冷光
    private static readonly Color EscSteel = new(0.549f, 0.647f, 0.741f);        // 银蓝箭身亮面
    private static readonly Color EscSteelDark = new(0.145f, 0.196f, 0.259f);    // 箭身暗面 / 描边
    private static readonly Color EscCrimson = new(0.925f, 0.204f, 0.282f);      // #EC3448 卡图主色（倒钩 / 羽翼）
    private static readonly Color EscCrimsonDeep = new(0.302f, 0.024f, 0.075f);  // #4D0613 放射线暗底
    private static readonly Color EscBrass = new(0.784f, 0.627f, 0.353f);        // 卡图上矛的铜箍 / 铜尾帽
    private static readonly Color EscBrassDark = new(0.443f, 0.333f, 0.153f);
    private static readonly Color EscDust = new(0.478f, 0.435f, 0.400f, 0.34f);  // 脚下扬尘（灰褐，非加色）

    // ── 时序 ─────────────────────────────────────────────────────────────
    /// <summary>鱼叉飞行时间。比狙击的 0.055 慢一点 —— 这是一根<b>带重量的矛</b>，不是子弹。</summary>
    private const float ArrowTravel = 0.11f;

    /// <summary>命中星爆的余韵长度。</summary>
    /// <summary>
    /// 爆点的<b>总时长</b>。原来是 0.34 —— 实机反馈「肉眼实际看不清」，因为所有层
    /// 都在同一瞬间起跑：白核、白星、红环、彩星、碎玻璃全叠在头 0.2 秒里，
    /// 人眼在正常速度下只读到一个白团，读不出里面的结构。
    ///
    /// <para>修法不是"加大加亮"（那只会更白），而是<b>把时间轴拉开、让各层接力</b>：
    /// 头 0.12 秒只有白核和短星芒（负责"打中了"这一下的冲击），
    /// <b>白光死掉之后</b>红青分色的色散星芒、彩虹环、碎玻璃才一个个浮出来，
    /// 一直铺到 0.8 秒。眼睛先看到"亮"，再看到"彩色"，最后看到"碎片在飘"。</para>
    /// </summary>
    private const float ArrowBurst = 0.80f;

    /// <summary>她后滑甩出去的时间（前段要快，读作"被拽走"）。</summary>
    private const float SlideOutTime = 0.16f;

    /// <summary>她在外面停留（缩成一个小影子）的时间 —— 没有这一段就只是"抖了一下"。</summary>
    private const float SlideHoldTime = 0.13f;

    /// <summary>弹回来的时间（用 OutBack 带一点过冲）。</summary>
    private const float SlideBackTime = 0.36f;

    /// <summary>后滑距离。2510 宽的画面上 330px ≈ 13%，一眼能看出"人挪了"。</summary>
    private const float SlideDist = 205f;

    /// <summary>
    /// 她后滑时往下沉多少。
    /// <para>★ 2026-10-21 用户要求「把角色后退改成向左平移」，所以这里必须是 <b>0</b> ——
    /// 非零就变成"往左下斜着退"，不是平移。同理 <see cref="SlideAway"/> 里的倾角也归零。</para>
    /// </summary>
    private const float SlideDrop = 0f;

    /// <summary>矛整体缩放。1.0 时整根矛只有 90px，在 2510px 的画面里读不出来。</summary>
    private const float ArrowScale = 1.55f;

    /// <summary>甩出去时缩到多少（配合淡出读作"退到远处"而不是"变小"）。</summary>
    private const float SlideShrink = 0.82f;

    /// <summary>甩出去时的透明度下限（不做到 0 是为了还能看见她"在那儿"）。</summary>
    private const float SlideAlpha = 0.28f;

    /// <summary>敌人被推出去的距离（用户要求"轻微位移"，30px 在 2510 宽上刚好能看出但不夸张）。</summary>
    private const float KnockPx = 150f;

    /// <summary>敌人推出去再弹回来的总时间。</summary>
    private const float KnockTime = 0.46f;

    // ── 节流 ─────────────────────────────────────────────────────────────
    /// <summary>顿帧/抖屏 的节流（同一张牌连打时有间隔，避免连续震）。</summary>
    private static ulong _lastEscapeMs;

    /// <summary>每个生物节点上一次被甩出去的时刻（600ms 内不重复，免得两个 tween 抢 Position）。</summary>
    private static readonly System.Collections.Generic.Dictionary<ulong, ulong> _escapeSlideMs = new();

    /// <summary>排查用：只探一次「写进去的坐标到底有没有留住」。</summary>
    private static bool _slideProbed;

    /// <summary>
    /// 残影专用的高层画布。战斗特效容器在出牌容器**下面** —— 打出的那张卡正好挡在她身前，
    /// 残影画在低层会被卡面整块盖住（这就是"看不见残影"的真正原因，探针已确认坐标写对了）。
    /// CanvasLayer 挂在场景根上、层级 20，什么都不遮，坐标直接用视口像素。
    /// </summary>
    private static CanvasLayer? _ghostCanvas;

    /// <summary>
    /// 残影的宿主 = **战斗界面根节点**（`NCombatRoom.Instance.Ui`）。
    /// 战斗特效容器在出牌容器**下面** —— 打出的那张卡正好挡在她身前，残影画在低层会被卡面整块盖住
    /// （探针确认过坐标是写对的，问题纯粹是层序）。挂到 Ui 根上并给个大 ZIndex，就能压在卡面上。
    /// CanvasLayer 那条路走不通：它的子节点用的是窗口像素，而这套坐标是 1920x1080 画布空间
    /// （实测 screenTransform scale=1.333、origin=(0,80)）。
    /// </summary>
    private static Node2D GhostHost()
    {
        try
        {
            object? room = AccessTools_TypeByName("MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom")?
                .GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?
                .GetValue(null);

            if (room == null)
            {
                return null!;
            }

            if (room.GetType().GetProperty("Ui")?.GetValue(room) is not Control ui)
            {
                return null!;
            }

            if (ui.GetNodeOrNull("RayModEscapeGhosts") is Node2D existing)
            {
                return existing;
            }

            var host = new Node2D { Name = "RayModEscapeGhosts", ZIndex = 2000 };
            ui.AddChild(host);

            return host;
        }
        catch
        {
            return null!;
        }
    }
    // ══════════════════════════════════════════════════════════════════════
    //  入口
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 「脱身矢」的命中演出。<b>返回 true 表示已经认领这一次伤害</b>，
    /// 调用方（<c>CardFieldVfx.OnDamageGiven</c>）不得再叠通用命中层 ——
    /// 否则鱼叉和通用爆闪会同时出现，反而糊。
    /// </summary>
    internal static bool EscapeArrowHit(object cardSource, object? dealer, object? target)
    {
        // 不是这张牌 ⇒ 不认领，交给后面的分支
        if (cardSource is not RayMod.Cards.EscapeArrow)
        {
            return false;
        }

        // 是这张牌 ⇒ 无论能不能画都认领（宁可什么都不演，也不要叠出通用爆闪）
        if (!Enabled || TestModeIsOn() || target == null)
        {
            return true;
        }

        if (VfxParentForEffects() is not Control layer)
        {
            LogOnce("[脱身矢·表现] 拿不到战斗特效容器（VfxParentForEffects 为 null）");
            return true;
        }

        Vector2 to = ChestPos(target);
        Vector2 from = dealer != null ? ChestPos(dealer) : Vector2.Zero;

        if (from == Vector2.Zero || to == Vector2.Zero)
        {
            LogOnce($"[脱身矢·表现] 定位失败：from={from} to={to}");
            return true;
        }

        Vector2 delta = to - from;

        if (delta.LengthSquared() < 64f)
        {
            // 两个生物位置几乎重合（宠物贴脸之类），飞行没有意义，只留星爆
            SpawnCrimsonBurst(layer, to, Vector2.Right);
            LogOnce("[脱身矢·表现] 双方位置过近，只播星爆");
            return true;
        }

        Vector2 dir = delta.Normalized();

        SpawnEscapeRound(layer, from, to, dir);

        // 她这边先动：截影 → 后滑。截影必须在 SlideAway 之前 —— 那一帧她还在原地，
        // 抠下来的才是"原来的她"。这一段是"开完枪立刻走"，和命中无关，所以不等。
        SpawnCapturedGhosts(dealer, Vector2.Left, layer);
        SlideAway(dealer, dir, layer);

        // 命中表现必须等弹头真的飞到（ArrowTravel）。不等的话帧序是
        // "敌人已经炸了 / 弹头还在半路"，慢放一看就穿帮。
        object? hitTarget = target;

        FieldFx.After(layer, ArrowTravel, "[脱身矢·命中]", () =>
        {
            if (!GodotObject.IsInstanceValid(layer))
            {
                return;
            }

            // 先把"她到敌人"这条线画出来，再在终点炸 —— 顺序颠倒会少掉"连成一条"的观感
            SpawnTrajectoryAfterglow(layer, from, to, dir);
            SpawnCrimsonBurst(layer, to, dir);
            // 全屏这一下必须最后建（读屏幕纹理的 quad 要排在容器最上层）
            // 用户点名要的三样：色散要**看得见彩色**、暗一下要有、缩放模糊收着用
            // （zoom 太猛会把画面拉成糊的，色散才是"飒"的来源）。
            SpawnScreenImpact(layer, to, zoom: 1.25f, split: 0.075f, vignette: 0.18f);

            // 命中这一下再抖一次屏。出牌时那次 DoHitStop 是"她开枪"，
            // 这一次是"打中了"—— 两个不同的冲击点，分开抖才读得出先后。
            // 用 Medium/Short 而不是 Strong：Strong 会和出牌那次撞在一起变成一路乱抖。
            ScreenShake("Medium", "Short");

            Knockback(hitTarget, dir, layer);
        });

        ulong now = Time.GetTicksMsec();

        if (now - _lastEscapeMs > 400UL)
        {
            _lastEscapeMs = now;
            DoHitStop();
        }

        Diag("escapearrow", $"[脱身矢·表现] 命中：from={from} to={to} 距离={delta.Length():F0}", 6);

        return true;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ① 银蓝鱼叉
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 一根带倒钩的银蓝矛从 <paramref name="from"/> 飞到 <paramref name="to"/>。
    /// <para>形状用<b>非加色</b>多边形拼（加色永远只能变亮，金属的暗面就没了）：
    /// 暗描边 → 亮箭身 → 白色矛尖 → 深红倒钩与尾羽，外加一道加色高光条。
    /// 尾部拖两条 <see cref="Line2D"/> 当运动残迹，长度随飞行拉长。</para>
    /// <para>★ 缓冲区陷阱：<c>FieldFx.Root</c> 已经设过 <c>GlobalPosition</c>，
    /// 之后每帧写的是 <c>Position</c>（相对父节点），所以必须先存
    /// <c>basePos = root.Position</c> 再叠加位移 —— 直接写 <c>delta * e</c> 会让整根箭
    /// 从特效容器的原点飞出去。</para>
    /// </summary>
            // ── 弹头贴图（本机 STS2 LoRA 出图 + 绿幕抠图，见 _tools/gen_round.py / cut_round.py）──
    private const string EscapeRoundPath = Entry.ResPath + "/images/vfx/escape_round.png";

    /// <summary>弹头在屏幕上的长度（像素）。贴图宽 512，按比例缩。</summary>
    private const float RoundLength = 104f;

    private static Texture2D? _escRoundTex;

    private static Texture2D? EscapeRoundTexture()
    {
        if (_escRoundTex != null && GodotObject.IsInstanceValid(_escRoundTex))
        {
            return _escRoundTex;
        }

        try
        {
            _escRoundTex = GD.Load<Texture2D>(EscapeRoundPath);

            if (_escRoundTex == null)
            {
                LogOnce($"[脱身矢·弹头] 贴图加载失败：{EscapeRoundPath}");
            }
        }
        catch (Exception e)
        {
            LogOnce($"[脱身矢·弹头] {e.GetType().Name}: {e.Message}");
        }

        return _escRoundTex;
    }

    /// <summary>
    /// 打出去的那发弹头。
    /// <para>★ 这里原本是手拼多边形的一根"矛"（带倒钩和尾羽）—— 那是<b>凭空发明的形状</b>，
    /// 用户的原话是"哪里来的渔叉"。这张牌本来就是她开一枪，所以现在用的是一枚
    /// <b>真正的子弹</b>：本机 STS2 LoRA 出的精灵（黄铜弹壳 + 铜箍 + 银蓝弹尖），
    /// 绿幕抠成透明贴图。贴图缺失时退化成一根白热短棒，至少不会"什么都没有"。</para>
    /// </summary>
    private static void SpawnEscapeRound(Control layer, Vector2 from, Vector2 to, Vector2 dir)
    {
        Texture2D? tex = EscapeRoundTexture();

        // 枪口：抛一枚弹壳 + 一小团硝烟 —— 她确实"开了一枪"，不是凭空飞出一发弹
        SnipeCasing(layer, from, dir.X);

        Node2D muzzle = FieldFx.Root(layer, from, "RayModEscapeMuzzle");
        SmokePuff(muzzle, new Color(0.62f, 0.60f, 0.58f, 0.30f), 20f, 0.34f, new Vector2(dir.X * 70f, -22f), 3);
        DebrisShards(muzzle, EscSteel, 5, 90f, 240f, 2.4f, 0.34f);
        FieldFx.FreeAfter(muzzle, 0.9f);

        Node2D root = FieldFx.Root(layer, from, "RayModEscapeRound");
        Vector2 basePos = root.Position;
        root.Rotation = dir.Angle();
        root.ZIndex = 6;

        float halfLen = RoundLength * 0.5f;

        if (tex != null)
        {
            var sprite = new Sprite2D
            {
                Name = "Round",
                Centered = true,
                Texture = tex,
                Scale = Vector2.One * (RoundLength / Mathf.Max(1f, tex.GetWidth())),
            };

            root.AddChild(sprite);
        }
        else
        {
            FieldFx.Line(root, new Vector2[] { new(-halfLen, 0f), new(halfLen, 0f) }, EscWhite, 7f);
        }

        // 尾焰：从弹尾往后拖（加色，外冷光 + 内纯白）
        Line2D halo = FieldFx.Line(
            root,
            new Vector2[] { new(-halfLen - 40f, 0f), new(-halfLen, 0f) },
            new Color(EscIce, 0.22f),
            11f);

        Line2D core = FieldFx.Line(
            root,
            new Vector2[] { new(-halfLen - 40f, 0f), new(-halfLen, 0f) },
            new Color(EscWhite, 0.82f),
            3.4f);

        // 尾端白色爆闪（枪口焰）：卡图里那团白星就在弹尾
        Polygon2D muzzleStar = FieldFx.Poly(root, StarPts(22f, 6f, 0f), new Color(EscWhite, 0.90f), additive: false);
        muzzleStar.Position = new Vector2(-halfLen, 0f);

        FieldFx.Animate(muzzleStar, 0.15f, k =>
        {
            muzzleStar.Scale = Vector2.One * Mathf.Lerp(1.30f, 0.50f, FieldFxEase.OutCubic(k));
            muzzleStar.Modulate = new Color(1f, 1f, 1f, 1f - FieldFxEase.InCubic(k));
        });

        // 残影宿主：弹头每一小段就复制一份淡蓝的自己去前面，闭成一条"运动残像链"。
        // 光靠 Line2D 拖尾读作"一条线"，叠上真身残影才有速度感。
        Node2D ghostHost = FieldFx.Root(layer, Vector2.Zero, "RayModEscapeGhosts");
        float ghostScale = tex != null ? RoundLength / Mathf.Max(1f, tex.GetWidth()) : 1f;
        int ghostNext = 0;
        const int ghostTotal = 4;

        FieldFx.FreeAfter(ghostHost, ArrowTravel + 0.7f);

        Vector2 span = to - from;

        FieldFx.Animate(root, ArrowTravel, k =>
        {
            // OutCubic：出膛瞬间最快，接近目标时收住（不会"匀速滑过去"）
            float e = FieldFxEase.OutCubic(k);
            root.Position = basePos + (span * e);

            // 尾焰长度跟着拉长，前 45% 就拉满
            float len = Mathf.Lerp(26f, 116f, Mathf.Clamp(k / 0.45f, 0f, 1f));
            halo.SetPointPosition(0, new Vector2(-halfLen - (len * 1.35f), 0f));
            core.SetPointPosition(0, new Vector2(-halfLen - len, 0f));

            if (tex != null && ghostNext < ghostTotal && e >= (ghostNext + 1f) / (ghostTotal + 1f))
            {
                ghostNext++;
                SpawnRoundGhost(ghostHost, tex, root.GlobalPosition, root.Rotation, ghostScale, 0.26f - (ghostNext * 0.04f));
            }
        });

        // 停在目标上 0.08 秒淡掉（不淡的话会有一枚弹头"钉"在敌人身上）
        FieldFx.After(root, ArrowTravel, "[脱身矢·弹头淡出]", () =>
        {
            try
            {
                FieldFx.Animate(root, 0.08f, k => root.Modulate = new Color(1f, 1f, 1f, 1f - k));
            }
            catch
            {
                // 节点已被回收就什么都不做
            }
        });

        FieldFx.FreeAfter(root, ArrowTravel + 0.22f);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ② 命中：白色四角星爆 + 深红放射
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>命中爆点</b>（2026-10-21 整层重做）。
    ///
    /// <para><b>用户口径</b>：<i>"打在敌人身上的命中特效太简陋了"</i>。旧版之所以简陋，是四件事：
    /// ① 星芒是<b>纯色多边形</b>（读作一张发光纸片）；② 冲击环是 <c>Line2D</c> 的<b>等宽圆</b>
    /// （几何题里的圆，不像光）；③ 中央是一整块白，<b>没有由亮到暗的衰减</b>；
    /// ④ 背景<b>完全没被推动</b>，所以整层像"贴在画面上"，不像"从画面里炸出来"。</para>
    ///
    /// <para>现在按"贵"的那一套重排，四层同时存在：</para>
    /// <list type="number">
    ///   <item><b>中心白热核</b> —— <see cref="FieldFxShaders.Flash"/>，<c>pow(1-d, falloff)</c>
    ///         的软衰减，0.30 秒内从 0.25 涨到 1.15 再落回 0；</item>
    ///   <item><b>三层星芒</b> —— <see cref="FieldFx.StarVaried"/> 让每根尖长短不一，
    ///         再用 <see cref="FieldFx.SetStarGradient"/> 把尖端的 alpha 抹到 0
    ///         （<b>光从核心到尖端是渐隐的</b>，纯色尖端是最廉价的信号）；</item>
    ///   <item><b>三层着色器冲击环</b> —— <see cref="FieldFxShaders.Ring"/> 自带
    ///         硬环芯 / 6 倍宽柔光 / 16 倍宽外晕 / 第二道细环 / 圆周明暗起伏 / 断口，
    ///         比 <c>Line2D</c> 的等宽圆高一个量级；</item>
    ///   <item><b>屏幕折射</b> —— <see cref="FieldFxShaders.Refract"/> 把背景真的推歪一圈，
    ///         这是整套里最"贵"的一下，所有纯贴图方案都做不到。</item>
    /// </list>
    ///
    /// <para>外加"<b>贯穿</b>"：箭是从敌人身上穿过去的，所以爆点后面还有一处
    /// 更小的出口爆点 + 一条白热贯穿线 —— 只有爆点没有出口，读起来像"打在盾上"。</para>
    /// </summary>
    private static void SpawnCrimsonBurst(Control layer, Vector2 at, Vector2 dir)
    {
        Node2D root = FieldFx.Root(layer, at, "RayModEscapeBurst");
        root.ZIndex = 7;

        Vector2 u = dir.LengthSquared() > 0.0001f ? dir.Normalized() : Vector2.Right;
        float baseAngle = u.Angle();

        // ── ① 中心白热核：软衰减的 Flash，取代原来那一整块白多边形 ──────────
        // 半径 190→148、寿命 0.30→0.24：太大的白核会把底下三层星芒糊成一片白，
        // 上一版"看得到光、看不出结构"就是这个原因。
        ColorRect core = FieldFx.ShaderQuad(root, FieldFxShaders.Flash, 148f);
        FieldFx.SetParam(core, "tint", EscWhite);
        FieldFx.SetParam(core, "alpha", 0f);
        FieldFx.SetParam(core, "falloff", 3.10f);
        core.Scale = Vector2.One * 0.001f;

        // 白核只负责"打中了"这一下：0.20 秒后必须彻底让开，否则后面所有彩色层
        // 都会被它压平成一团白（这就是"看不清"的根因）。
        FieldFx.Animate(core, 0.20, k =>
        {
            FieldFx.SetParam(core, "alpha", 1f - FieldFxEase.InCubic(k));
            core.Scale = Vector2.One * Mathf.Lerp(0.30f, 1.06f, FieldFxEase.OutExpo(k));
        });
        FieldFx.FreeAfter(core, 0.26);

        // 冷色外晕（比白核活得久一点，让"亮"有两个层次）
        ColorRect halo = FieldFx.ShaderQuad(root, FieldFxShaders.Flash, 760f);
        FieldFx.SetParam(halo, "tint", new Color(EscIce, 0.38f));
        FieldFx.SetParam(halo, "alpha", 0f);
        FieldFx.SetParam(halo, "falloff", 4.2f);

        // 外晕是大面积白，0.28 秒收干净；多留 0.1 秒都是在洗掉色散。
        FieldFx.Animate(halo, 0.28, k =>
        {
            FieldFx.SetParam(halo, "alpha", 0.10f * (1f - k));
        });
        FieldFx.FreeAfter(halo, 0.34);

        // ── ①b 余晖：白核死后留在空气里的一团<b>暖白柔光</b> ─────────────
        //    存在的意义是"画面亮过之后不要瞬间回黑"——白核 0.20 秒就没了，
        //    直接掉回背景亮度会让整个爆点读作"闪了一下纸片"。
        //    这一层大到 940，衰减拉到 1.9（几乎没有硬边），只加亮度不加形状，
        //    alpha 从 0.34 起、0.60 秒才收干，正好垫在色散下面当底光。
        ColorRect afterglow = FieldFx.ShaderQuad(root, FieldFxShaders.Flash, 940f);
        FieldFx.SetParam(afterglow, "tint", new Color(1f, 0.87f, 0.74f));
        FieldFx.SetParam(afterglow, "alpha", 0f);
        FieldFx.SetParam(afterglow, "falloff", 1.9f);

        FieldFx.Animate(afterglow, 0.60f, k =>
        {
            FieldFx.SetParam(afterglow, "alpha", 0.34f * (1f - FieldFxEase.OutCubic(k)));
            afterglow.Scale = Vector2.One * Mathf.Lerp(0.35f, 1.35f, FieldFxEase.OutCubic(k));
        });
        FieldFx.FreeAfter(afterglow, 0.66);

        // ── ② 三层星芒：长短不一 + 尖端渐隐 ──────────────────────────────
        //    寿命刻意压短（0.28~0.34）：它们是白色的，在**前排**负责冲击；
        //    再长就只是在替白核洗屏。
        AddStarLayer(root, baseAngle, 214f, 5, 0.070f, 0.32f, 0.34f, 0f, 0.10f,
            EscWhite, new Color(EscIce, 0f));
        AddStarLayer(root, baseAngle + 0.42f, 150f, 4, 0.105f, 0.30f, 0.32f, 0.03f, -0.16f,
            new Color(EscIce, 0.95f), new Color(EscCrimson, 0f));
        AddStarLayer(root, baseAngle - 0.75f, 104f, 7, 0.150f, 0.26f, 0.28f, 0.05f, 0.24f,
            EscWhite, new Color(new Color(0.925f, 0.204f, 0.282f), 0f));

        // ── ②b 色散星芒：红 / 青 / 白三份错开叠画 ─────────────────────────
        //    这一层是"色散长在爆点身上"，不读屏幕纹理、不依赖背景有没有高对比边缘。
        //    ★ 它是<b>唯一延迟 0.10 秒、活到 0.62 秒</b>的一层：等白核白晕都死透了
        //    才开始显形，于是正常速度下眼睛看到的是"白光散掉之后红边青边还在跳"。
        AddChromaticStar(root, baseAngle, 238f, 5, 0.072f, 0.62f, 0.10f, 34f);

        // ── ②c 针状尖刺：固体的尖（区别于线条光）────────────────────────
        NeedleSpikes(root, baseAngle, new Color(EscCrimson, 0.92f), 7, 196f, 0.46f);

        // ── ③ 放射速度线（这层保留：卡图背景就是一圈深红速度线）────────────
        for (int i = 0; i < 9; i++)
        {
            float a = baseAngle + ((i - 4f) * 0.215f) + (GD.Randf() - 0.5f) * 0.06f;
            Vector2 d = new(Mathf.Cos(a), Mathf.Sin(a));
            float len = 230f - (Mathf.Abs(i - 4f) * 24f);

            Line2D ray = FieldFx.Line(
                root,
                new Vector2[] { d * 30f, d * len },
                new Color(EscCrimson, 0.95f),
                7.0f);

            float startAt = 30f;
            FieldFx.Animate(ray, 0.50f, k =>
            {
                ray.Modulate = new Color(1f, 1f, 1f, 1f - FieldFxEase.InCubic(k));
                ray.SetPointPosition(0, d * Mathf.Lerp(startAt, len * 1.35f, FieldFxEase.OutCubic(k)));
                ray.SetPointPosition(1, d * Mathf.Lerp(len, len * 1.75f, FieldFxEase.OutCubic(k)));
            });
        }

        // ── ③b 斜向速度线（照她技能图的构图：一组平行斜线整体掠过）──────
        SpawnSlashLines(root, u);

        // ── ④ 三层着色器冲击环 ──────────────────────────────────────────
        //    顺序：白（最快最大）→ 深红 → 冷蓝。三道的起始半径/寿命都不同，
        //    叠起来才有"一圈一圈推出去"的层次；单道环永远像画了个圆。
        //    寿命 0.46~0.62：环是"扩出去"的运动，人眼跟得住的速度大约是 0.5 秒推开；
        //    0.3 秒级的速度在正常播放里只剩一下闪，看不到"环在走"。
        SpawnShaderRing(root, EscWhite, new Color(EscIce, 0.9f), 540f,
            r0: 0.12f, r1: 0.52f, w0: 0.062f, w1: 0.009f,
            life: 0.46f, delay: 0f, glow: 0.30f, halo: 0.045f, gap: 0.44f, radius2: 0f);

        SpawnShaderRing(root, new Color(EscCrimson, 1f), EscCrimsonDeep, 1000f,
            r0: 0.08f, r1: 0.74f, w0: 0.085f, w1: 0.011f,
            life: 0.62f, delay: 0.03f, glow: 0.55f, halo: 0.070f, gap: 0.42f, radius2: 0f);

        SpawnShaderRing(root, new Color(EscIce, 0.95f), new Color(EscWhite, 0.9f), 460f,
            r0: 0.16f, r1: 0.80f, w0: 0.040f, w1: 0.007f,
            life: 0.54f, delay: 0.10f, glow: 0.60f, halo: 0.14f, gap: 0.30f, radius2: 0f);

        // ── ④b 变形镜头光：一条极长的水平冷光横贯爆点 ────────────────────
        SpawnAnamorphicStreak(root, at, baseAngle, layer);

        // ── ④c 彩虹环 + 碎玻璃：一整个爆点里只有白和红，再怎么调都单调；
        //    这一道细彩环负责在同一帧里给出**全部色相**，碎玻璃负责给出**锐角的形状**。
        //    彩环延迟 0.12 秒浮出、活 0.58 秒 —— 它和白核错开，才不会被洗成白色。
        SpawnSpectrumRing(root, 820f, 0.34f, 0.12f, 0.58f);
        GlassShards(root, new Color(EscIce, 0.95f), 10, 220f, 640f, 15f, 0.87f);
        GlassShards(root, new Color(1f, 0.30f, 0.36f, 0.95f), 6, 260f, 720f, 11f, 0.80f);

        // ── ④d 命中时从爆点里甩出去的乱飞子弹 ───────────────────────────
        SpawnSpinningRounds(layer, at, u);

        // ── ⑤ 屏幕折射：背景被真的推歪一圈（最贵的一下）───────────────────
        ColorRect rf = FieldFx.ShaderQuad(root, FieldFxShaders.Refract, 1150f);
        FieldFx.SetParam(rf, "tint", EscIce);
        FieldFx.SetParam(rf, "strength", 0.030f);
        FieldFx.SetParam(rf, "alpha", 0f);

        FieldFx.Animate(rf, 0.55, k =>
        {
            float e = FieldFxEase.OutExpo(k);
            FieldFx.SetParam(rf, "radius", Mathf.Lerp(0.05f, 0.86f, e));
            FieldFx.SetParam(rf, "width", Mathf.Lerp(0.22f, 0.05f, e));
            FieldFx.SetParam(rf, "alpha", 1f - FieldFxEase.InCubic(k));
        });
        FieldFx.FreeAfter(rf, 0.62);

        // ── ⑥ 贯穿：箭从敌人身上穿过去，所以还有出口 ─────────────────────
        SpawnPierceExit(layer, at, u);

        // ── ⑦ 火星 / 碎屑 / 烟 ────────────────────────────────────────
        GpuParticles2D sparks = FieldFx.Sparks(
            root,
            EscIce,
            amount: 22,
            speedMin: 150f,
            speedMax: 470f,
            lifetime: 0.34f,
            gravity: 430f,
            scaleMin: 0.10f,
            scaleMax: 0.32f);

        sparks.Rotation = baseAngle - (Mathf.Pi * 0.5f);
        FieldFx.Ignite(sparks);

        DebrisShards(root, EscSteel, 10, 100f, 280f, 3.2f, 0.46f);
        DebrisShards(root, EscCrimson, 7, 120f, 330f, 2.4f, 0.42f);

        Node2D smoke = FieldFx.Root(layer, at, "RayModEscapeSmoke");
        SmokePuff(smoke, new Color(0.52f, 0.50f, 0.50f, 0.26f), 30f, 0.55f, u * 66f, 4);
        SmokePuff(smoke, new Color(0.30f, 0.03f, 0.08f, 0.22f), 24f, 0.48f, u * 40f, 3);
        FieldFx.FreeAfter(smoke, 1.2f);

        DebrisShards(root, EscSteelDark, 6, 140f, 360f, 2.6f, 0.55f);

        FieldFx.FreeAfter(root, ArrowBurst + 0.55f);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ②b 全屏冲击 / 弹道余辉 / 斜向速度线（2026-10-21 追加的"更酷炫"层）
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ★★ <b>全屏冲击</b>——命中那一下把<b>整个画面</b>拉向爆点：
    /// 径向缩放模糊（<c>zoom</c>）+ 三通道色散（<c>split</c>）+ 四周压暗（<c>vignette</c>）。
    ///
    /// <para><b>为什么这一层比爆点本身更重要</b>：爆点再华丽也只是"画面里的一块"，
    /// 而这一层改的是<b>观看方式</b> —— 镜头被拽了一下，人会本能地觉得"这一下很重"。
    /// 电影里所有重击都这么做，纯素材堆不出来。</para>
    ///
    /// <para><b>为什么复用 <see cref="FieldFxShaders.Impact"/></b>：它已经在「得见光芒」里
    /// 调到过能用的档位，而且读屏幕纹理的着色器对层级极其敏感，同一套代码风险最低。</para>
    ///
    /// <para>⚠️ <c>center</c> 要的是 <b>屏幕 UV</b>，不是画布坐标 —— 先拿面片自己的全局变换求逆，
    /// 把爆点换算成面片局部坐标再除以尺寸。算不出来就退回正中。</para>
    /// </summary>
    private static void SpawnScreenImpact(Control layer, Vector2 at, float zoom, float split, float vignette)
    {
        try
        {
            ColorRect quad = MakeFullScreenQuad(layer, "EscapeImpact", FieldFxShaders.Impact, bottom: false);

            Vector2 center = new(0.5f, 0.5f);
            Vector2 size = quad.Size;

            if (size.X > 16f && size.Y > 16f)
            {
                Vector2 local = quad.GetGlobalTransform().AffineInverse() * at;
                Vector2 uv = local / size;

                if (uv.X > 0.02f && uv.X < 0.98f && uv.Y > 0.02f && uv.Y < 0.98f)
                {
                    center = uv;
                }
            }

            FieldFx.SetParam(quad, "center", center);
            FieldFx.SetParam(quad, "zoom", 0f);
            FieldFx.SetParam(quad, "split", 0f);
            FieldFx.SetParam(quad, "vignette", 0f);
            FieldFx.SetParam(quad, "alpha", 0f);

            // 0.16 冲上去 → 0.34 收回来：快涨慢落，读作"镜头被拽了一下又弹回来"
            FieldFx.Animate(quad, 0.50, k =>
            {
                float e = k < 0.16f
                    ? FieldFxEase.OutExpo(k / 0.16f)
                    : 1f - FieldFxEase.InOutSine((k - 0.16f) / 0.84f);

                FieldFx.SetParam(quad, "zoom", zoom * e);
                FieldFx.SetParam(quad, "vignette", vignette * e);
                FieldFx.SetParam(quad, "alpha", Mathf.Clamp(e * 1.8f, 0f, 1f));
            });

            // ── 色散必须**自己一条时间轴**，不能跟着上面那条涨落。
            //    ★ 实机反馈「看不到彩色」的原因就在这：上面那条 0.16s 就冲到峰值、
            //      0.34s 就落回 0，在人眼里只是"亮了一下"，色散的偏移量根本没有停留。
            //      眼睛对**持续**的偏移远比瞬时的偏移敏感，所以这里做成
            //      **起手即满、然后整整半秒慢慢收**（开方衰减：前段掉得慢）。
            //    ⚠️ 深度（split 峰值）也必须够：低于 0.02 时，画面里没有高对比边缘的地方
            //      根本析不出颜色（暗紫地板、木纹都没有）。
            FieldFx.Animate(quad, 0.52, k =>
            {
                if (!GodotObject.IsInstanceValid(quad))
                {
                    return;
                }

                float s = Mathf.Sqrt(1f - k) * (1f - (0.35f * k));
                FieldFx.SetParam(quad, "split", split * s);
            });

            FieldFx.FreeAfter(quad, 0.58);
        }
        catch (Exception e)
        {
            LogOnce($"[脱身矢·全屏冲击] {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// <b>弹道余辉</b>：子弹飞过的那条线在空气里<b>留一下</b>再散开。
    ///
    /// <para>没有这一层，弹头一消失、命中点一亮，中间那 800px 就"什么都没发生过"；
    /// 有了它，画面才连成"从她手上到敌人身上"的一条。</para>
    ///
    /// <para>⚠️ 线的颜色 alpha 必须从 <b>1</b> 开始、只用 <c>Modulate</c> 控淡。
    /// 把 <c>DefaultColor</c> 写成 alpha=0 再拿 <c>Modulate</c> 提亮是<b>提不回来的</b>
    /// （两者是相乘），这条线上踩过一次就全隐了。</para>
    /// </summary>
    private static void SpawnTrajectoryAfterglow(Control layer, Vector2 from, Vector2 to, Vector2 dir)
    {
        Node2D root = FieldFx.Root(layer, from, "RayModEscapeGlow");
        root.ZIndex = 5;

        Vector2 span = to - from;
        Vector2[] pts = { Vector2.Zero, span };

        // 三层同线、宽度递减：34 / 16 / 5.5。单条线永远读作"一根铁丝"，
        // 叠三层才有"光在空气里散开"的厚度。
        Line2D outer = FieldFx.Line(root, pts, new Color(EscIce, 1f), 34f);
        Line2D glow = FieldFx.Line(root, pts, new Color(EscIce, 1f), 16f);
        Line2D core = FieldFx.Line(root, pts, new Color(EscWhite, 1f), 5.5f);

        // 头亮尾隐：Gradient 沿点序上色，起点=枪口（透明），终点=命中点（最亮）
        outer.Gradient = TrailGradient(new Color(EscIce, 0f), new Color(EscIce, 0.55f));
        glow.Gradient = TrailGradient(new Color(EscIce, 0f), new Color(EscCrimson, 0.85f));
        core.Gradient = TrailGradient(new Color(EscWhite, 0f), new Color(EscWhite, 1f));

        outer.Modulate = new Color(1f, 1f, 1f, 0f);
        glow.Modulate = new Color(1f, 1f, 1f, 0f);
        core.Modulate = new Color(1f, 1f, 1f, 0f);

        FieldFx.Animate(outer, 0.46f, k =>
        {
            float a = k < 0.08f ? k / 0.08f : 1f - FieldFxEase.InCubic((k - 0.08f) / 0.92f);
            outer.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(a * 0.30f, 0f, 1f));
        });

        FieldFx.Animate(glow, 0.36f, k =>
        {
            float a = k < 0.10f ? k / 0.10f : 1f - FieldFxEase.InCubic((k - 0.10f) / 0.90f);
            glow.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(a * 0.55f, 0f, 1f));
        });

        FieldFx.Animate(core, 0.24f, k =>
        {
            float a = k < 0.08f ? k / 0.08f : 1f - FieldFxEase.InCubic((k - 0.08f) / 0.92f);
            core.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(a * 0.9f, 0f, 1f));
        });

        // 沿线撒几颗冷色火星（各自朝上飘），一眼看出"这条线上真的飞过东西"
        for (int i = 1; i <= 5; i++)
        {
            Vector2 at = from + (span * (i / 6f));
            Node2D bit = FieldFx.Root(layer, at, "RayModEscapeGlowBit");
            bit.ZIndex = 5;

            GpuParticles2D p = FieldFx.Sparks(bit, EscIce, 3, 30f, 130f, 0.30f, -90f, 0.06f, 0.16f);
            FieldFx.Ignite(p);
            FieldFx.FreeAfter(bit, 0.55f);
        }

        FieldFx.FreeAfter(root, 0.50f);
    }

    /// <summary>
    /// <b>变形镜头光</b>（anamorphic streak）：一条横贯爆点的极长冷光。
    ///
    /// <para>为什么"酷"：它不属于任何物理物件，只属于<b>镜头</b>。观众在电影和现代
    /// 游戏里看过无数次，一出现就把画面读成"电影级的一击"。素材类特效堆不出来，
    /// 因为它画的是"摄影机被闪到了"，不是"东西炸了"。</para>
    ///
    /// <para>做法：三层同角度长条（宽 210 / 96 / 34）压扁成光带，用 Flash 着色器
    /// 当光斑而不是实心矩形，两端自然衰减。角度取<b>水平</b>而非沿弹道 ——
    /// 真实镜头的横向溢出永远是水平的。</para>
    /// </summary>
    private static void SpawnAnamorphicStreak(Node2D root, Vector2 at, float baseAngle, Control layer)
    {
        float[] widths = { 210f, 96f, 34f };
        float[] alphas = { 0.14f, 0.28f, 0.50f };
        float[] lives = { 0.40f, 0.46f, 0.52f };

        for (int i = 0; i < widths.Length; i++)
        {
            // ★★ **闭包陷阱，实机炸了 583 次**：`for` 的循环变量 `i` 在 C# 里**只有一个实例**，
            //    三个 lambda 捕获的是同一个变量；等动画真正开跑时 `i` 早就变成 3 了，
            //    于是 `alphas[3]` / `lives[3]` 直接抛 IndexOutOfRangeException。
            //    后果比"报个错"严重得多：异常抛在**设 alpha 那一行之前**，
            //    所以光度永远是 0 —— 这条横向镜头光**一次都没显示过**。
            //    修法：循环体里先拷一份局部变量，lambda 只捕获那份。
            int idx = i;

            ColorRect bar = FieldFx.ShaderQuad(root, FieldFxShaders.Flash, widths[idx]);
            FieldFx.SetParam(bar, "tint", idx == 2 ? EscWhite : EscIce);
            FieldFx.SetParam(bar, "alpha", 0f);
            FieldFx.SetParam(bar, "falloff", 1.6f);

            bar.Scale = new Vector2(6f, 0.045f);

            FieldFx.Animate(bar, lives[idx], k =>
            {
                if (!GodotObject.IsInstanceValid(bar))
                {
                    return;
                }

                float grow = FieldFxEase.OutExpo(k);
                FieldFx.SetParam(bar, "alpha", alphas[idx] * (1f - FieldFxEase.InCubic(k)));
                bar.Scale = new Vector2(Mathf.Lerp(6f, 19f, grow), Mathf.Lerp(0.045f, 0.020f, grow));
            });

            FieldFx.FreeAfter(bar, lives[idx] + 0.10f);
        }
    }

    /// <summary>
    /// <b>命中瞬间从爆点里甩出去的几枚乱飞子弹</b>——<b>边自转边岔开</b>地飞。
    ///
    /// <para>为什么加这个：爆点原本只有"光和碎片"，没有<b>实体</b>往外走。
    /// 子弹是她这张牌唯一的物件语汇（黄铜壳 + 银蓝尖），让几枚真的从爆心飞出去，
    /// 这一下才从"炸了一下"变成"打穿了、东西从另一侧崩出来"。</para>
    ///
    /// <para><b>三个必须做对的地方</b>：</para>
    /// <list type="number">
    /// <item><b>自转</b>：<see cref="Sprite2D"/> 是长的，不转就是 6 根平行短棒；
    /// 每枚各转各的（2.2~11 rad/s，正负各半），才读得出"翻着飞"。</item>
    /// <item><b>阻力</b>：速度按 <c>1-0.55k</c> 递减再叠一个抛物线落势，
    /// 匀速直线会像贴纸平移。</item>
    /// <item><b>起点错开</b>：每枚从爆心<b>内侧</b> -18~-44px 起跑，
    /// 这样它们是从光里"钻出来"的，而不是凭空出现在光斑外面。</item>
    /// </list>
    /// </summary>
    private static void SpawnSpinningRounds(Control layer, Vector2 at, Vector2 dir)
    {
        try
        {
            float baseAngle = dir.LengthSquared() > 0.0001f ? dir.Angle() : 0f;

            Node2D root = FieldFx.Root(layer, at, "RayModEscapeSpinners");
            root.ZIndex = 30;

            const int count = 7;

            for (int i = 0; i < count; i++)
            {
                // 岔开角度：在弹道两侧各摊开约 60°，再各加一点随机抖动 ——
                // 完全对称会排成一把扇子，加了抖动才是"乱飞"。
                float spread = ((i + 0.5f) / count) - 0.5f;          // -0.42 .. +0.42
                float a = baseAngle + (spread * 2.10f) + ((GD.Randf() - 0.5f) * 0.42f);

                Vector2 vel = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (560f + (GD.Randf() * 520f));

                float spin = (5.0f + (GD.Randf() * 9.0f)) * (i % 2 == 0 ? 1f : -1f);
                float life = 0.74f + (GD.Randf() * 0.30f);
                float delay = 0.018f * i;

                float len = 46f + (GD.Randf() * 22f);
                float rot0 = a + ((GD.Randf() - 0.5f) * 0.80f);

                var round = new SpinRound
                {
                    Name = "Spin" + i,
                    Length = len,
                    Wide = len * 0.42f,
                    Hot = i % 3 == 0,
                    Rotation = rot0,
                    Visible = false,
                };

                root.AddChild(round);

                Vector2 start = vel.Normalized() * -(22f + (GD.Randf() * 30f));
                round.Position = start;

                // 闭包捕获：循环变量一律先拷成局部（这个文件里踩过一次 IndexOutOfRange）
                SpinRound h = round;
                float vx = vel.X;
                float vy = vel.Y;
                float spinRate = spin;
                float lifeMs = life;
                Vector2 from = start;

                FieldFx.After(root, delay, "[脱身矢·乱弹]", () =>
                {
                    if (!GodotObject.IsInstanceValid(h))
                    {
                        return;
                    }

                    h.Visible = true;

                    FieldFx.Animate(h, lifeMs, k =>
                    {
                        if (!GodotObject.IsInstanceValid(h))
                        {
                            return;
                        }

                        float t = k * lifeMs;
                        float drag = 1f - (0.62f * k);

                        h.Position = from
                            + new Vector2(vx, vy) * t * drag
                            + new Vector2(0f, 340f * k * k);

                        h.Rotation = rot0 + (spinRate * t);
                        h.Scale = Vector2.One * Mathf.Lerp(1f, 0.68f, k);

                        // 前 62% 全亮，最后 38% 淡出 —— 一开始就淡会读不到"飞出去"
                        h.Alpha = k < 0.62f ? 1f : 1f - ((k - 0.62f) / 0.38f);
                        h.QueueRedraw();
                    });
                });

                FieldFx.FreeAfter(h, delay + life + 0.12f);
            }

            FieldFx.FreeAfter(root, 1.3f);
            Diag("escapespin", $"[脱身矢·乱弹] 甩出 {count} 枚旋转子弹", 6);
        }
        catch (Exception e)
        {
            LogOnce($"[脱身矢·乱弹] {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// 一颗<b>用手画出来的子弹</b>（局部 +X = 弹头方向）。
    ///
    /// <para>★ 为什么不用贴图：第一版直接拿 <c>escape_round.png</c> 做 <see cref="Sprite2D"/>，
    /// 缩到 50px 后<b>实机几乎看不见</b>（黄铜在暗色木地板上一片糊）。用户的原话是
    /// "你可以去参考一下[无限]的子弹是怎么飞的" ——
    /// <c>CardFieldVfx.InfinityWorld.cs</c> 里的 <c>DrawBead</c> 是<b>纯实心多边形</b>
    /// （弹壳身 / 底缘 / 抽壳槽 / 铜弹头 / 亮边），所以任何分辨率任何缩放下都锐利。
    /// 这里照抄同一套画法，只是把 <c>DrawSetTransform</c> 换成了节点自身的
    /// <see cref="Node2D.Rotation"/>/<see cref="Node2D.Scale"/>（它本来就是个节点，
    /// 不需要绝对变换那一套补偿）。</para>
    /// </summary>
    private sealed partial class SpinRound : Node2D
    {
        internal float Length = 52f;
        internal float Wide = 22f;
        internal float Alpha = 1f;
        internal bool Hot;

        public override void _Draw()
        {
            float a = Mathf.Clamp(Alpha, 0f, 1f);

            if (a <= 0.01f)
            {
                return;
            }

            float len = Length;
            float wide = Mathf.Max(5f, Wide);
            float half = len * 0.5f;
            float hw = wide * 0.5f;
            float tipLen = len * 0.34f;
            float caseLen = len - tipLen;

            // 外圈暖光：子弹头是"热"的，边缘一圈铜色散光才不像贴纸
            DrawCircle(Vector2.Zero, len * 0.60f, new Color(EscBrass, 0.16f * a));

            // 弹壳体
            DrawRect(new Rect2(-half, -hw, caseLen, wide), new Color(EscBrass, a));

            // 底缘（后端一圈更暗更宽 —— 没有它读不出"这是弹壳的屁股")
            DrawRect(
                new Rect2(-half, -hw * 1.24f, wide * 0.30f, wide * 1.24f),
                new Color(EscBrassDark, a * 0.95f));

            // 抽壳槽：一道暗线，金属感全靠它
            DrawLine(
                new Vector2(-half + (wide * 0.55f), -hw),
                new Vector2(-half + (wide * 0.55f), hw),
                new Color(EscSteelDark, a * 0.75f),
                Mathf.Max(1f, wide * 0.14f),
                true);

            // 弹头（银蓝钢）
            Vector2[] tip =
            {
                new(-half + caseLen, -hw),
                new(half, 0f),
                new(-half + caseLen, hw),
            };

            DrawColoredPolygon(tip, new Color(EscSteel, a));

            // 弹头高光（顺着上沿一条亮线）
            DrawLine(
                new Vector2(-half + caseLen, -hw * 0.52f),
                new Vector2(half - (wide * 0.12f), -hw * 0.06f),
                new Color(EscWhite, a * 0.85f),
                Mathf.Max(1f, wide * 0.15f),
                true);

            // 弹壳身高光
            DrawLine(
                new Vector2(-half + (wide * 0.20f), -hw * 0.62f),
                new Vector2(-half + caseLen, -hw * 0.62f),
                new Color(1f, 0.92f, 0.72f, a * 0.55f),
                Mathf.Max(1f, wide * 0.13f),
                true);

            if (Hot)
            {
                DrawCircle(Vector2.Zero, len * 0.28f, new Color(EscWhite, 0.28f * a));
            }
        }
    }

    /// <summary>
    /// 沿点序从 <paramref name="tail"/> 渐变到 <paramref name="head"/> 的
    /// <see cref="Gradient"/>。用来做"枪口透明 → 命中点最亮"的弹道余辉。
    /// </summary>
    private static Gradient TrailGradient(Color tail, Color head)
    {
        var g = new Gradient();
        g.SetColor(0, tail);
        g.SetColor(1, head);
        return g;
    }

    /// <summary>
    /// <b>斜向速度线</b>——直接照她那张技能图的构图：一组<b>平行斜线</b>整体掠过爆点。
    ///
    /// <para>和已有的"放射速度线"不重复，透视含义不同：
    /// 放射线是<b>从中心往外张</b>（= 向外炸），平行斜线是<b>整体横向掠过</b>（= 极快地过眼）。
    /// 技能图上画的正好是后者。</para>
    /// </summary>
    private static void SpawnSlashLines(Node2D parent, Vector2 dir)
    {
        // 与弹道夹角约 58°，这样斜线会从爆点左右两侧"扫"过去
        float baseAngle = dir.Angle() - (Mathf.Pi * 0.32f);
        Vector2 along = new(Mathf.Cos(baseAngle), Mathf.Sin(baseAngle));
        Vector2 across = new(-along.Y, along.X);

        for (int i = 0; i < 7; i++)
        {
            float off = (i - 3f) * 48f;
            float len = 250f + (GD.Randf() * 230f);
            Vector2 c = across * off;

            Line2D slash = FieldFx.Line(
                parent,
                new Vector2[] { c - (along * len), c + (along * len) },
                new Color(i % 2 == 0 ? EscWhite : EscCrimson, 1f),
                1.6f + (GD.Randf() * 2.6f));

            slash.Modulate = new Color(1f, 1f, 1f, 0f);

            float delay = GD.Randf() * 0.05f;
            Vector2 drift = along * (-260f);

            FieldFx.After(parent, delay, "[脱身矢·速度线]", () =>
            {
                FieldFx.Animate(slash, 0.20f, k =>
                {
                    float a = k < 0.22f ? k / 0.22f : 1f - FieldFxEase.InCubic((k - 0.22f) / 0.78f);
                    slash.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(a * 0.85f, 0f, 1f));
                    slash.Position = drift * FieldFxEase.OutCubic(k);
                });
            });
        }
    }

    /// <summary>
    /// 一层星芒：<see cref="FieldFx.StarVaried"/> 给长短不一，<see cref="FieldFx.SetStarGradient"/>
    /// 让尖端 alpha 归零（★ 设了顶点色之后 <c>Polygon2D.Color</c> 失效，脉动只能走 <c>Modulate</c>）。
    /// </summary>
    /// <summary>
    /// ★★ <b>色散星芒</b>——把<b>色散做进爆点自己身上</b>，而不是只靠全屏那一层。
    ///
    /// <para><b>为什么非做不可</b>：全屏色散读的是屏幕纹理，如果那片区域本身没有高对比的边缘
    /// （暗紫地板、木纹），就析不出颜色 —— 实机反馈「命中敌人的特效没有色散」就是这个原因。
    /// 长在星芒上的色散<b>不依赖背景</b>，任何时候都看得见。</para>
    ///
    /// <para><b>做法</b>：同一颗星用<b>红 / 青 / 白</b>三份叠画，红青各自沿法线错开几个像素，
    /// 并且<b>随寿命继续拉开</b> —— 每一根尖都自带一道红边和一道青边，越往后分得越开。
    /// 三份必须用<b>同一个随机种子</b>，否则三颗星形状各不相同，叠起来是乱麻而不是色散。</para>
    /// </summary>
    private static void AddChromaticStar(
        Node2D parent,
        float angle,
        float radius,
        int points,
        float inner,
        float life,
        float delay,
        float off)
    {
        FieldFx.After(parent, delay, "[脱身矢·色散星]", () =>
        {
            var rng = new Random(((int)(radius * 977f)) ^ 0x5f3a);
            Vector2[] pts = FieldFx.StarVaried(radius, points, inner, rng, 0.30f, Mathf.RadToDeg(angle));

            Vector2 u = new(Mathf.Cos(angle), Mathf.Sin(angle));
            Vector2 n = new(-u.Y, u.X);

            Color[] tint =
            {
                new(1f, 0.14f, 0.20f),
                new(0.14f, 0.90f, 1f),
                Colors.White,
            };

            Vector2[] shift =
            {
                (-n * off) - (u * (off * 0.45f)),
                (n * off) + (u * (off * 0.45f)),
                Vector2.Zero,
            };

            float[] sc = { 1.0f, 1.0f, 0.82f };

            for (int i = 0; i < 3; i++)
            {
                Polygon2D p = FieldFx.Poly(parent, pts, Colors.White);
                FieldFx.SetStarGradient(p, tint[i], new Color(tint[i], 0f));
                p.Position = shift[i];
                p.Rotation = angle;
                p.Scale = Vector2.One * 0.14f;

                Polygon2D pp = p;
                int idx = i;

                FieldFx.Animate(pp, life, k =>
                {
                    if (!GodotObject.IsInstanceValid(pp))
                    {
                        return;
                    }

                    float e = FieldFxEase.OutExpo(Mathf.Clamp(k / 0.28f, 0f, 1f));
                    pp.Scale = Vector2.One * (Mathf.Lerp(0.14f, 1f, e) * sc[idx]);
                    pp.Position = shift[idx] * (1f + (k * 0.65f));
                    pp.Modulate = new Color(1f, 1f, 1f, 1f - FieldFxEase.InCubic(k));
                });
            }
        });
    }

    /// <summary>
    /// <b>彩虹能量环</b>（<see cref="FieldFxShaders.Spectrum"/>）：色相沿圆周铺满一整圈。
    ///
    /// <para>一整个爆点里只有白和红，再怎么调都"单调"；这一道环负责<b>在同一帧里给出全部色相</b>。
    /// 饱和度只给 0.62、环宽收到很细 —— 细彩环读作"能量边缘的衍射"，宽了就成了彩虹旗。</para>
    /// </summary>
    private static void SpawnSpectrumRing(Node2D parent, float size, float life, float delay, float spin)
    {
        FieldFx.After(parent, delay, "[脱身矢·彩虹环]", () =>
        {
            ColorRect q = FieldFx.ShaderQuad(parent, FieldFxShaders.Spectrum, size);

            FieldFx.SetParam(q, "radius", 0.10f);
            FieldFx.SetParam(q, "width", 0.020f);
            FieldFx.SetParam(q, "sat", 0.34f);
            FieldFx.SetParam(q, "glow_amt", 0.22f);
            FieldFx.SetParam(q, "alpha", 0f);
            FieldFx.SetParam(q, "spin", 0f);

            FieldFx.Animate(q, life, k =>
            {
                if (!GodotObject.IsInstanceValid(q))
                {
                    return;
                }

                float e = FieldFxEase.OutCubic(k);
                FieldFx.SetParam(q, "radius", Mathf.Lerp(0.09f, 0.80f, e));
                FieldFx.SetParam(q, "width", Mathf.Lerp(0.020f, 0.002f, e));
                FieldFx.SetParam(q, "alpha", 0.62f * (1f - FieldFxEase.InCubic(k)));
                FieldFx.SetParam(q, "spin", spin * k);
            });

            FieldFx.FreeAfter(q, life + 0.08f);
        });
    }

    /// <summary>
    /// <b>碎玻璃</b>：细长的锐角碎片，向外飞 + 自转 + 受重力下坠，加色发光。
    ///
    /// <para>和 <see cref="DebrisShards"/>（钝角小块、不发光）是两种东西：
    /// 那些是"被打碎的东西"，这些是"能量结晶出来的锐片" —— 一轮爆点里同时有两种碎屑，
    /// 才不像只有一种粒子在飞。</para>
    /// </summary>
    private static void GlassShards(Node parent, Color tint, int count, float speedMin, float speedMax, float len, float life)
    {
        for (int i = 0; i < count; i++)
        {
            float ang = GD.Randf() * Mathf.Tau;
            float spd = Mathf.Lerp(speedMin, speedMax, GD.Randf());
            Vector2 vel = new(Mathf.Cos(ang), Mathf.Sin(ang));

            float w = len * Mathf.Lerp(0.55f, 1.15f, GD.Randf());
            Vector2[] blade =
            {
                new(0f, -w),
                new(w * 0.30f, w * 0.55f),
                new(0f, w * 0.22f),
                new(-w * 0.30f, w * 0.55f),
            };

            Polygon2D piece = FieldFx.Poly(parent, blade, tint);
            float startRot = GD.Randf() * Mathf.Tau;
            float spin = Mathf.DegToRad(Mathf.Lerp(-900f, 900f, GD.Randf()));
            float l = life * Mathf.Lerp(0.72f, 1.2f, GD.Randf());
            piece.Rotation = startRot;

            FieldFx.Animate(piece, l, k =>
            {
                if (!GodotObject.IsInstanceValid(piece))
                {
                    return;
                }

                float t = k * l;
                piece.Position = (vel * spd * t) + new Vector2(0f, 0.5f * 820f * t * t);
                piece.Rotation = startRot + (spin * t);
                piece.Scale = Vector2.One * Mathf.Lerp(1.15f, 0.55f, k);
                piece.Modulate = new Color(1f, 1f, 1f, 1f - FieldFxEase.InCubic(k));
            });
        }
    }

    /// <summary>一道<b>细长针状尖刺</b>（锥形三角），顺着射线方向射出去。与 Line2D 速度线不同：
    /// 它有宽度渐变、会自转、会缩，读作"固体的尖"而不是"光条"。</summary>
    private static void NeedleSpikes(Node2D parent, float baseAngle, Color tint, int count, float len, float life)
    {
        for (int i = 0; i < count; i++)
        {
            float a = baseAngle + (((i - ((count - 1) * 0.5f)) * 0.30f)) + ((GD.Randf() - 0.5f) * 0.14f);
            Vector2 d = new(Mathf.Cos(a), Mathf.Sin(a));

            float L = len * Mathf.Lerp(0.65f, 1.35f, GD.Randf());
            float w = L * 0.055f;

            Vector2[] needle =
            {
                new(0f, -w),
                new(L, 0f),
                new(0f, w),
            };

            Polygon2D spike = FieldFx.Poly(parent, needle, tint);
            spike.Rotation = a;
            spike.Scale = Vector2.One * 0.4f;

            FieldFx.Animate(spike, life, k =>
            {
                if (!GodotObject.IsInstanceValid(spike))
                {
                    return;
                }

                float e = FieldFxEase.OutExpo(k);
                spike.Scale = Vector2.One * Mathf.Lerp(0.4f, 1.35f, e);
                spike.Position = d * (L * 0.55f * e);
                spike.Modulate = new Color(1f, 1f, 1f, 1f - FieldFxEase.InCubic(k));
            });
        }
    }

    private static void AddStarLayer(
        Node2D parent,
        float angle,
        float radius,
        int points,
        float inner,
        float growTime,
        float life,
        float delay,
        float spin,
        Color core,
        Color tip)
    {
        FieldFx.After(parent, delay, "[脱身矢·星芒]", () =>
        {
            var rng = new Random((int)(angle * 4096f) ^ (int)radius ^ ((int)parent.GetInstanceId() & 0x3ff));
            Vector2[] pts = FieldFx.StarVaried(radius, points, inner, rng, 0.30f, Mathf.RadToDeg(angle));

            Polygon2D poly = FieldFx.Poly(parent, pts, Colors.White);
            FieldFx.SetStarGradient(poly, core, tip);

            poly.Scale = Vector2.One * 0.16f;
            poly.Rotation = angle;

            FieldFx.Animate(poly, life, k =>
            {
                float e = FieldFxEase.OutExpo(Mathf.Clamp(k / growTime, 0f, 1f));
                poly.Scale = Vector2.One * Mathf.Lerp(0.16f, 1f, e);
                poly.Rotation = angle + (spin * k);
                poly.Modulate = new Color(1f, 1f, 1f, 1f - FieldFxEase.InCubic(k));
            });
        });
    }

    /// <summary>
    /// 一道<b>着色器冲击环</b>（<see cref="FieldFxShaders.Ring"/>）。半径/环宽/alpha 同时随
    /// <c>OutExpo</c> 展开 —— 前 20% 冲出去、后面慢慢淡，这才是冲击波；
    /// 圆半径和 alpha 用同一根曲线的话会读作"一个圈在缩小"。
    /// </summary>
    private static void SpawnShaderRing(
        Node2D parent,
        Color inner,
        Color outer,
        float size,
        float r0,
        float r1,
        float w0,
        float w1,
        float life,
        float delay,
        float glow,
        float halo,
        float gap,
        float radius2)
    {
        FieldFx.After(parent, delay, "[脱身矢·冲击环]", () =>
        {
            ColorRect q = FieldFx.ShaderQuad(parent, FieldFxShaders.Ring, size);
            FieldFx.SetParam(q, "tint", inner);
            FieldFx.SetParam(q, "tint2", outer);
            FieldFx.SetParam(q, "radius", r0);
            FieldFx.SetParam(q, "width", w0);
            FieldFx.SetParam(q, "glow_amt", glow);
            FieldFx.SetParam(q, "halo_amt", halo);
            FieldFx.SetParam(q, "vary", 0.55f);
            FieldFx.SetParam(q, "gap_amt", gap);
            FieldFx.SetParam(q, "radius2", radius2);
            FieldFx.SetParam(q, "width2", 0.008f);
            FieldFx.SetParam(q, "phase", GD.Randf() * 6.2f);
            FieldFx.SetParam(q, "alpha", 1f);

            FieldFx.Animate(q, life, k =>
            {
                float e = FieldFxEase.OutExpo(k);
                FieldFx.SetParam(q, "radius", Mathf.Lerp(r0, r1, e));
                FieldFx.SetParam(q, "width", Mathf.Lerp(w0, w1, e));
                FieldFx.SetParam(q, "alpha", 1f - FieldFxEase.InCubic(k));
            });

            FieldFx.FreeAfter(q, life + 0.08);
        });
    }

    /// <summary>
    /// <b>贯穿</b>：箭的目标是"穿过去"，所以爆点后面还有一处小出口。
    /// <para>没有出口时，观感是"打在盾上"；有了出口 + 一条白热贯穿线，
    /// 才读得出"从这边进去、从那边出来"。</para>
    /// </summary>
    private static void SpawnPierceExit(Control layer, Vector2 at, Vector2 u)
    {
        Vector2 exit = at + (u * 92f);
        Node2D root = FieldFx.Root(layer, exit, "RayModEscapeExit");
        root.ZIndex = 7;

        // 贯穿线：从入口往里 70px、到出口再外 46px，先抽长再淡掉
        Line2D line = FieldFx.Line(
            root,
            new Vector2[] { -u * 70f, u * 46f },
            new Color(EscWhite, 0.92f),
            3.2f);

        Line2D lineGlow = FieldFx.Line(
            root,
            new Vector2[] { -u * 70f, u * 46f },
            new Color(EscIce, 0.42f),
            9.0f);

        FieldFx.Animate(line, 0.26, k =>
        {
            line.Modulate = new Color(1f, 1f, 1f, 1f - FieldFxEase.InCubic(k));
        });

        FieldFx.Animate(lineGlow, 0.30, k =>
        {
            lineGlow.Modulate = new Color(1f, 1f, 1f, (1f - k) * 0.8f);
        });

        // 出口小爆：冷色，尺寸只有主爆的三分之一
        ColorRect flash = FieldFx.ShaderQuad(root, FieldFxShaders.Flash, 300f);
        FieldFx.SetParam(flash, "tint", EscIce);
        FieldFx.SetParam(flash, "alpha", 0f);
        FieldFx.SetParam(flash, "falloff", 2.1f);

        FieldFx.Animate(flash, 0.26, k =>
        {
            FieldFx.SetParam(flash, "alpha", 0.85f * (1f - FieldFxEase.InCubic(k)));
            flash.Scale = Vector2.One * Mathf.Lerp(0.30f, 1.05f, FieldFxEase.OutExpo(k));
        });

        GpuParticles2D jets = FieldFx.Sparks(
            root,
            EscIce,
            amount: 12,
            speedMin: 120f,
            speedMax: 320f,
            lifetime: 0.26f,
            gravity: 360f,
            scaleMin: 0.08f,
            scaleMax: 0.22f);

        jets.Rotation = u.Angle() - (Mathf.Pi * 0.5f);
        FieldFx.Ignite(jets);

        FieldFx.FreeAfter(root, 0.55);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ③ 残影 / 冲击环 通用小件
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 弹头的一格运动残影：复制一份贴图、染成冷白、加色叠在弹道后面淡出。
    /// <para>必须<b>先 AddChild 再设 GlobalPosition</b>（<c>FieldFx.Root</c> 的注释里
    /// 记过的同一个坑）—— 否则残影会全挤在画布左上角。</para>
    /// </summary>
    private static void SpawnRoundGhost(Node2D host, Texture2D tex, Vector2 globalPos, float rot, float scaleK, float alpha)
    {
        if (!GodotObject.IsInstanceValid(host))
        {
            return;
        }

        var ghost = new Sprite2D
        {
            Name = "RoundGhost",
            Centered = true,
            Texture = tex,
            Rotation = rot,
            Scale = Vector2.One * scaleK,
            Material = FieldFx.Additive,
        };

        host.AddChild(ghost);
        ghost.GlobalPosition = globalPos;
        ghost.Modulate = new Color(EscIce.R, EscIce.G, EscIce.B, alpha);

        FieldFx.Animate(ghost, 0.20f, k =>
        {
            ghost.Scale = Vector2.One * (scaleK * Mathf.Lerp(1f, 0.82f, k));
            ghost.Modulate = new Color(EscIce.R, EscIce.G, EscIce.B, alpha * (1f - FieldFxEase.InCubic(k)));
        });

        FieldFx.FreeAfter(ghost, 0.24f);
    }

    /// <summary>一圈向外炸开的冲击波环（椭圆的，压扁一点读作"地面上的"）。</summary>
    // ══════════════════════════════════════════════════════════════════════
    //  ①.5 她本人的「卡牌式线框残影」
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 从<b>当前帧的画面里</b>把她那一块抠下来，再用描边 shader 画成线框残影。
    /// <para>为什么不用克隆节点：她的 <c>NCreatureVisuals</c> 是 spine 骨骼动画，
    /// 整个子树里没有 <c>Sprite2D</c>/<c>AnimatedSprite2D</c> 可以复制（日志里那张
    /// 类型表就是证据）。卡牌的残影之所以能"描边"，是因为卡是矩形、四条线就能画；
    /// 她的剪影没法手写。</para>
    /// <para>所以改成截屏取影：Godot 里 <c>Viewport.GetTexture().GetImage()</c> 拿到的是
    /// 上一帧的最终画面，裁出她所在的矩形，再交给描边 shader 只留轮廓
    /// —— 效果上就是"她这个人被描了一圈发光的线，然后留在原地"。</para>
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<ulong, ulong> _escapeGhostMs = new();

    private static Shader? _edgeShader;

    private static int _capViewW;
    private static int _capViewH;
    private static string _capDbg = "";
    private static bool _capDumped;

    /// <summary>截图像素空间相对特效画布空间的常量偏移（本机 2560x1440 实测）。</summary>
    private static readonly Vector2 CapOffset = new(130f, 16f);

    /// <summary>描边 shader：按 alpha 的邻域最大值减自身得到 1~2px 的轮廓环。</summary>
    private static Shader EdgeShader()
    {
        if (_edgeShader != null)
        {
            return _edgeShader;
        }

        // 附在源码里而不是做成 .gdshader 资源：pck 导入链在本工程里已经坑过两次
        //（"Asset not cached"），一个几十行的 shader 没必要再走资源管线。
        // ★ 截屏取影的三个坑（都是实机撞出来的）：
        //   ① 截图**不透明**，alpha 恒为 1 —— 按 alpha 求轮廓只会得到全 0；
        //   ② 轮廓只能从**亮度梯度**求，再用饱和度/亮度掩膜滤掉背景的树枝与地砖；
        //   ③ 这个 Godot 版本的 `TEXTURE` 内建**在自定义函数里不可见**
        //      （报 `SHADER ERROR: Unknown identifier in expression: 'TEXTURE'`），
        //      必须自己声明一个 sampler 并把贴图塞进去。
        _edgeShader = new Shader
        {
            Code = @"
shader_type canvas_item;
render_mode blend_add;

uniform sampler2D src_tex : filter_linear, repeat_disable;
uniform vec4 line_color : source_color = vec4(0.72, 0.94, 1.0, 1.0);
uniform float thickness = 2.0;
uniform float fill = 0.30;
uniform float edge_gain = 3.6;

float luma(vec2 uv) {
    vec3 c = texture(src_tex, uv).rgb;
    return dot(c, vec3(0.299, 0.587, 0.114));
}

void fragment() {
    vec2 px = (1.0 / vec2(textureSize(src_tex, 0))) * thickness;

    float l0 = luma(UV);
    float lx = luma(UV + vec2(px.x, 0.0)) - luma(UV - vec2(px.x, 0.0));
    float ly = luma(UV + vec2(0.0, px.y)) - luma(UV - vec2(0.0, px.y));
    float edge = clamp(length(vec2(lx, ly)) * edge_gain, 0.0, 1.0);

    vec3 c0 = texture(src_tex, UV).rgb;
    float sat = max(c0.r, max(c0.g, c0.b)) - min(c0.r, min(c0.g, c0.b));

    // 她在这一帧里是「亮 + 高饱和」（粉发金甲），背景是暗蓝灰 ⇒ 这个掩膜能把背景滤掉
    float mask = clamp(sat * 2.4 + l0 * 1.3 - 0.22, 0.0, 1.0);

    float a = (edge * mask) + (l0 * fill * mask);
    COLOR = vec4(line_color.rgb * COLOR.rgb, a * COLOR.a);
}
",
        };

        return _edgeShader;
    }

    /// <summary>
    /// 截当前帧、裁出 <paramref name="center"/>（<b>画布坐标</b>）附近的一块，返回贴图。
    /// <para>★ 第四坑：特效坐标是画布坐标，<c>Viewport.GetImage()</c> 给的是<b>视口像素</b>，
    /// 两者之间还差一个 <c>GetCanvasTransform()</c>（游戏按窗口大小整体缩放）。
    /// 直接把画布坐标当像素去裁，裁到的是画面别处 —— 残影就永远"看不见"。</para>
    /// </summary>
    private static Image? CaptureImage(Control layer, Vector2 center, Vector2 size, out float canvasScale)
    {
        canvasScale = 1f;

        try
        {
            Viewport? vp = layer.GetViewport();

            Image? full = vp?.GetTexture()?.GetImage();

            if (full == null || full.IsEmpty())
            {
                return null;
            }

            _capViewW = full.GetWidth();
            _capViewH = full.GetHeight();

            Transform2D ct = layer.GetCanvasTransform();
            Vector2 vpCenter = ct * center;
            Vector2 cs = ct.Scale;
            canvasScale = Mathf.Max(0.05f, Mathf.Abs(cs.X));

            Vector2 vpSize = size * canvasScale;

            int x = (int)Mathf.Round(vpCenter.X - (vpSize.X * 0.5f));
            int y = (int)Mathf.Round(vpCenter.Y - (vpSize.Y * 0.5f));

            int x0 = Mathf.Max(0, x);
            int y0 = Mathf.Max(0, y);
            int x1 = Mathf.Min(full.GetWidth(), x + (int)Mathf.Round(vpSize.X));
            int y1 = Mathf.Min(full.GetHeight(), y + (int)Mathf.Round(vpSize.Y));

            _capDbg = $"画布中心={center} → 像素中心={vpCenter} 画布缩放={canvasScale:F3} 裁=({x0},{y0})-({x1},{y1})";

            if (x1 - x0 < 8 || y1 - y0 < 8)
            {
                return null;
            }

            Image? cropped = full.GetRegion(new Rect2I(x0, y0, x1 - x0, y1 - y0));

            // 调试：把抠下来的那块存盘，人眼确认裁的位置对不对（只存一次）
            if (cropped != null && !cropped.IsEmpty() && !_capDumped)
            {
                _capDumped = true;

                try
                {
                    cropped.SavePng(@"D:\study\sts2mod\_tmp\cap_dbg.png");
                    full.SavePng(@"D:\study\sts2mod\_tmp\cap_full.png");
                }
                catch
                {
                    // 存不下来不影响演出
                }
            }

            return cropped == null || cropped.IsEmpty() ? null : cropped;
        }
        catch (Exception ex)
        {
            Diag("escapecap", $"[脱身矢·截影] 失败：{ex.GetType().Name}", 4);
            return null;
        }
    }

    /// <summary>在她<b>还没滑走</b>的那一帧截一块图，沿后滑方向留 3 层线框残影。</summary>
    internal static void SpawnCapturedGhosts(object? dealer, Vector2 away, Control layer)
    {
        if (dealer == null || away.LengthSquared() < 0.01f)
        {
            return;
        }

        try
        {
            Node? raw = CreatureNodeOf(dealer) as Node;

            if (raw == null || !GodotObject.IsInstanceValid(raw))
            {
                return;
            }

            ulong id = raw.GetInstanceId();
            ulong now = Time.GetTicksMsec();

            if (_escapeGhostMs.TryGetValue(id, out ulong last) && (now - last) < 600UL)
            {
                return;
            }

            _escapeGhostMs[id] = now;

            // ★ 第五坑：截图的像素空间和特效画布空间**差一个常量偏移**（本机实测 +130/+16）：
            //   按 ChestPos 直接去裁，她永远落在裁剪框的右下角（dump 出来的 cap_dbg.png 就是证据）。
            //   所以：裁剪用「ChestPos + CapOffset」，而残影本体仍然摆在 ChestPos —— 
            //   那个偏移只属于截图，不属于画布。
            Vector2 chest = ChestPos(dealer);

            if (chest == Vector2.Zero)
            {
                return;
            }

            const float bodyW = 340f;
            const float bodyH = 520f;

            Vector2 box = new(bodyW, bodyH);
            // 残影就画在「截图中心」上：贴图里的她本来就在框中央，画在同一个点才能严丝合缝地
            // 盖住她原来的位置（偏移只属于截图坐标系，两边用同一个值即对齐）。
            Vector2 center = chest + CapOffset;
            Vector2 capCenter = center;

            Image? shot = CaptureImage(layer, capCenter, box, out float canvasScale);

            if (shot == null)
            {
                Diag("escapecap", "[脱身矢·截影] 拿不到画面（Viewport/Image 为空）", 4);
                return;
            }

            ImageTexture? lineArt = MakeLineArt(shot, out Vector2 bodyCentroid);

            if (lineArt == null)
            {
                Diag("escapecap", "[脱身矢·截影] 线稿生成失败", 4);
                return;
            }

            Diag("escapecap", $"[脱身矢·截影] 抠出 {shot.GetWidth()}x{shot.GetHeight()}（视口 {_capViewW}x{_capViewH}，{_capDbg}），线稿就绪", 4);

            Vector2 awayN = away.Normalized();

            // ── 残影：把「她本来站的那一块画面」抠出来，用描边材质在她原位留影。
            //    宿主是战斗界面根节点（见 GhostHost 的注释：战斗特效层在出牌层下面，会被卡面盖住）。
            Node2D host = GhostHost();

            if (host == null)
            {
                Diag("escapecap", "[脱身矢·截影] 拿不到残影宿主（NCombatRoom.Ui 为 null）", 4);
                return;
            }

            Vector2 texShift = new((shot.GetWidth() * 0.5f) - bodyCentroid.X, (shot.GetHeight() * 0.5f) - bodyCentroid.Y);
            Diag("escapealign", $"[脱身矢·残影] 质心=({bodyCentroid.X:F0},{bodyCentroid.Y:F0}) 贴图={shot.GetWidth()}x{shot.GetHeight()} 偏移={texShift}", 4);
            // ── 「残」= 沿她后滑的那条线**连续撒影**，而不是在原地放一两个影子。
            //    她 0.16s 滑完 205px；每 1/8 路程取一个采样点，在「她正好经过那一刻」把这个影子放下，
            //    影子的寿命比采样间隔长，于是同一时刻画面里总有 4~5 个逐渐变淡的她连成一条线。
            // ── 「残」= 沿她后滑的轨迹留下**几层**越来越淡的她。
            //    注意不能多：线稿本身很密，两层叠在一起就会糊成一团白（实测 9 层直接变光球），
            //    所以只取 3 个采样点、间距 100px、alpha 逐层砍半。
            // 4 层：位置沿后滑线铺开，越远越扁（横向压缩 = 速度感）、越淡。
            // 她的身体本身有 ~200px 宽，而这次只滑 205px ⇒ 层与层的间距必须**拉开到 1.6 倍**
            // 才读得出"一叠人"（间距小于半身宽时会糊成一团，看起来仍然只有一个）。
            // ★ 关键：她只滑 205px，而她本体将近 300px 宽 ⇒ 如果按 1:1 铺，四层会完全叠在一起
            //   （看上去还是"一个影子"）。所以间距放大到 1.55 倍（尾迹会伸到起点之后），
            //   同时把每一层横向压窄，才会读成"一串越来越淡的她"。
            // ★ 要"残"就得是一串：每 30px 一层、共 7 层，层层淡化并各自延后消失。
            //   她只滑 205px，所以尾迹整体略长于滑行距离（视觉上读作"甩出去的一条影"）。
            // ★ 叠 7 层加色会糊成一团白（实测），所以：层数少、间距大、**不用加色**、
            //   每层本来就淡 —— 只有这样才能读成"几个她连成一条线"。
            const int ghostCount = 3;
            const float stepPx = 74f;

            for (int i = 0; i < ghostCount; i++)
            {
                float f = i / (float)(ghostCount - 1);

                // 位置：沿她后滑的方向铺开（f=0 在她起跳点，f=1 在最远处）
                Vector2 at = chest + (awayN * (stepPx * i)) + new Vector2(0f, SlideDrop * f * 0.6f);

                // ★ alpha 必须**随后滑进度递增**，而不是递减。
                //   梯子是按"时间"撒的：f=0 那层是第 0 秒撒的（最老），f=1 那层是最后一个撒的（最新）。
                //   残影的读法是"越新越实、越老越淡"，所以 0.14 → 0.52 才是对的。
                //   第一版写反了（0.52 → 0.13），结果是**最老的影子最亮**：
                //   三层几乎完全重叠（间距 74px 而她本体近 300px 宽），
                //   叠合后的画面被那层 0.52 的老影子主导，看上去就像"旧的压在新的上面"——
                //   其实层序没反（Godot 里同级 ZIndex 按树序画，后加的在上面），
                //   是**不透明度反了**，肉眼读到的"前后"由亮度决定，不由 z 决定。
                float alpha = Mathf.Lerp(0.14f, 0.52f, f);
                float life = Mathf.Lerp(0.26f, 0.16f, f);

                SpawnOneGhost(host, lineArt, at, canvasScale, awayN, f * 0.11f, life, 1.0f * f, alpha, texShift, 1f + (f * 0.90f), i);
            }

            Diag("escapeghost", $"[脱身矢·残影] 撒下 {ghostCount} 层残影（每层 {stepPx:F0}px）", 4);

            // ── 「闪」：她离开的那一帧，原位留一个**纯黑剪影**。
            //    用户点名要的就是这一下（"我说的黑色是角色一瞬间的闪身"）——
            //    不是半透明的她，是**整个人变成一块黑**，然后立刻消失。
            SpawnBlackFlash(host, lineArt, chest, canvasScale, texShift, awayN);
        }        catch (Exception ex)
        {
            Diag("escapecap", $"[脱身矢·截影] 异常：{ex.GetType().Name}", 4);
        }
    }

    /// <summary>
    /// 留一层「她的线框残影」：贴图是命中那一瞬从画面上抠下来的她本人，
    /// 材质只留轮廓（<c>EdgeShader</c>），所以看起来就是"她留下的影子"。
    /// </summary>
    /// <summary>
    /// 在 CPU 上把截下来的那一块画面变成「线稿」：只保留她身上的边缘（亮度梯度 × 饱和度/亮度掩膜），
    /// 颜色统一成冰蓝。相比着色器版本，这条路**稳定可预期** —— 结果能直接存成 PNG 肉眼验。
    /// </summary>
    private static ImageTexture? MakeLineArt(Image src, out Vector2 centroid)
    {
        try
        {
            // ★ 截出来的图常常是 RGB8（3 字节/像素），不转 RGBA8 就按 i*4 取会直接越界。
            if (src.GetFormat() != Image.Format.Rgba8)
            {
                src.Convert(Image.Format.Rgba8);
            }

            centroid = Vector2.Zero;
            int w = src.GetWidth();
            int h = src.GetHeight();
            byte[] data = src.GetData();
            int n = w * h;

            var luma = new float[n];
            var seed = new float[n];

            // ★ 掩膜的关键：**别去猜「背景长什么样」，只认「她身上有什么」。**
            //   她身上三种可辨识的颜色：粉发、金黄枪身、亮白脸/手套；
            //   背景（暗紫地板、棕色树枝、紫砖）都够不到这三条线。
            //   再乘一个椭圆先验（她在框的中下部），把左上角那根树枝彻底切掉。
            for (int i = 0; i < n; i++)
            {
                float r = data[(i * 4) + 0] / 255f;
                float g = data[(i * 4) + 1] / 255f;
                float b = data[(i * 4) + 2] / 255f;
                float l = (0.299f * r) + (0.587f * g) + (0.114f * b);
                float mx = Mathf.Max(r, Mathf.Max(g, b));
                float mn = Mathf.Min(r, Mathf.Min(g, b));
                float d = mx - mn;
                float sat = mx > 1e-5f ? d / mx : 0f;
                float hue = 0f;

                if (d > 1e-5f)
                {
                    if (mx == r)
                    {
                        hue = 60f * (((g - b) / d) % 6f);
                    }
                    else if (mx == g)
                    {
                        hue = 60f * (((b - r) / d) + 2f);
                    }
                    else
                    {
                        hue = 60f * (((r - g) / d) + 4f);
                    }
                }

                if (hue < 0f)
                {
                    hue += 360f;
                }

                luma[i] = l;
                bool pink = (hue > 295f || hue < 24f) && sat > 0.25f && mx > 0.34f;
                bool bright = mx > 0.52f;
                seed[i] = pink || bright ? 1f : 0f;
            }

            // 椭圆先验：她在框的中下部；左上角那根树枝不在这里面
            int ww = w;
            int hh = h;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w) + x;

                    if (seed[i] <= 0f)
                    {
                        continue;
                    }

                    float ex = (x - (ww * 0.50f)) / (ww * 0.40f);
                    float ey = (y - (hh * 0.62f)) / (hh * 0.42f);

                    if ((ex * ex) + (ey * ey) > 1f)
                    {
                        seed[i] = 0f;
                    }
                }
            }

            // 方框模糊（半径 3）：把碎点糊成一个连续剪影，代替高斯
            var mask = BoxBlur(seed, w, h, 3);

            var edge = new float[n];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w) + x;
                    int xa = Mathf.Max(0, x - 1);
                    int xb = Mathf.Min(w - 1, x + 1);
                    int ya = Mathf.Max(0, y - 1);
                    int yb = Mathf.Min(h - 1, y + 1);

                    float dx = luma[(y * w) + xb] - luma[(y * w) + xa];
                    float dy = luma[(yb * w) + x] - luma[(ya * w) + x];
                    float g2 = Mathf.Sqrt((dx * dx) + (dy * dy));
                    edge[i] = Mathf.Clamp((g2 - 0.045f) * 9.0f, 0f, 1f);
                }
            }

            var outBytes = new byte[n * 4];
            float cr = 0.74f;
            float cg = 0.95f;
            float cb = 1.00f;

            for (int i = 0; i < n; i++)
            {
                // 残影 = 她的剪影被「冷到冰蓝 + 提亮」，不是网格线稿。
                // 线稿版试过：密密麻麻的描边在游戏里读起来像蓝色的机械模型，不像残影。
                // ★ 颜色必须是**统一的冰蓝**，不能带她身上的明暗。
                //   带明暗时她的枪/外套是黑的，几层黑叠在一起 ⇒ 读成一坨黑泥（实测就是这样）。
                //   统一色 + 只让 alpha 随掩膜变化，叠起来才是"一层层变淡的雾人"。
                float lift = 1f + (edge[i] * 0.55f);
                float a = (0.55f * mask[i]) + (edge[i] * (0.25f + (0.90f * mask[i])));
                a = Mathf.Clamp((a - 0.10f) / 0.90f, 0f, 1f) * 1.45f;
                a = Mathf.Clamp(a, 0f, 1f);

                outBytes[(i * 4) + 0] = (byte)Mathf.RoundToInt(Mathf.Clamp(cr * lift, 0f, 1f) * 255f);
                outBytes[(i * 4) + 1] = (byte)Mathf.RoundToInt(Mathf.Clamp(cg * lift, 0f, 1f) * 255f);
                outBytes[(i * 4) + 2] = (byte)Mathf.RoundToInt(Mathf.Clamp(cb * lift, 0f, 1f) * 255f);
                outBytes[(i * 4) + 3] = (byte)Mathf.RoundToInt(a * 255f);
            }

            var img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, outBytes);

            if (!_lineArtDumped && Engine.GetMainLoop() is SceneTree)
            {
                _lineArtDumped = true;
                Error err = img.SavePng("D:/study/sts2mod/_tmp/lineart_dbg.png");

                if (err != Error.Ok)
                {
                    Diag("escapeghost", $"[脱身矢·残影] 线稿落盘失败：{err}", 3);
                }
            }

            double sw = 0;
            double sx = 0;
            double sy = 0;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w) + x;
                    float a = outBytes[(i * 4) + 3] / 255f;

                    if (a < 0.05f)
                    {
                        continue;
                    }

                    sw += a;
                    sx += x * a;
                    sy += y * a;
                }
            }

            if (sw > 1.0)
            {
                centroid = new Vector2((float)(sx / sw), (float)(sy / sw));
            }

            return ImageTexture.CreateFromImage(img);
        }
        catch (Exception ex)
        {
            Diag("escapeghost", $"[脱身矢·残影] 线稿生成异常：{ex.GetType().Name}", 3);
            centroid = Vector2.Zero;
            return null;
        }
    }

    private static bool _lineArtDumped;

    /// <summary>可分离方框模糊：把碎点糊成连续剪影（两趟，每趟 O(n)）。</summary>
    private static float[] BoxBlur(float[] src, int w, int h, int radius)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        int span = (radius * 2) + 1;

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            float sum = 0f;

            for (int k = -radius; k <= radius; k++)
            {
                sum += src[row + Mathf.Clamp(k, 0, w - 1)];
            }

            for (int x = 0; x < w; x++)
            {
                tmp[row + x] = sum / span;
                sum -= src[row + Mathf.Clamp(x - radius, 0, w - 1)];
                sum += src[row + Mathf.Clamp(x + radius + 1, 0, w - 1)];
            }
        }

        for (int x = 0; x < w; x++)
        {
            float sum = 0f;

            for (int k = -radius; k <= radius; k++)
            {
                sum += tmp[(Mathf.Clamp(k, 0, h - 1) * w) + x];
            }

            for (int y = 0; y < h; y++)
            {
                dst[(y * w) + x] = sum / span;
                sum -= tmp[(Mathf.Clamp(y - radius, 0, h - 1) * w) + x];
                sum += tmp[(Mathf.Clamp(y + radius + 1, 0, h - 1) * w) + x];
            }
        }

        return dst;
    }
    private static void SpawnOneGhost(Node2D host, Texture2D tex, Vector2 at, float canvasScale, Vector2 drift, float delay, float life, float driftPx, float alpha, Vector2 texShift = default, float squashX = 1f, int order = 0)
    {
        FieldFx.After(host, delay, "[脱身矢·残影]", () =>
        {
            if (!GodotObject.IsInstanceValid(host))
            {
                return;
            }

            var ghost = new Sprite2D
            {
                Name = "EscapeBodyGhost",
                Texture = tex,
                Centered = true,
                Offset = texShift,
                Modulate = new Color(1f, 1f, 1f, alpha),
            };

            host.AddChild(ghost);

            Vector2 basePos = at / Mathf.Max(0.05f, canvasScale);
            ghost.Position = basePos;
            float inv = 1f / Mathf.Max(0.05f, canvasScale);
            ghost.Scale = new Vector2(inv * squashX, inv);

            // ★ 层序**显式按年龄给**，不要指望"后 AddChild 的就在上面"。
            //   这套残影是**延时撒**的（0 / 0.055 / 0.11 秒各一层），也就是说谁在上
            //   取决于三颗 SceneTree 定时器谁先回调；而更致命的是场景里还有别的
            //   Sprite2D 跟它们抢同一个宿主 —— 原本黑色剪影给了 ZIndex=7（比残影的 6 高），
            //   它偏偏是**最先撒的那一层**，于是整条拖尾被最老的一块黑影压在底下。
            //   现在：黑剪影 8 / 残影 10+order，年龄越大 z 越小 ⇒ 新的永远压在老的上。
            ghost.ZIndex = 10 + order;

            Sprite2D g = ghost;

            FieldFx.Animate(g, life, k =>
            {
                if (!GodotObject.IsInstanceValid(g))
                {
                    return;
                }

                // 慢慢往后飘一点，透明度用 k² 曲线 —— 前段几乎不变淡，读得清轮廓
                g.Position = basePos + (drift * (driftPx * FieldFxEase.OutCubic(k)));
                float fade = 1f - (k * k);
                g.Modulate = new Color(1f, 1f, 1f, alpha * fade);
            });

            FieldFx.FreeAfter(g, life + 0.08f);
        });
    }
    private static void SpawnBlackFlash(Node2D host, Texture2D lineArt, Vector2 at, float canvasScale, Vector2 texShift, Vector2 awayN)
    {
        FieldFx.After(host, 0.0, "[脱身矢·黑闪]", () =>
        {
            if (!GodotObject.IsInstanceValid(host))
            {
                return;
            }

            float inv = 1f / Mathf.Max(0.05f, canvasScale);
            Vector2 basePos = at / Mathf.Max(0.05f, canvasScale);

            // ── ① 本体：一块**接近纯黑**的剪影。
            //    贴图的 alpha 就是她的身体轮廓（见 MakeLineArt 的掩膜），
            //    把 RGB 压到 0.04 就只剩"一个人形的洞"。
            //    ★ 不能真的写 0 —— 全纯黑在暗紫地板上会和背景糊在一起，留一点点蓝
            //      才能和"没有东西"区分开，读起来像"她一瞬间变成了一道暗影"。
            // ── ①a 先垫一层**放大 7% 的冰蓝底**：黑剪影边缘会露出这一圈，
            //      读作"黑色的人形外面还有一道冷光"，否则纯黑在暗背景上就是一团糊。
            var halo = new Sprite2D
            {
                Name = "EscapeBlackFlashHalo",
                Texture = lineArt,
                Centered = true,
                Offset = texShift,
                Modulate = new Color(0.55f, 0.85f, 1f, 0.55f),
            };

            host.AddChild(halo);
            halo.Position = basePos;
            halo.Scale = Vector2.One * (inv * 1.11f);
            halo.ZIndex = 7;

            var body = new Sprite2D
            {
                Name = "EscapeBlackFlash",
                Texture = lineArt,
                Centered = true,
                Offset = texShift,
                Modulate = new Color(0.035f, 0.028f, 0.055f, 1f),
            };

            host.AddChild(body);
            body.Position = basePos;
            body.Scale = Vector2.One * (inv * 1.04f);

            // 黑剪影是**整条拖尾里最老的一层**（delay 0），所以它必须排在最下面：
            // 残影用 10+order，这里给 8 —— 新撒的每一层都压在它上面。
            body.ZIndex = 8;

            // ── ② 剪影本身**必须是纯黑**：第一版我在黑剪影上面又盖了一层线稿贴图，
            //    结果那张贴图的身体内部填的是冰蓝（不是透明），整块黑被它完全盖住，
            //    实机看到的是"发光的她"而不是"黑掉的她"。所以这里**不再叠线稿**。
            //    黑到什么程度：RGB 压到 0.03 左右 —— 真写 0 会和暗紫背景糊在一起，
            //    留一点点蓝才读得出"这里原本有个人"。

            Sprite2D b = body;
            Sprite2D hl = halo;

            // 闪身的时间感：0.05s 完全黑住（人眼至少要看得到 3 帧），然后 0.11s 淡出，
            // 同时整体缩一点（不是放大 —— 放大读作"炸开"，缩小才读作"抽走"）。
            FieldFx.Animate(b, 0.16, k =>
            {
                if (!GodotObject.IsInstanceValid(b))
                {
                    return;
                }

                float a = k < 0.31f ? 1f : 1f - FieldFxEase.InCubic((k - 0.31f) / 0.69f);
                b.Modulate = new Color(0.03f, 0.024f, 0.05f, a);
                b.Scale = Vector2.One * (inv * Mathf.Lerp(1.04f, 0.93f, FieldFxEase.OutCubic(k)));

                if (GodotObject.IsInstanceValid(hl))
                {
                    hl.Modulate = new Color(0.55f, 0.85f, 1f, 0.55f * a);
                    hl.Scale = Vector2.One * (inv * Mathf.Lerp(1.11f, 1.02f, FieldFxEase.OutCubic(k)));
                }
            });

            FieldFx.FreeAfter(b, 0.22);
            FieldFx.FreeAfter(hl, 0.22);

            Diag("escapeblack", $"[脱身矢·黑闪] 原位黑色剪影 at={at}", 4);
        });
    }

    private static void SpawnRing(Node2D parent, float radius, float squash, Color color, float width, float life)
    {
        Line2D ring = FieldFx.Line(parent, FieldFx.Circle(radius, 44, squash), color, width, closed: true);

        FieldFx.Animate(ring, life, k =>
        {
            ring.Scale = Vector2.One * Mathf.Lerp(0.55f, 3.0f, FieldFxEase.OutCubic(k));
            ring.Modulate = new Color(1f, 1f, 1f, 1f - FieldFxEase.InCubic(k));
        });
    }

    /// <summary>
    /// 把一个生物节点下的 <see cref="Sprite2D"/> 全部收集起来（最多 <paramref name="max"/> 个）。
    /// 递归，因为她的身体/头发/武器可能分散在几层子节点里。
    /// </summary>
    private static void CollectSprites(Node node, System.Collections.Generic.List<Sprite2D> into, int max)
    {
        foreach (Node child in node.GetChildren())
        {
            if (into.Count >= max)
            {
                return;
            }

            if (child is Sprite2D s && s.Texture != null)
            {
                into.Add(s);
            }

            CollectSprites(child, into, max);
        }
    }

    /// <summary>
    /// 诊断用：把节点树里出现的<b>类型名</b>列出来（每个类型一次），
    /// 用来看清"她的身体到底是用什么节点画的"。
    /// </summary>
    private static string DescribeTree(Node root)
    {
        try
        {
            var names = new System.Collections.Generic.List<string>();
            Walk(root, names, 0);

            return string.Join(", ", names);
        }
        catch (Exception e)
        {
            return e.GetType().Name;
        }
    }

    private static void Walk(Node node, System.Collections.Generic.List<string> names, int depth)
    {
        if (depth > 6 || names.Count > 14)
        {
            return;
        }

        foreach (Node child in node.GetChildren())
        {
            string n = child.GetType().Name;

            if (!names.Contains(n))
            {
                names.Add(n);
            }

            Walk(child, names, depth + 1);
        }
    }

    /// <summary>
    /// 给一个生物节点做运动残影：把它的精灵复制到特效层，染成冷白、加色，然后淡出。
    /// <para>这是"她后滑"唯一能让人一眼看出速度的做法 —— 单纯把节点位移 205px
    /// 在 2510px 宽的画面里只是"她挪了一下"，加上残影才是"她嗖地退出去"。</para>
    /// </summary>
    private static void SpawnBodyGhosts(Node src, Control layer, Vector2 away, int count)
    {
        try
        {
            var sprites = new System.Collections.Generic.List<Sprite2D>();
            CollectSprites(src, sprites, 8);

            Diag("escapeghost", $"[脱身矢·残影] 复制 {sprites.Count} 个精灵，{count} 层", 6);

            if (sprites.Count == 0)
            {
                Diag("escapeghosttree", "[脱身矢·残影] 她身上找不到 Sprite2D，节点类型表：" + DescribeTree(src), 6);
            }

            if (sprites.Count == 0)
            {
                return;
            }

            Node2D host = FieldFx.Root(layer, Vector2.Zero, "RayModEscapeBodyGhosts");
            FieldFx.FreeAfter(host, 1.1f);

            for (int i = count; i >= 1; i--)
            {
                float delay = i * 0.055f;
                float alpha = 0.40f - ((i - 1) * 0.10f);
                Vector2 nudge = away * (i * 26f);

                FieldFx.After(host, delay, "[脱身矢·残影]", () =>
                {
                    foreach (Sprite2D s in sprites)
                    {
                        if (!GodotObject.IsInstanceValid(s) || !GodotObject.IsInstanceValid(host))
                        {
                            continue;
                        }

                        var g = new Sprite2D
                        {
                            Name = "BodyGhost",
                            Centered = s.Centered,
                            Offset = s.Offset,
                            FlipH = s.FlipH,
                            FlipV = s.FlipV,
                            Texture = s.Texture,
                            Scale = s.Scale,
                            Material = FieldFx.Additive,
                        };

                        host.AddChild(g);
                        g.GlobalTransform = s.GlobalTransform;
                        g.GlobalPosition += nudge;

                        Sprite2D gg = g;

                        FieldFx.Animate(gg, 0.26f, k =>
                        {
                            gg.GlobalPosition += away * (1.8f * (1f - k));
                            gg.Modulate = new Color(EscIce.R, EscIce.G, EscIce.B, alpha * (1f - FieldFxEase.InCubic(k)));
                        });

                        FieldFx.FreeAfter(gg, 0.30f);
                    }
                });
            }
        }
        catch (Exception e)
        {
            LogOnce($"[脱身矢·残影] {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// 四角长星的多边形点集（<paramref name="points"/> 个长角，角间夹短角）。
    /// 以原点为中心 —— <c>Polygon2D</c> 没有 <c>PivotOffset</c>（那是 Control 的属性），
    /// 想绕中心转只能靠"顶点本身以原点为中心"。
    /// </summary>
    private static Vector2[] StarPts(float outer, float inner, float rotation)
    {
        const int Long = 4;
        int n = Long * 2;
        var pts = new Vector2[n];

        for (int i = 0; i < n; i++)
        {
            float a = rotation + (i * Mathf.Tau / n);
            float r = (i % 2 == 0) ? outer : inner;
            pts[i] = new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
        }

        return pts;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ③ 敌人被打出轻微位移
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 敌人被推出去 <see cref="KnockPx"/> 像素，然后用弹性回位。
    /// <para>和 <c>HitReact</c> 的区别：那个是"原地抖"（<c>sin</c> 振荡，中心不变），
    /// 用户这次要的是<b>位移</b> —— 先整体挪开、再被拉回来，所以这里用
    /// 「0→1 的推挤 + 带回弹的回归」两段，并且同时压扁一点。</para>
    /// <para>末帧精确写回原 <c>Position</c>，不会把它永久推歪。</para>
    /// </summary>
    private static void Knockback(object? target, Vector2 dir, Control layer)
    {
        if (target == null)
        {
            return;
        }

        try
        {
            Node? raw = CreatureNodeOf(target) as Node;

            if (raw is not Control and not Node2D)
            {
                return;
            }

            Vector2 pos = raw switch
            {
                Control c => c.Position,
                Node2D n2 => n2.Position,
                _ => Vector2.Zero,
            };

            Vector2 scale0 = raw switch
            {
                Control c => c.Scale,
                Node2D n2 => n2.Scale,
                _ => Vector2.One,
            };

            void Apply(Vector2 p, Vector2 s)
            {
                switch (raw)
                {
                    case Control c:
                        c.Position = p;
                        c.Scale = s;
                        break;

                    case Node2D n2:
                        n2.Position = p;
                        n2.Scale = s;
                        break;
                }
            }

            FieldFx.Animate(raw, KnockTime, k =>
            {
                float e;

                if (k < 0.16f)
                {
                    // 推出去
                    e = FieldFxEase.OutCubic(k / 0.16f);
                }
                else
                {
                    // 弹回来，带一次小幅过冲（sin 半周衰减）
                    float q = (k - 0.16f) / 0.84f;
                    e = (1f - FieldFxEase.OutCubic(q)) - (Mathf.Sin(q * Mathf.Pi) * 0.20f);
                }

                // 横向被推扁、纵向被拉长（挤压感），随 e 衰减
                float squeeze = Mathf.Abs(e) * 0.06f;

                Apply(
                    pos + (dir * (KnockPx * e)),
                    new Vector2(scale0.X * (1f + squeeze), scale0.Y * (1f - (squeeze * 0.6f))));
            });

            // 脚下擦出一道灰（"被推走"的地面证据）
            Vector2 skidAt = ChestPos(target);

            if (skidAt != Vector2.Zero)
            {
                Node2D foot = FieldFx.Root(layer, skidAt + new Vector2(0f, 84f), "RayModEscapeSkid");

                SmokePuff(foot, EscDust, 22f, 0.52f, new Vector2(dir.X * 54f, -14f), 4);
                DebrisShards(foot, EscSteelDark, 6, 60f, 180f, 2.6f, 0.42f);

                FieldFx.FreeAfter(foot, 0.95f);
            }

            Diag("escapeknock", $"[脱身矢·位移] 推出 {KnockPx:F0}px，{KnockTime:F2}s 后回位", 6);
        }
        catch (Exception e)
        {
            LogOnce($"[脱身矢·位移] {e.GetType().Name}: {e.Message}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ④ 她被甩出去，再回来
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>用户这次的核心要求</b>："击中敌人时，将莱伊模型移出。之后返回。"
    /// <para>做法：沿弹道的<b>反方向</b>急速后滑 <see cref="SlideDist"/> 像素，同时缩到
    /// <see cref="SlideShrink"/>、淡到 <see cref="SlideAlpha"/>（配合脚下的尘读作"退开了"），
    /// 停 <see cref="SlideHoldTime"/>，再用 <c>OutBack</c> 弹回原位。</para>
    /// <para>★ 为什么用"淡出"而不是真的把节点移出场景树：她的节点上挂着血条、
    /// 状态图标、以及游戏自己的动画，移出再插回来会打乱层级和 tween。
    /// 淡到 0.16 + 缩到 0.82 已经足够读作"人退到后面去了"。</para>
    /// <para>★ 必须精确写回 <c>Position</c> / <c>Rotation</c> / <c>Scale</c> / <c>Modulate</c>
    /// 四个量（末帧），否则她会永久偏移或被永久染色。</para>
    /// </summary>
    private static void SlideAway(object? dealer, Vector2 dir, Control layer)
    {
        if (dealer == null)
        {
            return;
        }

        try
        {
            Node? raw = CreatureNodeOf(dealer) as Node;

            if (raw is not Control and not Node2D)
            {
                LogOnce($"[脱身矢·后滑] 跳过：生物节点是 {raw?.GetType().Name ?? "null"}");
                return;
            }

            ulong id = raw.GetInstanceId();
            ulong now = Time.GetTicksMsec();

            if (_escapeSlideMs.TryGetValue(id, out ulong last) && (now - last) < 600UL)
            {
                return;
            }

            _escapeSlideMs[id] = now;

            Vector2 pos = raw switch
            {
                Control c => c.Position,
                Node2D n2 => n2.Position,
                _ => Vector2.Zero,
            };

            float rot0 = raw switch
            {
                Control c => c.Rotation,
                Node2D n2 => n2.Rotation,
                _ => 0f,
            };

            Vector2 scale0 = raw switch
            {
                Control c => c.Scale,
                Node2D n2 => n2.Scale,
                _ => Vector2.One,
            };

            Color mod0 = raw switch
            {
                Control c => c.Modulate,
                Node2D n2 => n2.Modulate,
                _ => Colors.White,
            };

            void Apply(Vector2 p, float r, Vector2 s, float alpha)
            {
                switch (raw)
                {
                    case Control c:
                        c.Position = p;
                        c.Rotation = r;
                        c.Scale = s;
                        c.Modulate = new Color(mod0.R, mod0.G, mod0.B, mod0.A * alpha);
                        break;

                    case Node2D n2:
                        n2.Position = p;
                        n2.Rotation = r;
                        n2.Scale = s;
                        n2.Modulate = new Color(mod0.R, mod0.G, mod0.B, mod0.A * alpha);
                        break;
                }
            }

            // ★ 2026-10-21 用户要求：<b>不要"沿弹道反方向退"，就是纯向左平移</b>。
            //   原来 away = -dir，而弹道从她胸口指向敌人、是斜向上的
            //   （实测 (0.99, -0.137)），于是她的位移带着一个向上的分量，
            //   读起来像"飘走"而不是"横着滑开"。现在方向硬编码成屏幕左，
            //   她、残影、尘、速度条全都跟着变成纯水平。
            Vector2 away = Vector2.Left;

            // 平移不转向：倾角必须是 0，否则"平移"会带上一个俯身的姿态。
            float lean = 0f;

            float total = SlideOutTime + SlideHoldTime + SlideBackTime;

            FieldFx.Animate(raw, total, k =>
            {
                float t = k * total;
                float slide;
                float alpha;

                if (t < SlideOutTime)
                {
                    // ① 甩出去
                    float e = FieldFxEase.OutCubic(t / SlideOutTime);
                    slide = e;
                    alpha = Mathf.Lerp(1f, SlideAlpha, e);
                }
                else if (t < SlideOutTime + SlideHoldTime)
                {
                    // ② 在外面停着（微微呼吸，免得像一张静止的贴纸）
                    float h = (t - SlideOutTime) / SlideHoldTime;
                    slide = 1f + (Mathf.Sin(h * Mathf.Pi) * 0.018f);
                    alpha = SlideAlpha;
                }
                else
                {
                    // ③ 弹回来（OutBack 有过冲，落地感更强）
                    float b = (t - SlideOutTime - SlideHoldTime) / SlideBackTime;
                    slide = 1f - FieldFxEase.OutBack(b);
                    alpha = Mathf.Lerp(SlideAlpha, 1f, Mathf.Clamp(b * 1.35f, 0f, 1f));
                }

                Apply(
                    pos + (away * (SlideDist * slide)) + new Vector2(0f, SlideDrop * slide),
                    rot0 + (lean * slide),
                    new Vector2(scale0.X, scale0.Y) * Mathf.Lerp(1f, SlideShrink, slide),
                    alpha);
            });

            // 她自己的残影：把生物节点下的 Sprite2D 复制几份丢在身后淡出。
            // 复制的是真贴图，所以残影的轮廓/配色和她一模一样（比画个人形色块强得多）。
            SpawnBodyGhosts(raw, layer, away, 3);

            // 起跳点的尘：她脚下擦出去的一道灰（非加色 —— 烟和尘必须能压暗画面）
            Vector2 dustAt = ChestPos(dealer);

            if (dustAt != Vector2.Zero)
            {
                Node2D foot = FieldFx.Root(layer, dustAt + new Vector2(0f, 84f), "RayModEscapeSlideDust");

                SmokePuff(foot, EscDust, 30f, 0.62f, new Vector2(away.X * 60f, -26f), 5);
                DebrisShards(foot, EscSteelDark, 7, 80f, 230f, 3.0f, 0.48f);

                // 冷光短弧：卡图里她身后是一排蓝白能量刺
                for (int i = 0; i < 5; i++)
                {
                    float off = (i - 2f) * 26f;
                    float h = 46f + (Mathf.Abs(i - 2f) * -8f);
                    Vector2 a = new(away.X * 18f, off - (h * 0.5f));
                    Vector2 b = new(away.X * 18f + (away.X * h * 0.55f), off + (h * 0.5f));

                    Line2D spike = FieldFx.Line(foot, new Vector2[] { a, b }, new Color(EscIce, 0.42f), 3.0f);

                    FieldFx.Animate(spike, 0.34f, k =>
                    {
                        spike.Modulate = new Color(1f, 1f, 1f, 1f - FieldFxEase.InCubic(k));
                    });
                }

                // 速度条：卡图里她身后是一整片水平速度线 —— 这是"她正在高速后退"最省事的读法，
                // 比单纯把她位移 300px 更清楚（位移在 2510px 的画面里只占 1/8）。
                for (int i = 0; i < 8; i++)
                {
                    float ry = (((i * 37f) % 150f) - 75f) * 1.35f;
                    float len = 130f + ((i * 53f) % 120f);
                    Vector2 s0 = new(away.X * 26f, ry);
                    Vector2 s1 = new(away.X * (26f + len), ry);

                    Line2D streak = FieldFx.Line(
                        foot,
                        new Vector2[] { s0, s1 },
                        new Color(EscWhite, 0.50f),
                        2.6f);

                    streak.Position = -away * 140f;

                    FieldFx.After(foot, i * 0.012f, "[脱身矢·速度条]", () =>
                    {
                        FieldFx.Animate(streak, 0.30f, k =>
                        {
                            streak.Position = away * (140f * FieldFxEase.OutCubic(k));
                            streak.Modulate = new Color(1f, 1f, 1f, 1f - FieldFxEase.InCubic(k));
                        });
                    });
                }

                FieldFx.FreeAfter(foot, 1.05f);
            }

            Diag("escapeslide", $"[脱身矢·后滑] 甩出 {SlideDist:F0}px，{total:F2}s 后回位", 6);
        }
        catch (Exception e)
        {
            LogOnce($"[脱身矢·后滑] {e.GetType().Name}: {e.Message}");
        }
    }
}
