using System;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using RayMod.Ammo;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Hud;

/// <summary>
/// ★★★ <b>「无限」的 HUD 演出</b>（2026-10-12 用户亲自定的编排 —— "我有一计"）：
/// <b>上限那一位数字突然变异 → 一刀劈开 → 上限变成 ∞</b>。
///
/// <para><b>为什么演在 HUD 上，而不是场上</b>：这张牌的效果就是"弹药可以突破上限"，
/// 所以演出必须发生在<b>显示上限的那串字</b>上 —— 那是叙事上正确的位置，而且那串字本来就是
/// <b>我们自己的</b> <see cref="ResourceHudCounter"/>（<c>Label</c>，节点名 <c>Value</c>），
/// 改起来不用碰任何游戏 UI。</para>
///
/// <para><b>编排</b>（总长 <see cref="Total"/> = 3.37s —— 开场 2.05s + 原来那五拍 1.32s）：</para>
/// <list type="number">
/// <item><b>开场（0 ~ 2.05s，2026-10-13 用户定的）</b>：<b>屏幕中央由粒子烟雾组成一个 ∞</b>
/// （错开浮现 → 定住一拍）→ <b>每颗粒子各飞各的</b>（自己的贝塞尔弧线 + 出发延迟 + 时长），
/// 散开后再汇聚到 HUD"上限"位置、聚成一个小 ∞ ⇒ 用户原话："感觉屏幕中间浮现一个粒子烟雾组成的
/// ∞ 然后飞向弹药 hub"+"粒子肯定要散开飞向，整个平移太不美观了"。见 <c>HudInfinityFx.Opening.cs</c>。</item>
/// <item><b>2.28s</b>：★ <b>2026-10-13 用户改的编排</b> —— 上限那串字<b>不再"一路增长"</b>，而是
/// <b>略微振动</b>（越接近刀口抖得越厉害）+ <b>沙石从字上掉落</b>（"数字像砂岩一样崩解"）。
/// <b>当前弹药（分子）纹丝不动</b> —— 用户原话："<b>后面那个数字才是上限</b>"。</item>
/// <item><b>2.81s</b>：振动幅度的顶点（<b>原来是刀光时刻 —— 用户 2026-10-13 说"刀光也去掉啊"，
/// 刀光已整块删除</b>，见 <c>DrawCrumblingFlash</c> 的说明）。</item>
/// <item><b>3.01s</b>：白闪（就闪在被劈中的那串数字上），变异数字消失。</item>
/// <item><b>3.16s</b>：<b>上限变 ∞</b> —— 把牌子自己的 <c>Label</c> 亮回来（文字由
/// <c>AmmoState</c> 的推送同源给出，见那里的 <c>∞</c> 分支）。</item>
/// <item>之后本节点<b>不销毁</b>：只留一圈<b>金色高亮</b>罩着牌子（"上限已解除"的状态显示），
/// 直到 <c>AmmoState.IsOverCapThisTurn</c> 变假（= 下回合开始，<c>AmmoPower.AfterPlayerTurnStart</c>
/// 关掉开关那一刻）再淡出销毁 ⇒ <b>演出与数值同一个开关收尾</b>。</item>
/// </list>
///
/// <para>⚠️ <b>为什么挂在 <c>NCombatRoom.Instance.Ui</c> 上</b>：变异的数字会长到牌子外面（甚至压过角色），
/// 所以必须在 <b>HUD 之上</b>画 —— 不能进我们那套场上特效层（<c>CombatVfxContainer</c> 在 UI 下面，会被 HUD 挡）。</para>
///
/// <para>⚠️ <b>一切尺寸都按牌子的实际矩形算</b>（<c>_plateSize</c>），不写死屏幕像素 ——
/// 预览里那套数字是在 2559×1599 的截图坐标系里量的，直接搬到别的分辨率就会错位。</para>
/// </summary>
internal sealed partial class HudInfinityFx : Control
{
    /// <summary>总开关。
    /// ★ 2026-10-13：从 <c>const</c> 改成**读设置页的开关**（模组设置 → RayMod → 莱伊 · 特效
    /// → 无限 · HUD 演出）。关掉之后"上限解除"的数值照常，只是没有演出。</summary>
    internal static bool Enabled =>
        RayMod.Audio.VoiceState.VfxEnabled && RayMod.Audio.VoiceState.FieldVfxEnabled;

