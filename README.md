# 怪物意图随机

适配《杀戮尖塔 2》v0.113.0 的单人 Mod。使用怪物画像、行动力池和攻击欲望池生成预告。每招会使用同一份数据生成预告并通过原版命令结算。开场状态在原版初始化后随机，按每只怪物实际获得的层数一次性入账。

## 安装与设置

构建 Mod 项目时，自动把 DLL 和清单安装到游戏目录的 `mods\MonsterIntentRandomizer`。重启游戏并在原版的模组界面启用。用预构建包时，可将 [dist/MonsterIntentRandomizer](dist/MonsterIntentRandomizer) 文件夹放入游戏的 `mods`。

游戏中打开 **设置 → 常规 → 怪物意图随机 · 设置**。常用参数可以直接调，逐怪参数页可以调整基础行动力、攻击段数、预期寿命、波动及四类意图倾向。全局参数页和画像 JSON 页可以直接改估值公式、开场候选、状态牌位置等。点击保存后从下一场战斗生效。配置保存于 `user://mod_data/MonsterIntentRandomizer/config.json`。局中重开战斗时，使用当前设置和该局种子重新生成。

## 哪些怪物会随机

`keepVanilla` 在 [画像文件](data/monster-profiles.json) 顶部，初始筛选保留仪式兽、女王、实验体、偷窃草蜢、十足蜈蚣、失落之物与遗忘之物等具有专属行动或联动的怪物。运行时也会跳过包含召唤、逃跑、睡眠等特殊意图的原版状态机，以及无画像的怪物和联机战斗。画像标记的强制首招和战斗中由原版触发的眩晕继续生效。

## 构建

使用本地 .NET 9 与游戏自带的程序集。

```powershell
dotnet build .\mod\MonsterIntentRandomizer.Mod.csproj -c Release
dotnet run -- --check
```

游戏位于其他位置时可传入 `"-p:GamePath=D:\路径\Slay the Spire 2"`。构建同时更新 `dist\MonsterIntentRandomizer`。只需打包时可传入 `-p:InstallMod=false`。命令行原型可用于对照逐怪参数，包含战斗中保留原版行动的怪物。

估值及参数格式见 [设计](DESIGN.md)。
