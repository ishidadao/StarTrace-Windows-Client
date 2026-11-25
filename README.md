# 星迹 StarTrace Windows Client

星迹是一款基于 WinUI 3 的开源抽卡记录客户端，支持原神与鸣潮。客户端坚持本地优先：记录先按“游戏 + UID”隔离保存到本机，再由用户通过 Ishidadao ID 统一认证，同步到星迹服务。
注：本客户端为[星迹 StarTrace Server](https://github.com/ishidadao/StarTrace-Server/)配套客户端，单独使用客户端可能使用体验~~极为~~不佳,请注意

## 功能特色

- **原神与鸣潮双数据源**：两款游戏的数据获取、卡池映射和本地账户相互独立。
- **一键读取记录**：打开游戏内祈愿历史或唤取记录页面后，自动扫描本机日志和 Web 缓存。
- **严格的 UID 隔离**：本地保存和上传均按“游戏 + UID”分组，不混合不同账号的记录。
- **可靠去重**：保留游戏记录的稳定标识；鸣潮记录使用稳定字段和 `count` 生成身份信息。
- **卡池统计**：展示角色活动、武器活动、角色常驻、武器常驻等分类，以及五星记录和当前垫抽。
- **角色与武器图标**：异步加载并缓存原神、鸣潮物品元数据；断网时可继续使用本地缓存。
- **UIGF 支持**：原神支持 UIGF v2/v3/v4 导入和 UIGF v4.2 导出，多 UID 文件会独立分组。
- **离线队列**：服务器不可用时记录仍写入本机，恢复网络并重启后可补传待同步数据。
- **统一认证同步**：点击“统一认证登录”直接打开系统浏览器，登录后自动返回客户端；不在客户端填写用户名、密码或服务器地址。不登录也可使用本地功能。
- **凭据保护**：采用授权码 + S256 PKCE，不嵌入应用密钥。访问令牌和刷新令牌均使用 Windows DPAPI 按当前系统用户加密保存，过期前自动刷新。
- **一键上传**：将原神、鸣潮全部本地 UID 加入队列；未登录时先打开统一认证，失败后保留队列供重试。
- **一键导出**：无需登录或联网即可导出全部游戏、UID 的完整 JSON 备份，可在星迹网页“导入记录”中恢复。原神 UIGF 导出仍保留。

## 系统要求

- Windows 10 1809 或更新版本，推荐 Windows 11
- x64 处理器
- 使用 MSI 或自包含发行包时，无需另行安装 .NET 运行时

## 安装

### MSI 安装包

从 GitHub Releases 下载 `StarTrace-Setup-x64.msi`，双击运行。安装向导提供三个可选功能：

1. **开机自启动**：登录 Windows 后自动启动星迹，默认不选择。
2. **桌面快捷方式**：在公共桌面创建快捷方式，默认选择。
3. **开始菜单快捷方式**：在开始菜单创建“星迹 StarTrace”，默认选择。

可在 Windows“设置 → 应用 → 已安装的应用”中修复、修改或卸载。

### 便携式自包含版本

下载并解压完整的 `win-x64` 发布目录，然后运行 `StarTrace.Client.exe`。不要只复制单个 EXE；WinUI 3 和 Windows App SDK 自包含版本需要保留同目录运行库。

## 使用方法

### 读取原神记录

1. 启动原神并进入“祈愿 → 历史记录”。
2. 等待历史记录页面完整显示。
3. 返回星迹，选择“原神”，点击“一键读取记录”。
4. 自动识别失败时，可在高级输入框粘贴完整的 `getGachaLog` 链接。

### 读取鸣潮记录

1. 启动鸣潮并进入“唤取 → 唤取记录”。
2. 等待记录页面完整显示，使游戏把授权信息写入本机日志。
3. 返回星迹，选择“鸣潮”，点击“一键读取记录”。
4. 自动识别失败时，可在高级输入框粘贴完整唤取记录链接。

授权地址只在本机用于请求游戏官方接口，不会作为抽卡记录上传到星迹服务器。

### 使用统一认证登录

1. 点击“统一认证登录”，系统浏览器会打开 `https://id.ishidadao.ltd`。
2. 使用与星迹网页相同的统一账号登录并授权“星迹 Windows 客户端”。账号需要 `startrace-users` 访问权限。
3. 浏览器会通过本机随机端口返回授权结果，客户端自动保存加密凭据并同步记录；等待期间可点击“取消登录”。
4. 登录后的按钮显示“同步记录”，不需要每次重新打开浏览器；重新启动后的待同步记录也会自动补传。
5. “退出登录”会清理本机凭据并尽力撤销令牌，保留本地记录和离线队列，不会退出其他应用的网页登录。

“服务在线”与“已登录云端”分别显示。没有客户端凭据时会显示“服务在线 · 未登录云端”，不会把浏览器里已有的网站登录当作桌面客户端登录。

读取和导入的记录即使尚未登录也会进入待上传队列；统一认证后会补传。一键上传按游戏和 UID 分组、按批次发送，只有服务器确认整个账号后才移出队列；鸣潮同秒记录会保持在同一批次以免丢失十连中的重复物品。

统一认证使用已注册的 public client `startrace-windows`，无客户端密钥；回调仅监听 `127.0.0.1` 随机端口。令牌只会发送到正式星迹 API `https://startrace.ishidadao.ltd`，不会发送到旧版保存的其他服务器。

升级后旧版记录和待同步队列保留，首次使用需要点击统一认证登录；不会自动把旧用户名/密码会话当作统一账号。身份平台接入说明：[Ishidadao ID 文档](https://id.ishidadao.ltd/docs)。

## 本地数据与缓存

| 内容 | 路径 |
| --- | --- |
| 抽卡记录、离线队列、同步设置 | `%LocalAppData%\StarTrace\client-data.json` |
| 角色/武器元数据与图标缓存 | `%LocalAppData%\StarTrace\item-icons` |

建议在迁移电脑、重装系统或批量清理记录前备份 `client-data.json`。不同 UID 的数据在文件内仍按游戏和 UID 独立保存。

## 从源码构建

### 开发环境

- Visual Studio 2022/2026，并安装“.NET 桌面开发”和“Windows 应用开发”工作负载
- .NET 8 SDK
- Windows 10/11 SDK 10.0.19041 或更高版本

项目固定使用 .NET 8 SDK。构建自包含 x64 版本：

```powershell
dotnet restore .\StarTrace.Client\StarTrace.Client.csproj
dotnet build .\StarTrace.Client\StarTrace.Client.csproj -c Release -p:Platform=x64
dotnet publish .\StarTrace.Client\StarTrace.Client.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishSingleFile=false
```

发布目录：

```text
StarTrace.Client\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish
```

主窗口使用 C# WinUI 控件树，避免部分 Windows 预览版与 Windows App SDK 1.8 组合下的非打包 XBF 加载异常。请勿仅为界面修改重新引入 `MainWindow.InitializeComponent` 或 `Expander`。

认证回归测试（Windows 上同时检查 DPAPI、退出登录和本地数据保留）：

```powershell
dotnet run --project .\Identity.Tests\Identity.Tests.csproj -c Release
```

### 构建 MSI

安装工程使用 WiX Toolset SDK，无需单独安装全局 WiX 工具：

```powershell
.\build-installer.ps1 -Version 1.0.0
```

脚本会先执行自包含发布，再生成：

```text
artifacts\installer\StarTrace-Setup-x64.msi
```

## 安全与隐私

- 不要把 `authkey`、`record_id`、完整抽卡链接、密码或访问令牌发布到 Issue、日志或截图中。
- 统一认证仅在系统浏览器中完成；客户端不收集账号密码。HTTP 仅用于本机回环授权回调。
- 星迹服务可以看到同步的游戏、UID 和抽卡记录；未登录时客户端继续本地保存。
- 图标元数据来自公开接口并在本地缓存；图片加载失败不会阻止记录列表显示。

## 免责声明

本项目是非官方开源工具，与米哈游、HoYoverse、库洛游戏及其关联公司不存在隶属、授权或背书关系。游戏名称、角色名称、武器名称及相关素材的权利归各自权利人所有。

使用者应自行遵守游戏服务条款、当地法律和所连接服务器的规则，并自行承担账号、数据、网络服务及第三方接口变更带来的风险。项目维护者不保证数据永久可用、统计结果绝对准确或第三方接口持续兼容，也不对因使用或无法使用本软件造成的直接或间接损失承担责任。

## 鸣谢

- 原神元数据读取思路参考 [FufuLauncher](https://github.com/FufuLauncher/FufuLauncher)。
- 鸣潮日志与元数据兼容思路参考 [Haiyu](https://github.com/HaiyuGame/Haiyu) 和 [WuWa Tracker](https://github.com/wuwatracker/wuwatracker)。
- Windows UI 基于 [WinUI 3 / Windows App SDK](https://github.com/microsoft/WindowsAppSDK)。

## 开源协议

客户端源码采用 [MIT License](LICENSE) 开源。第三方游戏名称、接口响应与素材不属于本项目 MIT 授权范围，使用时应遵循相应权利人的条款。
