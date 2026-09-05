package com.minidrop.app.webdav

import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.sync.withPermit
import okhttp3.Authenticator
import okhttp3.Credentials
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.Route
import org.xmlpull.v1.XmlPullParser
import java.io.IOException
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import javax.xml.parsers.SAXParserFactory

enum class DavStatus {
    SUCCESS, ALREADY_EXISTS_OR_CREATED, NOT_FOUND, AUTH_ERROR, RATE_LIMITED, QUOTA_ERROR, SERVER_ERROR, CLIENT_ERROR, NETWORK_ERROR, PROTOCOL_ERROR,
}

data class DavResult(
    val status: DavStatus,
    val httpCode: Int? = null,
    val retryAfter: Long? = null, // 毫秒
) {
    val ok: Boolean get() = status == DavStatus.SUCCESS || status == DavStatus.ALREADY_EXISTS_OR_CREATED
}

object ErrorClassifier {
    fun toErrorCode(result: DavResult): String = when (result.status) {
        DavStatus.NETWORK_ERROR -> "NETWORK"
        DavStatus.RATE_LIMITED -> "RATE_LIMIT"
        DavStatus.QUOTA_ERROR -> "QUOTA"
        DavStatus.SERVER_ERROR -> "SERVER"
        DavStatus.AUTH_ERROR -> "AUTH"
        else -> "PROTOCOL"
    }
}

data class DavItem(
    val name: String,
    val isCollection: Boolean,
    val etag: String?,
    val lastModified: Long?,
    val length: Long?,
) {
    val signature: String get() = etag ?: "${lastModified ?: 0}:${length ?: -1}"
}

/** 进程级请求闸门：元数据并发 2、上传串行、按操作类型计数（§3.6）。 */
class RequestGate {
    private val metadata = Semaphore(2)
    private val upload = Mutex()
    private val counts = ConcurrentHashMap<String, Long>()

    suspend fun <T> run(kind: String, uploadKind: Boolean, block: suspend () -> T): T {
        val counted: suspend (T) -> T = { v ->
            counts.merge(kind, 1, Long::plus)
            v
        }
        return if (uploadKind) {
            upload.withLock { counted(block()) }
        } else {
            metadata.withPermit { counted(block()) }
        }
    }

    fun snapshot(): Map<String, Long> = counts.toMap()
}

