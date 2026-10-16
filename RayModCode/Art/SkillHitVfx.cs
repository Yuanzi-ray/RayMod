using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Logging;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Art;

/// <summary>
/// <b>命中特效的种类</b>。每个技能一张卡各占一个（用户 2026-10-08 的口径：
/// "各有各的吧，比如得见光芒三技能的击中敌人时有特效"）。
/// </summary>
public enum SkillHitVfx
{
    None = 0,

    /// <summary>三技能「得见光芒」：命中时一团光爆 + 星点四散。</summary>
    Skill3Light = 1,
}

/// <summary>
/// <b>卡牌 → 命中特效</b>映射（按<b>类名</b>查，和 <c>SfxCatalog</c> 同规矩：
/// 本地化键/中文名会随文案改动而变，类名不会）。
/// </summary>
public static class HitVfxCatalog
{
    private static readonly Dictionary<string, SkillHitVfx> CardMap = new()
    {
        ["SeeTheLight"] = SkillHitVfx.Skill3Light,
    };

    /// <summary>没配特效的卡返回 <see cref="SkillHitVfx.None"/>（保持游戏原本的命中特效不变）。</summary>
    public static SkillHitVfx ForCardClass(string className) =>
        CardMap.TryGetValue(className, out SkillHitVfx vfx) ? vfx : SkillHitVfx.None;
}

/// <summary>
/// <b>命中特效节点</b>：命中瞬间重放方舟本体那一发命中特效（三技能「得见光芒」）。
///
/// <para><b>★ 两条路线，默认走路线 B（2026-10-08 下午改）</b>：</para>
///
/// <list type="number">
/// <item><b>路线 B = 烘焙图集</b>（<see cref="BakedHitVfx"/>，<b>默认</b>）：
/// 在 Unity 里把方舟的粒子数据**离线渲成序列帧**，游戏里只播一张 512×512 图块的图集（加法混合）。
/// 所见即所得 —— 本地看到什么样，玩家就看到什么样，引擎里再也不可能"数据对但不画"。
/// 三个旋钮（大小 / 亮度 / 速度）在「莱伊 · 界面」设置页里当场调。</item>
///
/// <item><b>路线 A = 转换场景</b>（<b>兜底</b>，没有烘焙图集时才走）：把 <c>_tools\ak_fx_convert.py</c>
/// 从方舟 prefab 转出来的 <c>vfx\ray_skill_03_hit_0*.tscn</c> 实例化，再把里面的粒子分两类画：
/// <b>单发闪光 → Sprite2D</b>（绘制确定）、<b>真·粒子 → GPUParticles2D</b>。
/// 这条路踩过六个根因（位置 / 被原生钝击盖住 / Harmony 自递归 / Curve 格式错 / 不透明贴图 / Unity 白块），
/// 详情见 <c>HANDOFF-命中特效-交接.md</c>。别再往它上面加东西 —— 有问题改烘焙侧。</item>
/// </list>
///
/// <para>⚠️ 全程包在 try/catch 里 —— 特效出问题绝不能让战斗结算崩掉（铁律 9 同款思路）。</para>
/// </summary>
public partial class SkillHitVfxNode : Node2D
{
    /// <summary>整发特效存活时长（秒）。取最长的粒子生命再留点余量；烘焙路线按图集帧数算。</summary>
    private const float LifeSeconds = 2.5f;

    /// <summary>这一发实际活多久（烘焙路线由图集帧数/播放帧率决定，在 <c>_Ready</c> 里设）。</summary>
    private float _life = LifeSeconds;

    /// <summary>方舟里"单发闪光"的判据：粒子数不超过这个值的，本质就是一张会闪的图。</summary>
    private const int FlashMaxAmount = 4;

    /// <summary>
    /// <b>整体尺寸增益</b>。转换器里 <c>PPU = 100</c> 是**估的常数**（方舟素材是 3D 世界单位，
    /// 换算到屏幕像素没有权威依据）。实机反馈"太小看不到" → 统一放大。
    /// </summary>
    private const float SizeGain = 1.5f;

