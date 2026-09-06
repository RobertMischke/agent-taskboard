using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class QuotaServiceGracefulDegradationTests : IDisposable
{
    private readonly string _repoDir = Path.Combine(
        Path.GetTempPath(),
        "agent-studio-quota-degrade-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _configuration;

    public QuotaServiceGracefulDegradationTests()
    {
        Directory.CreateDirectory(_repoDir);
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _repoDir,
                ["Quota:TtlSeconds"] = "1"
            })
            .Build();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task RefreshAllAsync_CodexAndClaudeSuccess_PersistsAndServesFreshMetadata()
    {
        var capturedAt = DateTime.UtcNow;
        var service = NewService(
            new ScriptedProbe(_ => new QuotaSnapshot
            {
                CliType = "codex",
                CliVersion = "codex-cli 0.149.0",
                FetchedAt = capturedAt,
                Plan = "Pro",
                Windows =
                [
                    new QuotaWindow { Label = "5-hour", UsedPct = 42 },
                    new QuotaWindow { Label = "Weekly", UsedPct = 61 }
                ]
            }),
            new ScriptedProbe(_ => new QuotaSnapshot
            {
                CliType = "claude",
                CliVersion = "2.1.202 (Claude Code)",
                FetchedAt = capturedAt,
                Plan = "Max",
                Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 37 }]
            }, "claude"));

        await service.RefreshAllAsync();

        var persisted = new QuotaCacheStore(
            _configuration,
            NullLogger<QuotaCacheStore>.Instance).Read();
        Assert.Equal(2, persisted.Count);
        Assert.Equal("codex-cli 0.149.0", persisted.Single(s => s.CliType == "codex").CliVersion);
        Assert.Equal("2.1.202 (Claude Code)", persisted.Single(s => s.CliType == "claude").CliVersion);
        Assert.All(persisted, snapshot => Assert.Equal(capturedAt, snapshot.CapturedAt));

        var served = service.GetCached().Snapshots;
        Assert.Equal(2, served.Count);
        Assert.All(served, snapshot =>
        {
            Assert.Equal(capturedAt, snapshot.CapturedAt);
            Assert.InRange(snapshot.AgeSeconds, 0, 1);
            Assert.False(snapshot.Stale);
        });
    }

    [Fact]
    public async Task RefreshAsync_FailedProbe_RetainsLastGoodValuesAndAddsFailureMetadata()
    {
        var fetchedAt = DateTime.UtcNow.AddMinutes(-2);
        var probe = new ScriptedProbe(call => call == 1
            ? new QuotaSnapshot
            {
                CliType = "codex",
                CliVersion = "codex-cli 0.149.0",
                FetchedAt = fetchedAt,
                Plan = "Pro",
                Source = "/status",
                Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 61 }]
            }
            : new QuotaSnapshot
            {
                CliType = "codex",
                CliVersion = "codex-cli 0.149.0",
                Error = "A task was canceled."
            });
        var service = NewService(probe);

        await service.RefreshAsync("codex");
        var stale = await service.RefreshAsync("codex");

        Assert.NotNull(stale);
        Assert.Equal(fetchedAt, stale.FetchedAt);
        Assert.Equal("Pro", stale.Plan);
        Assert.Equal(61, Assert.Single(stale.Windows).UsedPct);
        Assert.Equal("codex-cli 0.149.0", stale.CliVersion);
        Assert.NotNull(stale.ProbeFailedAt);
        Assert.Equal("Quota probe timed out before the CLI panel rendered.", stale.Error);

        var served = Assert.Single(service.GetCached().Snapshots);
        Assert.Equal(fetchedAt, served.CapturedAt);
        Assert.True(served.Stale);
        Assert.True(served.AgeSeconds >= 120);
        Assert.Equal(61, Assert.Single(served.Windows).UsedPct);
    }

    [Fact]
    public async Task GetWithBackgroundRefresh_DoesNotWaitForSynchronousProbeStartup()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var service = NewService(new BlockingProbe(entered, release));

        var stopwatch = Stopwatch.StartNew();
        var request = Task.Run(() => service.GetWithBackgroundRefresh());
        try
        {
            var report = await request.WaitAsync(TimeSpan.FromSeconds(1));
            stopwatch.Stop();

            Assert.Single(report.Snapshots);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(1)), "Background probe never started.");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Cached GET took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void ColdStart_ServesPersistedLastGoodImmediatelyWithoutRunningProbe()
    {
        var capturedAt = DateTime.UtcNow.AddMinutes(-5);
        var store = new QuotaCacheStore(_configuration, NullLogger<QuotaCacheStore>.Instance);
        store.Write(
        [
            new QuotaSnapshot
            {
                CliType = "codex",
                CliVersion = "codex-cli 0.149.0",
                FetchedAt = capturedAt,
                CapturedAt = capturedAt,
                ProbeFailedAt = capturedAt.AddMinutes(1),
                Plan = "Pro",
                Windows = [new QuotaWindow { Label = "Weekly", UsedPct = 61 }],
                Error = "A task was canceled."
            }
        ]);
        var probe = new CountingProbe();
        var service = NewService(probe);

        var stopwatch = Stopwatch.StartNew();
        var report = service.GetCached();
        stopwatch.Stop();

        var served = Assert.Single(report.Snapshots);
        Assert.Equal(0, probe.Calls);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(capturedAt, served.CapturedAt);
        Assert.Equal(61, Assert.Single(served.Windows).UsedPct);
        Assert.True(served.Stale);
        Assert.True(served.AgeSeconds >= 300);
        Assert.Equal("Quota probe timed out before the CLI panel rendered.", served.Error);
    }

    [Fact]
    public void CliVersionTracker_LogsOneAttributableChange()
    {
        var logger = new RecordingLogger<CliVersionTracker>();
        var tracker = new CliVersionTracker(logger);
        tracker.Seed("codex", "codex-cli 0.144.1");

        tracker.Observe("codex", "codex-cli 0.149.0", "startup");
        tracker.Observe("codex", "codex-cli 0.149.0", "periodic");

        var change = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, change.Level);
        Assert.Contains("CLI version changed", change.Message, StringComparison.Ordinal);
        Assert.Contains("0.144.1", change.Message, StringComparison.Ordinal);
        Assert.Contains("0.149.0", change.Message, StringComparison.Ordinal);
        Assert.Contains("startup", change.Message, StringComparison.Ordinal);
    }

    private QuotaService NewService(params IQuotaProbe[] probes)
    {
        var store = new QuotaCacheStore(_configuration, NullLogger<QuotaCacheStore>.Instance);
        return new QuotaService(
            NullLogger<QuotaService>.Instance,
            probes,
            _configuration,
            store);
    }

    private sealed class ScriptedProbe(
        Func<int, QuotaSnapshot> script,
        string cliType = "codex") : IQuotaProbe
    {
        private int _calls;
        public string CliType => cliType;
        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
            => Task.FromResult(script(Interlocked.Increment(ref _calls)));
    }

    private sealed class BlockingProbe(
        ManualResetEventSlim entered,
        ManualResetEventSlim release) : IQuotaProbe
    {
        public string CliType => "codex";

        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
        {
            entered.Set();
            release.Wait(ct);
            return Task.FromResult(new QuotaSnapshot { CliType = CliType });
        }
    }

    private sealed class CountingProbe : IQuotaProbe
    {
        public int Calls { get; private set; }
        public string CliType => "codex";

        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new QuotaSnapshot { CliType = CliType });
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
