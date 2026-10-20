using Godot;

namespace RayMod.Art;

/// <summary>
/// ★★ <b>场上特效的着色器库</b>——所有 shader 都是<b>代码里拼出来的字符串</b>，
/// 不依赖 <c>.gdshader</c> 资源文件（PCK 里少一类资源、也就少一类导出出错的可能）。
///
/// <para><b>为什么要着色器</b>：贴图精灵能做的只有"一个烤好的形状，缩放 + 改色"。
/// 一旦需要"环在扩张但线宽一直在变细""光柱内部有向上流动的条纹""爆开时画面轻微折射"，
/// 贴图就穷尽了 —— 这些都必须逐像素算。这也是本 mod 此前所有特效
/// "看着素"的根本原因：<b>形状是死的</b>。</para>
///
/// <para><b>统一约定</b>（所有 shader 都遵守，这样动画代码才统一）：</para>
/// <list type="bullet">
///   <item>参数一律用 <b>0~1 的归一化量</b>（<c>radius</c>/<c>alpha</c>…），
///         真实像素尺寸交给节点的 <c>Scale</c> —— 于是同一套动画在任何尺寸下都成立；</item>
///   <item>一律带 <c>tint</c>（<c>source_color</c>）和 <c>alpha</c> 两个参数，动画只改这两个；</item>
///   <item>光类一律 <c>render_mode blend_add</c>（叠加混合 = 发光）；</item>
///   <item>靠近四边形边缘的地方都乘一个 <c>edge</c> 衰减 ——
///         <b>否则环扩到边界会被方框裁出一条直边</b>，是这类特效最常见的穿帮。</item>
/// </list>
/// </summary>
internal static class FieldFxShaders
{
    private static Shader? _aa;
    private static Shader? _aaAdd;
    private static Shader? _smear;
    private static Shader? _bulletCore;
    private static Shader? _bulletStreak;
    private static Shader? _ring;
    private static Shader? _flash;
    private static Shader? _pillar;
    private static Shader? _magicCircle;
    private static Shader? _refract;
    private static Shader? _darkVeil;
    private static Shader? _lightFlood;
    private static Shader? _lightFloodAdd;
    private static Shader? _impact;
    private static Shader? _spectrum;
    private static Shader? _pinch;

    /// <summary>
    /// ★★ <b>多边形抗锯齿（SDF 描边）</b>——让代码生成的多边形边缘平滑。
    ///
    /// <para>原理：顶点着色器把片元的<b>局部坐标</b>存进 <c>lp</c>，片元着色器算
    /// "这个点到多边形所有边的最近距离 <c>d</c>"，然后用 <c>fwidth(lp)</c>（本像素的
    /// 世界尺寸）把 <c>d</c> 归一化成一像素宽的抗锯齿带：
    /// 距离 &lt; 1px 的片元按比例淡出。</para>
    ///
    /// <para>为什么不用 <c>Line2D.Antialiased</c> 或 <c>Polygon2D</c> 自带的多边形填充：
    /// 前者只能画线，后者<b>完全没有抗锯齿</b>（就是硬三角边），
    /// 一个白色星芒在高 <c>gain</c> 下的硬边会明显看得出是折线。</para>
    ///
    /// <para>⚠️ <b>顶点上限 128</b>（<c>pts</c> 数组长度）。超过的点会被截断 ——
    /// 但这没关系：多边形的<b>几何</b>本身仍由 <c>Polygon2D.Polygon</c> 完整绘制，
    /// 这里只影响"边缘附近怎么算距离"，128 点远多于本项目所有形状的顶点数。</para>
    ///
    /// <para>⚠️ <b>不要用它来画"很细的线"</b>：整个形状都在 1px 抗锯齿带里时会被整体洗淡。
    /// 细线用 <c>Line2D</c>（它有自己的 <c>Antialiased</c>）。</para>
    /// </summary>
    internal static Shader AaShader => _aa ??= Make(AaBody(""));

    /// <summary>同上，但叠加混合（发光）。<b>特效默认用这个。</b></summary>
    internal static Shader AaAddShader => _aaAdd ??= Make(AaBody("\nrender_mode blend_add;\n"));

    /// <summary>
    /// ★★★ <b>方向性运动模糊</b>（2026-10-18 深夜，手法直接来自参照实现
    /// <c>MoreDefectAnimations</c> 的 <c>NativeIceLance.ExposureShader</c>）。
    ///
    /// <para><b>它解决什么问题</b>：一颗高速飞行的贴图弹体，在 60fps 下每一帧只是一个清晰的小图形，
    /// 于是读起来像"贴纸在平移"。参照实现的做法是**沿运动方向做 12 次采样的拖影** ——
    /// 高光被"抹"成一条，速度感来自模糊本身，而不是另外画一条尾巴。</para>
    ///
    /// <para>参数：<c>dir</c> = 运动方向（UV 空间，屏幕像素方向除以贴图尺寸）、
    /// <c>blur_len</c> = 拖影总长（UV 单位，0.05~0.25）、<c>gain</c>/<c>opacity</c> 控制亮度。</para>
    ///
    /// <para>⚠️ 铁律 57：GLSL 写错 Godot **不报错、静默丢掉整个材质**。
    /// 用之前必须过 <see cref="FieldFx.ReportShaderHealth"/>。</para>
    /// </summary>
    internal static Shader BulletSmear => _smear ??= Make("""
shader_type canvas_item;
render_mode unshaded, blend_add;

uniform vec2 dir = vec2(1.0, 0.0);
uniform float blur_len = 0.12;
uniform float gain = 1.35;
uniform float opacity = 1.0;

void fragment() {
    vec4 c = texture(TEXTURE, UV) * 0.42;
    for (int i = 0; i < 12; i++) {
        float u = (float(i) + 0.5) / 12.0;
        c += texture(TEXTURE, UV - dir * blur_len * u) * (0.58 * 2.0 * (1.0 - u) / 12.0);
    }
    COLOR = c * gain * opacity;
}
""");

