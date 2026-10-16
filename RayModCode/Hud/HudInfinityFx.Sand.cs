using Godot;

namespace RayMod.Hud;

/// <summary>
/// <see cref="HudInfinityFx"/> 的**数字崩解段**（2026-10-13 用户改的编排）：
/// <b>上限那串数字不再"一路增长"，改成 —— 略微振动 + 沙石掉落</b>。
///
/// <para><b>用户原话</b>："把数字增长改掉吧。改成<b>略微振动+有沙石掉落</b>"。
/// 被改掉的那一版是 2026-10-12 用户自己定的"数字突然变异 10 → 100 → 1000 → …" ——
/// 所以<b>不是</b>bug，是**设计变更**：读法从"数字在发疯"改成"<b>数字像砂岩一样在崩解</b>"。</para>
///
/// <para><b>两件事</b>：</para>
/// <list type="number">
/// <item><b>振动</b>（<see cref="VibeAmp"/> 等）：幅度只有 <c>牌子宽 × 0.022 ≈ 2.7px</c> ——
/// 用户要的是"**略微**"，所以是**细抖**（23Hz）而不是晃；越接近刀口抖得越厉害（<see cref="VibeRamp"/>）。
/// 字号只做 ±2.5% 的脉动（张力），<b>不做任何增长</b>。</item>
/// <item><b>沙石</b>：44 颗粒子从**数字矩形里**掉出来（细沙 / 两颗石子 / 一撮碎石屑），
/// 走抛物线（初速 + 重力）、带自转，落到下面慢慢淡掉；白闪那一刻**停止生成**，
/// ∞ 出现时整团收尾淡出。</item>
/// </list>
///
/// <para>⚠️ <b>所有量都按"牌子宽"归一化</b>（<c>plate.W</c>），换分辨率不会歪；
/// 粒子档案来自生成器 <c>_tools\make_inf_show_spec.py</c>（<c>HudInfinitySandSpec</c>），
/// 预览读的是同一份 JSON ⇒ 预览 = 游戏。</para>
/// </summary>
internal sealed partial class HudInfinityFx
{
    /// <summary>沙石**停止生成**的时刻（= 白闪）。之后让已经在空中的掉完。</summary>
    private const float SandSpawnEnd = FlashAt;

    /// <summary>∞ 出现后，沙石整体收尾淡出的时长（免得尘埃还挂在新出现的 ∞ 旁边）。</summary>
    private const float SandKillFade = 0.18f;

    // ── 振动 / 脉动（与生成器 GEO 一致；单位是**数字字号 fontSize**）──
    /// <summary>振动幅度 = 字号 × 它（27px 字号 ⇒ ≈ 2.7px）—— "**略微**振动"。</summary>
    private const float VibeAmp = 0.10f;

    /// <summary>振动频率（Hz）—— 细抖，不是晃。</summary>
    private const float VibeHz = 23.0f;

    /// <summary>抖幅从 (1 - 它) 涨到 1（越接近刀口抖得越厉害）。</summary>
    private const float VibeRamp = 0.55f;

    /// <summary>字号脉动幅度（±2.5%）—— 只做张力，**不是增长**。</summary>
    private const float PulseAmp = 0.025f;

    // ── 沙石的物理（与生成器 GEO 一致；单位都是"字号/秒"）──
    //    ⚠️ 为什么不用牌子宽：预览里牌子矩形是从截图量的（160px），代码里是 64×Scale×1.28
    //    （=122.9px）—— 差 1.3×。按字号算两边才是同一个数（预览 HUD_BASE = 游戏 18×1.5 = 27）。
    private const float SandVy = 1.50f;      // 初速基准（字号/秒）
    private const float SandG = 33.0f;       // 重力基准（字号/秒²）

    /// <summary>沙石的颜色 —— 偏暖的石灰白（**不是发光**：石头是材质，所以走普通 alpha 混合，
    /// 不进那层加色的烟）。</summary>
    private static readonly Color SandTint = new(0.88f, 0.82f, 0.70f);
    private const float SandGain = 1.05f;

    private static readonly string[] SandPaths =
    {
        "/images/vfx/fx_sand_grain.png",     // 1 细沙（柔和小点）
        "/images/vfx/fx_pebble_1.png",       // 2 石子 A
        "/images/vfx/fx_pebble_2.png",       // 3 石子 B
        "/images/vfx/fx_sand_clump.png",     // 4 碎石屑簇
    };

    private readonly Texture2D?[] _sand = new Texture2D?[4];

