using System;
using Godot;

namespace RayMod.Hud;

/// <summary>
/// 开场那团烟雾的**加色层**（<c>BlendMode.Add</c>）。
///
/// <para><b>为什么要单独一层</b>：烟雾要的是"发光的烟"（加色，和黑底预览的 <c>screen</c> 合成一致），
/// 而本节点其余部分（刀光 / ∞ 字形 / 柔光）一直是**普通 alpha 混合**、且用户已经按那个观感验收过 ——
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
/// <see cref="HudInfinityFx"/> 的**开场段**（2026-10-13）：屏幕中央由**粒子烟雾组成一个 ∞**，
/// 然后**每颗粒子各飞各的**落到弹药 HUD 的"上限"位置上，聚成一个小 ∞ —— 接原来的编排
/// （上限变异 → 刀劈 → 真 ∞）。
///
/// <para><b>用户原话</b>："感觉屏幕中间浮现一个粒子烟雾组成的 ∞ 然后飞向弹药 hub，之后在接下来的动画会很帅。"
/// 看了第一版预览后追加："<b>粒子肯定要散开飞向，整个平移太不美观了</b>"。</para>
///
/// <para><b>所以第一版为什么被否</b>：那一版所有粒子骑在**同一条双纽线**上、整团插值平移 + 缩放
/// ⇒ 读起来是"一张烟图在滑"（用户说的"整个平移"）。这一版每颗粒子有
/// <b>自己的出发点 / 控制点 / 目的点 / 出发延迟 / 飞行时长</b>，先沿自己的弧线**散开**、再汇聚。</para>
///
/// <para><b>粒子档案来自生成器</b>：<c>Hud\HudInfinityFx.SmokeSpec.cs</c>
/// （由 <c>_tools\make_inf_smoke_spec.py</c> 生成，预览工具读的是同一份 JSON）——
/// 所以"预览里看到的"和"游戏里放的"是同一批数字，见 §S37.7.3。</para>
///
/// <para>⚠️ <b>整套运动是 <c>_t</c> 的纯函数</b>（无状态、逐帧重算）：这样逐帧不会累积误差，
/// 也和预览脚本可以逐帧对上（预览就是 <c>f(t)</c>）。</para>
/// </summary>
internal sealed partial class HudInfinityFx
{
    // ── 开场时间表（秒）——与 `_tools\make_inf_smoke_spec.py` 的 TIME 一致 ──
    /// <summary>中央烟雾 ∞ 成形（粒子出现是**错开**的，见 Delay）。</summary>
    internal const float FormEnd = 0.60f;

    /// <summary>定住一拍 —— 给"这就是一个 ∞"读出来的时间。</summary>
    internal const float HoldEnd = 0.88f;

    /// <summary>最后一颗粒子到位（**整段飞行 ≈1.2s**；用户明确说 0.38s 那版"太快了"）。</summary>
    internal const float FlyEnd = 2.05f;

    /// <summary>落定后"小 ∞"残影的淡出时长（和 <see cref="FlyEnd"/> 一起决定收尾）。</summary>
    internal const float SettleFade = 0.35f;

    // ── 几何（**一律按比例**，换分辨率不能歪）──
    private const float BigCenterXFrac = 0.50f;          // 大 ∞ 圆心（占控件宽）
    private const float BigCenterYFrac = 0.43f;          // 0.43 = 屏幕中间略偏上（避开手牌）
    private const float BigScaleFrac = 0.117f;           // 大 ∞ 半宽（占控件宽）—— 2559 下 = 300px
    private const float SmallScaleFracOfPlate = 0.34f;   // 落点小 ∞ 半宽（占牌子宽）
    private const float LandXFracOfPlate = 0.78f;        // 小 ∞ 圆心 = 真 ∞ 字形的落点
    private const float LandYFracOfPlate = 0.77f;
    private const float JitterFrac = 0.35f;              // 落点抖动（占小 ∞ 半宽）
    private const float DepartFrac = 1.00f;              // 出发延迟 = delay × 它（⇒ 错开 0~0.22s）
    private const float DriftFrac = 2.40f;               // 刚出现时的散开量（越小越"直接出现"）
    private const float LandSizeFrac = 0.75f;            // 落定尺寸 = sf × 小 ∞ 半宽 × 它
    private const float TrailStep = 0.055f;              // 拖影采样间隔（秒）—— 太近会糊进弹体里看不见
    private const float TrailAlpha = 0.35f;              // 第 1 个拖影的不透明度（第 g 个再 /g）

