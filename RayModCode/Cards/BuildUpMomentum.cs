using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Ammo;
using RayMod.Beasts;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 稀有技能「蓄势」：<b>一口气把三套地基各推一格</b> —— 获得 2 点能量、弹药上限 +3、
/// 弹药 +3、层数 +5、召唤 1 只沙地兽、抽 1 张牌。<b>消耗</b>。升级后抽 2 张。
///
/// <para>它是"启动牌"：0 费净赚 2 能量，代价是消耗掉自己。
/// 三种资源同时给，正好对应本角色三条独立的资源线（层数 / 弹药 / 沙地兽）。</para>
///
/// <para>⚠️ <b>加弹药的顺序</b>：先 <see cref="AmmoCmd.ModifyCap"/> 抬上限，
/// 再 <see cref="AmmoCmd.AddAmmo"/> 加弹药 —— 反过来的话新加的 3 点会被旧上限夹掉。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class BuildUpMomentum : ModCardTemplate, ISandBeastCard, IAmmoCard, IDepthCard
{
    private const int BaseEnergyCost = 0;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new EnergyVar(2),
        new CardsVar("Cap", 3),
        new CardsVar("Ammo", 3),
        new DynamicVar("DepthGain", 5m),
        new CardsVar("Summon", 1),
        new CardsVar("Draw", 1)
    ];

    /// <summary>消耗词条（设计稿明确写了）。</summary>
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
    [
        CardKeyword.Exhaust
    ];

    public BuildUpMomentum() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PlayerCmd.GainEnergy(DynamicVars.Energy.BaseValue, Owner);

        // 上限先抬，弹药后加。
        AmmoCmd.ModifyCap(Owner, DynamicVars["Cap"].IntValue);
        AmmoCmd.AddAmmo(Owner, DynamicVars["Ammo"].IntValue);

        await DepthCmd.AddDepth(
            choiceContext, Owner, DynamicVars["DepthGain"].IntValue, cardSource: this);

        await BeastCmd.Summon(choiceContext, Owner, DynamicVars["Summon"].IntValue, this);

        await CardPileCmd.Draw(choiceContext, DynamicVars["Draw"].BaseValue, Owner);
    }

    protected override void OnUpgrade()
    {
        // 升级：抽 1 → 2（设计稿："抽牌数提升至2"）。
        DynamicVars["Draw"].UpgradeValueBy(1m);
    }
}