    /// <summary>
    /// ★★★ <b>弹体核心</b>（2026-10-19）—— 在 <see cref="BulletSmear"/> 的基础上补三件
    /// RegentFX 的手法（调研见 `调研-贴图效果与质感-20261019.md` §2）。
    ///
    /// <para><b>① flipbook</b>（<c>frame / frame_count / flipbook_size</c>）：
    /// 默认 <c>frame_count = 1</c> ⇒ 采样路径与 <see cref="BulletSmear"/> <b>逐字相同</b>，
    /// 不换贴图不会有任何回归；要启用就塞一张 sheet、代码里递 <c>frame</c>。</para>
    ///
    /// <para><b>② 白热核心</b>（<c>hot / hot_thresh</c>）：原 shader 的 <c>gain</c> 封顶在 1.x，
    /// 而我们所有贴图的峰值只有 0.74~1.0（PNG 上限）⇒ <b>物理上到不了"光"</b>。
    /// RegentFX 的 LUT 最后一档是 <b>8.0</b>（`vfx_missile_core.tres`，`use_hdr = true`）；
    /// 这里给亮部再叠一层 <c>hot</c>，而本 shader 是 <c>blend_add</c> ⇒
    /// <b>超过 1.0 的值会真的累加变亮</b>，不会被 clamp 成同一个白。</para>
    ///
    /// <para><b>③ 四步内部闪动</b>（<c>shimmer</c>）：他们 4 帧实测帧间差只有
    /// <b>2.5~3.5/255 ≈ 1%</b> —— 目的不是"动"，是"别像贴纸"。这里用 <c>floor(mod(frame,4))</c>
    /// 把时间量化成 4 个台阶，让一条亮带沿<b>横向</b>（UV.x）扫过 ——
    /// 横向才能躲开轴向 12 次采样的运动模糊，否则一抹就没了。</para>
    ///
    /// <para>⚠️ 铁律 57：GLSL 写错 Godot 会<b>静默丢掉整个材质</b> ⇒ 启动时过
    /// <see cref="FieldFx.ReportShaderHealth"/>。</para>
    /// </summary>
    internal static Shader BulletCore => _bulletCore ??= Make("""
shader_type canvas_item;
render_mode unshaded, blend_add;

uniform vec2 dir = vec2(0.0, -1.0);
uniform float blur_len = 0.16;
uniform float gain = 1.35;
uniform float opacity = 1.0;

// ① flipbook（默认单帧 ⇒ 与原来的采样完全一致）
uniform float frame = 0.0;
uniform float frame_count = 1.0;
uniform vec2 flipbook_size = vec2(1.0, 1.0);

// ② 白热核心（数值见 CardFieldVfx.BulletMode 的 BulletHot/BulletHotThresh：扫描定档）
uniform float hot = 0.30;
uniform float hot_thresh = 0.90;

// ③ 四步横向闪动（幅度对齐 RegentFX 实测的 1% 量级）
uniform float shimmer = 0.03;

vec2 frame_uv(vec2 uv) {
    if (frame_count < 1.5) {
        return uv;
    }
    float n = floor(mod(frame, frame_count));
    vec2 cell = vec2(1.0 / flipbook_size.x, 1.0 / flipbook_size.y);
    float col = mod(n, flipbook_size.x);
    float row = floor(n / flipbook_size.x);
    return uv * cell + vec2(col, row) * cell;
}

void fragment() {
    vec4 c = texture(TEXTURE, frame_uv(UV)) * 0.42;
    for (int i = 0; i < 12; i++) {
        float u = (float(i) + 0.5) / 12.0;
        c += texture(TEXTURE, frame_uv(UV - dir * blur_len * u)) * (0.58 * 2.0 * (1.0 - u) / 12.0);
    }

    vec3 col = c.rgb * gain;
    float luma = max(max(col.r, col.g), col.b);
    col += vec3(hot) * smoothstep(hot_thresh, 1.0, luma);

    float phase = floor(mod(frame, 4.0)) * 0.25;
    float band = 1.0 - abs(fract(UV.x + phase) - 0.5) * 2.0;
    col *= 1.0 + shimmer * (smoothstep(0.55, 1.0, band) - 0.5) * 2.0;

    COLOR = vec4(col, c.a) * opacity;
}
""");

    /// <summary>
    /// ★★★ <b>弹道光束（拖尾）</b>——2026-10-19 重做，取代"3 层贴图等宽拉伸"。
    ///
    /// <para><b>为什么要换</b>：旧做法是把 <c>ray_sk_06_trail.png</c> 沿飞行方向拉长、三层叠 alpha。
    /// 实测剖面（`_tools\preview_bullet_trail.py`）：**宽度 26px 全长恒定、亮度 0.66 全长恒定**
    /// —— 就是用户否过的"**尺子 / 带子**"。贴图拉伸决定不了"沿长度怎么变"，所以必须程序化。</para>
    ///
    /// <para><b>这一版的剖面</b>（全部在 UV.x 上，0 = 尾、1 = 弹头）：
    /// <list type="bullet">
    ///   <item><b>宽度</b> 尾部 0.30 → 60% 处 0.80 → 弹头 1.00（收薄的锥形：不是尺子，也不是针）；</item>
    ///   <item><b>亮度</b> 尾部 0.35 → 60% 处 0.90 → 弹头 1.00（能量在头部 ⇒ 读"打出去"不是"拖着"）；</item>
    ///   <item><b>两头切口</b> <c>cap</c> 内做 smoothstep ⇒ 干净断面（"两头干净切口"）；</item>
    ///   <item><b>硬芯 + 柔晕</b> 硬芯等宽近白、柔晕 3.4 倍宽偏暖 ⇒ 不"塑料"（同 <see cref="Ring"/> 的教训）；</item>
    ///   <item><b>命中回缩</b> <c>tail_cut</c> 0→1 ⇒ 光束从尾部被命中点"吸进去"，而不是整体暗淡消失。</item>
    /// </list></para>
    ///
    /// <para>⚠️ 本 shader <b>不采样 TEXTURE</b>（形状全由 UV 算），所以挂什么贴图都行 ——
    /// 传入的 <c>ray_sk_06_trail.png</c> 只用来定 quad 的尺寸。</para>
    /// </summary>
    internal static Shader BulletStreak => _bulletStreak ??= Make("""
shader_type canvas_item;
render_mode unshaded, blend_add;

uniform float core_w = 0.57;                       // 硬芯半宽（d 的阈值，见 fragment 里的 d）
uniform float halo_mul = 3.4;                      // 柔晕宽度倍数
uniform float core_alpha = 0.95;
uniform float halo_alpha = 0.16;
uniform vec4 core_tint : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform vec4 halo_tint : source_color = vec4(1.0, 0.86, 0.70, 1.0);
uniform float tail_cut = 0.0;                      // 命中后尾部回缩（0 = 完整）
uniform float cap = 0.03;                          // 两头的切口宽度（UV.x）

float width_profile(float u) {
    return u < 0.6 ? mix(0.30, 0.80, u / 0.6) : mix(0.80, 1.00, (u - 0.6) / 0.4);
}

float alpha_profile(float u) {
    return u < 0.6 ? mix(0.35, 0.90, u / 0.6) : mix(0.90, 1.00, (u - 0.6) / 0.4);
}

void fragment() {
    float u = UV.x;                                         // 0 = 尾，1 = 弹头
    float d = abs(UV.y - 0.5) * 2.0;                        // 0 = 中线，1 = quad 边缘
    float capv = clamp(min(u, 1.0 - u) / max(cap, 0.0001), 0.0, 1.0);
    float ap = alpha_profile(u) * capv;
    float wp = width_profile(u);

    float cw = max(core_w * wp, 0.0001);
    float core = 1.0 - smoothstep(cw * 0.80, cw * 1.20, d);
    float hw = max(core_w * halo_mul * max(wp, 0.55), 0.0001);
    float t = d / hw;
    float halo = exp(-t * t * 2.2);

    vec3 col = core_tint.rgb * core * core_alpha + halo_tint.rgb * halo * halo_alpha;
    float a = max(core * core_alpha, halo * halo_alpha) * ap;
    a *= step(tail_cut, u);                                 // 尾部回缩

    COLOR = vec4(col, a);
}
""");

