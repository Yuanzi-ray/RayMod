using System;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using RayMod.Ammo;
using RayMod.Hud;

namespace RayMod.Art;

/// <summary>
/// <b>「无限」的世界层 —— 一条真实的弹链</b>（2026-10-20 用户："我们的特效要向动画的方向转变。
/// 用更加实际的元素来替代原始的光效和粒子"）。
///
/// <para><b>为什么不是发光线条</b>：第一版做的是"她胸口燃起一个大 ∞"（霓虹管 + 火星）。
/// 用户的口径很清楚：<b>光效和粒子要退位，用实体物件演一段动画</b>。
/// 所以现在 ∞ 的形状<b>由 34 颗真实黄铜子弹排出来</b>，它们沿曲线首尾相接地一直往前跑、
/// 永远跑不完 —— "弹药无限"这件事是<b>用子弹本身</b>说出来的，不是用光说出来的。</para>
///
/// <para><b>动画的三拍</b>：</para>
/// <list type="number">
/// <item><b>聚拢（0 → 0.40s）</b>：子弹从她脚下/两侧飞上来，<b>逐颗</b>卡进链里
///   （每颗自己有 0.20 秒的行程，起手时刻按顺序错开）—— 这是"装上弹链"，不是"淡入一张图"。</item>
/// <item><b>流动（0.40s → 本回合结束）</b>：整条链沿 ∞ 曲线流动，每隔半秒左右有一颗
///   <b>亮一下</b>（"这一发上膛了"），链条本身只有极淡的黄铜色，不抢视线。</item>
/// <item><b>余烬（收束之后）</b>：流速降到 1/3、亮度减半，一直挂着 —— 于是"本回合弹药没有上限"
///   是场上看得见的状态，而不是只有 HUD 那个小 ∞。</item>
/// </list>
///
/// <para><b>几何唯一性</b>：曲线点、切线、弧长参数化<b>全部</b>读 <see cref="InfinityShape"/>
/// （和 HUD 粒子、字形贴图同一个类型，铁律 80）。弧长表只建一次（<see cref="ArcLut"/>），
/// 否则 θ 均匀分布会让子弹在穿过点附近挤成一坨、在两端稀得看不出形状。</para>
/// </summary>
internal partial class CardFieldVfx
{
    // ── 尺寸（像素 · 1920 基准，整节点按 ViewportScale 等比缩放）──
    /// <summary>∞ 的半宽。全宽 = 2× = 336px，约为她身高的 1.1 倍。</summary>
    private const float InfHalfW = 168f;

    /// <summary>冲击波落地高度（相对胸口，向下为正）。</summary>
    private const float InfFootDrop = 148f;

    /// <summary>链上子弹数量。<b>34 颗</b>是"看得出是链、又不会挤成一条实线"的平衡点。</summary>
    private const int InfBeads = 34;

    /// <summary>单颗子弹的尺寸（沿切线方向的长度 / 宽度）。离线预演调过：21/7.2 太细，读成虚线。</summary>
    private const float InfBeadLen = 25f;
    private const float InfBeadWide = 8.6f;

    // ── 时间表（秒）──
    /// <summary>全部子弹卡进链里（对应 HUD 的 <c>CloudEnd = 0.30</c> 之后一点，给它收尾）。</summary>
    private const float InfRiseEnd = 0.40f;

    /// <summary>成形后的一次外扩脉冲。</summary>
    private const float InfPulseEnd = 0.58f;

    /// <summary>开始收束（对应 HUD 的 <c>RevealDone = 0.80</c> 之后）。</summary>
    private const float InfHoldEnd = 0.98f;

    /// <summary>收束完成 = 进入余烬态。</summary>
    private const float InfCollapseEnd = 1.26f;

    /// <summary>上限恢复后余烬的淡出时长。</summary>
    private const float InfFadeOut = 0.35f;

    /// <summary>兜底寿命（状态查询失灵时绝不允许它永久挂在场上）。</summary>
    private const float InfMaxLife = 40f;

    /// <summary>链条流速（归一化弧长 / 秒）—— 约 16 秒绕一圈，读得出"在动"但不晃眼。</summary>
    private const float InfFlowSpeed = 0.062f;

    /// <summary>余烬期的流速倍率。</summary>
    private const float InfEmberFlow = 0.34f;

