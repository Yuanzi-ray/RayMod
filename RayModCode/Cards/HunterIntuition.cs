using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Beasts;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 普通卡「猎人直觉」：获得 6 点格挡，给予敌人 1 层<b>虚弱</b>；
/// 若该敌人被沙地兽<b>标记</b>，则给予 2 层。升级后格挡提升至 8。
///
/// <para>它是"格挡 + 减益"的防守牌，也是唯一一张<b>吃沙地兽印记</b>的普通卡 ——
/// 先召唤沙地兽挂上印记，这张牌的价值就翻倍。</para>
///
/// <para>印记判定用 <see cref="SandBeastMarkPower"/>（印记就挂在敌人身上，
/// 见交接文档坑 11：判"敌人在不在印记里"只看这一个 Power，别再加条件）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class HunterIntuition : ModCardTemplate, ISandBeastCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Common;
    private const TargetType CardTarget = TargetType.AnyEnemy;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    public override bool GainsBlock => true;

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new BlockVar(6m, ValueProp.Move),
        new CardsVar("Weak", 1),
        new CardsVar("MarkedWeak", 2)
    ];

    public HunterIntuition() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        await CreatureCmd.GainBlock(Owner.Creature, DynamicVars.Block, cardPlay);

        int weak = cardPlay.Target.HasPower<SandBeastMarkPower>()
            ? DynamicVars["MarkedWeak"].IntValue
            : DynamicVars["Weak"].IntValue;

        await PowerCmd.Apply<WeakPower>(
            choiceContext, cardPlay.Target, weak, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：格挡 6 → 8（虚弱层数不变）。
        DynamicVars.Block.UpgradeValueBy(2m);
    }
}
