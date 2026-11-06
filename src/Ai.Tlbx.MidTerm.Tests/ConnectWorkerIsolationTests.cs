using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Globalization;
using Ai.Tlbx.MidTerm.Models;
using Ai.Tlbx.MidTerm.Models.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Ai.Tlbx.MidTerm.Services.Connect;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ai.Tlbx.MidTerm.Tests;

public sealed class ConnectWorkerIsolationTests
{
    [Fact]
    public async Task KilledNativeWorkerLeavesHttpOtherPeerAndFreshConnectionsAlive()
    {
        using var app = new AuthenticatedAppFixture();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        await LoginAsync(client);
        var settings = app.Services.GetRequiredService<SettingsService>().SettingsDirectory;
        var mainPid = Environment.ProcessId;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = await CreateAsync(settings, () => failed.TrySetResult(), deadline.Token);
        await using var other = await CreateAsync(settings, () => { }, deadline.Token);
        using var otherProcess = Process.GetProcessById(other.WorkerProcessId);
        using (var doomed = Process.GetProcessById(first.WorkerProcessId))
        {
            Assert.NotEqual(mainPid, doomed.Id);
            doomed.Kill(); // Only the task-owned child; never the installed server.
            await doomed.WaitForExitAsync(deadline.Token);
        }
        await failed.Task.WaitAsync(deadline.Token);
        using var version = await client.GetAsync("/api/version", deadline.Token);
        Assert.True(version.IsSuccessStatusCode);
        Assert.Equal(mainPid, Environment.ProcessId);
        Assert.False(otherProcess.HasExited);
        await using var replacement = await CreateAsync(settings, () => { }, deadline.Token);
        Assert.NotEqual(other.WorkerProcessId, replacement.WorkerProcessId);
        using var replacementProcess = Process.GetProcessById(replacement.WorkerProcessId);
        await replacement.DisposeAsync();
        await other.DisposeAsync();
        Assert.True(replacementProcess.HasExited);
        Assert.True(otherProcess.HasExited);
        using var parent = Process.GetCurrentProcess();
        Assert.DoesNotContain(parent.Modules.Cast<ProcessModule>(), module => module.ModuleName.Contains("datachannel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MalformedNativeOfferClosesOnlyItsWorker()
    {
        using var app = new AuthenticatedAppFixture();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        await LoginAsync(client);
        var settings = app.Services.GetRequiredService<SettingsService>().SettingsDirectory;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = await CreateAsync(settings, () => failed.TrySetResult(), deadline.Token);
        await peer.ApplySignalAsync(new ConnectSignal { Type = "offer", Sdp = "invalid-sdp" }, deadline.Token);
        await failed.Task.WaitAsync(deadline.Token);
        using var version = await client.GetAsync("/api/version", deadline.Token);
        Assert.True(version.IsSuccessStatusCode);
    }

    [Fact]
    public async Task WorkerExitsWhenItsParentDiesEvenWhileWaitingForCommands()
    {
        using var app = new AuthenticatedAppFixture();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var settings = app.Services.GetRequiredService<SettingsService>().SettingsDirectory;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var owner = await CreateAsync(settings, () => { }, deadline.Token);
        using var parent = Process.GetProcessById(owner.WorkerProcessId);
        var start = ConnectWebRtcTransport.StartInfo();
        start.ArgumentList[start.ArgumentList.Count - 2] = parent.Id.ToString(CultureInfo.InvariantCulture);
        start.ArgumentList[start.ArgumentList.Count - 1] = parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        using var child = Process.Start(start)!;
        var stderr = child.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            var init = new ConnectWorkerInitialization(settings, new ConnectSignal { IceServers = [] });
            await ConnectWorkerProtocol.WriteAsync(child.StandardInput.BaseStream, ConnectWorkerMessage.Initialize,
                JsonSerializer.SerializeToUtf8Bytes(init, ConnectJsonContext.Default.ConnectWorkerInitialization), deadline.Token);
            var ready = await ConnectWorkerProtocol.ReadAsync(child.StandardOutput.BaseStream, deadline.Token);
            Assert.Equal(ConnectWorkerMessage.Ready, ready?.Kind);
            var stdout = child.StandardOutput.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            parent.Kill();
            await child.WaitForExitAsync(deadline.Token);
            await stdout;
            await stderr;
            Assert.True(child.HasExited);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(CancellationToken.None);
        }
    }

    private static async Task LoginAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { Password = AuthenticatedAppFixture.Password }, AppJsonContext.Default.LoginRequest,
            cancellationToken: Xunit.TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
    }

    private static async Task<ConnectWebRtcTransport> CreateAsync(string directory, Action failed, CancellationToken ct)
    {
        try
        {
            return await ConnectWebRtcTransport.CreateAsync(directory, new ConnectSignal { IceServers = [] },
                _ => Task.CompletedTask, _ => Task.CompletedTask, () => Task.CompletedTask, failed, ct);
        }
        catch
        {
            foreach (var log in Directory.EnumerateFiles(Path.Combine(directory, "logs", "link-workers")))
                Console.Error.WriteLine(await File.ReadAllTextAsync(log, CancellationToken.None));
            throw;
        }
    }
}
