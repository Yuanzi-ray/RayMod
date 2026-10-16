using System;
using Godot;
using MegaCrit.Sts2.Core.Logging;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Art;

/// <summary>
/// <b>把选中的皮肤装到形象场景上</b> —— 换肤功能唯一碰骨架的地方。
///
/// <para><b>★★ 关键：必须在"场景已实例化、但还没进场景树"这个窗口里改。</b>
/// 此时 SpineSprite 的 <c>_ready</c> 还没跑，骨架是从<b>我们塞进去的</b>数据建的 ——
/// 不存在"换骨架把动画状态重置掉"的问题（铁律 18），也不用指望补丁去救。</para>
///
/// <para><b>为什么不用 RitsuLib 的 <c>CustomCombatSpineSkeletonDataPath</c></b>：
/// 那个开关是 <c>NCreature._Ready</c> 的 Postfix，跑在动画器建好<b>之后</b>，
/// 换骨架会把 <c>CreatureAnimator</c> 构造函数刚播的 Idle 抹掉 —— 那正是"人物不动"的根因。
/// 我们改成"进树前就装好"，从时序上绕开整个问题。</para>
///
/// <para>⚠️ <b>失败一律回退到场景自带骨架</b>（也就是默认的夏日皮肤），
/// 绝不让"皮肤资源坏了"演变成"人物不显示"。</para>
/// </summary>
public static class RaySkinApplier
{
    /// <summary>形象场景里那个 SpineSprite 节点名。原版 <c>NCreatureVisuals._Ready</c> 也是取
    /// <c>%Visuals</c>（<c>NCreatureVisuals.cs:253</c>）。</summary>
    public const string SpineNodePath = "%Visuals";

    private static readonly Logger Log = new("RayMod.Skin", LogType.Generic);

    /// <summary>
    /// 给刚 <c>instantiate</c> 出来的形象场景装上当前选中的骨架。
    /// </summary>
    /// <param name="visuals">形象场景根节点（<c>NCreatureVisuals</c>）。可以是 null，那样什么都不做。</param>
    public static void ApplyToVisualsScene(Node? visuals)
    {
        try
        {
            if (visuals == null)
            {
                return;
            }

            Node? spine = visuals.GetNodeOrNull<Node>(SpineNodePath);

            if (spine == null)
            {
                Log.Warn($"[皮肤] 场景里找不到 {SpineNodePath}，本次用场景自带骨架");
                return;
            }

            // 用 GetClass() 字符串判断而不是 C# 类型：spine-godot 的 SpineSprite
            // 不在游戏程序集的引用里（RitsuLib 自己也是这么判的）。
            string className = spine.GetClass().ToString();

            if (className != "SpineSprite")
            {
                Log.Warn($"[皮肤] {SpineNodePath} 不是 SpineSprite（实际是 {className}），本次用场景自带骨架");
                return;
            }

            RaySkin skin = RaySkinState.Selected;
            string path = RaySkinCatalog.Get(skin).RaySkeletonDataPath;
            var data = ResourceLoader.Load<Resource>(path);

            if (data == null)
            {
                Log.Warn($"[皮肤] 骨架资源加载失败：{path} → 回退场景自带骨架");
                return;
            }

            // 属性名照抄 .tscn 里的写法（spine-godot 的 set_skeleton_data_res）。
            spine.Set("skeleton_data_res", data);

            Log.Info($"[皮肤] 已装上 {RaySkinCatalog.DisplayName(skin)}（进树前设置，不会重置动画）");
        }
        catch (Exception e)
        {
            // 宁可"皮肤没换"，也不要"人物没了"。
            Log.Warn($"[皮肤] 装骨架时出错，回退场景自带骨架：{e}");
        }
    }
}
