using System;
using System.Linq;
using System.Reflection;
using Godot;

namespace RayMod.Art;

/// <summary>
/// <b>「牌面被烫穿」</b> —— 出牌那一刻，把<b>打出去的这张牌本身</b>演出来（2026-10-20）。
///
/// <para><b>为什么需要这一层</b>：到上一版为止，狙击/霰弹枪已经在四个层上做足了
/// —— 她的动作（后坐力）、枪口、弹道、受击方（抖动/白闪/碎屑/弹孔/烟）。
/// 但<b>玩家眼睛一直盯着的那个东西 —— 手牌里那张牌 —— 从出牌到飞走，一个像素都没变过</b>。
/// 一张 300×422 的大卡片在屏幕中央摊开、被打出去，结果它自己毫无反应，
/// 这就是"堆料还不够"的最后一块空洞。</para>
///
/// <para><b>为什么能拿到这张牌的节点</b>：<c>NCard.FindOnTable(CardModel, PileType?)</c>
/// （<c>MegaCrit.Sts2.Core.Nodes.Cards\NCard.cs:793</c>）是原版给"从模型找场上那张牌"
/// 用的静态入口，出牌期间它走 <c>PileType.Play ⇒ nCombatUi.GetCardFromPlayContainer(card)</c>。
/// 我们只在伤害钩子里调一次 —— 那一刻牌正好摊在中央、绝对还在场。</para>
///
/// <para><b>演什么</b>（0.62 秒）：
/// ① 打穿一个洞（暗核 + 白热边）；② 裂纹从洞里串到牌边；
/// ③ 焦痕从洞往外糊开（非加色，越糊越黑）；④ 牌炸成二十来块碎片飞出去。
/// 全程<b>不动卡片节点本身的 Position/Rotation/Modulate</b> —— 那是游戏的（它还要飞向弃牌堆），
/// 我们只在卡片<b>上面</b>叠一个跟随的 Control。跟错了就会撕牌动画。</para>
/// </summary>
internal partial class CardFieldVfx
{
    // ── 调色板：一张雷伊的牌被烧穿，颜色只该有这四种 ────────────────────
    private static readonly Color BurnPaper = new(0.898f, 0.878f, 0.823f); // 纸白
    private static readonly Color BurnFrame = new(0.957f, 0.800f, 0.169f); // #F4CC2B 卡框金
    private static readonly Color BurnChar = new(0.176f, 0.137f, 0.098f);  // 焦黑
    private static readonly Color BurnHot = new(1.000f, 0.965f, 0.859f);   // #FFF6DB 白热

    private const float BurnLife = 0.62f;
    private const float BurnHoleTime = 0.10f;    // 洞长到满
    private const float BurnCrackTime = 0.16f;   // 裂纹串完
    private const float BurnScorchTime = 0.34f;  // 焦痕糊满
    private const float BurnShardFly = 0.55f;
    private const float BurnShardGravity = 900f;
    private const int BurnShards = 20;

    private static ulong _burnCardId;
    private static ulong _burnAtMs;

