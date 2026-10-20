using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;

// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;
using LogType = MegaCrit.Sts2.Core.Logging.LogType;

namespace RayMod.Art;

/// <summary>
/// ★ <b>「灵异录像带」打出后，莱伊身边浮出的鬼影</b>（2026-10-20）。
///
/// <para><b>版本史（很重要，别退回旧形态）</b>：</para>
/// <list type="number">
///   <item><b>v1</b>：一整块浅青剪影 + 锯齿裙边 + 两个黑点眼 + 小圆嘴 ⇒ 用户判「太假太廉价」。</item>
///   <item><b>v2</b>：24 条横带 = 圆顶 + 直边 + 平底 ⇒ 实机读作<b>一块白墓碑</b>，再判廉价。</item>
///   <item><b>v3</b>：圆头 + 肩 + 收细的尾 + 手臂 + 雾丝 ⇒ 轮廓对了，但<b>矢量色块没有材质</b>，仍判廉价。</item>
///   <item><b>v4（本版）</b>：<b>改走贴图</b>。离线用 <c>D:\study\sts2mod\_tools\bake_tape_ghost.py</c>
///         烘一张 260×400 的 RGBA 鬼影（真材质：低频斑块 + 竖向纤维 + 衣褶 + 扫描线 + 烘进图里的
///         VHS 拉带 + 空洞黑眼），运行期只做位移、缩放、色散和信号故障。</item>
/// </list>
///
/// <para><b>为什么贴图一定比矢量强</b>：廉价感的根子是"看起来像颜色填出来的形状"。
/// 一张烘好的图自带<b>逐像素的密度差</b>（布料浓淡、纤维方向、噪点），这是多边形 + alpha 永远给不了的。</para>
///
/// <para><b>运行期还额外做的"信号感"</b>（贴图烘不出来的部分）：</para>
/// <list type="bullet">
///   <item>红 / 青两片<b>加色偏移</b>（色散）—— 偏移量随呼吸轻微变化，不是死值。</item>
///   <item>一条上下扫的<b>VHS tracking 横带</b>（用同一张贴图的 RegionRect 切片，偶尔横移）。</item>
///   <item>整只鬼偶尔<b>单帧掉亮度 / 横跳几像素</b>（信号不稳）。</item>
///   <item>前后景：绕到身后时缩小压暗，绕到身前时放大变亮。</item>
/// </list>
///
/// <para><b>和「子弹模式环轨」「填装形态螺旋」怎么区分</b>：那两套是几何规律的运动（等距、同速、同平面）。
/// 这里每只鬼的<b>半径、高度、角速度、上下浮动相位、出现/消失相位全不同</b>，
/// 不构成一圈环，读起来是"几只东西各自绕着她飘"。</para>
///
/// <para><b>生命周期</b>：<c>ParanormalTape</c> 打出时 <see cref="AttachTapeGhosts"/>，
/// 回合结束（<see cref="ClearTapeHand"/> → <see cref="RetireTapeGhosts"/>）时收场；
/// 节点自己每 0.5 秒自查特效开关。</para>
///
/// <para>⚠️ 坐标：父节点是 <c>room.CombatVfxContainer</c>（Control，世界坐标容器），
/// 位置一律用 <see cref="CardFieldVfx.ChestPos"/> 算再写 <c>GlobalPosition</c>。</para>
///
/// <para>⚠️ 贴图必须真的打进 pck：放在 <c>RayMod\images\vfx\tape_ghost.png</c>，
/// 运行期用 <c>res://RayMod/images/vfx/tape_ghost.png</c> 取。取不到时整段静默跳过（有日志）。</para>
/// </summary>
internal partial class CardFieldVfx
{
    private static readonly Logger GhostLog = new("RayMod.TapeGhosts", LogType.Generic);

