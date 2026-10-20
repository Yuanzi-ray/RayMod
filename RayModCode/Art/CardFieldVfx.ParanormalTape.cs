using System;
using Godot;

namespace RayMod.Art;

/// <summary>
/// <b>「灵异录像带」的牌面表现：一盘磁带把这行字吃进去。</b>
///
/// <para><b>用户 2026-10-20 选定</b>：下一张做「灵异录像带」，并追加一条
/// "并且更改本回合手牌（东倒西歪，鼠标掠过会晃动）" —— 手牌那半在
/// <c>Art\CardFieldVfx.TapeHand.cs</c>，这里只管<b>这张牌自己</b>。</para>
///
/// <para><b>为什么是"磁带"而不是"光效"</b>：用户已经明确过两次方向 ——
/// "我们的特效要向动画的方向转变。用更加实际的元素来替代原始的光效和粒子"。
/// 录像带的实体就是<b>磁带盒 + 两个转轴 + 抽出来的磁带条</b>，全部可以用
/// <c>DrawRect</c> / <c>DrawCircle</c> / <c>DrawLine</c> / <c>Line2D</c> 画出来，零贴图。</para>
///
/// <para><b>⚠️ 只用已经验证能在 <c>_Draw</c> 里画出来的原语</b>：
/// <c>DrawPolygon</c>（含 <c>DrawColoredPolygon</c>）在这条链路上<b>画不出来</b>
/// （见 <c>CardFieldVfx.Streak.cs</c> 里那两轮翻车），
/// 稳的是 <c>DrawRect</c> / <c>DrawCircle</c> / <c>DrawLine</c> / <c>DrawPolyline</c> / <c>DrawArc</c> / <c>Line2D</c>。</para>
/// </summary>
internal partial class CardFieldVfx
{
    private const float TapeLife = 1.15f;
    private const float TapeGlitchTime = 0.16f;
    private const float TapeBodyIn = 0.34f;
    private const float TapeSpoolFrom = 0.30f;
    private const float TapeSpoolTo = 0.78f;
    private const float TapeLeaveAt = 0.78f;

    /// <summary>磁带盒调色（旧塑料 + 磁带棕 + 标签纸白）。</summary>
    private static readonly Color TapeShell = new(0.129f, 0.125f, 0.149f);
    private static readonly Color TapeShellLit = new(0.243f, 0.231f, 0.267f);
    private static readonly Color TapeReel = new(0.086f, 0.082f, 0.102f);
    private static readonly Color TapeRibbonCol = new(0.325f, 0.204f, 0.125f);
    private static readonly Color TapeLabel = new(0.878f, 0.855f, 0.788f);

