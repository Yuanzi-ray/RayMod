using System;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Art;

/// <summary>
/// <b>烘焙版命中特效</b>（2026-10-08 路线 B）：游戏里只播一张**序列帧图集**，
/// 不再在引擎里重放方舟的粒子 prefab。
///
/// <para><b>为什么换路线</b>：路线 A（把方舟 prefab 转成 Godot 场景）一路踩了六个根因
/// （位置 / 被原生钝击盖住 / Harmony 自递归 / Curve 序列化格式错 / 不透明贴图 / Unity 白块），
/// 每修一个只靠实机反馈才知道结果。改成"在 Unity 里把方舟的粒子数据渲成序列帧"之后：
/// <b>本地看到的什么样，玩家看到的就是什么样</b>，引擎里再也不可能出现"数据对但不画"。</para>
///
/// <para><b>帧是【不透明黑底】的</b>（见 <c>_tools\bake_sheet.py</c> 与交接文档 §7.8）：
/// 加法混合下黑 = 不加任何东西，所以整张图直接 <b>ADD</b> 叠上去就是那一发特效。
/// 千万别按普通 alpha 混合播（会看到一个大黑方块）。</para>
///
/// <para><b>图块边长按皮肤各取所需</b>（<c>bake_sheet.py --side</c>）：游戏侧是按 1:1 像素画的，
/// 图块大小**只影响露出多少范围、不影响元素大小**，所以不必统一。2026-10-08 实测：
/// 原版 512px、闪耀 512px（内容只在 93px 半径内，绰绰有余）、
/// <b>夏卉 1024px</b>（`--side 512` 时亮部伸到 503px、丢掉 5.9% 的能量）。</para>
///
/// <para>⚠️ 全程 try/catch + 失败返回 false：不存在/读不了就回退路线 A，特效绝不能影响结算。</para>
/// </summary>
public static class BakedHitVfx
{
    private static readonly Logger Log = new("RayMod.Vfx", LogType.Generic);

    /// <summary>烘焙时的像素密度：1 方舟世界单位 = <b>80 像素</b>（<c>bake_unity.ps1 -Ppu 80</c>）。</summary>
    public const float BakePpu = 80f;

    public static string SheetPathOf(string file) => $"{Entry.ResPath}/vfx/baked/{file}_sheet.png";

    public static string MetaPathOf(string file) => $"{Entry.ResPath}/vfx/baked/{file}_sheet.json";

