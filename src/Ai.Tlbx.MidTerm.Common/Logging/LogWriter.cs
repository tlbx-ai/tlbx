using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace Ai.Tlbx.MidTerm.Common.Logging;

internal sealed class LogWriter : IDisposable
{
    private const int MaxQueuedEntries = 10_000;
    private const int BufferSizeBytes = 65536;
    private const int FlushIntervalMs = 100;
    private const int PeriodicCleanupIntervalMs = 60 * 60 * 1000; // 1 hour
    private const int SegmentDigits = 3;

    private readonly Channel<LogEntry> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;
    private readonly Timer _cleanupTimer;
    private readonly string _logDirectory;
    private readonly string _filePrefix;
    private readonly LogRotationPolicy _policy;

    private FileStream? _currentFile;
    private StreamWriter? _currentWriter;
    private string? _currentFilePath;
    private long _currentFileSize;
    private DateOnly _currentDate;
    private int _currentSegment;

    public LogWriter(string filePrefix, string logDirectory, LogRotationPolicy policy)
    {
        _filePrefix = filePrefix;
        _logDirectory = logDirectory;
        _policy = policy;

        _queue = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(MaxQueuedEntries)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _writerTask = ProcessQueueAsync(_cts.Token);

        // Startup cleanup on low-priority background thread (don't block launch)
        ThreadPool.QueueUserWorkItem(_ => EnforceDirectorySizeLimit(), null);

        // Periodic cleanup for long-running processes
        _cleanupTimer = new Timer(_ => EnforceDirectorySizeLimit(), null,
            PeriodicCleanupIntervalMs, PeriodicCleanupIntervalMs);
    }

    public void Write(LogSeverity level, string source, string message, bool immediateFlush)
    {
        WriteRaw(DateTime.Now, level, source, message, immediateFlush);
    }

    public void WriteRaw(DateTime timestamp, LogSeverity level, string source, string message, bool immediateFlush = false)
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        var entry = new LogEntry
        {
            Timestamp = timestamp,
            Level = level,
            Source = source,
            Message = message,
            ImmediateFlush = immediateFlush
        };

        _queue.Writer.TryWrite(entry);
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        var buffer = new StringBuilder(BufferSizeBytes);
        var lastFlush = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var needsImmediateFlush = false;

                // Use TryRead with Task.Delay instead of linked CTS to avoid allocations
                var hasData = _queue.Reader.TryRead(out var firstEntry);
                if (!hasData)
                {
                    // Wait for data or timeout, whichever comes first
                    var waitTask = _queue.Reader.WaitToReadAsync(ct).AsTask();
                    var delayTask = Task.Delay(FlushIntervalMs, ct);

                    var completedTask = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);

