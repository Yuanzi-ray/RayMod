using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Logging;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Art;

/// <summary>
/// ★★ <b>「得见光芒」的场上特效（游戏内实现）</b>—— 2026-10-12 用户定稿的编排：
///
/// <list type="number">
///   <item><b>莱伊附近落下一道光柱</b>（用户 2026-10-12 追加）+ 光点从不同方向朝她收束（由暗到亮、走弧线）；</item>
///   <item>光点在她身上聚成<b>一粒小光点</b>，停一拍；</item>
///   <item><b>伤害结算的那一瞬</b>：一道亮线从她这里打到敌人身上，光点直接在<b>敌人身上炸开</b>
///         （硬核闪光 → 柔和泛光 → 放射尖芒 → 四散余烬 + 光尘 → 涡旋余韵）；</item>
///   <item>敌人脚下两道<b>冲击波</b>（快亮粗 + 慢淡细，压得很扁、不是正圆）。</item>
/// </list>
///
/// <para><b>为什么拆成"出牌起手 / 伤害引爆"两拍</b>（用户："伤害应该在特效炸开的瞬间结算"）：
/// 若整套都挂在伤害钩子上，炸开会比结算晚 1 秒多，所以收束提前到出牌那一拍。</para>
///
/// <para>⚠️⚠️ <b>为什么收束锚在莱伊、炸开锚在敌人</b>（2026-10-12 实机踩到）：
/// 出牌那一刻拿到的 <c>CardPlay.Target</c> 在 <c>GetCreatureNode()</c> 上**返回 null**
/// （日志：<c>[场上特效·定位诊断] Creature.GetCreatureNode() 返回 null</c> →
/// <c>WorldPos()</c> 给回 <c>(0,0)</c> → **整套特效蹲到画布左上角**）。
/// 而"自己"（<c>play.Player.Creature</c>）任何时刻都查得到 ⇒ 收束锚她；
/// 敌人坐标**等伤害钩子**（那里传进来的 target 是准的，上一版就是这么对的）⇒ 炸开锚敌人。</para>
///
/// <para><b>★ 随机旋转</b>：每次播放给整棵特效一个随机 <c>Rotation</c>（光点 + 炸开都跟着转）。</para>
/// </summary>
internal partial class CardFieldVfx
{
    /// <summary>总开关（出问题就置 false）。</summary>
    internal const bool SeeTheLightEnabled = true;

    /// <summary>预览像素 → 世界单位（预览画布 2559px 宽 ≈ 战场约 1500 世界单位）。</summary>
    private const float WorldPerPreviewPx = 0.586f;

    // ★★ 2026-10-12 ★ **贴图必须和设计稿一致**：预览脚本（make_vfx_gif.py）用的是
    //    DOT = `star_05`（295×294，实心圆）+ HALO = `circle_04`（405×405，软边圆，画 2.1× 大）。
    //    而这一版之前用的是 `ray_sk_08_star_dot`（四角星芒）而且**只有一层、没有光晕**
    //    ⇒ 每颗粒子又小又暗（用户："光点有点暗看不到了"）。现在照设计稿：**软光晕 + 硬核两层**。
    private const string SeeMoteTexture = Entry.ResPath + "/images/vfx/fx_star_05.png";
    private const string SeeMoteHaloTexture = Entry.ResPath + "/images/vfx/fx_circle_04.png";
    private const string SeeFlareTexture = Entry.ResPath + "/images/vfx/fx_flare_01.png";
    private const string SeeLightTexture = Entry.ResPath + "/images/vfx/fx_light_02.png";
    private const string SeeStar8Texture = Entry.ResPath + "/images/vfx/fx_star_08.png";
    private const string SeeStar6Texture = Entry.ResPath + "/images/vfx/fx_star_06.png";
    private const string SeeTwirlTexture = Entry.ResPath + "/images/vfx/fx_twirl_01.png";
    private const string SeeSparkTexture = Entry.ResPath + "/images/vfx/fx_spark_01.png";

    // ── 时间轴（秒）──
    private const float SeeGatherEnd = 0.72f;      // 收束结束（光点全吸进中心那一点）
    private const float SeeHoldEnd = 0.98f;        // 停留段的呼吸周期
    private const float SeeBurstTotal = 0.46f;     // 炸开这一段
    private const float SeeGatherMaxWait = 2.6f;   // ★ 兜底（正常用不到）
    private const float SeeHoldShort = 0.30f;      // 收束完之后那"很短的一拍"（用户 2026-10-12：**再等久一点**，0.14 → 0.30）
    private const float SeeRush = 1.6f;            // 收到"伤害已结算"时把收束轻微加速（不再死等）

    /// <summary>
    /// ★★ <b>预览一整轮的总时长（秒）</b>：0.72 收束 + 0.26 停 + 0.46 炸开 = 1.44
    /// （<c>make_vfx_gif.py</c>：24 帧 × 60ms）。
    ///
    /// <para>⚠️ <b>粒子的 <c>t_start</c> / <c>t_arrive</c> 是"相对这 1.44s"的比例</b>
    /// （设计稿里是 <c>.00~.16</c> / <c>.36~.50</c>）—— 所以必须乘这个总数，
    /// <b>不能乘 <see cref="SeeGatherEnd"/></b>。旧代码乘的是 SeeGatherEnd ⇒ 光点在收束
    /// 走到一半（0.36s）就全部消失、剩下的收束时间里屏幕上什么都没有 ——
    /// 2026-10-12 对着设计稿逐帧比对时抓出来的（设计稿到第 12 帧/0.72s 才全部吸进中心）。</para>
    /// </summary>
    private const float SeePreviewTotal = 1.44f;

    /// <summary>
    /// ★★ <b>打伤害之前要等的时间</b>（秒）= 收束 + 停一拍 − 一点点。
    ///
    /// <para>用户 2026-10-12 定的：<b>"伤害应该在特效炸开的瞬间结算"</b>。所以不是"让特效等伤害"，
    /// 而是反过来 —— <b>让伤害等特效收束完</b>：卡自己的 <c>OnPlay</c> 在 <c>DamageCmd</c> 之前
    /// <c>await</c> <see cref="SeeLightHitWait"/> 秒。</para>
    ///
    /// <para>⚠️ 比时间轴<b>少 <see cref="SeeHitLead"/> 秒</b>：伤害钩子必须比 <c>BeginBurst()</c>
    /// <b>早几帧</b>把敌人坐标送到 —— 同一帧里"先炸、后收到坐标"就会炸在容器原点（画布左上角）。</para>
    /// </summary>
    internal const float SeeHitLead = 0.06f;