    /// <summary>"上膛"高亮的间隔（秒）与持续（秒）。</summary>
    private const float InfGlintPeriod = 0.58f;
    private const float InfGlintLife = 0.26f;

    /// <summary>基础倾角（弧度，≈ -7°）—— 正水平的 ∞ 像 HUD 贴图，斜一点才像能量。</summary>
    private const float InfTilt = -0.1222f;

    private static InfinityWorld? _infinityWorld;

    // ── 调色板（取自她自己的卡图，`image_palette` 实测）──
    private const float InfCoreR = 1.000f, InfCoreG = 0.965f, InfCoreB = 0.859f;   // #FFF6DB 暖白（高光）
    private const float InfMidR = 0.957f, InfMidG = 0.800f, InfMidB = 0.169f;      // #F4CC2B 金黄
    private const float InfBrassR = 0.890f, InfBrassG = 0.678f, InfBrassB = 0.275f; // #E3AD46 黄铜

    /// <summary>弹壳身（比卡图的金黄暗一档，才像金属而不是塑料）。</summary>
    private const float InfCaseR = 0.831f, InfCaseG = 0.651f, InfCaseB = 0.294f;   // #D4A64B

    /// <summary>弹头（铜）。</summary>
    private const float InfTipR = 0.784f, InfTipG = 0.471f, InfTipB = 0.235f;      // #C8783C

    /// <summary>金属暗部 / 连线。</summary>
    private const float InfInkR = 0.204f, InfInkG = 0.157f, InfInkB = 0.086f;       // #342814

