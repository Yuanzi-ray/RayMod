using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Depth;

/// <summary>
/// 「带层数门槛的牌」的基类：把 <see cref="IsPlayable"/> 和"消耗探井人豁免"这两件事
/// 一次办好，子类只管两样东西：
/// <list type="bullet">
/// <item><see cref="DepthConditionMet"/> —— 本卡的层数条件满不满足</item>
/// <item><see cref="OnPlayGated"/> —— 本卡的实际效果</item>
/// </list>
///
/// <para><b>⚠️ 别在子类里再覆写 <c>IsPlayable</c> / <c>OnPlay</c></b>：
/// 两者都标了 <c>sealed</c>。绕过基类自己写 <c>IsPlayable</c> 会让「探井人」对这张牌静默失效
/// （不报错，只是不生效）—— 这正是 Q16 要根除的问题。</para>
/// </summary>
public abstract class DepthGatedCard : ModCardTemplate, IDepthGated
{
    protected DepthGatedCard(
        int energyCost, CardType cardType, CardRarity rarity, TargetType target, bool showInCardLibrary)
        : base(energyCost, cardType, rarity, target, showInCardLibrary)
    {
    }

    /// <summary>本卡的层数条件是否满足（例如"层数大于 60"）。</summary>
    protected abstract bool DepthConditionMet { get; }

    /// <summary>层数不够就不可打出 —— 除非「探井人」的豁免还在。</summary>
    protected sealed override bool IsPlayable => DepthGate.CanPlay(Owner, DepthConditionMet);

    protected sealed override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // 真的打出来了：如果条件本来不满足（说明是靠豁免进来的），把这一次豁免吃掉。
        DepthGate.MarkPlayed(Owner, DepthConditionMet);

        await OnPlayGated(choiceContext, cardPlay);
    }

    /// <summary>本卡的实际效果（原来的 <c>OnPlay</c> 内容搬到这里）。</summary>
    protected abstract Task OnPlayGated(PlayerChoiceContext choiceContext, CardPlay cardPlay);
}
