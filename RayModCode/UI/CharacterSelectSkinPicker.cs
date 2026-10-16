using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Logging;
using RayMod.Art;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;
// ★ 2026-10-09 新增：按钮音（游戏自带 UI 音效）+ 过场淡出检测。
using FmodSfx = MegaCrit.Sts2.Core.Audio.FmodSfx;
using SfxCmd = MegaCrit.Sts2.Core.Commands.SfxCmd;
using NGame = MegaCrit.Sts2.Core.Nodes.NGame;

namespace RayMod.UI;

/// <summary>
/// <b>角色选择界面上的皮肤轮播</b>：中间一个<b>活的 Spine 预览</b>，左右两个游戏自带的金色三角按钮切换，
/// 下面显示皮肤名。
///
/// <para><b>怎么挂上去的</b>：这个类被 <c>RayMod_character_select_bg.tscn</c> 里那个
/// <c>SkinPicker</c> 节点用 <c>script = ExtResource(...)</c> 引用；那个场景会被游戏实例化进选角界面的
/// <c>AnimatedBg</c>（<c>NCharacterSelectScreen.cs:413</c>），由 RitsuLib 的
/// <c>CharacterSelectBackgroundRuntimeFactoryPatch</c> 把 <c>CharacterSelectBgPath</c> 变成 <c>Control</c>。</para>
///
/// <para><b>⚠️ 布局全用"屏幕比例"</b>：左侧是游戏自己的角色信息面板、底部中间是 Boss 信息框、
/// 右下是确认按钮 —— 控件放在<b>中偏右</b>的空区（用户指定的"红框"位置）。
/// 要挪位置只改下面那组 <c>*Frac</c> / <c>*Scale</c> 常量。</para>
///
/// <para><b>⚠️⚠️ 摆位为什么用"骨架原点"而不是"包围盒中心"</b>：试过用
/// <c>skeleton.get_bounds()</c> 自动居中，但三套皮肤量出来的可视中心差了 4 倍
/// （夏卉 +177 / 默认 +176 / 梦幻高空 <b>+636</b> —— 后者是吉他伸得很远、把包围盒撑宽了），
/// 按它居中会导致**换皮肤时模型横向跳几百像素**。
/// 骨架原点是<b>脚底</b>（实测包围盒底边 y≈0），三套都一致，所以用它当锚点最稳。</para>
///
/// <para>⚠️ <b>选择是"下一场战斗生效"</b>：战斗形象在进战斗时就建好了，中途换骨架会重置动画（铁律 18）。</para>
/// </summary>
public partial class CharacterSelectSkinPicker : Control
{
    private static readonly Logger Log = new("RayMod.Skin", LogType.Generic);

    // ───────────────────────── 可调布局（都是父控件尺寸的比例） ─────────────────────────
    /// <summary>模型**脚底原点**的横向位置（用户 2026-10-07 指定的"红框"中心横向）。</summary>
    private const float PreviewCenterXFrac = 0.74f;

    /// <summary>模型脚底的纵向位置（红框底边）。</summary>
    private const float PreviewGroundYFrac = 0.71f;

    /// <summary>预览缩放。战斗里用的是 <c>0.678</c>。2026-10-07 用户反馈 0.8 太大 → 0.6。</summary>
    private const float PreviewScale = 0.6f;

    /// <summary>
    /// 模型**身体中心**相对骨架原点的横向偏移（骨架单位）。<b>实测就是 0</b> —— 别动它。
    ///
    /// <para><b>踩过两次坑，都记在这：</b></para>
    /// <list type="number">
    /// <item>用 <c>skeleton.get_bounds()</c> 自动居中 → 三套皮肤量出来差 4 倍
    /// （夏卉 +177 / 默认 +176 / 梦幻高空 <b>+636</b>，吉他伸太远把包围盒撑宽了），
    /// 换皮肤时模型会横跳几百像素。❌</item>
    /// <item>改成"统一用 177 当身体中心" → 2026-10-07 用户实测：模型**正好偏左约 105px**，
    /// 而 177×0.6 ≈ 106px 就是我加的左移量。❌</item>
    /// </list>
    ///
    /// <para><b>结论</b>：包围盒中心被武器/道具撑宽，**不代表身体中心**。
    /// 骨架原点在脚底，而脚底就在身体正下方 → <b>原点直接对到目标中心即可</b>。</para>
    /// </summary>
    private const float VisualCenterOffsetUnits = 0f;

