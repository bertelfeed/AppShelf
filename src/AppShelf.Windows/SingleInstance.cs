using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AppShelf.Windows;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource cancellation = new();
    public bool IsPrimary { get; }

    public SingleInstance(string dataPath)
    {
        var identity = Environment.UserName + "|" + Path.GetFullPath(dataPath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        pipeName = "AppShelf." + hash;
        mutex = new Mutex(true, @"Local\" + pipeName, out var created);
        IsPrimary = created;
    }

    public async Task ForwardAsync(string command)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        // A taskbar click grants activation rights to this short-lived process.
        // Pass those rights to the resident process before asking it to show a window.
        if (GetNamedPipeServerProcessId(client.SafePipeHandle, out var processId))
            AllowSetForegroundWindow(processId);
        using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync(command);
    }

    public async Task ListenAsync(Action<string> receive)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellation.Token);
                using var reader = new StreamReader(server);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                var command = await reader.ReadLineAsync(deadline.Token);
                if (command is { Length: <= 256 }) receive(command);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { await Task.Delay(100, cancellation.Token).ConfigureAwait(false); }
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
