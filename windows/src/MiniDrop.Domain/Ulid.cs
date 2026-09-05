using System.Security.Cryptography;
using System.Text;

namespace MiniDrop.Domain;

/// <summary>
/// 26 字符大写 Crockford Base32 ULID（48bit 毫秒时间戳 + 80bit 随机数）。
/// 生成保证进程内单调不减：同一毫秒内随机部分按大端计数器 +1。
/// </summary>
public static class Ulid
{
    public const int Length = 26;
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int TimestampChars = 10;
    private const int RandomBytes = 10;

    private static readonly object Gate = new();
    private static long _lastMs = -1;
    private static readonly byte[] LastRandom = new byte[RandomBytes];

    public static string New() => New(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>指定时间戳生成（发送路径用，保证进程内单调）。</summary>
    public static string New(long timestampMs)
    {
        lock (Gate)
        {
            if (timestampMs < _lastMs)
                timestampMs = _lastMs;

            if (timestampMs == _lastMs)
            {
                IncrementRandom();
            }
            else
            {
                RandomNumberGenerator.Fill(LastRandom);
                _lastMs = timestampMs;
            }

            return EncodeTimestamp(timestampMs) + EncodeRandom(LastRandom);
        }
    }

    /// <summary>
    /// 按给定时间戳生成，不做单调钳制（测试播种/导入历史数据用）。
    /// 不影响 New() 的单调状态。
    /// </summary>
    public static string NewAt(long timestampMs)
    {
        var random = new byte[RandomBytes];
        RandomNumberGenerator.Fill(random);
        return EncodeTimestamp(timestampMs) + EncodeRandom(random);
    }

    /// <summary>严格校验：26 字符、大写、全部属于 Crockford 字母表。</summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != Length)
            return false;
        foreach (var c in value)
        {
            if (Alphabet.IndexOf(c) < 0)
                return false;
        }
        return true;
    }

    /// <summary>解码前 10 个字符（时间戳部分），忽略输入大小写。</summary>
    public static bool TryGetTimestampMs(string value, out long timestampMs)
    {
        timestampMs = 0;
        if (string.IsNullOrEmpty(value) || value.Length < TimestampChars)
            return false;

        long acc = 0;
        for (var i = 0; i < TimestampChars; i++)
        {
            var idx = Alphabet.IndexOf(char.ToUpperInvariant(value[i]));
            if (idx < 0)
                return false;
            acc = (acc << 5) | (uint)idx;
        }
        // 前 2 位必须为 0（48 bit 时间戳）
        if ((acc >> 48) != 0)
            return false;
        timestampMs = acc;
        return true;
    }

    public static string EncodeTimestamp(long timestampMs)
    {
        Span<char> chars = stackalloc char[TimestampChars];
        for (var i = TimestampChars - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(timestampMs & 0x1F)];
            timestampMs >>= 5;
        }
        return new string(chars);
    }

    private static string EncodeRandom(byte[] random)
    {
        var sb = new StringBuilder(16);
        // 80 bit = 16 × 5bit；大端消费，最后一字节不足 5bit 的部分在低位补齐
        long buffer = 0, bits = 0;
        foreach (var b in random)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Alphabet[(int)((buffer >> (int)bits) & 0x1F)]);
            }
        }
        // 80 = 16*5，余数应为 0，但保守处理剩余位
        if (bits > 0)
            sb.Append(Alphabet[(int)((buffer << (int)(5 - bits)) & 0x1F)]);
        return sb.ToString(0, 16);
    }

    private static void IncrementRandom()
    {
        for (var i = RandomBytes - 1; i >= 0; i--)
        {
            if (++LastRandom[i] != 0)
                return;
        }
        // 80bit 溢出：推进到下一毫秒（实践上不可达）
        _lastMs++;
        RandomNumberGenerator.Fill(LastRandom);
    }
}

/// <summary>ULID 时间戳派生规则：UTC 月份、90 天生命周期。</summary>
public static class UlidClock
{
    public const int RetentionDays = 90;

    /// <summary>month(id)：ULID 时间戳对应的 UTC YYYY-MM。</summary>
    public static string MonthOf(string ulid)
    {
        if (!Ulid.TryGetTimestampMs(ulid, out var ms))
            throw new ArgumentException("非法 ULID：" + ulid, nameof(ulid));
        return MonthFromMs(ms);
    }

    public static string MonthFromMs(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM");

    public static string CurrentUtcMonth(DateTimeOffset now) => now.UtcDateTime.ToString("yyyy-MM");

    public static string PreviousMonth(string month)
    {
        var parts = month.Split('-');
        var y = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        var m = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        m--;
        if (m == 0) { m = 12; y--; }
        return $"{y:D4}-{m:D2}";
    }

    public static int CompareMonths(string a, string b) => string.CompareOrdinal(a, b);

    /// <summary>90 天生命周期：仅按 ULID 时间戳判断。</summary>
    public static bool IsExpired(string ulid, DateTimeOffset now)
    {
        if (!Ulid.TryGetTimestampMs(ulid, out var ms))
            return false;
        return now.ToUnixTimeMilliseconds() - ms >= RetentionDays * 24L * 3600 * 1000;
    }

    /// <summary>90 天边界所在的月份（该月及更早的 ULID 已到期）。</summary>
    public static string ExpiryMonth(DateTimeOffset now)
        => MonthFromMs(now.ToUnixTimeMilliseconds() - RetentionDays * 24L * 3600 * 1000);
}