    /// <summary>
    /// 把「打出去的这张牌被烧穿」演一遍。<paramref name="dir"/> 是子弹飞的方向，
    /// 碎片会顺着它多飞一点（物理解释：动能给了纸片）。
    /// </summary>
    internal static void BurnPlayedCard(object? cardSource, Vector2 dir)
    {
        try
        {
            if (cardSource == null || !Enabled || TestModeIsOn())
            {
                return;
            }

            // 同一张牌只烧一次（狙击连发 8 段伤害会调 8 次）。
            ulong now = Time.GetTicksMsec();
            Control? card = FindCardNode(cardSource);
            if (card == null || !GodotObject.IsInstanceValid(card))
            {
                LogOnce("[牌面·烧穿] 找不到场上的牌节点（FindOnTable 返回 null）");
                return;
            }

            ulong id = card.GetInstanceId();
            if (id == _burnCardId && now - _burnAtMs < 1200UL)
            {
                return;
            }

            _burnCardId = id;
            _burnAtMs = now;

            Vector2 size = new(300f, 422f);
            Vector2 origin = new(-150f, -211f);
            try
            {
                // 与壳层同一套几何：卡片局部原点是**卡面中心**，%Frame 才是真实轮廓。
                if (card.GetNodeOrNull("%Frame") is Control frame
                    && GodotObject.IsInstanceValid(frame)
                    && frame.Size.X > 8f && frame.Size.Y > 8f)
                {
                    size = frame.Size;
                    origin = card.GetGlobalTransform().AffineInverse() * frame.GlobalPosition;
                }
            }
            catch
            {
                // 量不到就用默认几何
            }

            var overlay = new CardBurnOverlay
            {
                Name = "RayModCardBurn",
                Card = card,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            overlay.Position = origin;
            overlay.Size = size;
            overlay.PivotOffset = size * 0.5f;
            card.AddChild(overlay);
            overlay.SetProcess(true);

            Vector2 centerGlobal = card.GetGlobalTransform() * (origin + size * 0.5f);
            SpawnBurnShards(centerGlobal, size, dir);

            Diag("burn-on", $"[牌面·烧穿] 挂上：卡面 {size.X:F0}×{size.Y:F0}");
        }
        catch (Exception e)
        {
            LogOnce($"[牌面·烧穿] {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>`NCard.FindOnTable(CardModel, PileType?)` 的软调用（拿不到就当没牌）。</summary>
    internal static Control? FindCardNode(object cardSource)
    {
        try
        {
            Type? nc = AccessTools_TypeByName("MegaCrit.Sts2.Core.Nodes.Cards.NCard");
            MethodInfo? find = nc?
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "FindOnTable" && m.GetParameters().Length == 2);
            if (find == null)
            {
                LogOnce("[牌面·烧穿] 软解析失败：NCard.FindOnTable(CardModel, PileType?)");
                return null;
            }

            return find.Invoke(null, new object?[] { cardSource, null }) as Control;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把牌炸成碎片：纸白为主、卡框金与焦黑各掺一点。</summary>
    private static void SpawnBurnShards(Vector2 centerGlobal, Vector2 cardSize, Vector2 dir)
    {
        if (VfxParentForEffects() is not Control layer)
        {
            return;
        }

        Vector2 bias = dir.LengthSquared() > 0.01f ? dir.Normalized() : Vector2.Right;

        for (int i = 0; i < BurnShards; i++)
        {
            // 每片碎纸：一个不规则四边形（手工抖动，不用随机圆）
            // 尺寸要够大 —— 20 片 14×18 的小纸屑在 2560 宽画面里就是一把白点，
            // 看不出"这张牌碎了"。
            float w = (float)GD.RandRange(34.0, 70.0);
            float h = (float)GD.RandRange(38.0, 92.0);
            float jx = (float)GD.RandRange(-0.35, 0.35) * w;
            float jy = (float)GD.RandRange(-0.35, 0.35) * h;
            Vector2[] pts =
            {
                new(-w * 0.5f, -h * 0.5f),
                new(w * 0.5f + jx, -h * 0.5f + jy),
                new(w * 0.5f, h * 0.5f),
                new(-w * 0.5f + jx * 0.5f, h * 0.5f + jy * 0.5f),
            };

            double pick = GD.Randf();
            Color color = pick < 0.60 ? BurnPaper : (pick < 0.85 ? BurnFrame : BurnChar);
            // 泛光色：纸片发暖白、金片发更亮的金、焦黑片发暗红余烬
            Color glowTint = pick < 0.60 ? BurnHot : (pick < 0.85 ? BurnFrame : new Color(1f, 0.55f, 0.18f));

            var root = FieldFx.Root(layer, centerGlobal, "RayModBurnShard");
            // 出发点散落在卡面范围内（不是从中心一个点喷出来的）
            root.Position += new Vector2(
                (float)GD.RandRange(-0.45, 0.45) * cardSize.X,
                (float)GD.RandRange(-0.45, 0.45) * cardSize.Y);

            // ★ 三层要一起转 ⇒ 旋转挂在中间节点上。
            //   直接转 Polygon2D 的话泛光和色散边会留在原地方，飞起来就成了三块分开的东西。
            var frame = new Node2D { Name = "RayModBurnShardFrame" };
            root.AddChild(frame);

            // ② 泛光：三层同形状的放大壳、加色，越外越淡。
            //   ★ 只叠一层会得到一个硬边的"彩色描边方块"（实测就是这么翻车的），
            //     三层递进 alpha 才像真的一圈光在往外散。
            Polygon2D bloomFar = FieldFx.Poly(frame, ScaledPts(pts, 1.85f),
                new Color(glowTint.R, glowTint.G, glowTint.B, 0.055f));
            Polygon2D bloomMid = FieldFx.Poly(frame, ScaledPts(pts, 1.42f),
                new Color(glowTint.R, glowTint.G, glowTint.B, 0.095f));
            Polygon2D bloom = FieldFx.Poly(frame, ScaledPts(pts, 1.14f),
                new Color(glowTint.R, glowTint.G, glowTint.B, 0.16f));

            // ① 主体：顶点渐变。**必须跨色相**（暖白 → 卡框金 / 焦褐），
            //   只做明暗差在暗背景上读不出来；跨色相一眼就能看出"这块纸在烧"。
            Polygon2D poly = FieldFx.Poly(frame, pts, color, additive: false);
            poly.Color = new Color(1f, 1f, 1f);
            Color hi = color.Lerp(BurnHot, 0.88f);
            Color lo = pick < 0.60 ? color.Lerp(BurnFrame, 0.72f) : color.Lerp(BurnChar, 0.72f);
            poly.VertexColors = new[]
            {
                hi,
                hi.Lerp(lo, 0.35f),
                lo,
                lo.Lerp(hi, 0.20f),
            };

            // ③ 色散边缘：红/青两条轮廓反向错位。
            //   ★ 3px 宽 2.2 alpha 0.70 实测太抢 —— 读成"贴了一圈霓虹描边"，
            //     而不是镜头色散。退回 2.0px / alpha 0.42，让它只出现在边缘上。
            Line2D fringeR = FieldFx.Line(frame, pts, new Color(1f, 0.26f, 0.20f, 0.42f), 1.5f, closed: true);
            Line2D fringeC = FieldFx.Line(frame, pts, new Color(0.26f, 0.90f, 1f, 0.42f), 1.5f, closed: true);
            fringeR.Position = new Vector2(2.0f, 0.7f);
            fringeC.Position = new Vector2(-2.0f, -0.7f);

            // 速度：以卡片中心为原点向外，再额外加一点子弹方向的动量
            Vector2 outw = (root.Position - centerGlobal);
            outw = outw.LengthSquared() > 1f ? outw.Normalized() : Vector2.Up;
            Vector2 v0 = outw * (float)GD.RandRange(170.0, 420.0) + bias * (float)GD.RandRange(40.0, 190.0);
            v0.Y -= (float)GD.RandRange(60.0, 260.0);   // 起手先往上抛

            float spin = (float)GD.RandRange(-720.0, 720.0);
            float rot0 = (float)GD.RandRange(0.0, 6.28);
            Vector2 p0 = root.Position;

            FieldFx.Animate(root, BurnShardFly, k =>
            {
                float t = (float)k * BurnShardFly;
                root.Position = p0 + v0 * t + new Vector2(0f, 0.5f * BurnShardGravity * t * t);
                frame.Rotation = rot0 + Mathf.DegToRad(spin) * t;
                float fade = k < 0.65f ? 1f : Mathf.Clamp(1f - (k - 0.65f) / 0.35f, 0f, 1f);
                frame.Modulate = new Color(1f, 1f, 1f, fade);
                // 泛光比本体衰减得更快：飞远了还在发光，一把纸屑就变成一堆灯泡
                bloom.Modulate = new Color(1f, 1f, 1f, Mathf.Lerp(0.90f, 0.12f, k));
                bloomMid.Modulate = new Color(1f, 1f, 1f, Mathf.Lerp(0.90f, 0.06f, k));
                bloomFar.Modulate = new Color(1f, 1f, 1f, Mathf.Lerp(0.95f, 0f, k));
            });

            FieldFx.FreeAfter(root, BurnShardFly + 0.05f);
        }
    }

    /// <summary>把顶点绕原点缩放（给泛光层做同形状的放大壳）。</summary>
    private static Vector2[] ScaledPts(Vector2[] src, float k)
    {
        var dst = new Vector2[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            dst[i] = src[i] * k;
        }

        return dst;
    }

    /// <summary>
    /// 叠在卡片上的「烧穿」画面。它挂在卡片节点下 ⇒ 卡片飞、转、缩，它全跟着走，
    /// 不用自己算轨迹（和 <c>CardShellSleeve</c> 一个套路）。
    /// </summary>
    private sealed partial class CardBurnOverlay : Control
    {
        internal Control? Card;
        private float _t;

        public override void _Process(double delta)
        {
            _t += Mathf.Min((float)delta, 0.033f);
            QueueRedraw();
            if (_t >= BurnLife)
            {
                QueueFree();
            }
        }

        public override void _Draw()
        {
            Vector2 size = Size;
            if (size.X < 8f || size.Y < 8f)
            {
                return;
            }

            Vector2 c = size * 0.5f;
            float kHole = Mathf.Clamp(_t / BurnHoleTime, 0f, 1f);
            float kCrack = Mathf.Clamp(_t / BurnCrackTime, 0f, 1f);
            float kScorch = Mathf.Clamp(_t / BurnScorchTime, 0f, 1f);
            // 最后 0.18 秒整体淡出（牌这时已经在往弃牌堆飞了）
            float fade = Mathf.Clamp((BurnLife - _t) / 0.18f, 0f, 1f);
            if (fade <= 0.001f)
            {
                return;
            }

            // ★ 洞**不能太大**：第一版取 0.26×min，整张牌被一个黑饼盖住，
            //   卡图（玩家真正在看的东西）全没了 —— 和早先"整卡平涂金光"同一个错误。
            //   0.155 大约是一个硬币大小：读作"被打穿了一个洞"，而不是"牌没了"。
            float rHole = Mathf.Min(size.X, size.Y) * 0.155f * FieldFxEase.OutCubic(kHole);

            // ③ 焦痕：从洞往外糊开的暗色。**必须是渐变**（三圈递减），
            //   单一实心圆会得到一个硬边的黑饼，看起来像贴图没加载。
            if (kScorch > 0.01f)
            {
                float reach = Mathf.Lerp(rHole, Mathf.Max(size.X, size.Y) * 0.58f, FieldFxEase.OutCubic(kScorch));
                for (int layer_i = 0; layer_i < 3; layer_i++)
                {
                    float r = reach * (1f - layer_i * 0.26f);
                    if (r < 2f)
                    {
                        continue;
                    }

                    Vector2[] ring = FieldFx.Circle(r, 44);
                    // 每一圈再按角度抖一点，边界才不像量角器画的
                    for (int i = 0; i < ring.Length; i++)
                    {
                        float jag = 1f + 0.10f * Mathf.Sin(i * 2.3f + layer_i * 1.7f);
                        ring[i] = ring[i] * jag + c;
                    }

                    DrawColoredPolygon(ring, new Color(BurnChar, (0.30f - layer_i * 0.09f) * kScorch * fade));
                }
            }

            // ② 裂纹：从洞心射出去的折线（内层一条白热细线压在黑缝上）
            if (kCrack > 0.01f)
            {
                int cracks = 11;
                for (int i = 0; i < cracks; i++)
                {
                    float ang = Mathf.Tau * i / cracks + (i % 3) * 0.17f;
                    Vector2 p0 = c + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * rHole;
                    float reach = Mathf.Lerp(rHole, Mathf.Max(size.X, size.Y) * (0.35f + 0.22f * ((i * 37) % 10) / 10f), kCrack);
                    Vector2 prev = p0;
                    for (int s = 1; s <= 3; s++)
                    {
                        float f = s / 3f;
                        float wob = (float)Math.Sin(i * 2.7 + s * 1.9) * 10f * f;
                        Vector2 dirv = new(Mathf.Cos(ang), Mathf.Sin(ang));
                        Vector2 nrm = new(-dirv.Y, dirv.X);
                        Vector2 p = c + dirv * (rHole + (reach - rHole) * f) + nrm * wob;
                        DrawLine(prev, p, new Color(BurnChar, 0.80f * fade), 3.4f, true);
                        DrawLine(prev, p, new Color(BurnHot, 0.55f * (1f - f) * fade), 1.7f, true);
                        prev = p;
                    }
                }
            }

            // ① 洞：**锯齿边界**（不是正圆）+ 白热边缘 + 边缘余烬
            if (rHole > 1.5f)
            {
                // 洞外的焦黑晕（一圈，比洞大 35%）
                Vector2[] halo = FieldFx.Circle(rHole * 1.6f, 36);
                for (int i = 0; i < halo.Length; i++)
                {
                    halo[i] = halo[i] * (1f + 0.16f * Mathf.Sin(i * 1.9f)) + c;
                }

                DrawColoredPolygon(halo, new Color(BurnChar, 0.55f * fade));

                // 锯齿状的洞（烧穿的纸边没有一处是直的）
                Vector2[] core = FieldFx.Circle(rHole, 32);
                for (int i = 0; i < core.Length; i++)
                {
                    core[i] = core[i] * (1f + 0.20f * Mathf.Sin(i * 3.1f + 0.7f)) + c;
                }

                DrawColoredPolygon(core, new Color(0.055f, 0.043f, 0.031f, 0.96f * fade));

                // 洞边缘：两层热边（外层暖金、内层白热）+ 一圈余烬点
                Vector2[] rim = FieldFx.Circle(rHole * 1.06f, 40);
                for (int i = 0; i < rim.Length; i++)
                {
                    rim[i] = rim[i] * (1f + 0.14f * Mathf.Sin(i * 2.6f + 1.1f)) + c;
                }

                DrawPolyline(rim, new Color(BurnFrame, 0.85f * fade), 4.2f, true);
                DrawPolyline(rim, new Color(BurnHot, 0.75f * fade), 1.8f, true);

                int embers = 12;
                for (int i = 0; i < embers; i++)
                {
                    float ang = Mathf.Tau * i / embers + 0.23f;
                    float flick = 0.55f + 0.45f * Mathf.Sin(_t * 26f + i * 2.1f);
                    float rr = rHole * (1.02f + 0.10f * Mathf.Sin(i * 4.4f));
                    Vector2 p = c + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * rr;
                    DrawCircle(p, 1.6f + 1.4f * flick, new Color(BurnHot, 0.55f * flick * fade));
                }
            }
        }
    }
}
