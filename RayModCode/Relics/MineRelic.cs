using System;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名（和 DepthPower 一样）。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Relics;

/// <summary>
/// 初始遗物：下矿。回合开始时层数 +4。
///
/// 这个遗物同时承担一个<b>结构性职责</b>：它是层数与弹药两个 Power 的挂载点，
/// 也是"每场战斗归零"的执行点。
///
/// <para>原版的 Power 必须在战斗中被 Apply 才会有实例，而<b>遗物收不到
/// <c>BeforeCombatStart</c></b>（<c>RunState.IterateHookListeners</c> 在
/// <c>childCombatState != null</c> 时不会把遗物放进监听列表），
/// 所以"战斗开始时初始化"这件事只能靠 <c>AfterPlayerTurnStart</c> 在第一次触发时补做 ——
/// 判据就是"Power 还不存在"。</para>
///
/// <para><b>2026-10-04：它还是原版遗物「欧洛巴斯之触」的精炼源。</b>
/// <c>[RegisterTouchOfOrobasRefinement(typeof(DeepMineRelic))]</c> 让那条"把初始遗物升级"
/// 的原版路线认得「下矿」：欧洛巴斯之触（第二幕 Orobas 事件发放）会把这块遗物
/// <b>原地替换</b>成升级版 <see cref="DeepMineRelic"/>（多一条"每回合多抽一张牌"）。
/// ⚠️ 所以两个遗物<b>共用</b> <see cref="RunTurnStart"/> / <see cref="RunRewardTime"/>
/// 这两个静态方法 —— 换过去之后挂载点职责不能断（层数/弹药/沙地兽的每场复位全靠它）。</para>
/// </summary>
[RegisterRelic(typeof(RayModRelicPool))]
[RegisterCharacterStarterRelic(typeof(RayModCharacter))]
[RegisterTouchOfOrobasRefinement(typeof(DeepMineRelic))]
public sealed class MineRelic : ModRelicTemplate
{
    /// <summary>回合开始时增加的层数（2026-10-10 用户要求：+3 → +4）。</summary>
    /// <remarks>
    /// ⚠️ 升级版「深矿」<see cref="DeepMineRelic"/> 转调的是同一份 <see cref="RunTurnStart"/>，
    /// 因此<b>两块遗物共用这一个常量</b> —— 改这里等于两块一起改（用户要的正是这个）。
    /// </remarks>
    private const int DepthPerTurn = 4;

    public override RelicRarity Rarity => RelicRarity.Starter;

    /// <summary>
    /// 占位图标：直接复用模板遗物的图。
    ///
    /// 不新建 MineRelic.png 是刻意的——Godot 的 .import 里带 UID 和导入路径哈希，
    /// 手工造一个容易和资源索引不一致（RitsuLib 的 RITSU013 就会报找不到资源）。
    /// 等有正式美术素材时，把这里换成 res://RayMod/images/relics/MineRelic.png，
    /// 并在 Godot 里正常导入一次即可。
    /// </summary>
    public override RelicAssetProfile AssetProfile => new(
        IconPath: $"{Entry.ResPath}/images/relics/{GetType().Name}.png",
        IconOutlinePath: $"{Entry.ResPath}/images/relics/{GetType().Name}.png",
        BigIconPath: $"{Entry.ResPath}/images/relics/{GetType().Name}.png");

