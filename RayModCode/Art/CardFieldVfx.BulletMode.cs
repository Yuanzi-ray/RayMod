using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using RayMod.Ammo;
using RayMod.Powers;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Art;

/// <summary>
/// ★★ <b>「子弹模式」的常驻弹幕</b>（2026-10-12 用户设计稿）：
///
/// <list type="number">
///   <item>打出「子弹模式」后，<b>子弹悬浮在莱伊身边</b>（每颗有自己的方位/远近/高低/大小，
///         各自的<span>随机朝向</span>还会缓慢摆动）；</item>
///   <item>每当你出<b>消耗弹药的牌</b>时，<b>随机几颗</b>子弹沿弧线飞向那个敌人，命中一记小爆闪；</item>
///   <item><b>打掉不补</b>：弹药扣了就少几颗 —— 弹幕数量 = <c>min(当前弹药, 15)</c>，
///         弹药涨回来才补弹（用户 2026-10-12 拍板的 A 方案："弹幕本身就是弹药的可视化"）。</item>
/// </list>
///
/// <para><b>发几颗 / 打谁</b>：发出去的子弹数 = 这次<b>实际消耗的弹药数</b>；
/// 打群的牌（<c>CardPlay.Target</c> 为空）会取 <c>ICombatState.Enemies</c> 全体，
/// 子弹<b>轮流分配</b>（消耗 3 颗、2 个敌人 ⇒ 2+1）—— 总数永远等于弹药消耗，账对得上。</para>
///
/// <para><b>为什么用 <see cref="AmmoEvents"/> 而不是加补丁</b>：弹药的真实值存在跑团级
/// <c>AmmoState</c> 里、战斗内的 <c>AmmoPower</c> 只走 <c>SetAmount</c> 镜像
/// （因此原版的 <c>AfterPowerAmountChanged</c> <b>收不到</b>弹药变化，见 <c>AmmoEvents</c> 的注释）。
/// 那条总线正好就是"弹药变了"的唯一可靠信号 ⇒ 不改任何既有文件的核心逻辑。</para>
///
/// <para>⚠️ 生命周期：节点挂在 <c>CombatVfxContainer</c>（和"得见光芒"同一套坐标系），
/// <b>每 0.5s 自查一次"能力还在不在"</b>，能力掉了就淡出并自毁 —— 不需要 removal 钩子。</para>
/// </summary>
internal partial class CardFieldVfx
{
    /// <summary>总开关（出问题就置 false）。</summary>
    internal const bool BulletModeVfxEnabled = true;

    // ── ★★ 弹头形态（2026-10-18 深夜，用户："能不能更帅更优雅"）──
    //
    //   反思：v2→v4 我一直在**加元素**（描边/热尖/色散/火星/角括号/扫描弧/暗底/八芒），
    //   但"优雅"来自 **运动、留白、整齐**，不是元素清单 —— 用户 2026-10-12 就说过
    //   "新加的这些对观感影响不大"。所以这一版是**减**：
    //
    //     Arrow（光矢，默认）：细长**菱形**弹头（白，内含更亮的芯）+ 尖上一点白热
    //                          + 一条**贯穿航程 52% 的极细光尾**。命中只有一个小四芒 + 一圈细环。
    //     Blade（刃）：楔形**黑剪影** + 一道白刃线（倾斜 16°）；命中是交叉斩痕。
    //
    //   换形态只改这一行。
    private enum HeadStyle
    {
        Arrow,
        Blade,
    }

    /// <summary>悬浮上限（用户："也可以和弹药挂钩，但不超过15，不然太多了"）。</summary>
    private const int BulletMax = 15;

    private const string BulletTexturePath = Entry.ResPath + "/images/vfx/fx_bullet.png";

    /// <summary>拖尾贴图：包里现成的**横向光束条带**（沿飞行方向非等比拉伸即可当拖尾）。</summary>
    private const string BulletTrailPath = Entry.ResPath + "/images/vfx/ray_sk_06_trail.png";
    /// <summary>弹体光晕贴图。
    /// <para>★★ 2026-10-19：从 <c>fx_circle_04</c> 换成 <c>fx_glow_solid</c>。
    /// 前者是**一根细亮环**（铁律 75 实测：中心 alpha 0.000、峰值在 0.60R），
    /// 当光晕用会在弹体外**画出一个灰圈**（用户实机截图："挺奇怪的"里那圈灰弧就是它）。
    /// 换贴图时**系数不用动**：实测 <c>fx_glow_solid</c> 的总光量是它的 10.6 倍
    /// （eff 均值 0.287 vs 0.027），而 <see cref="DrawBody"/> 里那套 0.68 × 0.30 的乘子
    /// 给出的峰值加成正好 0.20 —— 柔光晕该有的量级。</para></summary>
    private const string BulletHaloPath = Entry.ResPath + "/images/vfx/fx_glow_solid.png";
    private const string BulletFlarePath = Entry.ResPath + "/images/vfx/fx_flare_01.png";
    private const string BulletSparkPath = Entry.ResPath + "/images/vfx/fx_spark_01.png";
    /// <summary>拖尾/残留轨迹用的**圆亮点**（比弹体虚影亮得多，才看得见"轨迹"）。</summary>
    private const string BulletDotPath = Entry.ResPath + "/images/vfx/fx_star_05.png";

    /// <summary>弹体贴图里**弹体本身**的高度（画布 256、弹体 223；见 <c>make_bullet_sprite.py</c>）。</summary>
    private const float BulletSpriteContent = 223f;

    // ── 尺寸/位置 ──
    //
    // ★★★ 2026-10-18 深夜：**这些数不再乘 `WorldPerPreviewPx`（0.586）**。
    //
    //    为什么这一改是决定性的：设计稿（预览 `make_vfx_gif.py`）是用**画布像素**画的，
    //    而游戏里的 `r.Length` 是**世界单位**；两者差 0.586 倍 ⇒ 我在预览里看着刚好的每一样东西，
    //    进游戏都只有 0.586 倍大：白描边 ≈1.2px、色散线 ≈2px、角括号 ≈1px……
    //    用户 2026-10-18："你所谓加了的元素我压根看不到什么区别" —— 就是这个 0.586。
    //    现在**设计稿像素 = 世界单位**，游戏渲染的东西和预览逐像素一致，预览从此可信。
    private const float BulletRingMin = 175f;   // 离她最近
    private const float BulletRingMax = 300f;   // 离她最远
    private const float BulletLenMin = 48f;     // 弹体最短（用户 2026-10-18："现在子弹太大了"）
    private const float BulletLenMax = 64f;     // 弹体最长
    private const float BulletBobMax = 16f;     // 起伏幅度

    /// <summary>
    /// ★★ 2026-10-12 用户实机反馈"会有这种子弹生成在角色身上" —— 修法：
    /// <list type="number">
    ///   <item><b>不再压扁</b>：原来 y 乘 0.55（从预览 GIF 照搬的"透视"），
    ///         等于把一圈弹**压进她的身体轮廓**；预览是黑底、没有角色，所以图上完全看不出来。</item>
    ///   <item><b>按轮廓避让</b>：每颗子弹的落点取"设计稿半径"与"<c>ClearanceAt</c> 算出的最小安全距离"
    ///         的较大值 —— 安全距离 = 她的轮廓椭圆边界 + 弹体半长 + 余量。</item>
    /// </list>
    /// 轮廓尺寸**从她的 <c>Hitbox</c> 实测**（读不到就退回保守下限），不是拍脑袋的常数。
    /// </summary>
    private const float BulletClearMargin = 18f;

    // ── 时间（秒）──

    /// <summary>
    /// 从她身边飞到敌人。<b>0.24 → 0.42</b>（2026-10-18 深夜，用户原话：
    /// <b>"要能看清子弹怎么飞过去"</b>）。
    ///
    /// <para>0.24s 只够 14 帧，眼睛跟不上一颗 40 单位的小东西 ⇒ 体感就是"闪一下、敌人掉血"，
    /// <b>完全看不出它飞过去的过程</b>。这才是"和没有有啥区别"的真正原因（不是粗细，是时间）。
    /// 0.42s 大约是"看得清轨迹、又不拖沓"的下限。</para>
    /// </summary>
    private const float BulletFlyTime = 0.20f;

    /// <summary>连发错开。<b>0.06 → 0.035</b>（2026-10-18 深夜）：改成**齐射** ——
    /// 同一张牌消耗的几颗几乎同时出膛，画面上同时看得到 3~5 道弹道，
    /// 而不是"一颗一颗排队飞"。用户："轨迹不帅" —— 单颗飞得再帅也不如一次齐射的构图。
    /// <para>⚠️ 别再压到 0.01 那种：那样几颗的**命中**会完全重叠成一个过曝白团，
    /// 而且狙击镜会在同一个点叠三份。0.035 是"看起来同时、实际错开 2 帧"的甜区。</para></summary>
    private const float BulletStagger = 0.035f;

    /// <summary>狙击镜的最小间隔（毫秒）：齐射时 N 颗同时命中，**只给第一颗**挂镜子。</summary>
    private const double BulletScopeCooldownMs = 200;

    private const float BulletImpactTime = 0.30f;   // 命中爆闪持续
    private const float BulletFadeIn = 0.22f;       // 补弹淡入
    private const float BulletFadeOut = 0.25f;      // 收场淡出

    // ── ★ "就地轻爆"（无目标牌消耗弹药时）的专用量表（2026-10-12 第二轮：原来几乎看不见）──
    /// <summary>爆闪大小倍率（1 = 命中那一记）。**0.42 → 0.80**：太小的话在战斗画面里基本看不见。</summary>
    private const float BulletPopScale = 0.80f;

    /// <summary>火星粒数（原来 2 粒太小太稀）。</summary>
    private const int BulletPopSparks = 4;

    /// <summary>连爆错开（原来 0.05）。</summary>
    private const float BulletPopStagger = 0.06f;

    /// <summary>自己一条命（比命中爆闪的 0.30 长一点，才读得出"炸开"）。</summary>
    private const float BulletPopTime = 0.45f;

    /// <summary>轻爆光环的基准尺寸（世界单位）= 弹长上限 ⇒ 环从 ~19 扩到 ~172 世界单位。</summary>
    private const float BulletPopWorld = BulletLenMax;

    // ── ★★ 命中「小狙击镜」（2026-10-18 重做：照**自己的**视觉语言，不用 P3Makoto 那套数值）──

    /// <summary>狙击镜外环半径（世界单位）。★ 设计稿像素 = 世界单位（见常量区的 0.586 说明），
    /// 所以这里的 76 就是预览里那个 76：约等于一个弹体长度，压得住画面又不像魔法阵。</summary>
    private const float BulletScopeRadius = 76f;

    /// <summary>飞行时弹体的拉伸（激光弹头是一颗**亮点**：轻微沿弹轴拉长 + 收窄）。</summary>
    private const float BulletSpeedStretch = 1.20f;
    private const float BulletSpeedThin = 0.92f;

    /// <summary>运动模糊拖影长度（UV 单位）。参照实现 <c>NativeIceLance</c> 用的是 12 次采样沿运动方向。</summary>
    private const float BulletSmearLen = 0.16f;

    // ── ★★ 弹体"白热核心 + 四步闪动"（2026-10-19，学 RegentFX）──
    //    依据：`调研-贴图效果与质感-20261019.md` §2.2 / §2.6
    //    · 他们的弹体是 4 帧 flipbook，但**实测帧间差只有 2.5~3.5/255（约 1%）** ⇒
    //      目的不是"动"，是"别像贴纸"。所以我们不新建贴图，直接用着色器做等效的四步闪动。
    //    · 他们的 LUT 最后一档是 **8.0**（use_hdr）⇒ 亮部继续往上叠，我们原来 gain 封顶 1.x。

    /// <summary>白热强度：亮部额外叠多少（<c>blend_add</c> ⇒ &gt;1 真的会累加变亮）。
    /// <para>★ 0.35 是**量出来的**，不是拍的：<c>_tools\preview_bullet_core.py</c> 扫了三档，
    /// 判据是「闪动帧间差落在 RegentFX 实测的 2.5~3.5/255」且「新过曝面积最小」——
    /// 因为 <c>blend_add</c> 下峰值超过 1 会被裁成同一个白，真正的可见后果是
    /// <b>亮部面积变大 = 子弹看着变粗</b>，而用户最恨"子弹太大"。
    /// 三档实测（弹长 56）：hot 0.90/阈值 0.55 ⇒ 峰 2.32、过曝 17.5%；
    /// 0.55/0.70 ⇒ 1.96、15.5%；<b>0.35/0.80 ⇒ 1.75、13.0%</b>。</para></summary>
    private const float BulletHot = 0.30f;

    /// <summary>从多亮开始算"亮部"（0~1，低于此值不动）。★ 0.90 = <b>只烧弹尖</b>。
    /// <para>为什么必须这么高：实测这张贴图的**银弹头 luma 均值 0.745、铜弹壳 0.665** ——
    /// 两者几乎一样亮，区别只在色度（银=低饱和 / 铜=高饱和）。所以任何"整体调亮"都会
    /// 让弹壳和弹尖一起白掉；想只让弹尖发光，只能把阈值压到 0.9 以上。</para></summary>
    private const float BulletHotThresh = 0.90f;

    /// <summary>四步闪动的幅度。<b>0.03 是对齐实测的</b>：RegentFX 4 帧的帧间差只有 2.5~3.5/255（约 1%），
    /// 第一版写 0.12 量出来 **16.6/255（我的 5 倍）** ⇒ 会明显"闪烁"。</summary>
    private const float BulletShimmer = 0.03f;

    /// <summary>闪动步进频率（对齐 RegentFX 的 <c>flipbook_speed = 15</c>）。</summary>
    private const float BulletFlipRate = 15f;

    // ── ★★★ 伪 3D 环轨（2026-10-19 用户拍板：**切线朝向 / 单圈 / 后半段被挡住**）──
    //    为什么改这条：飞行弹体连否 14 版，用户原话"子弹大概是修不好了"。
    //    环轨不依赖贴图质感 —— 3D 感完全来自**椭圆投影 + 前后明暗/大小差 + 遮挡**这三条。
    //    预览与参数对照见 `_tools\preview_bullet_orbit.py`（近/远 尺寸比 1.86、亮度比 1.82）。

    /// <summary>环半径（世界单位）。★ 必须 &gt; 她的半高 196，否则环会从她身上穿过去。</summary>
    private const float OrbitRadius = 240f;

