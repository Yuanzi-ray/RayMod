using System;
using System.Collections.Generic;
using Godot;

namespace RayMod.Art;

/// <summary>
/// <b>拖拽手牌时留下「线框残影」</b>（2026-10-19，用户提的需求："拖动卡牌时线框留下残影"）。
///
/// <para><b>为什么是线框而不是实心</b>：<see cref="CardFlyTrail"/> 的飞行残影已经定稿成
/// 「轮廓亮、面极淡」的全息线框（用户："我其实挺喜欢那个线框的感觉"）。
/// 拖拽和飞行是同一套视觉语言，所以这里复用同一种画法，只是额外加了四角的角标
/// —— 拖动时卡牌本体会挡住一部分残影，角标能让"这张卡刚才在这里"一眼读出来。</para>
///
/// <para><b>入口</b>：<c>NMouseCardPlay.LerpToMouse(NHandCardHolder)</c> 的 Postfix。
/// 那个方法是拖拽期间的<b>每帧循环体</b>：
/// <code>do { await LerpToMouse(Holder); } while (!IsCardInPlayZone() &amp;&amp; !cancelled);</code>
/// 而且它是 <c>async</c> 方法 —— async 方法的状态机<b>不会被 JIT 内联</b>，
/// 所以挂在这里不受铁律 73 影响（不像 <c>NCardFlyVfx.Create</c> 那种会被内联的小方法）。</para>
///
/// <para><b>稀疏控制</b>：不是每帧都留一张，而是"卡牌相对上一张残影走过 <see cref="DragGhostGap"/>
/// 像素"才留一张。这样手慢慢挪的时候不会堆出一坨残影，快速甩牌时又足够密。</para>
/// </summary>
internal partial class CardFieldVfx
{
    // ── 参数（都在这里，想调手感只改这一块）──

    /// <summary>相邻两张残影的世界距离（像素）。</summary>
    private const float DragGhostGap = 24f;

    /// <summary>单张残影寿命（秒）。</summary>
    private const float DragGhostLife = 0.30f;

    /// <summary>同时最多存在多少张残影（防止快速甩牌时刷出几百个节点）。</summary>
    private const int DragGhostMaxAlive = 14;

    /// <summary>线段颜色 = 卡图奶金 <c>#F9DA97</c>。</summary>
    private static readonly Color DragGhostTint = new(0.976f, 0.855f, 0.592f);

    /// <summary>角标高光 = 卡图暖白 <c>#FFF6DB</c>。</summary>
    private static readonly Color DragGhostHot = new(1.000f, 0.965f, 0.859f);

    private static readonly List<DragGhost> DragGhosts = new();

    private static Vector2 _dragGhostAnchor;
    private static ulong _dragGhostCard;
    private static bool _dragGhostHasAnchor;

    /// <summary>
    /// 由 <c>Patches\CardReloadVfxPatch.CardDragGhostPatch</c> <b>每帧</b>调用一次（拖拽期间）。
    ///
    /// <para><b>★ 执行顺序按"便宜的先做"排</b>：这个方法在拖动时每秒被调 60 次，
    /// 而 <c>TestModeIsOn()</c> / <see cref="VfxParentForEffects"/> 都是<b>每次调用都走反射</b>的
    /// （<c>AccessTools.TypeByName</c> 会扫程序集）。所以：
    /// <list type="number">
    ///   <item>先看总闸 <see cref="CardFieldVfx.Enabled"/>（纯字段）；</item>
    ///   <item>再算"走了多远"，不够远<b>直接返回</b> —— 大多数帧都在这一步返回；</item>
    ///   <item>只有真要生成了，才做 TestMode 探测、反射取尺寸、反射取容器。</item>
    /// </list></para>
    /// </summary>
    internal static void SpawnDragGhost(Control? card)
    {
        try
        {
            if (!Enabled)
            {
                return;
            }

            if (card == null || !GodotObject.IsInstanceValid(card) || !card.IsInsideTree())
            {
                return;
            }

            Transform2D xf = card.GetGlobalTransform();
            ulong id = card.GetInstanceId();

            // ★ 防御（2026-10-20，用户反馈"线框轨迹也有到处乱飞的现象"）：
            //   坐标离谱就这一帧不画。卡牌在过渡态（刚被回收 / 还在布局 / 变换还没写）
            //   时读到的 GlobalTransform 可能是原点或极大值，画出来就是"屏幕角落莫名一道线框"。
            //   判断本身很便宜（一次 Rect2 包含测试），比事后排查便宜得多。
            if (card.GetViewport() is Viewport vp)
            {
                Rect2 visible = vp.GetVisibleRect();

                if (!visible.Grow(visible.Size.Length() * 0.5f).HasPoint(xf.Origin))
                {
                    return;
                }
            }

            // 上一张残影离得不够远 ⇒ 这次不留（这就是稀疏控制的全部）。
            // ★ 这里用 Transform2D.Origin（卡牌左上角）而不是卡面中心 —— 中心要反射取 Body 尺寸，
            //   而热路径上不该每秒反射 60 次。拖拽时卡几乎不旋转，左上角距离和中心距离等价。
            if (_dragGhostHasAnchor
                && _dragGhostCard == id
                && _dragGhostAnchor.DistanceTo(xf.Origin) < DragGhostGap)
            {
                return;
            }

            _dragGhostAnchor = xf.Origin;
            _dragGhostCard = id;
            _dragGhostHasAnchor = true;

            // ── 到这里才值得付反射的代价 ──
            if (TestModeIsOn())
            {
                return;
            }

            // 卡面尺寸要反射取 Body（NCard.Size 实测是 0,0），和飞行残影共用同一份解析。
            Vector2 size = CardFlyTrail.CardVisualSize(card);

            if (size.X < 8f || size.Y < 8f)
            {
                return;
            }

            Node? host = VfxParentForEffects();

            if (host == null || !GodotObject.IsInstanceValid(host) || !host.IsInsideTree())
            {
                return;
            }

            // 清掉已经失效的（它们自己 QueueFree，但这里也要把引用剔除）
            DragGhosts.RemoveAll(g => g == null || !GodotObject.IsInstanceValid(g));

            if (DragGhosts.Count >= DragGhostMaxAlive)
            {
                return;
            }

            // ★★★ 2026-10-19 修正：「残影始终在卡牌右下一些距离」的根因。
            //
            //   之前写的是 `xf * (size * 0.5f)` —— 那是把"局部原点在卡牌左上角"当成了前提。
            //   但实测（见 `Art\CardFieldVfx.ShellSleeve.cs:222-229` 的实测记录）：
            //     `NCard.Size` = (0,0)，子节点**全部用负偏移**排布，
            //     `%Frame: pos=(-150,-211) size=(300,422)`
            //   ⇒ **卡片局部坐标的原点就在卡面正中心**，卡面范围是 x[-150,150] × y[-211,211]。
            //   所以再乘半个尺寸，等于凭空多给了一个 (150,211) 的偏移 —— 正好是"右下半个卡身"。
            //
            //   而 `DragGhost._Draw` 是以自身原点为中心画 `Rect2(-half, size)`，
            //   两个"居中"一凑，位置就对了：把残影直接放在卡牌的变换原点即可。
            var ghost = new DragGhost
            {
                GlyphSize = size,
                Life = DragGhostLife,
            };

            host.AddChild(ghost);
            ghost.GlobalPosition = xf.Origin;

            // 旋转：Transform2D 的两根基分别是
            //   X = (cosθ, sinθ)、Y = (-sinθ, cosθ)
            // ⇒ 抽角度必须用 X 基：Atan2(X.Y, X.X) = θ。
            //   （曾经写成 Atan2(Y.X, X.X) = Atan2(-sinθ, cosθ) = -θ，角度取反 ——
            //     直着拖看不出来，手牌带 ±15° 扇形倾角时残影会反向歪。）
            ghost.Rotation = Mathf.Atan2(xf.X.Y, xf.X.X) + CardFlyTrail.CardBodyRotation(card);
            ghost.Scale = new Vector2(xf.X.Length(), xf.Y.Length()) * CardFlyTrail.CardBodyScale(card);

            DragGhosts.Add(ghost);
        }
        catch
        {
            // 表现层失败绝不外抛
        }
    }

