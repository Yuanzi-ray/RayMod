using System;
using System.Collections.Generic;
using Godot;

namespace RayMod.Art;

/// <summary>
/// <b>「霰弹枪」（<see cref="RayMod.Cards.Shotgun"/>）的场上表现</b>（2026-10-20）。
///
/// <para><b>用户要求（原话）</b>：「霰弹枪。<b>有多少子弹就从不同方向触发击中敌人特效多少次</b>，
/// 敌人多的时候，方向要不一致（即随机）。」</para>
///
/// <para><b>机制对得上</b>：这张牌是 <c>WithHitCount(1 + 消耗的弹药数)</c> 配
/// <c>TargetingAllOpponents</c>，所以对 N 个敌人、m 层弹药 ⇒ 一共打出 <c>N × (1+m)</c> 段伤害。
/// 每次 <c>Hook.AfterDamageGiven</c> 就是"一颗霰弹打中了"。</para>
///
/// <para><b>2026-10-20 第五轮（用户画了张草图："子弹围成一圈。然后依次飞向敌人"）</b><br/>
/// 之前的毛病是<b>和狙击撞车</b> —— 两者都是"从她那边射出一道朝敌人的痕迹"。
/// 现在改成<b>先立靶、再发牌</b>：
/// <list type="number">
/// <item>她身前<i>弹出一圈子弹</i>（<see cref="ShotgunRing"/>，9 颗均匀排布 + 一圈极淡的导轨环）；</item>
/// <item>之后<b>每一段伤害</b>就从这个环上<b>拿一颗</b>出去打 —— 那颗在环上消失，
///       飞出的方向天然各不相同（环上 9 个点各朝不同方向），完全满足"方向要不一致"；</item>
/// <item>环上的弹陆续打完就自己淡出。</item>
/// </list>
/// 关键在于"环是源头"：读作「一圈弹药 → 一颗颗打光」，和狙击那条<b>单发一击</b>的光柱分得开。</para>
///
/// <para>⚠️ 全程零贴图。⚠️ <c>Polygon2D</c> 是 Node2D，<b>没有</b> <c>PivotOffset</c>
/// （那是 Control 的属性）⇒ 绕中心旋转要靠"顶点本身就以原点为中心"来保证。</para>
/// </summary>
internal partial class CardFieldVfx
{
    private static readonly Color ShotHot = new(1.000f, 0.949f, 0.820f);   // 命中闪光
    private static readonly Color ShotWarm = new(1.000f, 0.760f, 0.330f);  // 来袭亮痕 / 火星
    private static readonly Color ShotCase = new(0.831f, 0.651f, 0.294f);  // 黄铜弹壳

    /// <summary>命中点相对敌人中心的偏心（不要让所有弹都扎同一个像素）。</summary>
    private const float ShotRadius = 30f;

    /// <summary>没有环时的兜底行程（她站太远/定位失败时才用）。</summary>
    private const float ShotStreak = 128f;

    /// <summary>命中后的余韵时长。</summary>
    private const float ShotLife = 0.22f;

    /// <summary>黄金角：兜底方向用，递推出来两两互不相同（真随机会撞车）。</summary>
    private const float ShotGoldenAngle = 2.3999632f;

    /// <summary>环上排几颗弹。6 颗一圈每颗间距 ≈ 145px —— 打掉两三颗时剩下的不会显得"没打完"。</summary>
    private const int ShotRingPellets = 6;

    /// <summary>环的半径（世界像素）。比她的身高略宽 ⇒ 弹是"围着她"而不是"贴在她身上"。</summary>
    private const float ShotRingRadius = 146f;

    /// <summary>环在她身前抬多高（胸口往上一点，别和脚底的特效打架）。</summary>
    private const float ShotRingLift = -18f;

    /// <summary>最后一发之后环还要亮多久才淡出。★ 别太长：环久留会和"子弹轨迹"抢视线。</summary>
    private const float ShotRingLife = 0.42f;

    /// <summary>环淡出的时长。</summary>
    private const float ShotRingFade = 0.25f;

    /// <summary>连发计数（跨战斗不需要清零；<see cref="Play"/> 每次出牌会归零）。</summary>
    private static int _shotgunHitIndex;

    /// <summary>当前这一轮齐射的弹环。没有就是 null。</summary>
    private static ShotgunRing? _shotgunRing;

    /// <summary>这一轮已经打掉第几颗。</summary>
    private static int _shotgunRingSlot;

