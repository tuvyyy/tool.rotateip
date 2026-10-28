using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace NetworkProfileManager.Tests;

internal static class QaEvidence
{
    public static string Root
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "NetworkProfileManager.sln"))) return Path.Combine(directory.FullName, "artifacts");
            throw new DirectoryNotFoundException("QA workspace not found.");
        }
    }
    public static void Write(string name, object data)
    {
        var path = Path.Combine(Root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }
}
internal sealed class ScriptedProxy : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentBag<Task> handlers = [];
    private readonly ConcurrentBag<TcpClient> clients = [];
    private readonly Task accept;
    public int Connections;
    public ConcurrentQueue<Exception> Errors { get; } = [];
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public ScriptedProxy(Func<TcpClient, CancellationToken, Task> action, IPAddress? address = null)
    {
        listener = new TcpListener(address ?? IPAddress.Loopback, 0); listener.Start();
        accept = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token); clients.Add(client); Interlocked.Increment(ref Connections);
                    handlers.Add(Task.Run(async () =>
                    {
                        using (client) try { await action(client, stop.Token); }
                        catch (Exception ex) { if (!stop.IsCancellationRequested) Errors.Enqueue(ex); }
                    }));
                }
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
        });
    }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync(); listener.Stop(); foreach (var client in clients) client.Dispose();
        await accept; await Task.WhenAll(handlers).WaitAsync(TimeSpan.FromSeconds(5)); stop.Dispose();
    }
}
