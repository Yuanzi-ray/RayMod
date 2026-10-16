using STS2RitsuLib.Scaffolding.Content;

using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Art;

/// <summary>
/// <b>Power 图标的唯一出口</b>（照 <see cref="CardArt"/> 的思路，2026-10-06 加）。
///
/// <para><b>约定</b>：一个 Power 的图标放在
/// <c>res://RayMod/images/powers/&lt;类名&gt;.png</c>，
/// 比如 <c>DepthPower.cs</c> → <c>images/powers/DepthPower.png</c>。</para>
///
/// <para>⚠️ <b>Power 没有卡图那样的"自动回退"</b>：我们的 Power 是裸 <c>PowerModel</c>，
/// 不实现 <c>IModPowerAssetOverrides</c> 的话 RitsuLib 的 <c>PowerIconPathPatch</c>
/// 拿不到路径，日志里就是原来那句 <c>Missing sprite 'ray_mod_power_xxx'</c>。
/// 三个成员（<c>AssetProfile</c> + <c>CustomIconPath</c> + <c>CustomBigIconPath</c>）
/// 缺一不可 —— 每个 Power 用这个类一行接上。</para>
///
/// <para>⚠️ 与卡图同理：<b>override 指向不存在的文件比不接更糟</b>（会直接报错而不是优雅退化），
/// 所以 30 张图必须先出齐再跑那次批量接线。</para>
/// </summary>
public static class PowerArt
{
    /// <summary>按类名派生图标路径（小图标与悬停大图标共用同一张）。</summary>
    public static PowerAssetProfile Profile(string className) => new(
        IconPath: $"{Entry.ResPath}/images/powers/{className}.png",
        BigIconPath: $"{Entry.ResPath}/images/powers/{className}.png");
}
