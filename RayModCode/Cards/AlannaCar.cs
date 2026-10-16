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
/// 普通卡「阿兰娜的车」：造成 10 点伤害；如果你有弹药，则额外造成 10 点伤害。
/// 升级后额外伤害提升至 13。
///
/// <para>判据只看"有没有弹药"（<see cref="AmmoCmd.HasAmmo"/>，&gt; 0 即可），
/// <b>不消耗</b>弹药 —— 它是纯收益的炸弹，和「狙击」「霰弹枪」那种烧弹药的牌不同。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class AlannaCar : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 2;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Common;
    private const TargetType CardTarget = TargetType.AnyEnemy;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CalculationBaseVar(10m),
        new ExtraDamageVar(10m),
        // 乘数：有弹药就是 1（⇒ 10 + 10 = 20），没有就是 0（⇒ 10）。
        // ⚠️ 用 card.Owner，不能捕获 this（WithMultiplier 要求 lambda 是 static 的）。
        new CalculatedDamageVar(ValueProp.Move)
            .WithMultiplier((card, _) => AmmoCmd.HasAmmo(card.Owner) ? 1m : 0m)
    ];

    public AlannaCar() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        // 伤害 = 基准 10 + 额外 10 × (有弹药 ? 1 : 0)，卡面显示的就是这个实算值。
        await DamageCmd.Attack(DynamicVars.CalculatedDamage)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_heavy_blunt", null, "heavy_blunt_attack.mp3")
            .Execute(choiceContext);
    }

    protected override void OnUpgrade()
    {
        // 升级：额外伤害 10 → 13（设计稿："额外造成伤害提升至13"）。
        DynamicVars.ExtraDamage.UpgradeValueBy(3m);
    }
}