    /// <summary>
    /// ★ 起一次「无限」：实体弹链 + 音效 + 一记抖屏。由 <c>Cards\Infinite.OnPlay</c> 调用。
    ///
    /// <para>三道闸（便宜的先做）：总开关 → 本地玩家 → 战斗房间在不在。
    /// 多人的铁律：卡牌特写属于表现层，只有<b>本地玩家</b>才播（§S81/§S84）。</para>
    /// </summary>
    internal static void SpawnInfinityBurst(Player? owner)
    {
        try
        {
            if (!Enabled || owner == null || TestModeIsOn())
            {
                return;
            }

            if (!IsLocalPlayer(owner))
            {
                return;
            }

            object? creature = owner.Creature;

            if (creature == null)
            {
                return;
            }

            if (BackVfxParentForEffects() is not Control back)
            {
                LogOnce("[无限·世界] 拿不到 BackCombatVfxContainer，跳过世界层");
                return;
            }

            if (_infinityWorld != null && GodotObject.IsInstanceValid(_infinityWorld))
            {
                _infinityWorld.QueueFree();
            }

            Vector2 chest = ChestPos(creature);
            float scale = ViewportScale();

            var fx = new InfinityWorld
            {
                Name = "RayModInfinityWorld",
                Owner_ = owner,
                Scale = Vector2.One * scale,
                Material = FieldFx.Additive,
            };

            back.AddChild(fx);
            fx.GlobalPosition = chest;
            fx.Arm();
            _infinityWorld = fx;

            PlayInfinitySfx();
            ScreenShake("Weak", "Short");

            LogOnce($"[无限·世界] 已起：胸口 {chest}，{InfBeads} 颗子弹，半宽 {InfHalfW * scale:F0}px，缩放 {scale:F2}");
        }
        catch (Exception e)
        {
            LogOnce($"[无限·世界] 起效失败：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>视口宽 / 1920 —— 所有"像素级"常量都乘它，改分辨率不会变形（铁律 80）。</summary>
    private static float ViewportScale()
    {
        try
        {
            if (Engine.GetMainLoop() is SceneTree tree && tree.Root != null)
            {
                float w = tree.Root.GetVisibleRect().Size.X;
                return w > 1f ? Mathf.Clamp(w / 1920f, 0.6f, 2.2f) : 1f;
            }
        }
        catch
        {
            // 读不到就按 1:1
        }

        return 1f;
    }

    /// <summary>
    /// 一记"能量涌上来"的音效：直接用游戏自己的 <c>event:/sfx/ui/gain_energy</c>
    /// （玩家获得能量的那个 UI 音），语义正对"上限被打开"，也不用往 pck 里塞新音频。
    /// 走反射调 <c>SfxCmd.Play(string, float)</c>：本类不引用游戏类型。
    /// </summary>
    private static void PlayInfinitySfx()
    {
        try
        {
            if (!RayMod.Audio.VoiceState.SfxEnabled)
            {
                return;
            }

            Type? cmd = AccessTools_TypeByName("MegaCrit.Sts2.Core.Commands.SfxCmd");
            MethodInfo? play = cmd?.GetMethod("Play", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(float) }, null);

            play?.Invoke(null, new object[] { "event:/sfx/ui/gain_energy", 0.85f });
        }
        catch (Exception e)
        {
            LogOnce($"[无限·世界] 音效失败：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// <c>NGame.Instance.ScreenShake(ShakeStrength, ShakeDuration, float)</c> —— <b>只抖屏，不顿帧</b>。
    ///
    /// <para>为什么不复用本类已有的 <c>DoHitStop</c>：那个是"抖屏 + 顿帧"，顿帧会把正在播的
    /// <b>出牌动画</b>冻住；「无限」是一张给自己加状态的技能牌，没有命中、没有受击，
    /// 冻帧只会读成掉帧。</para>
    /// </summary>
    private static bool ScreenShake(string strengthName, string durationName)
    {
        try
        {
            Type? nGame = AccessTools_TypeByName("MegaCrit.Sts2.Core.Nodes.NGame");
            object? instance = nGame?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

            if (instance == null)
            {
                return false;
            }

            MethodInfo? target = null;

            foreach (MethodInfo m in nGame!.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "ScreenShake" && m.GetParameters().Length == 3)
                {
                    target = m;
                    break;
                }
            }

            if (target == null)
            {
                return false;
            }

            ParameterInfo[] ps = target.GetParameters();
            object? strength = Enum.Parse(ps[0].ParameterType, strengthName);
            object? duration = Enum.Parse(ps[1].ParameterType, durationName);

            target.Invoke(instance, new[] { strength, duration, -1f });
            return true;
        }
        catch (Exception e)
        {
            LogOnce($"[无限·世界] 抖屏失败：{e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  弧长表：让 34 颗子弹沿曲线**等距**，而不是按 θ 均分
    // ═══════════════════════════════════════════════════════════════════════

    private const int ArcLutN = 512;
    private static float[]? _arcLut;

    /// <summary>
    /// θ 均分 ≠ 弧长均分：双纽线在穿过点附近速度是两端的好几倍，
    /// 直接按 θ 摆子弹会看到"中间挤成一坨、两端稀得看不出形状"。所以先建一张
    /// 累计弧长表（只建一次），再把归一化弧长 <c>s</c> 反查回 θ。
    /// </summary>
    private static float[] ArcLut()
    {
        if (_arcLut != null)
        {
            return _arcLut;
        }

        var lut = new float[ArcLutN + 1];
        var unit = new InfinityShape(Vector2.Zero, 1f);

        Vector2 prev = unit.PointAt(0f);
        float acc = 0f;

        for (int i = 1; i <= ArcLutN; i++)
        {
            Vector2 p = unit.PointAt(Mathf.Tau * i / ArcLutN);
            acc += p.DistanceTo(prev);
            lut[i] = acc;
            prev = p;
        }

        float total = acc > 1e-6f ? acc : 1f;

        for (int i = 1; i <= ArcLutN; i++)
        {
            lut[i] /= total;
        }

        _arcLut = lut;
        return lut;
    }

    /// <summary>归一化弧长 → θ（二分 + 线性插值，512 段在这个尺寸下误差远小于 1px）。</summary>
    private static float ThetaAt(float s)
    {
        float[] lut = ArcLut();
        s = Mathf.PosMod(s, 1f);

        int lo = 0, hi = ArcLutN;

        while (lo + 1 < hi)
        {
            int mid = (lo + hi) >> 1;

            if (lut[mid] <= s)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        float span = Mathf.Max(1e-6f, lut[hi] - lut[lo]);
        float u = (s - lut[lo]) / span;

        return Mathf.Tau * (lo + u) / ArcLutN;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  世界层节点
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 她身后那条弹链。<b>整套运动是 <c>_t</c> 的纯函数</b>（无状态、逐帧重算），
    /// 掉帧不会累积误差，也方便和预览脚本逐帧对照。
    /// </summary>
    private sealed partial class InfinityWorld : Node2D
    {
        private const float BaseHalfW = InfHalfW;

        internal Player? Owner_;

        private float _t;
        private float _fade = 1f;
        private bool _retiring;
        private float _pollClock;

        /// <summary>缓存 <c>IsOverCapThisTurn</c>（每 0.2s 查一次，别每帧去碰存档）。</summary>
        private bool _overCap = true;

        private static Texture2D? _glowTex;

        internal void Arm()
        {
            SetProcess(true);
            _ = ArcLut();          // 预热弧长表（第一帧之后不再重建）
            QueueRedraw();
        }

        public override void _Process(double delta)
        {
            try
            {
                float dt = Mathf.Min((float)delta, 0.05f);
                _t += dt;

                // ── 跟随：她会小幅位移（受击抖动 / 待机呼吸），每帧重新贴一次胸口 ──
                Player? owner = Owner_;
                object? creature = owner?.Creature;

                if (owner == null || creature == null)
                {
                    Retire();
                }
                else if (!_retiring)
                {
                    GlobalPosition = ChestPos(creature);
                }

                // ── 状态轮询：上限恢复 = 这一回合结束 ⇒ 余烬淡出 ──
                if (!_retiring)
                {
                    _pollClock += dt;

                    if (_pollClock >= 0.20f)
                    {
                        _pollClock = 0f;

                        try
                        {
                            _overCap = AmmoCmd.IsOverCapThisTurn(owner);
                        }
                        catch
                        {
                            _overCap = true;   // 查不到就当还在，别提前收
                        }
                    }

                    if (!_overCap && _t >= InfCollapseEnd)
                    {
                        Retire();
                    }
                }

                if (_retiring)
                {
                    _fade -= dt / InfFadeOut;

                    if (_fade <= 0f || _t > InfMaxLife)
                    {
                        QueueFree();
                        return;
                    }
                }
                else if (_t > InfMaxLife)
                {
                    Retire();
                }

                QueueRedraw();
            }
            catch (Exception e)
            {
                LogOnce($"[无限·世界] _Process 失败：{e.GetType().Name}: {e.Message}");
                Retire();
            }
        }

        private void Retire()
        {
            if (_retiring)
            {
                return;
            }

            _retiring = true;
        }

        public override void _Draw()
        {
            float g = Glow();

            if (g <= 0.004f)
            {
                return;
            }

            // 整体压一个基础倾角 + 极轻摇摆。用"呼吸"而不是"旋转"：
            // 连续旋转会读成"一圈东西绕她公转"，那是子弹模式已经用掉的语汇（用户明确否过）。
            //
            // ⚠️ <c>DrawSetTransform</c> 是<b>绝对</b>变换（覆盖，不和外层叠加），
            // 所以倾角必须<b>显式传进每个绘制段</b>，不能只在这里设一次就以为全局生效
            // （第一版就是这里错：连线和子弹各转各的）。存到 <c>_sway</c> 里由各段自己用。
            _sway = InfTilt + Mathf.Sin(_t * 1.7f) * 0.028f;

            DrawShockwave();
            DrawChestGlow(g);
            DrawBelt(g);

            DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
        }

        /// <summary>本帧的整体倾角（弧度）。所有绘制段都必须显式用它。</summary>
        private float _sway;

        /// <summary>
        /// 总亮度包络：聚拢 0→1（<c>OutCubic</c>）→ 成形后一次外扩脉冲 → 收束到余烬 → 乘淡出。
        /// <b>所有分量都过这里</b>，别让某一段自己算 alpha。
        /// </summary>
        private float Glow()
        {
            float g = FieldFxEase.OutCubic(Mathf.Clamp(_t / InfRiseEnd, 0f, 1f));

            if (_t >= InfRiseEnd && _t < InfPulseEnd)
            {
                float pk = (_t - InfRiseEnd) / (InfPulseEnd - InfRiseEnd);
                g *= 1f + 0.10f * Mathf.Sin(pk * Mathf.Pi);
            }

            if (_t >= InfHoldEnd)
            {
                float ck = Mathf.Clamp((_t - InfHoldEnd) / (InfCollapseEnd - InfHoldEnd), 0f, 1f);
                g = Mathf.Lerp(1f, 0.42f, FieldFxEase.OutCubic(ck));

                if (ck >= 1f)
                {
                    g *= 1f + 0.10f * Mathf.Sin(Mathf.Tau * (_t - InfCollapseEnd) / 3.6f);
                }
            }

            return g * _fade;
        }

        /// <summary>当前半宽：聚拢时 0.55 → 1.0，收束时 1.0 → 0.62。</summary>
        private float HalfW()
        {
            float rise = Mathf.Lerp(0.55f, 1f, FieldFxEase.OutCubic(Mathf.Clamp(_t / InfRiseEnd, 0f, 1f)));

            if (_t >= InfHoldEnd)
            {
                float ck = Mathf.Clamp((_t - InfHoldEnd) / (InfCollapseEnd - InfHoldEnd), 0f, 1f);
                rise = Mathf.Lerp(1f, 0.62f, FieldFxEase.InOutSine(ck));
            }

            return BaseHalfW * rise;
        }

        /// <summary>流速：成形前不流（子弹还在归位），之后匀速，余烬期降到 1/3。</summary>
        private float FlowDistance()
        {
            if (_t <= InfRiseEnd)
            {
                return 0f;
            }

            float live = _t - InfRiseEnd;

            if (_t < InfHoldEnd)
            {
                return live * InfFlowSpeed;
            }

            float held = InfHoldEnd - InfRiseEnd;
            float ember = _t - InfHoldEnd;

            return held * InfFlowSpeed + ember * InfFlowSpeed * InfEmberFlow;
        }

        /// <summary>
        /// ★★ <b>弹链本体</b>：34 颗黄铜子弹沿 ∞ 等距排布，整体沿曲线流动。
        ///
        /// <para>每颗子弹：① 按弧长表定位（等距，不在穿过点挤成坨）；② 按<b>切线方向</b>转
        /// （否则会在拐弯处横七竖八）；③ 起手时从她的脚下飞上来、逐颗卡进链里
        /// （<c>k = 0</c> 时它还在半空，<c>k = 1</c> 时已经就位）—— 这就是"装弹链"这个动作本身。</para>
        /// </summary>
        private void DrawBelt(float g)
        {
            float halfW = HalfW();
            var shape = new InfinityShape(Vector2.Zero, halfW);

            float flow = FlowDistance();

            // 连线走"整体倾角"这一层（DrawLine 受当前 transform 影响）
            DrawSetTransform(Vector2.Zero, _sway, Vector2.One);

            // 链节连线（相邻两颗之间）—— 让 34 颗子弹读成"一条链"而不是"一堆散子弹"
            Vector2[] slots = new Vector2[InfBeads];
            float[] thetas = new float[InfBeads];

            for (int i = 0; i < InfBeads; i++)
            {
                float s = (i / (float)InfBeads + flow) % 1f;
                thetas[i] = ThetaAt(s);
                slots[i] = shape.PointAt(thetas[i]);
            }

            float linkA = 0.20f * g;
            var linkColor = new Color(InfCaseR, InfCaseG, InfCaseB, linkA);

            for (int i = 0; i < InfBeads; i++)
            {
                // 画出前 i 颗的位移程度：链越"晚成"，露出的连线越短
                if (Assemble(i) < 0.75f)
                {
                    continue;
                }

                Vector2 a = slots[i];
                Vector2 b = slots[(i + 1) % InfBeads];

                if (a.DistanceTo(b) > halfW * 1.2f)
                {
                    continue;   // 跨接处（曲线自交）不要拉长线
                }

                DrawLine(a, b, linkColor, Mathf.Max(1.2f, InfBeadWide * 0.22f), true);
            }

            // 子弹本体
            for (int i = 0; i < InfBeads; i++)
            {
                float assemble = Assemble(i);

                if (assemble <= 0.001f)
                {
                    continue;
                }

                // 起手：从脚下/两侧飞上来（每颗自己的偏移方向按索引错开，避免像整齐的列队）
                float ang = Mathf.Tau * (i * 0.618f);
                var from = new Vector2(Mathf.Cos(ang) * halfW * 0.85f, InfFootDrop * 1.15f + Mathf.Sin(ang) * 60f);
                Vector2 pos = from.Lerp(slots[i], FieldFxEase.OutCubic(assemble));

                // 切线方向（前后各取一点，稳健）
                Vector2 ahead = shape.PointAt(thetas[i] + 0.05f);
                Vector2 behind = shape.PointAt(thetas[i] - 0.05f);
                float rot = Mathf.Atan2(ahead.Y - behind.Y, ahead.X - behind.X);

                // "上膛"高亮：每隔 0.58 秒有一颗亮一下（顺序沿链条推进 ⇒ 读成"弹药在往前送"）
                float glintPhase = Mathf.PosMod(_t / InfGlintPeriod - i * 0.045f, 1f);
                float glint = glintPhase * InfGlintPeriod < InfGlintLife
                    ? 1f - glintPhase * InfGlintPeriod / InfGlintLife
                    : 0f;

                DrawBead(pos, rot, assemble, g, glint);
            }
        }

        /// <summary>第 <paramref name="i"/> 颗子弹的归位程度（0 = 还没出现，1 = 已在链上）。</summary>
        private float Assemble(int i)
        {
            // 34 颗在 0 ~ 0.20s 里依次出发，每颗自己飞 0.20s ⇒ 最后一颗正好在 0.40s（= InfRiseEnd）到齐。
            // 这个"错开"是整段动画的骨架：所有子弹同时飞 = 一坨，依次飞 = 装弹链。
            float start = 0.20f * (i / (float)InfBeads);
            const float Fly = 0.20f;

            return Mathf.Clamp((_t - start) / Fly, 0f, 1f);
        }

        /// <summary>
        /// 画一颗子弹（局部 +X 为弹头方向）：弹壳身 + 底缘 + 弹头。
        /// <b>纯实心多边形，没有一张贴图</b> —— 所以它在任何分辨率、任何缩放下都锐利。
        /// </summary>
        private void DrawBead(Vector2 pos, float rot, float assemble, float g, float glint)
        {
            float len = InfBeadLen;
            float wide = InfBeadWide;
            float tipLen = len * 0.30f;
            float caseLen = len - tipLen;

            // ⚠️ 绝对变换 ⇒ 倾角必须在这里自己补上（pos 是"未倾斜"的世界坐标）
            DrawSetTransform(pos.Rotated(_sway), rot + _sway, Vector2.One);

            float a = g * Mathf.Lerp(0.75f, 1f, assemble);

            // 弹壳身
            DrawRect(new Rect2(-len * 0.5f, -wide * 0.5f, caseLen, wide),
                     new Color(InfCaseR, InfCaseG, InfCaseB, a));

            // 底缘（稍宽、稍暗）
            DrawRect(new Rect2(-len * 0.5f, -wide * 0.58f, wide * 0.34f, wide * 1.16f),
                     new Color(InfInkR, InfInkG, InfInkB, a * 0.9f));

            // 抽壳槽（一道暗线，金属感全靠它）
            DrawLine(new Vector2(-len * 0.5f + wide * 0.55f, -wide * 0.5f),
                     new Vector2(-len * 0.5f + wide * 0.55f, wide * 0.5f),
                     new Color(InfInkR, InfInkG, InfInkB, a * 0.55f), Mathf.Max(1f, wide * 0.16f), true);

            // 弹头（铜）
            Vector2[] tip =
            {
                new(-len * 0.5f + caseLen, -wide * 0.5f),
                new(len * 0.5f, 0f),
                new(-len * 0.5f + caseLen, wide * 0.5f),
            };

            DrawColoredPolygon(tip, new Color(InfTipR, InfTipG, InfTipB, a));

            // 弹头高光（一条亮边，顺着上沿）
            DrawLine(new Vector2(-len * 0.5f + caseLen, -wide * 0.42f),
                     new Vector2(len * 0.5f - wide * 0.16f, -wide * 0.06f),
                     new Color(InfCoreR, InfCoreG, InfCoreB, a * 0.55f), Mathf.Max(1f, wide * 0.14f), true);

            // ★ "上膛"高亮：整颗子弹泛暖金 + 一圈短光晕（这一拍是动画节奏，不是粒子）
            if (glint > 0.01f)
            {
                float k = glint * g;

                DrawRect(new Rect2(-len * 0.5f, -wide * 0.5f, caseLen, wide),
                         new Color(InfMidR, InfMidG, InfMidB, 0.55f * k));

                DrawColoredPolygon(tip, new Color(InfCoreR, InfCoreG, InfCoreB, 0.45f * k));
                DrawCircle(Vector2.Zero, wide * 1.9f, new Color(InfMidR, InfMidG, InfMidB, 0.12f * k));
            }

            DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
        }

        /// <summary>
        /// 脚下那圈压扁的冲击波（<c>squashY = 0.40</c> 读作"贴地扩散"）：两圈不同相位，
        /// 只在起势后 0.55 秒内存在 —— 它是<b>唯一保留下来的"光"</b>，因为落地本身需要一个反馈。
        /// </summary>
        private void DrawShockwave()
        {
            const float Start = 0.02f;
            const float Life = 0.55f;

            float k = (_t - Start) / Life;

            if (k < 0f || k >= 1f)
            {
                return;
            }

            for (int ring = 0; ring < 2; ring++)
            {
                float rk = Mathf.Clamp(k - ring * 0.16f, 0f, 1f);

                if (rk <= 0f || rk >= 1f)
                {
                    continue;
                }

                float re = FieldFxEase.OutCubic(rk);
                float radius = Mathf.Lerp(28f, BaseHalfW * 1.35f, re);
                float a = (1f - rk) * (1f - rk) * (ring == 0 ? 0.55f : 0.30f) * _fade;

                Vector2[] pts = FieldFx.Circle(radius, 44, 0.40f);

                for (int i = 0; i < pts.Length; i++)
                {
                    pts[i] += new Vector2(0f, InfFootDrop);
                }

                DrawPolyline(pts, new Color(InfBrassR, InfBrassG, InfBrassB, a),
                    Mathf.Lerp(5f, 1.4f, re), true);
            }
        }

        /// <summary>
        /// 胸口那团极淡的暖光。<b>刻意压到 0.10</b>：这一版的主体是子弹，
        /// 光只是让子弹"有光源"，越亮越会把子弹洗成剪影（上一版就是这个问题）。
        /// </summary>
        private void DrawChestGlow(float g)
        {
            Texture2D? tex = GlowTex();

            if (tex == null)
            {
                return;
            }

            float hold = _t >= InfHoldEnd ? 0.5f : 1f;
            float size = BaseHalfW * 2.1f * (0.9f + 0.1f * g);
            Color c = new(1f, 0.86f, 0.52f, 0.10f * g * hold * _fade);

            DrawSetTransform(Vector2.Zero, 0f, Vector2.One * (size / Mathf.Max(1f, tex.GetWidth())));
            DrawTexture(tex, new Vector2(-tex.GetWidth() / 2f, -tex.GetHeight() / 2f), c);
            DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
        }

        private static Texture2D? GlowTex()
        {
            if (_glowTex != null && GodotObject.IsInstanceValid(_glowTex))
            {
                return _glowTex;
            }

            try
            {
                _glowTex = GD.Load<Texture2D>(Entry.ResPath + "/images/vfx/fx_glow_solid.png");
            }
            catch
            {
                // 加载不到就不画那一团光，子弹照画
            }

            return _glowTex;
        }

        public override void _ExitTree()
        {
            if (_infinityWorld == this)
            {
                _infinityWorld = null;
            }
        }
    }

    /// <summary>
    /// 启动自检：把世界层依赖的<b>三处反射</b>都解析一遍打进日志。
    ///
    /// <para>为什么必须做（铁律：反射摸陌生 API 先打成员表）——这一层没有 Harmony 补丁，
    /// 反射失败的症状是<b>打「无限」时场上什么都不发生</b>，日志里一个字也没有。</para>
    /// </summary>
    internal static void LogInfinityWorldProbe()
    {
        try
        {
            Type? room = AccessTools_TypeByName("MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom");
            bool back = room?.GetProperty("BackCombatVfxContainer", BindingFlags.Public | BindingFlags.Instance) != null;

            Type? cmd = AccessTools_TypeByName("MegaCrit.Sts2.Core.Commands.SfxCmd");
            bool sfx = cmd?.GetMethod("Play", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(float) }, null) != null;

            Type? nGame = AccessTools_TypeByName("MegaCrit.Sts2.Core.Nodes.NGame");
            bool shake = false;

            if (nGame != null)
            {
                foreach (MethodInfo m in nGame.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name == "ScreenShake" && m.GetParameters().Length == 3)
                    {
                        shake = true;
                        break;
                    }
                }
            }

            Log.Info($"[无限·世界] 软解析：BackVfxContainer={back} SfxCmd.Play={sfx} ScreenShake={shake} " +
                     $"⇒ 挂载={back && sfx && shake}（弹链 {InfBeads} 颗）");
        }
        catch (Exception e)
        {
            Log.Warn($"[无限·世界] 软解析失败（世界层不挂，HUD 那一路不受影响）：{e.GetType().Name}: {e.Message}");
        }
    }
}
