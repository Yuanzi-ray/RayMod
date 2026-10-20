using System;
using Godot;
using MegaCrit.Sts2.Core.Logging;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Art;

/// <summary>
/// ★★★ <b>场上特效的原语库</b>——本项目所有"手写特效"的公共地基。
///
/// <para><b>为什么需要它</b>：此前的特效只有"贴图精灵 + Line2D"两种武器，
/// 于是不管怎么调参都走不出"一个烤好的形状放大缩小"这个框架 —— 用户的原话是
/// "太素了"。这里补上的四样东西，恰好是那四条限制：</para>
///
/// <list type="number">
///   <item><b><see cref="Animate"/></b> —— 一条 0→1 时间轴驱动整棵树。
///         此前的写法是每帧在 <c>_Process</c> 里手算所有东西，
///         既没法给不同子效果做不同的缓动，也很难让它们精确对齐到"第 0.42 秒"。</item>
///   <item><b><see cref="Poly"/> / <see cref="Stroke"/> / <see cref="Circle"/> / <see cref="Star4"/></b>
///         —— 顶点<b>用代码算</b>，配合 <see cref="FieldFxShaders.AaAddShader"/> 做抗锯齿。
///         星芒、六边形、锥形拖尾、任意多边形，全都不需要美术资源。</item>
///   <item><b><see cref="ShaderQuad"/> + <see cref="FieldFxShaders"/></b> —— 逐像素的效果：
///         扩张的环、内部流动的光柱、屏幕折射。</item>
///   <item><b><see cref="Sparks"/></b> —— 真正的 <c>GpuParticles2D</c>（带颜色渐变行），
///         而不是拿十几个 Sprite2D 手动模拟。</item>
/// </list>
///
/// <para><b>另外两件"工程"上的事，同样影响观感</b>：
/// <see cref="FreeAfter"/> 统一回收节点（特效不会被忘在场上），
/// <see cref="After"/> 统一派发延时步骤（不会因为某个子效果抛异常就卡住整串）。</para>
/// </summary>
internal static class FieldFx
{
    private static readonly Logger Log = new("RayMod.Art", LogType.Generic);

    /// <summary>叠加混合材质：所有"发光"的东西都用它。<b>全局共用一个</b>（只读，不改属性）。</summary>
    private static CanvasItemMaterial? _additive;

    /// <summary>普通混合材质：需要一个"实心的东西"遮住背景时用。</summary>
    private static CanvasItemMaterial? _normal;

    private static GradientTexture2D? _softDot;

    /// <summary>
    /// <b>叠加混合</b>（加色）。发光感全靠它：多层的亮度会相加，
    /// 于是重叠处自然过曝成白 —— 这正是"亮"的物理来源。
    ///
    /// <para>⚠️ 共用同一个实例是安全的，因为这里<b>从不改它的任何属性</b>；
    /// 每个节点各自的亮度走 <c>Modulate</c>（那是节点自己的）。</para>
    /// </summary>
    internal static CanvasItemMaterial Additive => _additive ??= new CanvasItemMaterial
    {
        BlendMode = CanvasItemMaterial.BlendModeEnum.Add,
    };

    /// <summary>普通混合（覆盖）。做"压暗背景"这类东西时用。</summary>
    internal static CanvasItemMaterial Normal => _normal ??= new CanvasItemMaterial
    {
        BlendMode = CanvasItemMaterial.BlendModeEnum.Mix,
    };

    /// <summary>
    /// ★ <b>程序化软光点</b>——32×32 的径向渐变贴图，<b>代码生成，不是美术资源</b>。
    ///
    /// <para>它是整套里复用率最高的贴图：光点本体、光晕、粒子贴图、
    /// 柔光层全都用它，只靠缩放和 <c>Modulate</c> 区分。
    /// 用 32×32 就够是因为它本身是<b>平滑渐变</b>，放大后不会看到像素块
    /// （双线性过滤会自动插值）。</para>
    ///
    /// <para><c>FillTo = (1, 0.5)</c> 让渐变半径正好是半个 UV，
    /// 也就是内切于四边形 —— 所以它铺在任何尺寸的方框里都是一个正圆。</para>
    /// </summary>
    internal static GradientTexture2D SoftDot => _softDot ??= new GradientTexture2D
    {
        Gradient = new Gradient
        {
            Offsets = [0f, 0.35f, 1f],
            Colors =
            [
                new Color(1f, 1f, 1f, 1f),
                new Color(1f, 1f, 1f, 0.45f),
                new Color(1f, 1f, 1f, 0f),
            ],
        },
        Fill = GradientTexture2D.FillEnum.Radial,
        FillFrom = new Vector2(0.5f, 0.5f),
        FillTo = new Vector2(1f, 0.5f),
        Width = 32,
        Height = 32,
    };

