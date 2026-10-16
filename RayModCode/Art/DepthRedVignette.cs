using System;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Rooms;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Art;

/// <summary>
/// <b>层数 &gt; 60 时，屏幕四角略微泛红</b>（2026-10-12 用户指定，第一版"简单特效"）。
///
/// <para><b>挂点</b>：只在两处调 <see cref="Update"/>，两处都是"层数变化的汇聚点"
/// （<c>DepthCmd.SyncToPower</c> / <c>DepthCmd.EnsurePower</c> —— 它们本来就要推 HUD 计数器）。
/// 没有轮询、没有 Harmony、没有事件订阅 ⇒ 不可能出现"两份判据打架"。
/// 阈值也在这一处判（<see cref="RedDepth"/>）。</para>
///
/// <para><b>为什么挂在战斗房间节点下，而不是挂 SceneTree.Root 的 CanvasLayer</b>：
/// ① 层数只在战斗内有意义（`DepthState.ResetForCombat` 每场归零；战斗结束后 <c>state.Depth</c>
///    还留着上一场的值，挂在全局就会"打完架在地图上还红着"）；
/// ② ★ <b>不会压住游戏的过场遮罩</b>：全局 CanvasLayer 会画在 <c>NGame.Transition</c> 之上
///    （§O4 / 铁律 48 那一类事故）。挂在房间节点下，房间被销毁时它跟着消失，
///    而且天然在"全局淡出遮罩"之下。战斗房间不存在时直接隐藏。</para>
///
/// <para>⚠️ 节点自建自管：房间换掉（下一场战斗是新的房间节点）时旧节点随房间一起释放，
/// <see cref="Update"/> 会用 <c>IsInstanceValid</c> 重新建一个。</para>
/// </summary>
public static class DepthRedVignette
{
    private static readonly Logger Log = new("RayMod.Art", LogType.Generic);

    /// <summary>开始泛红的层数（用户指定：大于 60）。</summary>
    private const int RedDepth = 60;

    /// <summary>刚到 60 时的强度（"略微" —— 别做成血色屏幕）。</summary>
    private const float MinAlpha = 0.10f;

    /// <summary>层数很高时的强度上限。</summary>
    private const float MaxAlpha = 0.26f;

    /// <summary>从 61 到 61+N 之间线性加深。</summary>
    private const float RampDepth = 40f;

    private static CanvasItem? _overlay;
    private static ShaderMaterial? _material;
    private static bool _loggedFailure;

    /// <summary>
    /// 层数变化时调用（两处汇聚点）。<paramref name="depth"/> ≤ 60 或不在战斗房间时自动隐藏。
    /// </summary>
    public static void Update(int depth)
    {
        try
        {
            var room = NCombatRoom.Instance;

            if (room == null || depth <= RedDepth)
            {
                Hide();
                return;
            }

            float t = Mathf.Clamp((depth - RedDepth) / RampDepth, 0f, 1f);
            float alpha = Mathf.Lerp(MinAlpha, MaxAlpha, t);

            EnsureOverlay(room);
            _material?.SetShaderParameter("vig_strength", alpha);
        }
        catch (Exception e)
        {
            // 画面效果绝不能影响战斗结算（铁律 9 的同款思路）：只记一次，绝不再抛。
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                Log.Warn($"[层数泛红] 更新失败（只记这一次，效果停用）: {e}");
            }
        }
    }

    /// <summary>隐藏（层数掉回阈值内 / 离开战斗）。</summary>
    public static void Hide()
    {
        try
        {
            if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
            {
                _overlay.Visible = false;
            }
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    private static void EnsureOverlay(Node room)
    {
        if (_overlay != null && GodotObject.IsInstanceValid(_overlay) && _overlay.GetParent() == room)
        {
            _overlay.Visible = true;
            return;
        }

        // 房间换了（或第一次）：重建。旧的随旧房间一起释放，这里不用手动回收。
        _overlay = null;
        _material = null;

        var rect = new ColorRect
        {
            Name = "RayModDepthRedVignette",
            Color = Colors.White,                 // 颜色与透明度全部由 shader 决定
            MouseFilter = Control.MouseFilterEnum.Ignore,   // 绝不拦输入
        };

        _material = new ShaderMaterial { Shader = BuildShader() };
        _material.SetShaderParameter("vig_strength", MinAlpha);
        rect.Material = _material;

        // ★ 自检（铁律 57 的同款）：shader 编译失败时 Godot **不报错、只是静默忽略整个材质**，
        //   唯一可查的信号是"uniform 列表为空"。把它变成一条 ERROR，别让它悄悄什么都不做。
        int uniformCount = _material.Shader?.GetShaderUniformList()?.Count ?? 0;

        if (uniformCount == 0)
        {
            Log.Error("[层数泛红] shader 没编译成功（uniform 列表为空）—— 效果不会显示：" +
                      "检查 GLSL 语法与保留字（铁律 57）");
        }
        else
        {
            Log.Info($"[层数泛红] shader 就绪（uniform {uniformCount} 个）");
        }

        // 铺满整屏（锚点走 Control 的标准全屏预设；父节点是 Control 系的房间根节点）
        rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        rect.OffsetLeft = 0;
        rect.OffsetTop = 0;
        rect.OffsetRight = 0;
        rect.OffsetBottom = 0;

        room.AddChild(rect);
        _overlay = rect;

        Log.Info($"[层数泛红] 浮层已建立（阈值 depth>{RedDepth}，父节点={room.Name}）");
    }

    /// <summary>
    /// 四角泛红的 shader。
    /// ⚠️ <b>变量名一律加 <c>vig_</c> 前缀</b> —— 铁律 57：GLSL 保留字（如 <c>char</c>）会让
    /// Godot <b>静默忽略整个材质且不报错</b>，前缀是最省心的规避方式。
    /// </summary>
    private static Shader BuildShader()
    {
        var shader = new Shader
        {
            Code = @"
shader_type canvas_item;

// 强度（0 = 完全不可见）
uniform float vig_strength : hint_range(0.0, 1.0) = 0.0;
// 从哪里开始泛红（0=中心就开始，1=只有最外圈）
uniform float vig_inner : hint_range(0.0, 1.5) = 0.85;
// 泛红的颜色（偏暗的红，避免刺眼）
uniform vec3 vig_tint = vec3(0.72, 0.05, 0.05);

void fragment() {
    // p: -1..1（屏幕中心为原点）
    vec2 p = UV * 2.0 - 1.0;

    // 椭圆半径（屏幕宽高比不正时不要把左右拉得过宽）
    float r = length(p * vec2(0.82, 1.0));

    // 四角权重：|x|、|y| 同时大的地方（= 四个角）权重最高
    float corner = min(abs(p.x), abs(p.y));

    // 两条曲线取最大：一条管「贴边」，一条管「四角」
    float edge = smoothstep(vig_inner, 1.32, r);
    float quad = smoothstep(0.30, 0.92, corner);

    float a = clamp(max(edge, quad * 0.95), 0.0, 1.0) * vig_strength;

    COLOR = vec4(vig_tint, a);
}
"
        };

        return shader;
    }
}