    /// <summary>
    /// ★★ <b>扩张的环</b>——冲击波、光晕、收束环全靠它。
    ///
    /// <para>动画时只改三个参数：<c>radius</c>（0.04→0.95 扩张）、
    /// <c>width</c>（0.05→0.008 变细）、<c>alpha</c>（1→0 淡出）。</para>
    ///
    /// <para><c>glow</c> 是在环内外各铺 5 倍宽的一层柔光：<b>只有硬环会很"塑料"</b>，
    /// 加上柔光才像真的能量波。压扁（做地面上的椭圆冲击波）靠节点的 <c>Scale.y</c>，
    /// 不要在 shader 里写死。</para>
    /// </summary>
    internal static Shader Ring => _ring ??= Make("""
shader_type canvas_item;
render_mode blend_add;

uniform vec4 tint : source_color = vec4(1.0, 0.97, 0.86, 1.0);   // 环带内侧（亮）
uniform vec4 tint2 : source_color = vec4(1.0, 0.58, 0.20, 1.0);  // 环带外侧（暖）
uniform float radius = 0.3;
uniform float width = 0.05;
uniform float alpha = 1.0;
uniform float glow_amt = 0.35;
uniform float vary = 0.35;      // 沿圆周的明暗起伏（0 = 完美均匀的圆环，很"塑料"）
uniform float phase = 0.0;
// ── 堆料：环不止一层 ──
uniform float radius2 = 0.0;    // 第二道细环的半径（0 = 关闭）
uniform float width2 = 0.006;
uniform float halo_amt = 0.12;  // 最外层极淡的外晕（16 倍宽）
uniform float gap_amt = 0.0;    // 环上的缺口比例（0 = 连续）

float hash12(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

void fragment() {
    vec2 p = UV * 2.0 - 1.0;
    float d = length(p);
    float ang = atan(p.y, p.x);
    float band = abs(d - radius);

    // 硬环芯 + 5 倍宽柔光
    float w = max(width, 0.0001);
    float core = 1.0 - smoothstep(0.0, w, band);
    float glow = (1.0 - smoothstep(0.0, w * 6.0, band)) * glow_amt;
    // ★ 三层结构：硬芯 / 中段柔光 / 极淡外晕 —— 只有一层硬环是最"塑料"的
    float halo = (1.0 - smoothstep(0.0, w * 16.0, band)) * halo_amt;

    // ★ 第二道细环（半径不同、更细）：一道环读起来单薄，两道才有"结构"
    float core2 = 0.0;
    if (radius2 > 0.001) {
        core2 = 1.0 - smoothstep(0.0, max(width2, 0.0001), abs(d - radius2));
    }

    // ★ 缺口：真实光环常有断口，连续的圆看起来像几何题
    float gap = 1.0;
    if (gap_amt > 0.001) {
        gap = 1.0 - gap_amt * step(0.62, fract(ang / 6.2831853 * 5.0 + phase * 0.37));
    }

    // ★ 沿圆周的明暗起伏：两道宽弧 + 一层细密变化。
    //   完全均匀的环是"矢量图"，真实的光环总有几段更亮。
    float wave = 0.62
               + 0.26 * sin(ang * 2.0 + phase)
               + 0.12 * sin(ang * 5.0 - phase * 1.7);
    float k = mix(1.0, wave, vary);

    // ★ 环带内侧偏白、外侧偏暖 —— 单色环最"塑料"
    float t = clamp((d - radius + width) / max(width * 2.0, 0.0001), 0.0, 1.0);
    vec3 col = mix(tint.rgb, tint2.rgb, t);

    float edge = 1.0 - smoothstep(0.78, 1.0, d);

    float a = (core + core2 * 0.85 + glow + halo) * k * alpha * edge * gap;
    // ★ 抖动：大面积柔光渐变一定出色带，±1.2% 的噪声把它打散（几乎看不见，但没有色带）
    a += (hash12(FRAGCOORD.xy) - 0.5) * 0.024;

    COLOR = vec4(col * (1.0 + core * 0.8), clamp(a, 0.0, 1.0));
}
""");