    /// <summary>
    /// 一张拖拽残影。
    /// <para>★ 自己开 <c>_Process</c> 自己走完自己释放 —— 拖拽残影没有"父节点 Tick"这一层，
    /// 而同时最多只有 <see cref="DragGhostMaxAlive"/> 个，开销可以忽略。</para>
    /// </summary>
    internal sealed partial class DragGhost : Node2D
    {
        internal Vector2 GlyphSize = new(300f, 422f);
        internal float Life = DragGhostLife;

        private float _t;
        private bool _dead;

        public override void _Ready()
        {
            Material = FieldFx.Additive;
            ZIndex = -1;            // 压在其它战斗特效下面，别抢主体
            SetProcess(true);
        }

        public override void _Process(double delta)
        {
            try
            {
                if (_dead)
                {
                    return;
                }

                _t += (float)delta;

                if (_t >= Life)
                {
                    Retire();
                    return;
                }

                QueueRedraw();
            }
            catch
            {
                // _Process 里抛异常会让 Godot 把这个节点一直留在树上（表现为"残影卡住不消失"）
                Retire();
            }
        }

        public override void _ExitTree()
        {
            DragGhosts.Remove(this);
        }

        private void Retire()
        {
            _dead = true;
            DragGhosts.Remove(this);
            QueueFree();
        }

        public override void _Draw()
        {
            if (GlyphSize.X < 8f || GlyphSize.Y < 8f)
            {
                return;
            }

            float k = Mathf.Clamp(_t / Mathf.Max(Life, 0.01f), 0f, 1f);

            // 1.7 次方淡出：比线性掉得快（老残影不拖泥带水），但比平方慢（不会"刚出现就没了"）
            float fade = Mathf.Pow(1f - k, 1.7f);

            var half = GlyphSize * 0.5f;

            // ① 极淡的"面"——只为了让轮廓读起来是一张卡，而不是一个空框
            DrawRect(new Rect2(-half, GlyphSize), new Color(DragGhostTint, 0.03f * fade));

            // ② 外轮廓（主体）
            DrawRect(new Rect2(-half, GlyphSize), new Color(DragGhostTint, 0.88f * fade),
                filled: false, width: 2.0f);

            // ③ 内缩一圈的第二条轮廓（全息/技术感的来源）
            DrawRect(new Rect2(-half + new Vector2(4f, 4f), GlyphSize - new Vector2(8f, 8f)),
                new Color(DragGhostHot, 0.38f * fade), filled: false, width: 1.2f);

            // ④ 四角角标 —— 拖动时卡牌本体会盖住残影的一部分，角标负责"框住它"
            float arm = Mathf.Min(GlyphSize.X, GlyphSize.Y) * 0.16f;
            var corner = new Color(DragGhostHot, 0.95f * fade);

            for (int i = 0; i < 4; i++)
            {
                float sx2 = (i & 1) == 0 ? -1f : 1f;
                float sy2 = (i & 2) == 0 ? -1f : 1f;

                var c = new Vector2(half.X * sx2, half.Y * sy2);

                DrawLine(c, c + new Vector2(-arm * sx2, 0f), corner, 3.2f);
                DrawLine(c, c + new Vector2(0f, -arm * sy2), corner, 3.2f);
            }
        }
    }
}