    /// <summary>鬼影贴图（离线烘焙，见 <c>_tools\bake_tape_ghost.py</c>）。</summary>
    internal const string TapeGhostTexturePath = "res://RayMod/images/vfx/tape_ghost.png";

    private static Texture2D? _tapeGhostTex;

    private static bool _tapeGhostTexTried;

    private static TapeGhostRing? _tapeGhostRing;

    /// <summary>取鬼影贴图（只尝试一次，失败就静默降级）。</summary>
    private static Texture2D? TapeGhostTexture()
    {
        if (_tapeGhostTexTried)
        {
            return _tapeGhostTex;
        }

        _tapeGhostTexTried = true;

        try
        {
            _tapeGhostTex = GD.Load<Texture2D>(TapeGhostTexturePath);
        }
        catch (Exception e)
        {
            GhostLog.Warn($"[录像带·鬼影] 贴图加载异常：{e.GetType().Name}: {e.Message}");
        }

        if (_tapeGhostTex == null)
        {
            GhostLog.Warn($"[录像带·鬼影] 贴图没读到：{TapeGhostTexturePath} ⇒ 鬼影不出场");
        }
        else
        {
            GhostLog.Info($"[录像带·鬼影] 贴图就位：{_tapeGhostTex.GetWidth()}×{_tapeGhostTex.GetHeight()}");
        }

        return _tapeGhostTex;
    }