class WebDavClient(
    rootUrl: String,
    account: String,
    passwordProvider: () -> String?,
    timeoutSeconds: Long = 30,
    val gate: RequestGate = RequestGate(),
) {
    companion object {
        const val PROPFIND_BODY = """<?xml version="1.0" encoding="utf-8"?>
<D:propfind xmlns:D="DAV:"><D:prop><D:resourcetype/><D:getetag/><D:getlastmodified/><D:getcontentlength/></D:prop></D:propfind>"""
        const val SERVER_PAGE_SIZE = 750

        fun normalizeRootUrl(url: String): String {
            val t = url.trim()
            require(t.startsWith("https://")) { "只允许 HTTPS 根 URL" }
            return if (t.endsWith('/')) t else "$t/"
        }
    }

    private val root = normalizeRootUrl(rootUrl)
    private val http: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(timeoutSeconds, TimeUnit.SECONDS)
        .readTimeout(timeoutSeconds, TimeUnit.SECONDS)
        .writeTimeout(timeoutSeconds, TimeUnit.SECONDS)
        // WebDAV 只连坚果云（国内直连必通），绕过系统代理，避免 VPN 接管导致连接失败
        .proxy(java.net.Proxy.NO_PROXY)
        .authenticator(object : Authenticator {
            override fun authenticate(route: Route?, response: Response): Request? {
                if (response.request.header("Authorization") != null) return null
                return response.request.newBuilder()
                    .header("Authorization", Credentials.basic(account, passwordProvider() ?: ""))
                    .build()
            }
        })
        .build()

    fun absoluteUri(relativePath: String): String {
        val segments = relativePath.split('/').filter { it.isNotEmpty() }
        val encoded = segments.joinToString("/") { java.net.URLEncoder.encode(it, "UTF-8")
            .replace("+", "%20") }
        val trailing = if (relativePath.endsWith('/')) "/" else ""
        return root + encoded + trailing
    }

    private suspend fun classify(response: Response): DavResult {
        response.use {
            val code = it.code
            return when {
                code in intArrayOf(200, 201, 204, 207) -> DavResult(DavStatus.SUCCESS, code)
                code == 404 -> DavResult(DavStatus.NOT_FOUND, code)
                code == 405 -> DavResult(DavStatus.ALREADY_EXISTS_OR_CREATED, code)
                code == 401 || code == 403 -> DavResult(DavStatus.AUTH_ERROR, code)
                code == 507 -> DavResult(DavStatus.QUOTA_ERROR, code)
                code == 429 || code == 503 -> DavResult(
                    DavStatus.RATE_LIMITED, code,
                    it.header("Retry-After")?.let { h -> h.toLongOrNull()?.let { s -> s * 1000 } },
                )
                code >= 500 -> DavResult(DavStatus.SERVER_ERROR, code)
                else -> DavResult(DavStatus.CLIENT_ERROR, code)
            }
        }
    }

    private fun fromException(e: Exception): DavResult = when (e) {
        is IOException -> DavResult(DavStatus.NETWORK_ERROR)
        else -> DavResult(DavStatus.PROTOCOL_ERROR)
    }

    suspend fun mkCol(relativePath: String): DavResult = gate.run("Mkcol", false) {
        try {
            val req = Request.Builder().url(absoluteUri(relativePath)).method("MKCOL", null).build()
            classify(http.newCall(req).execute())
        } catch (e: Exception) {
            fromException(e)
        }
    }

    /** 确保目录存在；409（父目录缺失）时逐级创建后重试。 */
    suspend fun ensureDirectory(relativeDir: String): DavResult {
        val first = mkCol(relativeDir)
        if (first.ok || first.status == DavStatus.NOT_FOUND) return first
        if (first.httpCode == 409) {
            var acc = ""
            for (seg in relativeDir.split('/').filter { it.isNotEmpty() }) {
                acc = if (acc.isEmpty()) seg else "$acc/$seg"
                mkCol(acc)
            }
            return mkCol(relativeDir)
        }
        return first
    }

    suspend fun put(relativePath: String, content: ByteArray): DavResult = gate.run("Put", true) {
        try {
            val body = content.toRequestBody("application/octet-stream".toMediaType(), 0, content.size)
            val req = Request.Builder().url(absoluteUri(relativePath)).put(body).build()
            classify(http.newCall(req).execute())
        } catch (e: Exception) {
            fromException(e)
        }
    }

    /** 上传源文件流：同时计算 SHA-256，携带 Content-Length。 */
    suspend fun putFile(relativePath: String, file: java.io.File, onProgress: (Long) -> Unit): Pair<DavResult, String?> =
        gate.run("Put", true) {
            var sha: String? = null
            val result = try {
                val body = object : RequestBody() {
                    override fun contentType() = "application/octet-stream".toMediaType()
                    override fun contentLength() = file.length()
                    override fun writeTo(sink: okio.BufferedSink) {
                        val digest = java.security.MessageDigest.getInstance("SHA-256")
                        file.inputStream().use { input ->
                            val buf = ByteArray(64 * 1024)
                            var total = 0L
                            while (true) {
                                val n = input.read(buf)
                                if (n <= 0) break
                                digest.update(buf, 0, n)
                                sink.write(buf, 0, n)
                                total += n
                                onProgress(total)
                            }
                        }
                        sha = digest.digest().joinToString("") { "%02x".format(it) }
                    }
                }
                val req = Request.Builder().url(absoluteUri(relativePath)).put(body).build()
                classify(http.newCall(req).execute())
            } catch (e: Exception) {
                fromException(e)
            }
            result to sha
        }

    /** 读取已知对象，上限 maxBytes：超出停止并标记 TOO_LARGE。 */
    suspend fun getBytes(relativePath: String, maxBytes: Long = 2L * 1024 * 1024): Pair<DavResult, ByteArray?> =
        gate.run<Pair<DavResult, ByteArray?>>("GetMeta", false) {
            try {
                val req = Request.Builder().url(absoluteUri(relativePath)).get().build()
                http.newCall(req).execute().use { resp ->
                    val classified = classify(resp)
                    if (!classified.ok) return@run classified to null
                    val contentLength = resp.body?.contentLength() ?: -1
                    if (contentLength > maxBytes) return@run classified to null
                    val out = java.io.ByteArrayOutputStream()
                    resp.body?.byteStream()?.use { input ->
                        val buf = ByteArray(64 * 1024)
                        var total = 0L
                        while (true) {
                            val n = input.read(buf)
                            if (n <= 0) break
                            total += n
                            if (total > maxBytes) return@run classified to null
                            out.write(buf, 0, n)
                        }
                    }
                    classified to out.toByteArray()
                }
            } catch (e: Exception) {
                fromException(e) to null
            }
        }

    /** 流式下载到目标流（.part），带 SHA-256 计算与进度。 */
    suspend fun getToFile(relativePath: String, target: java.io.File, onProgress: (Long) -> Unit): Pair<DavResult, String?> =
        gate.run<Pair<DavResult, String?>>("GetFile", false) {
            var sha: String? = null
            val result = try {
                val req = Request.Builder().url(absoluteUri(relativePath)).get().build()
                http.newCall(req).execute().use { resp ->
                    val classified = classify(resp)
                    if (!classified.ok) return@run classified to null
                    val digest = java.security.MessageDigest.getInstance("SHA-256")
                    resp.body?.byteStream()?.use { input ->
                        target.outputStream().use { out ->
                            val buf = ByteArray(64 * 1024)
                            var total = 0L
                            while (true) {
                                val n = input.read(buf)
                                if (n <= 0) break
                                out.write(buf, 0, n)
                                digest.update(buf, 0, n)
                                total += n
                                onProgress(total)
                            }
                        }
                    }
                    sha = digest.digest().joinToString("") { "%02x".format(it) }
                    classified
                }
            } catch (e: Exception) {
                fromException(e)
            }
            result to sha
        }

    suspend fun delete(relativePath: String): DavResult = gate.run("Delete", false) {
        try {
            val req = Request.Builder().url(absoluteUri(relativePath)).delete().build()
            val result = classify(http.newCall(req).execute())
            // DELETE 404 视为成功（幂等收敛）
            if (result.status == DavStatus.NOT_FOUND) DavResult(DavStatus.SUCCESS, result.httpCode) else result
        } catch (e: Exception) {
            fromException(e)
        }
    }

    /** 分页 PROPFIND Depth:1；目录不存在按空集合；聚合全部页。 */
    suspend fun propfindDir(relativeDir: String): Pair<DavResult, List<DavItem>> {
        val items = ArrayList<DavItem>()
        var next: String? = null
        var first = true
        var pages = 0
        while (true) {
            val (result, page) = propfindPage(relativeDir, next, first)
            if (!result.ok) return result to emptyList()
            first = false
            pages++
            items.addAll(page.items)
            next = PropfindPager.extractNextLink(page.nextLinkDescription)
            if (next == null || pages >= 64) break
        }
        return DavResult(DavStatus.SUCCESS, 207) to items
    }

    private suspend fun propfindPage(relativeDir: String, nextUrl: String?, first: Boolean): Pair<DavResult, MultistatusPage> =
        gate.run("Propfind", false) {
            try {
                val url = if (first) absoluteUri("$relativeDir/") else nextUrl!!
                val body = PROPFIND_BODY.toRequestBody("application/xml".toMediaType())
                val req = Request.Builder().url(url)
                    .header("Depth", "1")
                    .method("PROPFIND", body)
                    .build()
                http.newCall(req).execute().use { resp ->
                    val classified = classify(resp)
                    if (classified.status == DavStatus.NOT_FOUND) return@run classified to MultistatusPage(emptyList(), null)
                    if (!classified.ok) return@run classified to MultistatusPage(emptyList(), null)
                    val bytes = resp.body?.bytes() ?: ByteArray(0)
                    classified to MultistatusParser.parse(bytes)
                }
            } catch (e: Exception) {
                fromException(e) to MultistatusPage(emptyList(), null)
            }
        }
}

