using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Logging;

using Logger = MegaCrit.Sts2.Core.Logging.Logger;
using LogType = MegaCrit.Sts2.Core.Logging.LogType;

namespace RayMod.Art;

/// <summary>
/// ★ <b>「灵异录像带」的墓碑</b>（2026-10-20，用户口径：
/// 「打出灵异录像带的时候，被攻击的敌人头上出现「R.I.P」之后又墓碑飞向这个对象旁边立住。直到回合结束」）。
///
/// <para><b>演出分两拍</b>：</para>
/// <list type="number">
///   <item><b>R.I.P 浮出来</b>：在被攻击者头顶正上方，用<b>矢量笔画</b>拼出 R.I.P
///         （不做贴图、不依赖字体 —— 游戏里取字体要走主题，反而容易崩）；
///         边浮边轻微横向抖动（录像带跑带），1.3 秒后淡出。</item>
///   <item><b>0.34 秒后墓碑砸下来</b>：圆顶石板从斜上方加速下坠 + 旋转，0.52 秒后落在它脚边，
///         落地瞬间起尘 + 崩碎屑 + 石块回弹一下；然后<b>立在那儿不动，直到本回合结束</b>。</item>
/// </list>
///
/// <para><b>为什么是"落到脚边"而不是"盖在身上"</b>：盖在身上会遮挡敌人本体和血条，
/// 而这张牌的核心信息是"我打了它、它还剩多少血"。所以落在<b>侧面半步</b>的位置，
/// 一半压在地面上 —— 读起来是"给它先立了个碑"。</para>
///
/// <para><b>生命周期</b>：<see cref="TapeGrave"/> 每 0.5 秒自查一次「回合号 / 房间实例」是否变过
/// （和手牌那套同一个判据，因为读档不走任何钩子）；特效开关关掉也立刻收场。
/// 回合结束时 <see cref="ClearTapeGraves"/> 会被 <see cref="ClearTapeHand"/> 顺带调到。</para>
///
/// <para>⚠️ 全套只用已验证可画的图元（<c>FieldFx.Line</c> / <c>FieldFx.Poly</c> / <c>FieldFx.Circle</c>）：
/// <c>DrawPolygon</c> 在这条链路上画不出来（见 <c>CardFieldVfx.Streak.cs</c>）。</para>
/// </summary>
internal partial class CardFieldVfx
{
    private static readonly Logger GraveLog = new("RayMod.TapeGrave", LogType.Generic);

    private static readonly List<TapeGrave> _tapeGraves = new();

    // ───────────────────────── 调参 ─────────────────────────

    /// <summary>墓碑石板的宽（世界像素）。</summary>
    private const float GraveStoneW = 118f;

    /// <summary>墓碑石板的高（贴图等比缩放到这个高度，宽度由贴图比例决定）。</summary>
    private const float GraveStoneH = 178f;

    /// <summary>飞进来的时长（短一点才砸得"狠"）。</summary>
    private const float GraveFlyTime = 0.14f;

    /// <summary>
    /// 下坠过程中留几块残影。<b>这是"快"的主要读法</b> ——
    /// 原来在碑身上挂三条竖着的速度线，用户判"太廉价"（那三条线和碑没有关系，
    /// 读起来像贴在石头上的白虚线）。残影是从碑本身复制出来的，形状完全一致。
    /// </summary>
    private const int GraveGhostTotal = 4;

    /// <summary>R.I.P 先单独亮一下，碑再起飞（"字出来 → 碑飞来"两拍）。</summary>
    private const float GraveFlyDelay = 0.24f;

    /// <summary>
    /// 「出牌 → 伤害落地」的实测间隔（秒）。用户要求"造成伤害同时墓碑落地"，
    /// 所以碑不能等伤害钩子才起飞，而是在 <see cref="TapeGraveOnPlay"/> 里预排：
    /// 起飞延迟 = <c>TapeGraveLead - GraveFlyTime</c>，落地正好压在伤害数字那一帧。
    /// 改这张牌的前置效果（格挡动画等）之后这个值要跟着量一次。
    /// </summary>
    private const float TapeGraveLead = 0.16f;

    /// <summary>本次出牌是否已经预排过一块碑（命中钩子里别再补一块）。</summary>
    private static bool _graveArmed;

    /// <summary>落地回弹的时长。</summary>
    private const float GraveSettleTime = 0.26f;

    /// <summary>R.I.P 的总时长（含淡出）。</summary>
    private const float GraveRipLife = 1.30f;

    /// <summary>收场淡出时长。</summary>
    private const float GraveFadeOut = 0.35f;

    /// <summary>R.I.P 的字高（世界像素）。</summary>
    private const float RipScale = 86f;

