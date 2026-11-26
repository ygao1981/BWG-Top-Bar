using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BwgTopBar;

internal sealed record SshResources(
    decimal MemTotalBytes, decimal MemAvailableBytes,
    decimal DiskTotalBytes, decimal DiskAvailableBytes,
    double? CpuUsage);

internal static class SshReader
{
    public static async Task<SshResources> ReadAsync(ApplicationSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 用本机 OpenSSH 已配置的密钥认证；只读访问，且不跳过主机指纹校验。
        if (!Regex.IsMatch(settings.SshUser, "^[A-Za-z_][A-Za-z0-9_-]*$"))
            throw new InvalidOperationException("SSH 用户名格式不正确");
        if (!Regex.IsMatch(settings.SshHost, "^[A-Za-z0-9.:_-]+$"))
            throw new InvalidOperationException("SSH 主机名格式不正确");
        if (settings.SshPort < 1 || settings.SshPort > 65535)
            throw new InvalidOperationException("SSH 端口必须介于 1 到 65535 之间");

        using var process = new Process();
        process.StartInfo.FileName = "ssh";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        void Arg(string val) => process.StartInfo.ArgumentList.Add(val);
        Arg("-o"); Arg("BatchMode=yes");
        Arg("-o"); Arg("StrictHostKeyChecking=yes");
        Arg("-o"); Arg("ConnectTimeout=6");
        Arg("-p"); Arg(settings.SshPort.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(settings.SshKeyPath))
        {
            Arg("-i"); Arg(Environment.ExpandEnvironmentVariables(settings.SshKeyPath));
        }
        Arg($"{settings.SshUser}@{settings.SshHost}");
        // 2 次 /proc/stat 间隔一秒测算 CPU 百分比，free/df 给出 VPS 自身实际可用资源。
        Arg("export LC_ALL=C; head -n 1 /proc/stat; sleep 1; head -n 1 /proc/stat; free -b; df -P -B1 /");

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("未检测到 Windows OpenSSH 客户端，请先安装该系统组件");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(14));
        var outTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await Task.WhenAll(process.WaitForExitAsync(timeout.Token), outTask, errTask); }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("SSH 查询超时；请检查网络和密钥认证");
        }
        var output = await outTask;
        var error = await errTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(error.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)
                ? "SSH 主机指纹未获信任，请先在终端手动连接一次"
                : "SSH 未能读取资源；请检查主机、端口和免密登录");

        return Parse(output);
    }

    private static async Task StopProcessAsync(Process process)
    {
        // 清理不使用调用方已取消的令牌，且不因清理异常掩盖取消/超时原因。
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cleanup.Token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            System.ComponentModel.Win32Exception or OperationCanceledException) { }
    }

    private static SshResources Parse(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cpuLines = lines.Where(s => s.StartsWith("cpu ", StringComparison.Ordinal)).Take(2).ToArray();
        if (cpuLines.Length != 2) throw new InvalidOperationException("SSH CPU 读取格式异常");
        var first = Cpu(cpuLines[0]);
        var second = Cpu(cpuLines[1]);
        double? usage = null;
        if (first.Length == second.Length &&
            Enumerable.Range(0, first.Length).All(i => second[i] >= first[i]))
        {
            decimal deltaTotal = 0;
            for (var i = 0; i < first.Length; i++) deltaTotal += second[i] - first[i];
            var deltaIdle = (decimal)(second[3] - first[3]) + (second[4] - first[4]);
            if (deltaTotal > 0 && deltaIdle <= deltaTotal)
                usage = (double)(100m * (1m - deltaIdle / deltaTotal));
        }

        var memIndex = Array.FindIndex(lines, s => s.StartsWith("Mem:", StringComparison.Ordinal));
        if (memIndex < 1) throw new InvalidOperationException("SSH 内存读取格式异常：缺少内存数据或列名");
        var memHeader = Fields(lines[memIndex - 1]);
        var totalIndex = Array.IndexOf(memHeader, "total") + 1;
        var availableIndex = Array.IndexOf(memHeader, "available") + 1;
        var memFields = Fields(lines[memIndex]);
        if (availableIndex == 0)
            throw new InvalidOperationException("无法读取可用内存：VPS 的 free 命令未提供 available 字段");
        if (totalIndex == 0 || totalIndex >= memFields.Length || availableIndex >= memFields.Length)
            throw new InvalidOperationException("SSH 内存数据不完整，请检查 VPS 的 free 命令输出");
        var memTotal = ReadBytes(memFields[totalIndex], "内存总量");
        var memAvail = ReadBytes(memFields[availableIndex], "可用内存");
        if (memTotal <= 0 || memAvail > memTotal)
            throw new InvalidOperationException("SSH 内存数据异常：可用内存与总量不一致");

        var headerIdx = Array.FindIndex(lines, s => s.StartsWith("Filesystem", StringComparison.Ordinal));
        if (headerIdx < 0 || headerIdx + 1 >= lines.Length) throw new InvalidOperationException("SSH 磁盘读取格式异常");
        var disk = Fields(lines[headerIdx + 1]);
        if (disk.Length < 4) throw new InvalidOperationException("SSH 磁盘字段不完整");
        var diskTotal = ReadBytes(disk[1], "磁盘总量");
        var diskAvailable = ReadBytes(disk[3], "剩余磁盘");
        if (diskTotal <= 0 || diskAvailable > diskTotal)
            throw new InvalidOperationException("SSH 磁盘数据异常：剩余容量与总量不一致");
        return new(memTotal, memAvail, diskTotal, diskAvailable, usage);
    }

    private static string[] Fields(string line) =>
        line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static decimal ReadBytes(string value, string label)
    {
        if (!decimal.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) || bytes < 0)
            throw new InvalidOperationException($"SSH {label}数据格式异常，请检查 VPS 的资源查询命令");
        return bytes;
    }

    private static ulong[] Cpu(string line)
    {
        // guest 和 guest_nice 已分别包含在 user/nice 中，只统计前八项。
        var p = Fields(line).Skip(1).Take(8).ToArray();
        if (p.Length < 5) throw new InvalidOperationException("CPU 字段不完整");
        var counters = new ulong[p.Length];
        for (var i = 0; i < p.Length; i++)
            if (!ulong.TryParse(p[i], NumberStyles.None, CultureInfo.InvariantCulture, out counters[i]))
                throw new InvalidOperationException("SSH CPU 数据格式异常，请检查 VPS 的运行状态");
        return counters;
    }
}
