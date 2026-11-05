using System.Diagnostics;
using System.Text;

namespace GroundControl.Docker;

// Runs commands of the Docker CLI (the configured executable and context).
internal sealed class DockerClient(string executable, string? context)
{
    // Runs the command and returns its output lines.
    public async Task<List<string>> GetOutputAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var output = new List<string>();
        await this.RunAsync(arguments, [], output.Add, cancellationToken);
        return output;
    }

    // Runs the command with the given null-separated input, reports its output lines and returns its standard error;
    // the standard input is closed once the input is written, or once the command exits if it is to be kept open.
    public async Task<string> RunAsync(
        IEnumerable<string> arguments,
        IReadOnlyCollection<string> input,
        Action<string> onOutput,
        CancellationToken cancellationToken,
        Func<string, bool>? onError = null,
        bool throwOnError = true,
        bool keepInputOpen = false)
    {
        // Containers write UTF-8, while the default encoding is the console code page of the process (e.g., IBM850 within an IDE on Windows).
        var argumentList = arguments.ToList();
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (context is not null)
        {
            startInfo.ArgumentList.Add("--context");
            startInfo.ArgumentList.Add(context);
        }

        foreach (var argument in argumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        await using var registration = cancellationToken.Register(() => process.Kill(entireProcessTree: true));

        var errors = new List<string>();
        var errorReading = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(CancellationToken.None) is { } line)
            {
                if (onError?.Invoke(line) != true)
                {
                    errors.Add(line);
                }
            }
        }, CancellationToken.None);

        foreach (var value in input)
        {
            await process.StandardInput.WriteAsync($"{value}\0");
        }

        await process.StandardInput.FlushAsync(CancellationToken.None);
        if (!keepInputOpen)
        {
            process.StandardInput.Close();
        }

        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            onOutput(line);
        }

        await errorReading;
        await process.WaitForExitAsync(CancellationToken.None);
        if (keepInputOpen)
        {
            process.StandardInput.Close();
        }

        var error = string.Join(Environment.NewLine, errors.TakeLast(50));
        if (throwOnError && process.ExitCode != 0 && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{executable} {string.Join(' ', argumentList)} has failed:{Environment.NewLine}{error}");
        }

        return error;
    }
}