    // ── 时间表（秒）──
    // ★★ 2026-10-13：前面多了一段**开场**（屏幕中央烟雾 ∞ 成形 → **逐粒子**飞向弹药 HUD，
    //    见 `HudInfinityFx.Opening.cs`），原来的几拍**一个都没改**，只是整体后移 2.15s。
    private const float Total = FlyEnd + 1.32f;   // 3.37s（含开场 ≈2.05s + 收尾 1.32s）
    private const float MutateAt = 2.28f;     // 上限那位开始振动 + 沙石开始掉（= 烟落定后 0.23s 的喘息）
    private const float PeakAt = 2.81f;       // 振动幅度的顶点（**原来是刀光时刻**，刀光已去掉，§S40）
    private const float FlashAt = 3.01f;      // 一记白光（沙石**停止生成**）
    private const float RevealAt = 3.16f;     // ∞ 揭晓（恢复牌子自己的字）
    private const float RevealDone = 3.37f;

    // ★★★ 2026-10-13 用户改的设计（**别改回去**）：
    //    ① "把数字增长改掉吧。改成略微振动+有沙石掉落" ⇒ 发疯序列 `Glitch = {10,100,…}` **已删除**；
    //    ② "刀光也去掉啊" ⇒ **刀光（横扫的粗刃 + 残影 + 刃口 + 劈中火花）整块已删除**（§S40）。
    //    现在这一拍 = **数字略微振动 + 沙石崩落 → 一记白光 → ∞**。

    // ★★ 形状全部走**贴图**，不用 `DrawLine`/`DrawCircle` 硬画几何图元 ——
    //    2026-10-12 用户："总觉得想那种廉价页游"：平色等粗的方块/圆环/直线就是这个观感的来源。
    //
    // ⚠️ **刀光三张（厚刃 / 细刃口 / 劈中火花）已停用**（§S40 用户"刀光也去掉啊"）：
    //    贴图仍在包里（`fx_slash_heavy.png` / `fx_slash_fine.png` / `fx_scorch.png`），
    //    要加回来只需 ① 恢复下面三行常量 ② `_Ready` 里加载 ③ `_Draw` 里恢复刀光那一段（§S38/§S39 有原文）。
    //    现在**不加载** ⇒ 不占显存。
    // private const string SlashHeavyPath = Entry.ResPath + "/images/vfx/fx_slash_heavy.png";  // 厚刃（带锥度）
    // private const string SlashFinePath = Entry.ResPath + "/images/vfx/fx_slash_fine.png";    // 细亮刃口
    // private const string ScorchPath = Entry.ResPath + "/images/vfx/fx_scorch.png";           // 劈中的火花
    // ★★★ 2026-10-13 订正：`fx_circle_04` **中心 alpha = 0.000、峰值在 0.60R** ⇒ 它是**一根细亮环**，
    //    当柔光用就是**甜甜圈**（预览里一眼可见）。`fx_glow_solid.png` = 方舟 `bgsnow_moon_01`，
    //    实测 1.000 / 0.35R 1.000 / 0.65R 0.370 / 0.9R 0.018 = 平滑径向衰减、边缘干净。
    //    筛选用 `_tools\probe_glow_textures.py`（别靠名字猜形状）。
    private const string GlowPath = Entry.ResPath + "/images/vfx/fx_glow_solid.png";
    private const string FlarePath = Entry.ResPath + "/images/vfx/fx_flare_01.png";         // 白光芯
    private const string InfinityPath = Entry.ResPath + "/images/characters/RayMod_hud_infinity.png";

    private static readonly Logger Log = new("RayMod.Hud", LogType.Generic);

    private static bool _loggedFailure;
    private static HudInfinityFx? _live;

    // ── 状态 ──
    private Player _player = null!;
    private float _t;
    private bool _revealed;
    private Label? _number;
    private Control? _root;                     // 牌子的根节点（每帧重取）
    private Rect2 _plate;                       // 牌子的全局矩形（每帧重取，窗口缩放也不怕）
    private bool _overCap;                      // 本回合上限是否真的解除中（每帧更新）
    private float _fade = 1f;                   // 收尾淡出（上限解除后）
    private Texture2D? _glow;
    private Texture2D? _flare;
    private Texture2D? _infinityGlyph;

