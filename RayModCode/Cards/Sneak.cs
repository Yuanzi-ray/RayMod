using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Ammo;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 普通卡「潜行」：造成 10 点伤害，弹药 +2。升级后：弹药 +3。
///
/// <para>「弹药 +N」的标准范式（后面 快速装填 / 无限子弹 等都照这个写）。
/// 注意上限硬夹 —— 弹药满 7 时再 +2 只会变成 7，不溢出。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Sneak : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Common;
    private const TargetType CardTarget = TargetType.AnyEnemy;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DamageVar(10m, ValueProp.Move),
        new CardsVar("Ammo", 2)
    ];

    public Sneak() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        await DamageCmd.Attack(DynamicVars.Damage.BaseValue)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_slash", null, "slash_attack.mp3")
            .Execute(choiceContext);

        AmmoCmd.AddAmmo(Owner, DynamicVars["Ammo"].IntValue);
    }

    protected override void OnUpgrade()
    {
        DynamicVars["Ammo"].UpgradeValueBy(1m);
    }
}