    /// <summary>
    /// ★ <b>中心闪光</b>——"这一下打中了"的瞬间爆亮。
    ///
    /// <para>动画节奏（照抄手感最好的那套）：<c>Scale</c> 从 0.5 用 <c>OutExpo</c> 冲到 1.25，
    /// <c>alpha</c> 从 1 用 <c>OutCubic</c> 落到 0，全程 0.2~0.3 秒。
    /// <b>快</b>是关键——闪光拖长就变成"一层雾"。</para>
    /// </summary>
    internal static Shader Flash => _flash ??= Make("""
shader_type canvas_item;
render_mode blend_add;

uniform vec4 tint : source_color = vec4(1.0, 0.97, 0.88, 1.0);
uniform float alpha = 1.0;
uniform float falloff = 2.2;

float hash12(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

void fragment() {
    float d = length(UV * 2.0 - 1.0);
    float a = pow(clamp(1.0 - d, 0.0, 1.0), falloff);
    a += (hash12(FRAGCOORD.xy) - 0.5) * 0.022;   // 抖动：大面积渐变一定出色带
    COLOR = vec4(tint.rgb, clamp(a, 0.0, 1.0) * alpha * tint.a);
}
""");

    /// <summary>
    /// ★★ <b>光柱</b>——「得见光芒」的核心元素，也是"素"与"不素"差距最大的地方。
    ///
    /// <para>三件事同时在做：</para>
    /// <list type="number">
    ///   <item><b>横向三层</b>：中轴极亮（<c>core</c>，3 次幂所以只有中间一条白），
    ///         外侧一层柔光（<c>halo</c>，1.2 次幂铺得很开）—— 单层渐变会显得像"一条灰带"；</item>
    ///   <item><b>纵向两端淡出</b>：顶部散进空气、底部轻轻落在地面上，都不能硬切；</item>
    ///   <item><b>内部流动条纹</b>：<c>sin(UV.y * freq + TIME * speed)</c> 让光柱里像有能量在往上走。
    ///         ★ 符号是 <c>+ TIME</c> 才是<b>向上</b>流（UV.y=1 是底部）；
    ///         写成 <c>- TIME</c> 会变成往下淌，看着像漏光。</item>
    /// </list>
    ///
    /// <para>动画节奏：<c>Scale.y</c> 用 <c>OutExpo</c> 从 0 长到 1（拔地而起），
    /// <c>Scale.x</c> 先宽后窄（<c>0.2→1</c> 再 `→0.4`），<c>alpha</c> 用 <c>InCubic</c> 收掉。</para>
    /// </summary>
    internal static Shader Pillar => _pillar ??= Make("""
shader_type canvas_item;
render_mode blend_add;

uniform vec4 tint : source_color = vec4(1.0, 0.92, 0.72, 1.0);
uniform float alpha = 1.0;
uniform float core_pow = 3.0;
uniform float halo_pow = 1.2;
uniform float streak_speed = 14.0;
uniform float streak_freq = 40.0;
uniform float streak_amt = 0.25;
uniform float taper = 0.55;     // 顶端宽度比例（1 = 直筒；< 1 = 往上收，像一道光柱而不是一块板）

float hash12(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

void fragment() {
    // ★ 宽度随高度收窄：UV.y=0 是顶端、1 是底端 ⇒ 顶端更窄，形成"光柱"而不是矩形板。
    //   这是光柱难看的最大原因 —— 一个等宽的米色矩形，两侧硬边明晃晃的。
    float wscale = mix(1.0, max(taper, 0.05), 1.0 - UV.y);
    float x = abs(UV.x * 2.0 - 1.0) / max(wscale, 0.05);   // 0 = 中轴，>1 = 两侧边缘
    float k = clamp(1.0 - x, 0.0, 1.0);

    float core = pow(k, core_pow);            // 极窄的亮芯
    float halo = pow(k, halo_pow) * 0.35;     // 铺开的柔光

    // 纵向淡出：UV.y = 0 是顶端（离地最远），1 是底端（脚边）
    float top = smoothstep(0.0, 0.55, UV.y);
    float bottom = 1.0 - smoothstep(0.94, 1.0, UV.y);

    // 向上流动的条纹（+ TIME ⇒ 相位随时间往 UV.y 小的方向走 ⇒ 视觉上往上流）
    float streak = 1.0 - streak_amt
                 + streak_amt * sin(UV.y * streak_freq + TIME * streak_speed + UV.x * 6.0);
    // ★ 堆料：再叠一层更快、更密的细纹。只有一层条纹的光柱是"塑料管"，
    //   两层不同频率/速度叠起来才有"能量在柱体里流动"的层次。
    streak *= 1.0 - 0.12 + 0.12 * sin(UV.y * streak_freq * 2.7 + TIME * streak_speed * 1.9 - UV.x * 11.0);

    float a = (core + halo) * top * bottom * streak * alpha;
    a += (hash12(FRAGCOORD.xy) - 0.5) * 0.022;
    COLOR = vec4(tint.rgb * (1.0 + core * 0.6), clamp(a, 0.0, 1.0) * tint.a);
}
""");

