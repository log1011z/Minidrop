# MiniDrop 详细设计文档

| 项 | 内容 |
|---|---|
| 版本 | v1.1 |
| 日期 | 2026-09-05 |
| 状态 | 设计冻结，可进入 M0 验证与实现 |
| 客户端 | Windows、Android |
| 存储 | 坚果云 WebDAV |

---

## 0. 设计结论

MiniDrop 是个人多设备间的 self-chat 式投递工具。用户把文字或文件丢进一条共同 Timeline，另一台设备在需要时手动刷新；文件本体只有点开时才下载。

V1 冻结以下规则：

1. 远端消息和墓碑按消息 ULID 的 UTC 月份分目录。
2. 普通刷新只观察远端最近 20 条消息；更早记录每次按需加载 20 条。
3. Windows 和 Android 接收侧均不自动刷新、不轮询、不监听网络恢复；空闲时 WebDAV 请求为 0。
4. 上传由持久队列在后台可靠执行。Android 只运行一个 upload pump，SQLite/Room 是唯一队列真相源。
5. 远端文件对象名只使用 UUID，原文件名只存于 JSON。
6. 创建顺序为“文件 → 消息 JSON”；JSON PUT 成功是创建 commit。
7. 删除顺序为“墓碑 → 文件 → 消息 JSON”；墓碑 PUT 成功是删除 commit，消息 JSON 最后删除。
8. 本地数据库不保存 `raw_json`，发送时由结构化行生成 JSON，接收时校验后拆表保存。
9. `retry_wait` 表示可自动重试的暂时错误；`failed` 表示需要用户动作的终止错误。失败任务不会单独过期。
10. 消息生命周期统一按 ULID 时间戳计算；`created_at` 只负责展示排序。

---

## 1. 定位、范围与原则

### 1.1 产品定位

> MiniDrop = 一个以聊天时间线为界面、以 WebDAV 为公共存储的个人跨设备文字和文件投递工具。

它不追求实时到达。典型操作是：设备 A 发出内容，用户拿起设备 B，打开 MiniDrop 后点击刷新或下拉刷新。它也不维护收件箱、已读、未读、对方在线状态或多端光标。

### 1.2 V1 范围

- Windows 托盘程序，`Ctrl+Shift+D` 呼出。
- Windows 粘贴文字、粘贴文件、拖入文件、系统“发送到 MiniDrop”。
- Android Timeline、文字发送、系统分享单个或多个文件。
- 手动刷新最近 20 条、按批加载更早 20 条。
- 文件按需下载、SHA-256 校验和系统打开。
- 本地持久上传队列、失败恢复和进度展示。
- 整条消息删除及跨设备 tombstone 收敛。
- 90 天本地生命周期和按需触发的远端维护。

### 1.3 非目标

- 不做实时通信、WebSocket、推送和接收侧后台任务。
- 不做已读、未读、新消息角标、发送方/接收方页面。
- 不做文件自动下载、网盘目录浏览；本地或已缓存图片支持缩略图，远端图片由用户点击后下载预览。
- 不做消息编辑、批量删除、仅本机隐藏。
- 不做多用户和权限共享。
- 不做端到端加密；远端保存明文消息和文件。
- 不保证新设备自动拥有全部 90 天历史。

### 1.4 设计原则

- SQLite/Room 是本地事实源，也是上传队列与同步索引。
- 远端对象不可变；创建和删除都用可重试、幂等的顺序实现最终收敛。
- 消息元数据与文件本体分离，接收同步不下载文件。
- 接收只由明确的用户动作发起；发送则在用户已明确投递后可靠完成。
- 协议路径只使用 ASCII 标识符，避免跨平台文件名和 URL 编码差异。

---

## 2. 总体架构

### 2.1 组件

```text
Windows                                      Android
┌────────────────────┐                    ┌────────────────────┐
│ WPF Timeline       │                    │ Compose Timeline   │
│ Hotkey / Tray      │                    │ Share Receiver     │
├────────────────────┤                    ├────────────────────┤
│ Application Layer  │                    │ Application Layer  │
│ Sync / Transfer    │                    │ Sync / Transfer    │
├────────────────────┤                    ├────────────────────┤
│ SQLite             │                    │ Room / SQLite      │
└─────────┬──────────┘                    └──────────┬─────────┘
          │              HTTPS WebDAV               │
          └──────────────────┬──────────────────────┘
                             │
                  ┌──────────▼──────────┐
                  │ Nutstore /MiniDrop │
                  │ items / tombstones │
                  │ files              │
                  └─────────────────────┘
```

两个客户端不共享实现代码，但共享：远端协议、SQLite DDL、状态迁移表、JSON Schema 和跨端测试夹具。

### 2.2 核心不变量

| 编号 | 不变量 | 直接结果 |
|---|---|---|
| I1 | 消息 ULID 和文件 UUID 在本地建消息时一次生成 | 重试不改变身份，PUT 可安全覆盖 |
| I2 | 所有文件上传成功后才 PUT 消息 JSON | 接收端看到合法 JSON 时，引用文件已经发布 |
| I3 | 创建后不修改消息 JSON | 无字段合并、版本冲突和最后写入者问题 |
| I4 | 接收端从不 LIST `/files/` | 上传中对象和孤儿对象不会进入 Timeline |
| I5 | 墓碑成功后消息在逻辑上已删除 | 后续文件和 JSON 清理可以重试 |
| I6 | 删除时最后 DELETE 消息 JSON | 清理中断时仍可读取文件 UUID 并继续删除 |
| I7 | 协议对象路径由 ID 唯一确定 | C# 与 Kotlin 不需实现一致的文件名清洗算法 |

### 2.3 时间语义

- `created_at`：发送设备提供的 RFC 3339 UTC 时间，负责 Timeline 展示和排序。
- 消息 ULID 时间戳：负责远端月份路径、历史抓取顺序和 90 天生命周期。
- 排序：`created_at DESC, id DESC`。即使设备时钟偏差导致显示顺序异常，也不改写远端对象。
- 生命周期：只解码 ULID 前 48 位时间，不读取 `created_at`。

