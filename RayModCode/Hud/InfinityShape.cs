using Godot;

namespace RayMod.Hud;

/// <summary>
/// <b>∞ 的唯一几何</b>（2026-10-19 重做「无限」时引入）。
///
/// <para><b>为什么必须有这个类型</b>：重做前的病根是"<b>粒子摆出来的是 A 尺寸、UI 贴图是 B 尺寸</b>"——
/// 大 ∞ 半宽按<b>控件宽</b>算（<c>w × 0.117</c>）、落点小 ∞ 半宽按<b>牌子宽</b>算（<c>plateW × 0.34</c>）、
/// 字形贴图又按<b>字号</b>算（<c>fontSize × 1.60</c>）—— 三个数互不相干，
/// 所以"烟凝成 ∞"那一刻两者**必然对不上**，"生硬"是数学必然，不是调参问题。</para>
///
/// <para><b>现在</b>：粒子、字形贴图、落定尺寸全部读<b>同一个</b> <see cref="InfinityShape"/>
/// （由 <c>HudInfinityFx.TryGetGlyphShape</c> 统一算出），后者只由<b>一个字号的倍数</b>派生
/// （铁律 80：单位只在"字号"这一处定义）。</para>
///
/// <para><b>为什么高宽比是 <see cref="InkAspect"/></b>：字形贴图 <c>RayMod_hud_infinity.png</c> 是 256×256，
/// 但里面那团墨迹实测 bbox = x 0..255 / y 57..198 ⇒ <b>256×142、完美居中、高宽比 0.5547</b>。
/// 而伯努利双纽线 <c>y = sin·cos/(1+sin²)</c> 的峰值只有 <c>1/3</c>、横轴峰值是 <c>1</c>
/// ⇒ 原生宽高比只有 <b>0.287</b>，比贴图<b>扁了一半</b>。
/// 所以这里必须把 y 方向乘 3 归一化、再乘 <see cref="InkAspect"/> —— 否则粒子曲线和贴图永远差一截胖瘦。</para>
/// </summary>
internal readonly struct InfinityShape
{
    /// <summary>字形贴图里 ∞ 墨迹的<b>高 / 宽</b>（实测 142 / 256）。</summary>
    internal const float InkAspect = 0.5547f;

    /// <summary>双纽线 y 向归一化：<c>sin·cos/(1+sin²)</c> 的峰值恰为 1/3。</summary>
    private const float LemniscatePeak = 3f;

    internal readonly Vector2 Center;

    /// <summary>半宽（= 墨迹宽度的一半 = 贴图绘制宽度的 0.5 倍）。</summary>
    internal readonly float HalfW;

    internal InfinityShape(Vector2 center, float halfW)
    {
        Center = center;
        HalfW = halfW > 1f ? halfW : 1f;
    }

    /// <summary>半高（由 <see cref="HalfW"/> 与 <see cref="InkAspect"/> 派生 —— 不许另一处再算）。</summary>
    internal float HalfH => HalfW * InkAspect;

    internal float FullW => HalfW * 2f;

    /// <summary>曲线上参数 <paramref name="theta"/> 处的点。</summary>
    internal Vector2 PointAt(float theta)
    {
        float s = Mathf.Sin(theta);
        float c = Mathf.Cos(theta);
        float d = 1f + s * s;

        // x 的极值 = HalfW；y 的极值 = HalfH（× LemniscatePeak 把那个 1/3 归一化掉）
        return new Vector2(Center.X + c / d * HalfW,
                           Center.Y + s * c / d * HalfH * LemniscatePeak);
    }

    /// <summary>
    /// 离 <paramref name="p"/> 最近的曲线点 —— "凝结"就是让每颗粒子朝它收拢。
    ///
    /// <para>采样 96 段：在这个尺寸（几十像素）下相邻采样点的间距远小于 1px，
    /// 人眼分不出"最近的采样点"和"数学最近点"，但省掉了牛顿迭代的收敛问题。</para>
    /// </summary>
    internal Vector2 Nearest(Vector2 p)
    {
        float best = float.MaxValue;
        Vector2 bestPoint = PointAt(0f);

        const int Samples = 96;

        for (int i = 0; i < Samples; i++)
        {
            Vector2 q = PointAt(Mathf.Tau * i / Samples);
            float d2 = q.DistanceSquaredTo(p);

            if (d2 < best)
            {
                best = d2;
                bestPoint = q;
            }
        }

        return bestPoint;
    }

    /// <summary>以 <paramref name="center"/> 为心、把点缩放到 `[0..1]` 的半宽单位（调试/日志用）。</summary>
    internal Vector2 Normalize(Vector2 p)
    {
        return new Vector2((p.X - Center.X) / HalfW,
                           (p.Y - Center.Y) / (HalfH * LemniscatePeak));
    }
}
