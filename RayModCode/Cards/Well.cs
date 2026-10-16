using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 普通卡「井」：获得 7 点格挡；在<b>层数 +5 和层数 -5 之间随机</b>。<b>保留</b>。
/// 升级后<b>可以选择</b>层数 +5 或者 -5（不再是随机），仍然保留。
///
/// <para>升级版的选择用 <c>CardSelectCmd.FromChooseACardScreen</c>
///（原版"从若干张展示牌里选一张"的界面，上限 3 张）——
/// 展示用的两张牌是 <see cref="WellDeeper"/> / <see cref="WellShallower"/>，
/// 它们只是<b>选项的载体</b>（不进奖励池、不进图鉴、不会被真的打出）。</para>
///
/// <para>为什么这个选择有意义：层数不是越高越好 —— ×× 层的减益之外，
/// 「去咧嘴谷」「旅行」这类牌要求层数<b>小于</b> 9 才能打出，
/// 所以降层数是一个真实选项。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Well : ModCardTemplate, IDepthCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Common;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    public override bool GainsBlock => true;

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new BlockVar(7m, ValueProp.Move),
        new CardsVar("Depth", 5)
    ];

    /// <summary>保留词条（设计稿明确写了，升级后仍是保留）。</summary>
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
    [
        CardKeyword.Retain
    ];

    public Well() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await CreatureCmd.GainBlock(Owner.Creature, DynamicVars.Block, cardPlay);

        int step = DynamicVars["Depth"].IntValue;

        if (IsUpgraded)
        {
            // 升级版：给玩家看两张"选项牌"，按选中的那张决定方向。
            var deeper = CombatState.CreateCard<WellDeeper>(Owner);
            var shallower = CombatState.CreateCard<WellShallower>(Owner);

            var picked = await CardSelectCmd.FromChooseACardScreen(
                choiceContext, new CardModel[] { deeper, shallower }, Owner);

            await DepthCmd.AddDepth(
                choiceContext, Owner, picked == shallower ? -step : step, cardSource: this);

            return;
        }

        // 未升级：±step 随机（用战斗内卡牌随机源，保持同一颗种子的可复现性）。
        int delta = Owner.RunState.Rng.CombatCardSelection.NextItem(new[] { step, -step });

        await DepthCmd.AddDepth(choiceContext, Owner, delta, cardSource: this);
    }

    protected override void OnUpgrade()
    {
        // 升级：随机 → 可选。层数步长维持 5，格挡维持 7。
        // 行为差别靠 IsUpgraded 分支处理，这里不需要改数值。
    }
}
