<div align="center">

# MiniDrop

当前正式版：**v1.1** · [下载安装包](https://github.com/log1011z/Minidrop/releases/tag/v1.1) · [更新说明](docs/RELEASE-v1.1.md)

**个人多设备间的 self-chat 式文件与文字投递工具**

以聊天时间线为界面、以坚果云 WebDAV 为公共存储。
电脑上丢进一段文字或一个文件，拿起手机点一下刷新即可取用——反之亦然。

`Windows` · `Android` · `WebDAV` · `WPF` · `Jetpack Compose`

</div>

---

## 特性

- **聊天时间线**：文字与文件以消息形式呈现，支持多文件一条消息
- **手动可控的同步**：刷新只看最近 20 条，更早记录按需分批加载；接收侧**零后台请求**（不轮询、不推送、不监听网络）
- **可靠上传**：本地 SQLite 队列 + 后台泵，逐文件上传成功后才发布消息；失败自动退避重试，无需干预
- **跨设备删除**：墓碑协议保证任一端删除、全端收敛，崩溃可恢复
- **文件按需下载**：SHA-256 校验，点击才下载；Windows 下载目录可选，Android 支持系统目录选择（SAF）
- **安全**：HTTPS only；凭据存 Windows 凭据管理器 / Android Keystore（AES-256-GCM）；日志不落密码与正文
- **90 天生命周期**：本地按 ULID 时间戳清理，远端按需维护

## 工作原理

```
Windows (WPF)                         Android (Compose)
┌──────────────────┐                 ┌──────────────────┐
│ 时间线 / 托盘 / 热键 │                 │ 时间线 / 分享接收    │
│ 上传泵 / 手动同步   │                 │ 上传泵(WorkManager) │
│ SQLite 队列       │                 │ Room 队列          │
└────────┬─────────┘                 └────────┬─────────┘
         └────────── 坚果云 WebDAV ────────────┘
              items/<月份>/<ULID>.json
              tombstones/<月份>/<ULID>.json
              files/<uuid>
```

- 消息 ULID 的时间戳决定月份目录与 90 天生命周期
- 创建：文件全部上传成功后才发布消息 JSON（一次 PUT 即提交）
- 删除：墓碑 → 文件 → 消息 JSON（最后删除），任一步骤中断下次扫描自动收敛
- 协议细节见 [docs/PROTOCOL.md](docs/PROTOCOL.md)，完整设计见 [DESIGN.md](DESIGN.md)

## 构建

### Windows（.NET 10 + WPF）

```powershell
cd windows
dotnet build MiniDrop.slnx
dotnet test tests/MiniDrop.Tests/MiniDrop.Tests.csproj    # 93 个单元测试

# 依赖框架版（约 3.4 MB，需安装 .NET Desktop Runtime 10）
dotnet publish src/MiniDrop.Windows/MiniDrop.Windows.csproj `
  -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish

# 自包含版（约 66 MB，免安装运行时）
dotnet publish src/MiniDrop.Windows/MiniDrop.Windows.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o publish-sc
```

### Android（Kotlin + Compose，minSdk 26）

```bash
cd android
./gradlew assembleRelease     # 配置本机 keystore.properties 后生成正式签名 APK
./gradlew testReleaseUnitTest # 与 C# 共享同一批协议夹具
```

要求 JDK 17+（Android Studio 自带 JBR 25 时，请在 Gradle 设置中选择 JDK 17/21）。
国内网络可在 `gradle/wrapper/gradle-wrapper.properties` 使用腾讯镜像（本仓库默认已配置）。

## 使用

1. 在坚果云网页版「安全选项」中生成**应用密码**（不要使用登录密码）
2. Windows：启动后按 `Ctrl+Shift+D`，右上角 ⚙ 填入账号与应用密码，测试连接后保存
3. Android：⚙ 设置中同样配置
4. 任一端发送，另一端手动刷新即可看到；文件点击“下载并打开”即可取用
5. 可选：运行 `m0/MiniDrop.M0` 工具在真实账号上验证服务行为（见 [docs/M0-RESULTS.md](docs/M0-RESULTS.md)）

手机可通过系统分享菜单投递文件或文字；电脑可通过右键「发送到 → MiniDrop」投递文件。
主界面选择、拖入或粘贴文件后，会先出现在附件区，可以移除附件、补充文字，再一起发送；仅附件也可以发送。系统分享/“发送到”仍直接投递。
Windows 支持 Ctrl+V 粘贴截图，自动作为 PNG 附件加入输入区；文件卡右键提供“复制文件”“打开所在文件夹”和“仅下载”。Android 文件卡的分享按钮可直接分享给其他应用，尚未下载时会先下载。
Android 上传失败或等待重试时可点击消息上的“重试”，不显示上传失败原因。两端刷新与发送相互独立；只修改下载目录、设备名等本地偏好可以离线保存，连接配置变更才需要测试连接。
电脑分享会在文件加入本地队列后确认，失败时显示提示。两端长文字默认显示约 5 行，点击「展开全文」查看，点击「收起」恢复预览。Windows 可拖选局部文字后 Ctrl+C 或右键复制，Android 可长按正文选择文字并使用系统复制菜单；点击 http/https 或 www 链接会打开浏览器，不另加复制按钮。Android 长按消息顶部的设备名/时间区域可删除消息。

图片消息支持缩略图和应用内放大预览。本机原图或已下载缓存直接显示；远端图片先显示“点击加载图片”，点击后下载并预览，不自动下载历史图片。Android 预览支持双指缩放、拖动和复位；Windows 预览支持使用其他应用打开。暂不播放动图，预览显示静态帧。

Windows 时间线支持像素平滑滚动与拖动滚动条；刷新、新投递不会打断正在阅读旧消息的位置。滚到列表底部会继续读取本地记录，远端更早消息仍通过「加载更早」获取。

## 仓库结构

```
├── DESIGN.md            # 详细设计文档（v1.1，冻结）
├── docs/PROTOCOL.md     # 跨端协议契约（路径/Schema/错误码/状态机）
├── docs/M0-RESULTS.md   # 真实服务实测结论模板
├── fixtures/            # 跨端共享协议夹具（C# 与 Kotlin 测试读同一批文件）
├── m0/MiniDrop.M0/      # 坚果云实测控制台工具
├── windows/             # WPF 客户端（Domain/Storage/WebDav/Application/UI 分层）
└── android/             # Compose 客户端（core/data/webdav/sync/ui）
```

两端不共享实现代码，共享：远端协议、SQLite DDL 语义、状态机与测试夹具，
由 fixtures 驱动的两端测试保证行为一致。

## 测试

- Windows：xUnit，101 个用例（协议夹具、上传状态机、同步窗口、删除故障注入、下载校验、数据库并发和发送边界）。
- Android：JUnit，52 个用例（协议一致性、分享入口、暂存文件保护、上传校验值、附件草稿、刷新期间发送、重试、文件打开/分享、链接识别和本地时区显示）。
- Windows 界面检查：`dotnet run --project windows/tests/MiniDrop.UiChecks -- artifacts/ui`，使用独立测试数据库验证像素偏移、虚拟化、阅读位置、文本收起、本地与远端历史分页及草稿保留，并输出普通、窄窗口、文件与空列表的实际界面 PNG；远端检查使用本机模拟服务，不访问真实账号。

## 许可证

[MIT](LICENSE)