    /// <summary>
    /// <b>尺寸下限（像素）</b>：按 PPU=100 换算，方舟里那些泡泡/水花只有 8~30px（等于看不见），
    /// 而它们在你截图里明显是 60~100px 的东西。所以给一个下限把它们抬起来，
    /// 已经把尺寸大的（三百多像素的星芒）不受影响 —— 相对关系保留。
    /// </summary>
    private const float MinFlashPx = 50f;

    /// <summary>
    /// <b>加法闪光的透明度下限</b>：方舟这些粒子的染色 alpha 很低（爆闪只有 0.37），
    /// 因为它靠 HDR + 泛光把亮度提上去；我们没有那层后处理，所以按加法混合的粒子把 alpha 抬起来，
    /// 否则就是"一团很淡的东西"。普通混合的不动。
    /// </summary>
    private const float MinAddAlpha = 0.75f;

    /// <summary><c>GpuParticles2D</c> 在实机点火的时机 —— 太早点不着（实测），延后一帧更稳。</summary>
    private const int IgniteDelayFrames = 2;

    private const bool ShowDebugMarker = false;
    private const float DebugMarkerSeconds = 1.4f;

    private static readonly Logger Log = new("RayMod.Vfx", LogType.Generic);

    /// <summary>一枚"单发闪光"：Sprite2D + 它在方舟里的生命期动画参数。</summary>
    private sealed class Flash
    {
        public Sprite2D Sprite = null!;
        public float Life = 0.5f;
        public Color Base = Colors.White;
        public float BasePx;      // 方舟给的原始尺寸（像素）
        public float TexPx;       // 贴图长边
        public Vector2 Origin;    // 出生点（相对特效原点）
        public Vector2 Vel;       // 初速度（像素/秒，方向已随机化）
        public float Grav;        // 重力（像素/秒²）
        public Gradient? Ramp;    // AK 的生命期颜色渐变（没有就自己补淡出）
        public Curve? ScaleOver;  // AK 的生命期尺寸曲线
    }

    private readonly List<Flash> _flashes = new();

    /// <summary>每枚闪光随机一个发射方向（方舟那边是 spread=180 随机撒开的）。</summary>
    private readonly Random _rng = new();

    private Node2D? _converted;
    private float _age;
    private int _frames;

    private Creature? _target;
    private SkillHitVfx _kind;

    /// <summary>按种类造一个（失败返回 null —— 调用方会静默跳过）。</summary>
    public static Node2D? Create(SkillHitVfx kind, Creature? target = null)
    {
        try
        {
            if (kind == SkillHitVfx.None)
            {
                return null;
            }

            return new SkillHitVfxNode { _target = target, _kind = kind };
        }
        catch (Exception e)
        {
            Log.Warn($"[特效] 创建失败（{kind}）：{e.Message}");
            return null;
        }
    }

    /// <summary>
    /// 转换器生成的场景（<b>三套皮肤各有各的</b>：原皮 / 闪耀 <c>shining#2</c> / 夏卉 <c>summer#22</c>；
    /// 夏卉用的编号都不一样 —— <c>ray_skill_03_hit_02_summer#22</c>，不能靠加后缀猜）。
    ///
    /// <para>⚠️ 2026-10-08 起这只是**兜底**：默认走 <see cref="BakedHitVfx"/> 的烘焙图集
    /// （Unity 离线渲出来的序列帧，所见即所得）。两者用**同一个文件名**，见 <see cref="FileOf"/>。</para>
    /// </summary>
    private static string ScenePathOf(SkillHitVfx kind) =>
        string.IsNullOrEmpty(FileOf(kind)) ? string.Empty : $"{Entry.ResPath}/vfx/{FileOf(kind)}.tscn";

