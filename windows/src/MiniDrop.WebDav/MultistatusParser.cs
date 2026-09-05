using System.Xml;

namespace MiniDrop.WebDav;

/// <summary>
/// PROPFIND 分页适配器 —— M0 回填点。
/// 坚果云公开说明目录单次返回可能受 750 项限制并支持多页；私有分页字段以 M0 实测为准。
/// 当前约定：responsedescription 中出现下一页链接时继续抓取；单页达到 ServerPageSize 却无下一页链接时记录告警。
/// 严禁把第一页当作完整集合。
/// </summary>
public sealed class PropfindPager
{
    public const int ServerPageSize = 750;

    public bool WarnedIncomplete { get; internal set; }

    /// <summary>从一页响应的 responsedescription 中提取下一页链接（相对或绝对）；无则返回 null。</summary>
    public string? ExtractNextLink(string? responseDescription)
    {
        if (string.IsNullOrEmpty(responseDescription))
            return null;
        var idx = responseDescription.IndexOf("href=\"", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            // 也可能是纯 URL 文本
            var t = responseDescription.Trim();
            return t.StartsWith("http", StringComparison.OrdinalIgnoreCase) || t.StartsWith('/') ? t : null;
        }
        var start = idx + 6;
        var end = responseDescription.IndexOf('"', start);
        return end <= start ? null : responseDescription[start..end];
    }
}

/// <summary>multistatus 解析：以本地名匹配，容忍命名空间前缀差异。</summary>
public static class MultistatusParser
{
    public sealed record Page(IReadOnlyList<DavItem> Items, string? NextLinkDescription);

    public static Page Parse(Stream xmlStream)
    {
        var items = new List<DavItem>();
        string? nextDesc = null;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        using var reader = XmlReader.Create(xmlStream, settings);

        string? href = null, etag = null, lastModified = null, length = null, respDesc = null;
        bool isCollection = false, inResponse = false;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                var name = reader.LocalName;
                if (name == "response")
                {
                    inResponse = true;
                    href = etag = lastModified = length = respDesc = null;
                    isCollection = false;
                    continue;
                }
                if (!inResponse)
                {
                    // multistatus 级的 responsedescription（部分服务器用于分页下一页）
                    if (name == "responsedescription" && !reader.IsEmptyElement)
                        nextDesc ??= reader.ReadInnerXml();
                    continue;
                }

                if (name == "collection")
                {
                    isCollection = true;
                    continue;
                }
                if (reader.IsEmptyElement)
                    continue;

                switch (name)
                {
                    case "href":
                        href = reader.ReadElementContentAsString();
                        break;
                    case "getetag":
                        etag = reader.ReadElementContentAsString();
                        break;
                    case "getlastmodified":
                        lastModified = reader.ReadElementContentAsString();
                        break;
                    case "getcontentlength":
                        length = reader.ReadElementContentAsString();
                        break;
                    case "responsedescription":
                        respDesc = reader.ReadInnerXml();
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "response")
            {
                inResponse = false;
                if (href is not null && TryBuildItem(href, isCollection, etag, lastModified, length, out var item))
                    items.Add(item);
                if (respDesc is not null)
                    nextDesc ??= respDesc;
            }
        }

        return new Page(items, nextDesc);
    }

    private static bool TryBuildItem(string href, bool isCollection, string? etag, string? lastModified, string? length, out DavItem item)
    {
        item = null!;
        var decoded = Uri.UnescapeDataString(href);
        var trimmed = decoded.TrimEnd('/');
        var lastSeg = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        if (lastSeg.Length == 0)
            return false;

        DateTimeOffset? mtime = null;
        if (lastModified is not null && DateTimeOffset.TryParse(lastModified, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t))
            mtime = t;
        long? len = null;
        if (length is not null && long.TryParse(length, out var l))
            len = l;

        item = new DavItem(lastSeg, isCollection, etag, mtime, len);
        return true;
    }
}
