using System.Diagnostics;
using System.Text;
using MiniDrop.Domain;
using MiniDrop.WebDav;

namespace MiniDrop.M0;

/// <summary>
/// M0 实测工具（DESIGN §16）：在真实坚果云账号上验证服务事实。
/// 只做安全操作；需要应用专用密码，不落日志。
///
/// 用法：
///   dotnet run --project m0/MiniDrop.M0 -- --url https://dav.jianguoyun.com/dav/MiniDropM0Test/ --user &lt;账号&gt; --pass-env MINIDROP_PASS [--aggressive]
///
/// --aggressive 才会执行大文件上传等重操作；默认跳过。
/// 结果输出到 m0-results.md（gitignore 内），请人工核对后回填 docs/M0-RESULTS.md。
/// </summary>
internal static class Program
{
    private static readonly List<string> Findings = [];

    private static async Task<int> Main(string[] args)
    {
        string? url = null, user = null, pass = null;
        var aggressive = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--url": url = args[++i]; break;
                case "--user": user = args[++i]; break;
                case "--pass": Console.Error.WriteLine("警告：命令行传密码会留在 shell 历史，建议 --pass-env"); pass = args[++i]; break;
                case "--pass-env": pass = Environment.GetEnvironmentVariable(args[++i]); break;
                case "--aggressive": aggressive = true; break;
            }
        }
        if (url is null || user is null || pass is null)
        {
            Console.WriteLine("用法: --url <WebDAV根目录> --user <账号> --pass-env <环境变量名> [--aggressive]");
            return 2;
        }
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("拒绝：只允许 HTTPS");
            return 2;
        }

        var root = WebDavClient.NormalizeRootUrl(url);
        using var dav = new WebDavClient(new WebDavOptions
        {
            RootUrl = root,
            Account = user,
            PasswordProvider = () => pass,
            Timeout = TimeSpan.FromSeconds(60),
        });

        Console.WriteLine($"目标: {root}");
        Console.WriteLine($"开始时间: {DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}");
        Console.WriteLine(new string('=', 60));

        // 1. MKCOL 已存在 / 父目录不存在
        await CheckAsync("1.1 MKCOL 已存在目录", async () =>
        {
            var r = await dav.MkColAsync("items");
            return $"items → {r.HttpStatusCode} ({r.Status})";
        });
        await CheckAsync("1.2 MKCOL 父目录不存在", async () =>
        {
            var r = await dav.MkColAsync($"m0probe-{Guid.NewGuid():N}/child");
            return $"→ {r.HttpStatusCode} ({r.Status})";
        });

        // 2. 同名 PUT 覆盖 + 可见性
        var probeId = Ulid.New();
        var probeMonth = UlidClock.MonthOf(probeId);
        var probePath = RemotePaths.MessagePath(probeMonth, probeId);
        await CheckAsync("2.1 PUT 覆盖同名对象", async () =>
        {
            var r1 = await dav.PutAsync(probePath, "first"u8.ToArray(), "application/json");
            var r2 = await dav.PutAsync(probePath, "second"u8.ToArray(), "application/json");
            var (gr, content) = await dav.GetBytesAsync(probePath, Limits.MaxJsonBytes);
            return $"PUT1={r1.HttpStatusCode} PUT2={r2.HttpStatusCode} 回读={(content is null ? "?" : Encoding.UTF8.GetString(content))}";
        });

        // 3. PROPFIND 分页字段
        await CheckAsync("3.1 PROPFIND 响应字段与分页线索", async () =>
        {
            var (r, items) = await dav.PropfindDirAsync($"items/{probeMonth}");
            return $"状态={r.HttpStatusCode} 条目={items.Count}（空目录/少量目录无法观察分页；需在 >750 项目录上人工复核 PropfindPager 约定）";
        });

        // 4. PUT 后立即 PROPFIND 一致性
        await CheckAsync("4.1 PUT 后立即可见性", async () =>
        {
            var sw = Stopwatch.StartNew();
            var (r, items) = await dav.PropfindDirAsync($"items/{probeMonth}");
            sw.Stop();
            var visible = items.Any(i => i.Name == probeId + ".json");
            return $"立即 PROPFIND 可见={visible} 耗时={sw.ElapsedMilliseconds}ms";
        });

        // 6. DELETE 不存在对象
        await CheckAsync("6.1 DELETE 不存在对象", async () =>
        {
            var r = await dav.DeleteAsync(RemotePaths.MessagePath(probeMonth, Ulid.New()));
            return $"→ {r.HttpStatusCode} ({r.Status})";
        });

        // 8. 限流行为说明（无法主动触发，仅记录观察方式）
        Findings.Add("5. 429/503 Retry-After：无法安全主动触发；如遇限流请从日志抓取响应头回填。");
        Findings.Add("7. 账号单文件/流量上限：见坚果云帮助（默认 500MB/免费 600 次 30min）；如需实测用 --aggressive。");

        // 9. 取消上传是否留下部分对象（aggressive）
        if (aggressive)
        {
            await CheckAsync("9.1 PUT 中断后对象状态", async () =>
            {
                var big = new byte[8 * 1024 * 1024];
                var uuid = Guid.NewGuid().ToString("D");
                var cts = new CancellationTokenSource(200);
                DavResult r;
                try
                {
                    r = await dav.PutAsync(RemotePaths.FilePath(uuid), new MemoryStream(big), big.Length,
                        "application/octet-stream", null, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    r = new DavResult(DavStatus.NetworkError, null);
                }
                await Task.Delay(1000);
                var (gr, content) = await dav.GetBytesAsync(RemotePaths.FilePath(uuid), 1024);
                var exists = content is not null;
                await dav.DeleteAsync(RemotePaths.FilePath(uuid));
                return $"中断结果={r.Status} 1s后对象存在={exists}（存在则 M0 结论：会留下可覆盖的部分对象）";
            });
        }
        else
        {
            Findings.Add("9. 取消上传残留：未测（--aggressive 开启后执行 8MB 中断实验）。");
        }

        // 10. 尾斜线与编码
        await CheckAsync("10.1 尾斜线 PROPFIND", async () =>
        {
            var (r1, c1) = await dav.PropfindDirAsync($"items/{probeMonth}");
            var (r2, c2) = await dav.PropfindDirAsync($"items/{probeMonth}/");
            return $"无斜线={r1.HttpStatusCode}({c1.Count}) 带斜线={r2.HttpStatusCode}({c2.Count})";
        });
        await CheckAsync("10.2 大写 ULID 路径直达", async () =>
        {
            var (r, content) = await dav.GetBytesAsync(probePath, Limits.MaxJsonBytes);
            return $"GET {probePath.Split('/')[^1]} → {r.HttpStatusCode} 内容={(content is null ? "?" : Encoding.UTF8.GetString(content))}";
        });

        // 清理探针对象
        await dav.DeleteAsync(probePath);

        Console.WriteLine(new string('=', 60));
        Console.WriteLine("结论摘要（回填 docs/M0-RESULTS.md 用）：");
        foreach (var f in Findings)
            Console.WriteLine("  " + f);
        Console.WriteLine("详细输出已在上方；请人工确认后写入 docs/M0-RESULTS.md。");
        return 0;
    }

    private static async Task CheckAsync(string title, Func<Task<string>> action)
    {
        Console.Write($"{title} ... ");
        try
        {
            var detail = await action();
            Console.WriteLine(detail);
            Findings.Add($"{title}: {detail}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"异常 {e.GetType().Name}");
            Findings.Add($"{title}: 异常 {e.GetType().Name}");
        }
    }
}
