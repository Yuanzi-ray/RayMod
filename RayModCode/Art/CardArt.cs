using Godot;

namespace RayMod.Art;

/// <summary>
/// <b>卡图路径的唯一出口</b>（2026-10-04 化简：原来 92 张卡各自写一遍占位图路径）。
///
/// <para><b>约定（美术阶段的核心约定）</b>：一张卡的立绘放在
/// <c>res://RayMod/images/cards/&lt;卡类名&gt;.png</c>，比如
/// <c>Nurture.cs</c> → <c>images/cards/Nurture.png</c>。
/// <b>放进去就自动生效，不用改任何代码</b>；没有这个文件就回退到两张占位图之一
/// （攻击用 <c>_PlaceholderAttack.png</c>、其余用 <c>_PlaceholderDefend.png</c>）。</para>
///
/// <para>用法（<c>Cards/*.cs</c> 里已经全部是这一行）：</para>
/// <code>
/// public override CardAssetProfile AssetProfile => new(
///     PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));
/// </code>
///
/// <para><b>⚠️ 为什么占位图要用 <c>_Placeholder*.png</c> 这套独立文件名（2026-10-05 改）</b>：
/// 打击/防御两张基础牌<b>自己的</b>立绘路径正好就是 <c>RayModStrike.png</c> / <c>RayModDefend.png</c>
/// （由 <c>GetType().Name</c> 派生），所以"把打击卡换成莱伊专属图"和"攻击牌占位图变成那张图"
/// 在旧写法下是<b>同一个文件、无法分开</b> —— 一换就牵连全部 90 张还没画的卡。
/// 现在把中性占位图挪到独立的 <c>_Placeholder*.png</c>，两者解耦：
/// 画好的卡图可以自由覆盖 <c>RayModStrike.png</c> / <c>RayModDefend.png</c>，未画的卡仍显示中性图。</para>
///
/// <para>⚠️ 只判断<b>资源是否存在</b>（<c>ResourceLoader.Exists</c>），不做任何缓存 ——
/// 这个属性只在注册/展示时读几次，代价可以忽略；加了缓存反而会在热重载时拿到旧值。</para>
///
/// <para>⚠️ 用 <c>ResourceLoader.Exists</c> 而不是 <c>GD.Load</c>：后者对不存在的路径会打错误日志。</para>
/// </summary>
public static class CardArt
{
    /// <summary>没有专属立绘时用哪张占位图。</summary>
    public enum Placeholder
    {
        /// <summary>攻击牌：<c>_PlaceholderAttack.png</c>。</summary>
        Attack,

        /// <summary>技能/能力牌：<c>_PlaceholderDefend.png</c>。</summary>
        Defend,
    }

    private const string PlaceholderAttack = Entry.ResPath + "/images/cards/_PlaceholderAttack.png";

    private const string PlaceholderDefend = Entry.ResPath + "/images/cards/_PlaceholderDefend.png";

    /// <summary>取一张卡的立绘路径：优先 <c>images/cards/&lt;卡类名&gt;.png</c>，否则用占位图。</summary>
    /// <param name="className">卡类名（调用处一律传 <c>GetType().Name</c>）。</param>
    /// <param name="fallback">没有立绘时的占位图。</param>
    public static string Portrait(string className, Placeholder fallback)
    {
        string own = $"{Entry.ResPath}/images/cards/{className}.png";

        if (ResourceLoader.Exists(own))
        {
            return own;
        }

        return fallback == Placeholder.Attack ? PlaceholderAttack : PlaceholderDefend;
    }
}
