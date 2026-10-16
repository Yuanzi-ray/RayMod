using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Ammo;
using RayMod.Characters;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 稀有能力「填装形态」：<b>你每打出一张牌，就 +1 点弹药与 +1 点弹药上限</b>。
/// 升级后每次 +2。
///
/// <para>它把"出牌"直接变成弹药发动机：一场战斗打十几张牌就是十几点上限，
/// 配合「以弹为盾」（格挡 = 上限）和「无限子弹」会滚得很快。</para>
///
/// <para>逻辑在 <see cref="LoadingPosturePower"/>（那里的顺序注释很关键：
/// 必须先抬上限再加弹药）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class LoadingPosture : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 3;
    private const CardType CardKind = CardType.Power;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CardsVar("PerCard", 1)
    ];

    public LoadingPosture() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<LoadingPosturePower>(
            choiceContext, Owner.Creature, DynamicVars["PerCard"].BaseValue, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：每张牌 +1/+1 → +2/+2（设计稿）。
        DynamicVars["PerCard"].UpgradeValueBy(1m);
    }
}