    /// <summary>
    /// ★ <b>地面法阵</b>——贴在角色脚下的压扁圆盘（节点 <c>Scale.y</c> ≈ 0.3 就是透视感）。
    ///
    /// <para>内容：两道同心细环 + 一圈刻度 + 一圈虚线 + 六边形内框 + 中心柔光，
    /// 全部靠 <c>atan</c> / <c>fract</c> / <c>step</c> 程序化生成，<b>没有贴图</b>。
    /// <c>spin</c> 让刻度环和外环以不同速度反向转 —— 静止的法阵像贴纸，反向转才有"机械感"。</para>
    /// </summary>
    internal static Shader MagicCircle => _magicCircle ??= Make("""
shader_type canvas_item;
render_mode blend_add;

uniform vec4 tint : source_color = vec4(1.0, 0.9, 0.65, 1.0);
uniform float alpha = 1.0;
uniform float spin = 0.35;

float band(float d, float r, float w) {
    return 1.0 - smoothstep(0.0, w, abs(d - r));
}

void fragment() {
    vec2 p = UV * 2.0 - 1.0;
    float d = length(p);
    float ang = atan(p.y, p.x);
    float c = 0.0;

    // 外圈双环
    c += band(d, 0.96, 0.012);
    c += band(d, 0.86, 0.008) * 0.8;

    // 刻度环：48 格，0.18 的占空比
    float ticks = step(fract(ang / 6.2831853 * 48.0 + TIME * spin), 0.18);
    c += ticks * step(0.87, d) * step(d, 0.95) * 0.9;

    // 虚线内环：和外环反向转
    float dash = step(fract(ang / 6.2831853 * 12.0 - TIME * spin * 1.6), 0.6);
    c += band(d, 0.62, 0.01) * dash;

    // 六边形内框
    for (int i = 0; i < 6; i++) {
        float a2 = float(i) * 1.0471976 + TIME * spin * 0.5;
        vec2 n = vec2(cos(a2), sin(a2));
        c += (1.0 - smoothstep(0.0, 0.012, abs(dot(p, n) - 0.54))) * step(d, 0.62);
    }

    // 中心柔光
    c += pow(clamp(1.0 - d, 0.0, 1.0), 3.0) * 0.35;

    c *= step(d, 1.0);                        // 方框外一律不要
    COLOR = vec4(tint.rgb * (0.8 + c * 0.6), clamp(c, 0.0, 1.0) * alpha * tint.a);
}
""");

    /// <summary>
    /// ★★★ <b>冲击后处理</b>——爆开那一瞬整块画面的"力量感"来源。
    ///
    /// <para><b>为什么单独做一个全屏后处理，而不是继续加形状</b>：
    /// 形状再怎么堆，改变的只是"爆点上有什么"；而这一层改的是<b>整幅画面</b>
    /// —— 世界被从中心拽了一下、颜色被拉开、四周压暗。观众感知到的强度来自后者。
    /// 这是游戏里"重击感"最便宜的来源。</para>
    ///
    /// <para>三件事叠在一起：</para>
    /// <list type="number">
    ///   <item><b>径向缩放模糊</b>：朝爆点方向取 6 档采样再平均，
    ///         世界像被吸向爆点（<c>zoom</c>）；</item>
    ///   <item><b>色散</b>：R 通道与 B 通道反向偏移（<c>split</c>），
    ///         只在边缘更强（乘 <c>r</c>），中心保持干净；</item>
    ///   <item><b>暗角收缩</b>：<c>vignette</c> 把四周压暗，视线被逼到爆点。</item>
    /// </list>
    ///
    /// <para>⚠️ 它读 <c>hint_screen_texture</c>，必须画在<b>所有东西之上</b>，
    /// 且整段只持续 0.2~0.3 秒 —— 后处理放久了会糊、会晕。</para>
    /// </summary>
    /// <summary>
    /// ★★★ <b>光谱环</b>——沿圆周走完整色相的发光环。<b>"光谱感"最强的一件。</b>
    ///
    /// <para>一圈从红到紫再回红的能量波扫出去，眼睛立刻读到"能量 / 棱镜 / 色散"，
    /// 而不是"一个金色的圆"。它和 <see cref="Impact"/> 的色散<b>互相加强</b>：
    /// 环本身带色相、色散把背景的色拉开，两层一起看就是"整个空间在折射"。</para>
    ///
    /// <para>与 <see cref="Ring"/> 的分工：Ring 是<b>单一色系</b>（白→暖橙，用于光柱 / 冲击波），
    /// Spectrum 是<b>全色相</b> —— 只用在爆开那一瞬，<b>用多了会花</b>。</para>
    /// </summary>
    /// <summary>
    /// ★★★ <b>局部扭曲</b>——<b>只把敌人那一小块画面拉扯变形</b>，不是整屏色散。
    ///
    /// <para>和 <see cref="Impact"/>（整屏径向拉爆）的分工：Impact 是"镜头被震"，
    /// 这个是"<b>那个东西被打了</b>"。作用于敌人身上时会看到它被向内吸进去又弹回来 ——
    /// 这是打击感里最直接的一层，因为观众的目光就在被打的对象上。</para>
    ///
    /// <para><c>strength</c> 为负 = 向内吸（被压缩 / 被打扁），为正 = 向外鼓。
    /// 用 <c>fall²</c> 让边缘平滑收口，否则扭曲范围会有一圈可见的圆边。</para>
    /// </summary>
    internal static Shader Pinch => _pinch ??= Make("""
shader_type canvas_item;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
uniform vec2 center = vec2(0.5, 0.5);
uniform float aspect = 1.78;
uniform float radius = 0.10;
uniform float strength = 0.0;
uniform float alpha = 1.0;

void fragment() {
    vec2 p = (SCREEN_UV - center) * vec2(aspect, 1.0);
    float r = length(p);

    // 只在 radius 内生效，边缘平滑收口
    float fall = 1.0 - smoothstep(0.0, max(radius, 0.001), r);
    float k = fall * fall;

    vec2 dir = p / max(r, 0.0001);
    vec2 warp = dir * strength * k;

    vec3 col = texture(screen_tex, SCREEN_UV + warp).rgb;

    // 扭曲最强的地方压一点亮，否则看不出"被吸"
    col *= 1.0 + strength * 2.2 * k;

    COLOR = vec4(col, alpha);
}
""");

    internal static Shader Spectrum => _spectrum ??= Make("""
shader_type canvas_item;
render_mode blend_add;

uniform float radius = 0.72;
uniform float width = 0.03;
uniform float alpha = 1.0;
uniform float spin = 0.0;
uniform float sat = 0.72;
uniform float glow_amt = 0.55;

vec3 hsv2rgb(vec3 c) {
    vec3 p = abs(fract(c.xxx + vec3(1.0, 0.6666667, 0.3333333)) * 6.0 - 3.0);
    return c.z * mix(vec3(1.0), clamp(p - 1.0, 0.0, 1.0), c.y);
}

void fragment() {
    vec2 p = UV * 2.0 - 1.0;
    float d = length(p);
    float ang = atan(p.y, p.x);
    float band = abs(d - radius);
    float w = max(width, 0.0001);

    float core = 1.0 - smoothstep(0.0, w, band);
    float glow = (1.0 - smoothstep(0.0, w * 6.0, band)) * glow_amt;

    // 色相沿圆周铺满一整圈
    float hue = fract(ang / 6.2831853 + spin);
    vec3 col = hsv2rgb(vec3(hue, sat, 1.0));

    float edge = 1.0 - smoothstep(0.80, 1.0, d);
    COLOR = vec4(col, (core + glow) * alpha * edge);
}
""");

