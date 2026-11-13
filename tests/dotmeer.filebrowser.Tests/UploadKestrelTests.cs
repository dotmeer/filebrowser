using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using dotmeer.filebrowser.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace dotmeer.filebrowser.Tests;

[Collection("Web application factory")]
public sealed class UploadKestrelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StalledBody_CancelAndDisconnectHaveDistinctResults(bool cancel)
    {
        using var workspace = new TestWorkspace();
        using var host = TestHostFactory.Create(workspace.WriteConfiguration(workspace.RootPath), useKestrel: true);
        var cookies = new CookieContainer();
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies });
        var html = await client.GetStringAsync(host.Address);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(token.Success);

        var id = Guid.NewGuid();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host.Address.Host, host.Address.Port);
        await using var stream = tcp.GetStream();
        var request = $"POST /api/fs/upload?root=files&path=&name=stalled.bin&uploadId={id:D} HTTP/1.1\r\n"
            + $"Host: {host.Address.Authority}\r\n"
            + $"Cookie: {cookies.GetCookieHeader(host.Address)}\r\n"
            + $"X-CSRF-TOKEN: {WebUtility.HtmlDecode(token.Groups[1].Value)}\r\n"
            + "Content-Type: application/octet-stream\r\nContent-Length: 1048576\r\n\r\nabc";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        var repository = host.Services.GetRequiredService<TaskRepository>();
        await WaitForStatusAsync(repository, id, FileTaskStatus.Running);
        if (cancel)
        {
            await host.Services.GetRequiredService<TaskCancellationService>().CancelAsync(id);
        }
        else
        {
            tcp.Close();
        }

        var task = await WaitForStatusAsync(repository, id, cancel ? FileTaskStatus.Cancelled : FileTaskStatus.Failed);
        Assert.Equal(cancel ? FileTaskErrorCodes.CancelledByUser : FileTaskErrorCodes.UploadAborted, task.Error);
        Assert.Equal(cancel ? "not_processed" : "failed", Assert.Single(task.Items).Status);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.RootPath));
        Assert.Empty(await repository.GetTemporaryArtifactsAsync());

        if (cancel)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(stream, leaveOpen: true);
            var statusLine = await reader.ReadLineAsync(timeout.Token);
            Assert.StartsWith("HTTP/1.1 409", statusLine);
        }
    }

    private static async Task<FileTask> WaitForStatusAsync(TaskRepository repository, Guid id, FileTaskStatus status)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var task = await repository.GetAsync(id);
            if (task?.Status == status) return task;
            await Task.Delay(10, timeout.Token);
        }
    }
}