    /// <summary>特效文件名（三套皮肤各一个；烘焙图集与转换场景共用这个名字）。</summary>
    private static string FileOf(SkillHitVfx kind)
    {
        if (kind != SkillHitVfx.Skill3Light)
        {
            return string.Empty;
        }

        return RaySkinState.Selected switch
        {
            RaySkin.Shining => "ray_skill_03_hit_01_shining_2",
            RaySkin.Summer => "ray_skill_03_hit_02_summer_22",
            _ => "ray_skill_03_hit_01",
        };
    }

    public override void _Ready()
    {
        try
        {
            // 命中特效要压在生物之上（铁律 8 那条"ZIndex 必须 0"只针对沙地兽那种走 Y 排序的节点）。
            ZAsRelative = false;
            ZIndex = 20;

            GlobalPosition = ResolveSpawnPosition(out string how, out Vector2 creaturePos, out Vector2 spawnPos);

            Node? parent = GetParent();
            Log.Info($"[特效] 定位=({GlobalPosition.X:F0},{GlobalPosition.Y:F0}) 依据={how} " +
                     $"生物=({creaturePos.X:F0},{creaturePos.Y:F0}) VfxSpawn=({spawnPos.X:F0},{spawnPos.Y:F0}) " +
                     $"父={(parent == null ? "null" : parent.Name + "/" + parent.GetType().Name)} " +
                     $"视口=({GetViewportRect().Size.X:F0},{GetViewportRect().Size.Y:F0})");

            string scenePath = ScenePathOf(_kind);

            // ── 路线 B（默认）：烘焙图集。所见即所得，游戏里只播一张图。 ──
            if (BakedHitVfx.TryPlay(this, FileOf(_kind), out float bakedLife))
            {
                _life = bakedLife;
                return;
            }

            // ── 路线 A（兜底）：重放转换出来的粒子场景。 ──
            var packed = string.IsNullOrEmpty(scenePath) ? null : ResourceLoader.Load<PackedScene>(scenePath);

            if (packed == null)
            {
                Log.Warn($"[特效] 场景加载失败（本次没有特效）：{scenePath}");
                QueueFreeSafely();
                return;
            }

            _converted = packed.Instantiate<Node2D>();
            AddChild(_converted);
        }
        catch (Exception e)
        {
            Log.Warn($"[特效] 初始化失败（只记这一条）：{e.Message}");
            QueueFreeSafely();
        }
    }

