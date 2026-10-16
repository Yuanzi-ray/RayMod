namespace RayMod.Hud;

/// <summary>
/// <b>战斗 HUD 计数器的布局参数</b>（2026-10-08）—— 由模组设置页的滑条驱动。
///
/// <para><b>为什么做成可调</b>：计数器挂在能量球右侧，而那一角同时挤着
/// 能量球、<b>游戏自己的 Power 图标框</b>和手牌 —— 我改了两版都撞上东西
/// （横排撞手牌、竖排撞 Power 框）。与其猜屏幕坐标，不如把三个旋钮交给用户当场调。</para>
///
/// <para>改完立即生效：<see cref="NotifyChanged"/> 会让两个计数器**下次刷新时重建**，
/// 而弹药/层数每次变化都会刷新一次（出牌就会变），所以不用重启战斗。</para>
/// </summary>
internal static class HudLayout
{
    /// <summary>左右偏移（像素）。正数往右。<b>值存在 <see cref="Audio.VoiceState"/> 的 cfg 里</b>（重启不丢）。</summary>
    internal static double OffsetX
    {
        get => Audio.VoiceState.HudOffsetX;
        set => Audio.VoiceState.HudOffsetX = value;
    }

    /// <summary>上下偏移（像素）。正数往下。</summary>
    internal static double OffsetY
    {
        get => Audio.VoiceState.HudOffsetY;
        set => Audio.VoiceState.HudOffsetY = value;
    }

    /// <summary>整体缩放（1.0 = 图标 64px、数字 16px）。</summary>
    internal static double Scale
    {
        get => Audio.VoiceState.HudScale;
        set => Audio.VoiceState.HudScale = value;
    }

    /// <summary>设置页改完之后调一次：让计数器重建到新位置。</summary>
    internal static void NotifyChanged()
    {
        ResourceHud.InvalidateLayout();
    }
}