---

## 3. 远端 WebDAV 协议

### 3.1 目录布局

```text
/MiniDrop/
├── items/
│   ├── 2026-08/
│   │   └── <message-ulid>.json
│   └── 2026-09/
│       └── <message-ulid>.json
├── tombstones/
│   ├── 2026-08/
│   │   └── <message-ulid>.json
│   └── 2026-09/
│       └── <message-ulid>.json
└── files/
    └── <file-uuid>
```

`month(id)` 取 ULID 内时间戳对应的 UTC `YYYY-MM`。消息与其墓碑必须进入同一个月份名；客户端不得用当前月份或 `created_at` 推导路径。

根目录和三个一级目录在测试连接时用 `MKCOL` 创建。月份目录在首次向该月写消息或墓碑前惰性创建。多个设备并发创建同一目录是正常情况；成功、已存在响应都归一为 `AlreadyExistsOrCreated`，具体状态码在 M0 记录后固化。

### 3.2 消息 JSON Schema v1

```json
{
  "version": 1,
  "id": "01K5AXK4Z9P8Q2M7N3R5T7VW9X",
  "device_id": "be7f3a2c-1b9d-4c2e-8f0a-3d5e6b7a9c1d",
  "device_name": "Desktop",
  "created_at": "2026-09-05T06:30:00.123Z",
  "text": "实验结果",
  "files": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "name": "result.zip",
      "size": 12345678,
      "mime": "application/zip",
      "sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
    }
  ]
}
```