    /// <summary>装沙石贴图（<c>_Ready</c> 里调；缺了就整段跳过，绝不影响别的拍）。</summary>
    private void LoadSandTextures()
    {
        for (int i = 0; i < SandPaths.Length; i++)
        {
            _sand[i] = ResourceLoader.Load<Texture2D>(Entry.ResPath + SandPaths[i]);
        }

        if (_sand[0] == null)
        {
            Log.Warn("[无限·HUD] 沙石贴图缺失 ⇒ 跳过「沙石掉落」这一拍（振动照旧）；"
                     + "查 pck 是否包含 images/vfx/fx_sand_grain.png 等 4 张"
                     + "（新加的 png 必须跑过一次 godot --headless --import 才会进包）");
        }
    }

    /// <summary>数字那串字所在的矩形（局部坐标）—— 沙石就从这里面掉出来。</summary>
    private Rect2 NumberRect()
    {
        Label? label = ResourceHud.Ammo.LabelNode;

        if (label != null && GodotObject.IsInstanceValid(label) && label.IsInsideTree())
        {
            return new Rect2(ToLocal(label.GlobalPosition), label.Size);
        }

        // 牌子还在但 Label 拿不到：退化成"牌子中间那一行"
        var size = new Vector2(_plate.Size.X, _plate.Size.Y * 0.34f);

        return new Rect2(_plate.Position.X, _plate.Position.Y + _plate.Size.Y * 0.60f, size.X, size.Y);
    }

    /// <summary>沙石（<c>_t</c> 的纯函数；画在本节点自己的 `_Draw` 里 ⇒ 普通 alpha 混合 = 材质感）。</summary>
    private void DrawSand()
    {
        if (_t < MutateAt - 0.02f || _t > RevealAt + SandKillFade || _sand[0] == null)
        {
            return;
        }

        float unit = PlateFontSize();          // ★ 归一化单位 = **数字字号**（不是牌子宽，见常量区说明）

        if (unit < 4f)
        {
            return;
        }

        Rect2 num = NumberRect();

        if (num.Size.X < 4f)
        {
            return;
        }

        float spawnWindow = Mathf.Max(0.05f, SandSpawnEnd - MutateAt);

        // ∞ 出现后整团收尾（否则尘埃会挂在新出现的 ∞ 旁边）
        float kill = Mathf.Clamp(1f - (_t - RevealAt) / SandKillFade, 0f, 1f);
        float[] p = HudInfinitySandSpec.Particles;

        for (int i = 0; i < HudInfinitySandSpec.Count; i++)
        {
            int b = i * HudInfinitySandSpec.Stride;
            Texture2D? tex = _sand[Mathf.Clamp((int)p[b + HudInfinitySandSpec.Tex], 1, 4) - 1];

            if (tex == null)
            {
                continue;
            }

            float born = MutateAt + p[b + HudInfinitySandSpec.Delay] * spawnWindow;
            float age = _t - born;
            float life = Mathf.Max(0.05f, p[b + HudInfinitySandSpec.Life]);

            if (age < 0f || age > life)
            {
                continue;
            }

            // 抛物线：初速 + 重力（都按**字号**归一化）
            float vy = p[b + HudInfinitySandSpec.Vy] * SandVy * unit;
            float g = p[b + HudInfinitySandSpec.G] * SandG * unit;
            var at = new Vector2(
                num.Position.X + p[b + HudInfinitySandSpec.Dx] * num.Size.X + p[b + HudInfinitySandSpec.Vx] * unit * age,
                num.Position.Y + p[b + HudInfinitySandSpec.Dy] * num.Size.Y + vy * age + 0.5f * g * age * age);

            float k = age / life;
            float fadeIn = Mathf.Min(1f, age / 0.05f);                      // 出现很快
            float fadeOut = k < 0.55f ? 1f : 1f - Smoothstep((k - 0.55f) / 0.45f);
            float alpha = p[b + HudInfinitySandSpec.Alpha] * fadeIn * fadeOut * kill;

            if (alpha <= 0.004f)
            {
                continue;
            }

            float size = p[b + HudInfinitySandSpec.Sf] * unit * (1f + 0.35f * k);
            float deg = p[b + HudInfinitySandSpec.Rot] + p[b + HudInfinitySandSpec.Spin] * age;

            DrawTex(tex, at, size, Mathf.DegToRad(deg),
                    new Color(SandTint.R, SandTint.G, SandTint.B, alpha), SandGain);
        }
    }
}
