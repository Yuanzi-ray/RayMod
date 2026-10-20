using System;
using Godot;
using RayMod.Ammo;

namespace RayMod.Art;

/// <summary>
/// <b>「狙击」（<see cref="RayMod.Cards.Snipe"/>）的场上表现</b>（2026-10-20）。
///
/// <para><b>这张牌为什么值得单独一套</b>：它的机制是"<b>消耗所有弹药，每 1 层对随机敌人打 5 点</b>"
/// —— 一回合攒了 8 层就是<b>一口气打 8 发</b>。而它现在用的是
/// <c>WithHitFx("vfx/vfx_attack_slash")</c>，也就是<b>砍一刀</b>的刀光：
/// 一把枪打出 8 道刀光，是全工程最违和的一处。换成子弹之后，
/// 「弹药层数 → 几发子弹」这条因果<b>不用看数字就能读懂</b>。</para>
///
/// <para><b>为什么挂在伤害上、而不是挂在出牌上</b>：见 <c>CardHitVfxPatch</c> 的注释 ——
/// 出牌只是"决定了要打"，真正的伤害在若干帧后才落地，而且这张牌<b>每段的随机目标都不同</b>。
/// <c>Hook.AfterDamageGiven</c> 同时给了 <c>dealer</c>（从谁的枪口出）、
/// <c>target</c>（飞到哪）、<c>cardSource</c>（是不是这张牌），是唯一能对得齐的地方。</para>
///
/// <para><b>2026-10-20 第二轮（用户："好像不太对。你可以自己录屏看是不是你做的效果？"）</b><br/>
/// 我自己录屏逐帧核对过 <c>D:\study\sts2mod\_tmp\rec_snipe.mp4</c>：<b>机制是对的、画也是画了的</b>
/// （日志有 <c>[狙击·表现] 开火 chest=(480,644) muzzle=(538,644) to=(1587,658)</c>，
/// 帧 <c>f_0020.png</c> 里能数出那条从枪口到敌人的暖色线），但<b>太细太暗太快</b>：
/// 3.2px 的近白线在 2560 宽的画面上只是一根头发，旁边原版 <c>vfx_starry_impact</c>
/// 的紫白爆闪反而抢戏。所以这一版的全部改动都指向<b>加粗加亮加长</b>，不是改逻辑。</para>
///
/// <para><b>每一发都做完整四件事</b>（缺任何一件就退化成"一条黄线"）：
/// ① 枪口火光（星 + 锥 + 亮核）；② <b>弹头真的飞过去</b>（0.055 秒，之后拖尾再留 0.14 秒）；
/// ③ 弹壳弹出来；④ 命中点爆开（星 + 双环 + 闪核 + 火星）。带沙地兽印记的目标整套放大
/// —— 那一次伤害是 ×1.5 的，表现上必须能看出来"这一发打得更重"。</para>
///
/// <para>⚠️ 全程零贴图（<c>FieldFx</c> 原语 + 自绘多边形），因为这工程已经踩过
/// "Asset not cached: res://...tscn" 的坑（见 <c>CardFlyTrail</c> 的注释）。</para>
/// </summary>
internal partial class CardFieldVfx
{
    // ── 调色板（和她卡图同源：暖白 / 暖金 / 铜）──────────────────────────
    private static readonly Color SnipeHot = new(1.000f, 0.965f, 0.859f);   // #FFF6DB 枪口与弹头
    private static readonly Color SnipeWarm = new(1.000f, 0.800f, 0.360f);  // 拖尾外晕
    private static readonly Color SnipeCopper = new(0.784f, 0.471f, 0.235f); // 弹壳
    private static readonly Color SnipeBrass = new(0.831f, 0.651f, 0.294f);  // 弹壳高光

    // ── 沙尘调色板（矿井/沙地底色；**不能加色**，见 SandDust 的注释）────────
    private static readonly Color SandLight = new(0.835f, 0.741f, 0.545f, 0.42f);  // 扬起来的浮尘
    private static readonly Color SandDark = new(0.588f, 0.494f, 0.337f, 0.34f);   // 贴地的老尘
    private static readonly Color SandGrain = new(0.878f, 0.792f, 0.588f, 0.85f);  // 沙粒

    /// <summary>枪口相对她胸口的位置（牌局部/世界坐标同向：敌人在右 ⇒ +X）。</summary>
    private static readonly Vector2 SnipeMuzzleOffset = new(58f, 0f);

    /// <summary>弹头飞行时长。太短看不出"飞"，太长和伤害数字对不上。</summary>
    private const float SnipeTravel = 0.055f;

    /// <summary>一发弹道的总时长（飞行 + 拖尾余韵）。</summary>
    private const float SnipeTracerLife = 0.20f;

    /// <summary>
    /// 三层弹道宽度。★ 2026-10-20 实测：原来的 3.2 / 12 在 2560 宽的屏幕上只是一根头发，
    /// 逐帧看都难认。加粗到 6 / 30 之后才读得出"一道光柱"。
    /// </summary>
    private const float SnipeCoreWidth = 6f;
    private const float SnipeInnerWidth = 2.2f;
    private const float SnipeHaloWidth = 30f;