| 字段 | 规则 |
|---|---|
| `version` | 整数，v1 恒为 1 |
| `id` | 26 字符大写 Crockford Base32 ULID，必须等于文件名 |
| `device_id` | UUID v4，安装时生成并持久保存 |
| `device_name` | 1–32 个 Unicode 字符，trim 后非空 |
| `created_at` | RFC 3339 UTC，含毫秒 |
| `text` | `null` 或字符串；最大 100 KiB UTF-8 |
| `files` | `null` 或数组；最多 50 项 |
| `files[].id` | UUID v4；消息内不可重复 |
| `files[].name` | 展示名，UTF-8 最大 200 字节，不含 `/`、`\` 和控制字符 |
| `files[].size` | 0 到配置的单文件上限，单位字节 |
| `files[].mime` | 可空；非空时最大 127 个 ASCII 字符 |
| `files[].sha256` | 64 位小写十六进制 |

有效消息必须至少有一段 trim 后非空的文字，或至少一个文件。JSON 响应体上限为 2 MiB，超出后停止读取并拒绝。未知字段忽略。

序列化使用 UTF-8、无 BOM、固定字段名、RFC 3339 UTC 和小写 SHA-256。字段顺序不参与协议正确性。

### 3.3 Tombstone Schema v1

```json
{
  "version": 1,
  "id": "01K5AXK4Z9P8Q2M7N3R5T7VW9X",
  "deleted_at": "2026-09-05T07:00:00.000Z",
  "deleted_by": "be7f3a2c-1b9d-4c2e-8f0a-3d5e6b7a9c1d"
}
```

同步只依赖墓碑文件名，正文用于诊断。删除方仍写合法 JSON，方便人工检查和以后升级。

### 3.4 WebDAV 操作矩阵

| 方法 | 路径 | 用途 |
|---|---|---|
| `MKCOL` | 根目录、一级目录、月份目录 | 初始化 |
| `PROPFIND Depth:1` | 某个月的 `items` 或 `tombstones` | 手动刷新、历史加载、维护 |
| `PUT` | `files/<uuid>` | 上传文件，覆盖重试 |
| `PUT` | `items/YYYY-MM/<ulid>.json` | 发布消息 commit |
| `PUT` | `tombstones/YYYY-MM/<ulid>.json` | 删除 commit |
| `GET` | 已知消息或文件路径 | 拉元数据、按需下载 |
| `DELETE` | 已知消息、墓碑或文件路径 | 删除与生命周期维护 |

实现统一处理百分号编码、尾部斜线和命名空间前缀。`PROPFIND` 只请求 `resourcetype`、`getetag`、`getlastmodified`、`getcontentlength`，不解析无关属性。

坚果云公开说明 WebDAV 单次目录返回可能受 750 项限制并支持多页加载。客户端必须实现分页适配，不能把第一页当作完整集合。分页字段及下一页请求形式由 M0 从真实响应确认后写入适配器测试，不在设计阶段猜测私有格式。

### 3.5 为什么不用 MOVE

接收端只有看到消息 JSON 后才会知道文件 UUID，而 JSON 总在文件成功 PUT 后发布。即使文件 PUT 中断，在 `/files/<uuid>` 留下半截对象，也没有消息引用它；重试覆盖同一路径即可。MOVE 不会改善接收可见性，却会增加临时目录、Destination 编码和服务器兼容性。

代价是从未被消息 JSON 引用的半截对象无法通过常规维护定位。V1 接受这种少量孤儿；设置页可在 V2 提供一次显式“全量对账”，届时才 LIST `/files/`。

### 3.6 服务限制

- V1 默认单文件最大 500 MB；设置中允许降低，不允许高于 M0 实测上限。
- 入队前检查每个文件，超过限制时整条消息不创建。
- 请求通过进程级 `RequestGate` 串行或低并发执行；元数据 GET 最大并发 2，上传消息串行。
- 429/503 尊重 `Retry-After`；无该头时使用本地退避。
- 请求数在诊断日志按操作类型计数，设置页显示最近一次刷新用量。

---

## 4. 本地数据模型

### 4.1 SQLite DDL

Windows 每次数据库操作独立租用连接；同一事务内的命令使用事务所属连接。UI 读取与上传事务不共享连接对象，连接池负责复用已释放的连接。

```sql
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS meta (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS messages (
  id           TEXT PRIMARY KEY,
  remote_month TEXT NOT NULL,
  device_id    TEXT NOT NULL,
  device_name  TEXT NOT NULL,
  created_at   TEXT NOT NULL,
  text         TEXT,
  direction    TEXT NOT NULL CHECK(direction IN ('in','out')),
  received_at  TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_messages_timeline
  ON messages(created_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS idx_messages_month
  ON messages(remote_month, id DESC);

CREATE TABLE IF NOT EXISTS files (
  file_id      TEXT PRIMARY KEY,
  message_id   TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
  idx          INTEGER NOT NULL,
  name         TEXT NOT NULL,
  size         INTEGER NOT NULL,
  mime         TEXT,
  sha256       TEXT,
  direction    TEXT NOT NULL CHECK(direction IN ('in','out')),
  source_path  TEXT,
  source_modified_at INTEGER,             -- outgoing 入队时的 mtime；incoming 为 NULL
  cache_path   TEXT,
  state        TEXT NOT NULL,
  UNIQUE(message_id, idx)
);
CREATE INDEX IF NOT EXISTS idx_files_message ON files(message_id, idx);

CREATE TABLE IF NOT EXISTS upload_jobs (
  message_id       TEXT PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
  state            TEXT NOT NULL CHECK(state IN
                    ('queued','uploading','retry_wait','failed')),
  attempts         INTEGER NOT NULL DEFAULT 0,
  next_attempt_at  INTEGER,
  bytes_done       INTEGER NOT NULL DEFAULT 0,
  bytes_total      INTEGER NOT NULL DEFAULT 0,
  error_code       TEXT,
  error_message    TEXT,
  enqueued_at      TEXT NOT NULL,
  updated_at       TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_jobs_next
  ON upload_jobs(state, next_attempt_at, enqueued_at);

CREATE TABLE IF NOT EXISTS rejected_items (
  remote_path      TEXT PRIMARY KEY,
  message_id       TEXT NOT NULL,
  remote_signature TEXT NOT NULL,
  reason_code      TEXT NOT NULL,
  fail_count       INTEGER NOT NULL,
  quarantined      INTEGER NOT NULL DEFAULT 0,
  last_failed_at   TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS sync_months (
  month                    TEXT PRIMARY KEY,
  last_scanned_at          TEXT,
  last_item_signature      TEXT,
  last_tombstone_signature TEXT
);
```

`raw_json` 不存在。本机消息在所有文件得到 SHA-256 后从 `messages + files` 序列化；远端 JSON 校验成功后在一个事务内拆成这两张表。

历史分页位置保存在 `meta`：

| 键 | 含义 |
|---|---|
| `history_cursor_month` | 下一次“加载更早”继续扫描的月份 |
| `history_cursor_before_id` | 只选择字典序小于该 ID 的远端项；空表示从该月末尾开始 |
| `history_initialized` | 是否已完成最近 20 条初始化 |
| `last_manual_refresh_at` | 设置页显示，不作为同步语义 |
| `last_remote_maintenance_at` | 限制远端维护频率 |

### 4.2 原子事务

- 创建发送消息：`messages`、全部 `files` 和 `upload_jobs` 在一个事务内写入。
- 接收消息：JSON 校验完成后，`messages` 和全部 `files` 在一个事务内写入；主键冲突用 `INSERT OR IGNORE` 收敛。
- 删除本地消息：先收集要删除的缓存路径，再在事务中删除 `messages`；事务提交后尽力删除缓存文件。
- 更新 job 状态和错误字段：每次状态转换单独提交，保证进程崩溃后可恢复。

### 4.3 上传状态机

```text
queued ──pump 取出──▶ uploading ──发布成功──▶ 删除 job
  ▲                      │
  │                      ├─ 暂时错误 ──▶ retry_wait ──到期──┐
  │                      │                                  │
  └────用户重试──────── failed ◀──── 终止错误               │
  ▲                                                         │
  └─────────────────────────────────────────────────────────┘
```

- `retry_wait`：网络中断、超时、429、503、可恢复 5xx。由队列调度器在约束满足且到期后自动转 `queued`。
- `failed`：401/403、源文件丢失、读取权限错误、文件超过限制、明确的配额错误、连续可恢复错误达到上限。只能由用户点重试或修正对应配置后恢复。
- 网络恢复只唤醒 `retry_wait`，绝不批量重试 `failed`。
- 修改账号或应用密码后，只把 `error_code='AUTH'` 的 failed job 转为 queued。
- 用户点击重试：先重新验证源文件与大小，再将指定 job 转为 queued。
- failed job 一直保留，直到发送成功、用户删除消息或消息达到 90 天生命周期。
- App 启动时将遗留的 `uploading` job 转为 `queued`；文件的 `uploading` 状态转回 `pending`。

文件上传状态为 `pending | uploading | uploaded`。文件失败原因保存在 job；重试时已 `uploaded` 的文件跳过。

### 4.4 下载状态机

```text
remote ──用户下载──▶ downloading ──校验成功──▶ cached
  ▲                       │                         │
  └────取消───────────────┤                         └─缓存丢失──▶ remote
                          └─失败──▶ failed ──重试──▶ downloading
```

下载是用户动作，不因网络恢复自动启动。下载任务可持久化，但不与上传共用队列状态。

### 4.5 Timeline 状态推导

| 条件 | 展示 |
|---|---|
| outgoing 且有 queued job | 排队中 |
| outgoing 且 uploading | 正在上传 n% |
| outgoing 且 retry_wait | 等待重试 |
| outgoing 且 failed | 上传失败、重试按钮；Windows 保留错误摘要，Android 不显示失败原因 |
| outgoing 且无 job | 已发布的普通消息 |
| incoming | 普通消息；文件按各自下载状态显示 |

“最近 20 条”只限制远端观察窗口，不限制本地数据库容量，也不删除已经加载的历史。

---

## 5. 上传管线

### 5.1 入队

1. 校验文字长度、文件数量、单文件大小、源文件可读性和可用空间。
2. 生成一个消息 ULID 和每个文件的 UUID。
3. 计算 `remote_month = month(message_id)`。
4. 在一个事务中写消息、文件和 queued job。
5. Timeline 立即显示该消息。
6. 唤醒 upload pump。

纯文字消息也建立 job，因为消息 JSON 仍需要可靠 PUT。

### 5.2 Pump 算法

Windows 和 Android 使用相同逻辑：

```text
while 有执行资格的 job:
    SELECT 最早 queued 或已到期 retry_wait
      ORDER BY enqueued_at
      LIMIT 1
    原子 claim 为 uploading
    ensure 月份目录
    逐个上传尚未 uploaded 的文件
    从结构化行生成 JSON
    PUT message JSON
    成功后删除 job
```

同一进程最多一个 pump。SQLite 是队列，操作系统调度器只负责唤醒 pump，不再维护一套“每条消息一个队列”。

### 5.3 文件上传

- 远端路径固定为 `/files/<uuid>`。
- 读取源文件时同时计算 SHA-256 和写入 HTTP 请求流，避免二次读盘。
- Windows 在上传前后核对源文件的 size 和 `source_modified_at`；期间变化则不发布 JSON，并转为 `SOURCE_CHANGED`。Android staging 文件由应用独占，仍在打开前核对 size。
- 上传途中失败时丢弃部分哈希；下次从头 PUT 并重算。
- 每个文件成功后持久化 SHA-256 和 `uploaded`。
- 进度入库节流为每 250 ms 或每 1 MiB 一次，满足任一条件即可。
- 所有文件 uploaded 后才序列化 JSON。
- JSON PUT 成功后删除 job。此时消息才对其他设备可见。

### 5.4 退避与错误分类

暂时错误退避：10 秒、30 秒、2 分钟、5 分钟、15 分钟；第五次之后转 `failed`，避免无限耗费请求。若服务端返回合法 `Retry-After`，采用两者较长值。

| error_code | 例子 | 状态 |
|---|---|---|
| `NETWORK` | 离线、连接重置、DNS、超时 | retry_wait |
| `RATE_LIMIT` | 429、带限流语义的 503 | retry_wait |
| `SERVER` | 其他 5xx | retry_wait |
| `AUTH` | 401、403 | failed |
| `SOURCE_MISSING` | 源文件被移走 | failed |
| `SOURCE_CHANGED` | 上传前 size/mtime 与入队不符 | failed |
| `FILE_TOO_LARGE` | 超过配置上限 | failed |
| `QUOTA` | 明确的账户流量/空间限制 | failed |
| `PROTOCOL` | 非预期响应、无法安全判定成功 | failed |

### 5.5 Android staging

Android 分享得到的 `content://` 可能只允许读取一次。`ShareReceiverActivity` 必须先复制到 `filesDir/staging/<uuid>`，复制成功后才在数据库创建消息。

- 复制前若 provider 提供大小，先检查空间和 500 MB 上限。
- 大小未知时流式复制并执行硬上限；越界即删除 staging 临时文件。
- 所有附件全部复制成功后一次入队，任一失败都不创建半条消息。
- 上传成功或消息删除后删除 staging 文件。
- 启动时清理没有数据库引用且超过 48 小时的 staging 文件。

Android 使用唯一上传任务链：`minidrop_upload_pump`，策略为 APPEND_OR_REPLACE，约束网络可用。Worker 循环消费数据库队列；追加唤醒保证队列耗尽与 Worker 结束之间的新分享仍会执行。重试定时器使用独立任务 `minidrop_upload_pump_retry`，仅唤醒串行上传链，不取消正在上传的任务。Worker 和发送入口等待设置、凭据与启动恢复完成后执行。需要前台运行的长上传显示系统进度通知；M0 在目标 Android 版本上验证长任务和系统配额行为。

---

## 6. 手动同步与历史加载

### 6.1 触发边界

| 事件 | Windows | Android |
|---|---|---|
| 启动应用 | 只显示本地缓存 | 只显示本地缓存 |
| 打开窗口/页面 | 不请求 WebDAV | 不请求 WebDAV |
| 托盘常驻/应用后台 | 0 次接收请求 | 0 次接收请求 |
| 网络恢复 | 不触发接收同步 | 不触发接收同步 |
| 用户刷新 | 顶栏按钮或 F5 | 下拉刷新或顶栏按钮 |
| 历史加载 | 点击“加载更早” | 点击“加载更早” |

上传队列的后台网络行为不属于接收同步；它只处理用户已经明确发送的内容。

### 6.2 月份扫描原语

`scanMonth(month)` 顺序读取两个完整目录集合：

1. 分页 `PROPFIND /items/<month>/` 得到 `R_month`。
2. 分页 `PROPFIND /tombstones/<month>/` 得到 `T_month`。
3. 过滤非法文件名，解析 ETag/mtime/size 作为远端签名。
4. 逻辑可见集合为 `V_month = R_month - T_month`。
5. 对本地属于该月且 ID 在 `T_month` 的消息执行本地删除。
6. 对 `R_month ∩ T_month` 中至多 20 条执行删除收敛：GET item 取得文件 UUID，DELETE 全部文件，全部成功后最后 DELETE item。失败项留给下次扫描或远端维护。

先取得墓碑集合再选可见消息，避免把已删除 JSON 重新导入。若某个月份目录不存在，按空集合处理；其他错误使本次扫描失败，不更新成功时间。

### 6.3 普通刷新：最近 20 条

刷新目标是发现“当前远端最近 20 条逻辑可见消息”。算法：

```text
remaining = 20
month = 当前 UTC 月
selected = []

while remaining > 0 and 回溯月份数 < 3:
    scanMonth(month)
    从 V_month 按 ULID 倒序取最多 remaining 条
    加入 selected
    remaining = 20 - selected.count
    根据停止规则决定是否扫描上月
    month = previousMonth(month)

对 selected 中本地不存在且未 quarantine 的项 GET JSON
校验后按事务入库
```

停止规则：

- 首次安装或本地消息少于 20：逐月向前，最多 3 个月，尽量凑满 20。
- 已初始化且当前月有至少 20 个可见远端项：只扫描当前月。
- 已初始化但当前月不足 20：扫描“构成本地最近 20 条的月份”，通常是当前月和上月；最多 3 个月。
- 即使本地已有候选消息，也要处理扫描月份内全部墓碑文件名，因为删除同步不产生 JSON GET。

对最近 20 条的缺失项，元数据 GET 最大并发 2；全部结果落库后一次刷新 UI。某一项失败不回滚其他项。

这个策略有明确语义：两次刷新之间若远端新增超过 20 条，中间较旧的新消息不会在普通刷新中自动补齐，用户可通过“加载更早”取得。V1 接受这一取舍以减少请求。

### 6.4 历史初始化与游标

首次成功取得最近窗口后，游标必须指向“已经检查过的远端窗口边界”，不能简单使用本地最老消息。部分 JSON 可能被拒绝，同月也可能存在未取的空洞。

- `history_cursor_month` = 本次选中最老候选所属月份。
- `history_cursor_before_id` = 本次选中候选中最小的 ULID。
- 如果月份已完全耗尽，则游标移到前一个月，`before_id = null`。
- 刷新发现比当前窗口更新的消息时不移动历史游标。

### 6.5 加载更早：每批 20 条

```text
month = history_cursor_month
before = history_cursor_before_id
selected = []

while selected.count < 20 and 回溯月份数 < 3:
    scanMonth(month)
    candidates = V_month 中 id < before 的项（before 为空则不限）
    倒序加入 selected，直到 20 条
    若该月耗尽：month = previousMonth(month); before = null
    否则：before = selected 中最小 id

GET 本地不存在且未 quarantine 的项
无论 GET 成功、被拒绝或已在本地，都把远端扫描边界前移
持久化新游标
```

一次点击最多选择 20 个候选消息，但为凑够 20 可扫描最多 3 个月。到达 90 天边界或连续 3 个月为空时显示“没有更早记录”。用户再次点击可继续。

### 6.6 拒绝与 quarantine

对 JSON 解析失败、体积超限、字段非法、ID/路径不一致、版本不支持的项：

1. 在 `rejected_items` 记录远端路径、签名、错误码和次数。
2. 同一远端签名连续失败 3 次后标记 quarantine，后续扫描不再 GET。
3. ETag 变化；没有 ETag 时 mtime+size 变化，则清零次数并重试。
4. 应用升级且支持的 schema 版本变化时，清除 `UNSUPPORTED_VERSION` quarantine。
5. 设置页提供“重试被拒绝条目”，只清除 quarantine，不直接发请求。

这避免永久损坏或未来版本 JSON 在每次刷新时反复消耗请求。

### 6.7 互斥、取消和结果

- 同一时间只允许一个接收操作：Refresh、Load Older、远端维护三者互斥。
- 用户在进行中再次点同一动作只显示当前进度，不新建请求。
- 离开页面不取消操作；用户可以显式取消，已经入库的消息保留。
- 成功提示：`已刷新，新增 n 条`；n=0 时显示 `已是最新`。
- 部分失败：显示 `已获取 n 条，m 条暂时无法读取`，日志保存路径和原因。
- `last_manual_refresh_at` 只有至少一个目标月份完整扫描成功后才更新。

### 6.8 请求预算示例

| 场景 | 典型请求 |
|---|---|
| 当月已有 ≥20 条且无新增 | 2×PROPFIND（items+tombstones） |
| 当月 5 条、需跨到上月 | 4×PROPFIND + 缺失 JSON GET |
| 当月新增 3 条 | 2×PROPFIND + 3×GET |
| 加载更早 20 条，同月 | 2×PROPFIND + 至多 20×GET |
| 文件下载 | 1×GET 文件对象 |

若某月超过服务端单页上限，PROPFIND 次数按页增加。目录按月的主要收益是日常不触碰老月份，而不是假设每个月永远只有一页。
若扫描月份存在尚未完成的墓碑删除，还会增加对应的 item GET 和 DELETE；没有残留删除时不会产生这些请求。

---

## 7. 删除协议

### 7.1 用户删除流程

用户确认删除整条消息后：

```text
取消该消息正在进行的上传/下载
        ↓
PUT tombstone                         ← 删除 commit
        ↓ 成功
本地 Timeline 立即隐藏并删除数据库行
        ↓
DELETE 每个 /files/<uuid>
        ↓ 全部成功或 404
DELETE /items/YYYY-MM/<ulid>.json     ← 必须最后
```

若墓碑 PUT 失败，本地消息保持不变并提示重试。墓碑成功后，即使后续清理失败，本地也保持删除状态。

### 7.2 崩溃恢复证明

| 崩溃点 | 远端状态 | 恢复方式 |
|---|---|---|
| 墓碑前 | item/files 存在，无墓碑 | 消息仍有效，用户可再次删除 |
| 墓碑后、删文件前 | tombstone + item + files | 扫描得 `R∩T`，GET item 得到文件 UUID 后继续 |
| 删除部分文件后 | tombstone + item + 剩余 files | 重复 DELETE，404 视为成功 |
| 所有文件后、item 前 | tombstone + item | DELETE item |
| item 后 | 仅 tombstone | 删除完成 |

消息 JSON 最后删除是恢复能力的关键。若先删 item 再删文件，崩溃后无法从常规目录得到文件 UUID。

### 7.3 其他设备处理

`scanMonth` 发现本地消息 ID 位于墓碑集合时：

1. 取消对应本地下载或上传。
2. 删除数据库消息和关联文件行。
3. 删除 incoming cache 和 Android staging；绝不删除 Windows 用户原始 `source_path`。
4. 若 Worker 稍后完成但数据库行已消失，丢弃结果并清理临时文件。

由于刷新只扫描活跃月份，另一设备删除很老的消息可能在用户加载该月份或远端维护前才在本机消失。这是手动、窗口化同步模型的已知语义。

---

## 8. 文件下载与打开

### 8.1 下载

```text
用户点击下载
  → GET /files/<uuid>
  → 写 <cache>/<uuid>.part，同时计算 SHA-256
  → 校验 size 和 sha256
  → 原子改名为 <cache>/<uuid>_<display-extension>
  → 更新 state=cached、cache_path
```

缓存名只为本机打开体验使用，可由各平台安全生成，不属于跨端协议。展示名始终来自数据库 `name`。

- 取消或失败时删除 `.part`。
- hash/size 不一致时删除文件并设为 failed。
- cached 但路径不存在时自动回到 remote。
- V1 不自动下载，不在普通刷新中发送 HEAD/GET 文件请求。

### 8.2 打开

- Windows：通过 ShellExecute 打开缓存文件；扩展名从安全处理后的原始名称保留。
- Android：通过 `FileProvider` 生成 content URI，`ACTION_VIEW` 携带 JSON MIME 和临时读取权限。
- 没有关联应用时显示简短错误，不改变缓存状态。

---

## 9. Windows 客户端

### 9.1 技术选型

| 项 | 选择 |
|---|---|
| 运行时/UI | .NET 10 LTS + WPF |
| 架构 | MVVM + CommunityToolkit.Mvvm |
| 数据 | Microsoft.Data.Sqlite + 手写 DAO |
| 托盘 | H.NotifyIcon.Wpf |
| 网络 | HttpClient + 自定义 WebDAV adapter |
| 凭证 | Windows Credential Manager |
| 分发 | self-contained 单文件或安装包，M6 决定 |

.NET 10 LTS 支持期到 2028 年 11 月，适合 2026 年新项目。

### 9.2 进程与服务

建议项目结构：

```text
MiniDrop.Domain        消息、文件、状态、校验规则
MiniDrop.Storage       SQLite migration、DAO、事务
MiniDrop.WebDav        HTTP、分页、重试分类
MiniDrop.Application   Send、Refresh、LoadOlder、Delete、Download
MiniDrop.Windows       WPF、托盘、热键、Credential Manager
MiniDrop.Tests         协议夹具与状态测试
```

核心后台对象：`UploadPump`、`RequestGate`、`TransferRegistry`、`SyncCoordinator`。接收侧没有 Timer。

### 9.3 单实例与 SendTo

- 命名互斥体判定首实例。
- 第二实例把文件路径经命名管道交给首实例，等待本地入队确认后退出；等待最长 15 秒，失败时显示提示。首实例在主界面准备好后接收请求，第二实例不打开共享数据库。
- 管道消息采用长度前缀 JSON，限制总大小和文件数，拒绝不存在路径。
- 首实例收到后直接执行入队，无需显示窗口。
- 每次启动更新 `%APPDATA%\Microsoft\Windows\SendTo\MiniDrop.lnk` 的目标路径；设置页可恢复。

### 9.4 窗口行为

- `Ctrl+Shift+D` 显示/隐藏，显示后焦点进入输入框。
- 托盘左键打开；右键包含打开、刷新、设置、退出。
- 关闭按钮和 Esc 隐藏到托盘。
- 打开只读本地数据库，不自动刷新。
- 列表使用虚拟化；窗口初次打开加载本地最近 200 条，向上滚动继续读本地页。
- 远端“加载更早”按钮只在本地历史顶部显示。

### 9.5 交互

```text
┌──────────────────────────────┐
│ MiniDrop              ⟳  ⚙  │
│ [加载更早]                   │
│ 14:03 Desktop                │
│ 这段文字拿手机上用    [复制]  │
│                              │
│ 14:05 Pixel                  │
│ paper.pdf · 12.4 MB  [下载]  │
│                              │
│ 14:07 Desktop                │
│ result.zip · 正在上传 63%    │
├──────────────────────────────┤
│ 粘贴文字或拖入文件…       ↑  │
└──────────────────────────────┘
```

- Enter 发送，Shift+Enter 换行。
- 剪贴板文件或截图先加入附件草稿，截图保存为 PNG；文字粘贴进输入框。
- 选择或拖入多个文件可继续增删附件、补充文字，点击发送后形成一条消息；系统“发送到”保持直接投递。
- 文件卡主按钮下载完成后打开；右键可仅下载、复制文件或打开所在文件夹。
- 刷新与发送分别维护忙碌状态，远端刷新不阻止本地发送入队。
- 右键消息删除，弹一次不可恢复确认。
- 发送成功/失败可 Toast；接收不弹 Toast，因为没有后台接收。

---

## 10. Android 客户端

### 10.1 技术选型

| 项 | 选择 |
|---|---|
| 语言/UI | Kotlin + Jetpack Compose Material 3 |
| 数据 | Room + SQLite |
| 配置 | DataStore |
| 密钥 | Android Keystore AES-256-GCM |
| 后台上传 | WorkManager 单一 pump |
| 下载 | 协程 + 可选持久 WorkRequest |
| 最低版本 | minSdk 26 |

WebDAV 密码使用随机 AES-256-GCM 密钥加密；密钥保存在 Android Keystore，密文、IV 和版本放在私有 DataStore。密钥不可用或认证标签失败时要求用户重新输入密码。设计不使用已弃用的 `EncryptedSharedPreferences`。

### 10.2 组件

- `MainActivity`：Timeline、输入栏、刷新和加载更早。
- `ShareReceiverActivity`：处理 `ACTION_SEND` / `ACTION_SEND_MULTIPLE`。
- `SettingsScreen`：连接、设备名、缓存、日志和维护。
- `UploadPumpWorker`：唯一后台上传执行器。
- `MiniDropDatabase`：Room 实体、DAO 和 migration。
- `WebDavClient`：与 Windows 共享行为测试。

### 10.3 分享流程

```text
系统分享至 MiniDrop
  → 解析文字与 URI
  → 全部附件复制到 staging
  → 原子创建消息和 job
  → 唤醒唯一 upload pump
  → 提示“已加入 MiniDrop”并返回原应用
```

复制期间透明 Activity 显示进度和取消；取消后清除本次临时文件，不创建消息。

### 10.4 Timeline

- 进入页面只显示 Room 数据。
- 下拉刷新才调用 §6.3。
- 顶部“加载更早”调用 §6.5。
- 长按消息顶部的设备名/时间区域弹出删除确认；正文长按用于选择局部文字。
- 正文使用系统选择与复制菜单，链接可点击打开，不添加复制按钮；文件卡支持下载并打开、失败重试和分享给其他应用。
- 主界面选文件后先加入附件草稿，可补充文字、移除附件，再统一发送。刷新不阻止发送。
- 上传失败或等待重试时显示上传重试按钮，不显示失败原因；重试不打断正在上传的任务。
- Android 没有接收 WorkManager、周期任务或网络恢复接收逻辑。

---

## 11. 设置与首次运行

### 11.1 配置项

| 配置 | 默认值 | 说明 |
|---|---|---|
| WebDAV 根 URL | `https://dav.jianguoyun.com/dav/MiniDrop/` | 可修改 |
| 账号 | 空 | 坚果云账号 |
| 应用密码 | 空 | 使用应用专用密码 |
| 设备名 | 主机名/机型 | 1–32 字符 |
| 下载目录 | 平台私有目录 | Windows 可选目录，Android 二选一 |
| 单文件上限 | 500 MB | 可降低；M0 后按实测修订 |
| 发送成功通知 | 开 | 可关闭 |
| 发送失败通知 | 开 | 可关闭 |
| 保留期 | 90 天 | V1 固定 |

没有轮询间隔和接收通知设置，因为 V1 不执行自动接收。

### 11.2 测试连接

使用当前表单值：

1. `PROPFIND` 根 URL，验证认证和服务可达。
2. 确保 `items`、`tombstones`、`files` 三个一级目录存在。
3. 不扫描消息、不触发刷新。
4. 连接配置变更时，测试成功后才保存；仅修改下载目录、设备名等本地偏好无需联网测试。密码不写日志。

### 11.3 配置变更

| 变更 | 行为 |
|---|---|
| 应用密码 | 保存后只重排 AUTH failed job；不自动接收 |
| 账号或根 URL | 视为切换数据集；必须确认且当前没有活动上传，随后清空消息索引、下载缓存、拒绝记录和历史游标，用户原始文件不删除 |
| 设备名 | 只影响以后创建的消息 |
| 下载目录 | 新下载写新目录；已有 `cache_path` 保持 |
| 单文件上限 | 只限制新入队；已有 job 继续按创建时规则执行 |

首次配置完成后进入空 Timeline，用户主动刷新取得最近 20 条。

数据集身份由规范化后的根 URL 与账号共同决定。V1 不在一份数据库中混合展示多个 WebDAV 数据集；用户取消切换时保持原配置和本地数据不变。

---

## 12. 生命周期与维护

### 12.1 本地清理

App 启动和手动刷新结束后可执行零网络的本地清理：

- ULID 时间戳早于当前 UTC 时间 90 天的消息、files 和 upload_jobs 删除。
- 删除关联的下载缓存和 Android staging；不删除 Windows source_path。
- 无数据库引用且超过 48 小时的 `.part` / staging 删除。
- failed job 不执行独立的 7 天清理。
- Android 尚有上传任务的消息及其 staging 源文件保留，避免长时间离线后丢失待发送内容；仅回收无数据库引用的过期暂存文件。

### 12.2 远端维护触发

远端维护不使用后台定时器。触发方式：

- 用户在设置页点“清理 90 天前远端记录”；或
- 用户执行手动刷新后，若距上次远端维护超过 7 天，在刷新完成后执行一次受限维护。

设置页显示预计扫描月份和最近维护时间。维护与刷新互斥。

### 12.3 远端维护算法

维护只扫描与 90 天边界相关及更老的月份目录，不扫描 `/files/`：

1. 对扫描月取得完整 `R` 和 `T`。
2. 对 `R∩T`：GET item，依次 DELETE 文件，最后 DELETE item。
3. 对 `R-T` 且 ULID 已过期：GET item，校验可读取的文件引用，DELETE 文件，最后 DELETE item。
4. item 已不存在的过期墓碑可 DELETE；未能确认 item 清理完成时保留墓碑。
5. 单次最多处理 100 条，剩余留到下次维护，避免请求风暴。

对损坏 JSON 无法得知其文件引用时，只删除过期 item 并记录“可能存在孤儿”；常规设计无法安全枚举并判断该对象引用的文件。

### 12.4 生命周期一致性

本地和远端都按 ULID 时间戳判断 90 天。离线超过 90 天的设备启动后会先删本地过期记录，不需要依赖已经过期的墓碑，因此墓碑也可以在完成远端本体清理后过期删除。

---

## 13. 安全、日志与隐私

- 只允许 HTTPS；默认拒绝证书错误和明文 HTTP。
- 使用坚果云应用专用密码，不保存账号主密码。
- Windows 密码保存在 Credential Manager；Android 使用 Keystore AES-GCM。
- 日志不记录 Authorization、密码、消息正文、文件内容和完整查询响应。
- 日志可记录方法、脱敏后的对象类型、HTTP 状态、耗时、字节数和错误码。
- 本地数据库和缓存位于应用私有目录；用户主动选择 Windows 公共目录时由 UI 提示其可见性。
- 坚果云可以读取远端明文。V1 接受此信任边界。
- JSON 和路径解析必须设置体积、数量和字符限制，防止异常远端内容耗尽内存或写出缓存目录。

---

## 14. 错误与用户文案

| 场景 | 文案 | 行为 |
|---|---|---|
| 未配置 | 请先完成 WebDAV 设置 | 打开设置 |
| 401/403 | 账号或应用密码不可用 | job failed |
| 网络中断 | 网络不可用，稍后继续上传 | 上传 retry_wait；刷新直接结束 |
| 429/503 | 服务繁忙，稍后继续上传 | 退避 |
| 源文件丢失 | 原文件不存在或已移动 | job failed |
| 源文件变化 | 文件在发送后发生变化，请重新发送 | job failed |
| 文件过大 | 单个文件不能超过 500 MB | 不入队 |
| 下载校验失败 | 文件校验失败，请重试 | file failed |
| JSON 被拒绝 | 不在 Timeline 展示 | 日志/rejected_items |
| 墓碑 PUT 失败 | 删除失败，请检查网络 | 保留本地消息 |
| 刷新部分失败 | 已获取 n 条，m 条暂时无法读取 | 保留成功项 |

错误信息用于用户决策；异常栈只进诊断日志。

---

## 15. 测试设计

### 15.1 跨端协议夹具

同一组 fixture 同时由 C# 和 Kotlin 测试读取：

- 合法文字、文件、混合消息。
- 空正文、重复文件 UUID、错误 SHA、超长名称、超大 JSON。
- ULID 到 UTC 月份的跨月、闰年和时区边界。
- 路径只含 UUID/ULID，不依赖显示名。
- 未知字段接受、未知更高版本拒绝并 quarantine。

### 15.2 状态机测试

- crash 后 uploading → queued。
- retry_wait 到期自动重排，failed 不因网络恢复重排。
- 修改密码只重排 AUTH。
- failed job 不随时间单独删除。
- 已 uploaded 文件在 JSON PUT 重试时不再上传。
- Android 多次唤醒仍只有一个 pump 消费 FIFO。

### 15.3 同步测试

- 当月最近 20 条、跨月凑 20、最多 3 个月。
- 21–40 条通过历史游标取得，刷新新消息不移动旧游标。
- 两次刷新间新增 35 条时，刷新取最新 20，加载更早补中间 15。
- tombstone 从可见集合排除，并删除已有本地行。
- 分页目录必须聚合全部页后排序。
- quarantine 第三次生效，签名改变后恢复尝试。
- 并发 refresh 合并，refresh 与 load older 互斥。

### 15.4 删除故障注入

在墓碑前、墓碑后、每个文件 DELETE 后和 item DELETE 前强制终止进程，验证下一次扫描能通过 `R∩T` 收敛。测试必须断言 item 只在全部文件 DELETE 成功后删除。

### 15.5 真实服务集成测试

默认跳过、用专用测试目录运行：MKCOL 并发、覆盖 PUT、DELETE 404、中文 JSON 内容、分页、ETag/mtime、限流、500 MB 上限、PROPFIND 一致性延迟。测试后只删除自己生成的前缀对象。

---

## 16. M0 实测清单

实现前用最小控制台工具在真实坚果云账号验证并记录：

1. MKCOL 已存在、父目录不存在时的真实状态码。
2. 同名 PUT 覆盖与 PUT 完成后的对象可见性。
3. PROPFIND 分页字段、下一页请求和无序返回行为。
4. PUT 后立即 PROPFIND 的一致性延迟。
5. 429/503 是否带 `Retry-After`。
6. DELETE 不存在对象的状态码。
7. 账号实际单文件上传限制和流量限制。
8. Android 目标版本上 100 MB 与接近上限文件的后台传输表现。
9. 请求取消后服务器是否留下可覆盖的部分对象。
10. 路径尾斜线、百分号编码和自定义根目录行为。

M0 只验证服务事实，不做压力测试。若结果与本文不同，更新 WebDAV adapter 约定和相应测试，再开始 UI 实现。

---

## 17. 实施里程碑

| 里程碑 | 内容 | 完成条件 |
|---|---|---|
| M0 | 坚果云与 Android 传输 spike | §16 结论落档 |
| M1 | 共享协议规范和测试夹具 | C#/Kotlin 对同一 fixture 结果一致 |
| M2 | Windows 存储、WebDAV、pump、手动刷新 | 文字跨实例收发，最近 20/历史加载通过 |
| M3 | Windows UI、托盘、热键、SendTo、文件 | 500 MB 边界内重启恢复和按需下载通过 |
| M4 | 删除与维护 | 全部故障注入点最终收敛 |
| M5 | Android Room、Timeline、分享、单 pump | 与 Windows 双向互通 |
| M6 | 安全、日志、分发和两设备日用验收 | 无接收后台请求，错误文案和升级路径通过 |

建议先完成 M0–M4 的 Windows 端，把协议和状态机跑实，再实现 Android；跨端夹具从 M1 开始固定，避免两端各自解释协议。

---

## 18. V2 候选

1. 显式全量对账工具：扫描 `/files/` 清理孤儿。
2. 小图片缩略图与可选自动下载。
3. 本地缓存容量上限和 LRU。
4. Android 用户发起数据传输 API 适配超长任务。
5. 可选“打开应用时自动刷新”，默认仍关闭。
6. 端到端加密与密钥导入。

---

## 附录 A：关键路径

```text
message_path(id)   = /items/<month(id)>/<id>.json
tombstone_path(id) = /tombstones/<month(id)>/<id>.json
file_path(file_id) = /files/<file_id>
```

只有 `file_id` 参与远端文件路径。`files[].name` 只负责显示和生成本地缓存名。

## 附录 B：参考 SQL

```sql
-- Timeline 本地分页
SELECT m.id, m.device_name, m.created_at, m.text, m.direction,
       j.state AS job_state, j.bytes_done, j.bytes_total,
       j.attempts, j.error_code, j.error_message
FROM messages m
LEFT JOIN upload_jobs j ON j.message_id = m.id
ORDER BY m.created_at DESC, m.id DESC
LIMIT :limit OFFSET :offset;

-- upload pump 选择候选；claim 必须放在事务中二次核对状态
SELECT message_id
FROM upload_jobs
WHERE state = 'queued'
   OR (state = 'retry_wait' AND next_attempt_at <= :now)
ORDER BY enqueued_at
LIMIT 1;
```

## 附录 C：资料依据

- 坚果云 WebDAV 帮助：默认单文件限制 500 MB、免费版每 30 分钟 600 次请求、付费版 1500 次、目录单次 750 项并支持分页：<https://help.jianguoyun.com/?p=2064>
- .NET 官方支持策略：.NET 10 为 LTS，支持到 2028-11-14：<https://dotnet.microsoft.com/en-us/platform/support/policy>
- Android Keystore 与推荐密码算法：<https://developer.android.com/privacy-and-security/cryptography>
- Android WorkManager 管理唯一任务：<https://developer.android.com/develop/background-work/background-tasks/persistent/how-to/manage-work>
- Android 长任务与 Android 16 配额说明：<https://developer.android.com/develop/background-work/background-tasks/persistent/how-to/long-running>