    /// <summary>
    /// 试着把烘焙图集挂到 <paramref name="host"/> 上并播放。
    /// 成功返回 true，并把 <paramref name="duration"/> 写成这一发实际要活多久（秒）。
    /// <b>失败返回 false</b>（文件不存在 / 读不了 / 图集坏了）—— 调用方那时回退路线 A。
    /// </summary>
    public static bool TryPlay(Node2D host, string file, out float duration)
    {
        duration = 0f;

        try
        {
            string sheetPath = SheetPathOf(file);
            string metaPath = MetaPathOf(file);

            if (!ResourceLoader.Exists(sheetPath) || !Godot.FileAccess.FileExists(metaPath))
            {
                Log.Info($"[特效] 没有烘焙图集，回退转换场景：{sheetPath}");
                return false;
            }

            var sheet = ResourceLoader.Load<Texture2D>(sheetPath);

            if (sheet == null)
            {
                Log.Warn($"[特效] 图集加载失败：{sheetPath}");
                return false;
            }

            if (!TryReadMeta(metaPath, out int frames, out int cols, out int tile, out double fps,
                             out double ppu)
                || frames <= 0 || cols <= 0 || tile <= 0)
            {
                Log.Warn($"[特效] 图集元数据不可用：{metaPath}");
                return false;
            }

            // ★ 警告（不改行为）：图集是**按 1:1 像素**画的（下面的 Scale 只用设置滑条的值），
            //   所以屏幕上的大小 = 图块像素数 ÷ ppu × ppu_游戏屏幕密度。ppu 一旦不是 80，
            //   整发特效的**世界尺寸就静默变了** —— 而 §9 标定出来的"ppu=80 就是原作比例"会失效。
            //   真要改 ppu，必须同时让 Scale 乘上 BakePpu / ppu（那时 world_tile 才真正用得上）。
            if (Math.Abs(ppu - BakePpu) > 0.01)
            {
                Log.Warn($"[特效] 图集 ppu={ppu:F1} ≠ 标定值 {BakePpu:F0}："
                         + $"图块 {tile}px 会按 1:1 画出去，屏幕上的世界尺寸与 §9 标定不一致"
                         + $"（要一致就得把 Scale 乘上 {BakePpu / ppu:F3}）");
            }

            var spriteFrames = new SpriteFrames();
            spriteFrames.RemoveAnimation("default");
            spriteFrames.AddAnimation("play");
            spriteFrames.SetAnimationLoop("play", false);

            int rows = Mathf.CeilToInt((float)frames / cols);

            for (int i = 0; i < frames; i++)
            {
                int c = i % cols;
                int r = i / cols;

                if ((r + 1) * tile > sheet.GetHeight() || (c + 1) * tile > sheet.GetWidth())
                {
                    Log.Warn($"[特效] 图集尺寸不够：要 {cols}x{rows} 格 {tile}px，"
                             + $"实际 {sheet.GetWidth()}x{sheet.GetHeight()}（已播 {i} 帧）");
                    break;
                }

                // FilterClip：不加它，相邻图块会在边缘互相渗色（图块紧挨着时看得见）
                spriteFrames.AddFrame("play", new AtlasTexture
                {
                    Atlas = sheet,
                    Region = new Rect2(c * tile, r * tile, tile, tile),
                    FilterClip = true,
                });
            }

            if (spriteFrames.GetFrameCount("play") <= 0)
            {
                Log.Warn("[特效] 图集一帧都没建出来");
                return false;
            }

            double playFps = Math.Max(1.0, fps * VoiceStateSpeed());
            spriteFrames.SetAnimationSpeed("play", playFps);
            duration = (float)(spriteFrames.GetFrameCount("play") / playFps);

            float scale = (float)VoiceStateScale();
            float bright = (float)VoiceStateBright();

            var anim = new AnimatedSprite2D
            {
                Name = "BakedHitVfx",
                SpriteFrames = spriteFrames,
                Centered = true,
                Position = Vector2.Zero,
                // ★ ADD：黑底帧在加法下"什么都不加"，所以整张图直接叠上去就是那一发特效
                Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
                Modulate = new Color(bright, bright, bright, 1f),
                Scale = Vector2.One * scale,
            };

            host.AddChild(anim);
            anim.Play("play");

            Log.Info($"[特效] 烘焙特效已播放：{file}  帧={spriteFrames.GetFrameCount("play")} "
                     + $"图块={tile}px 播放={playFps:F1}fps 时长={duration:F2}s 缩放={scale:F2} 亮度={bright:F2}");

            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"[特效] 烘焙特效失败（回退转换场景）：{e.Message}");
            return false;
        }
    }

    /// <summary>读 <c>&lt;effect&gt;_sheet.json</c>（bake_sheet.py 写的元数据）。</summary>
    private static bool TryReadMeta(string path, out int frames, out int cols, out int tile,
                                    out double fps, out double ppu)
    {
        frames = 0;
        cols = 0;
        tile = 0;
        fps = 30.0;
        ppu = BakePpu;

        string text = Godot.FileAccess.GetFileAsString(path);

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        Variant parsed = Json.ParseString(text);
        var dict = parsed.AsGodotDictionary();

        if (dict == null || dict.Count == 0)
        {
            return false;
        }

        frames = dict.ContainsKey("frames") ? dict["frames"].AsInt32() : 0;
        cols = dict.ContainsKey("cols") ? dict["cols"].AsInt32() : 0;
        tile = dict.ContainsKey("tile") ? dict["tile"].AsInt32() : 0;
        fps = dict.ContainsKey("fps") ? dict["fps"].AsDouble() : 30.0;
        ppu = dict.ContainsKey("ppu") ? dict["ppu"].AsDouble() : BakePpu;

        // 兜底：json 缺 tile 时按图集宽度/列数推（免得整套特效因为一个字段没了就消失）
        if (tile <= 0 && cols > 0)
        {
            var sheet = ResourceLoader.Load<Texture2D>(path.Replace("_sheet.json", "_sheet.png"));
            if (sheet != null)
            {
                tile = sheet.GetWidth() / Math.Max(1, cols);
            }
        }

        return true;
    }

    // ── 下面三个是"设置页的旋钮"（存在 user://raymod_voice.cfg，见 VoiceState）──

    private static double VoiceStateScale()
    {
        try { return Math.Clamp(Audio.VoiceState.HitVfxScale, 0.1, 6.0); } catch { return 1.0; }
    }

    private static double VoiceStateBright()
    {
        try { return Math.Clamp(Audio.VoiceState.HitVfxBright, 0.1, 4.0); } catch { return 1.0; }
    }

    private static double VoiceStateSpeed()
    {
        try { return Math.Clamp(Audio.VoiceState.HitVfxSpeed, 0.1, 4.0); } catch { return 0.6; }
    }
}