    /// <summary>由 <see cref="TapeCardOnPlay"/> 调（录像带打出时）。</summary>
    internal static void AttachTapeGhosts(object? card)
    {
        try
        {
            if (!Enabled || TestModeIsOn() || card == null)
            {
                return;
            }

            // 卡牌的 Owner 是个 Player —— 复用 GetProperty 那套反射取值。
            if (GetProperty(card, "Owner") is not Player owner)
            {
                GhostLog.Warn("[录像带·鬼影] 拿不到 Owner ⇒ 跳过");
                return;
            }

            if (!IsLocalPlayer(owner))
            {
                return;
            }

            if (_tapeGhostRing != null && GodotObject.IsInstanceValid(_tapeGhostRing))
            {
                _tapeGhostRing.Revive();
                return;
            }

            if (VfxParentForEffects() is not Node container)
            {
                GhostLog.Warn("[录像带·鬼影] 拿不到战斗特效容器 ⇒ 跳过");
                return;
            }

            if (TapeGhostTexture() == null)
            {
                return;
            }

            var ring = new TapeGhostRing();
            container.AddChild(ring);
            ring.Arm(owner);
            _tapeGhostRing = ring;

            GhostLog.Info($"[录像带·鬼影] 已浮出 {TapeGhostRing.GhostCount} 只（贴图版：色散 + tracking 横带）");
        }
        catch (Exception e)
        {
            GhostLog.Warn($"[录像带·鬼影] 挂载失败：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>回合结束 / 特效关掉时收场（淡出后自毁）。</summary>
    internal static void RetireTapeGhosts()
    {
        try
        {
            if (_tapeGhostRing != null && GodotObject.IsInstanceValid(_tapeGhostRing))
            {
                _tapeGhostRing.Retire();
            }
        }
        catch
        {
            // 表现层失败绝不外抛
        }

        _tapeGhostRing = null;
    }

    /// <summary>漂在她身边的一群鬼影（贴图版）。</summary>
    internal sealed partial class TapeGhostRing : Node2D
    {
        internal const int GhostCount = 4;

        // ───────────────────────── 调参 ─────────────────────────

        /// <summary>绕她的椭圆轨道半径（世界像素）。横向大、纵向小 ⇒ 读作"围着她走"。</summary>
        private const float OrbitRX = 214f;

        private const float OrbitRY = 74f;

        /// <summary>轨道中心相对胸口的高度（正 = 往下）。鬼的锚点在【脚】，所以这个值要大一些；放太低会拖到血条上。</summary>
        private const float OrbitLiftY = 96f;

        /// <summary>基准角速度（弧度/秒）。</summary>
        private const float SpinSpeed = 0.30f;

        /// <summary>鬼的身高（像素）。她本身约 320px 高 —— 鬼要比她矮一截，否则抢主体。</summary>
        private const float GhostHeight = 292f;

        /// <summary>贴图原始高度（和烘焙脚本一致；Sprite 的 Offset 也按它算）。</summary>
        private const float TexHeight = 400f;

        /// <summary>主体不透明度。贴图本身已经很淡，这里再压一层。</summary>
        private const float CoreAlpha = 0.68f;

        /// <summary>色散（红/青）强度。只在轮廓上可见，别做太宽 —— 宽了就成霓虹描边。</summary>
        private const float FringeAlpha = 0.15f;

        private const float FringeShiftX = 3.2f;

        private const float FringeShiftY = 1.1f;

        /// <summary>VHS 横带高度（贴图像素）。</summary>
        private const float BandHeightPx = 11f;

        /// <summary>横带扫下来的速度（整高的倍数/秒）。</summary>
        private const float BandSpeed = 0.42f;

        private const float BandAlpha = 0.30f;

        private const float FadeInTime = 0.75f;

        private const float FadeOutTime = 0.55f;

        // 冷白 → 淡青（贴图已经是这个色系，这里只做轻微偏色区分个体）
        private static readonly Color TintCool = new(0.86f, 0.94f, 1.00f);

        private static readonly Color TintTeal = new(0.78f, 0.96f, 0.98f);

        // 色散：红 / 青
        private static readonly Color FringeR = new(1.00f, 0.26f, 0.21f);

        private static readonly Color FringeC = new(0.22f, 0.86f, 1.00f);

        // ───────────────────────── 状态 ─────────────────────────

        private Player _owner = null!;

        private readonly List<Ghost> _ghosts = new();

        private float _t;

        private float _poll;

        private float _in;

        private float _out = 1f;

        private bool _retiring;

        private bool _armed;

        private bool _logged;

        private Texture2D _tex = null!;

        /// <summary>一只鬼的所有节点引用 + 它自己的运动参数。</summary>
        private sealed class Ghost
        {
            internal Node2D Root = null!;

            internal Sprite2D Core = null!;

            internal Sprite2D Band = null!;

            internal float Angle;

            internal float SpeedK = 1f;

            internal float RadiusK = 1f;

            internal float HeightK = 1f;

            internal float BobPhase;

            internal float BobAmp = 12f;

            internal float BreathPhase;

            internal float GlitchSeed;

            internal Color Tint = TintCool;

            /// <summary>色散两片（跟着核心一起动，方便做"偏移量呼吸"）。</summary>
            internal Sprite2D FringeRC = null!;

            internal Sprite2D FringeCC = null!;
        }

        internal void Arm(Player owner)
        {
            _owner = owner;
            _armed = true;
            _retiring = false;
            _in = 0f;
            _out = 1f;
            _t = 0f;

            _tex = TapeGhostTexture()!;

            Build();

            GlobalPosition = ChestPos(owner.Creature);

            SetProcess(true);
        }

        /// <summary>同一回合里重复打出 → 直接复活（不重建节点）。</summary>
        internal void Revive()
        {
            _retiring = false;
            _out = 1f;

            if (_in < 0.05f)
            {
                _in = 0f;
            }
        }

        internal void Retire()
        {
            _retiring = true;
        }

        private void Build()
        {
            if (_tex == null || _ghosts.Count > 0)
            {
                return;
            }

            for (int i = 0; i < GhostCount; i++)
            {
                var root = new Node2D { Name = $"RayModTapeGhost{i}" };
                AddChild(root);

                // 色散两片垫在下面（加色），核心压在上面。
                var fringeR = MakeSprite(root, "FringeR", FringeR, add: true);
                var fringeC = MakeSprite(root, "FringeC", FringeC, add: true);
                var core = MakeSprite(root, "Core", TintCool, add: false);
                var band = MakeBand(root);

                var g = new Ghost
                {
                    Root = root,
                    Core = core,
                    FringeRC = fringeR,
                    FringeCC = fringeC,
                    Band = band,
                    Angle = i * Mathf.Tau / GhostCount + (float)GD.RandRange(-0.35, 0.35),
                    SpeedK = (float)GD.RandRange(0.78, 1.28),
                    RadiusK = (float)GD.RandRange(0.80, 1.26),
                    HeightK = (float)GD.RandRange(0.74, 1.16),
                    BobAmp = (float)GD.RandRange(9.0, 18.0),
                    BobPhase = (float)GD.RandRange(0.0, Mathf.Tau),
                    BreathPhase = (float)GD.RandRange(0.0, Mathf.Tau),
                    GlitchSeed = (float)GD.RandRange(0.0, 10.0),
                    Tint = i % 2 == 0 ? TintCool : TintTeal,
                };

                _ghosts.Add(g);
            }
        }

        private static Sprite2D MakeSprite(Node parent, string name, Color color, bool add)
        {
            var sprite = new Sprite2D
            {
                Name = name,
                Centered = true,
                Texture = TapeGhostTexture(),
                Position = new Vector2(0f, -TexHeight * 0.5f),
                Modulate = new Color(color.R, color.G, color.B, 0f),
            };

            if (add)
            {
                sprite.Material = new CanvasItemMaterial
                {
                    BlendMode = CanvasItemMaterial.BlendModeEnum.Add,
                };
            }

            parent.AddChild(sprite);

            return sprite;
        }

        private Sprite2D MakeBand(Node parent)
        {
            var band = new Sprite2D
            {
                Name = "Band",
                Centered = false,
                Texture = _tex,
                RegionEnabled = true,
                RegionRect = new Rect2(0f, 0f, _tex.GetWidth(), BandHeightPx),
                Modulate = new Color(FringeC.R, FringeC.G, FringeC.B, 0f),
                Position = new Vector2(-_tex.GetWidth() * 0.5f, -TexHeight),
            };

            parent.AddChild(band);

            return band;
        }

        public override void _Process(double delta)
        {
            try
            {
                float dt = Mathf.Min((float)delta, 0.033f);

                _t += dt;

                if (!_armed || _owner == null)
                {
                    return;
                }

                // 跟着她走（她的节点会随出牌/受击位移）
                if (_owner.Creature != null)
                {
                    GlobalPosition = ChestPos(_owner.Creature);
                }

                // 淡入 / 淡出
                if (_in < 1f)
                {
                    _in = Mathf.Min(1f, _in + dt / FadeInTime);
                }

                if (_retiring)
                {
                    _out -= dt / FadeOutTime;

                    if (_out <= 0f)
                    {
                        QueueFree();
                        return;
                    }
                }

                // 特效被关掉就收场
                _poll += dt;

                if (_poll >= 0.5f)
                {
                    _poll = 0f;

                    if (!Enabled)
                    {
                        _retiring = true;
                    }
                }

                UpdateGhosts(dt);

                if (!_logged)
                {
                    _logged = true;
                    GhostLog.Info("[录像带·鬼影] 开始漂（贴图版：色散 + 横带 + 前后景）");
                }
            }
            catch
            {
                QueueFree();
            }
        }

        private void UpdateGhosts(float dt)
        {
            float ease = FieldFxEase.OutCubic(Mathf.Clamp(_in, 0f, 1f)) * Mathf.Clamp(_out, 0f, 1f);

            for (int i = 0; i < _ghosts.Count; i++)
            {
                Ghost g = _ghosts[i];

                if (!GodotObject.IsInstanceValid(g.Root))
                {
                    continue;
                }

                // 公转（每只速度不同，所以会互相超越，不构成一圈环）
                g.Angle += dt * SpinSpeed * g.SpeedK;

                float cos = Mathf.Cos(g.Angle);
                float sin = Mathf.Sin(g.Angle);

                // 前后景：sin>0 = 朝屏幕下方（镜头前），<0 = 身后
                float depth = Mathf.Clamp((sin + 1f) * 0.5f, 0f, 1f);

                Vector2 pos = new(
                    cos * OrbitRX * g.RadiusK,
                    sin * OrbitRY * g.RadiusK
                    + OrbitLiftY
                    + Mathf.Sin(_t * 1.25f + g.BobPhase) * g.BobAmp);

                // 信号不稳：偶尔横跳几像素
                float glitchWave = Mathf.Sin(_t * 2.3f + g.GlitchSeed * 3.1f);
                float jitter = glitchWave > 0.94f ? glitchWave * 9f : 0f;
                pos.X += jitter;

                g.Root.Position = pos;

                float depthScale = Mathf.Lerp(0.60f, 1.0f, depth);
                float scale = GhostHeight / TexHeight * g.HeightK * depthScale;
                g.Root.Scale = new Vector2(scale, scale);

                g.Root.ZIndex = depth > 0.5f ? 1 : -1;

                // 呼吸（不是恒定亮度，鬼才有"飘"的感觉）
                float breath = 0.55f + 0.45f * (0.5f + 0.5f * Mathf.Sin(_t * 1.9f + g.BreathPhase));

                // 显形 / 隐去：每只鬼有自己的慢周期，谁也说不准哪一只什么时候"实体化" ——
                // 四只常亮会读成一排装饰，逐个出现/消失才像"屋里本来就有东西"。
                float appar = Mathf.Clamp(
                    (Mathf.Sin(_t * 0.52f + g.BreathPhase * 1.7f - 1.15f) + 0.40f) * 1.55f, 0.10f, 1f);

                // 单帧掉亮度
                float flicker = glitchWave > 0.90f ? 0.55f : 1f;

                float alpha = CoreAlpha * ease * breath * appar * flicker * Mathf.Lerp(0.40f, 0.72f, depth);

                g.Core.Texture = _tex;
                g.Core.Modulate = new Color(g.Tint.R, g.Tint.G, g.Tint.B, alpha);

                // 色散：偏移量随呼吸变，不是死值
                float spread = FringeShiftX * (0.65f + 0.55f * breath) + Mathf.Abs(jitter) * 0.35f;
                float fringeA = FringeAlpha * ease * breath * appar * flicker * Mathf.Lerp(0.35f, 1.0f, depth);

                g.FringeRC.Position = new Vector2(spread, -TexHeight * 0.5f + FringeShiftY);
                g.FringeCC.Position = new Vector2(-spread, -TexHeight * 0.5f - FringeShiftY);
                g.FringeRC.Modulate = new Color(FringeR.R, FringeR.G, FringeR.B, fringeA);
                g.FringeCC.Modulate = new Color(FringeC.R, FringeC.G, FringeC.B, fringeA);

                // VHS tracking 横带：从头顶扫到脚，过了就重来；只在扫到一半时可见
                float bandY = Mathf.PosMod(_t * BandSpeed + g.GlitchSeed * 0.137f, 1f);
                float bandOn = bandY > 0.12f && bandY < 0.80f ? 1f : 0f;
                float bandAlpha = BandAlpha * bandOn * ease * appar * Mathf.Lerp(0.4f, 1f, depth);

                g.Band.RegionRect = new Rect2(0f, bandY * (TexHeight - BandHeightPx), _tex.GetWidth(), BandHeightPx);
                g.Band.Position = new Vector2(-_tex.GetWidth() * 0.5f + jitter * 1.4f, -TexHeight + bandY * TexHeight);
                g.Band.Modulate = new Color(FringeC.R, FringeC.G, FringeC.B, bandAlpha);
            }
        }
    }
}