data class MultistatusPage(val items: List<DavItem>, val nextLinkDescription: String?)

/**
 * PROPFIND 分页适配器 —— M0 回填点。
 * 当前约定：responsedescription 中出现下一页链接时继续；严禁把第一页当作完整集合。
 */
object PropfindPager {
    fun extractNextLink(responseDescription: String?): String? {
        if (responseDescription.isNullOrBlank()) return null
        val idx = responseDescription.indexOf("href=\"", ignoreCase = true)
        if (idx < 0) {
            val t = responseDescription.trim()
            return if (t.startsWith("http") || t.startsWith("/")) t else null
        }
        val start = idx + 6
        val end = responseDescription.indexOf('"', start)
        return if (end <= start) null else responseDescription.substring(start, end)
    }
}

/** multistatus 解析：SAX 流式，本地名匹配，容忍命名空间前缀差异。 */
object MultistatusParser {
    data class Builder(
        val items: MutableList<DavItem> = ArrayList(),
        var nextDesc: String? = null,
        var href: String? = null,
        var etag: String? = null,
        var lastModified: String? = null,
        var length: String? = null,
        var respDesc: String? = null,
        var isCollection: Boolean = false,
        var inResponse: Boolean = false,
        var textBuf: StringBuilder? = null,
        var captureTag: String? = null,
    )