                    if (completedTask == waitTask)
                    {
                        if (!await waitTask.ConfigureAwait(false))
                        {
                            // Completion means no more entries can arrive. Exit through
                            // finally to flush the remaining buffer before Dispose returns.
                            break;
                        }
                        hasData = _queue.Reader.TryRead(out firstEntry);
                    }
                }

                if (hasData)
                {
                    FormatEntry(buffer, firstEntry);
                    needsImmediateFlush |= firstEntry.ImmediateFlush;

                    // Drain all available entries
                    while (_queue.Reader.TryRead(out var entry))
                    {
                        FormatEntry(buffer, entry);
                        needsImmediateFlush |= entry.ImmediateFlush;

                        if (buffer.Length >= BufferSizeBytes)
                        {
                            await FlushBufferAsync(buffer).ConfigureAwait(false);
                            lastFlush = DateTime.UtcNow;
                        }
                    }
                }

                if (needsImmediateFlush ||
                    (buffer.Length > 0 && (DateTime.UtcNow - lastFlush).TotalMilliseconds >= FlushIntervalMs))
                {
                    await FlushBufferAsync(buffer).ConfigureAwait(false);
                    lastFlush = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            while (_queue.Reader.TryRead(out var entry))
            {
                FormatEntry(buffer, entry);
            }

            if (buffer.Length > 0)
            {
                try
                {
                    await FlushBufferAsync(buffer).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            CloseCurrentFile();
        }
    }

    private static void FormatEntry(StringBuilder buffer, LogEntry entry)
    {
        buffer.Append('[');
        buffer.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        buffer.Append("] [");
        buffer.Append(GetLevelString(entry.Level));
        buffer.Append("] [");
        buffer.Append(entry.Source);
        buffer.Append("] ");
        buffer.AppendLine(entry.Message);
    }

    private static string GetLevelString(LogSeverity level) => level switch
    {
        LogSeverity.Exception => "EXCEPTION",
        LogSeverity.Error => "ERROR",
        LogSeverity.Warn => "WARN",
        LogSeverity.Info => "INFO",
        LogSeverity.Verbose => "VERBOSE",
        _ => "UNKNOWN"
    };

    private async Task FlushBufferAsync(StringBuilder buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        try
        {
            var text = buffer.ToString();
            var textSize = Encoding.UTF8.GetByteCount(text);
            EnsureFileOpen();
            RotateIfNeeded(textSize);
            buffer.Clear();

            await _currentWriter!.WriteAsync(text).ConfigureAwait(false);
            await _currentWriter.FlushAsync(_cts.Token).ConfigureAwait(false);
            _currentFileSize += textSize;
        }
        catch
        {
        }
    }

    private void EnsureFileOpen()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        if (_currentFile is not null && _currentDate == today)
        {
            return;
        }

        if (_currentFile is not null)
        {
            CloseCurrentFile();
        }

        _currentDate = today;
        Directory.CreateDirectory(_logDirectory);
        _currentSegment = GetCurrentSegment(today);
        _currentFilePath = BuildLogFilePath(today, _currentSegment);
        OpenCurrentFile(_currentFilePath);
    }

    private void RotateIfNeeded(int pendingWriteBytes)
    {
        if (_currentFileSize == 0)
        {
            return;
        }

        if (_currentFileSize + pendingWriteBytes <= _policy.MaxFileSizeBytes)
        {
            return;
        }

        CloseCurrentFile();
        _currentSegment++;
        _currentFilePath = BuildLogFilePath(_currentDate, _currentSegment);
        OpenCurrentFile(_currentFilePath);
        EnforceDirectorySizeLimit();
    }

    private void OpenCurrentFile(string filePath)
    {
        var file = new FileStream(
            filePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);
        var writer = new StreamWriter(file, Encoding.UTF8, leaveOpen: true);
        var previousWriter = _currentWriter;
        var previousFile = _currentFile;

        _currentWriter = writer;
        _currentFile = file;
        _currentFileSize = file.Length;

        previousWriter?.Dispose();
        previousFile?.Dispose();
    }

    private int GetCurrentSegment(DateOnly today)
    {
        var pattern = string.Create(CultureInfo.InvariantCulture, $"{_filePrefix}-{today:yyyy-MM-dd}*.log");
        var files = Directory.EnumerateFiles(_logDirectory, pattern)
            .Select(path => (Path: path, Segment: TryParseSegment(Path.GetFileName(path))))
            .Where(item => item.Segment >= 0)
            .OrderByDescending(item => item.Segment)
            .ToList();

        if (files.Count == 0)
        {
            return 0;
        }

        var latest = files[0];
        var latestSize = new FileInfo(latest.Path).Length;
        return latestSize >= _policy.MaxFileSizeBytes ? latest.Segment + 1 : latest.Segment;
    }

    private string BuildLogFilePath(DateOnly today, int segment)
    {
        return Path.Combine(
            _logDirectory,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{_filePrefix}-{today:yyyy-MM-dd}.{segment.ToString($"D{SegmentDigits}", CultureInfo.InvariantCulture)}.log"));
    }

    private static int TryParseSegment(string fileName)
    {
        var dotIndex = fileName.LastIndexOf('.', fileName.Length - 5);
        if (dotIndex < 0)
        {
            return -1;
        }

        var segmentText = fileName[(dotIndex + 1)..^4];
        return int.TryParse(segmentText, CultureInfo.InvariantCulture, out var segment) ? segment : -1;
    }

    private void EnforceDirectorySizeLimit()
    {
        try
        {
        var files = Directory.GetFiles(_logDirectory, $"{_filePrefix}-*.log")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();

            var totalSize = files.Sum(f => f.Length);

            for (var i = files.Count - 1; i >= 1; i--)
            {
                if (files.Count <= _policy.MaxFileCount && totalSize <= _policy.MaxDirectorySizeBytes)
                {
                    break;
                }

                var file = files[i];
                totalSize -= file.Length;

                try
                {
                    file.Delete();
                }
                catch
                {
                }

                files.RemoveAt(i);
            }
        }
        catch
        {
        }
    }

    private void CloseCurrentFile()
    {
        _currentWriter?.Dispose();
        _currentFile?.Dispose();
        _currentWriter = null;
        _currentFile = null;
        _currentFilePath = null;
        _currentFileSize = 0;
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        _queue.Writer.Complete();

        try
        {
            using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            shutdownCts.CancelAfter(TimeSpan.FromSeconds(2));
            _writerTask.WaitAsync(shutdownCts.Token).GetAwaiter().GetResult();
        }
        catch
        {
            _cts.Cancel();
        }

        CloseCurrentFile();
        _cts.Dispose();
    }

    private readonly struct LogEntry
    {
        public DateTime Timestamp { get; init; }
        public LogSeverity Level { get; init; }
        public string Source { get; init; }
        public string Message { get; init; }
        public bool ImmediateFlush { get; init; }
    }
}
