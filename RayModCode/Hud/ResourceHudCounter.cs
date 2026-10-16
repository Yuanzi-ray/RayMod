using System;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Hud;

/// <summary>
/// <b>战斗 HUD 上的一个资源计数器</b>（图标 + 文字），像能量那样摆在能量球旁边。
/// 弹药和层数各用一个实例（见 <see cref="ResourceHud"/>）。
///
/// <para><b>挂到哪</b>：<c>NCombatRoom.Instance.Ui.EnergyCounterContainer</c>
/// —— 全走游戏**公开成员**：<c>NCombatRoom.Instance</c>（<c>NCombatRoom.cs:268</c>）
/// → <c>Ui</c>（<c>:276</c>）→ <c>NCombatUi.EnergyCounterContainer</c>（<c>NCombatUi.cs:241</c>，一个 <c>Control</c>，能量球也在里面）。</para>
///
/// <para><b>为什么不用 RitsuLib 的 <c>SecondaryResources</c></b>：那套是"第二个数值资源系统"，
/// 要求把数值整体迁进去、并注册 <c>SecondaryResourceDefinition</c> 且标记"始终显示"，
/// 否则 <c>RegisterCombatUpdater</c> 会**立刻把节点藏起来**。我们只要一个显示件，数值留在自己的
/// <c>AmmoState</c> / <c>DepthState</c> 就够了。</para>
///
/// <para><b>刷新</b>：调用方在自己的"数值汇聚点"里 <see cref="Push"/> 一次即可 —— 不查树、不轮询。
/// 节点自己拿不到 <c>Player</c>（<c>NEnergyCounter._player</c> 是私有的），所以推送是唯一干净的路。</para>
///
/// <para>⚠️ 节点挂在战斗 UI 上，会随界面一起销毁 —— 所以每次推送前验一次"还在不在"，不在就重建。</para>
/// </summary>
internal sealed class ResourceHudCounter
{
    /// <summary>
    /// ★ 2026-10-12 <b>改成六边形铭牌</b>（用户反馈 HUD"好丑"）。
    ///
    /// <para><b>为什么换</b>：原来是「圆角矩形 + 细描边」的 <c>Panel</c>，
    /// 而游戏自己的能量计用的是 <b>六边形金属板 + 倒角高光 + 四角铆钉 + 青灰结构色</b>
    /// （见 <c>RayMod_energy_counter.tscn</c> 与 <c>images/characters/RayMod_energy_orb_layer_1.png</c>）。
    /// 圆角矩形在这个游戏里没有先例 —— 所以它读起来像"临时贴上去的"。
    /// 换成同族语言之后就"是游戏的一部分"了。</para>
    ///
    /// <para>贴图是<b>程序化生成</b>的（<c>_tools\make_hud_plate.py</c>），不是抠能量计的图。</para>
    /// </summary>
    private const string PlateTexturePath = "res://RayMod/images/characters/RayMod_hud_plate.png";

    /// <summary>图标边长 = 基准 64 × <see cref="HudLayout.Scale"/>。</summary>
    private static float IconSize => (float)(64.0 * HudLayout.Scale);

    private static float LabelHeight => (float)(20.0 * HudLayout.Scale);

    /// <summary>
    /// 铭牌的边长。六边形是 1:1 的，所以"宽"就是"高" ——
    /// 图标要按铭牌的**内切圆**缩放，否则会顶到六条边上（见 <see cref="IconInsetFrac"/>）。
    /// </summary>
    private static float PlateWidth => IconSize * PlateWidthFrac;

    /// <summary>上下两个计数器之间的间距。</summary>
    private const float Gap = 6f;

    /// <summary>
    /// 铭牌相对图标的放大倍率。六边形在图标外还要留出**边框+铆钉**的余地，所以是 1.28。
    /// （这个值原样继承上一版的"底板宽度"，只是从"加宽的矩形"变成"六角铭牌"。）
    /// </summary>
    private const float PlateWidthFrac = 1.28f;

