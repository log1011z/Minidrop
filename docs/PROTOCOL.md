# MiniDrop 跨端协议规范 v1.1

本文从 `DESIGN.md` 提炼 C#（Windows）与 Kotlin（Android）两端共同遵守的契约。两端不共享实现代码，但对本文所述内容的行为必须一致，并由 `fixtures/` 下的共享夹具测试保证。

与设计文档冲突时以 `DESIGN.md` 为准；M0 实测结论回填后以 `docs/M0-RESULTS.md` 为准。

---

## 1. 远端路径

```
message_path(id)   = items/<month(id)>/<id>.json
tombstone_path(id) = tombstones/<month(id)>/<id>.json
file_path(file_id) = files/<file_id>
```

- `month(id)`：解码消息 ULID 前 48 位时间戳，取 UTC `YYYY-MM`。禁止用当前月份或 `created_at` 推导。
- `id`：26 字符大写 Crockford Base32 ULID；`file_id`：小写 UUID v4。
- 月份目录在首次写入前惰性 `MKCOL`；`MKCOL` 返回 201/405（已存在）均视为成功（M0 固化具体码）。

## 2. ULID 规则

- 字母表（Crockford Base32，32 字符，按位值升序）：
  `0123456789ABCDEFGHJKMNPQRSTVWXYZ`（排除 I、L、O、U）。
- 结构：48 bit 毫秒时间戳（前 10 字符）+ 80 bit 随机数（后 16 字符）。
- 生成：时间戳取当前 UTC 毫秒；同进程内必须单调不减——同一毫秒内将 80 位随机部分视作大端计数器 +1，溢出则推进到下一毫秒。
- 解码时间戳：仅使用前 10 个字符；忽略大小写输入，输出统一大写。
- 生命周期与月份只看 ULID 时间戳；`created_at` 只负责展示排序。

## 3. 消息 JSON Schema v1

固定字段（未知字段必须忽略）；UTF-8、无 BOM；序列化字段顺序不参与正确性。