    /// <summary>上一轮齐射"开火"表演的时间戳（毫秒）。用来把顿帧/枪口爆限成每轮一次。</summary>
    private static ulong _lastShotgunBlastMs;

    /// <summary>
    /// 一段霰弹伤害的表现。<b>返回 true = 这张牌已被本方法接管</b>。
    /// </summary>
    internal static bool ShotgunHit(object cardSource, object? dealer, object target)
    {
        if (cardSource is not RayMod.Cards.Shotgun)
        {
            return false;
        }

        try
        {
            if (!Enabled || TestModeIsOn())
            {
                return true;
            }

            Vector2 targetPos = ChestPos(target);

            if (targetPos == Vector2.Zero)
            {
                LogOnce("[霰弹枪·表现] 定位失败：ChestPos(target) 为零");
                return true;
            }

            if (VfxParentForEffects() is not Control layer)
            {
                LogOnce("[霰弹枪·表现] 拿不到战斗特效容器");
                return true;
            }

            int i = _shotgunHitIndex++;
            Vector2 chestOfDealer = ChestPos(dealer);

            // 偏心：每颗弹落点错开 30px 以内，读作"霰弹打了一片"
            float angle = (i * ShotGoldenAngle) + ((GD.Randf() - 0.5f) * 0.55f);
            Vector2 scatter = new(Mathf.Cos(angle), Mathf.Sin(angle));
            Vector2 hitPoint = targetPos + scatter * ShotRadius;

            // ── 起点：优先从弹环上拿一颗；拿不到就退回"从远处直飞过来" ──
            Vector2 from;
            bool fromRing = false;

            if (chestOfDealer != Vector2.Zero)
            {
                if (_shotgunRing == null || !GodotObject.IsInstanceValid(_shotgunRing))
                {
                    _shotgunRing = SpawnShotgunRing(layer, chestOfDealer);
                    _shotgunRingSlot = 0;
                }

                if (_shotgunRing != null)
                {
                    int slot = _shotgunRingSlot++ % ShotRingPellets;
                    from = _shotgunRing.PelletPoint(slot);
                    _shotgunRing.Consume(slot);
                    _shotgunRing.PokeTtl(ShotRingLife);
                    fromRing = true;
                }
                else
                {
                    from = hitPoint - scatter * ShotStreak;
                }
            }
            else
            {
                from = hitPoint - scatter * ShotStreak;
            }

            ShotPellet(layer, from, hitPoint, i, target);

            // 「牌面被烫穿」：整轮齐射只演一次（第 1 发里做，碎片顺着这一发的方向飘）
            if (i == 0)
            {
                BurnPlayedCard(cardSource, scatter);
            }

            // ★ 整轮齐射只演一次"开火"：顿帧 + 枪口爆 + 后坐力 + 抛壳。
            //   逐发都演会卡成幻灯片。
            ulong now = Time.GetTicksMsec();

            if (now - _lastShotgunBlastMs > 400)
            {
                _lastShotgunBlastMs = now;

                try
                {
                    DoHitStop();

                    if (chestOfDealer != Vector2.Zero)
                    {
                        Vector2 toTarget = targetPos.X >= chestOfDealer.X ? new Vector2(1f, 0f) : new Vector2(-1f, 0f);
                        Vector2 gunMuzzle = chestOfDealer + (toTarget * 52f);
                        SnipeMuzzleFlash(layer, gunMuzzle, toTarget, false);
                        Recoil(dealer, toTarget);

                        // 抛壳：三枚弹壳从枪口崩出去（霰弹枪一轮只清一次膛）
                        for (int c = 0; c < 3; c++)
                        {
                            SnipeCasing(layer, gunMuzzle, toTarget.X);
                        }
                    }
                }
                catch
                {
                    // 表演层失败不外抛
                }
            }

            if (i == 0)
            {
                Diag("sgv", $"[霰弹枪·表现] 齐射开始：环={(fromRing ? "有" : "无")} 环心={chestOfDealer} 首发={from} → {hitPoint}");
            }

            try
            {
                RayMod.Audio.SfxPlayer.Play(RayMod.Audio.Sfx.Hit, null);
            }
            catch
            {
                // 音效失败不影响表现
            }
        }
        catch (Exception e)
        {
            LogOnce($"[霰弹枪·表现] {e.GetType().Name}: {e.Message}");
        }

        return true;
    }