    /// <summary>
    /// 箭头中心的纵向位置。<b>要对齐模型的"身体中段"</b>，不是随便取个 0.5——
    /// 2026-10-07 用户反馈 0.50 时箭头浮在模型头部上方、看着不合理。
    /// 模型：脚底 0.71、高约 <c>429×0.6</c> → 身体中段约 <c>0.59</c>。
    /// </summary>
    private const float ArrowYFrac = 0.60f;

    /// <summary>箭头相对模型中心的横向偏移。要比身体半宽大，不然会被模型盖住（用户反馈过一次）。</summary>
    private const float ArrowOffsetXFrac = 0.09f;

    /// <summary>按钮边长（像素）。游戏那张箭头贴图是 128×128。</summary>
    private const float ArrowSize = 104f;

    /// <summary>覆盖层 z_index（按钮与皮肤名）。<b>必须大于模型用的 0</b> —— SpineSprite 是 Node2D，
    /// 会自己管绘制顺序，只靠 AddChild 的先后不保证压得住它。</summary>
    private const int OverlayZIndex = 20;

    /// <summary>皮肤名标签的纵向位置。⚠️ 必须在 Boss 信息框（约从高 68% 开始）之上或横向避开它。</summary>
    private const float LabelYFrac = 0.775f;

    // 游戏自带的金色三角箭头贴图（NGoldArrowButton 用的那套，common_ui 下）。
    // ⚠️ 这两个是**游戏本体**的资源，不在我们的 pck 里 —— 但运行时能直接 Load（游戏 pck 已挂载）。
    private const string ArrowLeftTexturePath = "res://images/packed/common_ui/settings_tiny_left_arrow.png";
    private const string ArrowRightTexturePath = "res://images/packed/common_ui/settings_tiny_right_arrow.png";

    /// <summary>贴图加载失败时自己画三角用的颜色/描边（兜底，不让按钮消失）。</summary>
    private static readonly Color ArrowFallbackColor = new(1f, 0.86f, 0.16f);
    private static readonly Color ArrowFallbackOutline = new(0.12f, 0.10f, 0.06f);
    private const float FallbackHalfW = 34f;
    private const float FallbackHalfH = 46f;
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>骨架异步建，轮询它是否就绪的上限（拍 × 0.05s）。</summary>
    private const int ReadyPollTicks = 60;

    /// <summary>场景里三套预览**插槽**的名字前缀（<c>PreviewSlot0/1/2</c>，下标 = catalog 下标）。</summary>
    private const string PreviewSlotNamePrefix = "PreviewSlot";

    private static string PreviewSlotName(int index) => $"{PreviewSlotNamePrefix}{index}";

    /// <summary>
    /// 三套皮肤的**插槽**（普通 <see cref="Node2D"/>），下标 = <see cref="RaySkinCatalog.All"/> 的下标。
    ///
    /// <para><b>★★ 为什么插槽是普通 Node2D，而不是直接拿 SpineSprite</b>：
    /// SpineSprite 是 GDExtension 类，**C# 拿不到它的托管包装** ——
    /// <c>GetNodeOrNull&lt;Node2D&gt;("…")</c>、甚至无类型的 <c>GetNodeOrNull("…")</c> 都会返回 <b>null</b>
    /// （2026-10-07 无头实测：一个都没拿到 → 显隐没生效 → 三套模型叠在一起）。
    /// 所以：**C# 只命令普通 Node2D 插槽（位置/缩放/显隐），骨架自己用
    /// <c>SkinPreviewSprite.gd</c> 播自己的动画。**</para>
    ///
    /// <para><b>★★ 为什么三套常驻、切换只切显隐</b>：最初只有<u>一个</u> SpineSprite，
    /// 切换时换 <c>skeleton_data_res</c> —— 那会让 spine 当场重建整个骨架
    /// （原版那套 .skel 有 1.15MB、56 个动画），主线程直接卡一下（用户反馈"切换不丝滑、会卡一下"）。
    /// 现在三套各自建好骨架并循环播 Idle，切换只是 <c>visible</c> 开关 → <b>瞬时、绝对平滑</b>。</para>
    /// </summary>
    private readonly List<Node2D> _previews = [];

    private Button? _arrowLeft;
    private Button? _arrowRight;
    private Label? _nameLabel;

    private int _index;

    /// <summary>已经因为过场而藏起来了（藏过就不再显示 —— 这次选角已经结束了）。</summary>
    private bool _hidden;

