using System;
using MegaCrit.Sts2.Core.Logging;
using RayMod.Art;
using STS2RitsuLib;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Settings;

/// <summary>
/// <b>Mod 设置页：换肤</b>。用 RitsuLib 的设置框架声明一个下拉框，
/// 玩家在「模组设置 → RayMod」里选皮肤，选择会自动落盘。
///
/// <para><b>为什么走 RitsuLib 的设置页而不是自己在选角界面塞按钮</b>（2026-10-07 的决定）：
/// RitsuLib 自带一套声明式设置框架（<c>RitsuLibFramework.RegisterModSettings</c> +
/// <c>ModSettingsSectionBuilder.AddEnumChoice</c>），<b>下拉框、本地化、持久化、重置、剪贴板都是现成的</b>。
/// 自己画 UI 要处理选角界面 <c>AnimatedBg</c> 的实例化时机、按钮排版、存档，
/// 而这些框架全都给了 —— 所以先用最低成本的方式把"能换"这件事做出来。</para>
///
/// <para><b>绑定的三个回调</b>（<c>ModSettingsBindings.Callback</c>）：
/// 读/写打到 <see cref="RaySkinState"/>，保存调 <see cref="RaySkinState.Save"/> ——
/// 也就是持久化仍然是我们自己的 <c>ConfigFile</c>，RitsuLib 只负责界面。
/// 这样"读盘时机"仍然牢牢握在 <c>Entry.Initialize()</c> 手里（必须早于第一场战斗）。</para>
///
/// <para>⚠️ <b>整个注册包在 try/catch 里</b>：设置页只是锦上添花，
/// 万一 RitsuLib 的版本变了、API 对不上，也<b>绝不能让 mod 启动失败</b>（铁律 9 的同款思路）。</para>
/// </summary>
public static class RayModSkinSettings
{
    /// <summary>页面 id（同一 mod 下唯一）。</summary>
    private const string PageId = "raymod_skin";

    /// <summary>设置项的键。换名字 = 老玩家的选择会丢，别乱改。</summary>
    private const string SkinDataKey = "ray_skin";

    private static readonly Logger Log = new("RayMod.Skin", LogType.Generic);

    public static void Register()
    {
        try
        {
            RitsuLibFramework.RegisterModSettings(
                Entry.ModId,
                page =>
                {
                    page.WithTitle(ModSettingsText.Literal("莱伊 · 皮肤"));
                    page.WithDescription(ModSettingsText.Literal("选择莱伊在战斗里用的 Spine 皮肤。"));

                    page.AddSection("ray_skin_section", section =>
                    {
                        section.AddEnumChoice<RaySkin>(
                            SkinDataKey,
                            ModSettingsText.Literal("角色皮肤"),
                            ModSettingsBindings.Callback<RaySkin>(
                                Entry.ModId,
                                SkinDataKey,
                                () => RaySkinState.Selected,
                                value => RaySkinState.Selected = value,
                                RaySkinState.Save,
                                SaveScope.Profile),
                            skin => ModSettingsText.Literal(RaySkinCatalog.DisplayName(skin)),
                            ModSettingsText.Literal("下一场战斗生效：当前这场的形象已经建好了，中途换会把动画重置掉。"),
                            ModSettingsChoicePresentation.Dropdown);
                    });
                },
                PageId);

            Log.Info("[皮肤] 设置页已注册（模组设置 → RayMod → 莱伊 · 皮肤）");
        }
        catch (Exception e)
        {
            // 设置页注册失败不影响游戏，更不影响已选中的皮肤（那个走的是 ConfigFile）。
            Log.Warn($"[皮肤] 设置页注册失败（游戏照常，只是没地方改皮肤）：{e}");
        }
    }
}
