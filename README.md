# MiniDrop

个人多设备间的 self-chat 式投递工具：把文字或文件丢进一条共同 Timeline，另一台设备在需要时手动刷新；文件本体只在点开时才下载。存储为坚果云 WebDAV，接收侧零后台请求。

> 设计文档见 [DESIGN.md](DESIGN.md)；跨端协议契约见 [docs/PROTOCOL.md](docs/PROTOCOL.md)。

## 仓库结构

```
MiniDrop/
├── DESIGN.md              # 设计文档 v1.1（冻结）
├── docs/PROTOCOL.md       # 跨端协议规范（路径/Schema/错误码/状态机/同步算法）
├── docs/M0-RESULTS.md     # M0 实测结论（用 m0 工具跑完后回填）
├── fixtures/              # 共享协议夹具（C# 与 Kotlin 测试读同一批文件）
├── m0/MiniDrop.M0/        # M0 实测控制台工具（§16 十项检查）
├── windows/               # Windows 客户端（.NET 10 + WPF）
│   ├── src/
│   │   ├── MiniDrop.Domain/       # ULID、消息/墓碑 JSON 严格校验、路径、错误码
│   │   ├── MiniDrop.Storage/      # SQLite DDL、DAO、事务（本地事实源）
│   │   ├── MiniDrop.WebDav/       # MKCOL/PROPFIND 分页/PUT/GET/DELETE、错误分类、RequestGate
│   │   ├── MiniDrop.Application/  # 发送、上传泵、手动同步、删除、下载、维护
│   │   └── MiniDrop.Windows/      # WPF UI、托盘、热键、单实例、SendTo、设置
│   └── tests/MiniDrop.Tests/      # 89 个用例（夹具驱动、状态机、同步窗口、故障注入）
└── android/               # Android 客户端（Kotlin + Compose M3 + Room + WorkManager）
    └── app/src/
        ├── main/java/com/minidrop/app/
        │   ├── core/      # 与 C# 严格对齐的协议实现
        │   ├── data/      # Room 数据库、DataStore、Keystore 加密
        │   ├── webdav/    # OkHttp WebDAV 适配
        │   ├── sync/      # 上传泵 Worker、同步协调器、删除/下载/维护
        │   └── ui/        # Compose 时间线、设置、分享接收
        └── test/          # 夹具驱动 JUnit 测试
```

两端不共享实现代码，共享：远端协议、SQLite DDL 语义、状态迁移表、JSON Schema 与测试夹具。

## 核心行为（V1 冻结规则）

1. 手动刷新只观察远端最近 20 条；更早记录每次按需加载 20 条。
2. **接收侧零后台**：不自动刷新、不轮询、不监听网络恢复；空闲时 WebDAV 请求为 0。
3. 上传由持久队列后台可靠执行：文件全部 PUT 成功后才发布消息 JSON（创建 commit）。
4. 删除顺序：墓碑 → 文件 → 消息 JSON（item 最后删除，保证崩溃可收敛）。
5. 远端文件对象名只用 UUID；原文件名只存于消息 JSON。
6. 90 天本地生命周期按 ULID 时间戳计算；远端维护按需触发（单次 ≤ 100 条）。

## Windows 构建与运行

要求：.NET 10 SDK（本机已通过 [dotnet-install](https://dot.net/v1/dotnet-install.ps1) 装到 `%LOCALAPPDATA%\Microsoft\dotnet`）。

```powershell
# 若 dotnet 不在 PATH：
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"

cd windows
dotnet build MiniDrop.slnx
dotnet test tests/MiniDrop.Tests/MiniDrop.Tests.csproj

# 运行
dotnet run --project src/MiniDrop.Windows
```

### 打包 Windows exe

两种模式（产物分别为 `windows/publish/` 与 `windows/publish-sc/`）：

```powershell
cd windows
# 依赖框架：约 1.6 MB，需目标机器装有 .NET Desktop Runtime 10
dotnet publish src/MiniDrop.Windows/MiniDrop.Windows.csproj `
  -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -o publish

# 自包含：约 66 MB，无需任何依赖（发给别人用这个）
dotnet publish src/MiniDrop.Windows/MiniDrop.Windows.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o publish-sc
```

依赖框架版要求系统可发现运行时：官方安装器会写注册表/环境变量；用户级
安装（dotnet-install 脚本）需设置用户环境变量 `DOTNET_ROOT` 指向运行时目录。

- 首次运行：创建 SendTo 菜单项；按 `Ctrl+Shift+D` 呼出；右上角 ⚙ 完成设置（坚果云账号 + **应用密码**，非登录密码）。
- 应用密码保存在 Windows 凭据管理器（目标 `MiniDrop/WebDAV`）。
- 数据库/缓存位置：`%LOCALAPPDATA%\MiniDrop\`。

## Android 构建

要求：JDK 17、Android SDK（compileSdk 35）。用 Android Studio 打开 `android/` 直接运行，或命令行：

```bash
cd android
./gradlew assembleDebug        # 产物：app/build/outputs/apk/debug/app-debug.apk
./gradlew testDebugUnitTest    # 夹具测试
```

Gradle Wrapper 已包含（`gradle-8.9`）。如 `distributionUrl` 下载慢，可在 `gradle/wrapper/gradle-wrapper.properties` 换成腾讯/阿里镜像。

## M0 实测（首次接入坚果云前执行）

```bash
set MINIDROP_PASS=<应用密码>
dotnet run --project m0/MiniDrop.M0 -- \
  --url https://dav.jianguoyun.com/dav/MiniDropM0Test/ \
  --user <坚果云账号> --pass-env MINIDROP_PASS
```

工具执行 §16 的安全检查（MKCOL 行为、PUT 覆盖、PROPFIND 分页线索、DELETE 404、编码与尾斜线等），`--aggressive` 追加大文件中断实验。结果人工核对后回填 `docs/M0-RESULTS.md`；若与约定不符，只需调整 `PropfindPager` 适配器（C# `MiniDrop.WebDav/PropfindPager`，Kotlin `webdav/WebDavClient.kt` 中的同类）。

## 数据与隐私

- 本地 SQLite/Room 与缓存均在应用私有目录；密码存 Windows 凭据管理器 / Android Keystore（AES-256-GCM）。
- 日志不含 Authorization、密码、消息正文、文件内容。
- 远端（坚果云）保存明文消息与文件——V1 接受此信任边界（见 §13）。