    public override void _Ready()
    {
        try
        {
            _index = IndexOf(RaySkinState.Selected);

            BuildPreview();
            BuildArrows();
            BuildLabel();
            Layout();

            Resized += Layout;   // 分辨率/父控件尺寸变化时重新摆位

            // ── 语音：选中她 →「编入队伍」──
            // ⚠️ oncePerCombat:false 是**必须的**：选角是场外的一次性事件，
            // 而 Squad 那句也可能在战斗里召唤沙地兽时播过 —— 用默认值会被"本场已播"挡掉。
            Audio.VoicePlayer.Play(
                Audio.VoiceCatalog.CharacterSelect, Audio.VoiceCategory.System, oncePerCombat: false);

            Log.Info($"[皮肤] 选角轮播已就绪（{RaySkinCatalog.All.Count} 套，当前 {RaySkinCatalog.DisplayName(RaySkinState.Selected)}）");
        }
        catch (Exception e)
        {
            // 选角界面的装饰出问题，绝不能影响能不能开始游戏。
            Log.Warn($"[皮肤] 选角轮播创建失败（不影响游戏）：{e}");
        }
    }

    /// <summary>
    /// ★ 2026-10-09：**过场一开始就把自己藏起来**（用户反馈"按钮和文字要开始之后一会才消失"）。
    ///
    /// <para><b>为什么光靠画面淡出盖不住</b>：我们的按钮和皮肤名设了
    /// <c>ZIndex = OverlayZIndex(20)</c>（为了压住 SpineSprite），而游戏的淡出遮罩
    /// <c>NGame.Transition</c> 是一个 <b>z_index 默认 0</b> 的 <c>ColorRect</c>
    /// （<c>%GameTransitionRect</c>，见 <c>NGame.cs:553</c>）。
    /// Godot 的绘制顺序是「先比 z_index，再比树序」——<b>z_index 大的先画在后（更靠上）</b>，
    /// 所以我们那 20 会**盖在淡出遮罩之上**：画面已经淡黑/切走了，按钮还浮在上面，
    /// 一直挂到选角场景被真正销毁为止。</para>
    ///
    /// <para><b>为什么不干脆把 z_index 降到 0</b>：SpineSprite 自己管绘制顺序，
    /// 树序压不住它（用户报过一次"另一个按钮看不到"）。所以 z_index 必须留着 ——
    /// 改成**主动侦测过场**：<c>NTransition.InTransition</c> 是 public 的，
    /// <c>FadeOut</c> 一开始就置 true（<c>NTransition.cs:113/117</c>），FadeIn 完置 false。</para>
    /// </summary>
    public override void _Process(double delta)
    {
        if (_hidden || _arrowLeft == null)
        {
            return;   // 还没建好，或者已经藏了
        }

        try
        {
            if (NGame.Instance?.Transition is { InTransition: true })
            {
                _hidden = true;
                Visible = false;
                SetProcess(false);   // 藏完就不用再轮询了
                Log.Info("[皮肤] 检测到过场淡出 → 皮肤轮播（按钮/皮肤名/预览）已隐藏");
            }
        }
        catch
        {
            // 读不到就下一帧再试 —— 这个装饰绝不能影响开局。
        }
    }

    // ───────────────────────────── 构建 ─────────────────────────────

    private void BuildPreview()
    {
        // ★ 三套骨架都在场景里声明好了（SkinPicker/PreviewSlot0..2/Sprite）。
        //   这里只负责：拿到**插槽**（普通 Node2D，C# 认得出）、把非当前那几套藏起来。
        //   骨架自己的 Idle 由 SkinPreviewSprite.gd 负责，C# 不碰 SpineSprite。
        for (int i = 0; i < RaySkinCatalog.All.Count; i++)
        {
            var slot = GetNodeOrNull<Node2D>(PreviewSlotName(i));

            if (slot == null)
            {
                Log.Warn($"[皮肤] 场景里没有 {PreviewSlotName(i)} 插槽（第 {i} 套皮肤不会有预览）");
                continue;
            }

            _previews.Add(slot);
        }

        ShowOnly(_index);
        Log.Info($"[皮肤] 预览插槽 {_previews.Count}/{RaySkinCatalog.All.Count} 个已就绪（三套常驻，切换只切显隐）");
    }

    private void BuildArrows()
    {
        _arrowLeft = MakeArrow(pointLeft: true, () => Step(-1));
        _arrowRight = MakeArrow(pointLeft: false, () => Step(1));
    }

