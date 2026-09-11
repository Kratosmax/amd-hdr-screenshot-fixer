# AMD HDR Screenshot Fixer

<img src="assets/app-icon.png" alt="应用图标" width="128">

面向 Windows 的 AMD HDR 截图校正工具。它通过 CPU 对 PNG 像素执行标定、轻量 3D LUT、RGB 增益、曝光、对比度和色阶调整，并提供实时预览、目录监听和自动导出。全部图像处理与校准均在本机完成，不使用在线或本地大模型。

本项目与 AMD 无隶属或授权关系；名称仅用于说明适用场景。

## 下载选择

从 [最新 Release](https://github.com/Kratosmax/amd-hdr-screenshot-fixer/releases/latest) 下载：

| 包 | 适用情况 |
| --- | --- |
| Full Setup | 推荐给普通用户；包含运行时，安装后可自动更新 |
| Lite Setup | 已安装 .NET 8 Desktop Runtime x64；体积较小 |
| Full Portable ZIP | 免安装、包含运行时；解压后运行 |
| Lite Portable ZIP | 免安装、需 .NET 8 Desktop Runtime x64 |

发布包当前未使用商业代码签名证书。Windows SmartScreen 可能在首次运行时提示未知发布者；请从本仓库 Release 下载并对照 `SHA256SUMS.txt`。

## 使用教程

1. 打开或拖入一张 PNG。
2. 调节红、绿、蓝、曝光和对比度；“高级微调”中还可调整饱和度、黑位和白位。方向键步进分别为 1% 和 0.01 EV。
3. “保存为默认”会持久化当前参数；“恢复默认”恢复到最后保存的值。
4. 点击“导出校正图”保存完整分辨率结果。

开启“监听”并选择目录后，新 PNG 会按默认参数导出到源文件旁的 `fixed` 子目录。界面显示最近处理的图片；窗口最小化时暂停预览解码，但监听和导出继续。监听开关每次启动默认关闭，目录会持久化。

### 高级校准

“高级校准”需要至少三组内容一致、尺寸相同的“AMD HDR 异常截图 + 正常参考截图”。程序直接从原始像素离线拟合 9³ 或 17³ 的轻量 3D LUT，并让每组图片轮流作为独立验证集；达到平均 ΔE2000 不高于 3 且 P95 不高于 8 时停止，否则展示有限候选中的最佳结果。默认模型只用于未被样本覆盖的颜色区域，不会先压缩原始输入。

校准页会并排显示最后一组图片的校正结果和参考图。应用后回到主窗口完成最终微调，再点击“保存为默认”。“恢复内置模型”可以删除设备校准效果并回到随程序提供的模型。动画、时间、鼠标等不一致区域会降低验证分数；应优先提供静态、颜色范围不同的画面。

### 设置

“设置”集中管理当前用户开机自启、自动检查更新和网络线路。GitHub 前缀线路与 HTTP 网络代理是两层独立配置：前者改写 GitHub 请求 URL，后者只控制 HTTP 客户端如何出网。前缀线路会向对应第三方服务暴露完整 GitHub 下载 URL；无论使用哪条线路，更新仍执行 RSA 签名、SHA-256、版本、通道和包结构校验。开机自启默认关闭，使用当前用户的 Windows `Run` 注册项，不需要管理员权限。

配置和更新日志位于：

```text
%LOCALAPPDATA%\AmdHdrScreenshotFixer
```

应用启动后会后台检查更新，也可点击“检查更新”。更新清单使用仓库专属 RSA 公钥验签，下载包还会校验 SHA-256、产品、版本、通道、大小和 ZIP 结构。安装器卸载程序时默认保留用户配置；如需彻底清理，可手动删除上述目录。

## 常见问题

- Lite 无法启动：运行 `dotnet --list-runtimes`，必须能看到 `Microsoft.WindowsDesktop.App 8.x` 且架构为 x64；只有 `Microsoft.NETCore.App`、ASP.NET Runtime 或 x86 版本不满足 WPF 依赖。确认后仍失败请保留完整弹窗文字。安装 [.NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/8.0/runtime)。
- 不能就地更新：旧式单 EXE 或开发目录没有安装标记，请从 Release 下载 Setup 或 Portable ZIP 完成一次迁移。
- 监听没有输出：确认开关已开启、目录存在、输入为 PNG，且文件不在任何 `fixed` 子目录中。
- 自动更新失败：应用不会安装未通过签名或哈希验证的内容；详细记录见 `%LOCALAPPDATA%\AmdHdrScreenshotFixer\update.log`。

## 自行编译

要求：Windows 10/11 x64、.NET 8 SDK 或更高版本。生成安装器还需 Inno Setup 6。

```powershell
dotnet restore .\AmdHdrScreenshotFixer.Gui\AmdHdrScreenshotFixer.Gui.csproj
dotnet build .\AmdHdrScreenshotFixer.Gui\AmdHdrScreenshotFixer.Gui.csproj -c Release
dotnet run --project .\AmdHdrScreenshotFixer.SmokeTests\AmdHdrScreenshotFixer.SmokeTests.csproj -c Release
dotnet run --project .\AmdHdrScreenshotFixer.Gui\AmdHdrScreenshotFixer.Gui.csproj
```

重新生成图标：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Icon.ps1
```

构建四种本地发布包：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Release.ps1
```

未提供私钥时只生成安装包和 ZIP，不生成可冒充正式发布的签名清单。正式发布由标签触发 GitHub Actions，并从仓库 Secret `UPDATE_SIGNING_KEY` 读取私钥。

## 项目结构

- `AmdHdrScreenshotFixer.Gui`：WPF 主程序、离线校准、图片处理、设置、监听与更新 UI。
- `AmdHdrScreenshotFixer.Core`：签名清单、下载边界、包校验和事务安装。
- `AmdHdrScreenshotFixer.Updater`：主程序退出后的独立替换器。
- `AmdHdrScreenshotFixer.ReleaseTool`：生成包元数据、签名清单及验证发布包。
- `AmdHdrScreenshotFixer.SmokeTests`：算法、配置、监听和更新安全冒烟测试。
- `installer`、`scripts`：Inno Setup 与可重现发布脚本。

## AI 继续开发

先阅读 [CODEX_PROGRESS.md](CODEX_PROGRESS.md)，再核对 `Directory.Build.props`、Git 状态与线上 Release。版本唯一来源是 `Directory.Build.props`；标签必须严格等于 `v<Version>`。

必须保留以下边界：用户配置不写入安装目录；监听忽略 `fixed`；更新清单必须验签；包必须校验哈希、通道、元数据和路径；私钥、用户截图、日志、`temp`、`bin`、`obj` 不得提交。最低验证为 GUI/Updater/ReleaseTool 编译、SmokeTests、带私钥的 `Build-Release.ps1`、两通道 `verify` 和真实 WPF 截图。提交、推送标签、创建或替换 Release 仍需用户明确授权。

## 许可证

[MIT](LICENSE)
