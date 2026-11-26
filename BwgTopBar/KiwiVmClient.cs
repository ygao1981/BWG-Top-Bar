using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace BwgTopBar;

internal static class KiwiVmClient
{
    // Never forward a credential-bearing request to a redirect destination.
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = new Uri("https://api.64clouds.com/v1/"),
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    public static async Task<JsonElement> GetAsync(string endpoint, string veid, string apiKey, CancellationToken token)
    {
        if (endpoint is not ("getServiceInfo" or "getLiveServiceInfo"))
            throw new InvalidOperationException("不允许调用修改 VPS 的接口。");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["veid"] = veid,
                    ["api_key"] = apiKey,
                }),
            };
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new InvalidOperationException("查询过于频繁，请稍后再试或延长刷新间隔。");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("无法访问搬瓦工接口，请检查网络，稍后重试。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ReadNumber(root, "error") is not decimal error)
                throw new InvalidOperationException("服务端返回的数据不完整，请稍后重试。");
            if (error != 0)
                throw new InvalidOperationException("搬瓦工未允许本次查询，请检查 VEID、API Key 及 API 访问权限。");
            return root.Clone();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException("查询超时，请检查网络，稍后重试。");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("连接失败，请检查网络及系统代理设置。");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("服务端返回的数据无法读取，请稍后重试。");
        }
    }

    public static decimal? ReadNumber(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var number)) return number;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    public static string? ReadString(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
    }
}

internal sealed class MonitorSnapshot
{
    public decimal? TotalTrafficBytes;
    public decimal? UsedTrafficBytes;
    public DateTimeOffset? ResetAt;
    public decimal? PlanRamBytes;
    public decimal? PlanDiskBytes;
    public decimal? AvailableRamBytes;
    public decimal? DiskTotalBytes;
    public decimal? DiskAvailableBytes;
    public double? CpuPercent;
    public string? LoadAverage;
    public string? PowerStatus;
    public DateTimeOffset? LastSuccess;
    public DateTimeOffset? LastResourceSuccess;
    public string? Warning;

    public void ApplyBasic(JsonElement root)
    {
        decimal? multiplier = root.TryGetProperty("monthly_data_multiplier", out _)
            ? KiwiVmClient.ReadNumber(root, "monthly_data_multiplier") : 1m;
        var total = KiwiVmClient.ReadNumber(root, "plan_monthly_data");
        var used = KiwiVmClient.ReadNumber(root, "data_counter");
        if (multiplier is not > 0 || total is not >= 0 || used is not >= 0)
            throw new InvalidOperationException("流量数据不完整或异常，请稍后重试并核对 KiwiVM 控制台。");

        decimal scaledTotal;
        decimal scaledUsed;
        try
        {
            // Preserve the original field semantics. Validate once against the user's panel.
            scaledTotal = checked(total.Value * multiplier.Value);
            scaledUsed = checked(used.Value * multiplier.Value);
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException("流量数值超出有效范围，请核对 KiwiVM 控制台。");
        }
        TotalTrafficBytes = scaledTotal;
        UsedTrafficBytes = scaledUsed;
        PlanRamBytes = Positive(KiwiVmClient.ReadNumber(root, "plan_ram"));
        PlanDiskBytes = Positive(KiwiVmClient.ReadNumber(root, "plan_disk"));
        ResetAt = null;
        var reset = KiwiVmClient.ReadNumber(root, "data_next_reset");
        if (reset is > 0 and <= 253402300799m)
            ResetAt = DateTimeOffset.FromUnixTimeSeconds((long)reset.Value);
        if (KiwiVmClient.ReadNumber(root, "suspended") == 1) PowerStatus = "已暂停";
        else if (PowerStatus == "已暂停") PowerStatus = null;
        LastSuccess = DateTimeOffset.Now;
    }