    /// <summary>
    /// 图标在铭牌内的缩放：六边形的**内切圆**半径 = 外接圆 × cos30° ≈ 0.866。
    /// ⚠️ 2026-10-12 用户反馈"power 贴图放里面太大了" → 从 0.73 收到 <b>0.56</b>：
    /// 图标只占内切圆的约六成，四周留出金属边和铆钉，读起来才像"嵌在铭牌里"而不是"撑满整块"。
    /// </summary>
    /// <summary>
    /// 图标边长 = 铭牌宽 × 这个系数。
    ///
    /// <para>★★ 2026-10-12 用户报"hub 里的贴图超出边界了" —— 真因：铭牌贴图 256×256 里是
    /// 一个**尖顶六边形**（上边是**两条斜边**），原来取 0.56 且上边距只留 6%，
    /// 于是图标的**两个上角必然戳出斜边**（截图里箭头就是这么冒出去的）。</para>
    ///
    /// <para>按"上角落在斜边之内 + 下边不压到数字"反推几何：设图标边长 s（铭牌宽为单位）、
    /// 上边距 y0，则斜边约束 y0 ≥ 0.045 + 0.254·s，数字行约束 y0 + s ≤ 0.60 ⇒ s ≤ 0.39。
    /// 取满值 0.39（配 <see cref="IconTopFrac"/> = 0.16）。</para>
    /// </summary>
    private const float IconInsetFrac = 0.39f;

    /// <summary>图标**上边距** = 铭牌宽 × 这个系数（和 <see cref="IconInsetFrac"/> 一起反推出来的）。</summary>
    private const float IconTopFrac = 0.16f;

    /// <summary>
    /// 整列相对"球高 0.60"再往上抬多少像素（会随 <see cref="HudLayout.Scale"/> 一起缩放）。
    /// 2026-10-09 用户反馈"整体往上移动几个像素" → 6px。
    /// ⚠️ 这是**基准**位移；想再微调仍用设置页的"上下偏移"。
    /// </summary>
    private const float LiftPixels = 6f;

    /// <summary>相对能量球的落点（以球尺寸为单位）。0.52 ≈ 球右侧。</summary>
    private const float OrbOffsetXFrac = 0.52f;

    /// <summary>
    /// 第一个计数器的**底边**落在球高的哪个位置。<b>0.60 = 球中下方</b>。
    /// ⚠️ 别再往下调：再往下就进手牌区了（用户截图里弹药计数器就是这么被卡牌盖住的）。
    /// 想微调请用设置页的"上下偏移"，别改这个常量。
    /// </summary>
    private const float OrbOffsetYFrac = 0.60f;

    private readonly string _nodeName;
    private readonly string _iconPath;
    private readonly int _slot;
    private readonly Logger _log;

    private Control? _root;
    private Label? _label;

    /// <summary>
    /// ★ 给「无限」的 HUD 演出用（<c>Hud\HudInfinityFx</c>）：牌子的根节点与数字 Label。
    ///
    /// <para>⚠️ 两个都可能被<b>重建或销毁</b>（<see cref="Invalidate"/>、战斗 UI 消失）——
    /// 调用方<b>每次都要重新取</b>，并验 <c>GodotObject.IsInstanceValid</c> + <c>IsInsideTree</c>，别缓存。</para>
    /// </summary>
    internal Control? Root => _root;

    /// <inheritdoc cref="Root"/>
    internal Label? LabelNode => _label;

    /// <summary>当前挂在谁身上（能量球本体，或退化成那个容器）。用于判断"要不要重新挂"。</summary>
    private Node? _host;

    private double _builtScale;
    private bool _loggedFailure;
    private bool _loggedAttached;

    /// <param name="slot">同一排里的第几个（0 最靠球）。</param>
    internal ResourceHudCounter(string nodeName, string iconPath, int slot, Logger log)
    {
        _nodeName = nodeName;
        _iconPath = iconPath;
        _slot = slot;
        _log = log;
    }