    /// <summary>烟的颜色（暖白 —— 和刀光/∞ 同一个色系；加色层里颜色 >1 就是更亮）。</summary>
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

        if (_t < FlyEnd + SettleFade)
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

    /// <summary>开场那团烟（<c>_t</c> 的纯函数）—— <paramref name="canvas"/> 就是加色层自己（见那里关于绘制目标的说明）。</summary>
    private void DrawSmoke(HudInfinitySmokeLayer canvas)
    {
        if (_t >= FlyEnd + SettleFade)
        {
            return;                                     // 残影也淡完了 ⇒ 本层不再画
        }

        float w = canvas.Size.X;
        float h = canvas.Size.Y;

        if (w < 8f || h < 8f || _plate.Size.X < 4f)
        {
            return;
        }

        float bigScale = w * BigScaleFrac;
        var bigCenter = new Vector2(w * BigCenterXFrac, h * BigCenterYFrac);
        float smallScale = _plate.Size.X * SmallScaleFracOfPlate;
        var landCenter = new Vector2(_plate.Position.X + _plate.Size.X * LandXFracOfPlate,
                                     _plate.Position.Y + _plate.Size.Y * LandYFracOfPlate);

        if (!_loggedOpening)
        {
            // 铁律 35：日志要打**实际用到的东西**（含全局坐标，好用来核对 ToLocal 那次换算）
            _loggedOpening = true;
            Log.Info($"[无限·HUD] 开场：烟雾 ∞ 圆心 (局部) {bigCenter} 半宽 {bigScale:F0}"
                     + $" → 落点 (局部) {landCenter} 小 ∞ 半宽 {smallScale:F0}"
                     + $"；牌子 (局部) {_plate.Position} {_plate.Size}（全局 {ResourceHud.Ammo.Root?.GlobalPosition}）");
        }

        for (int i = 0; i < HudInfinitySmokeSpec.Count; i++)
        {
            if (BuildParticle(i, bigCenter, bigScale, landCenter, smallScale,
                              out SmokeParticle sp, out Texture2D? tex) && tex != null)
            {
                DrawOne(canvas, sp, tex, w);
            }
        }

        DrawLandFlash(canvas, landCenter, smallScale);
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

    /// <summary>落点那一记"归位"闪光（烟聚成小 ∞ 的瞬间）。</summary>
    private void DrawLandFlash(HudInfinitySmokeLayer canvas, Vector2 at, float smallScale)
    {
        float k = Mathf.Clamp((_t - FlyEnd) / 0.34f, 0f, 1f);

        if (_t < FlyEnd - 0.02f || k >= 1f)
        {
            return;
        }

        float e = Smoothstep(k);
        DrawTexOn(canvas, _glow, at, smallScale * Mathf.Lerp(3.2f, 0.9f, e), 0f,
                  new Color(1f, 0.84f, 0.55f, 0.80f * (1f - e)), 1.5f);
        DrawTexOn(canvas, _flare, at, smallScale * 1.6f, 0f,
                  new Color(1f, 1f, 1f, 0.85f * (1f - e)), 1.4f);
    }

    // ── 逐粒子弹道 ────────────────────────────────────────────────────────

    /// <summary>
    /// 一颗粒子的**派生量**（出发点 / 控制点 / 目的点 / 自己的出发时刻与时长 / 自转…）。
    /// 每帧算一次、三次采样（本体 + 两个拖影）复用。
    /// </summary>
    private readonly struct SmokeParticle
    {
        internal readonly Vector2 Big;      // 它在双纽线上的位置（成形阶段就停在这附近）
        internal readonly Vector2 Offset;   // 相对弧长点的偏移（固定档案里的 ux/uy × 大 ∞ 半宽）
        internal readonly Vector2 P0;       // 弹道起点（= Big + Offset）
        internal readonly Vector2 Ctrl;     // ★ 二次贝塞尔的控制点 —— "各飞各的"就靠它
        internal readonly Vector2 P1;       // 目的点（小 ∞ 上的一个点 + 抖动）
        internal readonly float Sf;
        internal readonly float BigScale;
        internal readonly float SmallScale;
        internal readonly float A0;
        internal readonly float Rot;
        internal readonly float Spin;
        internal readonly float Delay;
        internal readonly float T0;
        internal readonly float T1;

        internal SmokeParticle(Vector2 big, Vector2 offset, Vector2 p0, Vector2 ctrl, Vector2 p1,
                               float sf, float bigScale, float smallScale, float a0, float rot,
                               float spin, float delay, float t0, float t1)
        {
            Big = big;
            Offset = offset;
            P0 = p0;
            Ctrl = ctrl;
            P1 = p1;
            Sf = sf;
            BigScale = bigScale;
            SmallScale = smallScale;
            A0 = a0;
            Rot = rot;
            Spin = spin;
            Delay = delay;
            T0 = t0;
            T1 = t1;
        }
    }

    private bool BuildParticle(int i, Vector2 bigCenter, float bigScale,
                               Vector2 landCenter, float smallScale,
                               out SmokeParticle sp, out Texture2D? tex)
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
        float dur = p[b + HudInfinitySmokeSpec.Dur];
        float jx = p[b + HudInfinitySmokeSpec.Jx];
        float jy = p[b + HudInfinitySmokeSpec.Jy];
        float om = p[b + HudInfinitySmokeSpec.Om];

        float theta = Mathf.Tau * i / HudInfinitySmokeSpec.Count;
        Vector2 big = Lemniscate(theta, bigCenter, bigScale);
        Vector2 offset = new Vector2(ux, uy) * bigScale;
        Vector2 p0 = big + offset;

        // "散开"的方向 = 从 ∞ 中心指向它自己；侧向用 arc（带符号）⇒ 每颗绕自己那一侧
        Vector2 outward = big - bigCenter;
        outward = outward.LengthSquared() > 0.0001f ? outward.Normalized() : Vector2.Right;
        var perp = new Vector2(-outward.Y, outward.X);
        Vector2 ctrl = p0 + outward * (om * bigScale) + perp * (arc * bigScale);

        Vector2 p1 = Lemniscate(theta, landCenter, smallScale)
                     + new Vector2(jx, jy) * (smallScale * JitterFrac);

        float t0 = HoldEnd + delay * DepartFrac;

        sp = new SmokeParticle(big, offset, p0, ctrl, p1, sf, bigScale, smallScale,
                               a0, rot, spin, delay, t0, t0 + dur);
        tex = _smoke[texIdx - 1];
        return tex != null;
    }

