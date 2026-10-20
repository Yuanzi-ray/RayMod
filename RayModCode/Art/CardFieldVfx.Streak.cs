using System;
using Godot;

namespace RayMod.Art;

/// <summary>
/// <b>「有劲」的轨迹</b>（2026-10-20，用户："我觉得那啥轨迹现在太平了，就是一条细线。可以做成的有动态"）。
///
/// <para><b>为什么原来"平"</b>：霰弹枪的尾迹是一根 <c>Line2D</c>，两点、定宽、定色；
/// 狙击的光柱是三条 <c>DrawLine</c>，也是定宽直线。定宽直线在加色混合下就是一根头发丝 ——
/// 它没有厚度变化、没有速度感、没有随时间动的东西。</para>
///
/// <para><b>这一版怎么做的</b>：<b>三个 <see cref="Line2D"/> 子节点</b>（外晕 / 主体 / 内芯），
/// 每帧把「带行波的采样点」写进 <c>Points</c>，宽度靠 <c>WidthCurve</c> 做锥形，颜色靠
/// <c>Gradient</c> 做"尾部透明 → 头部炽白"。再叠 2~4 个沿轨迹跑动的亮斑。</para>
///
/// <para><b>踩过的两个坑（别再走）</b>：
/// <list type="number">
/// <item><c>DrawPolygon(points, colors)</c>（以及逐段四边形的写法）在这条链路上<b>完全画不出来</b>，
///   只有 <c>DrawPolyline</c> 那条 1px 细线可见 —— 这就是用户说的"就是一条细线"。</item>
/// <item>用固定 30 段采样一条 1800px 的轨迹 ⇒ 圆间距 60px，画出来是"一串小点"，
///   换成"叠圆笔刷"也不成立。宽度必须交给 <c>Line2D.WidthCurve</c>，不是自己拼几何。</item>
/// </list></para>
/// </summary>
internal partial class CardFieldVfx
{
    /// <summary>
    /// 生成一条动态轨迹节点。节点自己走完"长出 → 停留 → 淡出"三段后自杀，
    /// 所以调用方只管给参数，不用记着回收。
    /// </summary>
    internal static EnergyStreak? SpawnStreak(
        Control layer,
        Vector2 from,
        Vector2 to,
        float growTime,
        float holdTime = 0.06f,
        float fadeTime = 0.16f,
        float thickWidth = 7f,
        float haloWidth = 22f,
        float waveAmp = 7f,
        float bend = 0f,
        Color? core = null,
        Color? glow = null,
        int pulses = 3)
    {
        try
        {
            if (from.DistanceTo(to) < 6f)
            {
                return null;
            }

            var streak = new EnergyStreak
            {
                Name = "RayModEnergyStreak",
                Span = to - from,
                GrowTime = Mathf.Max(0.02f, growTime),
                HoldTime = holdTime,
                FadeTime = Mathf.Max(0.04f, fadeTime),
                ThickWidth = thickWidth,
                HaloWidth = haloWidth,
                WaveAmp = waveAmp,
                Bend = bend,
                Pulses = Mathf.Clamp(pulses, 0, 6),
            };

            if (core.HasValue)
            {
                streak.Core = core.Value;
            }

            if (glow.HasValue)
            {
                streak.Glow = glow.Value;
            }

            layer.AddChild(streak);
            streak.GlobalPosition = from;
            FieldFx.FreeAfter(streak, streak.GrowTime + streak.HoldTime + streak.FadeTime + 0.25f);
            return streak;
        }
        catch (Exception e)
        {
            Diag("streakx", $"[轨迹] 建不起来：{e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>一条会长的、会抖的、会流动的轨迹。</summary>
    internal sealed partial class EnergyStreak : Node2D
    {
        /// <summary>从原点到终点的位移（节点放在起点上）。</summary>
        internal Vector2 Span;

        internal float GrowTime = 0.08f;
        internal float HoldTime = 0.06f;
        internal float FadeTime = 0.16f;

        /// <summary>弹头那一端的宽度。</summary>
        internal float ThickWidth = 7f;

        /// <summary>外晕的宽度（要明显比主体粗，否则没有发光感）。</summary>
        internal float HaloWidth = 22f;

        /// <summary>行波的最大横向振幅（像素）。</summary>
        internal float WaveAmp = 7f;

        /// <summary>
        /// 整条轨迹的弯曲量（像素，沿 <c>perp</c> 方向，正数往 perp 正侧弯）。
        /// <para>用来"绕过挡在中间的那张打出的卡牌" —— 直线弹道会被卡面切断成两截。</para>
        /// </summary>
        internal float Bend;

        /// <summary>内芯近白色。</summary>
        internal Color Core = new(1f, 0.965f, 0.859f);

        /// <summary>主体暖金色。</summary>
        internal Color Glow = new(1f, 0.760f, 0.330f);

        /// <summary>沿线跑动的亮斑个数。</summary>
        internal int Pulses = 3;

        private Line2D? _halo;
        private Line2D? _body;
        private Line2D? _core;

        private float _t;

        public override void _Ready()
        {
            // ★ 运行期 new 出来的节点在本工程不会自动开 process（同类事故：GiantEye / BulletMode /
            //   SnipeTracer）。少了这行整条轨迹不动 = 又回到"一条细线"。
            SetProcess(true);

            _halo = MakeLine("StreakHalo", HaloWidth, new Color(Glow.R, Glow.G, Glow.B, 0.42f), 1.25f);
            _body = MakeLine("StreakBody", ThickWidth, new Color(Glow.R, Glow.G, Glow.B, 0.95f), 1f);
            _core = MakeLine("StreakCore", Mathf.Max(2.4f, ThickWidth * 0.36f),
                new Color(Core.R, Core.G, Core.B, 1f), 0.55f);

            QueueRedraw();
        }

        /// <summary>
        /// 造一条带锥形宽度 + 渐变色的 <see cref="Line2D"/>。
        /// <para><c>WidthCurve</c> 的 y 是"宽度倍率"（0..1 对应 0..Width），
        /// 这里让它从尾部 0.03 长到头部 1.0 ⇒ 一眼看出往哪边飞。</para>
        /// </summary>
        private Line2D MakeLine(string name, float width, Color color, float waveScale)
        {
            var curve = new Curve();
            curve.AddPoint(new Vector2(0f, 0.02f));
            curve.AddPoint(new Vector2(0.30f, 0.10f));
            curve.AddPoint(new Vector2(0.62f, 0.42f));
            curve.AddPoint(new Vector2(0.86f, 0.82f));
            curve.AddPoint(new Vector2(1f, 1f));

            var gradient = new Gradient();
            gradient.SetColor(0, new Color(color.R, color.G, color.B, 0f));
            gradient.SetColor(1, color);

            var line = new Line2D
            {
                Name = name,
                Points = new[] { Vector2.Zero, Vector2.One },
                Width = width,
                WidthCurve = curve,
                Gradient = gradient,
                JointMode = Line2D.LineJointMode.Round,
                BeginCapMode = Line2D.LineCapMode.Round,
                EndCapMode = Line2D.LineCapMode.Round,
                Antialiased = true,
                Material = FieldFx.Additive,
            };

            line.SetMeta("wave", waveScale);
            AddChild(line);
            return line;
        }

        public override void _Process(double delta)
        {
            _t += Mathf.Min((float)delta, 0.033f);

            if (_t >= GrowTime + HoldTime + FadeTime)
            {
                QueueFree();
                return;
            }

            UpdateLines();
            QueueRedraw();
        }

        /// <summary>沿路径取一点：位移 × f，再叠上横向行波。</summary>
        private Vector2 PointAt(float f, Vector2 perp, float t, float waveScale = 1f)
        {
            // 振幅随 f^1.6 增长 ⇒ 尾部（f→0）钉死、头部（f→1）甩得最厉害
            float amp = WaveAmp * waveScale * Mathf.Pow(Mathf.Clamp(f, 0f, 1f), 1.6f);
            float wave = Mathf.Sin((f * 2.4f * Mathf.Tau) - (t * 9f)) * amp;

            // 整体弯曲：两端为 0、中间最大（sin 包络），所以起点和终点都对得上
            float bow = Bend * Mathf.Sin(Mathf.Pi * Mathf.Clamp(f, 0f, 1f));

            return (Span * f) + (perp * (wave + bow));
        }

        private float Head()
        {
            float grow = Mathf.Clamp(_t / GrowTime, 0f, 1f);
            return FieldFxEase.OutCubic(grow);
        }

        private float Fade()
        {
            float after = _t - GrowTime;

            return after <= 0f
                ? 1f
                : Mathf.Clamp(1f - (after / Mathf.Max(0.01f, HoldTime + FadeTime)), 0f, 1f);
        }

        private void UpdateLines()
        {
            float head = Head();
            float fade = Fade();

            if (head <= 0.02f || fade <= 0.01f)
            {
                foreach (Line2D? l in new[] { _halo, _body, _core })
                {
                    if (l != null && GodotObject.IsInstanceValid(l))
                    {
                        l.Visible = false;
                    }
                }

                return;
            }

            Vector2 span = Span * head;
            float len = span.Length();

            if (len < 4f)
            {
                return;
            }

            Vector2 axis = span / len;
            Vector2 perp = new(-axis.Y, axis.X);

            // 点数跟着长度走（每 12px 一个点），否则波是折线不是曲线
            int count = Mathf.Clamp((int)(len / 12f) + 2, 6, 120);

            foreach (Line2D? l in new[] { _halo, _body, _core })
            {
                if (l == null || !GodotObject.IsInstanceValid(l))
                {
                    continue;
                }

                float waveScale = (float)l.GetMeta("wave", 1f);
                var pts = new Vector2[count];

                for (int i = 0; i < count; i++)
                {
                    float local = i / (float)(count - 1);
                    pts[i] = PointAt(local * head, perp, _t, waveScale);
                }

                l.Points = pts;
                l.Visible = true;

                // 逐层各自的淡出（外晕最先没）
                float a = fade * (l == _halo ? 0.85f : 1f);
                l.Modulate = new Color(1f, 1f, 1f, a);
            }
        }

        public override void _Draw()
        {
            float head = Head();
            float fade = Fade();

            if (head <= 0.02f || fade <= 0.01f)
            {
                return;
            }

            Vector2 span = Span * head;
            float len = span.Length();

            if (len < 4f)
            {
                return;
            }

            Vector2 axis = span / len;
            Vector2 perp = new(-axis.Y, axis.X);

            // ── 流光脉冲：能量在管里跑 ──
            for (int k = 0; k < Pulses; k++)
            {
                float phase = Mathf.PosMod((_t * 2.4f) + (k / (float)Mathf.Max(1, Pulses)), 1f);
                float f = phase * head;

                if (f <= 0.04f || f >= head)
                {
                    continue;
                }

                Vector2 p = PointAt(f, perp, _t);

                // 越靠头部越亮（能量正在被运到弹头上）
                float a = 0.28f + (0.55f * f);
                float r = 2.6f + (4.2f * Mathf.Sin(Mathf.Pi * f));

                DrawCircle(p, r, new Color(Core.R, Core.G, Core.B, a * fade * 0.85f));
                DrawCircle(p, r * 2.2f, new Color(Glow.R, Glow.G, Glow.B, a * 0.26f * fade));
            }
        }
    }
}