    public void ApplyLive(JsonElement root)
    {
        // A resource query must not replace the newer traffic query or its update time.
        ClearResources();
        var state = KiwiVmClient.ReadString(root, "ve_status")?.Trim().ToLowerInvariant();
        PowerStatus = state switch
        {
            "running" => "运行中",
            "stopped" or "down" or "not running" => "已停止",
            "suspended" => "已暂停",
            _ => null,
        };
        if (PowerStatus == "运行中")
        {
            if (KiwiVmClient.ReadNumber(root, "mem_available_kb") is decimal kb && kb >= 0 && kb <= decimal.MaxValue / 1024m)
                AvailableRamBytes = kb * 1024m;
            var load = KiwiVmClient.ReadString(root, "load_average");
            var first = load?.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (decimal.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0)
                LoadAverage = value.ToString("0.##", CultureInfo.InvariantCulture);
        }
        LastResourceSuccess = DateTimeOffset.Now;
    }

    public void ApplySsh(SshResources resources)
    {
        ClearResources();
        AvailableRamBytes = resources.MemAvailableBytes;
        // Keep the API plan quota when available; Linux MemTotal is a fallback only.
        PlanRamBytes ??= resources.MemTotalBytes;
        DiskAvailableBytes = resources.DiskAvailableBytes;
        DiskTotalBytes = resources.DiskTotalBytes;
        CpuPercent = resources.CpuUsage;
        LastResourceSuccess = DateTimeOffset.Now;
    }

    public void ClearResources()
    {
        AvailableRamBytes = null;
        DiskTotalBytes = null;
        DiskAvailableBytes = null;
        CpuPercent = null;
        LoadAverage = null;
        PowerStatus = null;
        LastResourceSuccess = null;
    }

    public double? UsedPercent => TotalTrafficBytes is > 0 && UsedTrafficBytes is decimal u
        ? (double)u / (double)TotalTrafficBytes.Value * 100d : null;

    private static decimal? Positive(decimal? value) => value is > 0 ? value : null;
    private static string Gib(decimal bytes) => (bytes / 1073741824m).ToString("0.#", CultureInfo.InvariantCulture);

    public string ToLine(bool compact = false)
    {
        var segments = new List<string> { "BWG" };
        if (TotalTrafficBytes is > 0 && UsedTrafficBytes is decimal used)
        {
            var left = Math.Max(0, TotalTrafficBytes.Value - used);
            segments.Add($"剩余流量 {Gib(left)}/{Gib(TotalTrafficBytes.Value)} GiB");
        }
        else segments.Add("剩余流量未知");

        if (!compact)
        {
            if (CpuPercent.HasValue) segments.Add($"CPU {CpuPercent.Value:0}%");
            else if (!string.IsNullOrWhiteSpace(LoadAverage)) segments.Add($"负载 {LoadAverage}");

            if (AvailableRamBytes.HasValue && PlanRamBytes is > 0)
                segments.Add($"可用内存 {Gib(AvailableRamBytes.Value)}/{Gib(PlanRamBytes.Value)} GiB");
            else if (PlanRamBytes is > 0) segments.Add($"内存配额 {Gib(PlanRamBytes.Value)} GiB");

            if (DiskAvailableBytes.HasValue && DiskTotalBytes is > 0)
                segments.Add($"根分区可用 {Gib(DiskAvailableBytes.Value)}/{Gib(DiskTotalBytes.Value)} GiB");
            else if (PlanDiskBytes is > 0) segments.Add($"磁盘配额 {Gib(PlanDiskBytes.Value)} GiB");
        }

        if (ResetAt is DateTimeOffset resetTime)
        {
            var days = (int)Math.Ceiling((resetTime - DateTimeOffset.Now).TotalDays);
            segments.Add(days > 0 ? $"{days}天后重置" : "重置时间已到，待确认");
        }
        else segments.Add("重置时间未知");
        if (!string.IsNullOrWhiteSpace(PowerStatus)) segments.Add(PowerStatus);
        return string.Join("  |  ", segments);
    }
}