    // ───────────────────────────── 节点 / 生命周期 ─────────────────────────────

    /// <summary>
    /// 在 <paramref name="layer"/> 上建一个特效根节点，<b>定位到世界坐标
    /// <paramref name="globalPos"/></b>，之后所有子节点都可以用"相对它"的坐标。
    ///
    /// <para>⚠️ 必须先 <c>AddChild</c> 再设 <c>GlobalPosition</c> ——
    /// 不在树上的节点没有全局变换，赋值会被丢掉（结果整套特效蹲在画布左上角）。</para>
    /// </summary>
    internal static Node2D Root(Control layer, Vector2 globalPos, string name)
    {
        var root = new Node2D { Name = name };
        layer.AddChild(root);
        root.GlobalPosition = globalPos;
        return root;
    }

    /// <summary>
    /// <b>到点自动删掉</b>。<b>每个</b>特效根节点都必须挂一个 —— 手写特效最常见的事故
    /// 就是"这一处漏了回收"，于是特效不可见地一直挂在画布上（帧率缓慢下降，
    /// 而且下次同名特效的 <c>GetNode</c> 会拿到旧的幽灵节点）。
    /// </summary>
    internal static void FreeAfter(Node node, double seconds)
    {
        SceneTree? tree = node.GetTree();

        if (tree == null)
        {
            return;
        }

        tree.CreateTimer(seconds, true, false, false).Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(node))
            {
                node.QueueFree();
            }
        };
    }

    /// <summary>
    /// <b>延时派发一步</b>。<paramref name="anchor"/> 只用来取 SceneTree
    /// （所以它被提前释放时这一步就自然不执行了 —— 正是我们要的）。
    ///
    /// <para>和"在时间轴里用 <c>Segment</c> 切一段"的区别：<c>After</c> 用来做
    /// <b>离散事件</b>（在第 0.2 / 0.45 / 0.7 秒各响一声、各闪一下），
    /// <c>Segment</c> 用来做<b>连续变化</b>。离散事件写成每帧判断
    /// "现在到点了吗"会在掉帧时漏掉一拍，用定时器不会。</para>
    /// </summary>
    internal static void After(Node anchor, double seconds, string what, Action action)
    {
        if (seconds <= 0.0)
        {
            Safe(what, action);
            return;
        }

        SceneTree? tree = anchor.GetTree();

        if (tree == null)
        {
            return;
        }

        tree.CreateTimer(seconds, true, false, false).Timeout += () => Safe(what, action);
    }

    /// <summary>
    /// ★★★ <b>整个特效库的核心</b>：给 <paramref name="owner"/> 开一条
    /// <c>0 → 1</c> 的时间轴，<b>每帧回调一次进度</b>，由回调自己决定改什么。
    ///
    /// <para>这样做的好处（对比"每个属性各开一个 tween"）：</para>
    /// <list type="bullet">
    ///   <item>所有属性天然同步 —— 不会出现"缩放到了但透明度还差半帧"；</item>
    ///   <item>任意复杂的联动都写得出来（比如"环扩到 0.5 时线宽正好 0.02"）；</item>
    ///   <item>一个 tween 就够，开销和对象数量无关；
    ///         参考实现里整个 580 行的特效库<b>只有这一处</b> <c>CreateTween</c>。</item>
    /// </list>
    ///
    /// <para>回调收到的是 <b>0~1 的线性进度</b>，不带缓动 ——
    /// 缓动由回调自己挑（<see cref="FieldFxEase"/>），因为同一段里不同属性
    /// 往往需要不同的曲线（位置用 OutExpo、透明度用 InCubic）。</para>
    ///
    /// <para>⚠️ <b>回调里的节点可能已经被释放</b>（比如战斗突然结束），
    /// 所以内部先查 <c>IsInstanceValid</c> 再调用 —— 否则会崩在中途。</para>
    ///
    /// <para>⚠️ <b><paramref name="owner"/> 必须在场景树里</b>（<c>CreateTween</c> 要求）。
    /// 常见错法是"先 <c>new</c> 节点 → 建好子节点顺便开 tween → 再 <c>AddChild</c>"，
    /// 这样 tween 会拿不到 SceneTree。这里改成<b>明确拒绝并写日志</b>，
    /// 不再让那种写法静默地什么都不播。</para>
    /// </summary>
    internal static Tween? Animate(Node owner, double seconds, Action<float> step)
    {
        if (!GodotObject.IsInstanceValid(owner) || !owner.IsInsideTree())
        {
            Log.Warn($"[场上特效] Animate 被跳过：节点 {owner?.Name} 还不在场景树里" +
                     "（tween 拿不到 SceneTree）—— 把这段动画挪到 _Ready 之后再开");
            return null;
        }

        Tween tween = owner.CreateTween();
        tween.TweenMethod(
            Callable.From<float>(t =>
            {
                if (GodotObject.IsInstanceValid(owner))
                {
                    step(t);
                }
            }),
            0f,
            1f,
            seconds);
        return tween;
    }

    /// <summary>
    /// ★ <b>点火 <c>GpuParticles2D</c>（延后若干帧）</b>。
    ///
    /// <para>⚠️ <b>实机踩过的坑</b>：粒子节点刚 <c>AddChild</c> 完就设
    /// <c>Emitting = true</c>，<b>这一发常常根本不出现</b>。
    /// 必须等世界渲染过一两帧再点火。项目里另一处（<c>SkillHitVfx</c>）为此专门写了
    /// <c>IgniteDelayFrames = 2</c> 的帧计数，这里做成通用工具。</para>
    /// </summary>
    internal static void Ignite(GpuParticles2D particles, int frames = 2)
    {
        if (!GodotObject.IsInstanceValid(particles))
        {
            return;
        }

        particles.Emitting = false;

        SceneTree? tree = particles.GetTree();

        if (tree == null)
        {
            particles.Emitting = true;
            return;
        }

        int remaining = Mathf.Max(1, frames);

        void Step()
        {
            if (!GodotObject.IsInstanceValid(particles))
            {
                return;
            }

            if (--remaining <= 0)
            {
                particles.Emitting = true;
                return;
            }

            SceneTree? next = particles.GetTree();

            if (next != null)
            {
                next.ProcessFrame += Step;
            }
        }

        tree.ProcessFrame += Step;
    }

    /// <summary>淡出并释放（<c>Modulate.a</c> 用 <c>OutCubic</c> 收到 0，然后删节点）。</summary>
    internal static void Dismiss(CanvasItem? item, float duration = 0.2f)
    {
        if (item == null || !GodotObject.IsInstanceValid(item))
        {
            return;
        }

        Animate(item, duration, t =>
        {
            Color c = item.Modulate;
            c.A = 1f - FieldFxEase.OutCubic(t);
            item.Modulate = c;
        });

        FreeAfter(item, duration + 0.05);
    }

    // ───────────────────────────── 几何 ─────────────────────────────

    /// <summary>
    /// ★ <b>代码算的多边形</b>（实心填充），带 SDF 抗锯齿和可选的叠加混合。
    ///
    /// <para>这是"不依赖美术资源画出任何形状"的入口：给它一圈顶点，
    /// 它给你一个锐利、发光、任意缩放的图形。圆形 / 星形 / 六边形
    /// 都有现成的顶点生成器（<see cref="Circle"/>、<see cref="Star"/>、<see cref="Hexagon"/>）。</para>
    /// </summary>
    internal static Polygon2D Poly(Node parent, Vector2[] points, Color color, bool additive = true)
    {
        var poly = new Polygon2D
        {
            Color = color,
            Material = new ShaderMaterial
            {
                Shader = additive ? FieldFxShaders.AaAddShader : FieldFxShaders.AaShader,
            },
        };

        Reshape(poly, points);
        parent.AddChild(poly);
        return poly;
    }

    /// <summary>
    /// <b>改一个已有多边形的形状</b>（顶点数可以变）。用于"星芒张开""环变形"这类逐帧改形状的效果。
    /// 内部同时更新抗锯齿着色器用的 <c>pts</c>/<c>n</c> —— <b>少了这一步，形状变了边缘还是旧轮廓</b>。
    /// </summary>
    internal static void Reshape(Polygon2D poly, Vector2[] points)
    {
        poly.Polygon = points;

        if (poly.Material is not ShaderMaterial mat)
        {
            return;
        }

        // 着色器里 pts 固定 128 长度，所以必须补零到 128 再传（短数组会被 Godot 拒绝）
        var padded = new Vector2[128];
        Array.Copy(points, padded, Math.Min(points.Length, 128));
        mat.SetShaderParameter("pts", padded);

        // n 只报真实点数，超出 128 的直接放弃抗锯齿（而不是画错）
        mat.SetShaderParameter("n", points.Length <= 128 ? points.Length : 0);
    }

    /// <summary>
    /// <b>把一条折线"加粗"成带状多边形</b>的顶点算法。
    ///
    /// <para>做法：对每个点取前后点的连线当切线，旋转 90° 得法线，
    /// 沿法线上下各推半个线宽。输出是<b>前半段正序 + 后半段逆序</b>拼成的闭合带
    /// （这个顶点顺序才能形成不自交的简单多边形）。</para>
    ///
    /// <para><paramref name="taper"/> = true 时线宽按 <c>sin(π·i/(n-1))</c> 变化：
    /// 两端细、中间粗 —— <b>这就是"锥形拖尾"</b>，也是<see cref="FieldFxShaders.AaAddShader"/>
    /// 系列里最像"光"的一条线。不做 taper 就是一根等宽的带，看着像塑料条。</para>
    /// </summary>
    internal static Vector2[] StrokePts(Vector2[] points, float width, bool taper = false)
    {
        int n = points.Length;

        if (n == 0)
        {
            return [];
        }

        if (n == 1)
        {
            // 单点退化成一个正方形，避免下面的法线算法出事
            float h = width * 0.5f;
            return
            [
                points[0] + new Vector2(-h, -h),
                points[0] + new Vector2(h, -h),
                points[0] + new Vector2(h, h),
                points[0] + new Vector2(-h, h),
            ];
        }

        var result = new Vector2[n * 2];

        for (int i = 0; i < n; i++)
        {
            Vector2 tangent = points[Math.Min(i + 1, n - 1)] - points[Math.Max(i - 1, 0)];
            Vector2 dir = tangent.Normalized();

            float w = taper
                ? width * Mathf.Max(Mathf.Sin(Mathf.Pi * i / (n - 1)), 0.15f)
                : width;

            // 法线 = 切线旋转 90°
            Vector2 normal = new Vector2(-dir.Y, dir.X) * (w * 0.5f);

            result[i] = points[i] + normal;
            result[n * 2 - 1 - i] = points[i] - normal;
        }

        return result;
    }

    /// <summary>加粗一条折线（见 <see cref="StrokePts"/>）。</summary>
    internal static Polygon2D Stroke(Node parent, Vector2[] points, Color color, float width,
        bool additive = true, bool taper = false)
    {
        return Poly(parent, StrokePts(points, width, taper), color, additive);
    }

    /// <summary>
    /// <c>Line2D</c>：<b>细线用它，不要用 <see cref="Stroke"/></b> ——
    /// 宽度小于 2px 的多边形会被抗锯齿整体洗淡，而 <c>Line2D</c> 有自己的一像素抗锯齿。
    /// </summary>
    internal static Line2D Line(Node parent, Vector2[] points, Color color, float width,
        bool closed = false, bool additive = true)
    {
        var line = new Line2D
        {
            Points = points,
            DefaultColor = color,
            Width = width,
            Closed = closed,
            JointMode = Line2D.LineJointMode.Round,
            Antialiased = true,
        };

        if (additive)
        {
            line.Material = Additive;
        }

        parent.AddChild(line);
        return line;
    }

    /// <summary>圆周顶点。<paramref name="squashY"/> &lt; 1 得到椭圆（做地面冲击波用）。</summary>
    internal static Vector2[] Circle(float radius, int segments = 32, float squashY = 1f)
    {
        segments = Mathf.Max(3, segments);
        var pts = new Vector2[segments];

        for (int i = 0; i < segments; i++)
        {
            float a = Mathf.Tau * i / segments;
            pts[i] = new Vector2(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius * squashY);
        }

        return pts;
    }

    /// <summary>
    /// 任意角数的星形（<paramref name="points"/> 个尖）。
    /// <paramref name="inner"/> 是内半径占外半径的比例：越小越尖。
    /// 第一个尖朝正上方。
    /// </summary>
    /// <summary>
    /// <b>一段圆弧</b>的顶点：从 <paramref name="fromRad"/> 到 <paramref name="toRad"/>。
    ///
    /// <para>用来画<b>断开的弧段</b>——狙击镜 / 瞄准环 / 刻度圈的每一段都是它。
    /// 完整的圆读起来是几何图形，<b>断开成几段的弧才有"仪器感"</b>。</para>
    /// </summary>
    internal static Vector2[] Arc(float radius, float fromRad, float toRad, int segments = 14,
        float squashY = 1f)
    {
        segments = Mathf.Max(2, segments);
        var pts = new Vector2[segments + 1];

        for (int i = 0; i <= segments; i++)
        {
            float a = Mathf.Lerp(fromRad, toRad, i / (float)segments);
            pts[i] = new Vector2(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius * squashY);
        }

        return pts;
    }

    internal static Vector2[] Star(float radius, int points = 4, float inner = 0.28f,
        float rotationDeg = 0f)
    {
        points = Mathf.Max(2, points);
        var pts = new Vector2[points * 2];
        float rot = Mathf.DegToRad(rotationDeg);

        for (int i = 0; i < points * 2; i++)
        {
            float r = i % 2 == 0 ? radius : radius * inner;
            float a = Mathf.Tau * i / (points * 2) - Mathf.Pi / 2f + rot;
            pts[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
        }

        return pts;
    }

    /// <summary>
    /// <b>色相取色</b>（HSV → Color）：<paramref name="hue"/> 是 0~1 的色相环位置。
    ///
    /// <para>用它是为了让<b>光谱感直接长在几何体上</b> —— 24 根针芒各取一环 hue，
    /// 就得到一整片色相渐变的光爆。这比"靠屏幕色散去拆颜色"可靠得多：
    /// 那条路要求画面上本身有有色中间调，而爆开瞬间画面全是过曝白 + 全黑，拆不出颜色。</para>
    /// </summary>
    internal static Color Hue(float hue, float sat = 0.85f, float val = 1f)
    {
        float h = Mathf.PosMod(hue, 1f) * 6f;
        int sector = Mathf.FloorToInt(h);
        float f = h - sector;

        float q = val * (1f - sat * f);
        float t = val * (1f - sat * (1f - f));

        return (sector % 6) switch
        {
            0 => new Color(val, t, 1f - sat),
            1 => new Color(q, val, 1f - sat),
            2 => new Color(1f - sat, val, t),
            3 => new Color(1f - sat, q, val),
            4 => new Color(t, 1f - sat, val),
            _ => new Color(val, 1f - sat, q),
        };
    }

    /// <summary>四角星芒（"叮"一下的那个十字光）——<paramref name="inner"/> 很小所以非常尖。</summary>
    internal static Vector2[] Star4(float radius, float inner = 0.06f) => Star(radius, 4, inner);

    /// <summary>
    /// ★★ <b>射线长短不一的星芒</b>——<see cref="Star"/> 所有尖等长，放大后像是拿尺子画的；
    /// 真实的光爆每根射线长短不一。
    ///
    /// <para><paramref name="jitter"/> 是尖端的随机缩短比例（0.3 ≈ 最短那根只有 70% 长）。
    /// 多叠几层不同 <paramref name="jitter"/>、不同角数的星芒，就有"密集光爆"的层次，
    /// 而不是一个规整的几何星形。</para>
    /// </summary>
    internal static Vector2[] StarVaried(float radius, int points, float inner, Random rng,
        float jitter = 0.30f, float rotationDeg = 0f)
    {
        points = Mathf.Max(2, points);
        var pts = new Vector2[points * 2];
        float rot = Mathf.DegToRad(rotationDeg);

        for (int i = 0; i < points * 2; i++)
        {
            bool tip = i % 2 == 0;
            float r = tip
                ? radius * (1f - jitter * (float)rng.NextDouble())
                : radius * inner;
            float a = Mathf.Tau * i / (points * 2) - Mathf.Pi / 2f + rot;
            pts[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
        }

        return pts;
    }

    /// <summary>
    /// ★★ <b>给星芒做"沿射线衰减"的顶点色</b>——<b>这是"光"和"剪贴画"的分界线</b>。
    ///
    /// <para>一个纯色多边形，不管多锐利，读起来都是<b>纸片</b>；而真实的光从核心到尖端是
    /// <b>白 → 暖 → 透明</b>一路衰掉的。<see cref="Star"/> 生成的顶点是"外尖 / 内谷"交替的
    /// （偶数索引 = 尖端），所以只要给尖端和谷分别上色，插值出来就是一条沿射线方向的渐变。</para>
    ///
    /// <para>⚠️ <b>设了顶点色之后 <c>Polygon2D.Color</c> 就失效了</b>（Godot 用顶点色替代它），
    /// 所以要做亮度脉动请改用 <c>Modulate</c>，别再写 <c>Color</c> —— 两者都写会得到
    /// 两套 alpha 相乘、整个元素黑掉（本文件 2026-10-18 踩过）。</para>
    /// </summary>
    /// <param name="core">谷（靠近中心）的颜色 —— 通常最亮最白。</param>
    /// <param name="tips">尖端的颜色 —— 通常偏暖、alpha 很低甚至为 0。</param>
    internal static void SetStarGradient(Polygon2D poly, Color core, Color tips)
    {
        Vector2[]? pts = poly.Polygon;

        if (pts == null || pts.Length == 0)
        {
            return;
        }

        var colors = new Color[pts.Length];

        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = i % 2 == 0 ? tips : core;   // 偶数索引 = 外尖
        }

        poly.VertexColors = colors;
    }

    internal static Vector2[] Hexagon(float radius) => Circle(radius, 6);

    // ───────────────────────────── 着色器四边形 ─────────────────────────────

    /// <summary>
    /// ★ <b>一块"画着色器的方框"</b>：以 (0,0) 为中心、边长 <paramref name="size"/>，
    /// 挂上 <paramref name="shader"/>，然后用 <see cref="SetParam"/> 逐帧改参数。
    ///
    /// <para><c>PivotOffset</c> 设成中心、<c>Position</c> 设成 -size/2，
    /// 于是<b>缩放围绕中心</b>发生（不设的话是绕左上角放大，会"跑偏"）。</para>
    /// </summary>
    internal static ColorRect ShaderQuad(Node parent, Shader shader, float size, float squashY = 1f)
    {
        var quad = new ColorRect
        {
            Size = new Vector2(size, size),
            Position = new Vector2(-size / 2f, -size / 2f),
            PivotOffset = new Vector2(size / 2f, size / 2f),
            Scale = new Vector2(1f, squashY),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Color = Colors.White,
            Material = new ShaderMaterial { Shader = shader },
        };

        parent.AddChild(quad);
        return quad;
    }

    /// <summary>改 <see cref="ShaderQuad"/> 的着色器参数。<b>名字写错不会报错，只会静默无效</b> —— 调参时留意。</summary>
    internal static void SetParam(CanvasItem quad, string name, Variant value)
    {
        if (quad.Material is ShaderMaterial mat)
        {
            mat.SetShaderParameter(name, value);
        }
    }

    // ───────────────────────────── 粒子 ─────────────────────────────

    /// <summary>
    /// ★★ <b>一发 GPU 粒子</b>——余烬、光尘、星火。
    ///
    /// <para>相比"手动摆 N 个 Sprite2D 再逐帧算位置"，这里的每一颗都是显卡自己算的：
    /// 每颗粒子有自己的速度、阻尼、重力、随生命期变化的尺寸与颜色。</para>
    ///
    /// <para><b>几个关键默认为什么这么设</b>：</para>
    /// <list type="bullet">
    ///   <item><c>Spread = 180</c> ⇒ 从 <c>Direction</c> 向两侧各张 180°，也就是<b>整个圆</b>里随机；
    ///         想做成"朝天喷的火花"就把 <c>Direction</c> 改成 (0,-1) 并把 spread 收小；</item>
    ///   <item><c>Damping</c> 2~6 ⇒ 喷出来很快减速，这是"能量炸开"而不是"匀速飞走"的关键；</item>
    ///   <item><c>Explosiveness = 0.95</c>（单发时）⇒ 所有粒子几乎同一帧出生，
    ///         才叫"炸"；不设的话它们会在 <c>Lifetime</c> 里均匀地漏出来，像烟雾发生器；</item>
    ///   <item><c>LocalCoords = false</c> ⇒ 发射后粒子的位置在世界里固定，
    ///         父节点再动也不会拖着它们走（否则看起来像"贴在角色身上的贴纸"）。</item>
    /// </list>
    ///
    /// <para>⚠️ 建完<b>不要</b>自己设 <c>Emitting = true</c>，用 <see cref="Ignite"/>。</para>
    /// </summary>
    internal static GpuParticles2D Sparks(
        Node parent,
        Color color,
        int amount,
        float speedMin,
        float speedMax,
        float lifetime,
        float gravity = 0f,
        float scaleMin = 0.15f,
        float scaleMax = 0.45f,
        bool oneShot = true,
        Color[]? ramp = null)
    {
        Gradient gradient = ramp != null
            ? RampGradient(ramp)
            : new Gradient
            {
                Offsets = [0f, 0.7f, 1f],
                Colors =
                [
                    Colors.White,
                    Colors.White,
                    new Color(1f, 1f, 1f, 0f),
                ],
            };

        var process = new ParticleProcessMaterial
        {
            Direction = new Vector3(0f, -1f, 0f),
            Spread = 180f,
            InitialVelocityMin = speedMin,
            InitialVelocityMax = speedMax,
            Gravity = new Vector3(0f, gravity, 0f),
            ScaleMin = scaleMin,
            ScaleMax = scaleMax,
            DampingMin = 2f,
            DampingMax = 6f,
            // 有 ramp 时颜色交给 ramp（Color 必须是白，否则会把 ramp 乘暗）
            Color = ramp == null ? color : Colors.White,
            ColorRamp = new GradientTexture1D { Gradient = gradient },
        };

        var particles = new GpuParticles2D
        {
            Amount = Mathf.Max(1, amount),
            Lifetime = lifetime,
            OneShot = oneShot,
            Explosiveness = oneShot ? 0.95f : 0f,
            ProcessMaterial = process,
            Texture = SoftDot,
            Material = Additive,
            LocalCoords = false,
            Emitting = false,
        };

        parent.AddChild(particles);
        return particles;
    }

    /// <summary>
    /// 把一组颜色铺成渐变，<b>并在末尾自动补一个完全透明的同色</b>
    /// —— 否则粒子到寿命终点会突然消失（硬切），而不是淡出。
    /// </summary>
    internal static Gradient RampGradient(Color[] colors)
    {
        if (colors.Length == 0)
        {
            return new Gradient { Colors = [Colors.White, new Color(1f, 1f, 1f, 0f)] };
        }

        int n = colors.Length + 1;
        var offsets = new float[n];
        var result = new Color[n];

        for (int i = 0; i < colors.Length; i++)
        {
            offsets[i] = (float)i / (n - 1);
            result[i] = colors[i];
        }

        offsets[n - 1] = 1f;
        result[n - 1] = new Color(colors[^1], 0f);

        return new Gradient { Offsets = offsets, Colors = result };
    }

    // ───────────────────────────── 杂项 ─────────────────────────────

    /// <summary>
    /// ★ <b>着色器健康自检（铁律 57）</b>：GLSL 里用了保留字 / 语法错误时，
    /// Godot <b>不报错、只是静默忽略整个材质</b> —— 效果"什么都不画"，且日志干干净净。
    ///
    /// <para>唯一能查到的信号是：<b>该着色器的 uniform 列表为空</b>。
    /// 所以每个动态建的 shader 都应该过一遍这里，把它变成一条明确的 ERROR。</para>
    /// </summary>
    internal static bool ReportShaderHealth(Shader? shader, string label)
    {
        try
        {
            int count = shader?.GetShaderUniformList()?.Count ?? 0;

            if (count == 0)
            {
                Log.Error($"[场上特效] 着色器 {label} 没有编译成功（uniform 列表为空）——" +
                          "效果不会显示。检查 GLSL 语法与保留字（铁律 57）");
                return false;
            }

            Log.Info($"[场上特效] 着色器 {label} 就绪（uniform {count} 个）");
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"[场上特效] 着色器 {label} 自检抛异常：{e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>安全执行：一个子效果抛异常不该让整串特效停下（并且要留日志，别静默）。</summary>
    private static void Safe(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.Warn($"[场上特效] {what} 抛异常：{e.GetType().Name}: {e.Message}");
        }
    }
}