    fun parse(xml: ByteArray): MultistatusPage {
        val state = Builder()
        val parser = SAXParserFactory.newInstance().apply {
            isNamespaceAware = true
            try {
                setFeature("http://apache.org/xml/features/disallow-doctype-decl", true)
            } catch (_: Exception) {
            }
        }.newSAXParser()

        val handler = object : org.xml.sax.helpers.DefaultHandler() {
            override fun startElement(uri: String?, localName: String?, qName: String?, attributes: org.xml.sax.Attributes?) {
                val name = localName ?: qName ?: return
                when (name) {
                    "response" -> {
                        state.inResponse = true
                        state.href = null; state.etag = null; state.lastModified = null
                        state.length = null; state.respDesc = null; state.isCollection = false
                    }
                    "collection" -> if (state.inResponse) state.isCollection = true
                    "href", "getetag", "getlastmodified", "getcontentlength", "responsedescription" -> {
                        state.captureTag = name
                        state.textBuf = StringBuilder()
                    }
                }
            }

            override fun characters(ch: CharArray?, start: Int, length: Int) {
                state.textBuf?.append(ch, start, length)
            }

            override fun endElement(uri: String?, localName: String?, qName: String?) {
                val name = localName ?: qName ?: return
                val text = state.textBuf?.toString()
                state.textBuf = null
                state.captureTag = null
                when (name) {
                    "href" -> if (state.inResponse) state.href = text
                    "getetag" -> if (state.inResponse) state.etag = text
                    "getlastmodified" -> if (state.inResponse) state.lastModified = text
                    "getcontentlength" -> if (state.inResponse) state.length = text
                    "responsedescription" -> state.nextDesc = state.nextDesc ?: text
                    "response" -> {
                        state.inResponse = false
                        val href = state.href
                        if (href != null && href.isNotBlank()) {
                            buildItem(href, state.isCollection, state.etag, state.lastModified, state.length)
                                ?.let { state.items.add(it) }
                        }
                    }
                }
            }
        }
        parser.parse(java.io.ByteArrayInputStream(xml), handler)
        return MultistatusPage(state.items, state.nextDesc)
    }

    private fun buildItem(href: String, isCollection: Boolean, etag: String?, lastModified: String?, length: String?): DavItem? {
        val decoded = java.net.URLDecoder.decode(href, "UTF-8")
        val trimmed = decoded.trimEnd('/')
        val lastSeg = trimmed.substringAfterLast('/', "")
        if (lastSeg.isEmpty()) return null
        val mtime = lastModified?.let {
            try {
                java.time.format.DateTimeFormatter.RFC_1123_DATE_TIME.parse(it, java.time.OffsetDateTime::from).toInstant().toEpochMilli()
            } catch (_: Exception) {
                null
            }
        }
        val len = length?.toLongOrNull()
        return DavItem(lastSeg, isCollection, etag, mtime, len)
    }
}
