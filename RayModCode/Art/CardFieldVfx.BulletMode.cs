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

    /// <summary>悬浮上限（用户："也可以和弹药挂钩，但不超过15，不然太多了"）。</summary>
    private const int BulletMax = 15;

    private const string BulletTexturePath = Entry.ResPath + "/images/vfx/fx_bullet.png";
    private const string BulletHaloPath = Entry.ResPath + "/images/vfx/fx_circle_04.png";
    private const string BulletFlarePath = Entry.ResPath + "/images/vfx/fx_flare_01.png";
    private const string BulletSparkPath = Entry.ResPath + "/images/vfx/fx_spark_01.png";
    /// <summary>拖尾/残留轨迹用的**圆亮点**（比弹体虚影亮得多，才看得见"轨迹"）。</summary>
    private const string BulletDotPath = Entry.ResPath + "/images/vfx/fx_star_05.png";

    /// <summary>弹体贴图里**弹体本身**的高度（画布 256、弹体 223；见 <c>make_bullet_sprite.py</c>）。</summary>
    private const float BulletSpriteContent = 223f;

    // ── 尺寸/位置：数值来自设计稿（预览像素 × WorldPerPreviewPx）──
    private const float BulletRingMin = 190f * WorldPerPreviewPx;   // 离她最近
    private const float BulletRingMax = 330f * WorldPerPreviewPx;   // 离她最远
    private const float BulletLenMin = 66f * WorldPerPreviewPx;     // 弹体最短
    private const float BulletLenMax = 92f * WorldPerPreviewPx;     // 弹体最长
    private const float BulletBobMax = 18f * WorldPerPreviewPx;     // 起伏幅度

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
    private const float BulletFlyTime = 0.24f;      // 从她身边飞到敌人
    private const float BulletStagger = 0.06f;      // 连发错开
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

    private static readonly Logger BulletLog = new("RayMod.CardVfx", LogType.Generic);

    private static BulletSwarm? _bulletSwarm;

    /// <summary>上一次出牌要打的目标（世界坐标）—— 弹药扣掉时照它分配。空 = 没目标（就地轻爆）。</summary>
    private static Vector2[] _bulletTargets = Array.Empty<Vector2>();

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
            _bulletTargets = Array.Empty<Vector2>();
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
        _bulletTargets = Array.Empty<Vector2>();
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
            var list = new List<Vector2>();

            if (hitsEnemies)
            {
                object? target = GetProperty(play, "Target");

                if (target != null)
                {
                    Vector2 p = ChestPos(target);

                    if (p != Vector2.Zero)
                    {
                        list.Add(p);
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
                            list.Add(p);
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
            _bulletTargets = Array.Empty<Vector2>();
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
                    _bulletTargets = Array.Empty<Vector2>();
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
            public Sprite2D Body = null!;
            public Sprite2D Halo = null!;
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
            public float TrailNext;         // （旧点阵轨迹用，保留字段避免删一半）
            public BulletTrail? Trail;      // ★ 连续缎带拖尾（用户："直接做拖尾得了"）
            public Vector2 From, Control, To;
            public bool Dead;
        }

        private Player _owner = null!;
        private readonly List<Round> _rounds = new();
        private Vector2 _silhouette;        // 她的半宽/半高（实测；0 = 读不到）
        private readonly Random _rng = new();
        private float _poll;
        private float _clock;
        private Texture2D? _bodyTex, _haloTex, _flareTex, _sparkTex, _dotTex;

        internal void Configure(Player owner)
        {
            _bodyTex = Load(BulletTexturePath);
            _haloTex = Load(BulletHaloPath);
            _flareTex = Load(BulletFlarePath);
            _sparkTex = Load(BulletSparkPath);
            _dotTex = Load(BulletDotPath);

            Reconfigure(owner);

            SetProcess(true);               // ★ 显式开启（别赌"重写了 _Process 就会自动开"）

            BulletLog.Info($"[子弹模式·特效] 弹幕就绪：{_rounds.Count} 颗（世界坐标 {GlobalPosition}，" +
                           $"她的轮廓 {( _silhouette == Vector2.Zero ? "没读到" : $"半宽 {_silhouette.X:F0} / 半高 {_silhouette.Y:F0}")}）");
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
        internal string Fire(int count, Vector2[] targets)
        {
            if (count <= 0)
            {
                return string.Empty;
            }

            List<Round> idle = _rounds.Where(r => !r.Flying && !r.Retiring)
                                       .OrderBy(_ => _rng.Next())
                                       .Take(count)
                                       .ToList();

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
                Vector2 to = targets[i % targets.Length];

                if (to == Vector2.Zero)
                {
                    continue;
                }

                Round r = idle[i];
                r.Flying = true;
                r.FlyT = 0f;
                r.FlyDelay = i * BulletStagger;
                r.From = r.Root.Position;
                r.To = to - GlobalPosition;                             // 世界 → 弹幕本地
                r.Control = new Vector2((r.From.X + r.To.X) / 2f,
                                        Mathf.Min(r.From.Y, r.To.Y) - 150f * WorldPerPreviewPx);
                Reslot();
            }

            return string.Empty;
        }

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

            var body = new Sprite2D { Texture = _bodyTex, Material = AddMaterial() };
            root.AddChild(body);

            var halo = new Sprite2D { Texture = _haloTex, Material = AddMaterial() };
            root.AddChild(halo);

            var r = new Round
            {
                Root = root,
                Body = body,
                Halo = halo,
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

        /// <summary>按"当前悬浮数"重新均分方位（发射/补弹后剩下的会平滑地补位）。</summary>
        private void Reslot()
        {
            List<Round> idle = _rounds.Where(r => !r.Flying && !r.Retiring).ToList();

            for (int i = 0; i < idle.Count; i++)
            {
                idle[i].Slot = i;
                idle[i].Slots = idle.Count;
            }
        }

        public override void _Process(double delta)
        {
            try
            {
                float dt = (float)delta;
                _clock += dt;
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
                    r.Root.QueueFree();
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
                float e = Mathf.Pow(SmoothStep(u), 0.85f);
                Vector2 basePos = Bezier(r.From, r.Control, r.To, e);
                Vector2 dir = (r.To - r.From).Normalized();
                Vector2 perp = new(-dir.Y, dir.X);

                // ★ 螺旋摆动（垂直于行进方向，2.5 个来回、越到末端越小）——"飞行轨迹再加点料"
                float twist = Mathf.Sin(e * Mathf.Pi * 2.5f) * 26f * WorldPerPreviewPx * (1f - e * 0.35f);
                Vector2 pos = basePos + perp * twist;
                float tail = Mathf.Lerp(110f, 380f, e) * WorldPerPreviewPx;

                r.Root.Position = pos;
                DrawBody(r, pos, dir.Angle(), 1f, 1.25f);
                DrawTail(r, pos, dir, tail, 1f);

                // ★ 出膛一瞬：起点一记小火花 + 两粒火星（"被打出来"的那一下）
                if (!r.Launched && u < 0.18f)
                {
                    r.Launched = true;
                    SpawnImpact(r.From, 0.45f, 2);
                }

                // ★★ 残留轨迹：沿路径**每隔 ~0.055 段留下一颗会淡掉的光点** ⇒ 飞行过后
                //    路径上留一条"虚线"，弹体本身早飞走了、轨迹还在（用户要的"轨迹"就是这个）。
                while (r.TrailNext <= u && r.TrailNext < 1f)
                {
                    float ue = r.TrailNext;
                    float ee = Mathf.Pow(SmoothStep(ue), 0.85f);
                    Vector2 tp = Bezier(r.From, r.Control, r.To, ee);
                    float ttw = Mathf.Sin(ee * Mathf.Pi * 2.5f) * 26f * WorldPerPreviewPx * (1f - ee * 0.35f);
                    SpawnTrail(tp + perp * ttw, r.Length * 0.55f, 0.55f * (1f - ue * 0.5f));
                    r.TrailNext += 0.055f;
                }

                // ★★ **连续缎带拖尾**（用户 2026-10-12："还是不明显，直接做拖尾得了"）：
                //    外粗（柔光）+ 内亮（实心）两条 Line2D，随飞行把位置推进去；
                //    头亮尾透（Gradient）+ 前粗后细（WidthCurve）⇒ 就是一条彗尾。
                if (r.Trail == null)
                {
                    r.Trail = new BulletTrail();
                    AddChild(r.Trail);
                    r.Trail.Start(r.Length);
                }

                r.Trail.Push(pos);

                if (r.FlyT >= 1f)
                {
                    r.Trail.Finish();                    // 弹没了，缎带再飘 0.3s
                    r.Trail = null;
                    SpawnImpact(r.To);
                    r.Dead = true;
                    r.Root.QueueFree();
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

        private void ApplyIdle(Round r)
        {
            float bob = r.Bob * Mathf.Sin(Mathf.Tau * _clock * 1.7f + r.Phase);
            float radius = Mathf.Max(r.Radius, ClearanceAt(r.Ang, r.Length));
            Vector2 pos = new(Mathf.Cos(r.Ang) * radius, Mathf.Sin(r.Ang) * radius + bob);

            r.Root.Position = pos;
            DrawBody(r, pos, r.Aim + r.Spin * Mathf.Sin(Mathf.Tau * _clock * 0.8f * r.AimSpeed + r.Phase),
                     r.Alpha, 1f);
            DrawTail(r, pos, Vector2.Zero, 0f, 0f);
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
        private void DrawBody(Round r, Vector2 pos, float ang, float alpha, float gain)
        {
            r.Body.Visible = alpha > 0.01f;
            r.Body.Position = Vector2.Zero;                 // ★ local：Root 已经在弹体位置上了
            r.Body.Rotation = ang - Mathf.Pi / 2f;
            r.Body.Scale = Vector2.One * (r.Length / BulletSpriteContent) * r.PopScale;   // ★ PopScale = 轻爆那一弹
            r.Body.Modulate = Tint(r.Gain * gain, alpha);

            if (r.Halo.Texture != null)
            {
                r.Halo.Visible = alpha > 0.01f;
                r.Halo.Position = Vector2.Zero;              // ★ local：同上（这行原来是绝对坐标 = bug）
                r.Halo.Scale = Vector2.One * (r.Length * 1.05f * r.PopScale / r.Halo.Texture.GetWidth());
                r.Halo.Modulate = Tint(0.68f, 0.30f * alpha);
            }
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

        private static Vector2 Bezier(Vector2 a, Vector2 c, Vector2 b, float t)
        {
            float it = 1f - t;
            return it * it * a + 2f * it * t * c + t * t * b;
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

        private static CanvasItemMaterial AddMaterial() => new()
        {
            BlendMode = SentinelMode ? CanvasItemMaterial.BlendModeEnum.Mix : CanvasItemMaterial.BlendModeEnum.Add,
        };

        private static Texture2D? Load(string path) => ResourceLoader.Load<Texture2D>(path);

        /// <summary>
        /// ★★ <b>连续缎带拖尾</b>：两条 <c>Line2D</c>（外粗柔光 + 内亮实心）共用一串"最近 N 个位置"，
        /// 头亮尾透、前粗后细 ⇒ 一条彗尾。弹体没了之后再飘 <see cref="FadeAfter"/> 秒淡掉。
        ///
        /// <para>⚠️ 点的顺序**最新的在最前**：`Line2D.Gradient` / `WidthCurve` 都是按"第 0 点 → 最后一点"
        /// 推进的，顺序反了就会变成"头细尾粗、头透尾亮"。</para>
        /// </summary>
        private sealed partial class BulletTrail : Node2D
        {
            /// <summary>缎带采样点数。★ 14 → 18：14 点在 **144Hz** 下只覆盖 40% 的航程，
            /// 而预览那张图覆盖 52%（帧率越高缎带越短 = 用户看到的"游戏里没那么明显"）。</summary>
            private const int MaxPoints = 18;

            /// <summary>弹体没了之后再飘多久（★ 0.30 → 0.45：用户"游戏里没预览明显"）。</summary>
            private const float FadeAfter = 0.45f;

            /// <summary>拖尾的**暖琥珀**（与预览 <c>make_vfx_gif.py</c> 的 <c>TRAIL_C=(255,186,120)</c> 同色）。
            /// 纯白在加色混合下会跟场景里的米白高光糊成一片，暖色才"读得出来"。</summary>
            private static readonly Color TrailWarm = new(1f, 0.73f, 0.47f);

            /// <summary>内亮层：近白（略偏暖），比外粗层亮一档。</summary>
            private static readonly Color TrailHot = new(1f, 0.95f, 0.88f);

            private Line2D _core = null!;
            private Line2D _glow = null!;
            private readonly List<Vector2> _pts = new();
            private float _fade = -1f;              // < 0 = 还在积累（没结束）

            public void Start(float length)
            {
                SetProcess(true);
                _glow = MakeLine(length * 1.15f, 1.10f, TrailWarm);
                _core = MakeLine(length * 0.42f, 1.45f, TrailHot);
                AddChild(_glow);                    // 先加粗的 ⇒ 亮的叠在上面
                AddChild(_core);
            }

            public void Push(Vector2 local)
            {
                _pts.Insert(0, local);

                while (_pts.Count > MaxPoints)
                {
                    _pts.RemoveAt(_pts.Count - 1);
                }

                Vector2[] points = _pts.ToArray();
                _core.Points = points;
                _glow.Points = points;
            }

            /// <summary>弹体没了：开始淡出（自己 QueueFree）。</summary>
            public void Finish() => _fade = 0f;

            public override void _Process(double delta)
            {
                if (_fade < 0f)
                {
                    return;
                }

                try
                {
                    _fade += (float)delta;
                    float k = Mathf.Clamp(_fade / FadeAfter, 0f, 1f);

                    _core.Modulate = new Color(1f, 1f, 1f, 1f - k);
                    _glow.Modulate = new Color(1f, 1f, 1f, 1f - k);

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

            private static Line2D MakeLine(float width, float gain, Color color)
            {
                // ★★ 2026-10-12 用户实机："游戏里的拖尾没有黑底渲染的那么明显"。
                //    对照预览（`make_vfx_gif.py` 的 `ribbon`：头宽 ×0.55 → 尾宽 ×0.07、
                //    **透明度全程 0.85/0.95**）发现这里原来衰减得太狠：
                //    宽度到尾部只剩 **4%**、Gradient 透明度尾部 **0.05** ⇒ 画面上只剩一个亮点，
                //    尾巴几乎看不见（预览里是一条"大致等亮、渐细"的暖色带子）。
                //    现在按预览的比例来：宽度 头 1.00 → 中 0.72 → 尾 0.16、
                //    透明度 头 **0.95 → 尾 0.45**（预览那条带子的透明度是**全程** 0.85，
                //    尾部再压到 0.05 就等于"只有头、没有尾巴"）。
                var curve = new Curve();
                curve.AddPoint(new Vector2(0f, 1f));
                curve.AddPoint(new Vector2(0.4f, 0.72f));
                curve.AddPoint(new Vector2(1f, 0.16f));

                var grad = new Gradient();
                grad.SetColor(0, TintColor(color, gain, 0.95f));
                grad.SetColor(1, TintColor(color, gain, 0.45f));

                return new Line2D
                {
                    Width = width,
                    DefaultColor = Colors.White,
                    Gradient = grad,
                    WidthCurve = curve,
                    JointMode = Line2D.LineJointMode.Round,
                    BeginCapMode = Line2D.LineCapMode.Round,
                    EndCapMode = Line2D.LineCapMode.Round,
                    Material = AddMaterial(),
                };
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