    /// <summary>
    /// 用<b>游戏自带的箭头贴图</b>做按钮（<c>Button.Icon</c> + <c>ExpandIcon</c>，一个节点搞定点击和显示）。
    /// 贴图拿不到就退化成自绘三角 + 同一个按钮 —— 宁可丑，也不能没有按钮。
    /// </summary>
    private Button MakeArrow(bool pointLeft, Action onPressed)
    {
        Texture2D? texture = ResourceLoader.Load<Texture2D>(
            pointLeft ? ArrowLeftTexturePath : ArrowRightTexturePath);

        var button = new Button
        {
            Name = pointLeft ? "ArrowLeft" : "ArrowRight",
            Flat = true,                       // 去掉按钮自带的底板，只留图标
            Icon = texture,
            ExpandIcon = true,                 // 图标撑满按钮
            Size = new Vector2(ArrowSize, ArrowSize),
            MouseDefaultCursorShape = CursorShape.PointingHand,

            // ⚠️ 必须关掉焦点：`Flat` 按钮点下去会画一个**白色焦点框**（用户报的"点击时有白框"）。
            //    上一个版本清空了所有 stylebox 所以没这问题，这版用了 Icon 才冒出来。
            FocusMode = FocusModeEnum.None,

            // ⚠️ 必须抬高 z_index：SpineSprite 是 Node2D，会自己管绘制顺序/内部 z，
            //    只靠"后 AddChild" 并不能保证按钮画在模型上面 —— 用户报过一次"另一个按钮看不到"。
            //    显式给个比模型（z_index=0）大的值，就永远压在模型之上。
            ZIndex = OverlayZIndex,
        };

        if (texture == null)
        {
            Log.Warn($"[皮肤] 箭头贴图加载失败（{(pointLeft ? ArrowLeftTexturePath : ArrowRightTexturePath)}），改用自绘三角");
            button.AddChild(MakeFallbackTriangle(pointLeft));
        }

        // 悬停提亮（游戏自带的 NGoldArrowButton 是悬停放大图标，这里用提亮，效果接近又不涉及 pivot 计算）。
        button.MouseEntered += () =>
        {
            button.Modulate = new Color(1.2f, 1.2f, 1.2f);
            PlayUiSfx(FmodSfx.uiHover);
        };
        button.MouseExited += () => button.Modulate = Colors.White;

        // ★ 2026-10-09：**按钮音**（用户反馈"点下去没有声音"）。
        //   用**游戏自带**的 UI 音效事件 —— 它走游戏的 FMOD，所以音量/静音跟着玩家的音频设置走，
        //   不用我们自己再管一路音量。（我们自己的 SfxPlayer 是给战斗音效用的，不适合 UI。）
        button.Pressed += () =>
        {
            PlayUiSfx(FmodSfx.uiClick);
            onPressed();
        };
        AddChild(button);
        return button;
    }

    /// <summary>
    /// 放一个<b>游戏自带</b>的 UI 音效。<b>全程吞异常</b> —— 声音出问题绝不能影响能不能点/能不能开局。
    /// </summary>
    private static void PlayUiSfx(string eventPath)
    {
        try
        {
            SfxCmd.Play(eventPath);
        }
        catch (Exception e)
        {
            Log.Warn($"[皮肤] UI 音效播放失败（不影响按钮）：{e.Message}");
        }
    }

    /// <summary>兜底三角。<c>Polygon2D</c> 而不是文字箭头 —— 字形缺字就什么都看不见，多边形一定画得出来。</summary>
    private static Node2D MakeFallbackTriangle(bool pointLeft)
    {
        Vector2[] points = pointLeft
            ?
            [
                new Vector2(FallbackHalfW, -FallbackHalfH),
                new Vector2(FallbackHalfW, FallbackHalfH),
                new Vector2(-FallbackHalfW, 0f),
            ]
            :
            [
                new Vector2(-FallbackHalfW, -FallbackHalfH),
                new Vector2(-FallbackHalfW, FallbackHalfH),
                new Vector2(FallbackHalfW, 0f),
            ];

        // Button 是 Control，子节点按它的局部坐标画（左上角为原点），所以这里先挪到按钮中心。
        var holder = new Node2D { Position = new Vector2(ArrowSize / 2f, ArrowSize / 2f) };
        holder.AddChild(new Polygon2D { Color = ArrowFallbackColor, Polygon = points });
        holder.AddChild(new Line2D
        {
            Points = points,
            Closed = true,
            Width = 5f,
            DefaultColor = ArrowFallbackOutline,
            JointMode = Line2D.LineJointMode.Round,
            BeginCapMode = Line2D.LineCapMode.Round,
            EndCapMode = Line2D.LineCapMode.Round,
        });

        return holder;
    }