    public override async Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player)
    {
        // ★★ 2026-10-13 用户拍板：**三套系统（层数 / 弹药 / 沙地兽配额）发给每一个成员**。
        //
        //   为什么：原版遗物「棱彩宝石」（`PrismaticGem.cs:46` 把卡池换成
        //   `player.UnlockState.CharacterCardPools ∪ 原池`，而 `UnlockState.CharacterCardPools`
        //   = **所有角色含 mod 角色**）⇒ **非莱伊玩家能抽到莱伊的牌**。
        //   而这三套的"每场复位 + 战斗内载体（Power）"原先**只由莱伊的遗物挂**，
        //   于是非莱伊会掉进半成品状态：
        //     · 弹药没有 `AmmoPower`/`AmmoLedgerPower` ⇒ **不每场复位、上限不夹、账本没有**
        //     · 层数没有 `DepthPower` ⇒ **不每场复位、没有层数惩罚**（惩罚逻辑写在 DepthPower 里）
        //     · 沙地兽没有 `SandBeastQuotaPower` ⇒ 没有配额角标、吃不到印记增伤
        //   ⇒ 这是"想给队友层数/弹药"类**多人卡牌**的前提，必须先补齐。
        //
        //   ★ 这一段**故意不加 owner 过滤、而且必须排在下面那道过滤之前**：
        //     正是靠"莱伊的遗物会收到**每一个玩家**的回合开始钩子"这个特性把系统发出去。
        //     没有莱伊在场的局由 `BeastCmd.Summon` 那次 `EnsurePower` 兜底。
        //
        //   ⚠️ 非莱伊那两个开关必须是 `announceHud: false, clearBus: false`：
        //     · `announceHud` —— 他没有数据源就不该在战斗一开始冒出一块"层数 0 / 弹药 0/1"
        //       的牌子（等真的涨了数值，正常 Push 会自己把计数器建出来）；
        //     · `clearBus` —— `ClearAll()` 是**全局**的，而这条路会在**每个玩家**的回合开始
        //       被走到 ⇒ 队友的回合会把别人刚注册的订阅清掉（「宛如沙石」「入神」
        //       「子弹模式弹幕」静默失效到战斗结束）。那一刀只该由"自己那次"触发。
        if (base.Owner != player)
        {
            await Depth.DepthCmd.EnsurePower(
                choiceContext, player, cardSource: null, announceHud: false, clearBus: false);
            await Ammo.AmmoCmd.EnsurePower(
                choiceContext, player, cardSource: null, announceHud: false, clearBus: false);
            await Beasts.BeastCmd.EnsurePower(choiceContext, player, cardSource: null);

            return;
        }

        // 自己那个回合：原样走 RunTurnStart（三套挂载 + 加层数 + 全息兜底）。
        // ★ 2026-10-13 联机适配：**只认自己那个回合** —— 钩子会按"每一个玩家的回合"
        //   对场上每个监听者各跑一遍 ⇒ 2 人局里「下矿」的 `+{DepthPerTurn} 层`会每回合发两次。
        //   ⚠️ 遗物的 `Owner` 直接就是 `Player`（不是 Creature），所以这里不用解 PetOwner。
        await RunTurnStart(choiceContext, player);
    }

    /// <summary>
    /// <b>三套系统的挂载点</b>的回合开始逻辑（2026-10-04 抽出成静态方法）。
    ///
    /// <para>抽出来的原因：原版遗物「欧洛巴斯之触」会把这块遗物<b>替换</b>成升级版
    /// （<see cref="DeepMineRelic"/>，见 <c>[RegisterTouchOfOrobasRefinement]</c>），
    /// 而升级版<b>必须继续承担挂载点职责</b>（否则换完之后层数/弹药/沙地兽三套系统
    /// 连"每场战斗复位"都不会做了）。两个遗物共用这一份实现，不复制第二份。</para>
    ///
    /// <para>⚠️ <b>顺序必须</b>是：先 Ensure 再 AddDepth。
    /// 原因：Ensure 里带着"本场战斗第一次"的归零逻辑
    /// （<c>DepthCmd.EnsurePower</c> / <c>AmmoCmd.EnsurePower</c> 看到 Power 不在位就 ResetForCombat）。
    /// 如果先 AddDepth 再 Ensure，那么：
    ///     归零(0) → +4 = 4 → 再建 Power 时又归零 → 存成 0 → 最后同步成 0
    /// 第一回合的 +4 会被自己吃掉。反过来就不会：
    ///     归零(0) → 建 Power(0) → +4 = 4 → 同步到 Power 显示 4</para>
    /// </summary>
    internal static async Task RunTurnStart(PlayerChoiceContext choiceContext, Player player)
    {
        // ★★★ 2026-10-13：**整段包 try/catch**（本工程铁律：回合开始类钩子抛异常 = 整个游戏卡死）。
        //
        //   这里做的是「三套系统的挂载」+ 加层数 + 全息兜底，链条上有 5 个 await，
        //   每一步都可能因为"存档取不到 / Power 建不出来 / 全息记录坏了"而抛。
        //   而它被 await 在 `CombatManager.SetupPlayerTurn` 中间 ⇒ 一旦抛出去，
        //   **StartTurn 永远完不成**、双方一起卡死在那个回合（联机下是两个人一起挂，
        //   比单机贵得多）。同工程 `Powers\FeranmutsPower.cs` / `AnimalGuidePower.cs`
        //   的回合开始钩子早就包了 try/catch，这里当时漏了。
        //
        //   吞掉之后"本回合这批初始化没做完"，最坏是这一回合少点层数/少个计数器 ——
        //   远好于卡死。下次回合开始还会再来一遍（各 Ensure 幂等）。
        try
        {
            await Depth.DepthCmd.EnsurePower(choiceContext, player, cardSource: null);
            await Ammo.AmmoCmd.EnsurePower(choiceContext, player, cardSource: null);
            await Beasts.BeastCmd.EnsurePower(choiceContext, player, cardSource: null);

            await Depth.DepthCmd.AddDepth(choiceContext, player, DepthPerTurn, cardSource: null);

            // 「全息建模」的兜底：寿命已尽但没被摘掉的复制品（正常路径在奖励时机摘，
            // 不给奖励的战斗会走到这里）。只摘不倒数，不会和奖励时机重复计数。
            await Holo.HoloCmd.SweepExpired(player);
        }
        catch (Exception e)
        {
            Log.Error($"[下矿] 回合开始初始化失败（本回合跳过，**绝不向上抛** —— 抛了整个 StartTurn 会卡死）: {e}");
        }
    }

    /// <summary>
    /// 「全息建模」的<b>结算点</b>（2026-10-04 新增，与 <see cref="RunTurnStart"/> 同样被两个遗物共用）。
    ///
    /// <para><b>为什么是遗物 + 这个钩子</b>：那张牌要在"战斗结束时"让玩家选一个遗物复制，
    /// 而战斗内 Power 在 <c>Player.AfterCombatEnd()</c> 就被清空了，
    /// 遗物则是全程在身（且 <c>IterateHookListeners(null)</c> 的场合才把遗物算作监听者，
    /// 见 <c>RunState.cs:563</c>）；原版遗物 <c>LastingCandy</c> 用的正是这个钩子，
    /// 它被 await 在<b>奖励面板弹出之前</b>（<c>CombatRoom.cs:271</c>），
    /// 所以玩家先选遗物、再看到奖励 —— 时机正合适。</para>
    /// </summary>
    /// <summary>
    /// 「全息建模」的战斗奖励结算点（与 <see cref="MineRelic"/> 同源）。
    /// </summary>
    internal static async Task RunRewardTime(Player owner, RewardsSet rewards)
    {
        // 多人时每个玩家各有一块这个遗物，只处理自己的那一份。
        if (rewards.Player != owner)
        {
            return;
        }

        await Holo.HoloCmd.OnRewardTime(owner);
    }

    /// <summary>
    /// <b>动画分派</b>：记下"当前正在打的牌"，并给技能/能力牌补上施法动画（同源，两个遗物共用）。
    ///
    /// <para>为什么挂在遗物上：遗物确实是战斗内钩子监听者（<c>CombatState.IterateHookListeners</c>
    /// 会加玩家遗物，<c>CombatState.cs:429-436</c>），而 <c>Hook.BeforeCardPlayed</c> 走的正是这条路
    /// （<c>Hook.cs:263-270</c>）。角色自己收不到战斗钩子（<c>CharacterModel.ShouldReceiveCombatHooks => false</c>）。</para>
    /// </summary>
    internal static async Task RunCardPlayed(CardPlay cardPlay)
    {
        await Art.SpineAnimDispatch.OnCardPlayed(cardPlay);
    }

    /// <summary>出牌结束：清掉"当前卡"。</summary>
    internal static void RunCardPlayFinished(CardPlay? cardPlay)
    {
        Art.SpineAnimDispatch.OnCardPlayFinished(cardPlay);
    }

    /// <summary>把动画分派接到出牌流程上（见 <see cref="RunCardPlayed"/>）。</summary>
    public override async Task BeforeCardPlayed(CardPlay cardPlay)
    {
        await RunCardPlayed(cardPlay);
    }

    /// <summary>出牌结束清场。</summary>
    public override Task AfterCardPlayed(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        RunCardPlayFinished(cardPlay);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 把「全息建模」的结算接到遗物上（实现见 <see cref="RunRewardTime"/>）。
    /// 升级版 <see cref="DeepMineRelic"/> 覆写的是同一个方法，转调同一个实现。
    /// </summary>
    public override async Task BeforeCombatRewardOffered(RewardsSet rewards, CombatRoom room)
    {
        if (base.Owner == null)
        {
            return;
        }

        await RunRewardTime(base.Owner, rewards);
    }

    private static readonly Logger Log = new("RayMod.Relics", LogType.Generic);

    /// <summary>
    /// 「层数 &gt;60，战斗结束 50% 给遗物」的<b>发放点</b>（2026-10-04 新增）。
    ///
    /// <para><b>为什么改成走奖励界面</b>：原来是在 <c>DepthPower.AfterCombatEnd</c> 里
    /// 直接 <c>RelicCmd.Obtain</c> 把遗物塞进遗物栏 —— 玩家只看到一闪而过的动画 + 音效，
    /// <b>没有任何"我拿到了什么"的确认</b>。用户实测时的第一反应就是"遗物是坏的"。
    /// 改成奖励项之后，它和精英战的遗物一样出现在结算界面里：可见、可跳过、有名字。</para>
    ///
    /// <para><b>为什么挂在遗物上而不是 DepthPower 上</b>：
    /// <c>RewardsSet.Offer()</c> 触发 <c>Hook.ModifyRewards</c> 的时机在战斗<b>彻底结束之后</b>，
    /// 而 <c>CombatManager.cs:1004</c> 的 <c>Player.AfterCombatEnd()</c> 已经把战斗内的 Power
    /// 全部清掉了 —— 写在 Power 上的 <c>TryModifyRewards</c> 永远不会被调用。
    /// 遗物是全程在身的，所以原版也在遗物上做这件事（<c>BlackStar</c> / <c>WongosMysteryTicket</c>）。
    /// 掷骰仍然留在 <c>DepthPower.AfterCombatEnd</c>（那时 Power 还活着），
    /// 结果通过 <c>DepthState.PendingRelicReward</c> 传过来。</para>
    /// </summary>
    public override bool TryModifyRewards(Player player, List<Reward> rewards, AbstractRoom? room)
    {
        // 只在战斗房间的奖励里插（事件/宝箱那些没有"层数"这回事）。
        if (room is not CombatRoom)
        {
            return false;
        }

        Depth.DepthState state;

        try
        {
            state = Depth.DepthRunData.Store.Get(player);
        }
        catch (Exception e)
        {
            // 取不到跑团存档（理论上到不了这里，纯防御）。别静默吞掉。
            Log.Warn($"[下矿] TryModifyRewards 取不到层数状态: {e.Message}");
            return false;
        }

        // 领一次就清掉（重置点本来在下场战斗开始时，这里立刻清更稳）。
        bool added = false;

        if (state.PendingRelicReward)
        {
            state.PendingRelicReward = false;

            // 无参构造 = 随机抽一个（和原来 RelicFactory.PullNextRelicFromFront 同源）。
            rewards.Add(new RelicReward(player));
            added = true;

            Log.Info("[下矿] 遗物档：已把遗物奖励加入本场战斗的奖励列表");
        }

        // ★ 2026-10-09：「源源不断」斩杀敌人的奖励。
        //    与上面那条**刻意独立** —— 两条来源同时命中就该给两个奖励项，
        //    共用一个字段会静默吞掉一个（见 DepthState.PendingKillRelicReward 的注释）。
        if (state.PendingKillRelicReward)
        {
            state.PendingKillRelicReward = false;

            rewards.Add(new RelicReward(player));
            added = true;

            Log.Info("[源源不断] 斩杀遗物：已把遗物奖励加入本场战斗的奖励列表");
        }

        return added;
    }
}