    internal static Shader Impact => _impact ??= Make("""
shader_type canvas_item;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
uniform vec2 center = vec2(0.5, 0.5);
uniform float zoom = 0.0;
uniform float split = 0.0;
uniform float vignette = 0.0;
uniform float alpha = 1.0;

void fragment() {
    vec2 uv = SCREEN_UV;
    vec2 d = uv - center;
    float r = length(d);

    // ── 径向缩放模糊：6 档向中心收的采样 ──
    vec3 acc = vec3(0.0);
    float wsum = 0.0;
    for (int i = 0; i < 6; i++) {
        float fi = float(i);
        float k = 1.0 - fi * 0.020 * zoom;
        float w = 1.0 - fi * 0.12;
        acc += texture(screen_tex, center + d * k).rgb * w;
        wsum += w;
    }
    vec3 col = acc / max(wsum, 0.001);

    // ── 色散：R / B 反向偏移 ──
    //  ★★ 偏移量<b>不能只按"离爆点的距离"算</b>：旧写法是
    //      vec2 off = dir * split * r;      // r 是到 center 的距离
    //    于是<b>爆点本身 r≈0，色散量正好等于 0</b> —— 玩家眼睛盯着的地方
    //    一点彩色都没有，只有屏幕最边缘才有。实机反复反馈"看不到彩色"就是这个。
    //    修法：以<b>屏幕中心</b>为基准，并且全程保一个基础偏移，
    //    这样爆点、敌人、UI 全都有红青边，眼睛看到哪都在。
    if (split > 0.0001) {
        vec2 dc = uv - vec2(0.5, 0.5);
        float rc = length(dc);
        vec2 dir = dc / max(rc, 0.0001);
        float amount = split * (0.45 + rc * 1.5);
        vec2 off = dir * amount;
        col.r = texture(screen_tex, uv - off).r;
        col.g = texture(screen_tex, uv).g;
        col.b = texture(screen_tex, uv + off).b;
    }

    // ── 暗角：四周压暗，视线被逼向爆点 ──
    col *= 1.0 - vignette * smoothstep(0.20, 0.78, r);

    COLOR = vec4(col, alpha);
}
""");

    /// <summary>
    /// ★★ <b>屏幕折射环</b>——"贵"的那一下：能量波扫过时，<b>背景画面被真实地推歪</b>。
    ///
    /// <para>这是整套里唯一读 <c>hint_screen_texture</c> 的 shader，所以它必须画在
    /// <b>所有东西之上</b>（放在特效的最上层、并且只覆盖波扫过的区域）。</para>
    ///
    /// <para>做法：径向位移 <c>off = dir * (-x·exp(-x²))</c>，
    /// <c>x = (d - radius)/width</c> 是"到环的距离（以环宽为单位）"。
    /// 环的内侧 <c>x &lt; 0</c> 位移朝外、外侧朝内 —— <b>一侧压缩一侧拉伸</b>，
    /// 这才是真的水面波纹；只往一个方向推会像"画面在平移"。
    /// 三个颜色通道的位移量略不同（1.12 / 1.0 / 0.88）就是色散。</para>
    ///
    /// <para>它本身几乎不发光（<c>rim</c> 只有 0.22），
    /// <b>所以一定要和 <see cref="Ring"/> 一起用</b>：Ring 给亮边，Refract 给扭曲。</para>
    /// </summary>
    internal static Shader Refract => _refract ??= Make("""
shader_type canvas_item;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
uniform vec4 tint : source_color = vec4(1.0, 0.95, 0.8, 1.0);
uniform float radius = 0.3;
uniform float width = 0.12;
uniform float strength = 0.025;
uniform float alpha = 1.0;

void fragment() {
    vec2 uv = UV * 2.0 - 1.0;
    float d = length(uv);

    // 高斯环形剖面：无硬边
    float x = (d - radius) / max(width, 0.0001);
    float shape = exp(-x * x * 2.5);
    float edge = 1.0 - smoothstep(0.72, 1.0, d);
    float k = shape * edge;

    vec2 dir = d > 0.0001 ? uv / d : vec2(0.0);

    // 内侧朝外、外侧朝内（-x 让符号在 x=0 两侧相反）
    vec2 off = dir * (-x * shape) * edge * strength * alpha;

    float r = texture(screen_tex, SCREEN_UV - off * 1.12).r;
    float g = texture(screen_tex, SCREEN_UV - off).g;
    float b = texture(screen_tex, SCREEN_UV - off * 0.88).b;

    // 环壁上的一点冷光，让扭曲"有实体"
    float rim = (1.0 - smoothstep(0.0, 0.33, abs(x))) * edge * 0.22;
    vec3 col = vec3(r, g, b) + tint.rgb * rim * alpha;

    COLOR = vec4(col, clamp(k * 3.0, 0.0, 1.0) * clamp(alpha * 4.0, 0.0, 1.0));
}
""");

