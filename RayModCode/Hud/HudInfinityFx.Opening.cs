using System;
using Godot;

namespace RayMod.Hud;

/// <summary>
/// 开场那团烟雾的**加色层**（<c>BlendMode.Add</c>）。
///
/// <para><b>为什么要单独一层</b>：烟雾要的是"发光的烟"（加色，和黑底预览的 <c>screen</c> 合成一致），
/// 而本节点其余部分（∞ 字形 / 柔光）一直是**普通 alpha 混合**、且用户已经按那个观感验收过 ——
/// 直接把整节点的材质改成 Add 会把已经定稿的那几拍一起改亮。
/// Godot 里 <c>material</c> 只作用于**该 CanvasItem 自己的绘制**（子节点另有材质），
/// 所以只给这一层加 Add 是安全的。</para>
/// </summary>
internal sealed partial class HudInfinitySmokeLayer : Control
{
    /// <summary>
    /// 真正画烟的回调（在 <see cref="HudInfinityFx"/> 里）。
    ///
    /// <para>⚠️ <b>参数必须是这一层自己</b>：Godot 的 `DrawTexture` / `DrawSetTransform` 会画到
    /// **调用它的那个 CanvasItem** 上，而且只有该节点正在 `_Draw` 里时才有权画。
    /// 所以"在子层里画、却调父节点的方法"会得到
    /// <c>Drawing is only allowed inside NOTIFICATION_DRAW</c> 并且**什么都不画**
    /// —— 这正是本层第一版的 bug（编译、部署、`-Verify` 全过，画面上却一个烟都没有）。</para>
    /// </summary>
    internal Action<HudInfinitySmokeLayer>? Painter;

    public override void _Draw()
    {
        Painter?.Invoke(this);
    }
}

/// <summary>
/// <see cref="HudInfinityFx"/> 的**开场段** —— 2026-10-19 <b>重写</b>。
///
/// <para><b>用户原话（这一轮重做的起因）</b>：
/// "很多很多问题。我都想重做。<b>∞散开成粒子飘过去，然后 ui 就生硬的变成了 ∞。太诡异了</b>"</para>
///
/// <para><b>旧实现为什么必然"生硬"</b>（三条病根，全是结构性的、调参救不回来）：</para>
/// <list type="number">
/// <item><b>两套几何</b>：粒子 ∞ 的半宽按<b>控件宽</b>算（<c>w × 0.117</c>）、落点小 ∞ 按<b>牌子宽</b>算
///   （<c>plateW × 0.34</c>）、字形贴图按<b>字号</b>算 —— 三个数互不相干，交接那刻必然错位。
///   更狠的是<b>胖瘦也不同</b>：双纽线原生高宽比 0.287，而贴图墨迹是 0.5547，扁了一半。</item>
/// <item><b>时间不重叠</b>：粒子残影在 2.40s 淡完、字形贴图 3.16s 才弹出 ⇒ 中间 <b>0.76 秒画面里没有 ∞</b>，
///   然后凭空冒出 —— 这就是"啪"的一声。</item>
/// <item><b>没有承接过程</b>：粒子是"一团烟"、贴图是"一根硬线"，中间少"烟凝成线"那一拍。</item>
/// </list>
///
/// <para><b>现在的主线</b>（对应上面三条）：</para>
/// <list type="number">
/// <item>唯一的 <see cref="InfinityShape"/>：粒子的落点、字形的绘制、凝结的目标**全部**读它
///   （由 <see cref="HudInfinityFx.TryGetGlyphShape"/> 从字号派生）。</item>
/// <item>时间线**重叠**：<see cref="GlyphInAt"/>–<see cref="GlyphInEnd"/> 这一段**两者同框**，
///   字形 alpha 0→1 的同时粒子 alpha 1→0.35。</item>
/// <item>粒子不是"淡出"，而是**朝曲线收拢 + 缩小**（<see cref="SuckEnd"/>）⇒ 读成"聚成一条线"。</item>
/// </list>
///
/// <para><b>并且彻底删掉了"横跨屏幕飞"</b>：粒子一开始就<ins>聚在牌子那团 ∞ 的周围</ins>
/// （按同一形状<b>放大 <see cref="GrowMax"/> 倍</b>散开），然后<b>径向收进曲线</b>。
/// 全程不出牌子附近几十像素 —— 用户嫌的"飘过去"没有了。</para>
///
/// <para>⚠️ <b>整套运动是 <c>_t</c> 的纯函数</b>（无状态、逐帧重算）：这样逐帧不会累积误差，
/// 也和预览脚本可以逐帧对上（预览就是 <c>f(t)</c>）。</para>
/// </summary>
internal sealed partial class HudInfinityFx
{
    // ── 开场时间表（秒）——与 `_tools\make_vfx_gif.py` 的「无限」场景逐字一致 ──

