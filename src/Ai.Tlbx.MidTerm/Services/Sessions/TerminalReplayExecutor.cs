using System.Text;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

internal static class TerminalReplayExecutor
{
    internal const int ImageSettleDelayMs = 200;
    internal const int TextBeforeImageSettleDelayMs = 75;
    internal const int SubmitDelayMs = 200;
    internal const int MaxLargePasteSubmitDelayMs = 1200;

    public static async Task ExecuteAsync(
        IReadOnlyList<AppServerControlTerminalReplayStep> steps,
        Func<byte[], CancellationToken, Task> sendInputAsync,
        Func<string, string?, CancellationToken, Task<bool>> pasteImageAsync,
        Func<int, CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(sendInputAsync);
        ArgumentNullException.ThrowIfNull(pasteImageAsync);
        ArgumentNullException.ThrowIfNull(delayAsync);

        var pendingInput = new PendingReplayInput();
        var sentAnyContent = false;
        var lastFlushByteCount = 0;
        foreach (var step in steps)
        {
            if (step is null)
            {
                continue;
            }

            switch (step.Kind)
            {
                case "text":
                    if (string.IsNullOrEmpty(step.Text))
                    {
                        continue;
                    }

                    pendingInput.Append(EncodeText(step.Text, step.UseBracketedPaste));
                    sentAnyContent = true;
                    break;
                case "filePath":
                    if (string.IsNullOrWhiteSpace(step.Path))
                    {
                        continue;
                    }

                    pendingInput.Append(Encoding.UTF8.GetBytes(QuoteFilePath(step.Path)));
                    sentAnyContent = true;
                    break;
                case "textFile":
                    if (string.IsNullOrWhiteSpace(step.Path))
                    {
                        continue;
                    }

                    if (File.Exists(step.Path))
                    {
                        var content = await File.ReadAllTextAsync(step.Path, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(content))
                        {
                            pendingInput.Append(EncodeText(content, step.UseBracketedPaste));
                            sentAnyContent = true;
                        }
                    }
                    else
                    {
                        pendingInput.Append(Encoding.UTF8.GetBytes(QuoteFilePath(step.Path)));
                        sentAnyContent = true;
                    }
                    break;
                case "image":
                    if (string.IsNullOrWhiteSpace(step.Path))
                    {
                        continue;
                    }

                    var flushedTextBeforeImageBytes = await pendingInput.FlushAsync(sendInputAsync, cancellationToken)
                        .ConfigureAwait(false);
                    if (flushedTextBeforeImageBytes > 0)
                    {
                        lastFlushByteCount = flushedTextBeforeImageBytes;
                        await delayAsync(TextBeforeImageSettleDelayMs, cancellationToken).ConfigureAwait(false);
                    }

                    if (await pasteImageAsync(step.Path, step.MimeType, cancellationToken).ConfigureAwait(false))
                    {
                        sentAnyContent = true;
                        await delayAsync(ImageSettleDelayMs, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        pendingInput.Append(Encoding.UTF8.GetBytes(QuoteFilePath(step.Path)));
                        sentAnyContent = true;
                    }
                    break;
            }
        }

        if (!sentAnyContent)
        {
            return;
        }

        var finalFlushByteCount = await pendingInput.FlushAsync(sendInputAsync, cancellationToken)
            .ConfigureAwait(false);
        if (finalFlushByteCount > 0)
        {
            lastFlushByteCount = finalFlushByteCount;
        }

        await delayAsync(ResolveSubmitDelayMs(lastFlushByteCount), cancellationToken).ConfigureAwait(false);
        await sendInputAsync([(byte)'\r'], cancellationToken).ConfigureAwait(false);
    }

    private static string QuoteFilePath(string path)
    {
        return "\"" + path + "\"";
    }

    private static byte[] EncodeText(string text, bool useBracketedPaste)
    {
        var normalizedText = NormalizeTerminalPasteLineEndings(text);
        if (!useBracketedPaste)
        {
            return Encoding.UTF8.GetBytes(normalizedText);
        }

        return Encoding.UTF8.GetBytes($"\u001b[200~{normalizedText}\u001b[201~");
    }

    private static int ResolveSubmitDelayMs(int lastFlushByteCount)
    {
        if (lastFlushByteCount <= 0)
        {
            return SubmitDelayMs;
        }

        if (lastFlushByteCount <= 1024)
        {
            return SubmitDelayMs;
        }

        var scaledDelay = SubmitDelayMs + ((lastFlushByteCount - 1024) / 8);
        return Math.Clamp(scaledDelay, SubmitDelayMs, MaxLargePasteSubmitDelayMs);
    }

    private static string NormalizeTerminalPasteLineEndings(string text)
    {
        return text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
    }

    private sealed class PendingReplayInput
    {
        private readonly List<byte> buffer = [];

        public void Append(byte[] bytes)
        {
            buffer.AddRange(bytes);
        }

        public async Task<int> FlushAsync(
            Func<byte[], CancellationToken, Task> sendInputAsync,
            CancellationToken cancellationToken)
        {
            if (buffer.Count == 0)
            {
                return 0;
            }

            var bytes = buffer.ToArray();
            buffer.Clear();
            await sendInputAsync(bytes, cancellationToken).ConfigureAwait(false);
            return bytes.Length;
        }
    }
}