    /// <summary>
    /// ★★★ <b>带洞的黑</b>——「从黑暗中去到光亮处」的核心道具。
    ///
    /// <para><b>为什么不能只是"黑幕淡出"</b>：整体调低不透明度，读到的是
    /// "刚才暗了一下，现在好了"，那是<b>时间</b>上的变化；
    /// 而"从黑暗中去到光亮处"要的是<b>空间</b>上的变化 ——
    /// <b>黑暗被光从一点推开、退到画面外面去</b>。所以这里不调 alpha，是开一个洞。</para>
    ///
    /// <para>数学：<c>d</c> 是到洞心的距离，<c>smoothstep(hole, hole+soft, d)</c>
    /// 在洞里给 0（露出世界）、洞外给 1（全黑），中间 <c>soft</c> 宽是软过渡。
    /// <c>hole</c> 从 0 一路涨到 2.2 时就超出画面了 —— 黑暗被彻底推走。</para>
    ///
    /// <para>⚠️ 用 <c>UV</c> 而不是 <c>SCREEN_UV</c>：这个四边形铺满 VFX 容器，
    /// 用自身 UV 就等于"容器内归一化坐标"，不依赖屏幕与容器的对齐关系。</para>
    ///
    /// <para>⚠️ <c>center</c> 的 x 要先乘 <c>aspect</c> 再算距离，否则洞在宽屏上会拉成椭圆。</para>
    /// </summary>
    internal static Shader DarkVeil => _darkVeil ??= Make("""
shader_type canvas_item;

uniform vec2 center = vec2(0.5, 0.5);
uniform vec2 center2 = vec2(0.5, 0.5);   // ★ 第二个洞：永远开在角色身上（"角色不要黑"）
uniform float hole2 = 0.0;
uniform float aspect = 1.78;
uniform float hole = 0.0;
uniform float soft = 0.24;
uniform float alpha = 1.0;
uniform vec4 rim : source_color = vec4(1.0, 0.58, 0.18, 1.0);
uniform float rim_amt = 0.0;

// ★★ 裂纹：光从黑暗的缝隙里渗出来。crack = 强度，crack_reach = 裂纹爬到多远
uniform float crack = 0.0;
uniform float crack_reach = 0.0;
uniform float crack_segs = 9.0;          // 保留（旧的放射缝不再用）
uniform float crack_scale = 3.2;         // 越大碎块越多越细
uniform float crack_width = 0.012;
uniform vec4 crack_tint : source_color = vec4(1.0, 0.86, 0.62, 1.0);

float hash12(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float hash11(float x) {
    return fract(sin(x * 127.1) * 43758.5453);
}

// ★★★ 玻璃碎裂网格（标准 Voronoi 边界）
//    ⚠️ 第一版我用"黄金角螺旋撒点"，18 个站点在圆盘里排成放射状 ⇒
//       Voronoi 边界变成一圈辐条，出来是"太阳爆发"不是碎玻璃（离线试验台抓到）。
//    正确做法是**网格抖动撒点**：每个格子内放一个随机站点，查 3x3 邻域，
//    取最近与次近距离之差 d2-d1 —— 它在两个碎块的公共边界上为 0。
vec2 hash22(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.xx + p3.yz) * p3.zy);
}

float glass_edge(vec2 p) {
    vec2 ip = floor(p);
    vec2 fp = fract(p);

    float d1 = 8.0;
    float d2 = 8.0;

    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            vec2 g = vec2(float(x), float(y));
            vec2 site = g + hash22(ip + g);      // 格子内的随机站点
            float d = length(site - fp);

            if (d < d1) {
                d2 = d1;
                d1 = d;
            } else if (d < d2) {
                d2 = d;
            }
        }
    }

    return d2 - d1;
}

void fragment() {
    // 修正纵横比后再算距离 ⇒ 洞是正圆
    vec2 p = (UV - center) * vec2(aspect, 1.0);
    float d = length(p);

    // 洞里 0（看得见世界）、洞外 1（全黑）
    float v = smoothstep(hole, hole + max(soft, 0.001), d);

    // ★ 第二个洞：用户要求"角色不要黑" —— 莱伊身边永远亮着，
    //   外面的世界照样被黑暗吞掉。取 min 就是"两个洞任一开着就算开"。
    float d2 = length((UV - center2) * vec2(aspect, 1.0));
    v = min(v, smoothstep(hole2, hole2 + max(soft, 0.001), d2));

    // ★ 洞口镀边：光溅在黑暗上的那一圈。没有它，黑暗和光之间是一条干净的数学边界，
    //    看着像遮罩；有了它才像"光真的在推开黑暗"。
    float rimd = 1.0 - smoothstep(0.0, max(soft, 0.001) * 0.9, abs(d - hole - soft * 0.35));
    vec3 col = rim.rgb * rimd * rim_amt;

    // ★★ 裂纹：把圆分成 crack_segs 个扇区，扇区边界就是裂缝（带随机抖动）。
    //    缝隙处黑暗直接消失（v→0）⇒ 后面的世界透出来；缝两侧再叠一层暖光 ⇒ "渗"。
    float cr = 0.0;
    float cglow = 0.0;

    if (crack > 0.001) {
        // ★ 玻璃网格：p 是"以爆点为原点、已修正纵横比"的坐标
        float e = glass_edge(p * crack_scale);

        // 缝宽随进度变粗
        float w = max(crack_width * (0.55 + 1.10 * crack), 0.0005);

        // 裂纹从爆点向外长：reach 之外还没有裂
        float reachFade = 1.0 - smoothstep(crack_reach, crack_reach + 0.34, d);

        float gridCrack = 1.0 - smoothstep(0.0, w, e);

        // ★★ 主干裂纹：从**光汇聚点本身精确发出**的 7 条辐条。
        //    网格线是网格线，但如果光点只是"恰好落在某处"，读起来就不是"从这一点碎开"。
        //    让主裂纹的起点 = 光点 ⇒ 光点成为裂纹网上的一个**交点**，这才是玻璃被击中的样子。
        float sang = atan(p.y, p.x);
        float sa = (sang / 6.2831853 + 0.5) * 7.0;
        float sf = abs(fract(sa) - 0.5) * 2.0;        // 0 = 在辐条中心线上
        float swid = 0.09 + 0.15 * crack;
        float spokeCrack = 1.0 - smoothstep(0.0, swid, sf);

        cr = clamp(max(gridCrack, spokeCrack), 0.0, 1.0) * reachFade;
        cglow = clamp(max(1.0 - smoothstep(0.0, w * 4.5, e), spokeCrack * 0.75), 0.0, 1.0)
                * reachFade * crack;
    }

    // ★★★ 关键修正：缝隙**不能靠"挖掉黑暗"来读**。
    //    黑幕后面本身就是一片黑的世界 —— 挖掉黑，露出来的还是黑，所以什么都看不见。
    //    正确做法是让**缝隙自己发光**：保持不透明，把这一条的颜色换成亮暖色。
    //    于是它是"被光烧穿的裂缝"，不是"透明切口"。
    cr = clamp(cr, 0.0, 1.0);

    // 缝芯几乎全亮，缝两侧留一点黑边做对比 ⇒ 有"裂开"的实体感
    col = mix(col, crack_tint.rgb * 2.4, cr);
    col += crack_tint.rgb * cglow * 1.10;

    // 只在缝芯让极少量世界透出来（0.12），其余保持黑 —— 世界是黑的，透太多反而发灰
    v *= 1.0 - cr * 0.12;

    // 抖动：黑→透明的过渡横跨半个屏幕，不抖就会看到一圈圈色带
    v += (hash12(FRAGCOORD.xy) - 0.5) * 0.03;

    COLOR = vec4(col, clamp(v, 0.0, 1.0) * alpha);
}
""");