    /// <summary>弹壳存活时长 / 重力。</summary>
    private const float SnipeCasingLife = 0.55f;
    private const float SnipeCasingGravity = 1500f;

    /// <summary>
    /// 一次狙击伤害的表现。<b>返回 true = 这张牌已被本方法接管</b>
    /// （⇒ 调用方不要再叠通用命中层，否则子弹和刀光会同时出现）。
    /// <para><paramref name="main"/> = 这是本张牌的第 1 发 ⇒ 额外抖一次屏。</para>
    /// </summary>
    internal static bool SnipeShot(object cardSource, object? dealer, object target, bool main = true)
    {
        if (cardSource is not RayMod.Cards.Snipe)
        {
            return false;
        }

        try
        {
            // 已经认领了这张牌：即使下面画不出来，也不能退回通用层。
            if (!Enabled || TestModeIsOn() || dealer == null)
            {
                LogOnce($"[狙击·表现] 早退：Enabled={Enabled} Test={TestModeIsOn()} dealer={(dealer == null ? "null" : dealer.GetType().Name)}");
                return true;
            }

            Vector2 to = ChestPos(target);
            Vector2 chest = ChestPos(dealer);

            if (to == Vector2.Zero || chest == Vector2.Zero)
            {
                LogOnce($"[狙击·表现] 定位失败：chest={chest} to={to}（ChestPos 读不到 ⇒ 什么都画不出来）");
                return true;
            }

            if (VfxParentForEffects() is not Control layer)
            {
                LogOnce("[狙击·表现] 拿不到战斗特效容器（VfxParentForEffects 为 null）");
                return true;
            }

            // 枪口朝敌人那一侧（莱伊通常面朝右，但她会被镜像/站位改变 ⇒ 按目标方向翻）
            float dir = to.X >= chest.X ? 1f : -1f;
            Vector2 muzzle = chest + new Vector2(SnipeMuzzleOffset.X * dir, SnipeMuzzleOffset.Y);
            bool marked = IsMarked(target);

            SnipeMuzzleFlash(layer, muzzle, new Vector2(dir, 0f), marked);
            SnipeTrace(layer, muzzle, to, marked);
            SnipePathSparks(layer, muzzle, to, SnipeTravel);
            SnipeAirLines(layer, muzzle, to);
            SnipeCasing(layer, muzzle, dir);

            // 枪口的沙尘：开火把地上的土掀起来，往后飘
            Node2D muzzleDust = FieldFx.Root(layer, muzzle + new Vector2(-13f * dir, 12f), "SnipeMuzzleDust");
            SandDust(muzzleDust, new Vector2(-dir * 52f, -28f), 1f, grains: true);
            FieldFx.FreeAfter(muzzleDust, 1.15f);

            // 命中点等着弹头到达再爆（不然"先炸后飞"）
            SnipeImpactAfter(layer, to, marked, SnipeTravel, new Vector2(dir, 0f), target);

            // ★ 2026-10-20（第四轮，用户"都可以加"）：
            //   ① 顿帧 + 抖屏（DoHitStop 的 ShakeStrengthName/DurationName 就是 Strong/Normal）；
            //   ② 后坐力：把她往后顶一下再弹回 —— "是谁在开枪"这件事得演出来。
            //   连发时每发都顿帧会卡成幻灯片，所以只给第 1 发。
            if (main)
            {
                DoHitStop();
                Recoil(dealer, new Vector2(dir, 0f));

                // 「牌面被烫穿」：连发 8 段只有第 1 段演（其余段牌已经飞走/正在飞）
                BurnPlayedCard(cardSource, new Vector2(dir, 0f));
            }

            LogOnce($"[狙击·表现] 开火：chest={chest} muzzle={muzzle} to={to} 距离={(to - muzzle).Length():F0} 印记={marked} main={main}");

            try
            {
                RayMod.Audio.SfxPlayer.Play(RayMod.Audio.Sfx.Shot, null);
            }
            catch
            {
                // 音效失败不影响表现
            }
        }
        catch (Exception e)
        {
            LogOnce($"[狙击·表现] {e.GetType().Name}: {e.Message}");
        }

        return true;
    }