    /// <summary>
    /// 打出「无限」时调用（见 <c>Cards\Infinite.cs</c> 的 <c>OnPlay</c>）。
    /// 重复调用（例如同一回合连打两张）只让现有那份重新开始，不叠加。
    /// </summary>
    internal static void Play(Player? player)
    {
        if (!Enabled || player == null)
        {
            return;
        }

        // ★★ 联机门控（2026-10-13）：**HUD 演出只给本地玩家自己放**。
        //    这是挂在**我自己的**弹药计数器牌子上的演出（`ResourceHud.Ammo`），读的是我的数字；
        //    队友打出「无限」时在我这边放这套，不但位置对不上（他的牌子不在这），
        //    还会因为 `_live` 是静态单例、`:120` 会 `_live._player = player` 而**把我的演出抢走**。
        //    单机下 `LocalContext.IsMe` 恒真 ⇒ 行为零变化。
        if (!Art.CardFieldVfx.IsLocalPlayer(player))
        {
            return;
        }

        try
        {
            if (_live != null && GodotObject.IsInstanceValid(_live))
            {
                _live._player = player;
                _live._t = 0f;
                _live._revealed = false;
                _live._fade = 1f;
                _live._smokeCleared = false;      // ★ 开场（烟雾 ∞）要能再放一次
                _live.Visible = true;
                return;
            }

            Node? parent = UiParent();

            if (parent == null)
            {
                Log.Warn("[无限·HUD] 拿不到战斗 UI，跳过演出（数值不受影响）");
                return;
            }

            var fx = new HudInfinityFx { Name = "RayModInfinityHudFx", MouseFilter = MouseFilterEnum.Ignore };
            parent.AddChild(fx);
            fx.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            fx._player = player;
            _live = fx;
            Log.Info("[无限·HUD] 演出开始（上限那位变异 → 劈 → ∞）");
        }
        catch (Exception e)
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                Log.Error($"[无限·HUD] 启动失败（只记这一次）：{e}");
            }
        }
    }

    /// <summary>战斗 UI 根（和 HUD 计数器同一棵树，但挂它下面 ⇒ 画在 HUD 之上）。</summary>
    private static Node? UiParent()
    {
        try
        {
            return NCombatRoom.Instance?.Ui;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public override void _Ready()
    {
        SetProcess(true);
        _glow = ResourceLoader.Load<Texture2D>(GlowPath);
        _flare = ResourceLoader.Load<Texture2D>(FlarePath);
        _infinityGlyph = ResourceLoader.Load<Texture2D>(InfinityPath);
        BuildSmokeLayer();                       // ★ 开场那团烟（加色层，§S37.5）
        LoadSandTextures();                      // ★ 数字崩解用的沙石（§S39）

        if (_infinityGlyph == null)
        {
            // 贴图没进包就退化成"只有高亮与数字变化"，绝不报错刷屏
            Log.Warn("[无限·HUD] ∞ 字形贴图缺失" +
                     " ⇒ 演出降级；查 pck 是否包含 images/characters/RayMod_hud_infinity.png");
        }

        _number = new Label
        {
            Name = "MutatingNumber",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
            Text = string.Empty,
        };
        AddChild(_number);
    }

    public override void _Process(double delta)
    {
        try
        {
            _t += (float)delta;

            // ① 先取牌子矩形 + 把它自己的数字藏起来（免得和我们的变异数字叠字）
            if (!RefreshPlate())
            {
                QueueFree();
                return;
            }

            SetPlateNumberVisible(_t >= RevealAt || _t < MutateAt);

            // ② 到了 ∞ 那一拍：把牌子自己的数字亮回来（文字由 AmmoState 的推送同源给出）
            if (_t >= RevealAt && !_revealed)
            {
                _revealed = true;
                SetPlateNumberVisible(true);
                _number!.Text = string.Empty;
                PopPlate();
            }

            // ③ 上限解除（开关关了）⇒ 淡出收尾
            //    ★ `_overCap` 是**画不画 ∞、要不要接管那串字**的唯一判据 ——
            //      实机踩到：∞ 曾经在回合结束（数字已回到 `4/7`）之后还挂着淡出，贴在 7 后面，位置就错了。
            _overCap = AmmoCmd.IsOverCapThisTurn(_player);

            if (_t < MutateAt)
            {
                // ★★ 开场段：**上限还在**（牌子显示 `4/7`）。
                //    2026-10-13 加：`SetOverCapThisTurn` 一被调用，`AmmoState` 推送的就是 `<当前>/`
                //    （上限被压掉，见 AmmoHudText 的 ∞ 分支）⇒ 开场那 2 秒牌子会挂着一条"孤零零的斜杠"，
                //    而烟正是要**落到那个"7"所在的∞槽位**上。所以开场这一段由我们把上限显示回来，
                //    到「∞ 揭晓」那一拍再交还给 `AmmoState` 的同源文字。
                OwnLabelText(showCap: true);
            }
            else if (_overCap)
            {
                OwnLabelText(showCap: false);
            }
            else if (_revealed)
            {
                _fade -= (float)delta / 0.25f;

                if (_fade <= 0f)
                {
                    _live = null;
                    QueueFree();
                    return;
                }
            }

            // ④ 变异数字（只在露出 ∞ 之前画）
            UpdateNumber();

            // ⑤ 本帧的绘制（刀光 / 白闪 / 牌子高亮）
            QueueRedraw();
            RequestSmokeRedraw();                // ★ 开场那团烟画在加色子层上
        }
        catch (Exception e)
        {
            // 演出失败绝不能拖累战斗（这个节点每帧都在跑）
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                Log.Error($"[无限·HUD] 演出异常（只记这一次）：{e}");
            }

            _live = null;
            QueueFree();
        }
    }

    /// <summary>每帧重取牌子的矩形（计数器会被重建 ⇒ 不能缓存节点）。</summary>
    private bool RefreshPlate()
    {
        Control? root = ResourceHud.Ammo.Root;

        if (root == null || !GodotObject.IsInstanceValid(root) || !root.IsInsideTree())
        {
            return false;
        }

        // ★★★ 坐标必须换算到**本节点的局部系**：`_Draw()` 画的是局部坐标，
        //     而这里拿到的是**全局**（画布）坐标 —— 两者差一个 `GlobalPosition`。
        //     2026-10-12 实机"动画位置不对"就是这个：直接用全局坐标画 ⇒ 整套演出整体偏移。
        _plate = new Rect2(ToLocal(root.GlobalPosition), root.Size);

        return _plate.Size.X > 4f && _plate.Size.Y > 4f;
    }

    /// <summary>全局（画布）坐标 → 本浮层的局部坐标。</summary>
    private Vector2 ToLocal(Vector2 global)
    {
        // UI 这一层没有旋转/缩放，减掉自己的 GlobalPosition 就够（铁律：别用 Node2D 的 ToLocal，
        // Control 没那个方法；也不要用 GlobalTransform 硬套，链上有 anchor 时容易算错）。
        return global - GlobalPosition;
    }

    private void SetPlateNumberVisible(bool visible)
    {
        Label? label = ResourceHud.Ammo.LabelNode;

        if (label != null && GodotObject.IsInstanceValid(label))
        {
            label.Visible = visible;
        }
    }

    /// <summary>
    /// <b>接管牌子那串字</b>：上限解除期间让它显示 <c>&lt;当前弹药&gt;/</c>，那个 <c>∞</c> 由我们画在它右边。
    ///
    /// <para>为什么要接管：<c>∞</c> 的位置是"量出文字右端"算出来的 —— 如果标签此刻显示的是 <c>4/7</c>
    /// （例如刚被别的推送刷新过），<c>∞</c> 就会贴在 <c>7</c> 后面（实机踩过）。
    /// 每帧按当前弹药重写一次，顺便保证弹药花掉时那串字也是活的。</para>
    ///
    /// <param name="showCap">
    /// 开场段（见 <c>HudInfinityFx.Opening.cs</c>）要传 <c>true</c>：那时上限**还没**被变异，
    /// 牌子该显示 <c>4/7</c>（烟要落到那个"7"所在的槽位上）；其余时候传 <c>false</c>。
    /// </param>
    /// </summary>
    private void OwnLabelText(bool showCap)
    {
        Label? label = ResourceHud.Ammo.LabelNode;

        if (label != null && GodotObject.IsInstanceValid(label))
        {
            label.Text = showCap
                ? $"{AmmoCmd.GetAmmo(_player)}/{AmmoCmd.GetCap(_player)}"
                : $"{AmmoCmd.GetAmmo(_player)}/";
        }
    }

    /// <summary>∞ 揭晓时牌子"弹一下"（复用计数器自己那套：Modulate 提亮 + Scale 放大）。</summary>
    private void PopPlate()    {
        Control? root = ResourceHud.Ammo.Root;

        if (root == null || !GodotObject.IsInstanceValid(root))
        {
            return;
        }

        root.Modulate = new Color(1.85f, 1.70f, 1.30f, 1f);
        root.Scale = Vector2.One * 1.22f;
        Tween tween = root.CreateTween();
        tween.TweenProperty(root, "scale", Vector2.One, 0.26f).SetTrans(Tween.TransitionType.Back);
        tween.Parallel().TweenProperty(root, "modulate", Colors.White, 0.40f);
    }

    // ── 数字 ─────────────────────────────────────────────────────────────

    private float BaseFont => Mathf.Clamp(_plate.Size.Y * 0.30f, 12f, 40f);

    /// <summary>
    /// 变异数字的左端锚点：**就是牌子原本那串字的位置**（居中排的数字 ⇒ 左端 = 中心 − 量出的字宽/2）。
    ///
    /// <para>⚠️ 2026-10-13 再钉一次：现在这一段**不再增长**，画的就是**同一个 `4/7`**，
    /// 所以只要锚点偏一点，用户就会看到"数字跳了一下"。⇒ 锚点直接取**牌子 Label 自己的矩形中心**
    /// （`ToLocal(label.GlobalPosition) + label.Size/2`），拿不到 Label 时才退回按牌子算的经验式。</para>
    /// </summary>
    private Vector2 NumberAnchor
    {
        get
        {
            Label? label = ResourceHud.Ammo.LabelNode;

            if (label != null && GodotObject.IsInstanceValid(label) && label.IsInsideTree())
            {
                Vector2 center = ToLocal(label.GlobalPosition) + label.Size / 2f;

                // ⚠️ 量的是**我们即将画的那串字**（`<当前>/<上限>`），**不是** `label.Text`：
                //    崩解那一段牌子的字已经被 `OwnLabelText(showCap:false)` 改成 `4/`（给 ∞ 让位），
                //    照它量会短一截 ⇒ 我们画的 `4/7` 会整体右移半个字宽（"数字跳一下"）。
                float w = TextWidth($"{AmmoCmd.GetAmmo(_player)}/{AmmoCmd.GetCap(_player)}");

                return new Vector2(center.X - w / 2f, center.Y);
            }

            float baseW = BaseFont * 1.9f;                       // "4/7" 这类短串的大致宽度
            Vector2 fallback = _plate.Position + _plate.Size / 2f;

            return new Vector2(fallback.X - baseW / 2f, _plate.Position.Y + _plate.Size.Y * 0.80f);
        }
    }

    /// <summary>量一段文字的像素宽度（拿不到字体时按字号估）。</summary>
    private float TextWidth(string text)
    {
        Label? label = ResourceHud.Ammo.LabelNode;

        if (label != null && GodotObject.IsInstanceValid(label))
        {
            Font? font = label.GetThemeFont("font");
            int fs = label.GetThemeFontSize("font_size");

            if (font != null)
            {
                return font.GetStringSize(text, HorizontalAlignment.Left, -1, fs).X;
            }
        }

        return BaseFont * 0.6f * Mathf.Max(1, text.Length);
    }

    /// <summary>
    /// ★★ 2026-10-13 用户改的编排："<b>把数字增长改掉吧。改成略微振动+有沙石掉落</b>"。
    ///
    /// <para>被改掉的是 2026-10-12 用户自己定的"数字突然变异 10 → 100 → 1000 → …" ——
    /// <b>这是设计变更，不是 bug</b>。现在的读法：上限那串字<b>只是略微发抖</b>（越接近刀口抖得越厉害），
    /// 同时<b>沙石从字上掉下来</b>（见 <c>HudInfinityFx.Sand.cs</c>）⇒ "数字像砂岩一样在崩解"。</para>
    ///
    /// <para>因为不再增长，这段画的就是**和牌子一模一样的那串字** ⇒ 必须把牌子 Label 的主题
    /// （字号 / 颜色 / 描边 / 阴影）整套抄过来，否则会"看起来像另一个数字"（<see cref="ApplyPlateTheme"/>）。</para>
    /// </summary>
    private void UpdateNumber()
    {
        if (_number == null || !GodotObject.IsInstanceValid(_number))
        {
            return;
        }

        if (_t < MutateAt || _t >= RevealAt)
        {
            _number.Text = string.Empty;
            return;
        }

        ApplyPlateTheme();

        // 振动：**细抖**（23Hz），幅度从 45% 涨到 100%（越接近刀口越抖）
        // ⚠️ 归一化单位是**字号**（不是牌子宽）—— 预览与游戏两边字号是同一个数，见 Sand.cs 常量区
        int baseSize = PlateFontSize();
        float ramped = Mathf.Clamp((_t - MutateAt) / Mathf.Max(0.05f, PeakAt - MutateAt), 0f, 1f);
        float amp = baseSize * VibeAmp * Mathf.Lerp(1f - VibeRamp, 1f, ramped);
        float phase = _t * Mathf.Tau * VibeHz;

        _number.Text = $"{AmmoCmd.GetAmmo(_player)}/{AmmoCmd.GetCap(_player)}";
        _number.Modulate = new Color(1f, 1f, 1f, _fade);
        _number.Position = NumberAnchor + new Vector2(
            Mathf.Sin(phase) * amp,
            Mathf.Cos(phase * 0.83f) * amp * 0.75f);          // 两个轴不同频 ⇒ 抖动没有规律感
        _number.Scale = Vector2.One;

        // 字号只做 ±2.5% 的脉动（张力）—— 不是增长
        _number.AddThemeFontSizeOverride("font_size",
            Mathf.Max(8, (int)Mathf.Round(baseSize * (1f + PulseAmp * Mathf.Sin(_t * 37f)))));
    }

    /// <summary>牌子那串字的字号（拿不到就按牌子高估一个）。</summary>
    private int PlateFontSize()
    {
        Label? label = ResourceHud.Ammo.LabelNode;

        if (label != null && GodotObject.IsInstanceValid(label))
        {
            int fs = label.GetThemeFontSize("font_size");

            if (fs > 0)
            {
                return fs;
            }
        }

        return (int)Mathf.Clamp(_plate.Size.Y * 0.22f, 10f, 48f);
    }

    /// <summary>
    /// 把牌子 Label 的主题整套抄到我们这串字上 —— 字号 / 颜色 / 描边 / 阴影。
    ///
    /// <para>为什么必须抄：这一段画的是**同一个 `4/7`**（不再变异成别的数字），
    /// 少了描边与阴影就会明显"像贴上去的假数字"。值全走 <c>GetTheme*</c> 读**运行时真值**
    /// （铁律 35：别写死"设置值"）。</summary>
    private void ApplyPlateTheme()
    {
        Label? src = ResourceHud.Ammo.LabelNode;

        if (_number == null || src == null || !GodotObject.IsInstanceValid(src))
        {
            return;
        }

        _number.AddThemeColorOverride("font_color", src.GetThemeColor("font_color"));
        _number.AddThemeColorOverride("font_outline_color", src.GetThemeColor("font_outline_color"));
        _number.AddThemeColorOverride("font_shadow_color", src.GetThemeColor("font_shadow_color"));
        _number.AddThemeConstantOverride("outline_size", src.GetThemeConstant("outline_size"));
        _number.AddThemeConstantOverride("shadow_outline_size", src.GetThemeConstant("shadow_outline_size"));
        _number.AddThemeConstantOverride("shadow_offset_x", src.GetThemeConstant("shadow_offset_x"));
        _number.AddThemeConstantOverride("shadow_offset_y", src.GetThemeConstant("shadow_offset_y"));

        Font? font = src.GetThemeFont("font");

        if (font != null)
        {
            _number.AddThemeFontOverride("font", font);
        }
    }

    // ── 绘制：全部走**贴图 + 加色**（不用几何图元，见类顶部的说明）──────────────────

    public override void _Draw()
    {
        float w = _plate.Size.X;
        Vector2 center = _plate.Position + _plate.Size / 2f;
        float cy = _plate.Position.Y + _plate.Size.Y * 0.80f;

        DrawPlateGlow(center, w);                       // 牌子背后的柔光（"hub 高亮"）

        DrawSand();                                     // ★ 数字崩解掉下来的沙石（§S39）

        if (_t >= RevealAt && _overCap)
        {
            DrawInfinityGlyph();                        // ∞ 字形贴图（贴在"当前/"后面）
        }

        DrawCrumblingFlash();                           // ★ 崩解到极致的一记白光（§S40）
    }

    /// <summary>
    /// ★★ 2026-10-13 用户："**刀光也去掉啊**" ⇒ 原来那一整段（横扫的粗刃 + 两道残影 + 细刃口 +
    /// 劈中的放射火花）**已删除**。留下的只有这一记**白光** —— 它是"崩解到底"与"变成 ∞"之间的那一下，
    /// 也是用户 2026-10-12 原方案里就有的"闪一下"（删掉的是刀，不是闪）。
    ///
    /// <para>画在**上限那串字**的位置（`plate.X + 1.30W`，和原来刀口落点同一个地方），
    /// 一记柔光外扩 + 一个白芯。</para>
    /// </summary>
    private void DrawCrumblingFlash()
    {
        float k = Mathf.Clamp((_t - FlashAt) / 0.30f, 0f, 1f);

        if (_t < FlashAt || k >= 1f)
        {
            return;
        }

        float w = _plate.Size.X;
        float cy = _plate.Position.Y + _plate.Size.Y * 0.80f;
        var at = new Vector2(_plate.Position.X + w * 1.30f, cy);
        float e = Smoothstep(k);

        // 柔光外扩（"崩解到底"的那一圈光）+ 白芯
        DrawTex(_glow, at, w * Mathf.Lerp(0.8f, 2.6f, e), 0f,
                new Color(1f, 0.88f, 0.62f, 0.85f * (1f - e) * _fade), 1.6f);
        DrawTex(_flare, at, w * Mathf.Lerp(1.5f, 0.9f, e), 0f,
                new Color(1f, 1f, 1f, Mathf.Lerp(0.95f, 0f, k) * _fade), 1.4f);
    }

    /// <summary>按"目标宽度 + 旋转"摆一张贴图（Godot 的 `DrawTexture` 本身不支持旋转，得先 `DrawSetTransform`）。</summary>
    private void DrawTex(Texture2D? tex, Vector2 center, float targetWidth, float angle, Color color,
                         float gain = 1f)
    {
        DrawTexOn(this, tex, center, targetWidth, angle, color, gain);
    }

    /// <summary>
    /// 同上，但**画到指定的 CanvasItem 上**。
    ///
    /// <para>⚠️ 为什么必须有这个重载：`DrawTexture` / `DrawSetTransform` 作用的对象是**调用者**，
    /// 而且只有那个节点正在 `_Draw` 里时才有权画。开场那团烟画在**加色子层**
    /// （<see cref="HudInfinitySmokeLayer"/>）的 `_Draw` 里 ⇒ 必须把那一层传进来，
    /// 否则会在父节点上调用、报 <c>Drawing is only allowed inside NOTIFICATION_DRAW</c> 且**什么都不画**
    /// （编译、部署、`-Verify` 全过，画面上却没有烟 —— 2026-10-13 第一版就是这个 bug）。</para>
    /// </summary>
    private static void DrawTexOn(CanvasItem canvas, Texture2D? tex, Vector2 center, float targetWidth,
                                  float angle, Color color, float gain = 1f)
    {
        if (tex == null || color.A <= 0.004f)
        {
            return;
        }

        float s = targetWidth / Mathf.Max(1f, tex.GetWidth());
        color = new Color(color.R * gain, color.G * gain, color.B * gain, color.A);
        canvas.DrawSetTransform(center, angle, Vector2.One * s);
        canvas.DrawTexture(tex, new Vector2(-tex.GetWidth() / 2f, -tex.GetHeight() / 2f), color);
        canvas.DrawSetTransform(Vector2.Zero, 0f, Vector2.One);   // ★ 用完必须复位，否则后面全跟着转
    }

    /// <summary>
    /// 牌子高亮（用户："最后给 hub 上个高亮"）：**一张软径向光贴图**垫在牌子后面 + 牌子自己的弹跳。
    ///
    /// <para>⚠️ 走过的弯路都记在这：① 两圈 `DrawRect` = 一个围住 HUD 的黑方框；② 等粗六边形描边 +
    /// 几个平色 `DrawCircle` = 用户"这也太大了 / 像廉价页游"。几何图元天生缺 bloom 与层次，
    /// 换成贴图立刻就有"游戏里该有的光"了 —— 但**必须是真·径向光**：2026-10-13 实测
    /// `fx_circle_04` 是一根细亮环（中心 alpha 0），拿它当柔光就是甜甜圈，
    /// 现在用的是 `fx_glow_solid.png`（平滑径向衰减，见 <see cref="GlowPath"/>）。</para>
    /// </summary>
    private void DrawPlateGlow(Vector2 center, float w)
    {
        float pulse = _revealed
            ? 0.55f                                             // ∞ 之后：稳定
            : 0.26f + 0.40f * Mathf.Abs(Mathf.Sin(_t * 9f));     // 变异期间：脉动
        DrawTex(_glow, center, w * 2.3f, 0f, new Color(1f, 0.84f, 0.50f, pulse * _fade * 0.75f));
    }

    /// <summary>
    /// ★★★ <b>画 <c>∞</c></b>：直接贴**自己生成的字形贴图**
    /// （`RayMod_hud_infinity.png`，伯努利双纽线 + 金属管高光 + bloom，见 `_tools\make_infinity_assets.py`）。
    ///
    /// <para><b>为什么不用文字</b>：实机（2026-10-12）往 HUD 的 <c>Label</c> 里塞 <c>∞</c>（U+221E）
    /// <b>什么都不显示</b> —— 游戏字库里多半没有这个字形，字库还打包在 pck 里、离线查不了（**铁律 74**）。</para>
    ///
    /// <para><b>为什么不用 `DrawArc` 画两个环</b>：等粗双环是"廉价页游"观感的典型来源（用户原话）；
    /// 贴图能带上锥度、暗描边、高光与光晕。</para>
    ///
    /// <para><b>画在哪</b>：牌子自己的数字是<b>居中</b>对齐的（<c>Size = (plateW, 0.34·plateW)</c>），
    /// 所以文字右端 = 矩形中心 + 量出来的文字宽度一半；<c>∞</c> 就贴在斜杠右边。</para>
    /// </summary>
    private void DrawInfinityGlyph()
    {
        Label? label = ResourceHud.Ammo.LabelNode;

        if (label == null || !GodotObject.IsInstanceValid(label) || !label.IsInsideTree() || _infinityGlyph == null)
        {
            return;
        }

        float k = Mathf.Clamp((_t - RevealAt) / 0.22f, 0f, 1f);     // 揭晓那一拍"弹出来"
        float ease = Smoothstep(k);
        float fontSize = label.GetThemeFontSize("font_size");

        Font? font = label.GetThemeFont("font");
        float textW = font != null
            ? font.GetStringSize(label.Text, HorizontalAlignment.Left, -1, (int)fontSize).X
            : fontSize * Mathf.Max(1, label.Text.Length) * 0.6f;

        float size = fontSize * 1.60f * Mathf.Lerp(0.55f, 1.0f, ease);
        Vector2 at = ToLocal(label.GlobalPosition)
                     + new Vector2(label.Size.X / 2f + textW / 2f + size * 0.52f, label.Size.Y / 2f);

        DrawTex(_infinityGlyph, at, size, 0f, new Color(1f, 1f, 1f, _fade * Mathf.Lerp(0.35f, 1f, ease)), 1.90f);
    }

    private static float Smoothstep(float x)
    {
        x = Mathf.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }
}