    /// <summary>
    /// ★★ <b>光淹没画面</b>（普通混合）—— 引爆那一瞬的全屏过曝。
    ///
    /// <para><b>为什么要"过曝"而不是"加亮"</b>：加色混合只能把画面变亮、细节还在，
    /// 读起来是"亮了一下"；<b>把画面替换成纯白</b>才是"你看不见东西了，因为你在光里"——
    /// 这才是"去到光亮处"字面上的那一瞬。<c>reach</c> 从 0.15 冲到 2.6 时，
    /// 白光以爆点为中心向外<b>吞没</b>整个画面。</para>
    ///
    /// <para>配 <see cref="LightFloodAdd"/> 用：这个负责"白"，那个负责退去之后的"余温"。</para>
    /// </summary>
    internal static Shader LightFlood => _lightFlood ??= Make(LightFloodBody(""));

    /// <summary>同上但加色混合 —— 白光退去后弥漫在空气里的金色余温（不冲掉画面细节）。</summary>
    internal static Shader LightFloodAdd => _lightFloodAdd ??= Make(LightFloodBody("\nrender_mode blend_add;\n"));

    private static string LightFloodBody(string renderMode) => "shader_type canvas_item;" + renderMode + """

uniform vec2 center = vec2(0.5, 0.5);
uniform float aspect = 1.78;
uniform float reach = 1.0;
uniform float soft = 0.20;
uniform float fill = 0.55;
uniform float alpha = 1.0;
uniform vec4 tint : source_color = vec4(1.0, 1.0, 1.0, 1.0);

float hash12(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

void fragment() {
    vec2 p = (UV - center) * vec2(aspect, 1.0);
    float d = length(p);

    // ★ 光锋：d ≈ reach 那一圈最白 —— 这是"推黑暗"的那个前锋
    float front = 1.0 - smoothstep(0.0, max(soft, 0.001), abs(d - reach));

    // ★ 光锋已经扫过的区域（d < reach）：铺一层 fill 的白。
    //   fill 必须 < 1，否则锋内一片死白，爆开的星芒和光环就全看不见了。
    float swept = 1.0 - smoothstep(max(reach, 0.002), reach + max(soft, 0.001), d);

    // 锋内 = fill，锋面 = 1，锋外 = 0
    float a = swept * fill + front * (1.0 - fill);
    a += (hash12(FRAGCOORD.xy) - 0.5) * 0.022;

    COLOR = vec4(tint.rgb, clamp(a, 0.0, 1.0) * alpha * tint.a);
}
""";

    /// <summary>
    /// SDF 抗锯齿的公共主体。<paramref name="renderMode"/> 是插在
    /// <c>shader_type</c> 后面的那行（<c>""</c> 或换行的 <c>render_mode blend_add;</c>）。
    /// </summary>
    private static string AaBody(string renderMode) => """
shader_type canvas_item;
""" + renderMode + """

uniform vec2 pts[128];
uniform int n = 0;

// ★ 边缘色散强度：0 = 关闭；0.4~0.8 = 光的外缘出现一圈彩虹边（本体仍是白的）
uniform float fringe = 0.0;
uniform float fringe_width = 5.0;   // 色边宽度（以像素为单位）

varying vec2 lp;

vec3 hue2rgb(vec3 c) {
    vec3 p = abs(fract(c.xxx + vec3(1.0, 0.6666667, 0.3333333)) * 6.0 - 3.0);
    return c.z * mix(vec3(1.0), clamp(p - 1.0, 0.0, 1.0), c.y);
}

void vertex() {
    lp = VERTEX;
}

void fragment() {
    COLOR *= texture(TEXTURE, UV);

    if (n >= 3) {
        // 到多边形所有边的最近距离
        float d = 1e9;
        for (int i = 0; i < 128; i++) {
            if (i >= n) {
                break;
            }
            vec2 a = pts[i];
            vec2 b = pts[(i + 1) % n];
            vec2 ab = b - a;
            // 在 ab 上的投影参数，夹到 [0,1] 得到线段最近点
            float t = clamp(dot(lp - a, ab) / max(dot(ab, ab), 1e-6), 0.0, 1.0);
            d = min(d, length(lp - a - ab * t));
        }

        // fwidth(lp) = 本像素在局部坐标里占多长；×0.7071 是把对角像素折成垂直距离
        float px = max(length(fwidth(lp)) * 0.7071, 1e-4);

        // ★★ 边缘色散：只在"离边缘 fringe_width 像素内"加彩色，光本体完全不动。
        //    色相沿图形轮廓一圈走完 —— 这就是镜头色散（chromatic aberration）的样子：
        //    白光的外缘被拆开，一边偏蓝紫、一边偏橙红。
        //    ⚠️ 关键：**彩色只能出现在边缘**。整块染色就成了彩虹贴纸，不是光了。
        if (fringe > 0.0001) {
            float rim = 1.0 - smoothstep(0.0, px * fringe_width, d);
            float ang = atan(lp.y, lp.x);
            vec3 fc = hue2rgb(vec3(fract(ang / 6.2831853), 0.9, 1.0));
            COLOR.rgb += fc * rim * fringe;
        }

        COLOR.a *= clamp(d / px, 0.0, 1.0);
    }
}
""";

    private static Shader Make(string code) => new() { Code = code };
}
