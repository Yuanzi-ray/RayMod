using System;
using Godot;

namespace RayMod.Art;

/// <summary>
/// ★ <b>场上特效的缓动函数库</b>——纯函数，无状态，可以被每帧调用几百次。
///
/// <para>设计参考：手写特效时，<b>缓动曲线决定了"贵不贵"</b>。线性插值（<c>Lerp(a,b,t)</c>）
/// 是所有"廉价感"的根源：真实的力有加速、有惯性、有过冲。所以这里不提供 Linear，
/// 逼着调用方每次都想一下"这一下该快还是该慢"。</para>
///
/// <para><b>为什么单独一个文件</b>：这些函数在 <see cref="FieldFx"/> 的每个时间轴里都会用到，
/// 抽出来才好统一调手感（比如整体把 OutExpo 换成 OutQuint 试试）。</para>
/// </summary>
internal static class FieldFxEase
{
    /// <summary>极快冲出、极慢收尾（t=0.3 时就到 0.97）。用于"光柱拔地而起""冲击波扩张"。</summary>
    internal static float OutExpo(float t)
    {
        return t >= 1f ? 1f : 1f - Mathf.Pow(2f, -10f * t);
    }

    /// <summary>极慢起步、极快收尾。<b>专门用来做淡出</b>——比线性淡出"突然断掉"的感觉好很多。</summary>
    internal static float InExpo(float t)
    {
        return t <= 0f ? 0f : Mathf.Pow(2f, 10f * t - 10f);
    }

    /// <summary>比 OutCubic 更"末尾有料"的减速，幂 5 次。</summary>
    internal static float OutQuint(float t) => 1f - Mathf.Pow(1f - t, 5f);

    internal static float OutCubic(float t) => 1f - Mathf.Pow(1f - t, 3f);

    internal static float InCubic(float t) => t * t * t;

    internal static float InOutCubic(float t)
    {
        return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;
    }

    internal static float InOutSine(float t) => (1f - Mathf.Cos(Mathf.Pi * t)) / 2f;

    internal static float OutSine(float t) => Mathf.Sin(t * Mathf.Pi / 2f);

    /// <summary>
    /// ★★ <b>过冲缓动</b>（back-out）：<b>会冲过 1.0 再弹回来</b>（峰值约 1.1）。
    ///
    /// <para>这是"物体出现"这一下最重要的手感来源：光点从无到有时如果只是线性放大，
    /// 看起来像"被拉开放大"；用过冲则是"啪一下弹出来"。P3 的 <c>Glimmer</c> 光球出场
    /// 就是 <c>OutBack(t/0.2)</c>。</para>
    ///
    /// <para>⚠️ <b>返回值可能 &gt; 1</b>，所以它只能用在"缩放 / 亮度"上，
    /// <b>不能直接喂给 alpha</b>（Color 的 a 分量会被夹在 1，但语义上很怪）。</para>
    /// </summary>
    internal static float OutBack(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f);
    }

    /// <summary>
    /// ★ <b>把归一化时间 <paramref name="t"/> 切出一段</b>：从 <paramref name="start"/> 起，
    /// 长 <paramref name="length"/>（都是 0~1 的比例），返回这一段内部 0~1 的进度。
    /// 段外返回 0 / 1。
    ///
    /// <para>这是"一条时间轴编排多个子效果"的核心工具：整段动画只有一个 tween 从 0 跑到 1，
    /// 里面每个子效果用 <c>Segment(t, 0.3, 0.2)</c> 取出自己那一小段。
    /// <b>好处是不用给每个子效果单独开 tween</b>，也就不会出现"各个 tween 起跑时间差一帧"
    /// 导致的抖动。</para>
    /// </summary>
    internal static float Segment(float t, float start, float length)
    {
        if (length <= 0f)
        {
            return t >= start ? 1f : 0f;
        }

        return Mathf.Clamp((t - start) / length, 0f, 1f);
    }

    /// <summary>
    /// <b>淡入 + 保持 + 淡出的包络</b>：0 → <paramref name="fadeIn"/> 渐亮，
    /// <paramref name="fadeOutStart"/> → 1 渐暗，中间恒为 1。
    /// 用于"东西出现了，停一会儿，再消失"这种最普通的生命周期。
    /// </summary>
    internal static float Envelope(float t, float fadeIn, float fadeOutStart)
    {
        if (t < fadeIn)
        {
            return fadeIn <= 0f ? 1f : t / fadeIn;
        }

        if (t > fadeOutStart)
        {
            return fadeOutStart >= 1f ? 0f : 1f - (t - fadeOutStart) / (1f - fadeOutStart);
        }

        return 1f;
    }

    /// <summary>
    /// ★★ <b>脉动串</b>：一组峰值时刻，取"离最近峰值的三角窗"的高度。
    ///
    /// <para>数学上就是 <c>max over peaks( clamp(1 - |t - peak| / width, 0, 1) )</c>。
    /// 这是让"停顿段不空"最便宜也最有效的办法：光球在这几拍上放大、光环闪、星芒亮，
    /// 拍与拍之间完全安静 —— 于是观众读到的是"它在积蓄力量"，而不是"卡住了"。</para>
    ///
    /// <para><paramref name="peaks"/> 是 0~1 的归一化时刻；<paramref name="width"/> 是三角窗半宽
    /// （0.08~0.15 比较合适，太小会闪得看不清，太大就连成一片不脉动了）。</para>
    /// </summary>
    internal static float Pulse(float t, float width, params float[] peaks)
    {
        float best = 0f;

        foreach (float peak in peaks)
        {
            float h = 1f - Mathf.Abs(t - peak) / Mathf.Max(width, 1e-4f);
            best = Mathf.Max(best, Mathf.Clamp(h, 0f, 1f));
        }

        return best;
    }

    /// <summary>二次贝塞尔取点——弧形收束、彗星尾迹的路径。</summary>
    internal static Vector2 Bezier(Vector2 from, Vector2 control, Vector2 to, float t)
    {
        float u = 1f - t;
        return u * u * from + 2f * u * t * control + t * t * to;
    }

    /// <summary>三次贝塞尔取点（S 形轨迹，收束时比二次更像"被吸过去"）。</summary>
    internal static Vector2 Bezier3(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1f - t;
        float u2 = u * u;
        float t2 = t * t;
        return u2 * u * p0 + 3f * u2 * t * p1 + 3f * u * t2 * p2 + t2 * t * p3;
    }

    /// <summary>在 [a, b] 之间做"带缓动"的插值。<paramref name="ease"/> 吃 0~1 回 0~1。</summary>
    internal static float Eased(float t, float a, float b, Func<float, float> ease)
    {
        return Mathf.Lerp(a, b, ease(Mathf.Clamp(t, 0f, 1f)));
    }
}
