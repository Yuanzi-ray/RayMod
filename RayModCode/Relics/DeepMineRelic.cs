using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Relics;

/// <summary>
/// 「下矿」的<b>升级版</b>：深矿。回合开始时层数 +3，并且<b>每回合多抽一张牌</b>。
///
/// <para><b>怎么拿到它</b>（用户 2026-10-04 拍板）：<b>原版遗物「欧洛巴斯之触」</b>
/// （<c>TouchOfOrobas</c>，由第二幕 <c>Orobas</c> 先古事件发放，和「古老牙齿」在同一个选项池）
/// 会把玩家的初始遗物<b>原地替换</b>成升级版 —— 映射声明在 <see cref="MineRelic"/> 上
/// （<c>[RegisterTouchOfOrobasRefinement(typeof(DeepMineRelic))]</c>，
/// RitsuLib 的补丁接管原版 <c>TouchOfOrobas.GetUpgradedStarterRelic</c>）。
/// <b>不需要自己的事件或遗物</b>。</para>
///
/// <para><b>⚠️ 它必须继续承担「下矿」的挂载点职责</b>：原版是
/// <c>RelicCmd.Replace(旧, 新)</c>（<c>TouchOfOrobas.cs:154-161</c>），换过去之后旧遗物就没了，
/// 而"层数/弹药/沙地兽三套系统的每场战斗复位 + 回合开始 +3 层"全靠那个挂载点。
/// 所以这里<b>不复制逻辑</b>，直接转调 <see cref="MineRelic.RunTurnStart"/> /
/// <see cref="MineRelic.RunRewardTime"/>（同源，坑 31）。</para>
///
/// <para><b>稀有度照抄原版同类遗物</b>：原版的升级初始遗物（<c>RingOfTheDrake</c> /
/// <c>BlackBlood</c> 等）都是 <c>RelicRarity.Starter</c>，而且都在 <c>EventRelicPool</c> 里。
/// 这里同理：<c>[RegisterRelic]</c> 是<b>为了让模型进 ModelDb</b>
/// （RitsuLib 解析精炼目标时要 <c>ModelDb.GetByIdOrNull</c>，不注册就解析不到、
/// 玩家会拿到兜底的 <c>Circlet</c>），而 <c>Starter</c> 稀有度让它<b>不可能被随机抽到</b>
/// —— <c>RelicFactory.RollRarity</c>（<c>RelicFactory.cs:85-94</c>）只会掷出
/// Common / Uncommon / Rare，永远不会是 Starter。同时 <c>MerchantCost</c> = 999999999
/// （<c>RelicModel.cs:311</c>）也买不到。</para>
/// </summary>
[RegisterRelic(typeof(RayModRelicPool))]
public sealed class DeepMineRelic : ModRelicTemplate
{
    /// <summary>
    /// Starter：和原版的升级初始遗物一致（<c>RingOfTheDrake</c> / <c>BlackBlood</c>）。
    /// ⚠️ 这个稀有度同时保证了它不会被随机抽到（<c>RollRarity</c> 掷不出 Starter）。
    /// </summary>
    public override RelicRarity Rarity => RelicRarity.Starter;

    /// <summary>
    /// 每回合多抽几张。<b>用 <c>ModifyHandDraw</c> 而不是"回合开始再抽一张"</b>：
    /// 那才是原版"每回合多抽牌"的钩子（<c>RingOfTheSnake</c> / <c>RingOfTheDrake</c> /
    /// <c>BagOfPreparation</c> 全是它），而且它改的是<b>本回合抽牌数本身</b>，
    /// 所以第 6 张牌是和另外 5 张一起抽上来的，不是抽完再补一张。
    /// </summary>
    private const int ExtraCardsPerTurn = 1;

    /// <summary>
    /// 占位图标：和「下矿」共用同一张（理由见 <see cref="MineRelic.AssetProfile"/>）。
    /// </summary>
    public override RelicAssetProfile AssetProfile => new(
        IconPath: $"{Entry.ResPath}/images/relics/{GetType().Name}.png",
        IconOutlinePath: $"{Entry.ResPath}/images/relics/{GetType().Name}.png",
        BigIconPath: $"{Entry.ResPath}/images/relics/{GetType().Name}.png");

    /// <summary>
    /// <b>每回合多抽一张牌</b>（用户 2026-10-04 给出的升级效果）。
    /// 照抄原版写法：不是自己的抽牌请求直接放行。
    /// </summary>
    public override decimal ModifyHandDraw(Player player, decimal count)
    {
        if (player != base.Owner)
        {
            return count;
        }

        return count + ExtraCardsPerTurn;
    }

    /// <summary>
    /// 回合开始：<b>照旧做挂载点该做的事</b>（三套系统复位 + 层数 +3 + 「全息建模」兜底）。
    /// ⚠️ 换到这块遗物之后，<see cref="MineRelic"/> 的那个钩子就再也不会被调用了。
    /// </summary>
    public override async Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player)
    {
        // ★★ 2026-10-13：同 `MineRelic.AfterPlayerTurnStart` —— **三套系统（层数/弹药/沙地兽配额）
        //    发给每一个成员**，所以这一段不加 owner 过滤、必须排在过滤之前。
        //    非莱伊那两个开关 `announceHud: false, clearBus: false` 的理由见那边。
        if (base.Owner != player)
        {
            await Depth.DepthCmd.EnsurePower(
                choiceContext, player, cardSource: null, announceHud: false, clearBus: false);
            await Ammo.AmmoCmd.EnsurePower(
                choiceContext, player, cardSource: null, announceHud: false, clearBus: false);
            await Beasts.BeastCmd.EnsurePower(choiceContext, player, cardSource: null);

            return;
        }

        // ★★ 2026-10-13 联机适配：同 `MineRelic.AfterPlayerTurnStart` —— 钩子会按
        //    "每一个玩家的回合"各跑一遍，不判归属就会**每回合发两次层数**（2 人局）。
        //    ⚠️ 遗物的 `Owner` 直接就是 `Player`。
        await MineRelic.RunTurnStart(choiceContext, player);
    }

    /// <summary>「全息建模」的战斗奖励结算点（与 <see cref="MineRelic"/> 同源）。</summary>
    public override async Task BeforeCombatRewardOffered(RewardsSet rewards, CombatRoom room)
    {
        if (base.Owner == null)
        {
            return;
        }

        await MineRelic.RunRewardTime(base.Owner, rewards);
    }

    /// <summary>动画分派（与 <see cref="MineRelic"/> 同源）：换到这块遗物之后也要继续工作。</summary>
    public override async Task BeforeCardPlayed(CardPlay cardPlay)
    {
        await MineRelic.RunCardPlayed(cardPlay);
    }

    /// <summary>出牌结束清场（与 <see cref="MineRelic"/> 同源）。</summary>
    public override Task AfterCardPlayed(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        MineRelic.RunCardPlayFinished(cardPlay);
        return Task.CompletedTask;
    }
}