| 字段 | 类型 | 规则 | 违反码 |
|---|---|---|---|
| `version` | int | 恒为 `1`；其他值拒绝 | `SCHEMA_VERSION` |
| `id` | string | 26 字符大写 ULID，必须与远端文件名一致 | `BAD_ULID` / `ID_MISMATCH` |
| `device_id` | string | UUID v4 | `BAD_DEVICE` |
| `device_name` | string | trim 后 1–32 个 Unicode 字符 | `BAD_DEVICE_NAME` |
| `created_at` | string | RFC 3339 UTC，含毫秒（`...Z`） | `BAD_CREATED_AT` |
| `text` | string\|null | null 或 UTF-8 ≤ 100 KiB | `TEXT_TOO_LONG` |
| `files` | array\|null | null 或 ≤ 50 项 | `TOO_MANY_FILES` |
| `files[].id` | string | UUID v4，消息内唯一 | `BAD_FILE_ID` / `DUP_FILE_ID` |
| `files[].name` | string | UTF-8 ≤ 200 字节；不含 `/`、`\`、控制字符 | `BAD_FILE_NAME` |
| `files[].size` | int | 0 ≤ size ≤ 500 MB（配置上限） | `BAD_FILE_SIZE` |
| `files[].mime` | string\|null | 可空；非空时 ≤ 127 个 ASCII 字符 | `BAD_MIME` |
| `files[].sha256` | string\|null | 可空；非空时 64 位小写十六进制 | `BAD_SHA256` |

- 有效性：至少一段 trim 后非空 `text`，或至少一个文件；否则拒绝（`EMPTY_CONTENT`）。
- JSON 解析失败或体积 > 2 MiB：拒绝（`BAD_JSON` / `TOO_LARGE`）。
- 数字字段必须是非负整数；类型不符拒绝（`BAD_JSON`）。

### Tombstone JSON Schema v1

```json
{ "version": 1, "id": "<26位ULID>", "deleted_at": "<RFC3339 UTC ms>", "deleted_by": "<UUID v4>" }
```

同步只依赖墓碑**文件名**；正文损坏不影响删除语义，但删除方仍写合法 JSON。

## 4. 错误码

### 4.1 传输/任务错误码（upload_jobs.error_code 等）

| 码 | 场景 | 分类 |
|---|---|---|
| `NETWORK` | 离线、连接重置、DNS、超时 | 暂时 |
| `RATE_LIMIT` | 429、带限流语义 503 | 暂时 |
| `SERVER` | 其他 5xx | 暂时 |
| `AUTH` | 401、403 | 终止 |
| `SOURCE_MISSING` | 源文件被移走 | 终止 |
| `SOURCE_CHANGED` | 上传前 size/mtime 与入队不符 | 终止 |
| `FILE_TOO_LARGE` | 超过配置上限 | 终止 |
| `QUOTA` | 明确的账户流量/空间限制 | 终止 |
| `PROTOCOL` | 非预期响应、无法安全判定成功 | 终止 |

暂时错误退避序列：10s、30s、2m、5m、15m；第 5 次失败转 `failed`。`Retry-After` 与本地退避取较长者。

### 4.2 Schema 拒绝码（见 §3 表 + `BAD_JSON` / `TOO_LARGE` / `ID_MISMATCH` / `EMPTY_CONTENT`）

拒绝进入 `rejected_items`：记录远端路径、签名（ETag，无 ETag 则 mtime+size）、原因码、次数；同一签名连续 3 次失败标记 `quarantined`，签名变化清零重试。

## 5. 状态机

### 5.1 上传任务（upload_jobs.state）

```
queued → uploading → 成功: 删除 job
uploading → 暂时错误 → retry_wait(退避) → 到期 → queued
uploading → 终止错误 → failed
failed / retry_wait → 用户重试(重新校验源文件) → queued
```

- 启动恢复：遗留 `uploading` → `queued`；文件 `uploading` → `pending`。
- 网络恢复只唤醒 `retry_wait`；修改密码只重排 `error_code='AUTH'` 的 failed job。
- 文件状态：`pending | uploading | uploaded`；重试时 `uploaded` 跳过。
- 退避第 5 次转 failed；失败 job 不单独过期。

### 5.2 下载（文件级）

```
remote → downloading → 校验成功 → cached
downloading → 失败 → failed → 用户重试 → downloading
cached → 缓存文件丢失 → remote
```

下载/取消均由用户动作触发；`.part` 在取消或失败时删除；hash/size 不符删除文件置 failed。

### 5.3 Timeline 推导

| 条件 | 展示 |
|---|---|
| out 且 job=queued | 排队中 |
| out 且 job=uploading | 正在上传 n%（bytes_done/bytes_total） |
| out 且 job=retry_wait | 等待重试 |
| out 且 job=failed | 上传失败 + 错误摘要 + 重试按钮 |
| out 且无 job | 已发布 |
| in | 普通消息，文件按各自下载状态 |

## 6. 创建 / 删除时序

### 创建（不变量 I2）
逐个 PUT `files/<uuid>`（全部成功）→ 序列化 JSON → PUT `items/<month>/<id>.json`。JSON PUT 成功即 commit；创建后绝不修改消息 JSON。

### 删除（不变量 I5/I6）
取消该消息传输 → PUT `tombstones/<month>/<id>.json`（commit）→ 本地删除行 → 逐个 DELETE `files/<uuid>`（404 视为成功）→ 最后 DELETE `items/<month>/<id>.json`。墓碑 PUT 失败则本地保持不变。崩溃恢复依赖 `R∩T` 收敛：GET item 得文件 UUID，重复 DELETE，最后删 item。

## 7. 同步算法（接收侧，全部手动触发）

- `scanMonth(m)`：分页 PROPFIND `items/<m>/` 与 `tombstones/<m>/` → 过滤非法文件名 → `V = R − T`；对本地命中 T 的消息本地删除；对 `R∩T` 至多 20 条执行删除收敛。目录不存在按空集合。
- 刷新（20 条）：从当前 UTC 月起回溯最多 3 个月取可见项，ULID 倒序选满 20；停止规则按 DESIGN §6.3；缺失项 GET（并发 ≤ 2）校验入库；全部目标月扫描成功才更新 `last_manual_refresh_at`。
- 加载更早（20 条）：从 `history_cursor_month` + `history_cursor_before_id` 起，回溯最多 3 个月；无论 GET 成败前移扫描边界并持久化游标；到达 90 天边界或连续 3 个月为空提示"没有更早记录"。
- 互斥：刷新 / 加载更早 / 远端维护三者互斥；重复点击只显示进度。
- 远端维护：仅扫 ≤ 90 天边界月份；单次 ≤ 100 条；`R∩T` 收敛 + `R−T` 过期项清理；与刷新互斥。

## 8. PROPFIND 分页约定（M0 回填点）

- 请求：`PROPFIND Depth:1`，Body 只请求 `resourcetype/getetag/getlastmodified/getcontentlength`。
- 响应解析：`DAV:` 命名空间 `multistatus/response`；`href` 解码后取末段为对象名；`collection` 项排除。
- 分页：坚果云单页上限 750 项。适配器约定：若响应中存在 `<responsedescription>` 内含下一页 href（或 M0 确认的其他字段），则继续请求；`IPropfindPager`/`PropfindPager` 单点封装，M0 结论回填后只改该类。**严禁**把第一页当作完整集合。

## 9. HTTP 约定

- 只允许 HTTPS；拒绝明文。
- 路径段百分号编码：对每段 `URI encode`（保留 `/`）；ULID/UUID 均为 ASCII，不受影响。
- PUT 消息/文件使用 `Content-Length`（长度已知），不使用 chunked。
- 元数据 GET 并发 ≤ 2；上传串行；`RequestGate` 按操作类型计数。

## 10. 共享夹具（fixtures/）

| 路径 | 内容 |
|---|---|
| `ulid_month_cases.json` | ULID ↔ 时间戳 ↔ UTC 月份（跨月/闰年/年界/边界毫秒） |
| `messages/manifest.json` | 列出 valid 与 invalid（含期望拒绝码）夹具 |
| `messages/valid/*.json` | 合法消息（纯文字/单文件/多文件/未知字段/边界） |
| `messages/invalid/*.json` | 非法消息（每个文件期望一个拒绝码） |
| `tombstones/valid/*.json` | 合法墓碑 |
| `tombstones/invalid/*.json` | 非法墓碑 |

两端测试必须逐个文件断言"接受/拒绝 + 拒绝码"一致。