    /// <summary>R.I.P 抬到头顶的高度系数（乘敌人高度；另有上限，免得大虫子把小屏顶穿）。</summary>
    private const float RipHeadK = 0.46f;

    /// <summary>墓碑落地后离身体中心的侧向距离系数（乘敌人宽度）。</summary>
    private const float GraveSideK = 0.55f;

    // ───────────────────────── 色板 ─────────────────────────

    /// <summary>R.I.P 本体：暖白（录像带里那种过曝的字）。</summary>
    private static readonly Color RipHot = new(1.000f, 0.965f, 0.882f);

    /// <summary>R.I.P 描边：暗褐（让字在亮背景上也立得住）。</summary>
    private static readonly Color RipInk = new(0.118f, 0.090f, 0.071f);

    /// <summary>墓碑石面。</summary>
    private static readonly Color GraveStone = new(0.408f, 0.427f, 0.451f);

    /// <summary>墓碑暗部（侧面 / 阴影）。</summary>
    private static readonly Color GraveStoneDark = new(0.196f, 0.208f, 0.231f);

    /// <summary>刻字 / 刻痕。</summary>
    private static readonly Color GraveEtch = new(0.129f, 0.137f, 0.153f);

    /// <summary>底下的土。</summary>
    private static readonly Color GraveSoil = new(0.227f, 0.184f, 0.145f);

    /// <summary>草。</summary>
    private static readonly Color GraveGrass = new(0.216f, 0.286f, 0.180f);

    // ───────────────────────── 墓碑贴图 ─────────────────────────

    /// <summary>
    /// 墓碑素材：本机 STS2 卡图 LoRA（Anima base + SlayTheSpire2_CardArt）生成的
    /// 白底墓碑，再用 <c>_tools\cut_grave.py</c> 抠成 alpha（383×512，底边带一小片暗影）。
    /// </summary>
    /// <remarks>
    /// 手绘矢量和程序化烘焙（PIL 画石头）都被判过"塑料石头"；
    /// LoRA 一次就给出了和游戏一致的粗描边、平涂色块与金色轮廓光。
    /// 换素材只要覆盖 <c>images/vfx/tombstone.png</c>，不用改代码。
    /// </remarks>
    private const string GraveStonePath = Entry.ResPath + "/images/vfx/tombstone.png";

    private static Texture2D? _graveStoneTex;

    private static Texture2D? GraveStoneTexture()
    {
        if (_graveStoneTex != null && GodotObject.IsInstanceValid(_graveStoneTex))
        {
            return _graveStoneTex;
        }

        _graveStoneTex = GD.Load<Texture2D>(GraveStonePath);

        if (_graveStoneTex == null)
        {
            GraveLog.Warn("[录像带·墓碑] 贴图没加载到：" + GraveStonePath);
        }

        return _graveStoneTex;
    }