    /// <summary>
    /// 把场景里的粒子分两类处理：
    /// <b>单发闪光 → Sprite2D</b>（绘制确定，且与方舟观感一致）；
    /// <b>真·粒子 → GpuParticles2D</b>（点火延后一帧，太早点不着）。
    ///
    /// <para>⚠️ 这里**只换绘制方式，不改任何方舟数据**（贴图/尺寸/染色/位置/混合全部照搬）。
    /// 曾经在这里补过量、乘过 2.2 倍缩放 —— 结果"巨大无比，和原作不一致"，别再犯。</para>
    /// </summary>
    private void PrepareParticles(Node root)
    {
        try
        {
            int flashes = 0;
            int particleSystems = 0;
            var skipped = new List<string>();

            foreach (Node child in root.GetChildren())
            {
                if (child is not GpuParticles2D p)
                {
                    continue;
                }

                if (p.Texture == null)
                {
                    p.Visible = false;
                    skipped.Add(p.Name);
                    continue;
                }

                var mat = p.ProcessMaterial as ParticleProcessMaterial;

                if (p.Amount > FlashMaxAmount)
                {
                    particleSystems++;
                    p.Emitting = false;   // 延后一帧再点火（见 _Process）
                    continue;
                }

                // ── 单发闪光：换成 Sprite2D ──
                float texPx = Mathf.Max(p.Texture.GetWidth(), p.Texture.GetHeight());
                float basePx = Mathf.Max(MinFlashPx, texPx * (mat?.ScaleMin ?? 1f) * SizeGain);
                var sprite = new Sprite2D
                {
                    Texture = p.Texture,
                    Material = p.Material,      // 加法 / 序列帧材质原样搬过来
                    Position = p.Position,
                    Rotation = p.Rotation,
                    Scale = Vector2.One * (basePx / texPx),
                    Modulate = mat?.Color ?? Colors.White,
                    Visible = true,
                };

                p.GetParent()?.AddChild(sprite);

                // 初速度：方舟那边是"速度 × 随机方向"；单发闪光就随机一个角度，让它飘出去
                //（这才是实机里"往外散开"的观感来源）。
                float speed = mat == null ? 0f : (mat.InitialVelocityMin + mat.InitialVelocityMax) * 0.5f;
                float ang = (float)(_rng.NextDouble() * Mathf.Tau);
                var vel = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * speed;
                float grav = mat?.Gravity.Y ?? 0f;

                // 加法混合的把手 alpha 抬起来（方舟靠 HDR 泛光，我们没有那层 → 见 MinAddAlpha）
                Color baseColor = mat?.Color ?? Colors.White;

                if (p.Material is CanvasItemMaterial { BlendMode: CanvasItemMaterial.BlendModeEnum.Add } &&
                    baseColor.A < MinAddAlpha)
                {
                    baseColor = new Color(baseColor.R, baseColor.G, baseColor.B, MinAddAlpha);
                }

                _flashes.Add(new Flash
                {
                    Sprite = sprite,
                    Life = Mathf.Max(0.08f, (float)p.Lifetime),
                    Base = baseColor,
                    BasePx = basePx,
                    TexPx = texPx,
                    Origin = p.Position,
                    Vel = vel,
                    Grav = grav,
                    Ramp = (mat?.ColorRamp as GradientTexture1D)?.Gradient,
                    ScaleOver = (mat?.ScaleCurve as CurveTexture)?.Curve,
                });

                p.Visible = false;   // 原粒子系统让位
                flashes++;
            }

            Log.Info($"[特效] 就绪：单发闪光 {flashes} 个（→Sprite2D）· 粒子系统 {particleSystems} 个（→GPUParticles2D）" +
                     (skipped.Count > 0 ? $" · 无贴图跳过 {skipped.Count} 个({string.Join(",", skipped)})" : string.Empty));
        }
        catch (Exception e)
        {
            Log.Warn($"[特效] 粒子处理失败：{e.Message}");
        }
    }

    /// <summary>算这一发该出现在哪；依据会进日志（铁律 35）。四道防线：VfxSpawn → 生物坐标 → 屏幕中心 → 常数。</summary>
    private Vector2 ResolveSpawnPosition(out string how, out Vector2 creaturePos, out Vector2 spawnPos)
    {
        creaturePos = Vector2.Zero;
        spawnPos = Vector2.Zero;

        var creatureNode = _target?.GetCreatureNode();

        if (creatureNode != null && GodotObject.IsInstanceValid(creatureNode))
        {
            creaturePos = creatureNode.GlobalPosition;

            try
            {
                spawnPos = creatureNode.VfxSpawnPosition;
            }
            catch (Exception e)
            {
                Log.Warn($"[特效] 读 VfxSpawnPosition 抛异常：{e.Message}");
            }

            if (IsSanePosition(spawnPos))
            {
                how = "VfxSpawnPosition";
                return spawnPos;
            }

            if (IsSanePosition(creaturePos))
            {
                how = "生物GlobalPosition(VfxSpawnPosition不可信)";
                return creaturePos;
            }
        }

        Vector2 center = GetViewportRect().Size * 0.5f;

        if (IsSanePosition(center))
        {
            how = _target == null ? "屏幕中心(没有命中对象)" : "屏幕中心(生物坐标不可信)";
            return center;
        }

        how = "常数兜底";
        return new Vector2(640f, 360f);
    }

