# 创意工坊发布说明

玩家安装、设置和联机说明见 [README](README.md)。[workshop.json](workshop/workshop.json) 已填写中英文页面文案，[image.png](workshop/image.png) 是指定的游戏截图。

## 构建与准备

在仓库根目录运行下列命令。将 `GamePath` 改成自己的游戏目录。

```powershell
dotnet build .\mod\MonsterIntentRandomizer.Mod.csproj -c Release -p:InstallMod=false "-p:GamePath=D:\steam\steamapps\common\Slay the Spire 2"
New-Item -ItemType Directory -Path .\workshop\content -Force
Copy-Item -LiteralPath '.\dist\MonsterIntentRandomizer\MonsterIntentRandomizer.dll', '.\dist\MonsterIntentRandomizer\MonsterIntentRandomizer.json' -Destination .\workshop\content
```

`workshop/content` 放 DLL 和清单。`workshop/image.png` 是封面，当前截图约 821 KiB，低于 1 MB 限制。上传目录使用 Mega Crit 的 [官方工作区格式](https://github.com/megacrit/sts2-mod-uploader/blob/main/template/README.md)。

## 上传与更新

1. 打开 Steam 并登录发布用的账号。解压 [Mega Crit 官方 Mod Uploader](https://github.com/megacrit/sts2-mod-uploader/releases/latest) 到单独目录。
2. 在上传工具目录运行以下命令，把路径替换成仓库中的 `workshop` 目录。

   ```powershell
   .\ModUploader.exe upload -w "D:\path\to\MonsterIntentRandomizer\workshop"
   ```

3. 当前 `visibility` 是 `public`。上传成功后打开 Steam 工坊条目，检查中英文文案、源码链接和封面。
4. 更新同一个条目时，重新构建并替换 `content` 中的两个文件，更新 `changeNote`，然后重复上传命令。保留工具生成的 `mod_id.txt`，它用于定位已有条目。发布步骤见 [官方上传说明](https://github.com/megacrit/sts2-mod-uploader#creating-a-new-mod)。
