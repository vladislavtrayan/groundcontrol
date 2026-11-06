using System.Text;
using Docker.DotNet;

namespace GroundControl.Docker;

/// <summary>A process started within a container by docker exec, with its output attached.</summary>
internal sealed class ContainerExec(MultiplexedStream stream) : IDisposable
{
    /// <summary>Reports the lines of the standard output and of the standard error of the process until it exits, or until cancelled.</summary>
    public async Task ReadAsync(Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken)
    {
        var output = new LineBuffer(onOutput);
        var error = new LineBuffer(onError);
        var buffer = new byte[16384];
        try
        {
            while (await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken) is { EOF: false } read)
            {
                (read.Target == MultiplexedStream.TargetStream.StandardError ? error : output).Append(buffer.AsSpan(0, read.Count));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Killed.
        }

        output.Flush();
        error.Flush();
    }

    public void Dispose() => stream.Dispose();

    /// <summary>Lines of a stream read in chunks, split before decoding so multi-byte characters are kept whole.</summary>
    private sealed class LineBuffer(Action<string> onLine)
    {
        private readonly MemoryStream pending = new();

        public void Append(ReadOnlySpan<byte> bytes)
        {
            this.pending.Write(bytes);
            var buffer = this.pending.GetBuffer().AsSpan(0, (int)this.pending.Length);
            var consumed = 0;
            for (var newLine = buffer.IndexOf((byte)'\n'); newLine >= 0; newLine = buffer[consumed..].IndexOf((byte)'\n'))
            {
                onLine(Encoding.UTF8.GetString(buffer[consumed..(consumed + newLine)]).TrimEnd('\r'));
                consumed += newLine + 1;
            }

            var remainder = buffer[consumed..].ToArray();
            this.pending.SetLength(0);
            this.pending.Write(remainder);
        }

        public void Flush()
        {
            if (this.pending.Length > 0)
            {
                onLine(Encoding.UTF8.GetString(this.pending.GetBuffer(), 0, (int)this.pending.Length).TrimEnd('\r'));
                this.pending.SetLength(0);
            }
        }
    }
}
