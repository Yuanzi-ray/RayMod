using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Ammo;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见攻击「你只能走到这了」：造成相当于<b>本回合你与弹药有关的攻击牌已造成伤害总和</b>的伤害。
/// 升级后添加<b>保留</b>。
///
/// <para>伤害口径（用户 2026-10-03 拍板 Q5）：算<b>实际造成</b>的伤害
/// （<c>DamageResult.UnblockedDamage</c>，也就是真被打掉的血），被格挡的部分不算。
/// 并且<b>这张牌自己不算进账本</b>（用户拍板：否则它一打出就把自己的伤害记进去）。</para>
///
/// <para><b>为什么"加保留"是有意义的</b>：账本每回合清零，所以这张牌要么在本回合
/// 一顿弹药输出之后当收尾用，要么被保留下来、等一个"这回合能打很多弹药牌"的回合再兑现。
/// 保留给的正是这个等待的余地。</para>
///
/// <para>账本本体在 <see cref="AmmoLedgerPower"/>（常驻、每回合清零）。
/// 卡面用三元组实时显示它会打多少 —— 乘数读的就是账本，所以数字会随本回合的输出往上爬。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class OnlyGoThisFar : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.AnyEnemy;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        // 基准 0 + 1 × 本回合账本 ⇒ 卡面显示实算伤害。
        new CalculationBaseVar(0m),
        new ExtraDamageVar(1m),
        new CalculatedDamageVar(ValueProp.Move)
            .WithMultiplier((card, _) => AmmoCmd.GetTurnAmmoDamage(card.Owner))
    ];

    public OnlyGoThisFar() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        // 取账本并清零（一张牌只领一次，避免连打两张把同一笔伤害算两遍）。
        int total = AmmoCmd.TakeTurnAmmoDamage(Owner);

        // ★ 2026-10-17：玩家反馈"卡面显示 4~5，实际伤害为零"。
        //   卡面读的是 GetTurnAmmoDamage（只读、实时累计），这里读的是 TakeTurnAmmoDamage
        //   （取走并清零）—— 两者读的是**同一个字段**，所以显示与结算不一致只可能是
        //   "预览渲染之后、真正打出之前账本被清空了"。最可能的场景：升级后带【保留】，
        //   卡被留到下一回合，而账本在 AmmoLedgerPower.AfterPlayerTurnStart 里每回合清零。
        //   这里先把两个数字都打进日志，别再靠猜。
        Log.Info($"[你只能走到这了] Take 到账本 total={total}（卡面同一字段）");

        if (total <= 0)
        {
            // 以前这里静默 return：打出去什么都不发生、不报错、不提示 —— 最糟的失败方式。
            Log.Warn("[你只能走到这了] 账本为 0 ⇒ 不造成伤害。"
                     + "若此刻卡面仍显示非 0，就是卡面没随账本清零而刷新（见 AmmoLedgerPower.AfterPlayerTurnStart）");
            return;
        }

        await DamageCmd.Attack((decimal)total)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
            .Execute(choiceContext);
    }

    protected override void OnUpgrade()
    {
        // 升级：添加"保留"（设计稿："加保留"）。
        AddKeyword(CardKeyword.Retain);
    }
}