    /// <summary>目标身上有没有沙地兽印记（有 ⇒ 这一发是 ×1.5，要更大更亮）。</summary>
    private static bool IsMarked(object target)
    {
        try
        {
            return target is MegaCrit.Sts2.Core.Entities.Creatures.Creature c
                   && c.HasPower<RayMod.Beasts.SandBeastMarkPower>();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 枪口火：四角星 + 向前的锥 + 一颗亮核，0.14 秒散掉。
    /// <para>只留一颗小星星的话，在 2560 宽的画面里几乎看不见（第一版就是这么被漏掉的）。</para>
    /// </summary>
    private static void SnipeMuzzleFlash(Control layer, Vector2 at, Vector2 dir, bool marked)
    {
        Node2D root = FieldFx.Root(layer, at, "SnipeMuzzle");
        float len = marked ? 56f : 42f;

        Vector2[] star =
        {
            new(len, 0f),
            new(0f, -len * 0.30f),
            new(-len * 0.34f, 0f),
            new(0f, len * 0.30f),
        };

        Polygon2D flash = FieldFx.Poly(root, star, SnipeHot);
        flash.Rotation = dir.Angle();

        // 向前的锥：读作"一口气喷出去"，比单纯一颗星有方向感
        Vector2[] cone =
        {
            new(-6f, -len * 0.30f),
            new(len * 1.55f, 0f),
            new(-6f, len * 0.30f),
        };

        Polygon2D puff = FieldFx.Poly(root, cone, new Color(SnipeWarm.R, SnipeWarm.G, SnipeWarm.B, 0.55f));
        puff.Rotation = dir.Angle();

        // 亮核：一颗实心圆，压在所有东西上面（加色混合下这一颗就是"闪"的主体）
        Polygon2D core = FieldFx.Poly(root, FieldFx.Circle(marked ? 11f : 9f, 18), SnipeHot);

        FieldFx.Animate(flash, 0.14, k =>
        {
            flash.Scale = Vector2.One * Mathf.Lerp(0.55f, 1.70f, FieldFxEase.OutCubic(k));
            flash.Modulate = new Color(1f, 1f, 1f, 1f - k);
        });

        FieldFx.Animate(puff, 0.14, k =>
        {
            puff.Scale = new Vector2(Mathf.Lerp(0.45f, 1.35f, FieldFxEase.OutCubic(k)), 1f);
            puff.Modulate = new Color(1f, 1f, 1f, 0.85f * (1f - k));
        });

        FieldFx.Animate(core, 0.10, k =>
        {
            core.Scale = Vector2.One * Mathf.Lerp(1.25f, 0.25f, FieldFxEase.OutCubic(k));
            core.Modulate = new Color(1f, 1f, 1f, 1f - k);
        });

        LinesSparks(root, marked ? 12 : 8);

        // ★ 2026-10-20（第三轮，用户"太素了"）：光有光没有"东西" ⇒ 补两样实物。
        //   ① 枪口烟：三团灰白烟，非加色（`additive: false`）⇒ 真的像烟，不像光晕；
        //   ② 黄铜碎屑：几片自带旋转的小多边形，飞出去被重力拽下来 —— "有东西崩出来"。
        SmokePuff(root, new Color(0.66f, 0.63f, 0.60f, 0.50f), 22f, 0.55f, new Vector2(dir.X * 52f, -34f), 4);
        DebrisShards(root, SnipeBrass, marked ? 10 : 7, 130f, 360f, 4.2f, 0.46f);

        // ③ 第四轮：枪口余烟 —— 一团慢慢往上飘、活 0.9 秒的淡烟，把"刚开过枪"留在原地。
        SmokePuff(root, new Color(0.60f, 0.58f, 0.55f, 0.34f), 30f, 0.90f, new Vector2(dir.X * 26f, -72f), 2);

        FieldFx.FreeAfter(root, 1.05);
    }

    private static void LinesSparks(Node2D root, int amount)
    {
        GpuParticles2D p = FieldFx.Sparks(root, SnipeHot, amount: amount,
            speedMin: 150f, speedMax: 430f, lifetime: 0.20f,
            gravity: 80f, scaleMin: 0.08f, scaleMax: 0.22f);
        FieldFx.Ignite(p);
    }

    /// <summary>弹道：一个自绘节点，画"弹头 + 从枪口到弹头的拖尾"。</summary>
    private static void SnipeTrace(Control layer, Vector2 from, Vector2 to, bool marked)
    {
        // ★★ 2026-10-20 用户反馈"没看到子弹" ⇒ 两个真因都在这里：
        //   ① 上一版把节点挂在世界原点、靠 ToLocal 换算 —— 但**没有任何东西保证它自己会被重画**；
        //   ② 更要命的是 <see cref="SnipeTracer"/> 只重写了 _Process，而这个工程里
        //      "运行期 new 出来的节点"实测<b>不会自动开 process</b>（同类事故见 GiantEye / BulletMode
        //      里显式调用的 SetProcess(true)）。process 没开 ⇒ _t 永远是 0 ⇒ 弹头永远停在枪口，
        //      画出来就是一个 3px 点 —— 观感就是"根本没有子弹"。
        //   现在改成：父节点用 FieldFx.Root 定位到枪口（工程里唯一被验证过的定位方式），
        //   子弹只画"相对枪口的位移"，并在 _Ready 里显式 SetProcess(true)。
        Node2D root = FieldFx.Root(layer, from, "SnipeTraceRoot");

        var tracer = new SnipeTracer
        {
            Name = "SnipeTracer",
            Span = to - from,
            Marked = marked,
            Material = FieldFx.Additive,
        };

        root.AddChild(tracer);
        FieldFx.FreeAfter(root, SnipeTracerLife + 0.15f);

        // ★ 动态光柱（锥形 + 行波 + 流光 + 流动断口）：代替原来的三条定宽 DrawLine。
        SpawnStreak(layer, from, to,
            growTime: SnipeTravel,
            holdTime: SnipeTracerLife - SnipeTravel - 0.06f,
            fadeTime: 0.14f,
            thickWidth: SnipeCoreWidth * (marked ? 2.6f : 1.9f),
            haloWidth: SnipeHaloWidth * (marked ? 2.0f : 1.6f),
            waveAmp: marked ? 4f : 3f,
            core: SnipeHot,
            glow: SnipeWarm,
            pulses: marked ? 4 : 3);
    }

    /// <summary>弹壳：从枪口斜上方抛出去，自旋 + 重力 + 淡出。</summary>
    private static void SnipeCasing(Control layer, Vector2 at, float dir)
    {
        Node2D root = FieldFx.Root(layer, at, "SnipeCasing");

        Vector2[] caseShape =
        {
            new(-3.0f, -9.0f), new(3.0f, -9.0f), new(3.0f, 9.0f), new(-3.0f, 9.0f),
        };

        // ⚠️ Polygon2D 是 Node2D，<b>没有</b> PivotOffset（那是 Control 的属性）。
        //    绕中心旋转要靠"顶点本身就以原点为中心"来保证 —— 上面那四个点就是这个意思。
        Polygon2D casing = FieldFx.Poly(root, caseShape, SnipeCopper);

        Vector2 v0 = new(150f * dir, -420f);
        float spin = 720f * dir;
        Vector2 p0 = Vector2.Zero;

        FieldFx.Animate(casing, SnipeCasingLife, k =>
        {
            float t = (float)(k * SnipeCasingLife);
            casing.Position = p0 + v0 * t + new Vector2(0f, 0.5f * SnipeCasingGravity * t * t);
            casing.Rotation = Mathf.DegToRad(spin * t);
            casing.Modulate = new Color(1f, 1f, 1f, k < 0.6f ? 1f : 1f - (k - 0.6f) / 0.4f);
        });

        // 一道窄高光，让它在转动时能看出是弹壳而不是一个色块。
        // ★ 必须挂在 casing <b>下面</b>：挂在 root 上它就不跟着弹壳飞了（留在枪口当补丁）。
        FieldFx.Poly(casing,
            new[] { new Vector2(-1.9f, -8.4f), new Vector2(-0.3f, -8.4f),
                    new Vector2(-0.3f, 8.4f), new Vector2(-1.9f, 8.4f) },
            SnipeBrass);

        FieldFx.FreeAfter(root, SnipeCasingLife + 0.05);
    }

    /// <summary>命中点：延后到弹头到达那一刻再爆（穿透孔 + 星 + 闪核 + 双环 + 火星 + 碎屑 + 烟 + 出孔射线）。</summary>
    private static void SnipeImpactAfter(Control layer, Vector2 at, bool marked, float delay, Vector2 dir, object? target)
    {
        Node2D anchor = FieldFx.Root(layer, at, "SnipeImpact");

        FieldFx.After(anchor, delay, "[狙击·命中]", () =>
        {
            try
            {
                // ① 火星
                GpuParticles2D p = FieldFx.Sparks(anchor, marked ? SnipeWarm : SnipeHot,
                    amount: marked ? 30 : 20,
                    speedMin: 120f, speedMax: marked ? 560f : 420f,
                    lifetime: 0.42f, gravity: 520f,
                    scaleMin: 0.10f, scaleMax: marked ? 0.40f : 0.30f);
                FieldFx.Ignite(p);

                // ② 一颗大四角星，方向随机（每一发都不同，连发才不会像同一个印章）
                float a = GD.Randf() * Mathf.Tau;
                float len = marked ? 62f : 46f;
                Vector2[] star =
                {
                    new(len, 0f),
                    new(0f, -len * 0.26f),
                    new(-len * 0.26f, 0f),
                    new(0f, len * 0.26f),
                };

                Polygon2D burst = FieldFx.Poly(anchor, star, SnipeHot);
                burst.Rotation = a;

                FieldFx.Animate(burst, 0.20, k =>
                {
                    burst.Scale = Vector2.One * Mathf.Lerp(0.35f, 1.5f, FieldFxEase.OutCubic(k));
                    burst.Modulate = new Color(1f, 1f, 1f, 1f - k);
                });

                // ③ 闪核：0.10 秒收掉的一颗实心圆，负责"亮"这一下
                Polygon2D core = FieldFx.Poly(anchor, FieldFx.Circle(marked ? 26f : 20f, 22), SnipeHot);

                FieldFx.Animate(core, 0.12, k =>
                {
                    core.Scale = Vector2.One * Mathf.Lerp(1.1f, 0.2f, FieldFxEase.OutCubic(k));
                    core.Modulate = new Color(1f, 1f, 1f, 1f - k);
                });

                // ④ 双环：内环快、外环慢，读作"这一发打实了"
                Line2D ring = FieldFx.Line(anchor, FieldFx.Circle(marked ? 34f : 26f, 30),
                    new Color(SnipeHot.R, SnipeHot.G, SnipeHot.B, 0.95f), marked ? 5.4f : 4.2f, closed: true);

                FieldFx.Animate(ring, 0.24, k =>
                {
                    ring.Scale = Vector2.One * Mathf.Lerp(0.35f, 2.6f, FieldFxEase.OutCubic(k));
                    ring.Modulate = new Color(1f, 1f, 1f, 1f - k);
                });

                Line2D ring2 = FieldFx.Line(anchor, FieldFx.Circle(marked ? 46f : 36f, 30),
                    new Color(SnipeWarm.R, SnipeWarm.G, SnipeWarm.B, 0.75f), 2.6f, closed: true);

                FieldFx.Animate(ring2, 0.34, k =>
                {
                    ring2.Scale = Vector2.One * Mathf.Lerp(0.25f, 1.9f, FieldFxEase.OutCubic(k));
                    ring2.Modulate = new Color(1f, 1f, 1f, 1f - k);
                });

                // ── 2026-10-20（第三轮，用户"太素了"）：光这一层不够，补"打穿了"的实物痕迹 ──
                // ⑤ 弹孔：深色实心 + 暖金边缘（非加色，所以真的压暗一块）。
                //    第四轮（"都可以加"）从 1 个加到 3 个，散在弹着点附近 ⇒ 像"一枪穿了三个洞"。
                BulletHole(anchor, marked ? 15f : 11f, 0.55f);
                BulletHole(anchor, marked ? 10f : 8f, 0.46f, new Vector2(Mathf.Lerp(-26f, 26f, GD.Randf()), Mathf.Lerp(-22f, 22f, GD.Randf())));
                BulletHole(anchor, marked ? 8f : 6.5f, 0.38f, new Vector2(Mathf.Lerp(-34f, 34f, GD.Randf()), Mathf.Lerp(-26f, 26f, GD.Randf())));

                // ⑥ 穿透射线：从命中点继续朝同一方向穿出去一小段（读作"打穿了"）
                Line2D exit = FieldFx.Line(anchor,
                    new[] { dir * 6f, dir * (marked ? 150f : 110f) },
                    new Color(SnipeWarm.R, SnipeWarm.G, SnipeWarm.B, 0.85f), 3.4f);

                FieldFx.Animate(exit, 0.18, k =>
                {
                    exit.Modulate = new Color(1f, 1f, 1f, 1f - k);
                });

                // ⑦ 崩出来的黄铜碎屑（第四轮 12→22，再补一把暗色碎渣压住画面底部）+ ⑧ 烟团 3→6
                DebrisShards(anchor, SnipeBrass, marked ? 22 : 16, 160f, 520f, 4.6f, 0.55f);
                DebrisShards(anchor, new Color(0.32f, 0.24f, 0.16f), marked ? 10 : 7, 90f, 300f, 3.2f, 0.62f);
                SmokePuff(anchor, new Color(0.68f, 0.64f, 0.60f, 0.45f), 26f, 0.62f, new Vector2(0f, -52f), 6);

                // 弹着点的沙尘：比枪口大一号（子弹把土从敌人身上炸出来），中心压低到脚下
                Node2D hitDust = FieldFx.Root(layer, at + new Vector2(0f, 26f), "SnipeImpactDust");
                SandDust(hitDust, new Vector2(dir.X * 46f, -26f), marked ? 1.30f : 1f, grains: true);
                FieldFx.FreeAfter(hitDust, 1.25f);

                // ⑨ 受击方反应（用户："受击方反应也可以加"）：敌人抖回来 + 白闪 + 压扁
                HitReact(target, dir, layer, marked);

                // ⑩ 打穿：弹头穿出目标，**身后**再留一串孔（越远越小、留得越久）——
                //    "这颗子弹是一路穿过去的"，比单点命中多一层纵深。
                for (int holeIndex = 1; holeIndex <= 3; holeIndex++)
                {
                    Vector2 off = dir * (marked ? 96f : 74f) * holeIndex
                                  + new Vector2(0f, Mathf.Lerp(-12f, 12f, GD.Randf()));
                    BulletHole(anchor,
                        Mathf.Max(4f, (marked ? 9f : 7f) - holeIndex * 1.6f),
                        0.28f + 0.10f * holeIndex,
                        off);
                }
            }
            catch
            {
                // 表现层失败不外抛
            }
        });

        FieldFx.FreeAfter(anchor, delay + 0.9);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  "实物层"三个公共原语（2026-10-20 第三轮，用户："现在的问题是太素了"）
    //
    //  用户的整体方向是「特效要向动画转变，用更实际的元素替代原始的光效和粒子」。
    //  所以这三个都**不是光效**：
    //    · DebrisShards —— 会转、会被重力拽下来的小多边形（碎屑）；
    //    · SmokePuff     —— 非加色的灰团，扩张 + 上飘（烟）；
    //    · BulletHole    —— 深色实心 + 暖色边缘（弹孔）。
    //  ⚠️ 三者都用 `additive: false`：加色混合永远只能变亮，
    //     「烟」和「洞」必须能压暗画面，否则又会退回一团光。
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>崩出来的碎屑：n 片带旋转的小多边形，抛物线飞散 + 淡出。</summary>
    private static void DebrisShards(Node parent, Color tint, int count,
        float speedMin, float speedMax, float size, float life)
    {
        for (int i = 0; i < count; i++)
        {
            float ang = GD.Randf() * Mathf.Tau;
            float spd = Mathf.Lerp(speedMin, speedMax, GD.Randf());
            Vector2 vel = new(Mathf.Cos(ang), Mathf.Sin(ang));

            float w = size * Mathf.Lerp(0.65f, 1.45f, GD.Randf());
            Vector2[] shard =
            {
                new(-w, -w * 0.42f),
                new(w * 0.95f, -w * 0.30f),
                new(w * 0.55f, w * 0.50f),
                new(-w * 0.75f, w * 0.38f),
            };

            Polygon2D piece = FieldFx.Poly(parent, shard, tint, additive: false);
            float startRot = GD.Randf() * Mathf.Tau;
            float spin = Mathf.DegToRad(Mathf.Lerp(-760f, 760f, GD.Randf()));
            float l = life * Mathf.Lerp(0.70f, 1.25f, GD.Randf());
            piece.Rotation = startRot;

            FieldFx.Animate(piece, l, k =>
            {
                float t = (float)(k * l);
                piece.Position = vel * spd * t + new Vector2(0f, 0.5f * 900f * t * t);
                piece.Rotation = startRot + spin * t;
                piece.Modulate = new Color(1f, 1f, 1f, k < 0.55f ? 1f : 1f - (k - 0.55f) / 0.45f);
            });
        }
    }

    /// <summary>烟：几团非加色的软圆，一边扩张一边朝 drift 方向飘，越飘越淡。</summary>
    private static void SmokePuff(Node parent, Color tint, float size, float life, Vector2 drift, int puffs)
    {
        for (int i = 0; i < puffs; i++)
        {
            float sz = size * Mathf.Lerp(0.70f, 1.30f, GD.Randf());
            Vector2 off = new(
                Mathf.Lerp(-size * 0.32f, size * 0.32f, GD.Randf()),
                Mathf.Lerp(-size * 0.26f, size * 0.26f, GD.Randf()));

            Polygon2D puff = FieldFx.Poly(parent, FieldFx.Circle(sz, 14), tint, additive: false);
            puff.Position = off;

            float baseA = tint.A;
            float l = life * Mathf.Lerp(0.80f, 1.25f, GD.Randf());

            FieldFx.Animate(puff, l, k =>
            {
                puff.Scale = Vector2.One * Mathf.Lerp(0.55f, 1.95f, FieldFxEase.OutCubic(k));
                puff.Position = off + drift * k;
                puff.Modulate = new Color(1f, 1f, 1f, baseA * (1f - k));
            });
        }
    }

    /// <summary>弹孔：深色实心圆 + 一圈暖金边，先弹出来再慢慢淡掉。</summary>
    /// <param name="offset">相对父节点的位置（同一团命中点要撒多个洞时用）。</param>
    /// <summary>
    /// 沙尘：软团（<see cref="SmokePuff"/>）+ 细沙粒（<see cref="DebrisShards"/>，重力大、尺寸小）。
    /// <para>★ <b>全程不加色</b>：沙是<b>挡光</b>的东西，一加色就变成发光的雾，
    /// 和"土"完全不是一回事（这正是之前给她身边加光晕被嫌"廉价"的原因）。</para>
    /// <paramref name="drift"/> 是尘土整体飘的方向（枪口往后、弹着点往前下方）。
    /// </summary>
    private static void SandDust(Node parent, Vector2 drift, float scale, bool grains)
    {
        SmokePuff(parent, SandLight, 34f * scale, 0.86f, drift, 5);
        SmokePuff(parent, SandDark, 23f * scale, 0.66f, drift * 0.65f, 4);

        if (grains)
        {
            // 沙粒：飞得快、落得急（重力靠 DebrisShards 内部），尺寸比金属碎屑小一圈
            DebrisShards(parent, SandGrain, 18, 40f, 220f, 2.4f * scale, 0.62f);
        }
    }

    private static void BulletHole(Node parent, float radius, float life, Vector2 offset = default)
    {
        Polygon2D hole = FieldFx.Poly(parent, FieldFx.Circle(radius, 18),
            new Color(0.10f, 0.07f, 0.06f, 0.88f), additive: false);

        hole.Position = offset;

        Line2D rim = FieldFx.Line(parent, FieldFx.Circle(radius * 1.12f, 22),
            new Color(1.000f, 0.820f, 0.450f, 0.95f), 2.2f, closed: true);

        rim.Position = offset;

        FieldFx.Animate(hole, life, k =>
        {
            // 前 1/6 秒"弹出来"（先大后收），之后一直挂着直到淡出
            hole.Scale = Vector2.One * Mathf.Lerp(1.35f, 1f, Mathf.Min(1f, k * 6f));
            hole.Modulate = new Color(1f, 1f, 1f, k < 0.55f ? 1f : 1f - (k - 0.55f) / 0.45f);
        });

        FieldFx.Animate(rim, life, k =>
        {
            rim.Scale = Vector2.One * Mathf.Lerp(1.35f, 1f, Mathf.Min(1f, k * 6f));
            rim.Modulate = new Color(1f, 1f, 1f, k < 0.45f ? 1f : 1f - (k - 0.45f) / 0.55f);
        });
    }

    /// <summary>
    /// 后坐力：把出牌者的节点往后顶一下再弹回。
    /// <para>只动 <c>Position</c>（+ 一点 <c>Rotation</c> 的"仰枪"），0.22 秒内一定回到
    /// <b>动之前那个精确值</b>（先存 <c>pos</c>/<c>rot</c>，末帧写回），所以不会把她永久推歪。
    /// 同一个节点 0.30 秒内只吃一次，免得连发时几个 tween 互相抢。</para>
    /// <para>★ 2026-10-20 用户："角色后坐力几乎无表现"。真因是幅度 —— 9px 在 2560 宽的画面上
    /// 只占 0.35%，等于没动。现在 <c>Kick=38</c>、<c>Lean</c>=3.2°、<c>Squash</c>=0.93，
    /// 三个轴一起动（位移 + 仰角 + 压扁），任何一条被游戏自己的动画覆盖，另外两条还在。</para>
    /// </summary>
    private static void Recoil(object? dealer, Vector2 dir)
    {
        if (dealer == null)
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

            // 生物节点可能是 Control（NCreature）也可能是 Node2D，两种都得认
            if (raw is not Control and not Node2D)
            {
                LogOnce($"[后坐力] 跳过：生物节点是 {raw.GetType().Name}，既不是 Control 也不是 Node2D");
                return;
            }

            ulong id = raw.GetInstanceId();
            ulong now = Time.GetTicksMsec();

            if (_lastRecoilMs.TryGetValue(id, out ulong last) && (now - last) < 300)
            {
                return;
            }

            _lastRecoilMs[id] = now;

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

            void Apply(Vector2 p, float r, Vector2 s)
            {
                switch (raw)
                {
                    case Control c:
                        c.Position = p;
                        c.Rotation = r;
                        c.Scale = s;
                        break;

                    case Node2D n2:
                        n2.Position = p;
                        n2.Rotation = r;
                        n2.Scale = s;
                        break;
                }
            }

            const float Kick = 38f;
            const float Life = 0.22f;
            const float LeanDeg = 3.2f;
            const float Squash = 0.93f;

            // 她朝 dir 开枪 ⇒ 她往后仰的旋转方向取决于是向右还是向左开
            float lean = (dir.X >= 0f ? -1f : 1f) * Mathf.DegToRad(LeanDeg);

            FieldFx.Animate(raw, Life, k =>
            {
                // 前 20% 猛地后退，后面 OutCubic 弹回
                float e = k < 0.20f
                    ? k / 0.20f
                    : 1f - FieldFxEase.OutCubic((k - 0.20f) / 0.80f);

                Apply(
                    pos - (dir * (Kick * e)),
                    rot0 + (lean * e),
                    new Vector2(scale0.X * (1f - ((1f - Squash) * e)), scale0.Y));
            });

            LogOnce($"[后坐力] 命中：node={raw.GetType().Name} pos={pos} kick={Kick:F0} lean={LeanDeg:F1}°");
        }
        catch (Exception e)
        {
            LogOnce($"[后坐力] {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// 受击方反应：敌人挨打时"顿一下再抖回来" + 一团白闪 + 轻微压扁。
    /// <para>★ 2026-10-20 用户要求"受击方反应也可以加"。这一层比再加光点有效，
    /// 因为它把"打到了"这件事<b>演在被打的人身上</b>。</para>
    /// <para>位移用衰减正弦（4 个来回），0.20 秒内精确回到原位；白闪是一颗加色圆，
    /// 不去改节点的 Modulate（改材质容易和游戏自己的受击着色打架）。</para>
    /// </summary>
    private static void HitReact(object? target, Vector2 dir, Control layer, bool heavy)
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

            float amp = heavy ? 16f : 10f;
            const float Life = 0.20f;

            FieldFx.Animate((Node)raw, Life, k =>
            {
                // 4 个来回，幅度按 (1-k)² 衰减
                float env = (1f - k) * (1f - k);
                float wobble = Mathf.Sin(k * Mathf.Tau * 2f) * amp * env;
                Apply(pos + (dir * wobble), new Vector2(scale0.X * (1f - (0.05f * env)), scale0.Y * (1f + (0.03f * env))));
            });

            // 白闪：目标胸口一颗加色圆，0.09 秒收掉
            Vector2 at = ChestPos(target);

            if (at != Vector2.Zero && layer != null)
            {
                Node2D root = FieldFx.Root(layer, at, "HitFlash");
                Polygon2D flash = FieldFx.Poly(root, FieldFx.Circle(heavy ? 46f : 34f, 22),
                    new Color(1f, 0.97f, 0.90f, 0.55f));

                FieldFx.Animate(flash, 0.09, k =>
                {
                    flash.Scale = Vector2.One * Mathf.Lerp(1.25f, 0.35f, FieldFxEase.OutCubic(k));
                    flash.Modulate = new Color(1f, 1f, 1f, 1f - k);
                });

                FieldFx.FreeAfter(root, 0.20);
            }
        }
        catch
        {
            // 受击反应失败不影响其它表现
        }
    }

    private static readonly System.Collections.Generic.Dictionary<ulong, ulong> _lastRecoilMs = new();

    /// <summary>
    /// 气流线：弹道两侧各一条、中间一条，从枪口方向"嗖"地划过（0.14 秒）。
    /// <para>比加粗光柱更"动画"：观众看到的是<b>运动的线</b>，不是一根静止的亮条。</para>
    /// </summary>
    private static void SnipeAirLines(Control layer, Vector2 from, Vector2 to)
    {
        Node2D root = FieldFx.Root(layer, from, "SnipeAir");
        Vector2 span = to - from;
        Vector2 perp = new Vector2(-span.Y, span.X).Normalized();

        for (int i = 0; i < 3; i++)
        {
            float off = (i - 1) * 30f;
            Vector2 a = (span * 0.12f) + (perp * off);
            Vector2 b = (span * 0.88f) + (perp * off);

            Line2D ln = FieldFx.Line(root, new[] { a, b },
                new Color(SnipeHot.R, SnipeHot.G, SnipeHot.B, 0.38f), 1.8f);

            float delay = 0.02f * i;

            FieldFx.After(root, delay, "[狙击·气流]", () =>
            {
                FieldFx.Animate(ln, 0.14, k =>
                {
                    ln.Modulate = new Color(1f, 1f, 1f, 1f - k);
                });
            });
        }

        FieldFx.FreeAfter(root, 0.40);
    }

    /// <summary>
    /// 沿途火星：弹道经过的几处依次亮一下很小的火星（延后到弹头飞到那里才亮）。
    /// <para>比"整条线一起闪"更像有东西高速擦过去。</para>
    /// </summary>
    private static void SnipePathSparks(Control layer, Vector2 from, Vector2 to, float delay)
    {
        for (int i = 0; i < 4; i++)
        {
            float f = Mathf.Lerp(0.18f, 0.90f, i / 3f);
            Vector2 at = from.Lerp(to, f);
            Node2D root = FieldFx.Root(layer, at, "SnipePathSpark");

            FieldFx.After(root, delay * f, "[狙击·沿途]", () =>
            {
                try
                {
                    GpuParticles2D p = FieldFx.Sparks(root, SnipeWarm, amount: 6,
                        speedMin: 40f, speedMax: 170f, lifetime: 0.22f,
                        gravity: 260f, scaleMin: 0.06f, scaleMax: 0.16f);
                    FieldFx.Ignite(p);
                }
                catch
                {
                    // 表现层失败不外抛
                }
            });

            FieldFx.FreeAfter(root, delay + 0.5);
        }
    }

    /// <summary>
    /// 一发弹道。<b>画的是相对枪口的位移</b>（父节点 <c>SnipeTraceRoot</c> 已经被定位到枪口），
    /// 所以这里一行坐标换算都不用做。
    /// </summary>
    private sealed partial class SnipeTracer : Node2D
    {
        internal Vector2 Span;
        internal bool Marked;

        private float _t;

        /// <summary>
        /// ⚠️ <b>必须显式开 process</b>：运行期 new 出来的节点在本工程实测不会自动跑 <c>_Process</c>
        /// （见 <c>CardFieldVfx.BulletMode.cs</c> / 巨兽眼睛 那两处的 <c>SetProcess(true)</c>）。
        /// 少了这一行，<c>_t</c> 恒为 0 ⇒ 弹头钉在枪口、拖尾长度为 0 ⇒ 看起来"没有子弹"。
        /// </summary>
        public override void _Ready()
        {
            SetProcess(true);
            QueueRedraw();
        }

        public override void _Process(double delta)
        {
            // 夹一下 delta：卡帧时别让弹头一步瞬移到终点（那样就成了"闪一下"，没有"飞"）
            _t += Mathf.Min((float)delta, 0.033f);

            if (_t >= SnipeTracerLife)
            {
                QueueFree();
                return;
            }

            QueueRedraw();
        }

        public override void _Draw()
        {
            float travel = Mathf.Clamp(_t / SnipeTravel, 0f, 1f);

            // 弹头位置：OutCubic ⇒ 出膛极快、到点前收住（枪的口径感）
            Vector2 head = Span * FieldFxEase.OutCubic(travel);

            float after = _t - SnipeTravel;
            float fade = after <= 0f
                ? 1f
                : Mathf.Clamp(1f - after / Mathf.Max(0.01f, SnipeTracerLife - SnipeTravel), 0f, 1f);

            float w = Marked ? 1.35f : 1f;

            // ★ 2026-10-20 用户："轨迹现在太平了，就是一条细线。可以做成的有动态"
            //   ⇒ 三层定宽 DrawLine 已经搬进 <see cref="EnergyStreak"/>（锥形 + 行波 + 流光 + 流动断口），
            //   这里只留"弹头"本体：一颗亮到发白的圆，末尾跟着一截极短的实心尾（速度感）。
            if (head.LengthSquared() > 1f)
            {
                Vector2 back = head - (head.Normalized() * Mathf.Min(head.Length(), 26f * w));

                DrawLine(back, head, new Color(1f, 1f, 1f, 0.55f * fade), 5.0f * w, true);
            }

            // 弹头本体：一颗亮到发白的圆（加色混合下这个点最抓眼）
            DrawCircle(head, ((Marked ? 7.0f : 5.5f) * fade) + 0.8f,
                new Color(1f, 1f, 1f, 0.98f * fade));
        }
    }
}
