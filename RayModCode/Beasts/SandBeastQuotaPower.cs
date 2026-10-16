using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;

namespace RayMod.Beasts;

/// <summary>
/// 挂在<b>玩家</b>身上的沙地兽配额显示与记账。角标数字 = <b>本回合还剩几张牌能吃印记增伤</b>。
///
/// <para><b>为什么角标显示"配额"而不是"沙地兽数量"</b>（用户 2026-10-03 确认）：
/// 沙地兽真的站在战场上，数量直接看得见；配额却完全看不见，而它决定了这回合该先打哪两张牌。
/// 上限写在描述文本里。</para>
///
/// <para><b>记账时序（照抄原版 <c>VigorPower</c> 的快照思路）：</b></para>
/// <list type="number">
/// <item><c>BeforeCardPlayed</c>：清掉"本牌吃过加成"的标记。</item>
/// <item>伤害结算时 <see cref="SandBeastMarkPower.ModifyDamageMultiplicative"/> 若真的加成了，
/// 就调 <see cref="NoteBonusConsumed"/> 把标记置上。</item>
/// <item><c>AfterCardPlayed</c>：标记还立着就扣 1 点配额。</item>
/// </list>
/// <para><b>⚠️ 扣减必须放在 <c>AfterCardPlayed</c>，不能放 <c>BeforeCardPlayed</c></b> ——
/// 一张牌的伤害是在它的 <c>OnPlay</c> 里算的，放前面会让当前这张牌自己吃不到加成。
/// 另外一张牌只扣 1 点，所以"多段攻击"和"群体攻击"都只算一张牌。</para>
/// </summary>
[RegisterPower]
public sealed class SandBeastQuotaPower : PowerModel, IModPowerAssetOverrides
{
    /// <summary>
    /// 图标（2026-10-04 用户提供：桌面上的「头像_召唤物_沙地兽.png」，150×150）。
    /// 本 Power 的标题就是「沙地兽」，所以这颗图标就是玩家在能力栏里看到的"沙地兽"。
    ///
    /// <para>挂法说明：我们的 Power 是裸 <c>PowerModel</c>（不是 <c>ModPowerTemplate</c>），
    /// 所以走 RitsuLib 的 <see cref="IModPowerAssetOverrides"/> 接口 —— 它的
    /// <c>PowerIconPathPatch</c> 补的就是 <c>PowerModel.IconPath</c> / <c>PackedIconPath</c> 两个取值器。
    /// 三个成员都要给，否则补丁拿不到路径（日志里就是 <c>Missing sprite 'ray_mod_power_sand_beast_quota_power'</c>）。</para>
    /// </summary>
    private static readonly PowerAssetProfile IconProfile = new(
        IconPath: $"{Entry.ResPath}/images/powers/SandBeastQuotaPower.png",
        BigIconPath: $"{Entry.ResPath}/images/powers/SandBeastQuotaPower.png");

    public PowerAssetProfile AssetProfile => IconProfile;

    public string? CustomIconPath => IconProfile.IconPath;

    public string? CustomBigIconPath => IconProfile.BigIconPath;

    /// <summary>本张牌是否已经吃到过印记增伤（用于 AfterCardPlayed 时决定要不要扣配额）。</summary>
    private bool _bonusConsumedThisCard;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示剩余配额。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    /// <summary>本回合是否还有配额（由 <see cref="SandBeastMarkPower"/> 调用）。</summary>
    internal bool CanSpendQuota(BeastState state) => state.QuotaUsedThisTurn < state.Cap;

    /// <summary>记下"这张牌吃到了加成"（幂等，一张牌多段命中只会记一次）。</summary>
    internal void NoteBonusConsumed() => _bonusConsumedThisCard = true;

    /// <summary>把 State 里的剩余配额同步到角标。</summary>
    internal void SyncFromState()
    {
        var state = ResolveState();

        if (state != null)
        {
            SetAmount(state.QuotaRemaining, silent: true);
        }

        InvokeDisplayAmountChanged();
    }

    public override Task BeforeCardPlayed(CardPlay cardPlay)
    {
        // ★★ 2026-10-13 联机适配：**只认自己打出的牌**。
        //    这个钩子会被**每一个**战斗内监听者、**每一次**任意玩家的出牌各调一遍；
        //    不判归属的话，队友打出任意一张牌都会把我"本张牌是否吃过印记加成"的记账清掉
        //    ⇒ 我那张牌的 AfterCardPlayed 看到标志是 false ⇒ **配额不被扣**（白吃一次增伤）。
        if (cardPlay?.Card?.Owner != (base.Owner?.Player ?? base.Owner?.PetOwner))
        {
            return Task.CompletedTask;
        }

        _bonusConsumedThisCard = false;
        return Task.CompletedTask;
    }

    public override async Task AfterCardPlayed(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // ★★ 2026-10-13 联机适配：**只结算自己打出的牌**。
        //    `_bonusConsumedThisCard` 是"本张牌是否吃过印记加成"的记账，属于**本 Power 的所有者**；
        //    这个钩子会为每一个玩家的每次出牌各跑一遍 ⇒ 队友的牌会来消费**我**这份记账
        //    （配合上面 BeforeCardPlayed 的归属判据一起看：两道门必须成对，否则记账会错位）。
        if (cardPlay?.Card?.Owner != (base.Owner?.Player ?? base.Owner?.PetOwner))
        {
            return;
        }

        if (!_bonusConsumedThisCard)
        {
            return;
        }

        _bonusConsumedThisCard = false;

        var state = ResolveState();

        if (state == null || state.QuotaUsedThisTurn >= state.Cap)
        {
            return;
        }

        state.QuotaUsedThisTurn++;
        SyncFromState();

        BeastRunData.Log.Info(
            $"[沙地兽] 配额已用 {state.QuotaUsedThisTurn}/{state.Cap}，本回合还剩 {state.QuotaRemaining} 张牌可吃增伤");
    }

    /// <summary>
    /// 回合开始：<b>把"本回合"记账滚到新回合号，并刷新角标</b>。
    ///
    /// <para><b>★ 2026-10-11 修 bug 后这里不再是"复位点"</b>：
    /// 复位（清零）现在由 <c>BeastCmd.RollTurnOver</c> 负责，读写两侧都按
    /// <c>PlayerCombatState.TurnNumber</c> 滚账（见 <see cref="BeastState.CountedTurn"/>）。
    /// 原因：本钩子跑在<b>抽牌之后</b>（<c>CombatManager.cs:921 抽牌 → :923 派发</c>），
    /// 谁在"回合开始流程中间"召唤一只、或读一次，都会踩到旧的清零时机
    /// （「沙地兽的拯救」的"额外打出次数"就是这么错的）。
    ///
    /// <para>所以本方法现在的职责只剩两条：① 让判据日志标明"回合开始钩子触发"；
    /// ② 让角标在回合一开始就刷新。删掉它也不会算错 —— 但别删，日志靠它定位。</para>
    /// </summary>
    public override Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player)
    {
        // 本 Power 永远挂在玩家自己的生物上，直接比对这个 player 就够。
        if ((base.Owner?.Player ?? base.Owner?.PetOwner) != player)
        {
            return Task.CompletedTask;
        }

        BeastCmd.RollTurnForTurnStart(player);
        SyncFromState();

        return Task.CompletedTask;
    }

    /// <summary>
    /// 取玩家的沙地兽状态。战斗外（主菜单预览）拿不到存档，返回 null 而不是抛异常。
    /// </summary>
    private BeastState? ResolveState()
    {
        return BeastCmd.TryGetState(base.Owner?.Player ?? base.Owner?.PetOwner);
    }
}