    /// <summary>某一时刻的位置 / 尺寸 / 不透明度 / 自转角（**度**）。</summary>
    private static bool Sample(in SmokeParticle sp, float t,
                               out Vector2 pos, out float size, out float alpha, out float deg)
    {
        deg = sp.Rot + sp.Spin * t;

        if (t < sp.T0)
        {
            // ① 成形：错开出现，边出现边往自己的弧长点上"凝"（drift 1 → 0）
            float denom = Mathf.Max(0.05f, FormEnd - sp.Delay * FormEnd);
            float gk = Smoothstep((t - sp.Delay * FormEnd) / denom);
            pos = sp.Big + sp.Offset * ((1f - gk) * DriftFrac);
            size = sp.Sf * sp.BigScale * (0.55f + 0.45f * gk);
            alpha = sp.A0 * gk;
            return alpha > 0.004f;
        }

        if (t <= sp.T1)
        {
            // ② 飞行：自己的弧线（贝塞尔）—— 尺寸收到"落点该有的大小"
            float fk = Smoothstep((t - sp.T0) / Mathf.Max(0.05f, sp.T1 - sp.T0));
            pos = Bezier(sp.P0, sp.Ctrl, sp.P1, fk);
            size = Mathf.Lerp(sp.Sf * sp.BigScale, sp.Sf * sp.SmallScale * LandSizeFrac, fk);
            alpha = sp.A0 * Mathf.Lerp(1f, 0.30f, fk);
            return alpha > 0.004f;
        }

        // ③ 落定：留在小 ∞ 的位置上当残影，整团**同时**淡出（形状一起消失）
        pos = sp.P1;
        size = sp.Sf * sp.SmallScale * LandSizeFrac;
        alpha = sp.A0 * 0.30f * Mathf.Clamp(1f - (t - FlyEnd) / SettleFade, 0f, 1f);
        return alpha > 0.004f;
    }

    /// <summary>伯努利双纽线（**真的 ∞ 形状**，不是两个圆）——与预览脚本逐字一致。</summary>
    private static Vector2 Lemniscate(float theta, Vector2 center, float scale)
    {
        float s = Mathf.Sin(theta);
        float c = Mathf.Cos(theta);
        float d = 1f + s * s;

        return new Vector2(center.X + c / d * scale, center.Y + s * c / d * scale * 0.86f);
    }

    /// <summary>二次贝塞尔（<paramref name="t"/> 已由调用方 smoothstep 过）。</summary>
    private static Vector2 Bezier(Vector2 a, Vector2 c, Vector2 b, float t)
    {
        float u = 1f - t;

        return u * u * a + 2f * u * t * c + t * t * b;
    }
}