    /// <summary>
    /// 出牌时调用（挂在 <see cref="Play"/> 的 <c>BulletModeOnCardPlay</c> 旁边，
    /// <b>必须在 <c>SelectProfile</c> 早退之前</b> —— 这张牌没有 vfx 配置文件）。
    /// </summary>
    internal static void TapeCardOnPlay(object? card, object? play)
    {
        try
        {
            if (card is not RayMod.Cards.ParanormalTape)
            {
                return;
            }

            // ① 本回合手牌东倒西歪
            ArmTapeHand();

            // ①-b 她身边浮出鬼影（用户：「做围绕在莱伊身边的鬼影」）
            AttachTapeGhosts(card);

            // ①-c 预排墓碑：用户要「造成伤害同时墓碑落地」，所以要在这里倒排起飞时刻
            TapeGraveOnPlay(card, play);

            Diag("tapestep1", "[录像带·牌面] 进入：ArmTapeHand 已调");

            // ② 牌面：磁带把这张牌吃掉
            Control? cardNode = FindCardNode(card);

            if (cardNode == null)
            {
                Diag("tapecard", "[录像带·牌面] FindOnTable 返回 null（牌已不在场）");
                return;
            }

            Diag("tapestep2", $"[录像带·牌面] 找到牌节点 Enabled={Enabled} Test={TestModeIsOn()}");

            if (!Enabled || TestModeIsOn())
            {
                return;
            }

            Vector2 size = new(300f, 422f);

            if (cardNode.GetNodeOrNull("%Frame") is Control frame && frame.Size.X > 8f)
            {
                size = frame.Size;
            }

            var overlay = new TapeCassetteOverlay
            {
                Name = "RayModTapeCassette",
                MouseFilter = Control.MouseFilterEnum.Ignore,
                CardSize = size,
            };

            cardNode.AddChild(overlay);
            overlay.Position = new Vector2(-size.X * 0.5f, -size.Y * 0.5f);
            overlay.Size = size;
            overlay.PivotOffset = size * 0.5f;
            overlay.SetProcess(true);
            overlay.QueueRedraw();

            LogOnce($"[录像带·牌面] 磁带挂上：卡面 {size.X:F0}×{size.Y:F0}");
            Diag("tapestep3", $"[录像带·牌面] 已挂上 {overlay.Name} 尺寸={overlay.Size} 位置={overlay.Position}");

            // ③ 鬼影：几层半透明的卡框从牌上飘走（用户 2026-10-20："打出时加点鬼影"）
            if (VfxParentForEffects() is Control layer)
            {
                SpawnTapeGhosts(layer, cardNode.GlobalPosition, size);
            }
        }
        catch (Exception e)
        {
            Diag("tapecardx", $"[录像带·牌面] {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// 鬼影：四层半透明的卡框剪影，从卡面往上飘、边飘边转、边飘边淡。
    /// <para><b>为什么用 <c>Line2D</c> 框而不是实心面</b>：这条链路上 <c>DrawPolygon</c> 画不出来
    /// （见 <c>CardFieldVfx.Streak.cs</c>），而"线框全息"正好也是用户喜欢的那个调子。</para>
    /// </summary>
    private static void SpawnTapeGhosts(Control layer, Vector2 centerGlobal, Vector2 size)
    {
        try
        {
            const int ghosts = 4;

            for (int i = 0; i < ghosts; i++)
            {
                var root = new Node2D { Name = $"RayModTapeGhost{i}" };
                layer.AddChild(root);
                root.GlobalPosition = centerGlobal + new Vector2((i - 1.5f) * 12f, 0f);

                Vector2 startPos = root.Position;
                float halfW = size.X * (0.46f - i * 0.045f);
                float halfH = size.Y * (0.46f - i * 0.045f);

                Vector2[] outerRect =
                {
                    new(-halfW, -halfH), new(halfW, -halfH), new(halfW, halfH), new(-halfW, halfH),
                };

                Vector2[] innerRect =
                {
                    new(-halfW * 0.86f, -halfH * 0.86f), new(halfW * 0.86f, -halfH * 0.86f),
                    new(halfW * 0.86f, halfH * 0.86f), new(-halfW * 0.86f, halfH * 0.86f),
                };

                Color cold = new(0.70f, 0.92f, 1.00f);
                Line2D frame = FieldFx.Line(root, outerRect, new Color(cold.R, cold.G, cold.B, 0.34f - i * 0.05f), 2.4f, closed: true);
                Line2D frameInner = FieldFx.Line(root, innerRect, new Color(0.90f, 0.99f, 1.00f, 0.20f - i * 0.03f), 1.2f, closed: true);

                frame.Material = FieldFx.Additive;
                frameInner.Material = FieldFx.Additive;

                float delay = i * 0.08f;
                float drift = 130f + i * 30f;
                float spin = (i % 2 == 0 ? 1f : -1f) * (4f + i * 2f);
                float life = 0.90f;
                var node = root;

                FieldFx.After(root, delay, "[录像带·鬼影]", () =>
                {
                    FieldFx.Animate(node, life, k =>
                    {
                        float e = FieldFxEase.OutCubic(k);
                        node.Position = startPos + new Vector2(0f, -drift * e);
                        node.Rotation = Mathf.DegToRad(spin * e);
                        float a = (1f - k) * (1f - k);
                        frame.Modulate = new Color(1f, 1f, 1f, a);
                        frameInner.Modulate = new Color(1f, 1f, 1f, a * 0.8f);
                    });

                    FieldFx.FreeAfter(node, life + 0.06f);
                });
            }
        }
        catch (Exception e)
        {
            Diag("tapeghost", $"[录像带·鬼影] {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// 一盘盖在牌上的磁带：先"信号不良"横向错位，再实体化，然后两个转轴狂转把磁带抽出来，
    /// 最后连同这张牌一起往左下角卷走。
    /// </summary>
    private sealed partial class TapeCassetteOverlay : Control
    {
        internal Vector2 CardSize = new(300f, 422f);

        private float _t;
        private float _reelAngle;

        public override void _Ready()
        {
            // ★ 运行期 new 出来的节点在本工程不会自动跑 _Process。
            SetProcess(true);
        }

        public override void _Process(double delta)
        {
            _t += (float)delta;
            _reelAngle += delta > 0 ? (float)delta * 1080f : 0f;

            if (_t >= TapeLife)
            {
                QueueFree();
                return;
            }

            QueueRedraw();
        }

        public override void _Draw()
        {
            if (_t >= TapeLife)
            {
                return;
            }

            // ── 阶段一：信号不良（几条横向错位带）──
            if (_t < TapeGlitchTime + 0.20f)
            {
                DrawGlitchBands();
            }

            // ── 阶段二：磁带盒实体化 ──
            float bodyK = Mathf.Clamp(_t / TapeBodyIn, 0f, 1f);
            float bodyIn = FieldFxEase.OutBack(bodyK);

            if (bodyK <= 0.01f)
            {
                return;
            }

            // ── 阶段三：卷走 ──
            float leaveK = Mathf.Clamp((_t - TapeLeaveAt) / (TapeLife - TapeLeaveAt), 0f, 1f);
            float leave = FieldFxEase.InCubic(leaveK);

            Vector2 center = CardSize * 0.5f;
            Vector2 box = new(Mathf.Min(CardSize.X * 0.80f, 250f), Mathf.Min(CardSize.Y * 0.46f, 176f));

            // 离开：整体缩小 + 往下沉 + 侧移
            float scale = Mathf.Lerp(bodyIn, 1f, bodyK) * (1f - (leave * 0.55f));
            Vector2 slide = new(-CardSize.X * 0.18f * leave, CardSize.Y * 0.30f * leave);

            if (leaveK > 0.01f)
            {
                DrawSetTransform(center + slide, leave * 0.35f, Vector2.One * Mathf.Max(0.05f, scale));
            }
            else
            {
                DrawSetTransform(center, 0f, Vector2.One * Mathf.Max(0.05f, scale * Mathf.Lerp(0.82f, 1f, bodyK)));
            }

            float alpha = bodyK < 1f ? bodyK : 1f - (leave * 0.85f);

            DrawCassette(box, alpha);

            DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
        }

        /// <summary>"信号不良"：几条横向色带上下错位，模拟录像带撕裂。</summary>
        private void DrawGlitchBands()
        {
            int bands = 5;

            for (int i = 0; i < bands; i++)
            {
                // 每 0.05s 换一次位置 —— 抖得像真的信噪比崩了
                int step = (int)(_t * 20f) + (i * 7);
                float seed = ((step * 9301 + (i * 49297)) % 233280) / 233280f;

                float h = Mathf.Lerp(6f, 26f, seed);
                float y = seed * (CardSize.Y - h);
                float x = Mathf.Lerp(-10f, 14f, (seed * 3f) % 1f);

                var tint = new Color(
                    0.75f + (seed * 0.25f),
                    0.70f + (seed * 0.20f),
                    0.80f,
                    0.10f + (seed * 0.16f));

                DrawRect(new Rect2(x, y, CardSize.X, h), tint);
            }
        }

        /// <summary>磁带盒本体：外壳 / 标签纸 / 观察窗 / 两个转轴 / 抽出来的磁带条。</summary>
        private void DrawCassette(Vector2 box, float alpha)
        {
            float w = box.X;
            float h = box.Y;
            float left = -w * 0.5f;
            float top = -h * 0.5f;

            // 外壳（两层：暗底 + 上沿受光）
            DrawRect(new Rect2(left, top, w, h), new Color(TapeShell, alpha));
            DrawRect(new Rect2(left, top, w, h * 0.16f), new Color(TapeShellLit, alpha * 0.9f));
            DrawRect(new Rect2(left, top + h * 0.84f, w, h * 0.16f), new Color(TapeShellLit, alpha * 0.55f));

            // 标签纸（上半）
            float labelH = h * 0.24f;
            DrawRect(new Rect2(left + w * 0.06f, top + h * 0.10f, w * 0.88f, labelH),
                new Color(TapeLabel, alpha * 0.92f));

            // 标签上用三道短杠假装写了字（不放真字符，避免字体依赖）
            for (int i = 0; i < 3; i++)
            {
                DrawRect(
                    new Rect2(left + w * (0.12f + (i * 0.16f)), top + h * (0.16f + (i % 2 == 0 ? 0f : 0.06f)),
                        w * 0.11f, h * 0.035f),
                    new Color(0.16f, 0.15f, 0.18f, alpha * 0.75f));
            }

            // 观察窗（中间）
            float winH = h * 0.44f;
            float winTop = top + h * 0.40f;
            DrawRect(new Rect2(left + w * 0.08f, winTop, w * 0.84f, winH), new Color(0.055f, 0.055f, 0.070f, alpha));
            DrawRect(new Rect2(left + w * 0.08f, winTop, w * 0.84f, 3f), new Color(TapeShellLit, alpha * 0.8f));

            // 两个转轴（相对位置固定；卷带时左边变小右边变大）
            float spoolK = Mathf.Clamp((_t - TapeSpoolFrom) / (TapeSpoolTo - TapeSpoolFrom), 0f, 1f);
            float r0 = Mathf.Lerp(w * 0.165f, w * 0.095f, spoolK);
            float r1 = Mathf.Lerp(w * 0.095f, w * 0.165f, spoolK);

            Vector2 c0 = new(left + w * 0.30f, winTop + (winH * 0.5f));
            Vector2 c1 = new(left + w * 0.70f, winTop + (winH * 0.5f));

            DrawSpool(c0, r0, alpha);
            DrawSpool(c1, r1, alpha);

            // 磁带条：两个转轴之间一段（卷得越多越短），再往盒外抽一条
            if (_t >= TapeSpoolFrom)
            {
                var a = new Vector2(c0.X + r0, c0.Y);
                var b = new Vector2(c1.X - r1, c1.Y);
                DrawLine(a, b, new Color(TapeRibbonCol, alpha * 0.95f), Mathf.Max(2f, h * 0.035f), true);

                DrawSpillingRibbon(new Vector2(c1.X, c1.Y + r1), alpha, spoolK);
            }
        }

        /// <summary>一个转轴：亮环 + 暗盘 + 内孔 + 三根辐条（辐条跟 `_reelAngle` 转 ⇒ 看得出在转）。</summary>
        private void DrawSpool(Vector2 center, float radius, float alpha)
        {
            // 亮环：磁带盘在暗色窗口里是"金属圈"，没有这一圈就只剩一团黑
            DrawArc(center, radius, 0f, Mathf.Tau, 28, new Color(TapeLabel, alpha * 0.55f),
                Mathf.Max(1.4f, radius * 0.10f), true);

            DrawCircle(center, radius * 0.94f, new Color(TapeReel, alpha));
            DrawCircle(center, radius * 0.30f, new Color(TapeLabel, alpha * 0.85f));

            for (int i = 0; i < 3; i++)
            {
                float a = Mathf.DegToRad(_reelAngle + (i * 120f));
                Vector2 dir = new(Mathf.Cos(a), Mathf.Sin(a));
                DrawLine(center + (dir * radius * 0.32f), center + (dir * radius * 0.86f),
                    new Color(TapeLabel, alpha * 0.70f), Mathf.Max(1.2f, radius * 0.13f), true);
            }
        }

        /// <summary>从磁带盒右下角抽出去的一条带子（波浪 + 越抽越长）。</summary>
        private void DrawSpillingRibbon(Vector2 from, float alpha, float k)
        {
            const int Seg = 22;

            var pts = new Vector2[Seg + 1];
            float len = Mathf.Lerp(20f, CardSize.Y * 0.55f, k);

            for (int i = 0; i <= Seg; i++)
            {
                float f = i / (float)Seg;
                float x = from.X + (f * len * 0.85f);
                float y = from.Y + (f * len * 0.45f) + (Mathf.Sin((f * 5f) - (_t * 9f)) * 10f * f);

                pts[i] = new Vector2(x, y);
            }

            DrawPolyline(pts, new Color(TapeRibbonCol, alpha * 0.9f), Mathf.Max(2.4f, CardSize.X * 0.012f), true);
            DrawPolyline(pts, new Color(0.62f, 0.42f, 0.24f, alpha * 0.45f),
                Mathf.Max(1f, CardSize.X * 0.005f), true);
        }
    }
}