    /// <summary>粒子最早出现（已经散在放大 3.4 倍的 ∞ 上）。</summary>
    internal const float CloudStart = 0.00f;

    /// <summary>最后一颗粒子落到曲线上（密度 100%）。</summary>
    internal const float CloudEnd = 0.30f;

    /// <summary>★ UI ∞ 开始进画 —— 与粒子<b>同框</b>的起点。</summary>
    internal const float GlyphInAt = 0.30f;

    /// <summary>★ 粒子不再是"主体"（alpha 到 0.35 的余量）；字形已经 alpha 1。</summary>
    internal const float GlyphInEnd = 0.55f;

    /// <summary>粒子被完全"吸进"曲线（位置收拢 + 缩到 0.2 + alpha 0）。</summary>
    internal const float SuckEnd = 0.75f;

    // ── 粒子行为 ──
    /// <summary>出生态的散开倍数（上限 / 下限）—— 粒子从"放大 N 倍的同一个 ∞"上往回收。</summary>
    private const float GrowMax = 3.40f;
    private const float GrowMin = 1.70f;

    /// <summary>错开：每颗粒子的出现时刻 = delay × 它（delay ∈ 0~0.21 ⇒ 0~0.038s）。</summary>
    private const float StaggerSpan = 0.18f;

    /// <summary>每颗粒子自己从生到落的时间（秒）。</summary>
    private const float FlyTime = 0.16f;

    /// <summary>粒子贴图的绘制宽度 = <c>sf × 半宽 × 它</c>（sf ∈ 0.10~0.24）。</summary>
    private const float SizeGain = 3.20f;

    /// <summary>侧向绕行（占半宽的比例，随落定衰减到 0）—— 给"收敛"一点旋涡感。</summary>
    private const float SwirlFrac = 0.55f;

    /// <summary>被吸进曲线时，位置再往中心收这个比例（"吸进去"不只是变透明）。</summary>
    private const float SuckInFrac = 0.06f;

    /// <summary>拖影采样间隔（秒）—— 太近会糊进弹体里看不见。</summary>
    private const float TrailStep = 0.055f;

    /// <summary>第 1 个拖影的不透明度（第 g 个再 /g）。</summary>
    private const float TrailAlpha = 0.35f;

    /// <summary>烟的颜色（暖白 —— 和 ∞ 同一个色系；加色层里颜色 >1 就是更亮）。</summary>
    private static readonly Color SmokeTint = new(1f, 0.925f, 0.769f);
    private const float SmokeGain = 1.25f;

    /// <summary>三张烟贴图（`fx_smoke_1~3`，由生成器从素材库搬进包）。</summary>
    private readonly Texture2D?[] _smoke = new Texture2D?[3];

    private HudInfinitySmokeLayer? _smokeLayer;
    private bool _loggedOpening;
    private bool _smokeCleared;

    /// <summary>建加色层（在 <see cref="_Ready"/> 里调；它是本节点的**第一个子节点** ⇒ 画在最下面）。</summary>
    private void BuildSmokeLayer()
    {
        for (int i = 0; i < _smoke.Length; i++)
        {
            _smoke[i] = ResourceLoader.Load<Texture2D>($"{Entry.ResPath}/images/vfx/fx_smoke_{i + 1}.png");
        }

        if (_smoke[0] == null)
        {
            // 贴图没进包就整段开场跳过（数值与后面的编排不受影响），绝不报错刷屏
            Log.Warn("[无限·HUD] 烟雾贴图缺失 ⇒ 跳过开场；查 pck 是否包含 images/vfx/fx_smoke_1~3.png"
                      + "（新加的 png 必须跑过一次 godot --headless --import 才会进包）");
            return;
        }

        var layer = new HudInfinitySmokeLayer
        {
            Name = "Smoke",
            MouseFilter = MouseFilterEnum.Ignore,
            Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
        };

        AddChild(layer);
        layer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        layer.Painter = DrawSmoke;
        _smokeLayer = layer;
    }

