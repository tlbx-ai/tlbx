using Ai.Tlbx.MidTerm.Common.Logging;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class LogWriterTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "MidTermTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Logger_CreatesNewSegment_WhenCurrentSegmentExceedsSizeLimit()
    {
        Directory.CreateDirectory(_tempDirectory);

        using (var logger = CreateLogger())
        {
            logger.Info(() => new string('A', 60));
        }

        using (var logger = CreateLogger())
        {
            logger.Info(() => new string('B', 60));
        }

        var logFiles = Directory.GetFiles(_tempDirectory, "mt-*.log")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(2, logFiles.Length);
        Assert.EndsWith(".000.log", logFiles[0], StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".001.log", logFiles[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AAAA", File.ReadAllText(logFiles[0]), StringComparison.Ordinal);
        Assert.Contains("BBBB", File.ReadAllText(logFiles[1]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_CompletesPromptlyAndFlushesQueuedEntries(bool writeEntry)
    {
        Directory.CreateDirectory(_tempDirectory);
        await Task.Run(() =>
        {
            using var logger = CreateLogger();
            if (writeEntry) logger.Info(() => "shutdown marker");
        }).WaitAsync(TimeSpan.FromSeconds(1));

        if (writeEntry)
        {
            var file = Assert.Single(Directory.GetFiles(_tempDirectory, "mt-*.log"));
            Assert.Contains("shutdown marker", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    private Logger CreateLogger()
    {
        var logger = new Logger("mt", _tempDirectory, new LogRotationPolicy
        {
            MaxFileSizeBytes = 80,
            MaxFileCount = 10,
            MaxDirectorySizeBytes = 1024 * 1024
        });
        logger.MinLevel = LogSeverity.Info;
        return logger;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