    /// <summary>坐标可信吗：非 NaN/Inf，且离原点还有一段距离（战场上的命中点不可能贴着屏幕左上角）。</summary>
    private static bool IsSanePosition(Vector2 v) =>
        !float.IsNaN(v.X) && !float.IsNaN(v.Y) &&
        !float.IsInfinity(v.X) && !float.IsInfinity(v.Y) &&
        Mathf.Abs(v.X) + Mathf.Abs(v.Y) > 8f;

    /// <summary>诊断准星：把"特效原点"画成肉眼可见的品红十字（不依赖贴图和粒子系统）。</summary>
    public override void _Draw()
    {
        if (!ShowDebugMarker || _age > DebugMarkerSeconds)
        {
            return;
        }

        float alpha = 1f - _age / DebugMarkerSeconds;
        var line = new Color(1f, 0.15f, 0.85f, alpha);
        var fill = new Color(1f, 0.15f, 0.85f, alpha * 0.22f);

        DrawCircle(Vector2.Zero, 30f, fill);
        DrawCircle(Vector2.Zero, 30f, line, false, 2f);
        DrawLine(new Vector2(-46f, 0f), new Vector2(46f, 0f), line, 3f);
        DrawLine(new Vector2(0f, -46f), new Vector2(0f, 46f), line, 3f);
    }

    public override void _Process(double delta)
    {
        try
        {
            _age += (float)delta;
            _frames++;

            if (ShowDebugMarker && _age <= DebugMarkerSeconds)
            {
                QueueRedraw();
            }

            // ① 场景一进树就把粒子分类好（延后一帧，等所有节点就位）
            if (_converted != null && _frames == 2)
            {
                PrepareParticles(_converted);
            }

            // ② 粒子系统延后一点再点火（太早点不着，实测）
            if (_converted != null && _frames == IgniteDelayFrames)
            {
                foreach (Node child in _converted.GetChildren())
                {
                    if (child is GpuParticles2D { Visible: true } gp)
                    {
                        gp.Restart();
                        gp.Emitting = true;
                    }
                }
            }

            // ③ 单发闪光：按方舟自己的生命期渐变/曲线播放
            AnimateFlashes();

            if (_age >= _life)
            {
                QueueFreeSafely();
            }
        }
        catch (Exception e)
        {
            // ⚠️ 以前这里是直接 QueueFree 整个节点 —— 结果任何一个异常都会让"特效消失"，
            //    而且没有任何线索。改成：记日志 + 停掉动画（已经建好的精灵照样留在画面上）。
            Log.Warn($"[特效] 动画异常（特效已定格，不影响结算）：{e}");
            SetProcess(false);
        }
    }

    private void AnimateFlashes()
    {
        foreach (Flash f in _flashes)
        {
            if (!GodotObject.IsInstanceValid(f.Sprite))
            {
                continue;
            }

            float t = Mathf.Clamp(_age / f.Life, 0f, 1f);

            // 颜色：AK 的生命期渐变优先；没有渐变就自己补一条淡出（否则会"啪"地消失）
            Color c = f.Ramp?.Sample(t) ?? new Color(1f, 1f, 1f, 1f - t);
            f.Sprite.Modulate = new Color(
                f.Base.R * c.R,
                f.Base.G * c.G,
                f.Base.B * c.B,
                Mathf.Clamp(f.Base.A * c.A, 0f, 1f));

            // 尺寸：AK 的生命期曲线（1.0 = 原始大小）
            float s = f.ScaleOver?.Sample(t) ?? 1f;

            if (f.TexPx > 0f)
            {
                f.Sprite.Scale = Vector2.One * (f.BasePx * s / f.TexPx);
            }

            // 位置：初速度 × 时间 + 重力（复现方舟里"飘散/下坠"的感觉）
            f.Sprite.Position = f.Origin
                                + f.Vel * _age
                                + new Vector2(0f, 0.5f * f.Grav * _age * _age);

            if (t >= 1f)
            {
                f.Sprite.Visible = false;
            }
        }
    }

    private void QueueFreeSafely()
    {
        if (GodotObject.IsInstanceValid(this))
        {
            QueueFree();
        }
    }
}