    /// <summary>
    /// ★ 提前挂载（战斗开始时调用，见 <c>ResourceHud.EnsureAttached</c>）：
    /// 节点不在就建，并显示占位值。真实数值到来时 <see cref="Push"/> 会覆盖。
    /// </summary>
    internal void EnsureAttached(string placeholder)
    {
        try
        {
            if (_root == null || !GodotObject.IsInstanceValid(_root) || !_root.IsInsideTree())
            {
                Attach();
            }

            if (_label != null && GodotObject.IsInstanceValid(_label) && _label.Text.Length == 0)
            {
                _label.Text = placeholder;
            }
        }
        catch (Exception e)
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                _log.Warn($"[HUD] {_nodeName} 提前挂载失败（只记一次）：{e.Message}");
            }
        }
    }

    /// <summary>推一次显示值。节点不在就顺手重建；战斗外调用是安全的（容器不存在 → 什么都不做）。</summary>
    internal void Push(string value)
    {
        try
        {
            if (_root == null || !GodotObject.IsInstanceValid(_root) || !_root.IsInsideTree())
            {
                Attach();
            }

            if (_label != null && GodotObject.IsInstanceValid(_label))
            {
                // ★ 2026-10-12 用户："获得弹药或者层数时让它们对应亮一下"。
                //   判据 = **只在数字变大时闪**（消耗弹药、层数下降都不闪）；
                //   第一次 Push（_lastValue 还空）也不闪 —— 否则战斗一开始两个计数器会同时亮一下。
                bool gained = _lastValue.Length > 0 && LeadNumber(value) > LeadNumber(_lastValue);

                _label.Text = value;

                if (gained)
                {
                    Flash();
                }

                _lastValue = value;
            }
        }
        catch (Exception e)
        {
            // 抛异常会污染整个同步链路：只记一次，绝不再抛。
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                _log.Warn($"[HUD] {_nodeName} 刷新失败（只记一次）：{e}");
            }
        }
    }

    /// <summary>上一次显示的值（判断"这次是不是变多了"）。</summary>
    private string _lastValue = string.Empty;

    /// <summary>取显示值开头的整数：「1/7」→ 1 · 「42」→ 42；取不到返回 <see cref="int.MinValue"/>（不触发闪烁）。</summary>
    private static int LeadNumber(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return int.MinValue;
        }

        int i = 0;

        while (i < s.Length && !char.IsDigit(s[i]))
        {
            i++;
        }

        int j = i;

        while (j < s.Length && char.IsDigit(s[j]))
        {
            j++;
        }

        return j > i && int.TryParse(s.AsSpan(i, j - i), out int v) ? v : int.MinValue;
    }

    /// <summary>
    /// <b>亮一下</b>：整块铭牌瞬间提亮（Modulate &gt; 1 = 比原色更亮，偏暖白）+ 轻微弹一下，
    /// 0.45s 回到原样。用 <c>CreateTween</c>（Godot 自己管生命周期），不动数值、不动布局。
    /// </summary>
    private void Flash()
    {
        if (_root == null || !GodotObject.IsInstanceValid(_root) || !_root.IsInsideTree())
        {
            return;
        }

        try
        {
            _root.Modulate = new Color(1.75f, 1.70f, 1.45f, 1f);
            _root.Scale = Vector2.One * 1.16f;

            Tween t = _root.CreateTween();
            t.TweenProperty(_root, "modulate", Colors.White, 0.45f)
             .SetEase(Tween.EaseType.Out)
             .SetTrans(Tween.TransitionType.Cubic);
            t.Parallel().TweenProperty(_root, "scale", Vector2.One, 0.45f)
             .SetEase(Tween.EaseType.Out)
             .SetTrans(Tween.TransitionType.Back);
        }
        catch (Exception e)
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                _log.Warn($"[HUD] {_nodeName} 闪烁失败（只记一次）：{e.Message}");
            }
        }
    }

    /// <summary>卸掉节点：下次 <see cref="Push"/> 会用新的布局参数重建（设置页改完位置时用）。</summary>
    internal void Invalidate()
    {
        _root = null;
        _label = null;
        _lastValue = string.Empty;      // 重建后第一次 Push 不闪（否则会误判成"获得"）
    }

    private void Attach()
    {
        _root = null;
        _label = null;

        Control? container = NCombatRoom.Instance?.Ui?.EnergyCounterContainer;

        // 战斗外 / UI 还没建：本来就没有 HUD，静默返回（不是错误）。
        if (container == null)
        {
            return;
        }

        // ★★ 2026-10-09：**挂到能量球【自己】身上，而不是那个容器。**
        //
        //   用户反馈"战斗结束不和能量一起消失"。根因：能量计的隐藏**不是** `Visible = false`，
        //   而是 `NEnergyCounter.AnimateOut()` 把**位置**补间到 `_hidePosition = (-480, 128)`
        //   —— 0.6 秒滑出屏幕（`NEnergyCounter.cs:156` 与 `:266`，入场 `AnimateIn` 同理）。
        //   我们以前是容器的**兄弟**、坐标自己算死 → 球滑走了我们还杵在原地。
        //
        //   挂成球的子节点之后：球的入场/退场补间会**顺带把我们带走**（连入场滑入也一起有了），
        //   而且坐标变成球的局部坐标，比"算球的位置再相加"更不容易错位。
        var orb = container.GetChildren().OfType<NEnergyCounter>().FirstOrDefault();
        Node host = orb ?? container;
        bool hostIsContainer = ReferenceEquals(host, container);

        // 幂等：同一个宿主上已经有就复用 —— 但**缩放变了必须重建**（图标尺寸和字号是建的时候定死的）。
        // 宿主变了（球刚建出来 / 换了新球）也必须重挂，否则会留在旧宿主上被一起销毁。
        var existing = host.GetNodeOrNull<Control>(_nodeName);

        if (existing != null && ReferenceEquals(_host, host)
            && Math.Abs(_builtScale - HudLayout.Scale) < 0.001)
        {
            _root = existing;
            _label = existing.GetNodeOrNull<Label>("Value");
            Layout(orb, hostIsContainer);
            return;
        }

        if (existing != null)
        {
            // 缩放变了 / 只是宿主换了：旧节点整个换掉（否则图标/字号还是老尺寸）。
            existing.QueueFree();
        }

        _root = Build();
        _builtScale = HudLayout.Scale;
        host.AddChild(_root);
        _host = host;
        Layout(orb, hostIsContainer);

        if (!_loggedAttached)
        {
            _loggedAttached = true;
            _log.Info($"[HUD] {_nodeName} 已挂上（槽位 {_slot}，{IconSize:F0}px，宿主="
                      + $"{(hostIsContainer ? "容器" : "能量球")}）");
        }
    }

    /// <summary>
    /// 摆位。★ 2026-10-08：改成**竖着排**（原来横排）——
    /// 横排时 slot 1 会往右伸进手牌区被卡牌盖住（用户截图）；竖排两个共用"球右侧"那一列。
    /// ⚠️ 竖排仍可能压到游戏自己的 Power 图标框 —— 所以位置**开放给设置页**调
    /// （<see cref="HudLayout"/> 的左右/上下偏移 + 缩放），用户当场拖滑条即可。
    /// </summary>
    private void Layout(NEnergyCounter? orb, bool hostIsContainer)
    {
        if (_root == null)
        {
            return;
        }

        Vector2 orbSize = orb?.Size ?? new Vector2(128f, 128f);

        // 挂在球身上时用的是球的**局部坐标**（球自己的位置由它的补间管，我们不用也不该加）；
        // 退化成挂在容器上时才需要把球的位移加回来。
        float baseX = 0f;
        float baseY = 0f;

        if (hostIsContainer && orb != null)
        {
            baseX = orb.Position.X;
            baseY = orb.Position.Y;
        }

        float x = baseX + orbSize.X * OrbOffsetXFrac - (PlateWidth - IconSize) / 2f
                  + (float)HudLayout.OffsetX;
        // ★ 六边形铭牌是 1:1（高 = 宽 = PlateWidth），不再是"图标 + 数字行"两段高。
        float rowStep = PlateWidth + Gap;
        float y = baseY + orbSize.Y * OrbOffsetYFrac - LiftPixels * (float)HudLayout.Scale
                  - _slot * rowStep + (float)HudLayout.OffsetY;

        _root.Position = new Vector2(x, y);
    }

    private Control Build()
    {
        float plateW = PlateWidth;

        var root = new Control
        {
            Name = _nodeName,
            Size = new Vector2(plateW, plateW),   // 六边形：正方形

            // ★ 闪烁时围绕**中心**弹一下（默认轴心在左上角，会变成往右下涨）
            PivotOffset = new Vector2(plateW / 2f, plateW / 2f),

            // 纯装饰，别吃鼠标事件（整个容器是叠在界面上的）。
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        // ★★ 2026-10-12：底板从「圆角矩形 Panel」换成【六边形金属铭牌】（贴图）。
        //   原因见 PlateTexturePath 的注释：圆角矩形在这个游戏里没有先例，所以显廉价。
        //   铭牌是 1:1 的六边形，图标按内切圆缩放后居中，"数字"叠在铭牌**下半**（带游戏同款描边）。
        var plate = new TextureRect
        {
            Name = "Plate",
            Position = Vector2.Zero,
            Size = new Vector2(plateW, plateW),          // 六边形：宽=高
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Texture = ResourceLoader.Load<Texture2D>(PlateTexturePath),
        };

        if (plate.Texture == null)
        {
            _log.Warn($"[HUD] 铭牌贴图加载失败：{PlateTexturePath}");
        }

        root.AddChild(plate);

        // 图标在铭牌里居中：按【内切圆】的六成缩放（图标太大就顶到六条边上、显拥挤）。
        // ⚠️ 不能把图标往下压：上半留给图标、下半留给数字，压下去会和数字打架。
        float iconDraw = plateW * IconInsetFrac;
        float pad = (plateW - iconDraw) / 2f;
        var iconRect = new TextureRect
        {
            Name = "Icon",
            Position = new Vector2(pad, plateW * IconTopFrac),
            Size = new Vector2(iconDraw, iconDraw),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        var texture = ResourceLoader.Load<Texture2D>(_iconPath);

        if (texture == null)
        {
            _log.Warn($"[HUD] 图标加载失败：{_iconPath}");
        }

        iconRect.Texture = texture;
        root.AddChild(iconRect);

        // 数字叠在铭牌下半部（不是铭牌外面 —— 那样读起来是"两个漂浮物"）。
        var label = new Label
        {
            Name = "Value",
            Position = new Vector2(0f, plateW * 0.60f),
            Size = new Vector2(plateW, plateW * 0.34f),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        // 描边/阴影整套照能量计那个 Label（`RayMod_energy_counter.tscn` 的 theme_override_*），
        // 只是按我们的缩放比缩小：那边是 36px 字号 + 16 描边配 128px 球，比例 ≈0.28；
        // 这里是 18px 字号 + 10 描边配 64px 图标（0.28 / 0.16），观感一致。
        // 字号跟着缩放走，否则图标缩了数字还顶满。
        label.AddThemeFontSizeOverride("font_size", (int)Math.Round(18.0 * HudLayout.Scale));
        label.AddThemeColorOverride("font_color", new Color(1f, 0.9647059f, 0.8862745f));
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.35f));
        label.AddThemeColorOverride("font_outline_color", new Color(0.08f, 0.18f, 0.24f));
        label.AddThemeConstantOverride("outline_size", (int)Math.Round(10.0 * HudLayout.Scale));
        label.AddThemeConstantOverride("shadow_outline_size", (int)Math.Round(10.0 * HudLayout.Scale));
        label.AddThemeConstantOverride("shadow_offset_x", Math.Max(1, (int)Math.Round(2.0 * HudLayout.Scale)));
        label.AddThemeConstantOverride("shadow_offset_y", Math.Max(1, (int)Math.Round(2.0 * HudLayout.Scale)));

        root.AddChild(label);

        _label = label;
        return root;
    }
}