    /// <summary>
    /// 半径的**摆动幅度**（世界单位）与**摆动速度**（弧度/秒）—— 用户 2026-10-19："半径也可以一定区间摆动"。
    ///
    /// <para>即 <c>半径 = OrbitRadius + OrbitRadiusSwing × sin(ωt)</c>：
    /// ±26（≈ ±11%）是"环在一呼一吸"的量级；再大就会读成"环在散开/收拢"。</para>
    ///
    /// <para>⚠️ **三个摆动必须用互不相同的周期**，否则会同步成一种机械的规律感：
    /// 公转 1.10（5.7s）· 倾角 0.32（19.6s）· 半径 0.47（13.4s）。</para>
    ///
    /// <para>⚠️ 半径最大会到 240+26 = <b>266</b>，横向跨度 = 532 —— 屏幕左侧已经有 HP 条，
    /// 如果发现有弹从 HUD 底下穿过去，把 <see cref="OrbitRadiusSwing"/> 调小或把基准半径调小。</para>
    /// </summary>
    private const float OrbitRadiusSwing = 26f;
    private const float OrbitRadiusSwingRate = 0.47f;

    /// <summary>椭圆压扁比（0.34 ⇒ 俯视角；越大越"正视"，3D 纵深感越弱）。</summary>
    private const float OrbitSquash = 0.34f;

    /// <summary>整环倾角（度）—— 和头顶狙击镜同一个倾斜语言（-12°）。</summary>
    private const float OrbitTiltDeg = -12f;

    /// <summary>
    /// 倾角的**摆动幅度**（度）与**摆动速度**（弧度/秒）—— 用户 2026-10-19："倾斜角可以在一定区间内摆动"。
    ///
    /// <para>整环倾角 = <see cref="OrbitTiltDeg"/> + <see cref="OrbitTiltSwingDeg"/> × sin(ωt)。
    /// 9° 的幅度是"看得出在晃、但读不出抖"的量级；配合 0.32 rad/s ⇒ 约 20 秒一个来回，
    /// 和 5.7 秒一圈的公转不同周期 ⇒ 两者不会同步成一种规律感（同步了会显得机械）。</para>
    /// </summary>
    private const float OrbitTiltSwingDeg = 9f;
    private const float OrbitTiltSwingRate = 0.32f;

    /// <summary>近处元素放大比例（+30%）。和 <see cref="OrbitBrightMin"/> 一起构成"伪 3D"的两条线索。</summary>
    private const float OrbitNearBoost = 0.30f;

    /// <summary>最远处 / 最近处的明暗（只有 alpha 变，贴图颜色不动）。</summary>
    private const float OrbitBrightMin = 0.55f;
    private const float OrbitBrightMax = 1.00f;

    /// <summary>环的角速度（弧度/秒）—— 1.10 ⇒ 约 5.7 秒转一圈。</summary>
    private const float OrbitSpin = 1.10f;

    /// <summary>
    /// 命中爆闪的缩放（原来 = 1.0）。★ <b>1.0 那档是给「得见光芒」用的</b> ——
    /// <see cref="ImpactFx"/> 的尺寸是 <c>Lerp(170, 400) × WorldPerPreviewPx</c> ≈ **100~234 世界单位**，
    /// 而狙击镜只有 49 ⇒ **爆闪比镜子大 2~4 倍，把镜子整个盖住**，这才是"我也没看到所谓狙击镜"的真因。
    /// 单发子弹的命中只要一记**锐利的小爆**，主戏让给白芯 + 细环 + 火星。
    /// </summary>
    private const float BulletHitFlashScale = 0.60f;

    /// <summary>狙击镜整段寿命（秒）。</summary>
    private const float BulletScopeTime = 0.55f;

    /// <summary>外环的椭圆压扁比（0.42 ⇒ 一枚斜着的取景椭圆，而不是正圆靶心）。</summary>
    private const float BulletScopeSquash = 0.42f;

    /// <summary>四段弧在 0/90/180/270° 各断开的半角度数（总缺口 = 2 × 这个值）。</summary>
    private const float BulletScopeGapDeg = 7f;

    /// <summary>环的倾斜（度）—— 卡图那圈"干净斜椭圆环"就是斜的，正圆会读成靶心。</summary>
    private const float BulletScopeTiltDeg = -12f;

    /// <summary>头顶高度 = 生物 <c>Hitbox</c> 高 × 这个系数（0.45 ≈ 头部；和「得见光芒」同一口径）。</summary>
    private const float BulletHeadFrac = 0.45f;

    /// <summary>读不到 <c>Hitbox</c> 时的兜底头顶高度（世界单位）。</summary>
    private const float BulletHeadFallback = 90f;

    /// <summary>
    /// 一次出牌记下的"打谁"：<see cref="Chest"/> = 胸口（子弹飞到这里、爆闪也在这里），
    /// <see cref="HeadUp"/> = 胸口往上多高是头顶（**小狙击镜挂在那里**）。
    /// <para>为什么要带头顶偏移：子弹只拿得到胸口坐标，狙击镜挂在胸口就"插进身体里"，
    /// 读不出"被瞄准"的意思。高度从敌人的 <c>Hitbox</c> 实测（<see cref="HeadUpOf"/>）。</para>
    /// </summary>
    internal readonly struct BulletTarget
    {
        public readonly Vector2 Chest;
        public readonly float HeadUp;

        public BulletTarget(Vector2 chest, float headUp)
        {
            Chest = chest;
            HeadUp = headUp;
        }

        /// <summary>日志里要能一眼看出爆点坐标（<c>string.Join</c> 会用它）。</summary>
        public override string ToString() => $"({Chest.X:F0},{Chest.Y:F0})";
    }

    private static readonly Logger BulletLog = new("RayMod.CardVfx", LogType.Generic);

    private static BulletSwarm? _bulletSwarm;

    /// <summary>上一次出牌要打的目标（胸口 + 头顶高度）—— 弹药扣掉时照它分配。空 = 没目标（就地轻爆）。</summary>
    private static BulletTarget[] _bulletTargets = Array.Empty<BulletTarget>();

    /// <summary>上一次出牌的 <c>TargetType</c> 名字（只用于日志：一眼看出"为什么没飞"）。</summary>
    private static string _bulletTargetKind = "?";

    /// <summary>上一次出牌的卡类名（只用于日志：能直接看出"记的是哪张牌的目标"）。</summary>
    private static string _bulletCardName = "?";

    /// <summary>
    /// ★★ <b>"现在有哪一次出牌正在结算"</b> —— 由 <c>BulletModePower.BeforeCardPlayed</c> 置真、
    /// <c>AfterCardPlayed</c> 置假。
    ///
    /// <para>为什么要这个标志：弹药可能在<b>完全不是出牌</b>的时候被扣（遗物 / 能力 / 回合钩子里的
    /// <c>AmmoCmd.ConsumeAmmo</c>，例如「精制子弹」那类力量、以及 <c>Cannon</c> 之外的各种扣法）。
    /// 那时 <c>_bulletTargets</c> 里躺的是<b>上一张牌</b>的敌人 —— 拿来用就是"乱飞"。
    /// <b>没有出牌在结算 ⇒ 一律当"没目标"（就地轻爆）</b>，这才是用户要的语义。</para>
    /// </summary>
    private static bool _bulletPlayActive;