    /// <summary>每帧请加色层重画（**只在开场窗口内** —— 别让它在整场战斗里空转）。</summary>
    private void RequestSmokeRedraw()
    {
        if (_smokeLayer == null || !GodotObject.IsInstanceValid(_smokeLayer))
        {
            return;
        }

        if (_t < SuckEnd + 0.05f)
        {
            _smokeLayer.QueueRedraw();
            return;
        }

        if (!_smokeCleared)
        {
            // ★ 收尾必须**再画一次空的**：Godot 的 CanvasItem 会**保留上一次的绘制指令**，
            //   停止 QueueRedraw 不等于清屏 ⇒ 最后一帧那点没淡干净的残影会一直挂在屏幕上
            //   （直到下回合节点销毁）。这一次 DrawSmoke 走早退分支 = 什么都不画。
            _smokeCleared = true;
            _smokeLayer.QueueRedraw();
        }
    }

    /// <summary>
    /// 开场那团烟（<c>_t</c> 的纯函数）—— <paramref name="canvas"/> 就是加色层自己（见那里关于绘制目标的说明）。
    /// </summary>
    private void DrawSmoke(HudInfinitySmokeLayer canvas)
    {
        if (_t >= SuckEnd + 0.05f)
        {
            return;                                     // 吸完了 ⇒ 本层不再画
        }

        float w = canvas.Size.X;

        if (w < 8f || canvas.Size.Y < 8f)
        {
            return;
        }

        // ★★★ 唯一的几何来源：粒子摆点 / 收拢目标 / 字形贴图**读的是同一个 shape**
        if (!TryGetGlyphShape(out InfinityShape shape, out _))
        {
            return;
        }

        if (!_loggedOpening)
        {
            // 铁律 35：日志要打**实际用到的东西**（好用来核对 ToLocal 那次换算）
            _loggedOpening = true;
            Log.Info($"[无限·HUD] 开场：∞ 圆心 (局部) {shape.Center} 半宽 {shape.HalfW:F1}"
                     + $" 半高 {shape.HalfH:F1}；牌子 (局部) {_plate.Position} {_plate.Size}"
                     + $"（全局 {ResourceHud.Ammo.Root?.GlobalPosition}）");
        }

        for (int i = 0; i < HudInfinitySmokeSpec.Count; i++)
        {
            if (BuildParticle(i, shape, out SmokeParticle sp, out Texture2D? tex) && tex != null)
            {
                DrawOne(canvas, sp, tex, w);
            }
        }
    }

    /// <summary>一颗粒子的一帧（拖影就是拿 t 往前退几毫秒再算一遍）。</summary>
    private void DrawOne(HudInfinitySmokeLayer canvas, in SmokeParticle sp, Texture2D tex, float w)
    {
        for (int g = 0; g < 3; g++)
        {
            float t = _t - TrailStep * g;

            if (t <= 0f || !Sample(sp, t, out Vector2 pos, out float size, out float alpha, out float deg))
            {
                continue;
            }

            if (g > 0)
            {
                alpha *= TrailAlpha / g;                 // 拖影比本体淡
                size *= 1.02f;
            }

            if (size < 0.6f || pos.X < -w * 0.5f || pos.X > w * 1.5f)
            {
                continue;                                // 出画面就不画了（省绘制）
            }

            DrawTexOn(canvas, tex, pos, size, Mathf.DegToRad(deg),
                      new Color(SmokeTint.R, SmokeTint.G, SmokeTint.B, alpha), SmokeGain);
        }
    }

    // ── 逐粒子弹道 ────────────────────────────────────────────────────────

    /// <summary>一颗粒子的**派生量**（出生点 / 落点 / 自己的时刻 / 自转…）。每帧算一次、三次采样复用。</summary>
    private readonly struct SmokeParticle
    {
        internal readonly Vector2 Birth;    // 出生：同一个 ∞ 放大 Grow 倍
        internal readonly Vector2 Home;     // 落点：曲线上的家
        internal readonly Vector2 Swirl;    // 侧向绕行量（落定时衰减到 0）
        internal readonly Vector2 Center;   // ∞ 的圆心（被"吸进去"时朝它收）
        internal readonly float Sf;
        internal readonly float HalfW;
        internal readonly float A0;
        internal readonly float Rot;
        internal readonly float Spin;
        internal readonly float T0;
        internal readonly float T1;

        internal SmokeParticle(Vector2 birth, Vector2 home, Vector2 swirl, Vector2 center,
                               float sf, float halfW, float a0, float rot, float spin,
                               float t0, float t1)
        {
            Birth = birth;
            Home = home;
            Swirl = swirl;
            Center = center;
            Sf = sf;
            HalfW = halfW;
            A0 = a0;
            Rot = rot;
            Spin = spin;
            T0 = t0;
            T1 = t1;
        }
    }

