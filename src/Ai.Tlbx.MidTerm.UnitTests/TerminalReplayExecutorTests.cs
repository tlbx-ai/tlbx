using System.Text;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TerminalReplayExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_ReplaysTextAndImagesInOrderBeforeSubmitting()
    {
        var sentInputs = new List<string>();
        var imagePastes = new List<(string Path, string? MimeType)>();
        var delays = new List<int>();

        await TerminalReplayExecutor.ExecuteAsync(
            [
                new AppServerControlTerminalReplayStep
                {
                    Kind = "text",
                    Text = "Test "
                },
                new AppServerControlTerminalReplayStep
                {
                    Kind = "image",
                    Path = "Q:/repo/.midterm/uploads/image_1.png",
                    MimeType = "image/png"
                },
                new AppServerControlTerminalReplayStep
                {
                    Kind = "text",
                    Text = " and another "
                },
                new AppServerControlTerminalReplayStep
                {
                    Kind = "image",
                    Path = "Q:/repo/.midterm/uploads/image_2.png",
                    MimeType = "image/png"
                }
            ],
            (data, _) =>
            {
                sentInputs.Add(Encoding.UTF8.GetString(data));
                return Task.CompletedTask;
            },
            (path, mimeType, _) =>
            {
                imagePastes.Add((path, mimeType));
                return Task.FromResult(true);
            },
            (delayMs, _) =>
            {
                delays.Add(delayMs);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(["Test ", " and another ", "\r"], sentInputs);
        Assert.Equal(
            [
                ("Q:/repo/.midterm/uploads/image_1.png", "image/png"),
                ("Q:/repo/.midterm/uploads/image_2.png", "image/png")
            ],
            imagePastes);
        Assert.Equal(
            [
                TerminalReplayExecutor.TextBeforeImageSettleDelayMs,
                TerminalReplayExecutor.ImageSettleDelayMs,
                TerminalReplayExecutor.TextBeforeImageSettleDelayMs,
                TerminalReplayExecutor.ImageSettleDelayMs,
                TerminalReplayExecutor.SubmitDelayMs
            ],
            delays);
    }

    [Fact]
    public async Task ExecuteAsync_FallsBackToQuotedPathWhenImagePasteFails()
    {
        var sentInputs = new List<string>();

        await TerminalReplayExecutor.ExecuteAsync(
            [
                new AppServerControlTerminalReplayStep
                {
                    Kind = "image",
                    Path = "Q:/repo/.midterm/uploads/image_1.png",
                    MimeType = "image/png"
                }
            ],
            (data, _) =>
            {
                sentInputs.Add(Encoding.UTF8.GetString(data));
                return Task.CompletedTask;
            },
            (_, _, _) => Task.FromResult(false),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(["\"Q:/repo/.midterm/uploads/image_1.png\"", "\r"], sentInputs);
    }

    [Fact]
    public async Task ExecuteAsync_WrapsTextFileContentInBracketedPasteWhenRequested()
    {
        var tempFile = Path.GetTempFileName();
        var sentInputs = new List<string>();

        try
        {
            await File.WriteAllTextAsync(tempFile, "alpha\nbeta");
            await TerminalReplayExecutor.ExecuteAsync(
                [
                    new AppServerControlTerminalReplayStep
                    {
                        Kind = "textFile",
                        Path = tempFile,
                        UseBracketedPaste = true
                    }
                ],
                (data, _) =>
                {
                    sentInputs.Add(Encoding.UTF8.GetString(data));
                    return Task.CompletedTask;
                },
                (_, _, _) => Task.FromResult(false),
                static (_, _) => Task.CompletedTask,
                CancellationToken.None);
        }
        finally
        {
            File.Delete(tempFile);
        }

        Assert.Equal(["\u001b[200~alpha\rbeta\u001b[201~", "\r"], sentInputs);
    }

    [Fact]
    public async Task ExecuteAsync_BatchesAdjacentTextPathAndFileStepsWithTerminalNewlines()
    {
        var tempFile = Path.GetTempFileName();
        var sentInputs = new List<string>();
        var delays = new List<int>();

        try
        {
            await File.WriteAllTextAsync(tempFile, "delta\nomega");
            await TerminalReplayExecutor.ExecuteAsync(
                [
                    new AppServerControlTerminalReplayStep
                    {
                        Kind = "text",
                        Text = "alpha\nbeta "
                    },
                    new AppServerControlTerminalReplayStep
                    {
                        Kind = "filePath",
                        Path = "Q:/repo/a b.txt"
                    },
                    new AppServerControlTerminalReplayStep
                    {
                        Kind = "text",
                        Text = " gamma\r\ndone "
                    },
                    new AppServerControlTerminalReplayStep
                    {
                        Kind = "textFile",
                        Path = tempFile
                    }
                ],
                (data, _) =>
                {
                    sentInputs.Add(Encoding.UTF8.GetString(data));
                    return Task.CompletedTask;
                },
                (_, _, _) => Task.FromResult(false),
                (delayMs, _) =>
                {
                    delays.Add(delayMs);
                    return Task.CompletedTask;
                },
                CancellationToken.None);
        }
        finally
        {
            File.Delete(tempFile);
        }

        Assert.Equal(["alpha\rbeta \"Q:/repo/a b.txt\" gamma\rdone delta\romega", "\r"], sentInputs);
        Assert.Equal([TerminalReplayExecutor.SubmitDelayMs], delays);
    }

    [Fact]
    public async Task ExecuteAsync_BatchesMixedBracketedAndPlainTextWithoutDroppingMarkers()
    {
        var sentInputs = new List<string>();

        await TerminalReplayExecutor.ExecuteAsync(
            [
                new AppServerControlTerminalReplayStep
                {
                    Kind = "text",
                    Text = "a\nb"
                },
                new AppServerControlTerminalReplayStep
                {
                    Kind = "text",
                    Text = "c\nd",
                    UseBracketedPaste = true
                }
            ],
            (data, _) =>
            {
                sentInputs.Add(Encoding.UTF8.GetString(data));
                return Task.CompletedTask;
            },
            (_, _, _) => Task.FromResult(false),
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(["a\rb\u001b[200~c\rd\u001b[201~", "\r"], sentInputs);
    }

    [Fact]
    public async Task ExecuteAsync_PreservesTextFileImageTextFileReplayOrderWithBoundarySettle()
    {
        var firstTextFile = Path.GetTempFileName();
        var secondTextFile = Path.GetTempFileName();
        var sentInputs = new List<string>();
        var imagePastes = new List<string>();
        var delays = new List<int>();

        try
        {
            await File.WriteAllTextAsync(firstTextFile, "first\nblock");
            await File.WriteAllTextAsync(secondTextFile, "second\nblock");
            await TerminalReplayExecutor.ExecuteAsync(
                [
                    new AppServerControlTerminalReplayStep
                    {
                        Kind = "textFile",
                        Path = firstTextFile
                    },
                    new AppServerControlTerminalReplayStep
                    {
                        Kind = "image",
                        Path = "Q:/repo/.midterm/uploads/screen.png",
                        MimeType = "image/png"
                    },
                    new AppServerControlTerminalReplayStep
                    {
                        Kind = "textFile",
                        Path = secondTextFile
                    }
                ],
                (data, _) =>
                {
                    sentInputs.Add(Encoding.UTF8.GetString(data));
                    return Task.CompletedTask;
                },
                (path, _, _) =>
                {
                    imagePastes.Add(path);
                    return Task.FromResult(true);
                },
                (delayMs, _) =>
                {
                    delays.Add(delayMs);
                    return Task.CompletedTask;
                },
                CancellationToken.None);
        }
        finally
        {
            File.Delete(firstTextFile);
            File.Delete(secondTextFile);
        }

        Assert.Equal(["first\rblock", "second\rblock", "\r"], sentInputs);
        Assert.Equal(["Q:/repo/.midterm/uploads/screen.png"], imagePastes);
        Assert.Equal(
            [
                TerminalReplayExecutor.TextBeforeImageSettleDelayMs,
                TerminalReplayExecutor.ImageSettleDelayMs,
                TerminalReplayExecutor.SubmitDelayMs
            ],
            delays);
    }
}