    /// <summary>伤害是否等特效（置 false ⇒ 立刻结算，观感回到"看完收束才炸"）。</summary>
    internal const bool SeeHitWaitsForEffect = true;

    /// <summary>
    /// ★★ <b>黑幕的最终不透明度</b> —— "除了特效以外全部变暗（全体变暗、特效反而更亮）"。
    ///
    /// <para>2026-10-12 迭代：0.55（太浅）→ 0.70（用户："周围还是不够暗"）→ **1.00 全黑**（用户："直接尝试全黑"）。</para>
    ///
    /// <para>⚠️ <b>1.0 的含义</b>：整块战斗场景（莱伊、敌人、地形、游戏自己的 VFX）会**完全消失**，
    /// 画面上只剩我们的特效 + UI（手牌/血条在别的 CanvasLayer，不受影响）。
    /// 想要"很暗但还看得见人和怪"就回到 0.85。调参只改这一个数。</para>
    /// </summary>
    internal const float SeeDimAlpha = 1.00f;

    /// <summary>黑幕淡入 / 淡出的时长（秒）—— 突然压黑会有"跳帧感"，两端各留一小段。</summary>
    private const float SeeDimIn = 0.18f;
    private const float SeeDimOut = 0.30f;

    /// <summary>头顶高度 = 生物 <c>Hitbox</c> 高度 × 这个系数（0.45 ≈ 头部；见 <see cref="HeadUpOf"/>）。</summary>
    internal const float SeeHeadFrac = 0.45f;

    /// <summary>读不到 <c>Hitbox</c> 时的兜底头顶高度（世界单位）。</summary>
    internal const float SeeHeadUpFallback = 130f;

    /// <summary>
    /// ★ <b>早到的"引爆坐标"先存这里</b>（伤害钩子比出牌钩子先到的情形）。
    /// 旧版就是在这种情形下<b>静默丢弃</b>坐标 → 炸开拿到 (0,0) → 整套炸在容器原点（画布左上角）。
    /// </summary>
    private static Vector2 _pendingEnemy;
    private static bool _hasPendingEnemy;

    /// <summary>
    /// <b>「得见光芒」打伤害前要等多久</b>（秒，0 = 不等）—— 由卡自己的 <c>OnPlay</c> 在
    /// <c>DamageCmd</c> 之前 <c>await</c>。从"这套特效已经收束了多久"倒推，所以出牌钩子
    /// 晚到一点也不会等过头（时序若仍有偏差，改的是这个函数，不是卡）。
    /// </summary>
    internal static float SeeLightHitWait()
    {
        if (!Enabled || !SeeTheLightEnabled || !SeeHitWaitsForEffect || TestModeIsOn())
        {
            return 0f;
        }

        float target = SeeGatherEnd + SeeHoldShort - SeeHitLead;

        if (_activeCustom != null && GodotObject.IsInstanceValid(_activeCustom))
        {
            // 上一套已经在炸 / 已经演完 → 不拖住伤害
            return _activeCustom.Gathering ? Mathf.Max(0f, target - _activeCustom.Elapsed) : 0f;
        }

        return target;      // 起手节点还没建（理论上不会）：按整段等
    }

