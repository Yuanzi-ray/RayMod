# 源码发布声明 / Source Release Notice

> 本文件说明：**这个仓库里有什么、没有什么、以及为什么。** 克隆前请务必读一遍。

## 一句话

**本仓库只包含 RayMod 的源码。** 模组用到的第三方版权素材（明日方舟的语音、骨骼、美术）
**有意不包含在内** —— 它们不可再分发。因此**克隆本仓库不能直接构建出可用的模组**，
你需要自备这些素材。

## 仓库里有什么

| 目录 | 内容 | 说明 |
|---|---|---|
| `RayModCode/` | **C# 源码**（207 个文件）| 全部游戏逻辑：卡牌、能力、层数/弹药/沙地兽机制、特效、HUD、设置页、语音系统 |
| `RayMod/localization/` | 中英文本地化文案 | 卡面/能力/遗物/关键词/事件 的文案表 |
| `RayMod/scenes/` | Godot 场景 | 角色、能量计数器、商店、休息点等 |
| `RayMod/images/relics/` | 4 张遗物图标 | **我们自己画的**，无第三方元素 |
| 根目录 | 工程文件 | `RayMod.csproj` / `.sln` / `project.godot` / `export_presets.cfg` / `local.props.template` / `RayMod.json` |

合计约 **1.8 MB / 244 个文件**。

## 仓库里【没有】什么（有意排除）

| 排除的目录 | 内容 | 为什么不能包含 |
|---|---|---|
| `RayMod/audio/voice/` | 中日双语语音 28×2（112 个 wav）| **《明日方舟》原音**，版权属鹰角网络 |
| `RayMod/audio/sfx/` | 战斗音效 | 同为方舟原音 |
| `RayMod/spine/`、`/spine/` | 角色与沙地兽的 Spine 骨骼/贴图（三套皮肤）| 方舟官方素材 |
| `RayMod/images/cards/` | 92 张卡面插画 | AI 生成、但参考了官方设定 —— 灰色地带，一并排除 |
| `RayMod/images/powers/`、`characters/`、`monsters/`、`events/` | 能力图标 / 角色立绘与 UI / 沙地兽 sprite / 事件立绘 | 含方舟元素 |
| `RayMod/images/vfx/`、`RayMod/vfx/` | 特效贴图与烘焙图集 | 从方舟素材转出 |

这些规则写在 `.gitignore` 里，**并有注释说明每一项的理由**。

## 想自己构建？

1. 装 [STS2-RitsuLib](https://github.com/BAKAOLC/STS2-RitsuLib) 与 MinionLib 两个前置；
2. 复制 `local.props.template` 为 `local.props`，填你自己的 Steam / Godot 路径；
3. **自备上表里被排除的素材**，放回对应目录；
4. `dotnet build`。

缺少素材时，代码本身能编译，但模组运行起来会缺图/缺音。

## 版权与使用

* **代码部分**（`RayModCode/`、本地化文案、工程配置）：作者 **Yuanzi_**。
  允许阅读、学习、提交 PR。**禁止商用**；二次分发请注明作者。
* **被排除的第三方素材**：版权归鹰角网络（Hypergryph）等原权利人，
  仅在**模组本身**中以个人非盈利同人方式引用，**未包含在本仓库内、也不授权任何人使用**。

本模组是**非官方同人作品**，与鹰角网络、Studio Montagne、
以及《杀戮尖塔 2》开发商 Mega Crit 均无任何隶属或授权关系。

若权利人认为本仓库或模组侵犯了权益，请通过创意工坊留言或 Steam 联系作者，
将在收到通知后尽快删除相关内容。
