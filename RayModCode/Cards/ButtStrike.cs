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
/// 普通攻击「枪托打击」：造成 22 点伤害，升级后 28。
/// <b>只能在弹药为 0 时打出</b>。
///
/// <para><b>★ 2026-10-17 平衡调整（用户口径）</b>：</para>
/// <list type="bullet">
/// <item><b>「只有无弹药时可以打出」</b> —— 判据改用 <see cref="AmmoCmd.HasAmmo"/>：
/// 还有弹药时这张牌不可打出（原版同款 <see cref="IsPlayable"/> 钩子，牌会显示成不可打出的样式）。</item>
/// <item><b>「去掉场限制」</b> —— 删掉原来"每场战斗只能使用 2 次"的计数器
/// （<c>MaxPlaysPerCombat</c> / <c>_timesPlayedThisCombat</c> / <c>BeforeCombatStart</c> 复位 /
/// <c>{MaxPlays}</c> 文案占位符全部移除）。</item>
/// </list>
///
/// <para>于是这张牌的角色变了：从"每场两次的爆发"变成<b>弹药打空之后的收尾拳</b>——
/// 想再打一次就得先把弹药花光。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class ButtStrike : ModCardTemplate
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Common;
    private const TargetType CardTarget = TargetType.AnyEnemy;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DamageVar(22m, ValueProp.Move)
    ];

    /// <summary>★ 2026-10-17：弹药为 0 才可打出（用户口径）。</summary>
    protected override bool IsPlayable => !AmmoCmd.HasAmmo(Owner);

    public ButtStrike() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        await DamageCmd.Attack(DynamicVars.Damage.BaseValue)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_blunt", null, "heavy_blunt_attack.mp3")
            .Execute(choiceContext);
    }

    protected override void OnUpgrade()
    {
        // 升级：22 → 28。
        DynamicVars.Damage.UpgradeValueBy(6m);
    }
}
