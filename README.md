# 怪物意图随机

适配《杀戮尖塔 2》v0.113.0 的单人 Mod。使用怪物画像、行动力池和攻击欲望池生成预告。每招会使用同一份数据生成预告并通过原版命令结算。开场状态在原版初始化后随机，按每只怪物实际获得的层数一次性入账。

## 安装与设置

构建 Mod 项目时，自动把 DLL 和清单安装到游戏目录的 `mods\MonsterIntentRandomizer`。重启游戏并在原版的模组界面启用。用预构建包时，可将 [dist/MonsterIntentRandomizer](dist/MonsterIntentRandomizer) 文件夹放入游戏的 `mods`。

游戏中打开 **设置 → 常规 → 怪物意图随机 · 设置**。在 **逐怪参数** 选中怪物，下方的 **行动偏好** 可以添加招式、修改权重或移除条目。**常用参数** 中可调偏好直占比例的基础值和强度系数。逐怪页也可调整基础行动力、攻击段数偏好、波动及四类意图倾向。

仪式兽的冲撞和破防阶段在 **逐怪参数** 各占一个条目，可以独立启用和调整。冲撞阶段的随机自身强化仅有力量。

状态估值页分为出招效果、怪物强化和开场状态。随机开场强化与战斗中的强化共用 `buffs` 中的公式、房间权重、层数、叠加和独特规则。模拟会标出状态牌在每个去向的张数。点击保存后从下一场战斗生效。配置保存于 `user://mod_data/MonsterIntentRandomizer/config.json`。局中重开战斗时，使用当前设置和该局种子重新生成。

双击 `tools/sync_parameters.pyw` 打开参数同步窗口。选好文件和同步范围后，可以比较、从游戏拉取或推送到游戏。覆盖前会保留 `.bak`。也可以用 `python tools/sync_parameters.py diff` 在命令行比较。

## 哪些怪物会随机

`keepVanilla` 在 [画像文件](data/monster-profiles.json) 顶部，列出完整使用原版行动的怪物。无厌沙虫、知识恶魔、女王等采用 `randomMoves` 招式白名单，通用招式可以随机，召唤和阶段机制等专属招式由原版执行。 [专属招式核对记录](data/mixed-monster-actions.json)

瀑布巨兽保留首招「增压」，执行后获得专属能力「蒸汽积蓄」。从第二回合起，存活时每个自身回合结束获得原版数量的蒸汽喷发。蒸汽喷发独立于画像和随机出招。

## 构建

使用本地 .NET 9 与游戏自带的程序集。

```powershell
dotnet build .\mod\MonsterIntentRandomizer.Mod.csproj -c Release
dotnet run -- --check
```

游戏位于其他位置时可传入 `"-p:GamePath=D:\路径\Slay the Spire 2"`。构建同时更新 `dist\MonsterIntentRandomizer`。只需打包时可传入 `-p:InstallMod=false`。命令行原型可用于对照逐怪参数，包含战斗中保留原版行动的怪物。

估值及参数格式见 [设计](DESIGN.md)。