    /// <summary>
    /// ★★ <b>在 VFX 容器【最底下】垫一层全屏黑幕</b> —— "周围变暗、只有特效亮着"。
    ///
    /// <para>原理：黑幕与特效是**同一个容器里的兄弟**，黑幕被排到 index 0（最先画 = 最底下），
    /// 所以它只压暗**它下面的东西**（战斗场景、角色、游戏自己的 VFX）；
    /// 我们的特效是加色叠在它**上面**的，因此反倒更亮更跳。</para>
    ///
    /// <para>⚠️ 它**不碰 UI**（卡牌 / 血条 / 能量球在别的 CanvasLayer，保持可读）。</para>
    /// <para>⚠️ 黑幕**不是特效节点的子节点**（必须是兄弟才能排在它下面），所以回收要自己管：
    /// 见 <c>SeeLightVfx._ExitTree</c> —— 任何路径（含异常 QueueFree）都要把它收掉，
    /// 否则整块屏幕会永远暗着。</para>
    /// </summary>
    internal static ColorRect? CreateDim(Node container)
    {
        try
        {
            var dim = new ColorRect
            {
                Name = "SeeLightDim",
                Color = new Color(0f, 0f, 0f, 0f),
                MouseFilter = Control.MouseFilterEnum.Ignore,     // 不许挡住点击
            };

            container.AddChild(dim);
            dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);   // 铺满（keepOffsets=false ⇒ 连偏移一起设）
            container.MoveChild(dim, 0);                          // ★ 排到最前 = 画在所有人底下

            return dim;
        }
        catch (Exception e)
        {
            LogAlways($"[得见光芒] 黑幕创建失败：{e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// <b>莱伊"头顶"离胸口多高</b>（世界单位）—— 光柱的起点。
    ///
    /// <para>口径照抄游戏自己"往生物上方放东西"的写法（<c>NSpeechBubbleVfx.cs:364</c> 用
    /// <c>Hitbox.Size.Y × 0.5 × 0.75</c> 把气泡放到头顶之上），我们取
    /// <c>Hitbox.Size.Y × <see cref="SeeHeadFrac"/></c>（略低于那个值 = 正好落在头部）。
    /// 读不到 <c>Hitbox</c> 就退回 <see cref="SeeHeadUpFallback"/>。</para>
    /// </summary>
    private static float HeadUpOf(object? self)
    {
        try
        {
            object? node = CreatureNodeOf(self);
            object? hitbox = node?.GetType().GetProperty("Hitbox")?.GetValue(node);
            object? size = hitbox?.GetType().GetProperty("Size")?.GetValue(hitbox);

            if (size is Vector2 s && s.Y > 1f)
            {
                return s.Y * SeeHeadFrac;
            }
        }
        catch (Exception)
        {
            // 读不到就退回兜底值，不打扰战斗
        }

        return SeeHeadUpFallback;
    }

    /// <summary>
    /// <b>出牌那一刻起手收束</b>：在**莱伊**身上建一个正在收束的节点（含那道指向敌人的光柱）并记下来。
    /// 引爆由 <see cref="ExplodeActiveCustom"/> 在**真正造成伤害**那一刻触发，参数是敌人的世界坐标。
    /// </summary>
    /// <param name="target">本次出牌的目标生物（<c>CardPlay.Target</c>）—— 光柱要指向它。</param>
    internal static SeeLightVfx? SpawnSeeLightGather(object? self, object? target = null)
    {
        if (!Enabled || !SeeTheLightEnabled || self == null || TestModeIsOn())
        {
            return null;
        }

        // ★ 复用守卫：只有"还在收束、没开始炸"的那一个才复用（避免一次出牌建两层）；
        //   已在炸的节点说明是上一张牌的残留，直接不管它。
        if (_activeCustom != null && GodotObject.IsInstanceValid(_activeCustom) && _activeCustom.Gathering)
        {
            return _activeCustom;
        }

        try
        {
            Node? container = VfxParentForEffects();

            if (container == null)
            {
                LogAlways("[得见光芒] 拿不到 CombatVfxContainer，跳过");
                return null;
            }

            Texture2D? mote = ResourceLoader.Load<Texture2D>(SeeMoteTexture);
            Vector2 here = ChestPos(self);

            if (mote == null || here == Vector2.Zero)
            {
                LogAlways($"[得见光芒] 起手失败：贴图={mote != null} 自身世界坐标={here}");
                return null;
            }

            // ★ 光柱的朝向：**出牌那一刻**就取敌人坐标（原版 HandOfGreed.cs:53 证明这张房间注册表
            //   在 OnPlay 之前就查得到；详见 CreatureNodeOf 的注释）。拿不到就先竖直，等坐标补上。
            Vector2 foe = target == null ? Vector2.Zero : ChestPos(target);
            float headUp = HeadUpOf(self);

            var vfx = new SeeLightVfx();
            vfx.Configure(here, headUp, mote, foe);
            container.AddChild(vfx);
            vfx.AttachDim(CreateDim(container));      // ★ 黑幕：垫在容器最底下，只压暗场景（特效在它上面）

            // ★ 伤害钩子比出牌钩子先到（旧版就是这里把坐标丢了 → 炸在画布左上角）：起手时补上
            if (_hasPendingEnemy)
            {
                vfx.FeedEnemy(_pendingEnemy);
                _hasPendingEnemy = false;
                LogAlways($"[得见光芒] 起手补上早到的敌人坐标 {_pendingEnemy}");
            }

            // ★ 这一行就是"位置对不对"的判据：锚点 / 头顶高度 / 光柱指向 / 随机旋转 全在里面。
            LogAlways($"[得见光芒] 起手收束（锚在莱伊）：{SeeLightVfx.MoteCount} 颗光点 + 光柱" +
                      $"（世界坐标 {here}，头顶 +{headUp:F0}，" +
                      $"光柱指向 {(foe == Vector2.Zero ? "??（没拿到敌人坐标，先竖直朝上）" : foe.ToString())}" +
                      $"，随机旋转 {Mathf.RadToDeg(vfx.Spin):F0}°）");

            return vfx;
        }
        catch (Exception e)
        {
            LogAlways($"[得见光芒] 起手失败：{e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// <b>伤害结算那一瞬引爆</b>：把敌人坐标交给在途节点（它就是炸开的圆心）。
    ///
    /// <para>⚠️ 节点还没建时<b>绝不静默丢弃</b>：坐标存进 <see cref="_pendingEnemy"/>，起手时补上。
    /// 旧版就是在这里"悄悄 return" ⇒ 炸开只能拿到 (0,0) ⇒ 整套炸到容器原点（画布左上角）。</para>
    /// </summary>
    internal static void ExplodeActiveCustom(object? target)
    {
        try
        {
            Vector2 enemy = target == null ? Vector2.Zero : ChestPos(target);

            if (_activeCustom == null || !GodotObject.IsInstanceValid(_activeCustom))
            {
                if (enemy != Vector2.Zero)
                {
                    _pendingEnemy = enemy;
                    _hasPendingEnemy = true;
                    LogAlways($"[得见光芒] 引爆信号早到（起手节点还没建）→ 先存坐标 {enemy}");
                }
                else
                {
                    LogAlways("[得见光芒] 引爆信号到了，但既没有在途节点、也拿不到敌人坐标 → 本次不炸");
                }

                return;
            }

            if (enemy == Vector2.Zero)
            {
                LogAlways("[得见光芒] 引爆时拿不到敌人坐标 → 就地在莱伊身上炸");
            }

            _activeCustom.RequestExplode(enemy);
            LogAlways($"[得见光芒] 伤害已结算 → 打到 {enemy} 并引爆");
        }
        catch (Exception e)
        {
            LogAlways($"[得见光芒] 引爆失败：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>整套「得见光芒」。自己走完时间轴后 <c>QueueFree</c>。</summary>
    internal sealed partial class SeeLightVfx : Node2D
    {
        /// <summary>光点数量（与预览的 MOTE_N 一致）。</summary>
        internal const int MoteCount = 18;

        private const int EmberCount = 24;
        private const int DustCount = 14;
        private const float RingBaseRadius = 260f;

        private sealed class Mote
        {
            public Sprite2D Sprite = null!;     // 硬核（star_05：实心圆）
            public Sprite2D Halo = null!;       // 柔光晕（circle_04：画 2.1× 大 —— 设计稿里每颗粒子都有这一层）
            public float Ang0, R0, TStart, TArrive, Spiral, Squash, Size0, Gain, Amp, Ease;
        }

        private sealed class Bit
        {
            public Sprite2D Sprite = null!;
            public float Ang, D0, D1, S0, S1, Alpha;
        }

        private readonly List<Mote> _motes = new();
        private readonly List<Bit> _embers = new();
        private readonly List<Bit> _dust = new();
        private readonly List<(Line2D Ring, float Start, float R0, float R1, float W0, float W1, float Alpha, float Squash)> _rings = new();

        private Node2D _spinRoot = null!;      // 收束（跟着随机旋转）
        private Node2D _pillarRoot = null!;    // 光柱（**不跟随机旋转**：它有自己的朝向 —— 指向敌人）
        private Node2D _aimRoot = null!;       // ★ 光柱的朝向支座：坐在莱伊头上、转向敌人
        private Node2D _burstRoot = null!;     // 炸开（引爆时挪到敌人身上；也带随机旋转）
        private Node2D _groundRoot = null!;    // 冲击波（贴地）
        private Sprite2D _center = null!;
        private Sprite2D _centerHalo = null!;      // ★ 中心那粒的 2.1× 柔光晕
        private Sprite2D _centerBigHalo = null!;   // ★ 中心外面那圈更大的光晕（设计稿 lerp(260,620,kk)）
        private Sprite2D _flare = null!, _light = null!, _star8 = null!, _star6 = null!, _twirl = null!;
        private Line2D _pillar = null!;        // ★ 从莱伊头上指向敌人的光柱
        private Sprite2D _pillarGlow = null!;
        private Line2D? _streak;               // （已停用：那道光柱本身就是"打到敌人"的线，见 BeginBurst）

        private Vector2 _worldPos;
        private Vector2 _enemyPos;
        private float _t;
        private float _burstT;
        private bool _explode;
        private bool _bursting;
        private bool _hasEnemy;                     // ★ 引爆坐标是否已到手（false ⇒ 绝不拿 (0,0) 当圆心）
        private float _waitEnemy;                   // 到点之后为"等坐标"多等的时间
        private float _headUp = SeeHeadUpFallback;  // 莱伊"头顶"离胸口多高（光柱起点）
        private Vector2 _aimTarget;                 // 敌人坐标（光柱指向它）
        private bool _hasAim;                       // 是否已拿到敌人坐标

        /// <summary>到点后最多再等这么久；仍没有就落在莱伊身上（绝不用容器原点）。</summary>
        private const float EnemyGrace = 0.15f;

        /// <summary>还没拿到敌人坐标时光柱先用这个长度（竖直朝上；正常看不到）。</summary>
        private const float SeeBeamFallbackLen = 520f;

        /// <summary>还在收束/停留阶段（可被复用、可被引爆）。</summary>
        internal bool Gathering => !_bursting;

        /// <summary>本套已经走了多久（秒）—— 供 <c>SeeLightHitWait()</c> 倒推"伤害还要等多久"。</summary>
        internal float Elapsed => _t;

        /// <summary>本场播放的随机朝向（弧度）。</summary>
        internal float Spin { get; private set; }

        internal void Configure(Vector2 worldPos, float headUp, Texture2D moteTexture, Vector2 foePos)
        {
            _worldPos = worldPos;
            _headUp = headUp > 1f ? headUp : SeeHeadUpFallback;
            _aimTarget = foePos;
            _hasAim = foePos != Vector2.Zero;
            Spin = GD.Randf() * Mathf.Tau;

            var rng = new Random();

            // 四个支座：收束（转）/ 光柱（坐在莱伊头上、转向敌人）/ 炸开（转，挂到敌人身上）/ 冲击波（不转，贴地）
            _spinRoot = new Node2D { Name = "SpinRoot", Rotation = Spin };
            _burstRoot = new Node2D { Name = "BurstRoot", Rotation = Spin, Visible = false };
            _groundRoot = new Node2D { Name = "GroundRoot", Visible = false };
            _pillarRoot = new Node2D { Name = "PillarRoot" };          // 不转（世界朝向）
            _aimRoot = new Node2D { Name = "AimRoot" };                // ★ 只做"指向敌人"这一个角
            AddChild(_spinRoot);
            AddChild(_burstRoot);
            AddChild(_groundRoot);
            AddChild(_pillarRoot);
            _pillarRoot.AddChild(_aimRoot);

            // ── ★ 光柱（用户 2026-10-12 定稿）：**从莱伊头上指向敌人** ——
            //   一条沿 +X 的宽亮线（长度 = 到她头上到敌人的距离），由 _aimRoot 的 Position/Rotation 摆位；
            //   起点那团柔光就坐在她头上（所以它挂在 _aimRoot 的零点上）。
            _pillar = new Line2D
            {
                Points = new[] { Vector2.Zero, new Vector2(SeeBeamFallbackLen, 0f) },
                Width = 34f,
                DefaultColor = Colors.White,
                Modulate = Colors.Transparent,
                Material = AddMaterial(),
            };
            _aimRoot.AddChild(_pillar);

            _pillarGlow = MakeSprite(Load(SeeLightTexture), _aimRoot, hidden: true);
            _pillarGlow.Position = Vector2.Zero;

            UpdateAim();
            ReanchorGather();       // ★ 收束的落点 = 敌人（见 ReanchorGather 的注释）

            // ── 光点（固定档案：每颗都不一样，逐帧复用 ⇒ 轨迹不同也不抖）──
            //   ★ 设计稿里每颗粒子 = **柔光晕（circle_04，2.1×）+ 硬核（star_05）**两层；
            //     只画硬核会又小又暗（旧版就是这样）。数值全部照 make_vfx_gif.py 的 Particle。
            Texture2D? haloTexture = Load(SeeMoteHaloTexture);

            for (int i = 0; i < MoteCount; i++)
            {
                var sp = new Sprite2D
                {
                    Texture = moteTexture,
                    Position = new Vector2(9999f, 9999f),
                };
                sp.Material = AddMaterial();
                _spinRoot.AddChild(sp);

                Sprite2D halo = MakeSprite(haloTexture, _spinRoot, hidden: true);
                halo.Position = new Vector2(9999f, 9999f);

                _motes.Add(new Mote
                {
                    Sprite = sp,
                    Halo = halo,
                    Ang0 = Mathf.Tau * i / MoteCount + Range(rng, -0.22f, 0.22f),
                    R0 = Range(rng, 780f, 1150f) * WorldPerPreviewPx,
                    // ★ 这两个乘 SeePreviewTotal（整轮 1.44s），**不是** SeeGatherEnd —— 见该常量注释
                    TStart = Range(rng, 0f, 0.16f) * SeePreviewTotal,
                    TArrive = Range(rng, 0.36f, 0.50f) * SeePreviewTotal,
                    Spiral = Range(rng, -0.55f, 0.55f),
                    Squash = Range(rng, 0.42f, 0.66f),
                    Size0 = Range(rng, 78f, 126f) * WorldPerPreviewPx,   // ★ 设计稿的尺寸
                    Gain = Range(rng, 1.35f, 1.75f),                    // ★ 设计稿的 p.gain
                    Amp = Range(rng, 0.85f, 1.15f),                     // ★ 设计稿的 p.amp
                    Ease = 1.0f + rng.Next(3) * 0.5f,
                });
            }

            // ── 中心那粒（设计稿：硬核 + 2.1× 柔光晕 + 外圈一层更大的光晕）──
            _center = MakeSprite(moteTexture, _spinRoot);
            _center.Position = Vector2.Zero;
            _centerHalo = MakeSprite(haloTexture, _spinRoot);
            _centerHalo.Position = Vector2.Zero;
            _centerBigHalo = MakeSprite(haloTexture, _spinRoot);
            _centerBigHalo.Position = Vector2.Zero;

            // ── 炸开的元素（挂在 _burstRoot 下，引爆时整体挪到敌人身上）──
            _flare = MakeSprite(Load(SeeFlareTexture), _burstRoot, hidden: true);
            _light = MakeSprite(Load(SeeLightTexture), _burstRoot, hidden: true);
            _star8 = MakeSprite(Load(SeeStar8Texture), _burstRoot, hidden: true);
            _star6 = MakeSprite(Load(SeeStar6Texture), _burstRoot, hidden: true);
            _twirl = MakeSprite(Load(SeeTwirlTexture), _burstRoot, hidden: true);

            Texture2D? spark = Load(SeeSparkTexture);

            for (int i = 0; i < EmberCount; i++)
            {
                _embers.Add(new Bit
                {
                    Sprite = MakeSprite(spark, _burstRoot, hidden: true),
                    Ang = Range(rng, 0f, Mathf.Tau),
                    D0 = Range(rng, 60f, 150f) * WorldPerPreviewPx,
                    D1 = Range(rng, 430f, 1050f) * WorldPerPreviewPx,
                    S0 = Range(rng, 34f, 66f) * WorldPerPreviewPx,
                    S1 = Range(rng, 40f, 92f) * WorldPerPreviewPx,
                    Alpha = Range(rng, 0.55f, 1.0f),
                });
            }

            for (int i = 0; i < DustCount; i++)
            {
                _dust.Add(new Bit
                {
                    Sprite = MakeSprite(spark, _burstRoot, hidden: true),
                    Ang = Range(rng, 0f, Mathf.Tau),
                    D0 = Range(rng, 120f, 260f) * WorldPerPreviewPx,
                    D1 = Range(rng, 520f, 1150f) * WorldPerPreviewPx,
                    S0 = 20f * WorldPerPreviewPx,
                    S1 = 34f * WorldPerPreviewPx,
                    Alpha = Range(rng, 0.35f, 0.8f),
                });
            }

            // ★ 用户 2026-10-12：「冲击波要正圆」—— 原来两道环是"压扁的椭圆"（squash 0.26 / 0.32），
            //   现在一律正圆（squash = 1）。squash 仍留在元组里，只是没有一处再用非 1 的值。
            _rings.Add((MakeRing(1f), 0.04f, 150f, 1750f, 18f, 34f, 0.90f, 1f));
            _rings.Add((MakeRing(1f), 0.16f, 110f, 1100f, 9f, 20f, 0.50f, 1f));
        }

        /// <summary>
        /// ★ <b>收束的落点搬到敌人身上</b>（用户 2026-10-12："光点不应该往敌人身上聚集吗"）。
        ///
        /// <para>原来光点聚在**莱伊**身上、再靠光柱"打过去"，读起来是"能量先聚在自己身上再飞出去"；
        /// 现在整棵收束树（光点 + 中心光粒）挪到敌人身上 —— 能量**直接在目标处聚成一粒**，
        /// 停一拍之后就地炸开。节点锚点仍是莱伊（光柱要从她头上起算），所以这里是**相对位移**。</para>
        /// <para>拿不到敌人坐标时退回莱伊自己（<c>Vector2.Zero</c>）。</para>
        /// </summary>
        private void ReanchorGather()
        {
            if (_spinRoot != null && GodotObject.IsInstanceValid(_spinRoot))
            {
                _spinRoot.Position = _hasAim ? _aimTarget - _worldPos : Vector2.Zero;
            }
        }

        /// <summary>
        /// ★ <b>把光柱摆成"从莱伊头上指向敌人"</b>：支座坐在她头顶（<c>(0, -_headUp)</c>），
        /// 绕 +X 转到敌人方向，长度取"头顶到敌人"的距离。拿不到敌人坐标时竖直朝上（兜底长度）。
        /// <para>双方在回合制里都不动，所以只在**起手**与**坐标补到时**各算一次，不进逐帧。</para>
        /// </summary>
        private void UpdateAim()
        {
            if (_aimRoot == null || !GodotObject.IsInstanceValid(_aimRoot))
            {
                return;
            }

            _aimRoot.Position = new Vector2(0f, -_headUp);

            if (!_hasAim)
            {
                _aimRoot.Rotation = -Mathf.Pi / 2f;         // 竖直朝上（_pillar 沿 +X，-90° 才是朝上）
                SetBeamLength(SeeBeamFallbackLen);
                return;
            }

            Vector2 head = _worldPos + new Vector2(0f, -_headUp);
            Vector2 d = _aimTarget - head;

            _aimRoot.Rotation = d.Angle();
            SetBeamLength(Mathf.Max(d.Length(), 1f));
        }

        private void SetBeamLength(float len)
        {
            if (_pillar != null && GodotObject.IsInstanceValid(_pillar))
            {
                _pillar.Points = new[] { Vector2.Zero, new Vector2(len, 0f) };
            }
        }

        /// <summary>收到"引爆"指令（由命中钩子调用），参数是敌人的世界坐标。</summary>
        internal void RequestExplode(Vector2 enemyPos) => FeedEnemy(enemyPos);

        /// <summary>
        /// <b>记下"炸开的圆心"</b>（也是"坐标早到、起手时补上"那条路用的入口）。
        /// ⚠️ (0,0) 一律当"没拿到"—— 不设 flag，让 <c>_Process</c> 的去抖那一段兜底。
        /// </summary>
        internal void FeedEnemy(Vector2 enemyPos)
        {
            if (enemyPos == Vector2.Zero)
            {
                return;
            }

            _enemyPos = enemyPos;
            _hasEnemy = true;
            _explode = true;

            // ★ 光柱若还没瞄到敌人（起手时拿不到坐标），这一刻补上
            if (!_hasAim)
            {
                _aimTarget = enemyPos;
                _hasAim = true;
                ReanchorGather();       // 收束层这时才挪到敌人身上
                UpdateAim();
            }
        }

        public override void _Ready()
        {
            GlobalPosition = _worldPos;
            SetProcess(true);
        }

        // ───────────────────── 黑幕（"周围变暗、特效亮着"） ─────────────────────

        /// <summary>垫在 VFX 容器最底下的全屏黑幕（**兄弟节点**，所以要自己负责收）。</summary>
        private ColorRect? _dim;

        internal void AttachDim(ColorRect? dim) => _dim = dim;

        /// <summary>
        /// ★ <b>节点离开场景树时收掉黑幕</b> —— 之所以放在这里：黑幕是兄弟节点、不是子节点，
        /// 本节点被 <c>QueueFree</c>（正常播完 / 异常兜底）都不会带走它，
        /// 一旦漏收，整块屏幕会**永远保持变暗**。所以放在 <c>_ExitTree</c> 里（所有路径都会走到）。
        /// </summary>
        public override void _ExitTree()
        {
            if (_dim != null && GodotObject.IsInstanceValid(_dim))
            {
                _dim.QueueFree();
                _dim = null;
            }

            base._ExitTree();
        }

        /// <summary>黑幕不透明度：起手 <see cref="SeeDimIn"/> 秒淡入 → 全程保持 → 炸开末尾 <see cref="SeeDimOut"/> 秒淡出。</summary>
        private void UpdateDim()
        {
            if (_dim == null || !GodotObject.IsInstanceValid(_dim))
            {
                return;
            }

            float now = _bursting ? SeeGatherEnd + SeeHoldShort + _burstT : _t;
            float total = SeeGatherEnd + SeeHoldShort + SeeBurstTotal;

            float k = now < SeeDimIn
                ? now / SeeDimIn
                : now > total - SeeDimOut
                    ? Mathf.Max(0f, (total - now) / SeeDimOut)
                    : 1f;

            _dim.Color = new Color(0f, 0f, 0f, SeeDimAlpha * Mathf.Clamp(k, 0f, 1f));
        }

        public override void _Process(double delta)
        {
            try
            {
                UpdateDim();        // ★ 黑幕（收束段与炸开段都要跟）

                if (_bursting)
                {
                    _burstT += (float)delta;

                    if (_burstT >= SeeBurstTotal)
                    {
                        QueueFree();
                        return;
                    }

                    UpdateBurst(_burstT / SeeBurstTotal);
                    return;
                }

                // ★ 时间轴是**固定的**（不再死等伤害 —— 等就会晚，实机已经踩过）：
                //   收束 0.72s → 停很短一拍 → 炸开；
                //   期间若收到"伤害已结算"，只把收束轻微加速（SeeRush），让炸开更贴近结算那一帧。
                _t += (float)delta * (_explode ? SeeRush : 1f);

                if (_t >= SeeGatherEnd)
                {
                    float held = _t - SeeGatherEnd;

                    if (held >= SeeHoldShort || _t >= SeeGatherMaxWait)
                    {
                        // ★ 到点了但"炸开圆心"还没到手（伤害钩子在同一帧稍后到 / 还没结算）：
                        //   最多再等 EnemyGrace 秒；仍然没有就落在莱伊身上 ——
                        //   绝不用默认的 (0,0)，那会让整套炸到容器原点（画布左上角，踩过两次）。
                        if (!_hasEnemy)
                        {
                            _waitEnemy += (float)delta;

                            if (_waitEnemy < EnemyGrace)
                            {
                                UpdateHold(1f);
                                return;
                            }

                            LogAlways("[得见光芒] 到点仍没收到引爆坐标 → 就地在莱伊身上炸");
                            _enemyPos = _worldPos;
                            _hasEnemy = true;
                        }

                        BeginBurst();
                        return;
                    }

                    UpdateHold(Mathf.Clamp(held / SeeHoldShort, 0f, 1f));
                    return;
                }

                UpdateGather(_t / SeeGatherEnd);
            }
            catch (Exception)
            {
                QueueFree();                        // 特效出错绝不能拖累战斗
            }
        }

        /// <summary>切到炸开：收束层收掉、"打到敌人"的亮线拉出来、炸开层挪到敌人身上。</summary>
        private void BeginBurst()
        {
            _bursting = true;
            _burstT = 0f;

            _spinRoot.Visible = false;              // 收束层收掉（光点 + 中心光粒）
            // ★ 光柱**不收**：它现在就是从莱伊头上指向敌人的那条线，炸开这一段继续亮着、
            //   由 UpdateBurst 在前 1/3 里淡掉 —— 所以**不再另画** _streak（旧的细线，和它重复）。

            if (!_hasEnemy)                         // 兜底：任何路径都不许拿 (0,0) 当圆心
            {
                _enemyPos = _worldPos;
                _hasEnemy = true;
            }

            Vector2 offset = _enemyPos - GlobalPosition;
            _burstRoot.Position = offset;
            _burstRoot.Visible = true;
            _groundRoot.Position = offset + new Vector2(0f, 40f);
            _groundRoot.Visible = true;

            UpdateBurst(0f);
        }

        // ───────────────────────── 三个阶段 ─────────────────────────

        private void UpdateGather(float k)
        {
            float sum = 0f;

            foreach (var m in _motes)
            {
                if (_t < m.TStart)
                {
                    HideMote(m);
                    continue;
                }

                float u = (_t - m.TStart) / Mathf.Max(1e-3f, m.TArrive - m.TStart);

                if (u >= 1f)
                {
                    HideMote(m);
                    sum += 1f;
                    continue;
                }

                float e = Mathf.Pow(SmoothStep(u), m.Ease);
                float r = m.R0 * (1f - e);
                float ang = m.Ang0 + m.Spiral * e;
                Vector2 motePos = new(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r * m.Squash);

                m.Sprite.Visible = true;
                m.Sprite.Position = motePos;

                // ★ 数值全部照设计稿（make_vfx_gif.py 的 得见光芒()）：由暗到亮、越近越被吸小。
                float size = Mathf.Lerp(m.Size0, 16f * WorldPerPreviewPx, Mathf.Pow(e, 1.4f));
                float bright = Mathf.Pow(e, 1.15f);
                float alpha = (0.16f + 0.84f * bright) * m.Amp * SmoothStep(Mathf.Min(1f, u / 0.18f));

                m.Sprite.Scale = Vector2.One * (size / Mathf.Max(1f, m.Sprite.Texture.GetWidth()));
                m.Sprite.Modulate = Tint(m.Gain * Mathf.Lerp(0.55f, 1.30f, bright), alpha);

                // 柔光晕：2.1× 大小、FAINT 色（≈白×0.85）、gain 0.80、alpha 0.30× —— 设计稿的 glow()
                m.Halo.Visible = true;
                m.Halo.Position = motePos;
                m.Halo.Scale = Vector2.One * (size * 2.1f / Mathf.Max(1f, m.Halo.Texture.GetWidth()));
                m.Halo.Modulate = Tint(0.68f, 0.30f * alpha);

                sum += e * e;
            }

            float kk = SmoothStep(sum / MoteCount);

            // ★ 光柱：随收束一起出现并变亮（"光从天上落到她身边"那一笔）
            float pillar = SmoothStep(Mathf.Min(1f, k * 1.6f));
            _pillar.Modulate = Tint(1.15f, 0.10f + 0.55f * pillar);
            _pillar.Width = Mathf.Lerp(14f, 40f, pillar);
            _pillarGlow.Visible = true;
            _pillarGlow.Scale = Vector2.One * (Mathf.Lerp(120f, 320f, pillar) * WorldPerPreviewPx
                                               / Mathf.Max(1f, _pillarGlow.Texture?.GetWidth() ?? 1));
            _pillarGlow.Modulate = Tint(1.05f, 0.20f + 0.35f * pillar);

            // ★ 中心那一粒（设计稿）：硬核 lerp(64,168,kk) gain 1.85 + 2.1× 柔光晕 + 外圈大光晕 lerp(260,620,kk)
            float centerSize = Mathf.Lerp(64f, 168f, kk);
            float centerAlpha = 0.50f + 0.50f * kk;

            _center.Visible = true;
            _center.Scale = Vector2.One * (centerSize * WorldPerPreviewPx
                                           / Mathf.Max(1f, _center.Texture.GetWidth()));
            _center.Modulate = Tint(1.85f, centerAlpha);

            SetGlowSprite(_centerHalo, centerSize * 2.1f, 0.68f, 0.30f * centerAlpha);
            SetGlowSprite(_centerBigHalo, Mathf.Lerp(260f, 620f, kk), 1.15f, 0.32f * kk);
        }

        private void UpdateHold(float h)
        {
            foreach (var m in _motes)
            {
                HideMote(m);
            }

            // ★ 光柱（从莱伊头上指向敌人的那道）：停留这一段**继续亮着**，只压暗一点 ——
            //   它现在要一直演到炸开，不能像旧版那样在"等伤害"时只剩 0.16 的余晖（等于消失）。
            _pillar.Modulate = Tint(1.20f, 0.55f);
            _pillarGlow.Modulate = Tint(1.10f, 0.45f);

            // ★ 设计稿的停留段：只剩中心一粒（硬核 + 2.1× 柔光晕 + 外圈大光晕 360×breath），极轻微呼吸
            float breath = 1f + 0.10f * Mathf.Sin(h * Mathf.Tau);

            _center.Visible = true;
            _center.Scale = Vector2.One * (132f * breath * WorldPerPreviewPx
                                           / Mathf.Max(1f, _center.Texture.GetWidth()));
            _center.Modulate = Tint(1.80f, 1f);

            SetGlowSprite(_centerHalo, 132f * breath * 2.1f, 0.68f, 0.30f);
            SetGlowSprite(_centerBigHalo, 360f * breath, 1.05f, 0.22f);
        }

        private void UpdateBurst(float e)
        {
            float burst = SmoothStep(Mathf.Min(1f, e / 0.30f));
            float fade = Mathf.Pow(1f - e, 0.9f);

            // ★ 光柱（从莱伊头上指向敌人的那道）：前 1/3 淡掉 —— 它就是"打过去"那条线
            float beamFade = Mathf.Max(0f, 1f - e / 0.33f);

            if (_pillar != null && GodotObject.IsInstanceValid(_pillar))
            {
                _pillar.Modulate = Tint(1.80f, 0.95f * beamFade);
            }

            if (_pillarGlow != null && GodotObject.IsInstanceValid(_pillarGlow))
            {
                _pillarGlow.Modulate = Tint(1.40f, 0.55f * beamFade);
            }

            if (_streak != null && GodotObject.IsInstanceValid(_streak))
            {
                _streak.Modulate = Tint(1.6f, 0.95f * beamFade);   // 已停用（不再创建），留着不碍事
            }

            Show(_flare, 170f, 580f, burst, 1.80f, 0.98f * Mathf.Max(0f, 1f - e / 0.34f));
            Show(_light, 520f, 1500f, burst, 1.10f, 0.42f * fade);
            // ★★ 2026-10-12 **真 bug**：这三个角度是**度**（设计稿 im.rotate(18*e)），
            //   而 Godot 的 Node2D.Rotation 单位是**弧度** —— 原来直接把 18f * e 塞进去，
            //   等于转 1031°（star6 是 −1490°、twirl 是 2406°）⇒ 尖芒在 0.46s 里疯转 3~6.7 圈，
            //   看着就是"粒子在抽搐/乱转"，而不是设计稿那种"慢慢偏 18°"。必须 DegToRad。
            Show(_star8, 300f, 1300f, burst, 1.40f, 0.85f * fade, rot: Mathf.DegToRad(18f) * e);
            Show(_star6, 210f, 950f, burst, 1.25f, 0.52f * fade, rot: Mathf.DegToRad(-26f) * e);
            Show(_twirl, 320f, 1150f, burst, 1.00f, 0.24f * fade, rot: Mathf.DegToRad(42f) * e);

            UpdateBits(_embers, e, 0.55f, 1.45f, 1.2f);
            UpdateBits(_dust, e, 0.90f, 1.05f, 1.0f);

            foreach (var (ring, start, r0, r1, w0, w1, alpha, squash) in _rings)
            {
                float q = (e - start) / Mathf.Max(1e-3f, 1f - start);

                if (q <= 0f)
                {
                    ring.Visible = false;
                    continue;
                }

                q = Mathf.Min(1f, q);
                float p = 1f - Mathf.Pow(1f - q, 2.2f);

                ring.Visible = true;
                ring.Scale = Vector2.One * (Mathf.Lerp(r0, r1, p) * WorldPerPreviewPx
                                            / Mathf.Max(1f, RingBaseRadius));
                ring.Width = (Mathf.Lerp(w0, w1, p) * WorldPerPreviewPx) / Mathf.Max(0.01f, ring.Scale.X);
                ring.Modulate = Tint(1.25f, alpha * Mathf.Pow(1f - q, 1.3f));
                _ = squash;
            }
        }

        // ───────────────────────── 小工具 ─────────────────────────

        private static float SmoothStep(float x)
        {
            x = Mathf.Clamp(x, 0f, 1f);
            return x * x * (3f - 2f * x);
        }

        /// <summary>藏掉一粒光点的两层（硬核 + 柔光晕）—— 两处都要收，漏一层会留一个孤零零的光晕。</summary>
        private static void HideMote(Mote m)
        {
            m.Sprite.Visible = false;
            m.Halo.Visible = false;
        }

        /// <summary>摆一层"柔光"贴图（中心粒的晕、外圈晕）：<paramref name="previewSize"/> 是**预览像素**尺寸。</summary>
        private static void SetGlowSprite(Sprite2D sp, float previewSize, float gain, float alpha)
        {
            sp.Visible = alpha > 0.01f;
            sp.Scale = Vector2.One * (previewSize * WorldPerPreviewPx
                                      / Mathf.Max(1f, sp.Texture?.GetWidth() ?? 1));
            sp.Modulate = Tint(gain, alpha);
        }

        private static float Range(Random rng, float a, float b) => a + (float)rng.NextDouble() * (b - a);

        private static Color Tint(float gain, float alpha)
        {
            Color c = SentinelMode ? SentinelColor : Colors.White;

            // ★★ 2026-10-12 **真 bug（用户报"光点暗、炸开的粒子不对"的根因之一）**：
            //   这里原来把每个通道 Min(1f, …) **夹在 1.0** —— 而基色是**纯白**，
            //   于是**所有 gain ≥ 1 的调用全部等于白点、一个都没生效**：
            //   预览里 gain=1.45 的余烬、1.85 的中心光粒、1.80 的硬核闪光，
            //   在游戏里统统是 1.0 ⇒ 整段特效比设计稿暗 30%~45%（加色混合下就是"发灰、看不见"）。
            //   现在**不夹**（Color 允许 >1）：着色器里乘完再乘贴图，写进 LDR 帧缓冲时才被截断，
            //   这正是"加色发光"该有的行为，和预览脚本 colorize() 的 `lum*color*gain`（clip 到 255）一致。
            return new Color(c.R * gain, c.G * gain, c.B * gain, Mathf.Clamp(alpha, 0f, 1f));
        }

        private static CanvasItemMaterial AddMaterial() => new()
        {
            BlendMode = SentinelMode ? CanvasItemMaterial.BlendModeEnum.Mix : CanvasItemMaterial.BlendModeEnum.Add,
        };

        private static Sprite2D MakeSprite(Texture2D? tex, Node parent, bool hidden = false)
        {
            var sp = new Sprite2D
            {
                Texture = tex,
                Visible = !hidden,
                Modulate = Colors.Transparent,
                Material = AddMaterial(),
            };
            parent.AddChild(sp);
            return sp;
        }

        private Line2D MakeRing(float squash)
        {
            var pts = new Vector2[72];

            for (int i = 0; i < pts.Length; i++)
            {
                float a = Mathf.Tau * i / pts.Length;
                pts[i] = new Vector2(Mathf.Cos(a) * RingBaseRadius, Mathf.Sin(a) * RingBaseRadius * squash);
            }

            var ring = new Line2D
            {
                Points = pts,
                Closed = true,
                Width = 4f,
                DefaultColor = Colors.White,
                Visible = false,
                Modulate = Colors.Transparent,
                Material = AddMaterial(),
            };
            _groundRoot.AddChild(ring);
            return ring;
        }

        private static void Show(Sprite2D sprite, float from, float to, float t, float gain, float alpha,
                                 float rot = 0f)
        {
            sprite.Visible = alpha > 0.01f;
            sprite.Scale = Vector2.One * (Mathf.Lerp(from, to, t) * WorldPerPreviewPx
                                          / Mathf.Max(1f, sprite.Texture?.GetWidth() ?? 1));
            sprite.Rotation = rot;
            sprite.Modulate = Tint(gain, alpha);
        }

        private static void UpdateBits(List<Bit> bits, float e, float span, float gain, float fadePow)
        {
            float q = SmoothStep(Mathf.Min(1f, e / span));

            foreach (var b in bits)
            {
                float d = Mathf.Lerp(b.D0, b.D1, q);
                b.Sprite.Visible = true;
                b.Sprite.Position = new Vector2(Mathf.Cos(b.Ang) * d, Mathf.Sin(b.Ang) * d * 0.62f);
                b.Sprite.Scale = Vector2.One * (Mathf.Lerp(b.S0, b.S1, q)
                                                / Mathf.Max(1f, b.Sprite.Texture?.GetWidth() ?? 1));
                b.Sprite.Modulate = Tint(gain, b.Alpha * Mathf.Pow(1f - e, fadePow));
            }
        }

        private static Texture2D? Load(string path) => ResourceLoader.Load<Texture2D>(path);
    }
}
