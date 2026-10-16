using System.Collections.Generic;

namespace RayMod.Art;

/// <summary>
/// <b>可选皮肤</b>。加一套皮肤 = 加一个枚举值 + 往 <see cref="RaySkinCatalog.Entries"/> 里加一行，
/// <b>不用改任何别的地方</b>（设置页的下拉框、战斗形象、沙地兽形象都会自动跟上）。
/// </summary>
public enum RaySkin
{
    /// <summary>夏日（明日方舟 summer#22）。<b>默认值</b>，也是 <c>RayMod_character.tscn</c> 内联的那套。</summary>
    Summer = 0,

    /// <summary>闪耀（明日方舟 shining#2）。</summary>
    Shining = 1,

    /// <summary>
    /// <b>原始莱伊</b> —— 最早那套自制骨架（<c>res://spine/char_ray.skel</c>）。
    /// 也是<b>商店形象一直在用的那套</b>（<c>RayMod_merchant.tscn</c> 直接引用它）。
    /// <b>它没有配对的沙地兽 Spine 模型</b>，所以选它时沙地兽回退成 30 帧 sprite。
    /// </summary>
    Original = 2,
}

/// <summary>
/// <b>皮肤目录</b>：枚举值 → 两套骨架资源（莱伊本人 + 莎地兽）。
///
/// <para><b>⚠️ 路径必须指向 <c>*_data.tres</c>（<c>SpineSkeletonDataResource</c>），不能指向 <c>.skel</c></b>
/// —— 直接给 <c>.skel</c> 会报
/// <c>Expected BoundObject to be a SpineSkeletonDataResource, but it is a SpineSkeletonFileResource!</c>
/// （铁律 16）。<c>spine/skins/*</c> 下的由 <c>_tools/make_spine_data_tres.py</c> 生成。</para>
/// </summary>
public static class RaySkinCatalog
{
    /// <summary>默认皮肤。必须是场景里内联的那一套，这样"没选过/存档丢了/资源坏了"时表现和以前完全一致。</summary>
    public const RaySkin Default = RaySkin.Summer;

    /// <summary>一套皮肤的完整资源：莱伊骨架 + 沙地兽骨架（后者可为 null = 用帧动画）。</summary>
    /// <remarks>⚠️ 别把这个 record 叫 <c>Entry</c> —— 会把 <c>RayMod.Entry</c>（mod 入口类，
    /// <c>Entry.ResPath</c> 就是从它取的）在文件内遮住，编译报 <c>CS0117 未包含 ResPath 的定义</c>。</remarks>
    public sealed record SkinEntry(RaySkin Skin, string DisplayName, string RaySkeletonDataPath, string? BeastSkeletonDataPath);

    private const string SkinRes = $"{Entry.ResPath}/spine/skins";

    private static readonly Dictionary<RaySkin, SkinEntry> Entries = new()
    {
        [RaySkin.Summer] = new(
            RaySkin.Summer,
            "夏卉 FA098",
            $"{SkinRes}/summer/char_4117_ray_summer_data.tres",
            $"{SkinRes}/summer/token_ray_sndbst_summer_data.tres"),

        [RaySkin.Shining] = new(
            RaySkin.Shining,
            "梦幻高空",
            $"{SkinRes}/shining/char_4117_ray_shining_data.tres",
            $"{SkinRes}/shining/token_ray_sndbst_shining_data.tres"),

        // 原始莱伊（用户口中的"默认/原版"）：骨架在项目根的 res://spine/ 下。
        // ⚠️ 这个 .tres **必须放在 res://RayMod/ 里**（这里放 skins/original/），
        //    因为它只被 C# 用字符串路径引用 —— Godot 的 pck 导出靠"依赖边"收集资源，
        //    放在 res://spine/（不在导出过滤器里）会**静默不打包**，
        //    运行时就是 `Cannot open file 'res://spine/char_4117_ray_data.tres'`（2026-10-07 实测踩到）。
        //
        // 沙地兽（2026-10-07 补齐）：从明日方舟原包里解出的**原皮** token_10034_ray_sndbst
        // （ArkUnpacker `-m sp` 解 `pkgrps\btl_pfb_tokens_0.ab`，骨架 3.8.99 → 转换器转 4.2.11）。
        [RaySkin.Original] = new(
            RaySkin.Original,
            "默认(原版)",
            $"{SkinRes}/original/char_4117_ray_data.tres",
            $"{SkinRes}/original/token_ray_sndbst_original_data.tres"),
    };

    /// <summary>下拉框的选项顺序（也决定设置页里的排列）。</summary>
    public static IReadOnlyList<RaySkin> All { get; } = [RaySkin.Summer, RaySkin.Shining, RaySkin.Original];

    /// <summary>取整条目录项；不认识的枚举值回退到默认皮肤。</summary>
    public static SkinEntry Get(RaySkin skin) => Entries.TryGetValue(skin, out SkinEntry? entry) ? entry : Entries[Default];

    /// <summary>设置页里显示的名字。</summary>
    public static string DisplayName(RaySkin skin) => Get(skin).DisplayName;

    /// <summary>这个枚举值到底认不认识（存档被手改 / 版本回退时用得上）。</summary>
    public static bool IsKnown(RaySkin skin) => Entries.ContainsKey(skin);
}
