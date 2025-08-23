using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Wabbajack.Paths;

namespace Wabbajack.Common;

public class ProcessHelper
{
    public enum StreamType
    {
        Output,
        Error
    }

    public readonly Subject<(StreamType Type, string Line)> Output = new Subject<(StreamType Type, string)>();


    public AbsolutePath Path { get; set; }
    public IEnumerable<object> Arguments { get; set; } = Enumerable.Empty<object>();

    public bool LogError { get; set; } = true;

    public bool ThrowOnNonZeroExitCode { get; set; } = false;

    public Dictionary<string, string> EnvironmentVariables { get; set; } = new Dictionary<string, string>();
    
    public string? WorkingDirectory { get; set; }

    public async Task<int> Start()
    {
        var args = Arguments.Select(arg =>
        {
            return arg switch
            {
                AbsolutePath abs => $"\"{abs}\"",
                RelativePath rel => $"\"{rel}\"",
                _ => arg.ToString()
            };
        });
        var info = new ProcessStartInfo
        {
            FileName = Path.ToString(),
            Arguments = string.Join(" ", args),
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = WorkingDirectory
        };

        // Set environment variables if provided
        foreach (var kvp in EnvironmentVariables)
        {
            info.EnvironmentVariables[kvp.Key] = kvp.Value;
        }
        var finished = new TaskCompletionSource<int>();

        var p = new Process
        {
            StartInfo = info,
            EnableRaisingEvents = true
        };
        EventHandler Exited = (sender, args) => { finished.SetResult(p.ExitCode); };
        p.Exited += Exited;

        DataReceivedEventHandler OutputDataReceived = (sender, data) =>
        {
            if (string.IsNullOrEmpty(data.Data)) return;
            Output.OnNext((StreamType.Output, data.Data));
        };
        p.OutputDataReceived += OutputDataReceived;

        DataReceivedEventHandler ErrorEventHandler = (sender, data) =>
        {
            if (string.IsNullOrEmpty(data.Data)) return;
            Output.OnNext((StreamType.Error, data.Data));
        };
        p.ErrorDataReceived += ErrorEventHandler;


        p.Start();
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();

        ChildProcessTracker.AddProcess(p);

        try
        {
            p.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception)
        {
            // ignored
        }


        var result = await finished.Task;
        // Do this to make sure everything flushes
        p.WaitForExit();
        p.CancelErrorRead();
        p.CancelOutputRead();
        p.OutputDataReceived -= OutputDataReceived;
        p.ErrorDataReceived -= ErrorEventHandler;
        p.Exited -= Exited;

        Output.OnCompleted();

        if (result != 0 && ThrowOnNonZeroExitCode)
            throw new Exception(
                $"Error executing {Path} - Exit Code {result} - Check the log for more information - {string.Join(" ", args.Select(a => a!.ToString()))}");
        return result;
    }
}