    private bool BuildParticle(int i, in InfinityShape shape, out SmokeParticle sp, out Texture2D? tex)
    {
        float[] p = HudInfinitySmokeSpec.Particles;
        int b = i * HudInfinitySmokeSpec.Stride;

        tex = null;
        sp = default;

        float ux = p[b + HudInfinitySmokeSpec.Ux];
        float uy = p[b + HudInfinitySmokeSpec.Uy];
        float sf = p[b + HudInfinitySmokeSpec.Sf];
        float rot = p[b + HudInfinitySmokeSpec.Rot];
        float spin = p[b + HudInfinitySmokeSpec.Spin];
        float a0 = p[b + HudInfinitySmokeSpec.Alpha];
        int texIdx = Mathf.Clamp((int)p[b + HudInfinitySmokeSpec.Tex], 1, 3);
        float delay = p[b + HudInfinitySmokeSpec.Delay];
        float arc = p[b + HudInfinitySmokeSpec.Arc];
        float om = p[b + HudInfinitySmokeSpec.Om];

        float theta = Mathf.Tau * i / HudInfinitySmokeSpec.Count;
        Vector2 home = shape.PointAt(theta);

        // 出生 = 把"家"从 ∞ 圆心径向放大 Grow 倍 —— 形状还是同一个 ∞，只是更大
        // （⇒ 收拢是**纯径向向内**的，不会出现"朝外散"那种读反）
        float grow = Mathf.Lerp(GrowMax, GrowMin, Mathf.Clamp((om - 0.33f) / 0.50f, 0f, 1f));
        Vector2 birth = shape.Center + (home - shape.Center) * grow
                        + new Vector2(ux, uy) * (shape.HalfW * 6f);   // 一点点不均，别像整齐的放大动画

        // 侧向绕行：从家指向"外"的垂直方向 —— 收拢时带一点旋涡感
        Vector2 radial = home - shape.Center;
        Vector2 perp = radial.LengthSquared() > 0.0001f
            ? new Vector2(-radial.Y, radial.X).Normalized()
            : Vector2.Up;
        Vector2 swirl = perp * (arc * shape.HalfW * SwirlFrac);

        float t0 = CloudStart + delay * StaggerSpan;

        sp = new SmokeParticle(birth, home, swirl, shape.Center, sf, shape.HalfW,
                               a0, rot, spin, t0, t0 + FlyTime);
        tex = _smoke[texIdx - 1];
        return tex != null;
    }

    /// <summary>某一时刻的位置 / 尺寸 / 不透明度 / 自转角（**度**）。</summary>
    private bool Sample(in SmokeParticle sp, float t,
                        out Vector2 pos, out float size, out float alpha, out float deg)
    {
        deg = sp.Rot + sp.Spin * t;

        float baseSize = sp.Sf * sp.HalfW * SizeGain;

        if (t < sp.T0)
        {
            pos = sp.Birth;
            size = baseSize * 0.35f;                 // 刚冒出来还没长大
            alpha = 0f;
            return false;                            // 还没出生 ⇒ 不画（alpha 0 在 DrawTexOn 里也会被跳掉）
        }

        if (t < sp.T1)
        {
            // ① 出生 → 落到曲线（自己那一趟，彼此错开）
            float k = Smoothstep((t - sp.T0) / Mathf.Max(0.05f, sp.T1 - sp.T0));
            pos = sp.Birth.Lerp(sp.Home, k) + sp.Swirl * Mathf.Sin(k * Mathf.Pi);
            size = baseSize * Mathf.Lerp(0.45f, 1f, k);
            alpha = sp.A0 * k;
            return alpha > 0.004f;
        }

        float glyphK = Mathf.Clamp((t - GlyphInAt) / Mathf.Max(0.05f, GlyphInEnd - GlyphInAt), 0f, 1f);
        float suckK = Mathf.Clamp((t - GlyphInEnd) / Mathf.Max(0.05f, SuckEnd - GlyphInEnd), 0f, 1f);
        float suck = Smoothstep(suckK);

        // ② 同框段：停在曲线上，alpha 随字形一起降（字形在涨、它俩叠着 ⇒ 读成"烟凝成线"）
        // ③ 被吸进去：**朝 ∞ 圆心**收一小段 + 缩到 0.2 + 淡到 0（不是原地淡出，是"吸走"）
        pos = sp.Home.Lerp(sp.Center, SuckInFrac * suck);
        size = baseSize * Mathf.Lerp(1f, 0.20f, suck);
        alpha = sp.A0 * Mathf.Lerp(1f, 0.35f, Smoothstep(glyphK)) * (1f - suck);
        return alpha > 0.004f && suckK < 1f;
    }
}
