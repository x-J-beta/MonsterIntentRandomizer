# 发布更新

源码发布到 [GitHub](https://github.com/x-J-beta/MonsterIntentRandomizer)，安装包更新到现有[创意工坊条目](https://steamcommunity.com/sharedfiles/filedetails/?id=3840913980)。[玩家使用说明](README.md)和[双语更新文案](workshop/workshop.json)随源码维护。

## 构建预览包

使用本地 .NET 9 SDK、.NET 8 运行时以及安装好的《杀戮尖塔 2》。更新 [模组清单](mod/MonsterIntentRandomizer.json)和[项目文件](mod/MonsterIntentRandomizer.Mod.csproj)的版本号，再更新 `workshop/workshop.json` 的 `changeNote`。本次准备的版本是 `0.2.0`。

在仓库根目录运行：

```powershell
.\tools\release.ps1
```

仓库在 Steam 创意工坊目录之外时，用 `-GamePath 'D:\steam\steamapps\common\Slay the Spire 2'` 指定游戏目录。

脚本运行现有原型检查，使用仓库的默认配置构建 DLL，并在 `dist/release/v版本-UTC时间/` 生成手动安装 ZIP 和待上传的 `workshop` 目录。待上传目录含 DLL、清单、现有封面和更新说明。

## 推送 GitHub 并更新创意工坊

先用 `git status --short` 核对发布范围，将要发布的源码、默认配置、素材与说明提交到目标分支。脚本推送当前分支到 `origin` 的同名分支。

打开 Steam 并登录有该条目编辑权限的账号。准备支持 `upload -i` 的 Mega Crit 官方上传器。本机已有的上传器可用下列命令：

```powershell
.\tools\release.ps1 -PublishGitHub -PublishWorkshop `
    -UploaderPath '.\.scratch\WorkshopUploader\v0.2.0\ModUploader.exe'
```

其他机器可更换为 [官方上传器](https://github.com/megacrit/sts2-mod-uploader/releases/latest) 的实际路径。脚本先推送 GitHub，再用同一次构建的 DLL 和清单更新工坊条目 `3840913980`。上传配置提交封面及 `changeNote`。 [官方更新流程](https://github.com/megacrit/sts2-mod-uploader#updating-an-existing-mod)

单独执行时可只传 `-PublishGitHub` 或 `-PublishWorkshop`。如果 GitHub 已推送而工坊上传失败，用同一提交运行 `-PublishWorkshop` 重试。上传后在工坊条目核对更新说明和版本，并用 Steam 的订阅安装流程验证游戏加载。
