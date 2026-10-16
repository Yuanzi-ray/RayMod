namespace RayMod.Depth;

/// <summary>
/// <b>「和层数有关的牌」的统一标记</b>（「未探之宝」要数它）。
///
/// <para><b>口径（与 <see cref="RayMod.Ammo.IAmmoCard"/> /
/// <see cref="RayMod.Beasts.ISandBeastCard"/> 完全同构）</b>：
/// 卡面文案里提到「层数」二字的牌就实现它（<b>自己也算</b>）。
/// 所以「探井人」（看层数门槛）、「深入」（改层数）、「装弹」（层数+2）都在池子里。</para>
///
/// <para><b>⚠️ 与 <see cref="IDepthGated"/> 的区别，别搞混</b>：</para>
/// <list type="bullet">
/// <item><see cref="IDepthGated"/> = 「<b>有层数门槛</b>的牌」（"层数大于 N 时才能打出"），
/// 它决定「探井人」的豁免能不能生效，判据是<b>结构性</b>的。</item>
/// <item><see cref="IDepthCard"/> = 「<b>和层数有关</b>的牌」（文案里提到层数就算），
/// 它是「未探之宝」的<b>计数口径</b>，判据是<b>语义/文案</b>的。</item>
/// </list>
/// <para>两者是交叉关系：无缝门槛牌可以是 <see cref="IDepthCard"/>（如「深入」），
/// 门槛牌一定是 <see cref="IDepthCard"/>（门槛本身就要写"层数"）。</para>
///
/// <para><b>为什么用接口而不是运行时读文案</b>：描述是按语言取的
/// （中文"层数"、英文"Depth"），运行时匹配关键词会让<b>额外打出的次数随显示语言变化</b>
/// —— 玩法不能依赖语言。所以语义按口径定，实现用这个接口，
/// 并靠离线校验器强制"接口成员 == 文案里带层数的牌"
/// （<c>_tools\check_depth_cards.ps1</c>，照抄弹药/沙地兽那两个的模式，坑 29）。</para>
///
/// <para><b>约定：任何描述里提到「层数」的牌，都要实现这个接口。</b>
/// grep 检查：<c>Select-String "IDepthCard"</c>。</para>
/// </summary>
public interface IDepthCard
{
}
