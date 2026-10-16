using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 罕见技能「黑暗之下」的载体：<b>本回合</b>让目标敌人失去力量，回合结束时<b>自动还回去</b>。
///
/// <para>照抄原版 <c>DarkShacklesPower</c>（<c>src\Core\Models\Powers\DarkShacklesPower.cs</c>，全文 2 个成员）——
/// 这类"临时力量"全部继承 <see cref="TemporaryStrengthPower"/>，只需给两个成员：</para>
/// <list type="bullet">
/// <item><c>OriginModel</c>：哪张牌产生的（<b>Title 直接取它的标题</b>，所以不需要本地化键）</item>
/// <item><c>IsPositive => false</c>：表示这次是"失去力量"（基类据此把 Type 设成 Debuff、把符号取反）</item>
/// </list>
///
/// <para>基类 <c>TemporaryStrengthPower</c> 干了三件事（<c>TemporaryStrengthPower.cs</c>）：</para>
/// <list type="number">
/// <item><c>BeforeApplied</c>（:134）立刻把 <c>StrengthPower</c> 按符号施加（silent，所以力量牌上会真的显示减少）</item>
/// <item><c>AfterPowerAmountChanged</c>（:139）处理"层数被改"的情况（例如又被打了一张）</item>
/// <item><c>AfterSideTurnEnd</c>（:147）在这个回合结束时把自己移除，并<b>把力量加回来</b></item>
/// </list>
///
/// <para>⚠️ 所以施加时传的是 <b>正数</b>（<c>PowerCmd.Apply&lt;DarknessPower&gt;(..., 5m, ...)</c>），
/// 负号由 <c>IsPositive</c> 决定 —— 别像直接用 <c>StrengthPower</c> 那样传负数，会变成"加力量"。</para>
/// </summary>
[RegisterPower]
public sealed class DarknessPower : TemporaryStrengthPower, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(DarknessPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    public override AbstractModel OriginModel => ModelDb.Card<RayMod.Cards.Darkness>();

    /// <summary>false = 这次是"失去力量"（基类会把符号取反、Type 设为 Debuff）。</summary>
    protected override bool IsPositive => false;
}