    /// <summary>
    /// 出牌那一刻记下"这次要打谁"，并在能力在身时保证弹幕已挂上。
    /// <para>⚠️ 由 <see cref="Play"/> 在 <b><c>SelectProfile</c> 的早退之前</b>调用 ——
    /// 因为"子弹模式"的弹幕和"这张卡有没有自己的场上特效"无关（弹药牌大多没有配置文件）。</para>
    /// </summary>
    internal static void BulletModeOnCardPlay(object card, object play, object? combatState)
    {
        try
        {
            if (!Enabled || !BulletModeVfxEnabled || TestModeIsOn())
            {
                return;
            }

            // ★ 兜底再记一次（主记点在 BulletModePower.BeforeCardPlayed，见 NoteBulletTargetsForPlay）：
            //   这一次出牌的 CardPlay 与目标都一样，重复记不会产生第二份真相。
            BeginBulletPlay(card, play, combatState);

            if (GetProperty(play, "Player") is Player owner && owner.Creature.GetPower<BulletModePower>() != null)
            {
                AttachBulletSwarm(owner);
            }
        }
        catch (Exception e)
        {
            LogOnce($"[子弹模式·特效] 出牌处理失败（只记一次）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// ★★★ <b>在"弹药被扣的那一刻"记下这次出牌的目标</b> —— 由
    /// <c>BulletModePower.BeforeCardPlayed</c> 在 <c>ConsumeAmmo</c> <b>之前</b>调用。
    ///
    /// <para><b>为什么必须在这里记（2026-10-12 真 bug，用户："无目标地牌效果还是不对"）</b>：
    /// 原来只在 <see cref="Play"/>（= <c>CombatHistory.CardPlayStarted</c>）里记，而反编译
    /// <c>CardModel.cs</c> 的 <c>PlayCard</c> 循环里 <b><c>BeforeCardPlayed</c>(:1926) 早于
    /// <c>CardPlayStarted</c>(:1929) 三行</b> —— 于是"这张牌的目标"永远慢一拍、用的都是
    /// <b>上一张牌</b>的 <c>TargetType</c>。同一局日志实证：</para>
    /// <code>
    /// playing card RAY_MOD_CARD_RAY_MOD_STRIKE (targeting 噬尸蛞蝓) → ConsumeAmmo now 3
    ///   → ⇒ 就地轻爆 1 颗（TargetType=Self）        ← ❌ 这张牌是"打击"，用的是上一张「子弹模式」的 Self
    /// playing card RAY_MOD_CARD_RAY_MOD_DEFEND (no target)      → ConsumeAmmo now 1
    ///   → ⇒ 发射 1 颗（TargetType=AnyEnemy）        ← ❌ 这张牌是"防御"，用的是上一张"打击"的 AnyEnemy ⇒ 飞向敌人
    /// </code>
    /// <para>修法 = <b>把"记目标"挪进同一个 <c>BeforeCardPlayed</c></b>（与扣弹药天然同序，
    /// 不再依赖跨钩子的先后 —— 见铁律 71）。</para>
    /// </summary>
    internal static void NoteBulletTargetsForPlay(object play)
    {
        try
        {
            if (!Enabled || !BulletModeVfxEnabled || TestModeIsOn())
            {
                return;
            }

            object? card = GetProperty(play, "Card");

            if (card == null)
            {
                return;
            }

            // ★★ 联机门控（2026-10-13）：这条路径**不经过** `CardFieldVfx.Play` 的那道门
            //    （它由 `BulletModePower.BeforeCardPlayed` 直接调用），所以必须在这里单独判一次。
            //    不判的话，队友出牌会把我 `_bulletTargets`（全局静态）改成**他的目标**
            //    ⇒ 下一次**我**消耗弹药时，子弹飞向他那张牌的目标。
            if (!IsMyCard(card))
            {
                return;
            }

            // 打群要枚举全体敌人 ⇒ 把战斗状态一起带上（从卡上读，见 CardModel.CombatState）。
            BeginBulletPlay(card, play, GetProperty(card, "CombatState"));
        }
        catch (Exception e)
        {
            // ★ 读失败一律当"没目标"—— 绝不能沿用上一张牌的目标（那正是这个 bug 的表现）
            _bulletTargets = Array.Empty<BulletTarget>();
            LogOnce($"[子弹模式·特效] 记目标（出牌前）失败（只记一次）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>记下"这次出牌打谁" + 标记"有出牌正在结算"（见 <see cref="_bulletPlayActive"/>）。</summary>
    private static void BeginBulletPlay(object card, object play, object? combatState)
    {
        NoteBulletTargets(card, play, combatState);
        _bulletPlayActive = true;
    }

    /// <summary>
    /// 出牌结算结束（<c>BulletModePower.AfterCardPlayed</c>）：这一次的目标作废。
    /// <para>⚠️ 之后的弹药消耗（不是出牌引起的）就走"没目标 ⇒ 就地轻爆"，
    /// 不会拿着已经打完的那张牌的敌人乱飞。</para>
    /// </summary>
    internal static void EndBulletPlay()
    {
        _bulletPlayActive = false;
        _bulletTargets = Array.Empty<BulletTarget>();
        _bulletTargetKind = "?";
        _bulletCardName = "?";
    }

    /// <summary>
    /// 记下这次出牌的目标。判据用**卡的 <c>TargetType</c>**（不是"Target 是不是 null"）：
    ///
    /// <para>⚠️ 2026-10-12 真 bug：原来 <c>Target == null</c> 一律当"打群" ⇒ **无目标的牌
    /// （<c>Self</c> / <c>None</c> / 增益牌…）也会把子弹撒向全体敌人**（用户："注意，现在是会飞出去的"）。
    /// 游戏枚举：<c>None / Self / AnyEnemy / AllEnemies / RandomEnemy / AnyPlayer / AnyAlly / AllAllies /
    /// TargetedNoCreature / Osty</c> —— 只有<b>打敌人的那三种</b>才有目标，其余一律"没目标"。</para>
    /// </summary>
    private static void NoteBulletTargets(object card, object play, object? combatState)
    {
        try
        {
            string kind = GetProperty(card, "TargetType")?.ToString() ?? "?";
            _bulletTargetKind = kind;
            _bulletCardName = card.GetType().Name;

            // 只有这三种是"打敌人"；其余（含 Self / None / AnyPlayer / AllAllies / TargetedNoCreature / Osty）
            // 都算没目标 ⇒ 弹药被消耗时"原地轻轻炸开"
            bool hitsEnemies = kind is "AnyEnemy" or "AllEnemies" or "RandomEnemy";
            var list = new List<BulletTarget>();

            if (hitsEnemies)
            {
                object? target = GetProperty(play, "Target");

                if (target != null)
                {
                    Vector2 p = ChestPos(target);

                    if (p != Vector2.Zero)
                    {
                        list.Add(new BulletTarget(p, HeadUpOf(target)));
                    }
                }
                else if (kind is "AllEnemies" or "RandomEnemy"
                         && combatState != null
                         && GetProperty(combatState, "Enemies") is System.Collections.IEnumerable enemies)
                {
                    foreach (object? enemy in enemies)
                    {
                        if (enemy == null)
                        {
                            continue;
                        }

                        Vector2 p = ChestPos(enemy);

                        if (p != Vector2.Zero)
                        {
                            list.Add(new BulletTarget(p, HeadUpOf(enemy)));
                        }

                        if (list.Count >= 6)
                        {
                            break;
                        }
                    }
                }
            }

            _bulletTargets = list.ToArray();
        }
        catch (Exception e)
        {
            // ★ 失败当"没目标"：宁可就地轻爆，也不能沿用上一张牌的目标（见 NoteBulletTargetsForPlay）
            _bulletTargets = Array.Empty<BulletTarget>();
            LogOnce($"[子弹模式·特效] 记目标失败（只记一次）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>能力被施加时调用（见 <c>BulletModePower.AfterApplied</c>）：挂上弹幕 + 订阅弹药变化。</summary>
    internal static void AttachBulletSwarm(Player? owner)
    {
        if (!Enabled || !BulletModeVfxEnabled || owner == null || TestModeIsOn())
        {
            return;
        }

        // ★★ 联机门控（2026-10-13）：**弹幕只给本地玩家自己挂**。
        //    `_bulletSwarm` 是静态单例，而且 `BulletSwarm` 是锚在**出牌者的生物节点**上的
        //    （`ChestPos(_owner.Creature)`）⇒ 队友的弹幕挂到我这边就是"绕着队友飞、但我只有一份实例"。
        //    两人都有「子弹模式」时更明显：后挂的会把先挂的顶掉（`Reconfigure` 就地改主人），
        //    而 `_bulletTargets` / `_bulletPlayActive` 也是全局的 ⇒ 谁的牌打谁全乱。
        //    单机下 `IsMe` 恒真 ⇒ 行为零变化。
        if (!IsLocalPlayer(owner))
        {
            return;
        }

        try
        {
            // ★★ 每次都要注册：`AmmoEvents` 内部按**委托去重**（同一个静态方法只留一份），
            //    而它在"战斗 / 跑团结束"时会 `ClearAll()` 清空订阅 —— 若用静态标志"只注册一次"，
            //    就会在这里踩雷：**SL / 重开一局之后订阅被清掉、标志却还是 true**
            //    ⇒ 弹药事件再也进不来 ⇒ 子弹不飞（表现就是"第一次正常、SL 之后拖尾就不对了"，
            //    2026-10-12 用户实测）。
            AmmoEvents.Register(OnBulletModeAmmoChanged);

            if (_bulletSwarm != null && GodotObject.IsInstanceValid(_bulletSwarm))
            {
                // ★ SL / 重开后旧的 Player 实例可能已经作废 ⇒ 重新认人 + 重量轮廓 + 数量对账
                _bulletSwarm.Reconfigure(owner);
                return;
            }

            Node? container = VfxParentForEffects();

            if (container == null)
            {
                BulletLog.Info("[子弹模式·特效] 拿不到 CombatVfxContainer，跳过");
                return;
            }

            var swarm = new BulletSwarm();
            container.AddChild(swarm);
            swarm.Configure(owner);
            _bulletSwarm = swarm;

            BulletLog.Info($"[子弹模式·特效] 弹幕已挂上（弹药 {AmmoCmd.GetAmmo(owner)}，上限 {BulletMax}，" +
                           $"目标 {( _bulletTargets.Length == 0 ? "无" : string.Join(" ", _bulletTargets))}）");
        }
        catch (Exception e)
        {
            LogOnce($"[子弹模式·特效] 挂载失败（只记一次）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>弹药变化（<c>AmmoEvents</c>）：减少 ⇒ 发射"消耗数"颗；增加 ⇒ 补弹。</summary>
    internal static void OnBulletModeAmmoChanged(Player player, int oldValue, int newValue)
    {
        try
        {
            if (!Enabled || !BulletModeVfxEnabled || TestModeIsOn())
            {
                return;
            }

            // ★★ 联机门控（2026-10-13）：**弹药事件也必须先判"这是不是我的弹药"**。
            //    这条总线是全局静态的（`AmmoEvents`），联机时队友每次消耗/装填弹药都会走到这里。
            //    不判的话有两个真实后果：
            //      ① 队友（也玩莱伊、但没挂「子弹模式」）弹药一变，就会执行下面那句
            //         `_bulletSwarm?.Retire()` ⇒ **我的弹幕被他拆掉**；
            //      ② 队友的消耗会在我屏幕上"发射子弹"（用的是我那份 `_bulletTargets`）。
            if (!IsLocalPlayer(player))
            {
                return;
            }

            if (player.Creature.GetPower<BulletModePower>() == null)
            {
                _bulletSwarm?.Retire();
                return;
            }

            AttachBulletSwarm(player);

            if (_bulletSwarm == null || !GodotObject.IsInstanceValid(_bulletSwarm))
            {
                return;
            }

            int spent = oldValue - newValue;

            if (spent > 0)
            {
                // ★★ 第二道保险（2026-10-12）：`_bulletTargets` 只有"某次出牌正在结算"时才代表
                //    "这次的目标"。不是出牌扣的弹药（遗物 / 能力 / 回合钩子）⇒ 那些目标根本不存在
                //    ⇒ 就地轻爆。（见 _bulletPlayActive 的注释）
                if (!_bulletPlayActive && _bulletTargets.Length > 0)
                {
                    _bulletTargets = Array.Empty<BulletTarget>();
                    _bulletTargetKind = "无出牌（非出牌消耗）";
                    _bulletCardName = "—";
                }

                // ★ Fire 先执行，它返回"爆在哪 / 飞去哪"的一行描述（轻爆看不到时先看这行的坐标）。
                string where = _bulletSwarm.Fire(spent, _bulletTargets);

                if (_bulletTargets.Length == 0)
                {
                    BulletLog.Info($"[子弹模式·特效] 消耗 {spent} 点弹药，本次没有目标 ⇒ 就地轻爆 {spent} 颗" +
                                   $"（{_bulletCardName} TargetType={_bulletTargetKind}，弹药 {oldValue} → {newValue}" +
                                   $"{(where.Length == 0 ? string.Empty : $"，爆点 {where}")}）");
                }
                else
                {
                    BulletLog.Info($"[子弹模式·特效] 消耗 {spent} 点弹药 ⇒ 发射 {spent} 颗，" +
                                   $"目标 {_bulletTargets.Length} 个（{_bulletCardName} TargetType={_bulletTargetKind}，" +
                                   $"弹药 {oldValue} → {newValue}）");
                }
            }

            // 两种情况都要对账：发射后（少了几颗）/ 涨回来（补弹）/ 没目标时（只扣不飞）
            _bulletSwarm.SyncCount();
        }
        catch (Exception e)
        {
            LogOnce($"[子弹模式·特效] 弹药事件失败（只记一次）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>常驻弹幕节点。自己走时间轴，能力掉了（自查发现）就淡出自毁。</summary>
    internal sealed partial class BulletSwarm : Node2D
    {
        private sealed class Round
        {
            public Node2D Root = null!;
            public Node2D Body = null!;         // ★ 程序化弹体（描边 + 弹壳 + 弹尖），不再是 Sprite2D
            public ShaderMaterial? BodyMat;     // ★ 2026-10-19：弹体材质（逐帧递 flipbook 的 frame）
            public Sprite2D Halo = null!;

            // ★★ 伪 3D 环轨的后层副本（2026-10-19）：后半段挂在 `BackCombatVfxContainer` 上，
            //    于是她会把它挡住。两套节点**同一套局部坐标**，按深度二选一显示。
            public Node2D RootBack = null!;
            public Node2D BodyBack = null!;
            public ShaderMaterial? BodyMatBack;
            public Sprite2D HaloBack = null!;

            /// <summary>
            /// ★★ 环的**连带反应**（2026-10-19）：相邻的弹被"带"得沿切向外推一下再弹回。
            ///
            /// <para>为什么这条最提气：它把环从"15 个各自转的贴图"变成**一个物理整体**
            /// （一枚弹被打出去，整圈会跟着颤一下）。本质是弹簧：<c>Kick</c> 偏移 + <c>KickVel</c> 速度，
            /// 每帧 <c>vel += (-k·offset - d·vel)·dt</c> —— 有阻尼才会"弹回"而不是"飘走"。</para>
            /// </summary>
            public float Kick;
            public float KickVel;

            /// <summary>环上的相位（弧度）。**当前**值 —— 会朝 <see cref="RingTarget"/> 平滑靠拢。</summary>
            public float RingPhase;
            /// <summary>环上的目标相位：由 <c>Reslot</c> 按"当前颗数"均分。发射后剩下的会自动补位。</summary>
            public float RingTarget;
            /// <summary>这一帧是否在前半段（决定用前层还是后层那套节点）。</summary>
            public bool FrontHalf = true;
            public readonly List<Sprite2D> Tail = new();

            // ── 固定档案（生成时抽一次，逐帧复用 ⇒ 不抖）──
            public float Jitter, Radius, Length, Phase, Bob, Gain, Aim, Spin, AimSpeed;
            public float Ang;               // 当前方位（会朝 Slot 均分出来的目标方位靠）
            public int Slot, Slots = 1;

            // ── 运行时 ──
            public float Alpha;
            public bool Retiring;
            public bool Popping;            // ★ "就地轻爆"：淡出时自己弹一下（只有无目标牌会置）
            public float PopScale = 1f;     // ★ 那一弹的当前倍率（1 = 原大小）
            public bool Flying;
            public bool Launched;           // 出膛火花只放一次
            public float FlyDelay, FlyT;
            public BulletTrail? Trail;      // ★ 连续缎带拖尾（用户："直接做拖尾得了"）
            public Vector2 From, To;        // ★ 直线飞行只需要起点/终点（Control 随 Bezier 一起删掉）
            public Vector2 Head;            // ★ 命中目标**头顶**（弹幕本地坐标）= 小狙击镜挂的地方
            public Node2D? Puff;            // ★ 飞行中的**粒子尾容器**（光尘 + 火星两个发射器）
            public bool Dead;
        }

        private Player _owner = null!;
        private readonly List<Round> _rounds = new();
        private Vector2 _silhouette;        // 她的半宽/半高（实测；0 = 读不到）
        private readonly Random _rng = new();
        private float _poll;
        private float _clock;

        /// <summary>环的累计转角（弧度）。★ 用它而不是 <c>_clock * OrbitSpin</c> —— 因为 <see cref="_spinBoost"/> 会让角速度临时变大，用乘法会跳变。</summary>
        private float _ringAngle;
        /// <summary>开火后的**转速加成**（1 → 0，0.22s）：整圈"甩"一下。</summary>
        private float _spinBoost;
        /// <summary>开火后的**整圈亮度脉冲**（1 → 0，0.16s）。</summary>
        private float _ringFlash;

        /// <summary>上一次全屏曝光脉冲的时间戳（ms）—— 一次齐射只推一次，别叠成频闪。</summary>
        private double _lastExposureMs;
        private double _lastScopeMs;
        private Texture2D? _bodyTex, _streakTex, _haloTex, _flareTex, _sparkTex, _dotTex;

        /// <summary>★ 环轨的<b>后层</b>（挂在 <c>BackCombatVfxContainer</c> 上 ⇒ 被她挡住）。null = 没有后层，退化成全在前层。</summary>
        private Node2D? _backLayer;

        internal void Configure(Player owner)
        {
            _bodyTex = Load(BulletTexturePath);
            _streakTex = Load(BulletTrailPath);
            _haloTex = Load(BulletHaloPath);

            // 铁律 57：着色器写错会被**静默丢弃**，唯一信号是 uniform 列表为空 ⇒ 启动时验一次
            FieldFx.ReportShaderHealth(FieldFxShaders.BulletSmear, "子弹运动模糊");
            FieldFx.ReportShaderHealth(FieldFxShaders.BulletCore, "子弹白热核心");
            FieldFx.ReportShaderHealth(FieldFxShaders.BulletStreak, "弹道光束");
            _flareTex = Load(BulletFlarePath);
            _sparkTex = Load(BulletSparkPath);
            _dotTex = Load(BulletDotPath);

            EnsureBackLayer();

            Reconfigure(owner);

            SetProcess(true);               // ★ 显式开启（别赌"重写了 _Process 就会自动开"）

            BulletLog.Info($"[子弹模式·特效] 弹幕就绪：{_rounds.Count} 颗（世界坐标 {GlobalPosition}，" +
                           $"她的轮廓 {( _silhouette == Vector2.Zero ? "没读到" : $"半宽 {_silhouette.X:F0} / 半高 {_silhouette.Y:F0}")}）");
        }

        /// <summary>
        /// ★★★ 建「环轨后层」（2026-10-19）。环的后半段改挂到 <c>room.BackCombatVfxContainer</c>
        /// （生物**后**面），于是她的精灵会把它挡住 —— **这是"绕着转"和"贴在她身上"的唯一区别**。
        ///
        /// <para><b>两个容器都是 Control，坐标原点/缩放不一定相同</b>：这里把后层的
        /// <c>GlobalPosition</c> 直接对齐到本节点（前层）的全局位置，并把两个容器的
        /// <c>GlobalTransform</c> 打进日志。**如果哪天前后两半错位，先看这行日志**：
        /// 只要两个 transform 不等，就得按比例补偿缩放。</para>
        ///
        /// <para>拿不到后容器 ⇒ 只是**失去遮挡**（全部退化到前层），不会不带特效。</para>
        /// </summary>
        private void EnsureBackLayer()
        {
            try
            {
                if (_backLayer != null && GodotObject.IsInstanceValid(_backLayer))
                {
                    return;
                }

                Node? back = BackVfxParentForEffects();

                if (back == null)
                {
                    BulletLog.Info("[子弹模式·环轨] 拿不到 BackCombatVfxContainer ⇒ 不做遮挡（前后都画在前层）");
                    return;
                }

                _backLayer = new Node2D { Name = "BulletOrbitBack" };
                back.AddChild(_backLayer);
                _backLayer.GlobalPosition = GlobalPosition;

                // ⚠️ Godot 里 `GlobalPosition` 只在 **Node2D** 和 **Control** 上各有一份，
                //    基类 CanvasItem 上没有 ⇒ 要对两种类型分别取（这个项目踩过太多次"类型映射骗人"）。
                static Vector2 OrgOf(Node? n) => n switch
                {
                    Node2D n2 => n2.GlobalPosition,
                    Control c => c.GlobalPosition,
                    _ => Vector2.Zero,
                };

                Vector2 orgFront = OrgOf(GetParent());
                Vector2 orgBack = OrgOf(back);
                bool same = orgFront.IsEqualApprox(orgBack);

                BulletLog.Info($"[子弹模式·环轨] 后层已挂：后容器 {back.Name}｜原点一致={same}" +
                               $"｜前原点 {orgFront}｜后原点 {orgBack}");
            }
            catch (Exception e)
            {
                LogOnce($"[子弹模式·环轨] 后层挂载失败（只记一次）：{e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// （重新）认人 + 重量轮廓 + 跟随 + 数量对账。
        /// <para>★ SL / 重开一局之后**必须重来一遍**：旧的 <c>Player</c> 实例可能已经作废，
        /// 拿它去读弹药/能力会失败（症状同样是"第二次就不对"）。</para>
        /// </summary>
        internal void Reconfigure(Player owner)
        {
            _owner = owner;
            Follow();
            MeasureSilhouette();
            SyncCount();
        }

        /// <summary>让弹幕跟着莱伊（她的 VfxSpawnPosition）。</summary>
        private void Follow()
        {
            try
            {
                Vector2 p = ChestPos(_owner.Creature);

                if (p != Vector2.Zero)
                {
                    GlobalPosition = p;

                    // ★ 环轨后层必须**跟着她一起动**（它挂在另一个容器里，不会自动继承本节点的位移）
                    if (_backLayer != null && GodotObject.IsInstanceValid(_backLayer))
                    {
                        _backLayer.GlobalPosition = p;
                    }
                }
            }
            catch (Exception)
            {
                // 读不到就沿用上一帧的位置，不打扰战斗
            }
        }

        /// <summary>弹幕数量对账：悬浮数 = <c>min(弹药, 15)</c>（飞行中的不算、退役中的不算）。</summary>
        internal void SyncCount()
        {
            int want = Mathf.Clamp(AmmoCmd.GetAmmo(_owner), 0, BulletMax);
            List<Round> idle = _rounds.Where(r => !r.Flying && !r.Retiring).ToList();

            for (int i = idle.Count; i < want; i++)
            {
                AddRound();
            }

            for (int i = want; i < idle.Count; i++)
            {
                idle[i].Retiring = true;
            }

            Reslot();
        }

        /// <summary>发射 <paramref name="count"/> 颗（= 这张牌实际消耗的弹药），轮流分配给各目标。
        /// <para>返回一行"跑到哪去了"的描述（轻爆时 = 各爆点的**世界坐标**），只用于日志。</para></summary>
        internal string Fire(int count, BulletTarget[] targets)
        {
            if (count <= 0)
            {
                return string.Empty;
            }

            // ★★ 2026-10-19 **真 bug**：`SyncCount()` 在 `Fire()` **之前**跑（见 OnBulletModeAmmoChanged
            //    → AttachBulletSwarm → Reconfigure → SyncCount）。弹药被花光时 `want = 0`，
            //    SyncCount 会把**剩下的每一颗**都标成 `Retiring`；紧接着 Fire 若按 `!r.Retiring` 过滤，
            //    就会**一颗都取不到** ⇒ 不飞、没有光束、没有出膛、没有贯穿。
            //    症状恰好是"**只有一颗子弹时击出特效会丢失**"（1 点弹药打 1 费牌 = 常态）。
            //    修法：这里只排除"已经在飞"的，被标 Retiring 的可以**被复活**再打出去。
            List<Round> idle = _rounds.Where(r => !r.Flying)
                                       .OrderBy(_ => _rng.Next())
                                       .Take(count)
                                       .ToList();

            foreach (Round pick in idle)
            {
                pick.Retiring = false;          // 复活：它本来是要被"排掉"的那颗，现在要被用掉
                pick.Popping = false;
                pick.Alpha = 1f;
            }

            // ★★ 没有目标（打自己的牌 / 场上没敌人 / 无目标牌）：**原地轻轻炸开**（用户 2026-10-12）。
            //
            // ⚠️ 2026-10-12 第二轮（用户："无目标轻爆几乎看不到"）：原来 scale 只给 **0.42**、
            //    爆闪 0.30s、2 粒火星 —— 在战斗画面里就是"子弹旁边闪了一下"，根本看不见。
            //    现在三件事一起上：**放大到 0.80** · **自己一条 0.45s 的命** · **再加一圈扩散光环**，
            //    并且让那颗子弹**自己"啪"地弹一下**（见 Update 的 Popping 分支）—— 眼睛才抓得住。
            if (targets.Length == 0)
            {
                var popAt = new List<string>();

                for (int i = 0; i < idle.Count; i++)
                {
                    Round r = idle[i];
                    r.Retiring = true;      // 就地淡出（"轻轻"）
                    r.Popping = true;       // ★ 只在这里置：让"这一颗"弹一下再消失
                    SpawnImpact(r.Root.Position, BulletPopScale, BulletPopSparks, i * BulletPopStagger,
                                BulletPopTime);
                    SpawnPopRing(r.Root.Position, i * BulletPopStagger);
                    popAt.Add(WorldOf(r.Root.Position));
                }

                Reslot();
                return string.Join(" ", popAt);
            }

            for (int i = 0; i < idle.Count; i++)
            {
                BulletTarget t = targets[i % targets.Length];
                Vector2 to = t.Chest;

                if (to == Vector2.Zero)
                {
                    continue;
                }

                Round r = idle[i];
                r.Flying = true;
                r.FlyT = 0f;
                r.FlyDelay = i * BulletStagger;
                r.FrontHalf = true;                                     // ★ 飞出去的弹永远在前层（不被她挡住）
                r.PopScale = 1f;                                        // 环轨用它表达"近大远小"，飞行前归位
                r.From = r.Root.Position;
                r.To = to - GlobalPosition;                             // 世界 → 弹幕本地
                r.Head = new Vector2(r.To.X, r.To.Y - t.HeadUp);        // ★ 头顶 = 胸口再往上 HeadUp

                KickNeighbors(r);                                       // ★ 整圈跟着颤一下
                Reslot();
            }

            // ★★ 2026-10-19：**全屏曝光脉冲**（用户："效果不明显"）。
            //
            // 为什么加这个而不是继续加亮：弹体/光束**早就已经裁成纯白了**
            // （实测 2.42 倍，50% 像素顶格），再加亮屏幕上一个像素都不会变。
            // 唯一"一定看得见"的是**整屏**的变化 —— 这就是 RegentFX 每张牌都推一次曝光的原因
            // （`WorldEnvironmentUtil.TweenExposure`）。本工程的 `TweenExposure()` 早就写好了
            // （`CardFieldVfx.cs:1274`，交接文档里一直记着"没接"），这一轮终于接上。
            if (idle.Count > 0)
            {
                double nowMs = Time.GetTicksMsec();

                if (nowMs - _lastExposureMs > 220d)          // 一次齐射只推一次，别成频闪
                {
                    _lastExposureMs = nowMs;

                    // ⚠️ 这条必须打：曝光脉冲拿不到 WorldEnvironment 时是**静默失败**的（铁律 57 同型），
                    //    不打日志就会出现"我明明加了、屏幕上什么都没有"的局面（本轮已经吃过一次这个亏）。
                    bool pushed = TweenExposure();
                    LogOnce($"[子弹模式·特效] 全屏曝光脉冲 = {(pushed ? "已推" : "失败（拿不到 WorldEnvironment，这条不会有效果）")}");
                }
            }

            return string.Empty;
        }

        /// <summary>一颗被打出去时，把环上其余的弹沿切线**外推一下**（阻尼弹簧，见 <see cref="Round.Kick"/>）。</summary>
        private void KickNeighbors(Round fired)
        {
            foreach (Round o in _rounds)
            {
                if (o == fired || o.Flying || o.Retiring)
                {
                    continue;
                }

                o.KickVel += KickImpulse;

                // 整圈一起"亮一下 + 甩一下"（机关感：环是一个整体，不是各自为战）
                _ringFlash = 1f;
                _spinBoost = 1f;
            }
        }

        /// <summary>连带反应的初速度（世界单位/秒）。520 ⇒ 峰值外推约 20px 后弹回（弹簧 k=150 / 阻尼 11）。</summary>
        private const float KickImpulse = 520f;

        /// <summary>环轨的弹簧刚度 / 阻尼（见 <see cref="Round.Kick"/> 的注释）。</summary>
        private const float KickStiffness = 150f;
        private const float KickDamping = 11f;

        /// <summary>弹幕本地坐标 → **世界坐标**（只用于日志：轻爆看不到时先核对爆点）。</summary>
        private string WorldOf(Vector2 local) => $"({GlobalPosition.X + local.X:F0},{GlobalPosition.Y + local.Y:F0})";

        /// <summary>收场：全部淡出（能力掉了 / 战斗结束）。</summary>
        internal void Retire()
        {
            foreach (Round r in _rounds)            {
                r.Retiring = true;
            }
        }

        private void AddRound()
        {
            var root = new Node2D();
            AddChild(root);

            // ★★ 2026-10-18 深夜：弹体**程序化**（不再用 fx_bullet 贴图）。
            //    形状按"弹长 = 1"定义，靠 Node2D.Scale 缩到 world 长度。见 `BulletHead`。
            var body = new Node2D { Name = "Body" };
            root.AddChild(body);
            ShaderMaterial bodyMat = BuildHead(body);

            var halo = new Sprite2D { Texture = _haloTex, Material = AddMaterial() };
            root.AddChild(halo);

            // ★★ 后层副本（2026-10-19）：同一套局部坐标，只是挂在"生物后面"那个容器里。
            //    没有后容器时 `_backLayer` 为 null ⇒ 这两组节点建在 root 下（等于没有遮挡，但不会崩）。
            Node2D backHost = _backLayer ?? root;
            var rootBack = new Node2D { Name = "BodyBack" };
            backHost.AddChild(rootBack);

            var bodyBack = new Node2D { Name = "BodyB" };
            rootBack.AddChild(bodyBack);
            ShaderMaterial bodyMatBack = BuildHead(bodyBack);

            var haloBack = new Sprite2D { Texture = _haloTex, Material = AddMaterial(), Visible = false };
            rootBack.AddChild(haloBack);

            var r = new Round
            {
                Root = root,
                Body = body,
                BodyMat = bodyMat,
                Halo = halo,
                RootBack = rootBack,
                BodyBack = bodyBack,
                BodyMatBack = bodyMatBack,
                HaloBack = haloBack,
                Jitter = Range(_rng, -0.20f, 0.20f),
                Radius = Range(_rng, BulletRingMin, BulletRingMax),
                Length = Range(_rng, BulletLenMin, BulletLenMax),
                Phase = Range(_rng, 0f, Mathf.Tau),
                Bob = Range(_rng, BulletBobMax * 0.4f, BulletBobMax),
                Gain = Range(_rng, 1.5f, 1.9f),
                Aim = Range(_rng, 0f, Mathf.Tau),
                Spin = Range(_rng, 0.25f, 0.55f),
                AimSpeed = Range(_rng, 0.7f, 1.1f),
                Ang = 0f,
                Alpha = 0f,
            };

            // ★ 拖尾 5 节（用户 2026-10-12："飞行轨迹再加点料"）——用**圆亮点**而不是弹体虚影：
            //   弹体虚影偏暗偏小，实机上根本看不出来（用户："轨迹太不明显了，根本看不到"）。
            for (int k = 0; k < 5; k++)
            {
                var dot = new Sprite2D
                {
                    Texture = _dotTex,
                    Material = AddMaterial(),
                    Visible = false,
                };
                root.AddChild(dot);
                r.Tail.Add(dot);
            }

            _rounds.Add(r);
        }

        /// <summary>按"当前悬浮数"重新均分**环上相位**（发射/补弹后剩下的会平滑地补位）。</summary>
        private void Reslot()
        {
            List<Round> idle = _rounds.Where(r => !r.Flying && !r.Retiring).ToList();

            for (int i = 0; i < idle.Count; i++)
            {
                idle[i].Slot = i;
                idle[i].Slots = idle.Count;

                // ★ 2026-10-19 环轨：均分整圈（原来只占两侧 120° 的弧）
                idle[i].RingTarget = idle.Count <= 0 ? 0f : i * Mathf.Tau / idle.Count;
            }
        }

        public override void _Process(double delta)
        {
            try
            {
                float dt = (float)delta;
                _clock += dt;

                // ★ 开火后的两个"机关反应"的衰减 + 环的累计转角（见 _ringAngle 的注释）
                _ringAngle += (OrbitSpin + _spinBoost * 2.0f) * dt;
                _spinBoost = Mathf.Max(0f, _spinBoost - dt / 0.22f);
                _ringFlash = Mathf.Max(0f, _ringFlash - dt / 0.16f);
                _poll += dt;

                if (_poll >= 0.5f)
                {
                    _poll = 0f;

                    // ★ 只在**确定**能力没了的时候才收场：SL / 重开之后 _owner 可能是作废实例，
                    //   读能力会抛异常 —— 那时若当"没了"处理，弹幕会把自己拆掉（症状同样是"第二次就不对"）。
                    bool stillOn;

                    try
                    {
                        stillOn = _owner.Creature.GetPower<BulletModePower>() != null;
                    }
                    catch (Exception)
                    {
                        stillOn = true;
                    }

                    if (stillOn)
                    {
                        Follow();
                    }
                    else
                    {
                        Retire();
                    }
                }

                bool any = false;

                foreach (Round r in _rounds)
                {
                    Update(r, dt);
                    any |= !r.Dead;
                }

                _rounds.RemoveAll(r => r.Dead);

                if (!any && _rounds.Count == 0 && _owner.Creature.GetPower<BulletModePower>() == null)
                {
                    QueueFree();
                }
            }
            catch (Exception)
            {
                QueueFree();                            // 特效出错绝不能拖累战斗
            }
        }

        private void Update(Round r, float dt)
        {
            // ① 收场淡出
            if (r.Retiring && !r.Flying)
            {
                r.Alpha -= dt / BulletFadeOut;

                if (r.Alpha <= 0f)
                {
                    r.Dead = true;
                    KillRound(r);
                    return;
                }

                // ★★ "就地轻爆"那一颗：淡出的同时**自己弹一下**（先迅速涨到 1.6 倍再收），
                //    这样眼睛才抓得住"这里有一颗子弹被用掉了"（用户 2026-10-12：
                //    "无目标轻爆几乎看不到"——只有一记小爆闪是不够的，得让子弹本体动起来）。
                if (r.Popping)
                {
                    float k = 1f - r.Alpha;                          // 0 → 1
                    r.PopScale = 1f + 0.62f * Mathf.Sin(Mathf.Clamp(k, 0f, 1f) * Mathf.Pi);
                }

                ApplyIdle(r);
                return;
            }

            // ② 飞行
            if (r.Flying)
            {
                if (r.FlyDelay > 0f)
                {
                    r.FlyDelay -= dt;
                    ApplyIdle(r);
                    return;
                }

                r.FlyT += dt / BulletFlyTime;
                float u = Mathf.Clamp(r.FlyT, 0f, 1f);

                // ★★ 2026-10-18 用户要求"子弹轨迹要**直的**"：**去掉 Bezier、去掉螺旋摆动**，
                //    改成 From → To 的**等速直线插值**。三个理由：
                //      · 等速（不是平滑起停）：出膛就是一发子弹，不该有"慢慢滑出来 / 减速到站"的软感；
                //      · 直线 ⇒ 方向与朝向整段恒定，弹体不会左右扭，也不会"歪着飞到目标"；
                //      · 缎带拖尾记录的就是这些点 ⇒ 共线，尾巴自然成一条直线。
                Vector2 pos = r.From.Lerp(r.To, u);
                Vector2 dir = (r.To - r.From).Normalized();

                // ★ 飞出去的弹**永远在前层**：出膛那一刻可能还处在环的后半段，
                //   不强制切换的话它会一边飞一边被她挡住（看起来像"子弹消失了"）。
                r.FrontHalf = true;

                r.Root.Position = pos;
                DrawBody(r, pos, dir.Angle(), 1f, 0.78f, BulletSpeedStretch, BulletSpeedThin);

                // ★★ **粒子拖尾**（RegentFX 手法）：跟着弹体走、每帧丢粒子，粒子留在原地淡掉
                if (r.Puff == null)
                {
                    r.Puff = MakeTrailPuff();
                }

                if (r.Puff != null && GodotObject.IsInstanceValid(r.Puff))
                {
                    r.Puff.Position = pos;
                }

                // ★★ 2026-10-18 晚：**飞行时不再画那 5 颗点状尾巴**。
                //    弹道拉直之后，"彗尾缎带 + 一排等间距亮点"在一条直线上读成"串珠子"；
                //    轨迹现在完全交给那条细彗尾（头亮尾透明）。这里只是把它们藏起来。
                DrawTail(r, pos, Vector2.Zero, 0f, 0f);

                // ★ 出膛一瞬：**向后迸开的火星簇**（18+10 粒，两个发射器）+ 一记小火花
                if (!r.Launched && u < 0.18f)
                {
                    r.Launched = true;
                    SpawnImpact(r.From, 0.45f, 2);
                    SpawnLaunchBurst(r.From, dir);
                }

                // ★★ 残留光点：**2026-10-18 晚停用**（用户："子弹轨迹也好丑"）。
                //    它是 10-12 那版"轨迹不明显"的补丁 —— 沿路径每隔 0.055 段留一颗会淡的光点。
                //    但弹道拉直之后，等间距的点在一条直线上读成**一串珠子**，和缎带互相打架。
                //    现在轨迹只由上面那条彗尾负责（细、渐隐），下面 `SpawnTrail` / `TrailFx`
                //    保留代码不再调用（和 `SpawnSandDust` 的处理方式一致），要复活从这里接回去。
                //
                // while (r.TrailNext <= u && r.TrailNext < 1f) { ... SpawnTrail(...); r.TrailNext += 0.055f; }

                // ★★ **连续缎带拖尾**（用户 2026-10-12："还是不明显，直接做拖尾得了"）：
                //    外粗（柔光）+ 内亮（实心）两条 Line2D，随飞行把位置推进去；
                //    头亮尾透（Gradient）+ 前粗后细（WidthCurve）⇒ 就是一条彗尾。
                // ★★ **笔直弹道**：起点钉死、终点跟着弹头 ⇒ 这条线是被子弹"画"出来的，
                //    命中后还在空中留 0.70s（见 `BulletTrail`）。
                if (r.Trail == null)
                {
                    r.Trail = new BulletTrail();
                    AddChild(r.Trail);
                    r.Trail.Start(r.Length, _streakTex);
                }

                r.Trail.Track(r.From, r.From.DistanceTo(pos), dir.Angle());

                if (r.FlyT >= 1f)
                {
                    r.Trail?.Finish();                   // 弹没了，弹道线再留 0.12s（回缩淡出）
                    r.Trail = null;

                    // ★★ 贯穿：命中点再**往前打出去一截**（用户 2026-10-19："还不够帅"）。
                    //    这把枪是狙击/穿透的脾气 ⇒ 命中不该"停住"，要读出"打穿了"。
                    //    复用同一条程序化光束（更细、更淡、更短命），朝**弹道前方**再冲 1.4 个弹长，
                    //    然后由 `Finish()` 从尾部回缩收掉。
                    SpawnPierce(r.To, dir, r.Length);
                    KillPuff(ref r.Puff);
                    SpawnImpact(r.To, BulletHitFlashScale);
                    SpawnHitBurst(r.To, dir);     // ★ 命中一炸：锥形主喷 + 侧散 + 亮渣

                    // ★ 狙击镜 + **抖屏/顿帧**：齐射时 N 颗几乎同时命中，只做一次（间隔 < 250ms 跳过）。
                    //   `DoHitStop()` = 游戏原生的 `NGame.Instance.DoHitStop(ShakeStrength, ShakeDuration)`，
                    //   手法来自参照实现 RegentFX（它每张重击卡命中都调一次 `ScreenShake`）。
                    double nowMs = Time.GetTicksMsec();

                    if (nowMs - _lastScopeMs > BulletScopeCooldownMs)
                    {
                        _lastScopeMs = nowMs;
                        SpawnHeadScope(r.Head, BulletScopeRadius);
                        DoHitStop();
                    }

                    r.Dead = true;
                    KillRound(r);
                }

                return;
            }

            // ③ 悬浮：方位朝"两侧弧上的目标位"靠 + 自己的随机朝向缓慢摆动
            float target = SlotAngle(r.Slot, r.Slots) + r.Jitter;
            float gap = Mathf.Wrap(target - r.Ang, -Mathf.Pi, Mathf.Pi);
            r.Ang += Mathf.Clamp(gap, -4f * dt, 4f * dt);

            if (r.Alpha < 1f)
            {
                r.Alpha = Mathf.Min(1f, r.Alpha + dt / BulletFadeIn);
            }

            ApplyIdle(r);
        }

        /// <summary>
        /// ★★ 释放一颗弹的**两套**节点（2026-10-19 修"残留"）。
        ///
        /// <para><b>为什么必须成对释放</b>：环轨之后每颗弹有**前层**（<c>Root</c>，挂在弹幕下）和
        /// <b>后层</b>（<c>RootBack</c>，挂在 <c>BackCombatVfxContainer</c> 下）两份节点。
        /// 原来只 <c>r.Root.QueueFree()</c> ⇒ **后层那套永远留在场上**，而且它停在最后一帧的位置上
        /// ⇒ 用户看到的就是"不知道为啥有残留"（一颗停在半空的弹）。
        /// 用户 2026-10-19 实机截图确认过。</para>
        /// </summary>
        private static void KillRound(Round r)
        {
            try
            {
                if (GodotObject.IsInstanceValid(r.Root))
                {
                    r.Root.QueueFree();
                }
            }
            catch (Exception)
            {
                // 前层释放失败不影响后层
            }

            try
            {
                if (r.RootBack != null && GodotObject.IsInstanceValid(r.RootBack))
                {
                    r.RootBack.QueueFree();
                }
            }
            catch (Exception)
            {
                // 后层释放失败也不影响战斗
            }
        }

        /// <summary>
        /// ★★★ <b>环轨（伪 3D）</b>——2026-10-19 用户拍板的新方案，取代原来"两侧弧形悬浮"。
        ///
        /// <para><b>3D 感只有三条来源</b>（全部在这里，别再加第四条）：
        /// ① <b>椭圆投影</b>：圆的 y 乘 <see cref="OrbitSquash"/>，再整体倾斜 <see cref="OrbitTiltDeg"/>；
        /// ② <b>前后差</b>：深度 <c>d = sin φ</c> 决定缩放 <c>1 + OrbitNearBoost·d</c>
        /// 与明暗 <c>Lerp(OrbitBrightMin, OrbitBrightMax, (d+1)/2)</c> —— 近大亮、远小暗；
        /// ③ <b>遮挡</b>：<c>d &lt; 0</c> 的那半段改用后层那套节点（在 <c>BackCombatVfxContainer</c> 里），
        /// 于是她的精灵会盖住它。</para>
        ///
        /// <para><b>朝向 = 切线</b>（用户："朝向都行"）—— 读起来像一条流动的弹链，
        /// 而且脱轨飞出去时方向天然顺。</para>
        ///
        /// <para>⚠️ 位置算在<b>弹幕本地坐标</b>里（本节点已锚在她的胸口），
        /// 前后两套节点用**同一套局部坐标**，所以切换那一帧不会跳。</para>
        /// </summary>
        private void ApplyIdle(Round r)
        {
            float dt = (float)GetProcessDeltaTime();

            // ★ 补位：相位朝目标平滑靠拢（发射掉一颗后，剩下的会自己滑到均分位置，不"啪"地跳一下）
            float gap = Mathf.Wrap(r.RingTarget - r.RingPhase, -Mathf.Pi, Mathf.Pi);
            r.RingPhase += gap * Mathf.Min(1f, dt * 6f);

            // ★ 连带反应的弹簧（有阻尼 ⇒ 推出去会自己弹回，不是飘走）
            r.KickVel += (-r.Kick * KickStiffness - r.KickVel * KickDamping) * dt;
            r.Kick += r.KickVel * dt;
            r.Kick = Mathf.Clamp(r.Kick, -40f, 40f);

            float phi = _ringAngle + r.RingPhase;
            // ★ 倾角摆动（用户 2026-10-19）：整环的倾角在一个区间内来回摇。
            //   注意用 `_clock` 而不是每颗自己的相位 —— 环是**一个整体**，必须同相摆动，
            //   否则每颗的椭圆平面都不一样，会散成"一群乱飞的弹"。
            float tilt = Mathf.DegToRad(OrbitTiltDeg + OrbitTiltSwingDeg * Mathf.Sin(_clock * OrbitTiltSwingRate));

            // ① 圆 ⇒ 椭圆 ⇒ 倾斜
            // ★ 半径摆动（用户 2026-10-19）：环像在"一呼一吸"。同样用全局 `_clock` ⇒ 整圈同相。
            float radius = OrbitRadius + OrbitRadiusSwing * Mathf.Sin(_clock * OrbitRadiusSwingRate);

            Vector2 p = new(Mathf.Cos(phi) * radius,
                            Mathf.Sin(phi) * OrbitSquash * radius);
            p = p.Rotated(tilt);

            // 切线方向（对 φ 求导后再倾斜）—— 弹体指向它
            Vector2 tan = new Vector2(-Mathf.Sin(phi), Mathf.Cos(phi) * OrbitSquash).Rotated(tilt);
            float ang = tan.Angle();

            // ★ 连带反应：沿**切线**外推（整圈的弹一起被带一下，读成"环在颤"）
            p += tan.Normalized() * r.Kick;

            // ② 深度
            float depth = Mathf.Sin(phi);                       // +1 最近，-1 最远
            float bright = Mathf.Lerp(OrbitBrightMin, OrbitBrightMax, (depth + 1f) * 0.5f);

            r.PopScale = r.Popping
                ? r.PopScale                                    // ★ 就地轻爆那一弹：PopScale 归它管，别被这里覆盖
                : 1f + OrbitNearBoost * depth;                  // 环轨：近大远小（复用同一个字段）
            r.FrontHalf = depth >= 0f;                          // ③ 前一帧/后一帧由这里决定用哪套节点
            r.Ang = phi;

            r.Root.Position = p;
            r.RootBack.Position = p;

            // 明暗只走 alpha（Modulate），贴图颜色与 shader 都不动
            // ★ 开火那一拍整圈额外亮 +55%（见 _ringFlash）
            DrawBody(r, p, ang, r.Alpha * bright * (1f + 0.55f * _ringFlash), 0.70f);
            DrawTail(r, p, Vector2.Zero, 0f, 0f);
        }

        /// <summary>
        /// 把"第 slot 颗 / 共 slots 颗"映射成**她身体两侧的弧形方位**（弧度；0 = 正右，y 向下）。
        ///
        /// <para>为什么不用整圈均分：她的轮廓**高远大于宽**，正下方那颗要么压在她腿上、
        /// 要么被避让推到脚底以下（跑到画面外 / 钻到 HUD 后面）。所以两侧各占一半、各展开 120°，
        /// 从"斜上"扫到"斜下"，**不经过正下方**。</para>
        /// </summary>
        private static float SlotAngle(int slot, int slots)
        {
            if (slots <= 0)
            {
                return 0f;
            }

            int half = Mathf.Max(1, slots / 2);
            bool left = slot >= half;
            int count = Mathf.Max(1, left ? slots - half : half);
            int index = left ? slot - half : slot;
            float t = count <= 1 ? 0.5f : index / (float)(count - 1);
            float deg = left ? Mathf.Lerp(252f, 132f, t) : Mathf.Lerp(-72f, 48f, t);

            return Mathf.DegToRad(deg);
        }

        /// <summary>
        /// 该方位上"不压到她自己"的最小半径 = 她的轮廓椭圆边界 + 弹体半长 + 余量。
        ///
        /// <para>轮廓用椭圆近似（半宽 / 半高×0.92，各再加余量）；方位角 <paramref name="ang"/> 上的
        /// 边界距离 = <c>1 / sqrt((cos/rx)² + (sin/ry)²)</c>。</para>
        /// </summary>
        private float ClearanceAt(float ang, float length)
        {
            float rx = Mathf.Max(_silhouette.X, 40f) + BulletClearMargin;
            float ry = Mathf.Max(_silhouette.Y * 0.92f, 60f) + BulletClearMargin;
            float cx = Mathf.Cos(ang) / rx;
            float sy = Mathf.Sin(ang) / ry;
            float edge = 1f / Mathf.Max(1e-4f, Mathf.Sqrt(cx * cx + sy * sy));

            return edge + length * 0.55f + 10f;
        }

        /// <summary>实测她的轮廓（<c>Hitbox.Size</c> 的一半）；读不到就保持 0（退化：只用设计稿半径）。</summary>
        private void MeasureSilhouette()
        {
            try
            {
                object? node = CreatureNodeOf(_owner.Creature);
                object? hitbox = node?.GetType().GetProperty("Hitbox")?.GetValue(node);
                object? size = hitbox?.GetType().GetProperty("Size")?.GetValue(hitbox);

                if (size is Vector2 s && s.X > 1f && s.Y > 1f)
                {
                    _silhouette = new Vector2(s.X * 0.5f, s.Y * 0.5f);
                }
            }
            catch (Exception e)
            {
                BulletLog.Info($"[子弹模式·特效] 轮廓读不到（退回设计稿半径）：{e.GetType().Name}");
            }
        }

        /// <summary>弹体 + 光晕（弹尖朝 <paramref name="ang"/>；贴图弹尖在上 ⇒ 转角要减 90°）。
        /// <para>⚠️ 两者都是 <c>r.Root</c> 的**子节点**，而 Root 的位置已经是弹体位置了 ⇒
        /// 这里的 local 位置必须是 <c>Vector2.Zero</c>。2026-10-12 踩过：把绝对坐标写进子节点的
        /// local 位置 ⇒ 光晕被推到 **2 倍偏移**上（用户："轨迹根本看不到" —— 拖尾整条跑出了画面）。</para></summary>
        private void DrawBody(Round r, Vector2 pos, float ang, float alpha, float gain,
                              float stretchY = 1f, float stretchX = 1f)
        {
            // ★★ 2026-10-19 环轨：**两套节点同时摆位，只显示该显示的那一套**
            //    （两套都摆位，是为了"跨过前后半段"那一帧不出现旧位置跳变）。
            float k = r.Length * r.PopScale;
            Vector2 bodyScale = new(k * stretchX, k * stretchY);
            float rot = ang - Mathf.Pi / 2f;
            Color tint = Tint(r.Gain * gain, alpha);
            float framePhase = Mathf.Floor(_clock * BulletFlipRate + r.Phase * (4f / Mathf.Tau));

            ApplyBodyPair(r.Body, r.Halo, r.BodyMat, r.FrontHalf, alpha, rot, bodyScale, tint, framePhase);
            ApplyBodyPair(r.BodyBack, r.HaloBack, r.BodyMatBack, !r.FrontHalf, alpha, rot, bodyScale, tint, framePhase);
        }

        /// <summary>摆一套弹体 + 光环（前层或后层）。<paramref name="visible"/> 为假时两者都隐藏。</summary>
        private void ApplyBodyPair(Node2D bodyNode, Sprite2D halo, ShaderMaterial? mat, bool visible,
                                  float alpha, float rot, Vector2 bodyScale, Color tint, float framePhase)
        {
            bool on = visible && alpha > 0.01f;

            bodyNode.Visible = on;
            bodyNode.Position = Vector2.Zero;                 // ★ local：Root 已经在弹体位置上了
            bodyNode.Rotation = rot;
            bodyNode.Scale = bodyScale;
            bodyNode.Modulate = tint;

            if (mat != null)
            {
                mat.SetShaderParameter("frame", framePhase);
            }

            if (halo.Texture == null)
            {
                return;
            }

            halo.Visible = on;
            halo.Position = Vector2.Zero;                     // ★ local：同上（这行原来是绝对坐标 = bug）
            halo.Scale = Vector2.One * (bodyScale.Y / halo.Texture.GetWidth() * 1.05f);
            halo.Modulate = Tint(0.68f, 0.30f * alpha);
        }

        /// <summary>三连拖尾（沿运动反方向，越远越小越淡）。</summary>
        private void DrawTail(Round r, Vector2 pos, Vector2 dir, float tail, float alpha)
        {
            for (int k = 0; k < r.Tail.Count; k++)
            {
                Sprite2D dot = r.Tail[k];
                float d = tail * (k + 1) * 0.22f;

                if (tail <= 0f || alpha <= 0.01f)
                {
                    dot.Visible = false;
                    continue;
                }

                // ★ local：Root 已在弹体位置上 ⇒ 拖尾点只需"沿反方向退 d"（原来是绝对坐标 = bug）
                dot.Visible = true;
                dot.Position = -dir * d;
                dot.Rotation = dir.Angle() - Mathf.Pi / 2f;
                dot.Scale = Vector2.One * (r.Length * (0.62f - 0.09f * k)
                                           / Mathf.Max(1f, dot.Texture?.GetWidth() ?? 1f));
                dot.Modulate = Tint(1.5f * r.Gain, (0.50f - 0.085f * k) * alpha);
            }
        }

        /// <summary>
        /// ★★ <b>贯穿</b>：命中点朝弹道**前方**再打出一截短光束（0.12s 回缩收掉）。
        ///
        /// <para>为什么加它：用户 2026-10-19 "还不错但还不够帅"。这一条**不增加元素种类**，
        /// 只是把已有的弹道光束在命中点续了一截 —— 但读感完全不同：
        /// <b>命中"停住" = 打了一下；命中"穿出去" = 打穿了</b>。这也是"狙击枪"该有的脾气。</para>
        ///
        /// <para>参数：长度 1.4 × 弹长、硬芯 alpha 0.75（比主光束弱）、柔晕 0.10；
        /// 起点从命中点后退 6px，避免和命中爆闪叠成一坨白。</para>
        /// </summary>
        private void SpawnPierce(Vector2 at, Vector2 dir, float len)
        {
            try
            {
                var pierce = new BulletTrail();
                AddChild(pierce);

                float L = Mathf.Max(40f, len * 2.4f);          // ★ 1.4 → 2.4 个弹长：贯穿要"穿出去"才看得见
                pierce.Start(len, _streakTex, 0.85f, 0.18f);    // ★ 也比主光束之外那一记更亮/更暖一点
                pierce.Track(at - dir * 6f, L, dir.Angle());
                pierce.Finish();                    // 立刻进入"回缩 + 淡出"
            }
            catch (Exception)
            {
                // 贯穿失败不影响命中本体
            }
        }

        /// <summary>命中 / 出膛 / 就地轻爆的小爆闪
        /// （<paramref name="scale"/> 1 = 命中那一记，0.45 = 出膛小火花，
        /// <see cref="BulletPopScale"/> = 无目标时"原地轻爆"）。</summary>
        private void SpawnImpact(Vector2 local, float scale = 1f, int sparks = 3, float delay = 0f,
                                 float life = BulletImpactTime)
        {
            try
            {
                var fx = new ImpactFx { Texture = _flareTex, Spark = _sparkTex };
                AddChild(fx);
                fx.Position = local;
                fx.Play(_rng, scale, sparks, delay, life);
            }
            catch (Exception)
            {
                // 爆闪失败不影响别的
            }
        }

        /// <summary>
        /// ★★ "就地轻爆"的一圈**扩散光环**（2026-10-12 第二轮新增）。
        ///
        /// <para>为什么加它：只有"一记爆闪 + 子弹弹一下"时，在战斗画面里还是像"闪了一下"；
        /// 一圈**从弹体位置往外扩、边扩边淡**的柔光环才是"炸开"的视觉语言（原版命中特效也是这个套路）。
        /// 贴图用弹体自己的柔光晕 <c>fx_circle_04</c>（已在包内，不新增素材）。</para>
        /// </summary>
        private void SpawnPopRing(Vector2 local, float delay)
        {
            try
            {
                var ring = new PopRingFx { Texture = _haloTex };
                AddChild(ring);
                ring.Position = local;
                ring.Play(delay);
            }
            catch (Exception)
            {
                // 光环失败不影响别的
            }
        }

        /// <summary>
        /// 目标**头顶**离胸口多高（世界单位）—— 小狙击镜挂在这里。
        ///
        /// <para>口径和「得见光芒」的 <c>HeadUpOf</c> 一致：游戏自己往生物上方放气泡
        /// （<c>NSpeechBubbleVfx</c>）用的是 <c>Hitbox.Size.Y × 0.5 × 0.75</c>，
        /// 我们取 <c>Hitbox.Size.Y × 0.45</c> 正好落在头部。<b>下限</b>取
        /// <see cref="BulletHeadFallback"/> 的 35%（≈31 世界单位）—— 小怪（史莱姆那种矮的）
        /// 若按它自己的高度算，环会压在胸口上；读不到 <c>Hitbox</c> 就用兜底值。</para>
        /// </summary>
        private static float HeadUpOf(object? creature)
        {
            try
            {
                object? node = CreatureNodeOf(creature);
                object? hitbox = node?.GetType().GetProperty("Hitbox")?.GetValue(node);
                object? size = hitbox?.GetType().GetProperty("Size")?.GetValue(hitbox);

                if (size is Vector2 s && s.Y > 1f)
                {
                    return Mathf.Clamp(s.Y * BulletHeadFrac, BulletHeadFallback * 0.35f, 400f);
                }
            }
            catch (Exception)
            {
                // 读不到就用兜底值，不打扰战斗
            }

            return BulletHeadFallback;
        }

        /// <summary>
        /// ★★ <b>命中时目标头顶的"小狙击镜"</b>（2026-10-18 重做）。
        ///
        /// <para><b>为什么整个重写</b>：旧版（<c>SpawnSniperScope</c>，还留在「得见光芒」那边）
        /// 是照 P3Makoto 的 <c>Reticle</c> 连数值一起搬的 —— 3 段外弧反向转 + 4 内弧 +
        /// 3 个三角 + 4 个角括号、半径 1.4 倍那套。用户判断是"抄"，而且那个形态本质上是
        /// <b>一枚会转的魔法纹章</b>，读不出"被瞄准"这件事。</para>
        ///
        /// <para><b>这一版只用我们自己的语言</b>（卡图 <c>SeeTheLight.png</c>：八芒白光爆 +
        /// 一圈干净斜椭圆环 + 红底 + 黑剪影）：</para>
        /// <list type="number">
        ///   <item><b>斜椭圆断环</b> —— 四段弧在 0/90/180/270° 各断 14°，压扁 0.42、整环倾 −12°。
        ///         <b>缺口本身就是准心方位</b>；没有刻度、没有虚线、不转，所以不读成法阵。</item>
        ///   <item><b>八芒</b> —— 两枚四角星叠 45°（和「得见光芒」的星爆同一个形状来源），
        ///         命中一瞬冲到 1.12 再回落。</item>
        ///   <item><b>黑剪影芯</b> —— 正中心一枚<b>不发光</b>的黑圆（<c>Mix</c> 混合）。
        ///         卡图里白爆是背景、黑剪影才是主体；有了它才像"瞄准/命中"，
        ///         而不是"又一团发光"。这也是整套里唯一一处"暗"的元素。</item>
        ///   <item><b>两条短准线</b> —— 横竖各一条、中段留缝，0.16s 后半段退掉。</item>
        /// </list>
        ///
        /// <para>尺寸"小小的"：半径 <see cref="BulletScopeRadius"/> = 28（得见光芒那边验证用的是 64）。
        /// 全程 0.42s：0.12s 收拢 + 淡入 → 停一拍 → 最后 0.22s 淡出。</para>
        ///
        /// <para>⚠️ 照 §6 的教训：所有 <c>Color</c> 的 alpha 一律给 1，
        /// <b>亮度与淡入淡出只走 <c>Modulate</c></b> —— 否则 <c>Color.a × Modulate.a</c>
        /// 会把它乘成 0，元素整段隐形。</para>
        /// </summary>
        /// <param name="local">弹幕本地坐标（= 目标头顶，见 <see cref="Round.Head"/>）。</param>
        private void SpawnHeadScope(Vector2 local, float radius)
        {
            try
            {
                var root = new Node2D { Name = "HeadScope" };
                AddChild(root);
                root.Position = local;
                root.Modulate = new Color(1f, 1f, 1f, 0f);

                // ── ★ v5A「极简锁定」：**只有两样东西** ────────────────────────────
                //    用户 2026-10-18："狙击镜不好看"、"能不能更帅更优雅"。
                //    上一版堆了 暗底板 + 四段断环 + 八芒 + 扫描弧 + 四个角括号 + 白热圈 + 准线，
                //    在游戏里每一样都只有 1~3px（见常量区的 0.586 说明）⇒ 看起来就是一团糊。
                //
                //    现在：两条**相对的弧**从上下合拢（像"合上一只眼"）+ 中心一枚小黑点带极细白环。
                //    少，但每一样都够大、都在动。
                var lid = new Node2D { Rotation = Mathf.DegToRad(BulletScopeTiltDeg) };
                root.AddChild(lid);

                FieldFx.Line(lid, FieldFx.Arc(radius, -1.02f, 1.02f, 16, BulletScopeSquash),
                    new Color(1f, 1f, 1f, 1f), radius * 0.062f);
                FieldFx.Line(lid, FieldFx.Arc(radius, Mathf.Pi - 1.02f, Mathf.Pi + 1.02f, 16, BulletScopeSquash),
                    new Color(1f, 1f, 1f, 1f), radius * 0.062f);

                // 中心：小黑点（Mix 混合，压在爆闪上像"瞳孔"）+ 极细白环
                Polygon2D core = FieldFx.Poly(root, FieldFx.Circle(radius * 0.13f, 20),
                    new Color(0.02f, 0.02f, 0.03f, 0.95f), additive: false);

                FieldFx.Line(root, FieldFx.Circle(radius * 0.155f, 24, 1f),
                    new Color(1f, 1f, 1f, 1f), radius * 0.028f, closed: true);

                // ── 时间轴：0.32 内合拢到位（从 1.40 倍收到 1.0），停一拍，最后 0.40 淡出 ──
                FieldFx.Animate(root, BulletScopeTime, t =>
                {
                    float inT = Mathf.Min(1f, t / 0.32f);
                    lid.Scale = Vector2.One * Mathf.Lerp(1.40f, 1f, FieldFxEase.OutQuint(inT));

                    float coreT = Mathf.Sqrt(Mathf.Clamp((t - 0.18f) / 0.30f, 0f, 1f));
                    core.Scale = Vector2.One * Mathf.Lerp(0.45f, 1f, coreT);

                    float outA = t < 0.60f ? 1f : 1f - (t - 0.60f) / 0.40f;
                    root.Modulate = new Color(1f, 1f, 1f, Mathf.Max(0f, Mathf.Min(inT, outA)));
                });

                FieldFx.FreeAfter(root, BulletScopeTime + 0.13);
            }
            catch (Exception)
            {
                // 狙击镜失败不影响命中爆闪
            }
        }

        // ── 小工具（**本类自带**：SeeLightVfx 那套 Tint/Range/SmoothStep/Load/AddMaterial 是它的私有成员，
        //    跨类借用会把两个特效耦在一起）──

        private static float Range(Random rng, float a, float b) => a + (float)rng.NextDouble() * (b - a);

        private static float SmoothStep(float x)
        {
            x = Mathf.Clamp(x, 0f, 1f);
            return x * x * (3f - 2f * x);
        }

        /// <summary>哨兵模式 ⇒ 洋红；否则白色 × gain（★ 不夹通道：gain &gt; 1 就是要更亮）。</summary>
        private static Color Tint(float gain, float alpha) => TintColor(Colors.White, gain, alpha);

        /// <summary>带基色的 <see cref="Tint"/>（拖尾要暖琥珀色，见 <c>TrailWarm</c>）。</summary>
        private static Color TintColor(Color color, float gain, float alpha)
        {
            Color c = SentinelMode ? SentinelColor : color;
            return new Color(c.R * gain, c.G * gain, c.B * gain, Mathf.Clamp(alpha, 0f, 1f));
        }

        /// <summary>
        /// **弹头顶点**（按"弹长 = 1"定义；弹尖在 <b>local +y</b>）。
        ///
        /// <para>⚠️ 弹尖为什么在 +y：画面上朝向是 <c>Body.Rotation = ang − π/2</c>，
        /// 局部 +y 经这个旋转后正好指向行进方向（局部 −y 会指向**背后**，弹头就反了）。</para>
        /// </summary>
        private static Vector2[] ArrowShape(float s) =>
        [
            new Vector2(0f, 0.725f) * s,      // 尖
            new Vector2(0.260f, 0f) * s,      // 右腰
            new Vector2(0f, -0.490f) * s,     // 尾
            new Vector2(-0.260f, 0f) * s,     // 左腰
        ];

        private static Vector2[] ArrowCoreShape(float s) =>
        [
            new Vector2(0f, 0.490f) * s,
            new Vector2(0.104f, 0f) * s,
            new Vector2(0f, -0.230f) * s,
            new Vector2(-0.104f, 0f) * s,
        ];

        private static Vector2[] BladeShape(float s) =>
        [
            new Vector2(0f, 0.810f) * s,
            new Vector2(-0.710f, 0.170f) * s,
            new Vector2(-0.710f, -0.170f) * s,
        ];

        /// <summary>按 <see cref="BulletHead"/> 造弹头 —— ★ 用**贴图**（用户："别自己画素材了"）。
        /// <para>2026-10-19：材质从 <see cref="FieldFxShaders.BulletSmear"/> 换成
        /// <see cref="FieldFxShaders.BulletCore"/> —— 采样公式逐字相同（默认单帧），
        /// 多了「白热核心」和「四步闪动」两项 RegentFX 手法。要回滚只需把这一行换回去。</para>
        /// <returns>材质本体：<see cref="DrawBody"/> 每帧往里递 <c>frame</c>。</returns></summary>
        private ShaderMaterial BuildHead(Node2D body)
        {
            // 运动模糊 + 白热核心 + 四步闪动（手法见 FieldFxShaders.BulletCore）
            var mat = new ShaderMaterial { Shader = FieldFxShaders.BulletCore };
            mat.SetShaderParameter("dir", new Vector2(0f, -1f));   // 贴图里弹尖朝上 ⇒ UV 空间 -y
            mat.SetShaderParameter("blur_len", BulletSmearLen);

            // ★★ 2026-10-19：这里原来是 1.35，而外面 DrawBody 还会乘一层
            //   `Tint(r.Gain * 0.78)`（r.Gain ∈ 1.5~1.9）⇒ 屏幕上的总倍数是**两个乘数相乘**。
            //   原来 1.35 × (1.5~1.9 × 1.05) ≈ **2.2~2.7 倍**，而 fx_bullet.png 是写实金属图
            //   （银弹头 luma 均值 0.745、铜壳 0.665）⇒ **整整一半的弹体被裁成纯白**，
            //   银/铜的层次消失，读起来是"一团奶油"（用户："挺奇怪的"）。
            //   现在把着色器这一层归 1.0，亮度只由 DrawBody 那一层控制（目标总倍率 ≈1.3）。
            mat.SetShaderParameter("gain", 1.0f);
            mat.SetShaderParameter("hot", BulletHot);
            mat.SetShaderParameter("hot_thresh", BulletHotThresh);
            mat.SetShaderParameter("shimmer", BulletShimmer);
            mat.SetShaderParameter("frame_count", 1f);             // 单帧：贴图不换，走同一条采样路径

            // fx_bullet.png：子弹本体（银白弹头 + 黄铜弹壳，画布 256 / 弹体 223 高）
            var sprite = new Sprite2D
            {
                Texture = _bodyTex,
                Material = mat,

                // ★ 形状按"弹长 = 1"：body 的 Scale 就是 world 弹长，
                //   所以贴图局部缩放 = 1/223 ⇒ 223px 的弹体正好占满那 1 个单位。
                Scale = Vector2.One * (1f / BulletSpriteContent),
            };

            body.AddChild(sprite);
            return mat;
        }

        /// <summary>
        /// ★★★ <b>弹体的粒子拖尾</b> —— 手法直接照抄参照实现 RegentFX 的 <c>_projectileParticles</c>：
        /// <b>拖尾是一串粒子，不是画出来的带子</b>。
        ///
        /// <para>做法：一个 <c>GpuParticles2D</c> **跟着弹体走**、每帧在弹体位置丢粒子，
        /// 速度≈0、无重力 ⇒ 粒子留在原地慢慢淡掉 ⇒ 弹体身后自动形成一条**会散开、有粗细变化**的尾迹。
        /// 这是 Line2D 做不到的（用户："轨迹又粗又单一"）。</para>
        ///
        /// <para>⚠️ 照 §6 的教训：粒子节点 <c>AddChild</c> 完立刻 <c>Emitting = true</c> 常常**不出现**，
        /// 必须等渲染过一两帧再点火 —— 用 <see cref="FieldFx.Ignite"/>。</para>
        /// </summary>
        /// <summary>
        /// ★★★ <b>飞行中的粒子尾</b> —— 两个发射器，照参照实现 RegentFX `MyNLargeMissile` 的
        /// <c>_projectileParticles</c> 套路（一个负责"柔和的光尘"、一个负责"有方向的火星"）。
        ///
        /// <para>① 光尘：圆点、慢、留在原地淡掉 ⇒ 一条**会散开**的拖尾（不是几何直线）；
        /// ② 火星：`fx_spark_01` 条状贴图、快、寿命短 ⇒ 一路"迸溅"的感觉。
        /// 两者叠加才叫"粒子特效"，只有一个就是"一串点"。</para>
        ///
        /// <para>⚠️ 照 §6 的教训：<c>AddChild</c> 完立刻 <c>Emitting = true</c> 常常不出现，
        /// 必须用 <see cref="FieldFx.Ignite"/> 延后一帧点火。</para>
        /// </summary>
        private Node2D MakeTrailPuff()
        {
            var root = new Node2D { Name = "BulletPuff" };
            AddChild(root);

            // ① 光尘
            GpuParticles2D dust = FieldFx.Sparks(
                root,
                new Color(1f, 0.84f, 0.62f),
                amount: 34,
                speedMin: 0f,
                speedMax: 30f,
                lifetime: 0.34f,
                gravity: 0f,
                scaleMin: 0.14f,
                scaleMax: 0.42f,
                oneShot: false);

            dust.VisibilityRect = new Rect2(-900f, -900f, 1800f, 1800f);

            // ② 火星（换成条状贴图 = 才是"火星"而不是"光点"）
            GpuParticles2D spark = FieldFx.Sparks(
                root,
                new Color(1f, 0.72f, 0.40f),
                amount: 18,
                speedMin: 40f,
                speedMax: 190f,
                lifetime: 0.22f,
                gravity: 0f,
                scaleMin: 0.10f,
                scaleMax: 0.28f,
                oneShot: false);

            spark.Texture = _dotTex;                    // ★ 星芒（原来用 fx_spark_01 = 闪电图，缩小后是一坨糊）
            spark.VisibilityRect = new Rect2(-900f, -900f, 1800f, 1800f);

            FieldFx.Ignite(dust, 2);
            FieldFx.Ignite(spark, 3);
            return root;
        }

        /// <summary>停掉粒子尾并回收（子弹命中/收场时调用）。</summary>
        private void KillPuff(ref Node2D? puff)
        {
            try
            {
                if (puff != null && GodotObject.IsInstanceValid(puff))
                {
                    foreach (Node child in puff.GetChildren())
                    {
                        if (child is GpuParticles2D g)
                        {
                            g.Emitting = false;
                        }
                    }

                    FieldFx.FreeAfter(puff, 0.55);
                }
            }
            catch (Exception)
            {
                // 收尾失败不影响别的
            }

            puff = null;
        }

        /// <summary>
        /// ★★★ <b>一次喷溅</b> —— 所有"火星"都从这里出，参数照真实火星的物理给。
        ///
        /// <para><b>用户 2026-10-18："火星爆开那个有点假"</b>。原来的三个问题，逐条修：</para>
        /// <list type="number">
        ///   <item><b>球形均匀</b>（Spread=180、方向一律朝上）⇒ 读起来是"一圈软圆点"。
        ///         真实撞击火星是**顺着来弹方向往回喷的锥形** ⇒ 给 <c>dir</c> + 窄 <c>spread</c>。</item>
        ///   <item><b>贴图错了</b>：<c>fx_spark_01.png</c> 是一张 512×512 的**闪电爆裂图**，
        ///         缩到 0.1 倍只剩一坨糊。改用 <c>fx_star_05.png</c>（星芒）—— 小尺寸下才读得出"火花"。</item>
        ///   <item><b>不减速、不缩小</b> ⇒ 粒子匀速飞出去然后突然消失。
        ///         加 <c>Damping</c>（空气阻力）+ <c>ScaleCurve</c>（越飞越小）。</item>
        /// </list>
        /// </summary>
        private GpuParticles2D Spray(Color color, int amount, float vmin, float vmax, float life,
                                     float gravity, float smin, float smax,
                                     Vector2? dir = null, float spread = 180f)
        {
            GpuParticles2D p = FieldFx.Sparks(this, color, amount, vmin, vmax, life, gravity, smin, smax);

            p.Texture = _dotTex;                    // ★ 星芒贴图，不是那张闪电

            if (p.ProcessMaterial is ParticleProcessMaterial m)
            {
                if (dir.HasValue)
                {
                    Vector2 d = dir.Value;

                    if (d.LengthSquared() > 0.0001f)
                    {
                        d = d.Normalized();
                    }

                    m.Direction = new Vector3(d.X, d.Y, 0f);
                    m.Spread = spread;
                }

                // ★★★ 以下六项**逐条照抄** RegentFX 的 `vfx_common_specks.tscn`
                //     （工坊第一特效模组的通用火星组件，参数是他们调出来的）：
                m.LifetimeRandomness = 0.8f;              // 寿命随机 ⇒ 不会"一起消失"
                m.ParticleFlagAlignY = true;              // 条纹沿速度方向
                m.ParticleFlagDampingAsFriction = true;   // 阻尼当摩擦（不是简单减速）
                m.Flatness = 1f;                          // 压成 2D 平面
                m.TurbulenceEnabled = true;               // ★ 湍流：火星会乱飘，不是直线飞
                m.TurbulenceNoiseStrength = 5f;
                m.TurbulenceNoiseScale = 10f;
                m.TurbulenceInfluenceMin = 0.6f;
                m.TurbulenceInfluenceMax = 0.85f;

                m.DampingMin = 40f;
                m.DampingMax = 150f;
                m.ScaleCurve = ShrinkCurve;
            }

            return p;
        }

        /// <summary>粒子随寿命收缩的曲线（建一次复用）。</summary>
        private static CurveTexture? _shrink;

        private static CurveTexture ShrinkCurve => _shrink ??= new CurveTexture { Curve = BuildShrink() };

        private static Curve BuildShrink()
        {
            var c = new Curve();
            c.AddPoint(new Vector2(0f, 1f));
            c.AddPoint(new Vector2(0.55f, 0.62f));
            c.AddPoint(new Vector2(1f, 0.10f));
            return c;
        }


        /// <summary>
        /// ★ <b>出膛一瞬</b>：一朵向后迸开的火星（18 粒）+ 一记暖光。
        /// 手法来自 RegentFX 的 <c>_projectileStartParticles</c>。
        /// </summary>
        private void SpawnLaunchBurst(Vector2 at, Vector2 dir)
        {
            try
            {
                // ① 向后喷的**锥形**火星（不是球形）—— 这是"被打出去"的读法
                GpuParticles2D cone = Spray(new Color(1f, 0.88f, 0.66f), 16, 180f, 520f, 0.30f, 200f,
                                            0.07f, 0.26f, dir: -dir, spread: 62f);

                cone.Position = at;
                FieldFx.Ignite(cone);
                FieldFx.FreeAfter(cone, 0.9);

                // ② 一记暖光团（全向、慢、大）—— 锥形火星旁边需要一点"雾"，否则太干
                GpuParticles2D puff = Spray(new Color(1f, 0.80f, 0.52f), 8, 20f, 90f, 0.34f, 0f,
                                            0.20f, 0.42f);

                puff.Position = at;
                FieldFx.Ignite(puff, 2);
                FieldFx.FreeAfter(puff, 0.9);
            }
            catch (Exception)
            {
                // 出膛火花失败不影响别的
            }
        }

        /// <summary>
        /// ★ <b>命中一炸</b>（三个发射器，各管一种"火星"）。
        /// 结构照 RegentFX 的 <c>_impactParticles</c>，但方向按真实撞击的物理来：
        /// 主喷**顺来弹方向往回**、侧向一大片散开、再加几粒又大又慢的亮渣。
        /// </summary>
        private void SpawnHitBurst(Vector2 at, Vector2 dir)
        {
            try
            {
                // ① 主喷：往回打。★ 速度跨度照 RegentFX 的 100~2500 的比例放大（他们敢用 25 倍跨度）
                GpuParticles2D cone = Spray(new Color(1f, 0.94f, 0.80f), 24, 180f, 1500f, 0.30f, 300f,
                                            0.04f, 0.22f, dir: -dir, spread: 66f);

                cone.Position = at;
                FieldFx.Ignite(cone);

                // ② 侧向散开（绕垂直轴 150°）—— 只有一条锥会很单薄
                GpuParticles2D side = Spray(new Color(1f, 0.82f, 0.56f), 12, 120f, 380f, 0.34f, 380f,
                                            0.07f, 0.22f, dir: new Vector2(-dir.Y, dir.X), spread: 150f);

                side.Position = at;
                FieldFx.Ignite(side, 2);

                // ③ 几粒又大又慢的亮渣（落在附近慢慢灭，给一个"落点"）
                GpuParticles2D glow = Spray(new Color(1f, 0.96f, 0.88f), 5, 30f, 110f, 0.42f, 60f,
                                            0.18f, 0.40f);

                glow.Position = at;
                FieldFx.Ignite(glow, 3);

                FieldFx.FreeAfter(cone, 1.0);
                FieldFx.FreeAfter(side, 1.0);
                FieldFx.FreeAfter(glow, 1.1);
            }
            catch (Exception)
            {
                // 命中火星失败不影响别的
            }
        }

        /// <summary>
        /// 加色 / 普通混合材质 —— ★★ 2026-10-18：<b>不再每次 new</b>，改用 <see cref="FieldFx"/> 里
        /// 已经缓存好的两个单例。
        /// <para>原来每颗子弹要 new 出 2 个（弹体 + 光晕）、每个拖尾点 1 个、每条缎带 2 个 ——
        /// 15 颗满弹幕 + 飞行中的缎带，一次出牌能瞬间造出上百个 <c>CanvasItemMaterial</c>，
        /// 每个都要在渲染器里登记一遍。材质本身是**无状态的**（只决定 blend mode），
        /// 全场景共用一个完全安全。</para>
        /// </summary>
        private static CanvasItemMaterial AddMaterial()
            => SentinelMode ? FieldFx.Normal : FieldFx.Additive;

        private static Texture2D? Load(string path) => ResourceLoader.Load<Texture2D>(path);

        /// <summary>
        /// ★★★ <b>贴图拉伸的拖尾</b>（2026-10-18 深夜重写；用户："别自己画素材了，我们 mod 文件夹里面有很多素材"）。
        ///
        /// <para>用 <c>ray_sk_06_trail.png</c>（包里现成的一条光束条带贴图）沿飞行方向**非等比拉伸**：
        /// 起点钉在 <c>From</c>、长度跟着弹头长、横向保持很细。</para>
        ///
        /// <para><b>为什么不再用 Line2D 画带子</b>：我手画的那条带宽是 <c>弹长 × 0.55</c> ≈ 36~50 世界单位，
        /// 而参考实现（P3Makoto 的 <c>Streak</c>）线宽只有 <b>7</b> —— 用户说的"轨迹又粗又单一"就是这个。
        /// 贴图自带明暗/毛边，比等宽色带自然得多。</para>
        /// </summary>
        private sealed partial class BulletTrail : Node2D
        {
            /// <summary>弹头没了之后，这道光还在空中留多久（用户："拖拖拉拉" ⇒ 只留一小拍）。</summary>
            private const float FadeAfter = 0.12f;

            /// <summary>
            /// ★ 出膛**过冲**时长（秒）。用户 2026-10-19："还不错但还不够帅"。
            ///
            /// <para>做法：光束出现的头 60ms 里把亮度冲到 <b>1.9 倍</b>（硬芯）与 <b>3 倍</b>（柔晕），
            /// 然后线性落回常态。这是"能量刚被压出去"的那一下 —— <b>冲击感来自亮度的时间剖面，
            /// 而不是加更多元素</b>（这条是本项目最贵的一条教训：得见光芒的"光束膨胀 10 倍再收细"
            /// 比后来堆的十来个元素都有效）。</para>
            /// </summary>
            private const float LaunchOver = 0.06f;

            /// <summary>quad 的世界高度：决定柔晕能铺多宽（硬芯半宽见 <see cref="CoreHalf"/>）。</summary>
            private const float QuadH = 28f;

            /// <summary>硬芯半宽（世界像素）。★ 2026-10-19 从 8 提到 **12** —— 用户"效果不明显"：
            /// 亮度已经顶格，**唯一还能"变大"的就是尺寸和色对比**。12 ⇒ 直径 24px（弹长 69 的 35%）。</summary>
            private const float CoreHalf = 12f;

            private Sprite2D? _streak;
            private ShaderMaterial? _mat;
            private float _texW = 128f;
            private float _texH = 128f;
            private float _fade = -1f;
            private float _age;
            private float _coreA = 0.95f;
            private float _haloA = 0.16f;

            /// <summary>造一条光束（<paramref name="coreAlpha"/>/<paramref name="haloAlpha"/> 可压低 ⇒ 命中切割痕是同一套东西的弱化版）。</summary>
            public void Start(float length, Texture2D? tex, float coreAlpha = 0.95f, float haloAlpha = 0.16f)
            {
                SetProcess(true);
                _coreA = coreAlpha;
                _haloA = haloAlpha;

                _texW = tex?.GetWidth() ?? 128f;
                _texH = tex?.GetHeight() ?? 128f;

                // ★★★ 2026-10-19：**一条程序化光束**，不再是"3 层贴图等宽拉伸"。
                //   旧版实测宽度 26px / 亮度 0.66 **全长恒定** = 用户否过的"尺子"。
                //   现在沿长度的宽度剖面、亮度剖面、两头切口、命中回缩全在 shader 里
                //   （见 `FieldFxShaders.BulletStreak`），这里只负责把它摆成 from→head 那一段。
                _mat = new ShaderMaterial { Shader = FieldFxShaders.BulletStreak };
                _mat.SetShaderParameter("core_w", CoreHalf / (QuadH * 0.5f));
                _mat.SetShaderParameter("halo_mul", 4.2f);          // ★ 3.4 → 4.2：白上白看不见，靠**更宽的暖色晕**拉开对比
                _mat.SetShaderParameter("core_alpha", 0.95f);
                _mat.SetShaderParameter("halo_alpha", 0.26f);       // ★ 0.16 → 0.26：暗底上这条琥珀晕才是"看得见"的部分
                _mat.SetShaderParameter("tail_cut", 0f);

                _streak = new Sprite2D
                {
                    Texture = tex,                 // 形状不使用它，只借它当"有贴图的 quad"
                    Centered = true,
                    Material = _mat,
                };

                AddChild(_streak);
            }

            /// <summary>每帧：把光束从 <paramref name="from"/> 拉到弹头（轴向 = 飞行方向）。</summary>
            public void Track(Vector2 from, float dist, float ang)
            {
                Position = from;
                Rotation = ang;

                if (_streak == null)
                {
                    return;
                }

                _streak.Position = new Vector2(dist * 0.5f, 0f);
                _streak.Scale = new Vector2(Mathf.Max(1f, dist) / _texW, QuadH / _texH);
            }

            public void Finish() => _fade = 0f;

            public override void _Process(double delta)
            {
                try
                {
                    _age += (float)delta;

                    // ★ 出膛过冲：头 60ms 亮度冲高再落回（"被压出去"的那一下）
                    float over = _age < LaunchOver
                        ? 1f + 0.9f * (1f - _age / LaunchOver)
                        : 1f;

                    if (_fade < 0f)
                    {
                        // 还在飞：只更新亮度包络
                        _mat?.SetShaderParameter("core_alpha", _coreA * over);
                        _mat?.SetShaderParameter("halo_alpha", _haloA * over);
                        return;
                    }

                    _fade += (float)delta;
                    float k = Mathf.Clamp(_fade / FadeAfter, 0f, 1f);

                    // ★ 命中后**从尾部往前收**（tail_cut 0→1），不是整体变暗
                    //   ⇒ 读起来像"这条光被命中点吸回去了"，比淡出贵气得多。
                    _mat?.SetShaderParameter("tail_cut", k);
                    _mat?.SetShaderParameter("core_alpha", _coreA * over * (1f - k * 0.55f));
                    _mat?.SetShaderParameter("halo_alpha", _haloA * over * (1f - k));

                    if (k >= 1f)
                    {
                        QueueFree();
                    }
                }
                catch (Exception)
                {
                    QueueFree();
                }
            }
        }

        /// <summary>沿路径留一颗会淡掉的光点（见 <see cref="TrailFx"/>）。</summary>
        private void SpawnTrail(Vector2 local, float size, float alpha)
        {
            try
            {
                var fx = new TrailFx();
                AddChild(fx);
                fx.Position = local;
                fx.Play(_dotTex, size, alpha);
            }
            catch (Exception)
            {
                // 轨迹点失败不影响别的
            }
        }

        /// <summary>残留轨迹的一颗光点：0.35s 内缩小 + 淡掉，然后自己收。</summary>
        private sealed partial class TrailFx : Node2D
        {
            private const float Life = 0.35f;

            private Sprite2D _dot = null!;
            private Texture2D? _tex;
            private float _size, _alpha, _t;

            public void Play(Texture2D? tex, float size, float alpha)
            {
                SetProcess(true);
                _tex = tex;
                _size = size;
                _alpha = alpha;
                _dot = new Sprite2D
                {
                    Texture = tex,
                    Material = AddMaterial(),
                    Visible = true,
                };
                AddChild(_dot);
                _dot.Modulate = Tint(1.7f, alpha);
                ScaleDot(1f);
            }

            public override void _Process(double delta)
            {
                try
                {
                    _t += (float)delta;
                    float k = Mathf.Clamp(_t / Life, 0f, 1f);
                    ScaleDot(Mathf.Lerp(1f, 0.55f, k));
                    _dot.Modulate = Tint(1.7f, _alpha * (1f - k));

                    if (k >= 1f)
                    {
                        QueueFree();
                    }
                }
                catch (Exception)
                {
                    QueueFree();
                }
            }

            private void ScaleDot(float mul)
            {
                _dot.Scale = Vector2.One * (_size * mul / Mathf.Max(1f, _tex?.GetWidth() ?? 1f));
            }
        }

        /// <summary>命中的小爆闪（默认 0.3s，自己 QueueFree）。</summary>
        private sealed partial class ImpactFx : Node2D
        {
            public Texture2D? Texture;
            public Texture2D? Spark;

            private Sprite2D _flare = null!;
            private readonly List<(Sprite2D Sprite, float Ang, float Dist)> _sparks = new();
            private float _t;
            private float _scale = 1f;
            private float _delay;
            private float _life = BulletImpactTime;

            public void Play(Random rng, float scale = 1f, int sparks = 3, float delay = 0f,
                             float life = BulletImpactTime)
            {
                _scale = scale;
                _delay = delay;
                _life = Mathf.Max(0.05f, life);
                // ★ 显式开启（2026-10-12）：这些子弹 Sprite 初值都是 Visible=false，只靠 Show() 点亮 ——
                //   `_Process` 若没跑，爆闪/光环就**永远是隐形的**（症状正是"轻爆几乎看不到"）。
                //   Godot C# 一般会为重写的 `_Process` 自动开处理，但这条链上不值得赌。
                SetProcess(true);

                _flare = new Sprite2D
                {
                    Texture = Texture,
                    Material = AddMaterial(),
                    Visible = false,
                };
                AddChild(_flare);

                for (int i = 0; i < sparks; i++)
                {
                    var s = new Sprite2D
                    {
                        Texture = Spark,
                        Material = AddMaterial(),
                        Visible = false,
                    };
                    AddChild(s);
                    _sparks.Add((s, Range(rng, 0f, Mathf.Tau), Range(rng, 30f, 150f) * WorldPerPreviewPx));
                }
            }

            public override void _Process(double delta)
            {
                try
                {
                    _t += (float)delta;

                    if (_t < _delay)
                    {
                        return;                     // 错开一点点再爆（"原地轻爆"是一颗接一颗的）
                    }

                    float k = Mathf.Clamp((_t - _delay) / _life, 0f, 1f);
                    float g = 1f - k;

                    if (g <= 0f)
                    {
                        QueueFree();
                        return;
                    }

                    float size = Mathf.Lerp(170f, 400f, SmoothStep(k)) * WorldPerPreviewPx * _scale;
                    Show(_flare, size, 1.90f, 0.90f * g);

                    foreach ((Sprite2D sprite, float ang, float dist) in _sparks)
                    {
                        float d = Mathf.Lerp(30f, dist / WorldPerPreviewPx, SmoothStep(k))
                                  * WorldPerPreviewPx * _scale;
                        sprite.Position = new Vector2(Mathf.Cos(ang) * d, Mathf.Sin(ang) * d * 0.6f);
                        Show(sprite, Mathf.Lerp(26f, 14f, k) * WorldPerPreviewPx * _scale, 1.50f, 0.80f * g);
                    }
                }
                catch (Exception)
                {
                    QueueFree();
                }
            }

            private static void Show(Sprite2D sprite, float size, float gain, float alpha)
            {
                sprite.Visible = alpha > 0.01f;
                sprite.Scale = Vector2.One * (size / Mathf.Max(1f, sprite.Texture?.GetWidth() ?? 1f));
                sprite.Modulate = Tint(gain, alpha);
            }
        }

        /// <summary>
        /// ★★ "就地轻爆"的**扩散光环**：从弹体位置往外扩到 3.2 倍、边扩边淡（0.45s）。
        /// <para>视觉语言上"炸开"靠的就是这一圈（只有爆闪的话看起来只是"闪了一下"）。</para>
        /// </summary>
        private sealed partial class PopRingFx : Node2D
        {
            private const float Life = 0.45f;
            private const float FromMul = 0.35f;        // 起始大小（相对弹长）
            private const float ToMul = 3.20f;          // 扩到多大

            public Texture2D? Texture;

            private Sprite2D _ring = null!;
            private float _t;
            private float _delay;

            public void Play(float delay)
            {
                _delay = delay;
                SetProcess(true);           // ★ 同上：环的 Sprite 也是 Visible=false 起步

                _ring = new Sprite2D
                {
                    Texture = Texture,
                    Material = AddMaterial(),
                    Visible = false,
                };
                AddChild(_ring);
            }

            public override void _Process(double delta)
            {
                try
                {
                    _t += (float)delta;

                    if (_t < _delay)
                    {
                        return;
                    }

                    float k = Mathf.Clamp((_t - _delay) / Life, 0f, 1f);
                    float size = Mathf.Lerp(FromMul, ToMul, SmoothStep(k)) * BulletPopWorld;
                    float alpha = 0.62f * (1f - k);

                    _ring.Visible = alpha > 0.01f;
                    _ring.Scale = Vector2.One * (size / Mathf.Max(1f, Texture?.GetWidth() ?? 1f));
                    _ring.Modulate = TintColor(new Color(1f, 0.86f, 0.66f), 0.90f, alpha);

                    if (k >= 1f)
                    {
                        QueueFree();
                    }
                }
                catch (Exception)
                {
                    QueueFree();
                }
            }
        }
    }
}
