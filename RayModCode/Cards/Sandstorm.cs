using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Beasts;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见技能「沙尘暴」：获得 20 点格挡，<b>沙地兽上限 -1</b>，获得 2 点能量。升级后格挡 23。
///
/// <para>降上限走 <c>BeastCmd.ModifyCap(负数)</c> —— 用户规则 #7 已经实现：
/// <b>降上限导致场上数量超出时，会随机移除多出来的沙地兽，它们的印记一并消失</b>（<c>BeastCmd.cs:277</c> 附近）。</para>
///
/// <para>上限被夹在 [0, 6]，所以最多降到 0（此时 <c>BeastCmd.Summon</c> 会一只都召不出来）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Sandstorm : ModCardTemplate, ISandBeastCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    public override bool GainsBlock => true;

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new BlockVar(20m, ValueProp.Move),
        new EnergyVar(2),
        // 上限削减量走变量（原先是写在 OnPlay 里的字面 -1 → 卡面只能写死"上限-1"，改不动）。
        // 存**正数** 1：卡面显示的"1"就是这个值，减号留在文案里（见作业说明 §4.3）。
        new DynamicVar("CapLoss", 1)
    ];

    public Sandstorm() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await CreatureCmd.GainBlock(Owner.Creature, DynamicVars.Block, cardPlay);

        // 上限 -1（超出的沙地兽会被随机移除，见用户规则 #7）。削减量读变量 CapLoss。
        await BeastCmd.ModifyCap(choiceContext, Owner, -DynamicVars["CapLoss"].IntValue);

        await PlayerCmd.GainEnergy(DynamicVars.Energy.IntValue, Owner);
    }

    protected override void OnUpgrade()
    {
        // 升级：格挡 20 → 23（设计稿："获得格挡提升至23"）。上限与能量不变。
        DynamicVars.Block.UpgradeValueBy(3m);
    }
}
