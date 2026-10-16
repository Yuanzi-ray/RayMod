using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

namespace RayMod.Cards;

/// <summary>
/// 「值由委托实时算出」的 <see cref="DynamicVar"/>。
///
/// <para><b>为什么需要它：</b>想让卡面同时显示<b>动态伤害</b>和<b>另一个动态数字</b>
/// （例如「额外打出 N 次」）时，<see cref="CalculatedVar"/> 做不到 ——
/// 它的公式被写死为 <c>GetBaseVar().BaseValue + GetExtraVar().BaseValue × 乘数</c>
/// （<c>CalculatedVar.Calculate</c>），而 <c>GetBaseVar()</c>/<c>GetExtraVar()</c>
/// 默认取的是本卡<b>唯一那一对</b> <c>CalculationBase</c>/<c>CalculationExtra</c>，
/// 已经被动态伤害占用了。</para>
///
/// <para><b>怎么解决：</b>不继承 <see cref="CalculatedVar"/>，而是直接继承
/// <see cref="DynamicVar"/> 并覆写 <see cref="UpdateCardPreview"/>，
/// 把算好的值写进 <c>PreviewValue</c>/<c>EnchantedValue</c>
/// —— 这正是原版 <c>CalculatedBlockVar</c> 的套路，<b>完全绕开 base/extra 那对变量</b>。</para>
///
/// <para>⚠️ <b>必须在 <c>UpdateCardPreview</c> 里算，不能在 <c>SetOwner</c> 里算</b>：
/// <c>DynamicVarSet.InitializeWithOwner</c> 会在 <c>SetOwner</c> 阶段就调用
/// <c>CalculatedVar.UpdateValues()</c> → <c>GetBaseVar()</c> → <c>((CardModel)_owner).DynamicVars[...]</c>，
/// 而那时 <c>CardModel.DynamicVars</c> 还没挂上（原版注释原话：
/// "we can't call this until the model's DynamicVarSet property has been set"）
/// —— 2026-10-07 就是因为这个抛 <c>KeyNotFoundException</c>，导致图鉴网格每帧异常、整页渲染乱掉。
/// <c>UpdateCardPreview</c> 只在渲染/出牌时调用，那时 <c>DynamicVars</c> 已就绪，安全。</para>
///
/// <para>⚠️ 委托里也<b>不要</b>读写 <c>DynamicVars</c>，只读 <c>card.Owner</c> 的战斗状态。</para>
/// </summary>
public sealed class DelegateVar : DynamicVar
{
    private readonly Func<CardModel, decimal> _value;

    /// <param name="name">卡面占位符名（要与 <c>{Name:diff()}</c> 一致）。</param>
    /// <param name="fallback">无战斗上下文时的兜底值（通常 0）。</param>
    /// <param name="value">实时取值委托，参数是当前卡实例。</param>
    public DelegateVar(string name, decimal fallback, Func<CardModel, decimal> value)
        : base(name, fallback)
    {
        _value = value;
    }

    public override void UpdateCardPreview(
        CardModel card, CardPreviewMode previewMode, Creature? target, bool runGlobalHooks)
    {
        decimal v = _value(card);
        PreviewValue = v;
        EnchantedValue = v;
    }

    /// <summary>
    /// ★ <b>关键</b>：卡面文本 <c>{Name:diff()}</c> 的取值走的是这个方法，**不是**
    /// <c>PreviewValue</c>。
    ///
    /// <para>依据：反编译 0.111 的 <c>CalculatedVar</c>（第 107 行附近）——
    /// <c>protected override decimal GetBaseValueForIConvertible() =&gt; Calculate(null);</c>
    /// 它也是覆写这个方法让实算值出现在卡面文本里的。
    /// SmartFormat 通过 <c>IConvertible</c>（<c>ToInt32()</c>/<c>ToString()</c>…）
    /// 取变量值，而这些全部落到 <c>GetBaseValueForIConvertible()</c>。</para>
    ///
    /// <para>⚠️ 只写 <c>PreviewValue</c> 是不够的 —— 2026-10-07 实测：
    /// 只覆写 <c>UpdateCardPreview</c> 时卡面永远显示构造值 0，
    /// 打完牌之后才变（那是由另一条刷新路径写进去的）。</para>
    ///
    /// <para>⚠️ <c>_owner</c> 可能还没挂上（初始化早期）→ 直接返回兜底 <c>BaseValue</c>，
    /// 绝不在这里碰 <c>DynamicVars</c>（那个坑踩过，会崩图鉴）。</para>
    /// </summary>
    protected override decimal GetBaseValueForIConvertible()
    {
        if (_owner is not CardModel card)
        {
            return BaseValue;
        }

        return _value(card);
    }

    public override string ToString() => ((int)GetBaseValueForIConvertible()).ToString();
}
