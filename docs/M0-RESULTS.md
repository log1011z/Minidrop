# M0 实测结论（模板）

> 用 `m0/MiniDrop.M0` 工具在真实坚果云账号上执行后回填本文。
> 执行前准备：坚果云网页版 → 账户信息 → 安全选项 → 添加应用密码（供 WebDAV 使用）。
> 建议使用专用测试目录（如 `/dav/MiniDropM0Test/`），工具只操作自己生成的探针对象。

| # | 检查项 | 结果（实测） | 对约定的影响 |
|---|---|---|---|
| 1 | MKCOL 已存在目录的状态码 | 待填 | `AlreadyExistsOrCreated` 归一（当前按 405 处理） |
| 2 | 同名 PUT 覆盖与回读 | 待填 | 覆盖重试安全（当前假设可覆盖） |
| 3 | PROPFIND 分页字段与下一页形式 | 待填 | 回填 `PropfindPager`（C#/Kotlin 各一处） |
| 4 | PUT 后立即 PROPFIND 一致性 | 待填 | 刷新后 UI 立即性 |
| 5 | 429/503 是否带 Retry-After | 待填（难以主动触发，遇限流时记录） | 退避取较长者 |
| 6 | DELETE 不存在对象状态码 | 待填 | 404 视为成功 |
| 7 | 账号单文件上限/流量 | 官方说明 500MB；付费/免费请求数见帮助页 | 设置页上限默认值 |
| 8 | Android 长任务传输表现 | 待填（真机验证） | 前台通知与配额 |
| 9 | PUT 中断是否留下可覆盖的部分对象 | 待填（--aggressive 执行 8MB 实验） | 半截对象可覆盖重传 |
| 10 | 尾斜线/百分号编码行为 | 待填 | `AbsoluteUri` 编码策略 |

## 记录方式

运行工具后，把控制台输出中"结论摘要"逐条粘贴到上表"结果"列。若第 3 项发现下一页请求形式与 `<responsedescription><a href>` 约定不同，请同时修改：

- C#：`windows/src/MiniDrop.WebDav/MultistatusParser.cs`（`PropfindPager`）
- Kotlin：`android/app/src/main/java/com/minidrop/app/webdav/WebDavClient.kt`（`PropfindPager`）

并补充 `windows/tests/MiniDrop.Tests/FakeWebDavServer.cs` 的分页模拟，使两端测试与真实行为一致。
