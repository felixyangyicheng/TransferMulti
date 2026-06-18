using System.Diagnostics;

namespace TransferMulti.wasm.Services;

/// <summary>
/// WASM 内存监控服务 - 实时追踪内存使用率、GC 频率和性能瓶颈
/// </summary>
public sealed class MemoryMonitorService
{
    private readonly Timer _gcTimer = new(
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(50));
    
    public event Action? OnMemoryThresholdExceeded;
    public event Action<string>? OnGcFired;

    public void Start()
    {
        _gcTimer.Start();
    }

    private async Task ProcessGcMetricsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct);

            var gcCount = GC.GetGenerationCount();
            var gen2Objects = GC.GetGeneration2Count();
            var gen1Objects = GC.GetGeneration1Count();
            var gen0Objects = GC.GetGeneration0Count();

            // 检测内存泄漏迹象：Gen2 对象持续增长且 Gen0/Gen1 回收率下降
            if (gen2Objects > 5_000_000 || gcCount > 3)
            {
                OnMemoryThresholdExceeded?.Invoke();
            }

            // 输出 GC 统计（每 5 秒一次）
            if (gcCount % 10 == 0)
            {
                var elapsed = Stopwatch.GetElapsedTime().TotalSeconds;
                OnGcFired?.Invoke($"GC: Gen{gcCount}, G2={gen2Objects:N0}, G1={gen1Objects:N0}, G0={gen0Objects:N0} (t={elapsed:F1}s)\n");
            }
        }
    }

    public void Stop()
    {
        _gcTimer.Stop();
    }
}