    /// <summary>
    /// 出牌瞬间预排墓碑（由 <see cref="TapeCardOnPlay"/> 调用）。
    /// <para><b>为什么不能在伤害钩子里才起飞</b>：钩子触发时伤害数字已经在屏幕上，
    /// 碑再从天上掉下来就是"事后再演一遍"。用户明确要"造成伤害同时墓碑落地"，
    /// 所以在这里按 <see cref="TapeGraveLead"/> 倒排起飞时刻。</para>
    /// </summary>
    internal static void TapeGraveOnPlay(object? card, object? play)
    {
        if (card is not RayMod.Cards.ParanormalTape)
        {
            return;
        }

        _graveArmed = false;

        try
        {
            if (!Enabled || TestModeIsOn() || play == null)
            {
                return;
            }

            object? target = GetProperty(play, "Target");

            if (target == null)
            {
                return;
            }

            Vector2 chest = ChestPos(target);

            if (chest == Vector2.Zero)
            {
                return;
            }

            if (VfxParentForEffects() is not Control layer)
            {
                return;
            }

            Vector2 size = CreatureSizeOf(target);
            float lift = Mathf.Clamp(size.Y * 0.70f, 170f, 260f);
            Vector2 head = chest + new Vector2(0f, -lift);
            Vector2 viewport = layer.GetViewportRect().Size;

            if (viewport.Y > 0f && head.Y < viewport.Y * 0.07f)
            {
                head.Y = viewport.Y * 0.07f;
            }

            float side = GD.Randf() < 0.5f ? -1f : 1f;

            Vector2 land = chest + new Vector2(
                side * (size.X * GraveSideK + 46f),
                size.Y * 0.46f);

            SpawnRip(layer, head);
            SpawnGrave(layer, land, side, TapeGraveLead - GraveFlyTime);

            _graveArmed = true;

            Diag("tapegraveplay", $"[录像带·计时] 出牌预排碑 t={Time.GetTicksMsec()} lead={TapeGraveLead:F2}s");
        }
        catch (Exception e)
        {
            GraveLog.Warn($"[录像带·墓碑] 预排失败：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// 命中时调（由 <see cref="OnDamageGiven"/> 转进来）。返回 true = 这张牌的命中演出已认领，
    /// 调用方不要再叠通用命中层。
    /// </summary>
    internal static bool TapeGraveOnHit(object? cardSource, object? target)
    {
        if (cardSource is not RayMod.Cards.ParanormalTape)
        {
            return false;
        }

        try
        {
            // 出牌时已经预排过碑 ⇒ 这里什么都不做，否则会掉两块
            if (_graveArmed)
            {
                _graveArmed = false;

                Diag("tapegravehit", $"[录像带·计时] 伤害落地 t={Time.GetTicksMsec()}（碑已预排）");

                return true;
            }

            if (!Enabled || TestModeIsOn() || target == null)
            {
                return true;
            }

            Vector2 chest = ChestPos(target);

            if (chest == Vector2.Zero)
            {
                return true;
            }

            if (VfxParentForEffects() is not Control layer)
            {
                GraveLog.Warn("[录像带·墓碑] 拿不到战斗特效容器 ⇒ 跳过");
                return true;
            }

            Vector2 size = CreatureSizeOf(target);

            // 头顶：按敌人高度算，但有上下限 —— 大虫子（比如那条"活雾"）高几百像素，
            // 按比例会把字顶到屏幕外面去；抬得不够又会和伤害数字（也在头顶飘）撞在一起。
            float lift = Mathf.Clamp(size.Y * 0.70f, 170f, 260f);
            Vector2 head = chest + new Vector2(0f, -lift);

            // 别让字跑出画面顶部
            Vector2 viewport = layer.GetViewportRect().Size;

            if (viewport.Y > 0f && head.Y < viewport.Y * 0.07f)
            {
                head.Y = viewport.Y * 0.07f;
            }

            float side = GD.Randf() < 0.5f ? -1f : 1f;

            Vector2 land = chest + new Vector2(
                side * (size.X * GraveSideK + 46f),
                size.Y * 0.46f);

            SpawnRip(layer, head);
            SpawnGrave(layer, land, side);

            GraveLog.Info($"[录像带·墓碑] 立碑：head={head} land={land} 敌人尺寸={size}");
        }
        catch (Exception e)
        {
            GraveLog.Warn($"[录像带·墓碑] 失败：{e.GetType().Name}: {e.Message}");
        }

        return true;
    }

    /// <summary>回合结束 / 特效关掉时把所有碑收掉。</summary>
    internal static void ClearTapeGraves()
    {
        try
        {
            foreach (TapeGrave grave in _tapeGraves)
            {
                if (GodotObject.IsInstanceValid(grave))
                {
                    grave.Retire();
                }
            }
        }
        catch
        {
            // 表现层失败绝不外抛
        }

        _tapeGraves.Clear();
    }

    /// <summary>
    /// 取敌人的显示尺寸（用来定位 R.I.P 和墓碑）。
    /// <para><b>为什么不能只判 <c>Control</c></b>：实机日志证明 <c>GetCreatureNode</c> 拿回来的节点
    /// 在这个版本里<b>不是</b> <c>Control</c>（<c>is Control</c> 判定失败、量到的是默认值），
    /// 所以这里再补一路反射读 <c>Size</c> / <c>GlobalRect</c>；都没有就用中号敌人的经验值。</para>
    /// </summary>
    private static Vector2 CreatureSizeOf(object? target)
    {
        try
        {
            object? node = CreatureNodeOf(target);

            if (node == null)
            {
                return new Vector2(240f, 280f);
            }

            if (node is Control control && control.Size.X > 8f && control.Size.Y > 8f)
            {
                return control.Size;
            }

            System.Reflection.PropertyInfo? sizeProp = node.GetType()
                .GetProperty("Size", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            if (sizeProp?.GetValue(node) is Vector2 v && v.X > 8f && v.Y > 8f)
            {
                return v;
            }

            System.Reflection.PropertyInfo? rectProp = node.GetType()
                .GetProperty("GlobalRect", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            if (rectProp?.GetValue(node) is Rect2 r && r.Size.X > 8f && r.Size.Y > 8f)
            {
                return r.Size;
            }
        }
        catch (Exception e)
        {
            GraveLog.Warn($"[录像带·墓碑] 量敌人尺寸失败：{e.GetType().Name}: {e.Message}");
        }

        return new Vector2(240f, 280f);
    }

    // ══════════════════════ R.I.P ══════════════════════

    /// <summary>
    /// 头顶浮出 R.I.P。
    ///
    /// <para><b>为什么是实心块状字，而不是描边线字</b>：第一版用 3.4px 白线 + 7.6px 暗描边描出字母轮廓，
    /// 实机放大看读作"细线 HUD 文字" —— 就是用户反复否掉的那种廉价感。
    /// 现在每个字母由若干<b>实心矩形</b>（<see cref="FieldFx.Poly"/>）拼成碑刻/印章体，
    /// 再叠三层：暗色放大版当轮廓（压得住亮的背景）、红/青各一份错位当色散、暖白正本压在最上。</para>
    ///
    /// <para>逐字落位：五画各延迟 0.055 秒，从上方 20px 砸到位（<c>OutBack</c>）——
    /// 一排字整齐地同时淡入像 UI 弹窗，错开落位才像"盖上去的"。</para>
    /// </summary>
    private static void SpawnRip(Control layer, Vector2 at)
    {
        Node2D root = FieldFx.Root(layer, at, "RayModTapeRip");

        // 版面：五个字符的横向偏移（单位 = 1 个 RipScale）。
        // 总宽 2.66 个单位，整体水平居中到锚点。
        (char Ch, float X)[] layout =
        {
            ('R', 0.00f),
            ('.', 0.80f),
            ('I', 1.02f),
            ('.', 1.82f),
            ('P', 2.04f),
        };

        const float fullWidth = 2.66f;
        float originX = -fullWidth * 0.5f * RipScale;

        root.Scale = Vector2.One;
        root.Modulate = Colors.White;

        for (int i = 0; i < layout.Length; i++)
        {
            (char ch, float ux) = layout[i];

            List<Vector2[]> polys = GlyphPolys(ch);

            if (polys.Count == 0)
            {
                continue;
            }

            var glyph = new Node2D
            {
                Name = "G" + i,
                Position = new Vector2(originX + ux * RipScale, 0f),
            };

            root.AddChild(glyph);

            // ① 暗色轮廓：整字放大 1.18，不吃加色。
            //    绕"字身中心"放大：字身在单位坐标里占 x∈[0,0.62]、y∈[0,1]，
            //    中心 c=(0.31,-0.5)*RipScale；绕 c 放大 s 倍即 Position = c*(1-s)。
            const float outlineK = 1.18f;
            var dark = new Node2D
            {
                Name = "Dark",
                Scale = new Vector2(outlineK, outlineK),
                Position = new Vector2(0.31f, -0.5f) * RipScale * (1f - outlineK),
            };
            glyph.AddChild(dark);
            AddGlyphPolys(dark, polys, RipInk, additive: false);

            // ② 色散：红/青各错位一份（录像带跑色）
            var fringeR = new Node2D { Name = "FringeR", Position = new Vector2(-3.2f, 0.6f) };
            var fringeC = new Node2D { Name = "FringeC", Position = new Vector2(3.2f, -0.6f) };
            glyph.AddChild(fringeR);
            glyph.AddChild(fringeC);
            AddGlyphPolys(fringeR, polys, new Color(1.00f, 0.30f, 0.28f, 0.55f), additive: true);
            AddGlyphPolys(fringeC, polys, new Color(0.30f, 0.90f, 1.00f, 0.55f), additive: true);

            // ③ 正本
            var main = new Node2D { Name = "Main" };
            glyph.AddChild(main);
            AddGlyphPolys(main, polys, RipHot, additive: false);

            // 逐字落位
            Vector2 rest = glyph.Position;
            glyph.Position = rest + new Vector2(0f, -20f);
            glyph.Modulate = new Color(1f, 1f, 1f, 0f);

            FieldFx.After(root, 0.055 * i, "[录像带·R.I.P 落字]", () =>
            {
                if (!GodotObject.IsInstanceValid(glyph))
                {
                    return;
                }

                FieldFx.Animate(glyph, 0.30f, k =>
                {
                    if (!GodotObject.IsInstanceValid(glyph))
                    {
                        return;
                    }

                    float e = FieldFxEase.OutBack(k);
                    glyph.Position = rest + new Vector2(0f, -20f * (1f - e));
                    glyph.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(k * 3f, 0f, 1f));
                });
            });
        }

        FieldFx.Animate(root, GraveRipLife, k =>
        {
            if (!GodotObject.IsInstanceValid(root))
            {
                return;
            }

            // 整体：抖一下（信号不良）→ 停 → 淡出
            float a = k < 0.72f ? 1f : 1f - (k - 0.72f) / 0.28f;
            float flicker = Mathf.Sin(k * 47f) > 0.94f ? 0.30f : 1f;

            root.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(a * flicker, 0f, 1f));

            // 一边浮一边往上飘
            root.Position = at + new Vector2(Mathf.Sin(k * 24f) * 2.6f, -18f * FieldFxEase.OutCubic(k));
        });

        FieldFx.FreeAfter(root, GraveRipLife + 0.15f);
    }

    /// <summary>把一个字符的实心块塞进列表（单位坐标，字身 y ∈ [0,1]，宽约 0.62）。</summary>
    private static List<Vector2[]> GlyphPolys(char ch)
    {
        var polys = new List<Vector2[]>();

        // 笔画粗细
        const float t = 0.20f;

        void Rect(float x0, float y0, float x1, float y1)
            => polys.Add(new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) });

        switch (ch)
        {
            case 'R':
                Rect(0.00f, 0.00f, t, 1.00f);            // 竖干
                Rect(0.00f, 0.00f, 0.62f, t);            // 顶横
                Rect(0.62f - t, 0.00f, 0.62f, 0.60f);    // 右侧上段
                Rect(0.00f, 0.42f, 0.62f, 0.60f);        // 中横
                // 斜腿（平行四边形）
                polys.Add(new[]
                {
                    new Vector2(0.26f, 0.58f), new Vector2(0.46f, 0.58f),
                    new Vector2(0.66f, 1.00f), new Vector2(0.46f, 1.00f),
                });
                break;

            case 'P':
                Rect(0.00f, 0.00f, t, 1.00f);
                Rect(0.00f, 0.00f, 0.62f, t);
                Rect(0.62f - t, 0.00f, 0.62f, 0.60f);
                Rect(0.00f, 0.42f, 0.62f, 0.60f);
                break;

            case 'I':
                Rect(0.20f, 0.00f, 0.20f + t, 1.00f);    // 竖干
                Rect(0.00f, 0.00f, 0.60f, 0.17f);        // 上衬线
                Rect(0.00f, 0.83f, 0.60f, 1.00f);        // 下衬线
                break;

            case '.':
                Rect(0.00f, 0.82f, 0.18f, 1.00f);
                break;
        }

        return polys;
    }

    /// <summary>把一组单位多边形按 <see cref="RipScale"/> 铺到一个节点下。</summary>
    private static void AddGlyphPolys(Node2D parent, List<Vector2[]> polys, Color color, bool additive)
    {
        foreach (Vector2[] unit in polys)
        {
            var pts = new Vector2[unit.Length];

            for (int i = 0; i < unit.Length; i++)
            {
                // y 往上为负：字身整体抬到锚点上方
                pts[i] = new Vector2(unit[i].X * RipScale, (unit[i].Y - 1f) * RipScale);
            }

            FieldFx.Poly(parent, pts, color, additive);
        }
    }

    // ══════════════════════ 墓碑 ══════════════════════

    private static void SpawnGrave(Control layer, Vector2 land, float side, float startDelay = GraveFlyDelay)
    {
        // 清掉已经失效的登记
        _tapeGraves.RemoveAll(g => !GodotObject.IsInstanceValid(g));

        Node2D root = FieldFx.Root(layer, land, "RayModTapeGrave");

        var grave = new TapeGrave
        {
            Name = "Grave",
            ZIndex = 2,
        };

        root.AddChild(grave);
        grave.Arm(side, startDelay);
        _tapeGraves.Add(grave);
    }

    /// <summary>一块会飞进来、落地后一直立着的墓碑。</summary>
    private sealed partial class TapeGrave : Node2D
    {
        private Node2D _body = null!;

        /// <summary>碑的贴图精灵（残影要复制它）。贴图缺失的兜底路径下是 null。</summary>
        private Sprite2D? _stone;

        private int _ghostNext;

        private float _poll;

        private int _turn;

        private ulong _room;

        private float _fade = 1f;

        private bool _retiring;

        private bool _landed;

        internal void Arm(float side, float startDelay = GraveFlyDelay)
        {
            _turn = CurrentTurnNumber();
            _room = CurrentRoomId();

            Build();

            SetProcess(true);

            // 两拍：R.I.P 先单独亮一下，碑再起飞 —— 同时发生会读成"一堆东西一起冒出来"。
            // 出牌时预排的情况下 startDelay 已经是「出牌→伤害」减去下坠时长，
            // 于是碑落地那一帧正好压在伤害数字弹出的那一帧上。
            Modulate = new Color(1f, 1f, 1f, 0f);

            FieldFx.After(this, Mathf.Max(0.02f, startDelay), "[录像带·墓碑起飞]", () => BeginFly(side));
        }

        internal void Retire()
        {
            _retiring = true;
        }

        private void BeginFly(float side)
        {
            if (!GodotObject.IsInstanceValid(_body) || _retiring)
            {
                return;
            }

            Modulate = new Color(1f, 1f, 1f, 1f);

            // 从正上方加速砸下来（起点高 + 时间压短 = 明显更快）
            var from = new Vector2(side * 190f, -760f);
            float startRot = side * 0.55f;

            _body.Position = from;
            _body.Rotation = startRot;

            // ★ 这里原来是三条挂在碑身上的竖"速度线"，用户判"太廉价" —— 那三条线和
            //   石头没有关系，读起来像贴在碑上的白虚线。现在改成两件事：
            //   ① 过程中留 4 块<b>碑自己的残影</b>（形状/配色完全一致的淡影）；
            //   ② 末段沿下落方向<b>竖直拉长 22%</b>（运动模糊的经典做法）。
            _ghostNext = 0;

            FieldFx.Animate(_body, GraveFlyTime, k =>
            {
                if (!GodotObject.IsInstanceValid(_body))
                {
                    return;
                }

                // 加速砸下来：指数越大力道越"重"（1.9 太软读作飘；3.6 = 前段几乎不动、末段砸穿）
                float e = Mathf.Pow(Mathf.Clamp(k, 0f, 1f), 3.6f);
                _body.Position = from * (1f - e);
                _body.Rotation = startRot * (1f - e);

                // 拉长：只在后半段生效，否则整段都是一块被压扁的碑
                float smear = Mathf.Clamp((k - 0.50f) / 0.50f, 0f, 1f);
                _body.Scale = new Vector2(1f - (smear * 0.12f), 1f + (smear * 0.26f));

                // ★ 残影必须按「已经落到哪」而不是按时间 k 来撒：下落用 pow(k,3.6) 加速，
                //   k=0.2/0.4/0.6 时碑还在屏幕上方（y≈-639/-574）——那些残影一个都看不见，
                //   玩家只会看到落点旁边凭空多出一块石头。改按 e（缓动后的位置）撒。
                if (_ghostNext < GraveGhostTotal && e >= 0.55f + (_ghostNext * 0.11f))
                {
                    _ghostNext++;
                    SpawnStoneGhost(0.32f - ((_ghostNext - 1) * 0.05f));
                }
            });

            FieldFx.After(this, GraveFlyTime, "[录像带·墓碑落地]", Land);
        }

        /// <summary>
        /// 在碑<b>当前</b>位置留一块淡影。挂在 <c>this</c>（落点）而不是 <c>_body</c> 上，
        /// 这样它不会跟着碑继续往下掉 —— 残影的意义就是"留在原地"。
        /// </summary>
        private void SpawnStoneGhost(float alpha)
        {
            if (_stone == null || !GodotObject.IsInstanceValid(_stone))
            {
                return;
            }

            var ghost = new Sprite2D
            {
                Name = "StoneGhost",
                Texture = _stone.Texture,
                Centered = _stone.Centered,
                Scale = _stone.Scale,
                Material = FieldFx.Additive,
            };

            AddChild(ghost);
            ghost.GlobalPosition = _stone.GlobalPosition;
            ghost.GlobalRotation = _stone.GlobalRotation;
            ghost.GlobalScale = _stone.GlobalScale;
            ghost.Modulate = new Color(0.86f, 0.92f, 1f, alpha);

            Sprite2D g = ghost;

            FieldFx.Animate(g, 0.22f, k =>
            {
                if (!GodotObject.IsInstanceValid(g))
                {
                    return;
                }

                g.Modulate = new Color(0.86f, 0.92f, 1f, alpha * (1f - FieldFxEase.InCubic(k)));
            });

            FieldFx.FreeAfter(g, 0.26f);
        }

                private void Build()
        {
            _body = new Node2D { Name = "Stone" };
            AddChild(_body);

            Texture2D? tex = GraveStoneTexture();

            if (tex != null)
            {
                // 素材底边自带一小片暗影，所以把**底边**坐在 y = 0（地面点），水平居中。
                float k = GraveStoneH / Mathf.Max(1f, tex.GetHeight());

                var stone = new Sprite2D
                {
                    Name = "TombstoneSprite",
                    Texture = tex,
                    Centered = true,
                    Scale = Vector2.One * k,
                };

                stone.Position = new Vector2(0f, -GraveStoneH * 0.5f);
                _body.AddChild(stone);
                _stone = stone;

                // 碑脚一层薄土：把贴图的底边和地面"粘"起来，免得看着像贴纸浮着。
                FieldFx.Poly(_body, FieldFx.Circle(GraveStoneW * 0.46f, 24, 0.20f),
                    new Color(GraveSoil, 0.40f), additive: false);

                return;
            }

            // ── 贴图缺失时的兜底：一块素色石板（保证演出不塌成空） ──
            float w = GraveStoneW;
            float h = GraveStoneH;
            float r = w * 0.5f;
            float shoulder = -h + r;

            var slab = new List<Vector2>
            {
                new(-w * 0.5f, 0f),
                new(-w * 0.5f, shoulder),
            };

            for (int i = 0; i <= 14; i++)
            {
                float a = Mathf.Pi * (1f - i / 14f);
                slab.Add(new Vector2(Mathf.Cos(a) * r, shoulder - Mathf.Sin(a) * r));
            }

            slab.Add(new Vector2(w * 0.5f, 0f));

            FieldFx.Poly(_body, slab.ToArray(), GraveStone, additive: false);
            FieldFx.Line(_body, slab.ToArray(), GraveStoneDark, 2.6f, closed: true, additive: false);
        }

        private void Land()
        {
            if (!GodotObject.IsInstanceValid(_body) || _landed)
            {
                return;
            }

            _landed = true;

            // 下坠末段为了运动模糊把碑拉长过，落地这一帧必须先扶正，
            // 否则回弹动画会从"拉长的碑"开始，第一帧会看到它突然缩一下。
            _body.Scale = Vector2.One;

            // ① 这一拍要"硬"：顿帧 + 抖屏。游戏自己的重型攻击就是这么调的
            //    （CeremonialBeast.cs:199 / MechaKnight.cs:117）。
            DoHitStop();

            // ② 地面冲击环：压扁的椭圆从碑脚炸开（横向，专治"落地像贴纸"）
            SpawnLandRing();

            // ②-b 地面被压下去：一整块扁椭圆暗痕 + 顶沿亮边，快速摊平再淡出
            SpawnGroundPress();

            // ③ 土从两侧横向崩出去：不吃碑脚，只擦着地面飞；再补一股往上的扬尘
            for (int i = 0; i < 2; i++)
            {
                float dir = i == 0 ? -1f : 1f;

                SmokePuff(_body, new Color(0.235f, 0.196f, 0.161f), 16f, 0.28f,
                    new Vector2(dir * 108f, -14f), 3);
            }

            SmokePuff(_body, new Color(0.290f, 0.243f, 0.196f), 19f, 0.26f,
                new Vector2(0f, -74f), 3);

            DebrisShards(_body, GraveSoil, 14, 90f, 280f, 3.0f, 0.50f);

            // ④ 砸进去：先压扁再回弹（幅度比原来大一档，落地那一下才"顶得住"）
            FieldFx.Animate(_body, GraveSettleTime, k =>
            {
                if (!GodotObject.IsInstanceValid(_body))
                {
                    return;
                }

                float q = Mathf.Sin(k * Mathf.Pi) * (1f - k);
                _body.Scale = new Vector2(1f + 0.11f * q, 1f - 0.13f * q);
                _body.Rotation = Mathf.Sin(k * Mathf.Pi * 2.2f) * 0.055f * (1f - k);
            });

            // ⑤ 砸出来的坑和裂纹：留到回合结束，读作"碑是砸进地里的"
            SpawnLandPit();

            GraveLog.Info("[录像带·墓碑] 落地：留到本回合结束");
        }

        /// <summary>
        /// 地面被碑砸得"往下陷一下"：一块扁椭圆暗痕当场摊平、顶沿带一条亮边。
        /// <para>只盖一层暗椭圆会读成"地上有块泥"；必须配 ① 速起速收的摊平动作
        /// ② 顶沿那条被压出来的亮边，才读成"地面被压下去"。</para>
        /// </summary>
        private void SpawnGroundPress()
        {
            try
            {
                var press = new Node2D { Name = "GroundPress", ZIndex = -1 };
                AddChild(press);

                FieldFx.Poly(press, FieldFx.Circle(138f, 44, 0.20f),
                    new Color(0.157f, 0.129f, 0.102f, 0.66f), additive: false);

                Line2D rim = FieldFx.Line(press, FieldFx.Circle(138f, 44, 0.20f),
                    new Color(0.945f, 0.894f, 0.745f, 0.70f), 3.2f, closed: true);

                FieldFx.Animate(press, 0.26f, k =>
                {
                    if (!GodotObject.IsInstanceValid(press))
                    {
                        return;
                    }

                    float e = FieldFxEase.OutCubic(k);
                    float sx = Mathf.Lerp(0.52f, 1.0f, e);
                    float sy = Mathf.Lerp(1.40f, 0.66f, e);

                    press.Scale = new Vector2(sx, sy);
                    press.Modulate = new Color(1f, 1f, 1f, 1f - k * k);

                    if (GodotObject.IsInstanceValid(rim))
                    {
                        rim.Modulate = new Color(1f, 1f, 1f, 1f - k);
                    }
                });

                FieldFx.FreeAfter(press, 0.28f);
            }
            catch
            {
                // 表现层失败绝不外抛
            }
        }

        /// <summary>落地那一拍的横向冲击环（压扁椭圆，向外炸开）。</summary>
        private void SpawnLandRing()        {
            try
            {
                Line2D ring = FieldFx.Line(this, FieldFx.Circle(54f, 44, 0.26f),
                    new Color(1.000f, 0.860f, 0.520f, 0.95f), 7.5f, closed: true);

                FieldFx.Animate(ring, 0.40f, k =>
                {
                    if (!GodotObject.IsInstanceValid(ring))
                    {
                        return;
                    }

                    float e = FieldFxEase.OutCubic(k);
                    float s = Mathf.Lerp(0.40f, 3.10f, e);

                    ring.Scale = new Vector2(s, s);
                    ring.Modulate = new Color(1f, 1f, 1f, (1f - k) * (1f - k));
                });

                FieldFx.FreeAfter(ring, 0.42f);

                // 内圈一道更粗更白的光环，补"吨位"
                Line2D halo = FieldFx.Line(this, FieldFx.Circle(30f, 32, 0.26f),
                    new Color(1f, 1f, 1f, 0.65f), 13.0f, closed: true);

                FieldFx.Animate(halo, 0.24f, k =>
                {
                    if (!GodotObject.IsInstanceValid(halo))
                    {
                        return;
                    }

                    float s = Mathf.Lerp(0.5f, 2.00f, FieldFxEase.OutCubic(k));

                    halo.Scale = new Vector2(s, s);
                    halo.Modulate = new Color(1f, 1f, 1f, (1f - k) * 0.8f);
                });

                FieldFx.FreeAfter(halo, 0.26f);
            }
            catch (Exception e)
            {
                GraveLog.Warn("[录像带·墓碑] 冲击环失败：" + e.GetType().Name);
            }
        }

        /// <summary>碑脚砸出来的坑 + 三道裂纹（一直留到本回合结束）。</summary>
        private void SpawnLandPit()
        {
            try
            {
                Polygon2D pit = FieldFx.Poly(this, FieldFx.Circle(GraveStoneW * 0.78f, 30, 0.22f),
                    new Color(0.055f, 0.047f, 0.039f, 0.60f), additive: false);

                pit.ZIndex = -1;

                for (int i = 0; i < 3; i++)
                {
                    float a = Mathf.Lerp(-0.35f, 0.35f, i / 2f) + (GD.Randf() - 0.5f) * 0.25f;
                    float len = Mathf.Lerp(GraveStoneW * 0.9f, GraveStoneW * 1.5f, GD.Randf());
                    var p0 = new Vector2(Mathf.Cos(a) * GraveStoneW * 0.30f, 2f);
                    var p1 = p0 + new Vector2(Mathf.Cos(a) * len * 0.55f, Mathf.Sin(a) * len * 0.16f);
                    var p2 = p0 + new Vector2(Mathf.Cos(a) * len, Mathf.Sin(a) * len * 0.26f);

                    Line2D crack = FieldFx.Line(this, new[] { p0, p1, p2 },
                        new Color(0.086f, 0.075f, 0.063f, 0.55f), 2.4f, closed: false, additive: false);

                    crack.ZIndex = -1;
                }
            }
            catch (Exception e)
            {
                GraveLog.Warn("[录像带·墓碑] 落地坑失败：" + e.GetType().Name);
            }
        }

        public override void _Process(double delta)
        {
            try
            {
                float dt = Mathf.Min((float)delta, 0.033f);

                _poll += dt;

                if (_poll >= 0.5f && !_retiring)
                {
                    _poll = 0f;

                    if (!Enabled)
                    {
                        _retiring = true;
                    }
                    else
                    {
                        int turn = CurrentTurnNumber();
                        ulong room = CurrentRoomId();

                        if ((_turn > 0 && turn > 0 && turn != _turn) || (_room != 0UL && room != _room))
                        {
                            _retiring = true;
                        }
                    }
                }

                if (!_retiring)
                {
                    return;
                }

                _fade -= dt / GraveFadeOut;

                if (_fade <= 0f)
                {
                    QueueFree();
                    return;
                }

                Modulate = new Color(1f, 1f, 1f, _fade);
            }
            catch
            {
                QueueFree();
            }
        }
    }
}