    /// <summary>弹体轮廓：沿 +X 定义（鼻尖在 +12）。飞行弹和环上的弹共用同一形状。</summary>
    private static Vector2[] ShotPelletShape(float k) => new[]
    {
        new Vector2(12.0f, 0f) * k,
        new Vector2(4.0f, -5.0f) * k,
        new Vector2(-12.0f, -5.0f) * k,
        new Vector2(-12.0f, 5.0f) * k,
        new Vector2(4.0f, 5.0f) * k,
    };

    /// <summary>
    /// 立起这一轮齐射的弹环：一圈导轨 + <see cref="ShotRingPellets"/> 颗弹依次弹出。
    /// 环挂在战斗特效容器上（世界坐标），位置跟着"开火那一刻"的她固定不动 ——
    /// 弹是从那里打出去的，环本身不该跟着她走。
    /// </summary>
    private static ShotgunRing? SpawnShotgunRing(Control layer, Vector2 center)
    {
        try
        {
            var ring = new ShotgunRing
            {
                Name = "RayModShotgunRing",
            };

            layer.AddChild(ring);
            ring.GlobalPosition = center + new Vector2(0f, ShotRingLift);
            ring.Build(ShotRingRadius);
            FieldFx.FreeAfter(ring, ShotRingLife + ShotRingFade + 0.6f);
            Diag("sgr", $"[霰弹枪·表现] 弹环已立：心={ring.GlobalPosition} 半径={ShotRingRadius:F0} 颗数={ShotRingPellets} 父={layer.Name}");
            return ring;
        }
        catch (Exception e)
        {
            Diag("sgrx", $"[霰弹枪·表现] 弹环建不起来：{e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>一颗霰弹：从 <paramref name="from"/> 飞向 <paramref name="hitPoint"/>，命中后炸开。</summary>
    private static void ShotPellet(Control layer, Vector2 from, Vector2 hitPoint, int index, object? target)
    {
        Vector2 delta = hitPoint - from;
        float dist = delta.Length();

        if (dist < 2f)
        {
            delta = new Vector2(1f, 0f);
            dist = 1f;
        }

        Vector2 dir = delta / dist;

        // 飞行时长按距离给：环离她 132px、敌人可能在 900px 外，
        // 固定 0.11s 的话远距离就是一道瞬移的白线。
        float travel = Mathf.Clamp(dist / 2600f, 0.08f, 0.17f);

        Node2D root = FieldFx.Root(layer, hitPoint, "ShotgunPellet");

        Polygon2D pellet = FieldFx.Poly(root, ShotPelletShape(1f), ShotCase);
        pellet.Rotation = dir.Angle();
        pellet.Position = -delta;           // root 在命中点 ⇒ 起点 = -delta
        pellet.Modulate = new Color(1f, 1f, 1f, 0f);

        // 弹头铜色（和弹身同一个多边形，只是前段换色 ⇒ 一眼看得出"哪头是尖的"）
        Polygon2D nose = FieldFx.Poly(pellet,
            new[] { new Vector2(12.0f, 0f), new Vector2(4.0f, -5.0f), new Vector2(4.0f, 5.0f) },
            ShotWarm);

        // 飞行：InCubic ⇒ 越靠近越快（霰弹是"打过来"，不是"飘过来"）。
        // ★ 弹道必须是**直线**（用户 2026-10-20 明确："怎么做成曲线了。用直线啊"）。
        //   之前的"往上拱"是为了绕开中间那张打出的卡牌，但读起来就不像子弹了 ⇒ 拉直。
        //   轨迹的"动"靠锥形宽度 + 沿线流光 + 头部亮斑，不靠拐弯。

        // 尾迹：**动态轨迹**（锥形 + 极轻抖动 + 流光），直线走向。
        SpawnStreak(layer, from, hitPoint,
            growTime: travel,
            holdTime: 0.14f,
            fadeTime: 0.26f,
            thickWidth: 17f,
            haloWidth: 48f,
            waveAmp: 3f,
            core: ShotHot,
            glow: ShotWarm,
            pulses: 3);

        FieldFx.Animate(pellet, travel, k =>
        {
            float e = FieldFxEase.InCubic(k);
            pellet.Position = -delta * (1f - e);
            pellet.Modulate = new Color(1f, 1f, 1f, k < 0.25f ? k * 4f : 1f);
        });

        // 命中：星 + 闪核 + 环 + 火星（方向和来袭一致，读作"被打穿了"）
        FieldFx.After(root, travel, "[霰弹枪·命中]", () =>
        {
            try
            {
                pellet.QueueFree();

                float len = 42f;
                Vector2[] star =
                {
                    new(len, 0f),
                    new(0f, -len * 0.28f),
                    new(-len * 0.24f, 0f),
                    new(0f, len * 0.28f),
                };

                Polygon2D burst = FieldFx.Poly(root, star, ShotHot);
                burst.Rotation = dir.Angle() + ((index % 3) * 0.28f);

                FieldFx.Animate(burst, 0.18, k =>
                {
                    burst.Scale = Vector2.One * Mathf.Lerp(0.45f, 1.45f, FieldFxEase.OutCubic(k));
                    burst.Modulate = new Color(1f, 1f, 1f, 1f - k);
                });

                Polygon2D core = FieldFx.Poly(root, FieldFx.Circle(19f, 18), ShotHot);

                FieldFx.Animate(core, 0.10, k =>
                {
                    core.Scale = Vector2.One * Mathf.Lerp(1.1f, 0.2f, FieldFxEase.OutCubic(k));
                    core.Modulate = new Color(1f, 1f, 1f, 1f - k);
                });

                Line2D ring = FieldFx.Line(root, FieldFx.Circle(28f, 26),
                    new Color(ShotWarm.R, ShotWarm.G, ShotWarm.B, 0.90f), 4.2f, closed: true);

                FieldFx.Animate(ring, 0.22, k =>
                {
                    ring.Scale = Vector2.One * Mathf.Lerp(0.35f, 2.0f, FieldFxEase.OutCubic(k));
                    ring.Modulate = new Color(1f, 1f, 1f, 1f - k);
                });

                GpuParticles2D sparks = FieldFx.Sparks(root, ShotWarm, amount: 13,
                    speedMin: 110f, speedMax: 380f, lifetime: 0.30f,
                    gravity: 620f, scaleMin: 0.10f, scaleMax: 0.30f);

                // 默认发射方向是 (0,-1)；转到 dir 的反向 ⇒ 火星朝"被打穿的那一面"溅
                sparks.Rotation = dir.Angle() - Mathf.Pi * 0.5f;
                FieldFx.Ignite(sparks);

                // 实物层：弹孔（暖边）+ 崩出的黄铜碎屑 + 一小团烟
                BulletHole(root, 9f, 0.32f);
                BulletHole(root, 6.5f, 0.26f, new Vector2(Mathf.Lerp(-24f, 24f, GD.Randf()), Mathf.Lerp(-20f, 20f, GD.Randf())));
                BulletHole(root, 5.0f, 0.22f, new Vector2(Mathf.Lerp(-30f, 30f, GD.Randf()), Mathf.Lerp(-24f, 24f, GD.Randf())));
                DebrisShards(root, ShotCase, 14, 150f, 480f, 3.6f, 0.44f);
                DebrisShards(root, new Color(0.32f, 0.24f, 0.16f), 6, 90f, 280f, 2.8f, 0.50f);
                SmokePuff(root, new Color(0.68f, 0.64f, 0.60f, 0.38f), 16f, 0.46f, new Vector2(0f, -34f), 4);
                HitReact(target, dir, layer, false);
            }
            catch
            {
                // 表现层失败不外抛
            }
        });

        FieldFx.FreeAfter(root, travel + ShotLife + 0.35f);
    }

    /// <summary>
    /// 齐射弹环：她身前一圈子弹，一颗颗被"抽走"打向敌人。
    /// <para>环上的弹<b>不是装饰</b> —— 每一颗都对应一段真实伤害，
    /// 被打掉就在环上消失，所以"打了几发"这件事眼睛能数出来。</para>
    /// </summary>
    private sealed partial class ShotgunRing : Node2D
    {
        private readonly Polygon2D?[] _markers = new Polygon2D?[ShotRingPellets];
        private readonly bool[] _alive = new bool[ShotRingPellets];
        private Node2D? _ticks;
        private float _pulse;
        private float _radius = ShotRingRadius;
        private float _ttl = ShotRingLife;
        private float _fade = 1f;
        private float _t;

        /// <summary>第 <paramref name="slot"/> 颗弹在世界里的位置（发射点）。</summary>
        internal Vector2 PelletPoint(int slot)
        {
            float a = (float)(Mathf.Tau * slot / ShotRingPellets);
            return GlobalPosition + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * _radius;
        }

        internal void PokeTtl(float seconds)
        {
            _ttl = Mathf.Max(_ttl, seconds);
        }

        public override void _Ready()
        {
            // ★ 运行期 new 出来的节点在本工程不会自动开 process（GiantEye / BulletMode 都踩过）
            SetProcess(true);
        }

        internal void Build(float radius)
        {
            _radius = radius;
            int built = 0;

            // ── 导轨：一圈亮起来的环 + 一圈会转的短弧 ──
            //   只画一个静态圆读起来是"贴了个圆环当背景"；让它转 + 呼吸才是"弹药在这儿排着队"。
            Line2D guide = FieldFx.Line(this, FieldFx.Circle(radius, 64),
                new Color(ShotWarm.R, ShotWarm.G, ShotWarm.B, 0.34f), 2.6f, closed: true);
            guide.Modulate = new Color(1f, 1f, 1f, 0f);

            FieldFx.Animate(guide, 0.18, k =>
            {
                guide.Modulate = new Color(1f, 1f, 1f, 0.85f * k);
            });

            Line2D inner = FieldFx.Line(this, FieldFx.Circle(radius * 0.82f, 64),
                new Color(ShotHot.R, ShotHot.G, ShotHot.B, 0.14f), 1.4f, closed: true);
            inner.Modulate = new Color(1f, 1f, 1f, 0f);

            FieldFx.Animate(inner, 0.26, k =>
            {
                inner.Modulate = new Color(1f, 1f, 1f, k);
            });

            // 旋转的短弧：不是整圈，而是一圈"断续的刻度"，转起来才有速度感
            var ticks = new List<Vector2>();

            for (int t = 0; t < 6; t++)
            {
                float s = (float)(t * Mathf.Tau / 6f);

                for (int q = 0; q <= 6; q++)
                {
                    float a2 = s + (q * 0.026f);
                    ticks.Add(new Vector2(Mathf.Cos(a2), Mathf.Sin(a2)) * (radius * 1.10f));
                }

                ticks.Add(new Vector2(float.NaN, float.NaN));
            }

            List<Vector2[]> arcs = SplitNaN(ticks);

            foreach (Vector2[] arc in arcs)
            {
                Line2D seg = FieldFx.Line(this, arc,
                    new Color(ShotWarm.R, ShotWarm.G, ShotWarm.B, 0.55f), 2.2f);
                seg.Modulate = new Color(1f, 1f, 1f, 0f);

                FieldFx.Animate(seg, 0.24, k =>
                {
                    seg.Modulate = new Color(1f, 1f, 1f, k);
                });
            }

            _ticks = new Node2D { Name = "ShotgunRingTicks" };
            AddChild(_ticks);

            for (int slot = 0; slot < ShotRingPellets; slot++)
            {
                float a = (float)(Mathf.Tau * slot / ShotRingPellets);

                Vector2 home = new(Mathf.Cos(a), Mathf.Sin(a));

                Polygon2D marker = FieldFx.Poly(this, ShotPelletShape(1.15f), ShotCase);
                marker.Rotation = a;          // 弹头朝外（打出去的方向）
                marker.Position = home * (radius * 2.35f);
                marker.Scale = Vector2.Zero;

                // 弹头铜色（环上的弹也要一眼看出"这是子弹"）
                Polygon2D tip = FieldFx.Poly(marker,
                    new[] { new Vector2(13.8f, 0f), new Vector2(4.6f, -5.75f), new Vector2(4.6f, 5.75f) },
                    ShotWarm);

                Polygon2D glint = FieldFx.Poly(marker,
                    new[] { new Vector2(-13.8f, -1.1f), new Vector2(2.3f, -1.1f), new Vector2(2.3f, 0.3f), new Vector2(-13.8f, 0.3f) },
                    new Color(1f, 0.97f, 0.86f, 0.75f));

                _markers[slot] = marker;
                _alive[slot] = true;
                built++;

                // ★ 不是"在环上原地淡入"，而是**从外面飞进来落位**：
                //   静态弹出读起来像贴图，飞进来才有"弹药被拉过来排好队"的动作。
                float delay = 0.030f * slot;

                FieldFx.After(this, delay, "[霰弹枪·环]", () =>
                {
                    if (!GodotObject.IsInstanceValid(marker))
                    {
                        return;
                    }

                    FieldFx.Animate(marker, 0.22, k =>
                    {
                        float e = FieldFxEase.OutBack(k);
                        marker.Position = home * (radius * Mathf.Lerp(2.35f, 1f, e));
                        marker.Scale = Vector2.One * Mathf.Lerp(0.35f, 1f, Mathf.Clamp(k * 2.4f, 0f, 1f));
                    });
                });
            }

            Diag("sgb", $"[霰弹枪·表现] 环画好了：{built} 颗 半径={radius:F0} 世界位={GlobalPosition} 在树内={IsInsideTree()}");
        }

        /// <summary>把用 (NaN,NaN) 分隔的点列拆成几段（Godot 的 Line2D 不支持多段）。</summary>
        private static List<Vector2[]> SplitNaN(List<Vector2> src)
        {
            var result = new List<Vector2[]>();
            var cur = new List<Vector2>();

            foreach (Vector2 p in src)
            {
                if (float.IsNaN(p.X))
                {
                    if (cur.Count > 1)
                    {
                        result.Add(cur.ToArray());
                    }

                    cur = new List<Vector2>();
                    continue;
                }

                cur.Add(p);
            }

            if (cur.Count > 1)
            {
                result.Add(cur.ToArray());
            }

            return result;
        }

        /// <summary>这颗弹被打了出去：从环上消失（缩小 + 淡出 + 一记小闪光）。</summary>
        internal void Consume(int slot)
        {
            if (slot < 0 || slot >= ShotRingPellets || !_alive[slot])
            {
                return;
            }

            _alive[slot] = false;
            Polygon2D? marker = _markers[slot];
            Diag("sgc", $"[霰弹枪·表现] 取弹 slot={slot} 位置={PelletPoint(slot)} 标记={(marker == null ? "无" : "有")}");

            // 整圈被这一发顶得"跳"一下（环自己有 _pulse 驱动缩放）
            _pulse = 1f;

            // 出口闪一下：让人看见"是从这一格射出去的"
            float a = (float)(Mathf.Tau * slot / ShotRingPellets);
            Vector2 at = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * _radius;

            Polygon2D flash = FieldFx.Poly(this, FieldFx.Circle(9f, 14), ShotHot);
            flash.Position = at;

            FieldFx.Animate(flash, 0.16, k =>
            {
                flash.Scale = Vector2.One * Mathf.Lerp(0.55f, 2.8f, FieldFxEase.OutCubic(k));
                flash.Modulate = new Color(1f, 1f, 1f, 1f - k);
            });

            // 出口的一圈小爆发（加色圆环往外推）
            Line2D puff = FieldFx.Line(this, FieldFx.Circle(11f, 20),
                new Color(ShotWarm.R, ShotWarm.G, ShotWarm.B, 0.85f), 2.6f, closed: true);
            puff.Position = at;

            FieldFx.Animate(puff, 0.20, k =>
            {
                puff.Scale = Vector2.One * Mathf.Lerp(0.5f, 2.2f, FieldFxEase.OutCubic(k));
                puff.Modulate = new Color(1f, 1f, 1f, 1f - k);
            });

            if (marker != null && GodotObject.IsInstanceValid(marker))
            {
                FieldFx.Animate(marker, 0.10, k =>
                {
                    marker.Scale = Vector2.One * Mathf.Lerp(1f, 0.15f, k);
                    marker.Modulate = new Color(1f, 1f, 1f, 1f - k);
                });

                FieldFx.FreeAfter(marker, 0.16f);
            }
        }

        public override void _Process(double delta)
        {
            float dt = Mathf.Min((float)delta, 0.05f);
            _t += dt;
            _ttl -= dt;

            // 刻度在转、整圈在呼吸 —— 环活着才不会读成"贴了张图"
            if (_ticks != null && GodotObject.IsInstanceValid(_ticks))
            {
                _ticks.Rotation = (_t * 1.35f) % Mathf.Tau;
            }

            _pulse = Mathf.Max(0f, _pulse - (dt * 4.2f));
            float breathe = 1f + (0.022f * Mathf.Sin(_t * 3.4f)) + (_pulse * 0.10f);
            Scale = Vector2.One * breathe;

            if (_ttl <= 0f)
            {
                _fade -= dt / ShotRingFade;
                Modulate = new Color(1f, 1f, 1f, Mathf.Max(0f, _fade));

                if (_fade <= 0f)
                {
                    QueueFree();
                }
            }
        }
    }
}
