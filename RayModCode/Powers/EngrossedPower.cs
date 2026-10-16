using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Ammo;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 稀有能力牌「入神」的载体，两条效果：
/// <list type="number">
/// <item><b>本次真的扣了弹药的攻击 ×1.5</b>（用户拍板⑫：不是"所有弹药牌"）；</item>
/// <item><b>对同一敌人连续攻击时每层 +10%</b>（拍板⑧：无上限、换目标清零）。</item>
/// </list>
///
/// <para><b>① 的判据怎么来</b>：订阅 <see cref="AmmoEvents"/>（弹药变化总线），
/// <b>只有减少</b>才置位"本次出牌花过弹药"；每次出牌结束（<c>AfterCardPlayed</c>）复位。
/// 于是「狙击」「霰弹枪」这种"先扣弹药再打伤害"的牌自然吃到加成，
/// 而普通「打击」不吃。⚠️ 用了「子弹模式」时每张牌都扣弹药 → 每张攻击都吃 1.5 倍（这是组合收益）。</para>
///
/// <para><b>⚠️ 复位为什么放在 <c>AfterCardPlayed</c> 而不是 <c>BeforeCardPlayed</c></b>：
/// 「子弹模式」也在 <c>BeforeCardPlayed</c> 里扣弹药，两个 Power 的钩子<b>顺序不确定</b> ——
/// 如果本类在它<b>之后</b>复位，刚置的位就被清掉，1.5 倍永远不生效（而且不报错）。
/// 放在"出牌结束"就与顺序无关了：上一次出牌的复位一定早于下一次出牌的扣弹药。</para>
///
/// <para><b>② 的叠层口径</b>：<c>AfterCardPlayed</c> 时按"这张攻击牌的目标"推进
/// （<b>每张牌一层</b>，多段攻击不重复叠），伤害计算时按"当前目标是否等于上次目标"取
/// <c>1 + 0.1 × 层数</c>。换目标 → 下一次伤害回到 ×1.0 并重新从 1 层开始。</para>
/// </summary>
[RegisterPower]
public sealed class EngrossedPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(EngrossedPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    /// <summary>
    /// 拍板⑫：真的扣了弹药的攻击，伤害提高该百分比（默认 50，即原来的 ×1.5）。
    /// ★ 默认值只是兜底；实际值在能力被施加时从打出它的那张卡的
    /// <c>DynamicVars["AmmoBonusPercent"]</c> 读进来（卡面数字改了就真的生效）。
    /// </summary>
    private int _ammoBonusPercent = 50;

    /// <summary>
    /// 拍板⑧：同一目标每层提高该百分比（默认 10，即原来的 +10%）。
    /// ★ 默认值只是兜底；实际值从卡的 <c>DynamicVars["PerStackBonusPercent"]</c> 读进来。
    /// </summary>
    private int _perStackBonusPercent = 10;

    /// <summary>本次出牌过程中有没有真的扣过弹药。</summary>
    private bool _ammoSpentThisPlay;

    /// <summary>上一次攻击的目标（换目标 = 清零重来）。</summary>
    private Creature? _lastTarget;

    /// <summary>对当前目标已经打出了几张攻击牌。</summary>
    private int _stacks;

    private bool _subscribed;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示当前叠了几层（0 层时也留在场上）。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    /// <summary>角标显示"对当前目标已叠几层"，让玩家看得见进度。</summary>
    public override int DisplayAmount => _stacks;

    public override Task AfterApplied(Creature? applier, CardModel? cardSource)
    {
        // 从打出它的卡上取真实值（卡面数字改了就真的生效）；取不到就沿用默认值。
        if (cardSource is { } src)
        {
            if (src.DynamicVars.ContainsKey("AmmoBonusPercent"))
            {
                _ammoBonusPercent = src.DynamicVars["AmmoBonusPercent"].IntValue;
            }

            if (src.DynamicVars.ContainsKey("PerStackBonusPercent"))
            {
                _perStackBonusPercent = src.DynamicVars["PerStackBonusPercent"].IntValue;
            }
        }

        if (!_subscribed)
        {
            AmmoEvents.Register(OnAmmoChanged);
            _subscribed = true;
        }

        return Task.CompletedTask;
    }

    /// <summary>一定要注销：总线是<b>静态</b>列表，不注销的话这张牌退场后效果还在，且不报错。</summary>
    public override Task AfterRemoved(Creature oldOwner)
    {
        AmmoEvents.Unregister(OnAmmoChanged);
        _subscribed = false;

        return Task.CompletedTask;
    }

    /// <summary>弹药<b>减少</b>时置位（增加不算 —— 「储备」那类溢出转层数的也会广播）。</summary>
    private void OnAmmoChanged(Player player, int oldValue, int newValue)
    {
        if (newValue < oldValue && (base.Owner?.Player ?? base.Owner?.PetOwner) == player)
        {
            _ammoSpentThisPlay = true;
        }
    }

    // ===================== 伤害倍率 =====================

    public override decimal ModifyDamageMultiplicative(
        Creature? target, decimal amount, ValueProp props, Creature? dealer,
        CardModel? cardSource, CardPlay? cardPlay)
    {
        // 只算"我方用攻击牌打出去的伤害"（照抄 DepthPower 的口径）。
        if (dealer != base.Owner || cardSource == null || !props.IsPoweredAttack())
        {
            return 1m;
        }

        decimal multiplier = 1m;

        // ① 本次真的扣了弹药 → ×1.5
        if (_ammoSpentThisPlay)
        {
            multiplier *= 1m + _ammoBonusPercent / 100m;
        }

        // ② 同一个目标继续打 → 每层 +10%（无上限；换目标时 _lastTarget 已经变了，自然回到 ×1.0）
        if (target != null && target == _lastTarget && _stacks > 0)
        {
            multiplier *= 1m + _perStackBonusPercent / 100m * _stacks;
        }

        return multiplier;
    }

    // ===================== 出牌结束：复位 + 推进叠层 =====================

    public override Task AfterCardPlayed(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        if (cardPlay.Card?.Owner != (base.Owner?.Player ?? base.Owner?.PetOwner))
        {
            return Task.CompletedTask;
        }

        // 复位放在这里（不能放 BeforeCardPlayed，理由见类注释）。
        _ammoSpentThisPlay = false;

        if (cardPlay.Card.Type != CardType.Attack)
        {
            return Task.CompletedTask;
        }

        var target = cardPlay.Target;

        if (target == null)
        {
            return Task.CompletedTask;
        }

        if (target == _lastTarget)
        {
            _stacks++;
        }
        else
        {
            _lastTarget = target;
            _stacks = 1;
        }

        InvokeDisplayAmountChanged();

        return Task.CompletedTask;
    }
}
