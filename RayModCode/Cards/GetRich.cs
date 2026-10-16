using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 稀有技能「发家致富」：<b>0 费</b>，获得 3 点能量；<b>随机 2 张手牌在打出前费用翻倍</b>。升级后能量 3 → 5。
///
/// <para><b>2026-10-10 用户要求：随机 1 张 → 随机 2 张</b>（原话"发家致富改成随机将2张手牌费用翻倍"）。
/// 口径两条：① <b>去重</b> —— 从当前手牌里随机挑 2 张<b>不同的</b>牌（每挑中一张就从候选里移除），
/// 同一张牌不会被翻两次，否则"2 张"名不副实；② <b>先过滤再挑</b> —— 候选只收当前费用大于 0 的牌，
/// 所以"随机 2 张"名副其实（0 费牌翻倍无意义、还会多挂一个 0 费修正，见 <see cref="DoubleCost"/>）。
/// 手牌不足 2 张时能翻几张翻几张；一张能翻的都没有就直接返回，不抛异常。
/// 张数 2 是<b>结构常量</b>（<see cref="DoubledCardCount"/>），不提升为 DynamicVar ——
/// 与"0 费跳过"同理，它不随升级变化。</para>
///
/// <para><b>用户拍板⑩：费用判定用「当前费用」</b> —— 所以翻倍是
/// <c>EnergyCost.SetUntilPlayed(GetResolved() * 2)</c>（含全部修正的当前费用 ×2），
/// 不是基础费用 ×2。一张已经被别的效果减到 1 费的牌，翻倍后是 2 费。</para>
///
/// <para>"在打出前" = <c>SetUntilPlayed</c>（失效条件只有"打出"）——
/// 与本工程「追光者之瞳」的 0 费写法同源（那里踩过 <c>SetToFreeThisTurn</c> / <c>SetThisCombat</c>
/// 两个坑，见坑 38）。所以这张牌翻倍出来的费用<b>不会被回合结束清掉</b>，
/// 那张牌下回合抽到还是双倍价。</para>
///
/// <para>随机流用 <c>RunState.Rng.CombatCardSelection</c>（"随机选牌"的语义流，
/// 与「污染」一致）—— 别用 <c>CombatTargets</c>，那会污染随机目标序列。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class GetRich : ModCardTemplate
{
    private const int BaseEnergyCost = 0;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    /// <summary>要翻倍的手牌张数（用户 2026-10-10 拍板：1 → 2）。
    /// 结构常量、不是 DynamicVar —— 它不随升级变化（升级只改能量 3 → 5）。</summary>
    private const int DoubledCardCount = 2;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new EnergyVar(3)
    ];

    public GetRich() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PlayerCmd.GainEnergy(DynamicVars.Energy.BaseValue, Owner);

        // 候选 = 当前手牌里**真正能被翻倍**的牌（当前费用大于 0）—— 先过滤再挑，
        // 保证"随机 2 张"名副其实（0 费牌 DoubleCost 会跳过，挑到它等于白挑一张）。
        // ⚠️ 先拷一份 List 再做去重：Cards 是牌堆的活集合，绝不能就地改它。
        var candidates = PileType.Hand.GetPile(Owner).Cards
            .Where(card => card.EnergyCost.GetResolved() > 0)
            .ToList();

        if (candidates.Count == 0)
        {
            // 手牌空了（例如这张牌是最后一张）/ 手里全是 0 费牌 ——
            // 不是错误，只是这次没有可加价的牌。
            return;
        }

        // 手牌不足 2 张时能翻几张翻几张（1 张就翻 1 张）。
        int picks = Math.Min(DoubledCardCount, candidates.Count);

        for (int i = 0; i < picks; i++)
        {
            var picked = Owner.RunState.Rng.CombatCardSelection.NextItem(candidates);

            if (picked == null)
            {
                return;
            }

            // 去重：挑中即刻从候选里移除 —— 同一张牌不会被翻两次
            //（原版 SlimesWeak.GenerateMonsters 挑两只不同史莱姆就是这写法）。
            candidates.Remove(picked);

            DoubleCost(picked);
        }
    }

    /// <summary>把一张牌"打到之前费用翻倍"（按当前费用翻）。</summary>
    internal static void DoubleCost(CardModel card)
    {
        int current = card.EnergyCost.GetResolved();

        // 0 费牌翻倍没有意义，还会在卡面上多挂一个 0 费修正 → 跳过。
        if (current <= 0)
        {
            return;
        }

        card.EnergyCost.SetUntilPlayed(current * 2);
    }

    protected override void OnUpgrade()
    {
        // 升级：能量 3 → 5（设计稿："获得能量提升至5"）。
        DynamicVars.Energy.UpgradeValueBy(2m);
    }
}