    private void BuildLabel()
    {
        _nameLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Size = new Vector2(420f, 44f),
            ZIndex = OverlayZIndex,   // 同上：压在模型之上
        };

        _nameLabel.AddThemeFontSizeOverride("font_size", 30);
        _nameLabel.AddThemeColorOverride("font_color", Colors.White);
        _nameLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _nameLabel.AddThemeConstantOverride("outline_size", 8);   // 压在插画上也读得清

        AddChild(_nameLabel);
    }

    // ───────────────────────────── 摆位 ─────────────────────────────

    /// <summary>按父控件尺寸摆所有控件（<c>_Ready</c> 与 <c>Resized</c> 时都调）。</summary>
    private void Layout()
    {
        Vector2 size = Size;

        if (size.X <= 1f || size.Y <= 1f)
        {
            return;   // 还没排版，等下一次 Resized
        }

        float centerX = size.X * PreviewCenterXFrac;
        float arrowY = size.Y * ArrowYFrac;

        // 三套都摆到同一个锚点（切换时位置不动，只有显隐变化 → 完全不会跳）。
        foreach (Node2D slot in _previews)
        {
            // 插槽是**普通 Node2D**，所以这里可以用强类型属性（不像 SpineSprite 那样会踩类型映射的坑）。
            // 锚点：骨架原点在脚底，而脚底就在身体正下方 → 原点直接对到目标中心
            // （不要用包围盒中心，被武器撑宽过，见 VisualCenterOffsetUnits 的注释）。
            slot.Position = new Vector2(
                centerX - VisualCenterOffsetUnits * PreviewScale,
                size.Y * PreviewGroundYFrac);
            slot.Scale = Vector2.One * PreviewScale;
            slot.ZIndex = 0;
        }

        float offsetX = size.X * ArrowOffsetXFrac;
        _arrowLeft?.SetPosition(new Vector2(centerX - offsetX - ArrowSize / 2f, arrowY - ArrowSize / 2f));
        _arrowRight?.SetPosition(new Vector2(centerX + offsetX - ArrowSize / 2f, arrowY - ArrowSize / 2f));

        if (_nameLabel != null)
        {
            _nameLabel.SetPosition(new Vector2(centerX - _nameLabel.Size.X / 2f, size.Y * LabelYFrac));
            _nameLabel.Text = RaySkinCatalog.DisplayName(RaySkinCatalog.All[_index]);
        }
    }

    // ───────────────────────────── 交互 ─────────────────────────────

    /// <summary>左右切换（循环）。</summary>
    private void Step(int delta)
    {
        try
        {
            int count = RaySkinCatalog.All.Count;

            if (count == 0)
            {
                return;
            }

            _index = ((_index + delta) % count + count) % count;

            RaySkin selected = RaySkinCatalog.All[_index];
            RaySkinState.Selected = selected;
            RaySkinState.Save();

            // ★ 只切显隐：三套骨架早就建好了，所以这里是瞬时的（不会有重建骨架的卡顿）。
            ShowOnly(_index);
            Layout();

            Log.Info($"[皮肤] 浏览器切到 {RaySkinCatalog.DisplayName(selected)}（下一场战斗生效）");
        }
        catch (Exception e)
        {
            Log.Warn($"[皮肤] 切换失败：{e.Message}");
        }
    }

    /// <summary>只显示第 <paramref name="index"/> 套预览，其余藏起来（这就是"切换"的全部实现）。</summary>
    private void ShowOnly(int index)
    {
        for (int i = 0; i < _previews.Count; i++)
        {
            _previews[i].Visible = i == index;
        }
    }

    /// <summary>
    /// 骨架的待机播放<b>不在这里</b> —— 交给挂在每个 SpineSprite 上的
    /// <c>res://RayModCode/UI/SkinPreviewSprite.gd</c>（理由见 <see cref="_previews"/> 的注释：
    /// C# 拿不到 SpineSprite 的托管包装，所以驱动骨架这件事必须由 GDScript 做）。
    /// </summary>
    private static int IndexOf(RaySkin skin)
    {
        for (int i = 0; i < RaySkinCatalog.All.Count; i++)
        {
            if (RaySkinCatalog.All[i] == skin)
            {
                return i;
            }
        }

        return 0;
    }
}
