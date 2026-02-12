#:property Authors=Oskar Klintrot
#:property Copyright=Copyright (c) Oskar Klintrot 2026
#:property PackageIcon=icon.png
#:property PackageReadmeFile=README.md
#:property PackageLicenseExpression=MIT
#:property RepositoryUrl=https://github.com/OskarKlintrot/Watcher
#:property OutputType=Exe
#:property PackageId=dotnet-watcher
#:property PackAsTool=true
#:property ToolCommandName=watch
#:property Description=dotnet watch, but for file-based apps
#:property PackageTags=Watch,Watcher
#:property PublishTrimmed=true
#:property PublishSelfContained=true
#:property PublishAot=true
#:property StripSymbols=true
#:property RuntimeIdentifiers=linux-x64;linux-arm64;win-x64;win-arm64;

#:property TreatWarningsAsErrors=true
#:property AnalysisLevel=latest-Recommended
#:property WarningsAsErrors=true

#:package Microsoft.Extensions.Hosting@10.0.2

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var separatorIndex = Array.IndexOf(args, "--");

var watcherArgs = separatorIndex >= 0 ? args[..separatorIndex] : args;

var forwardedArgs =
    separatorIndex >= 0 && separatorIndex + 1 < args.Length ? args[(separatorIndex + 1)..] : [];

var debugEnabled = watcherArgs.Any(arg =>
    string.Equals(arg, "--debug", StringComparison.OrdinalIgnoreCase)
);

var filteredArgs = watcherArgs
    .Where(arg => !string.Equals(arg, "--debug", StringComparison.OrdinalIgnoreCase))
    .ToArray();

var filePath =
    filteredArgs.Length > 0
        ? ResolveFilePath(filteredArgs[0])
        : ResolveFilePath(Directory.GetCurrentDirectory());

if (!File.Exists(filePath))
{
    Console.Error.WriteLine($"File not found: {Path.GetFullPath(filePath)}");
    Environment.Exit(1);
    return;
}

var builder = Host.CreateApplicationBuilder(filteredArgs);
if (debugEnabled)
{
    builder.Logging.SetMinimumLevel(LogLevel.Debug);
}

builder.Services.AddSingleton(new WatcherOptions(filePath, forwardedArgs));
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();

static string ResolveFilePath(string inputPath)
{
    if (!Directory.Exists(inputPath))
    {
        return inputPath;
    }

    var fullDirectoryPath = Path.GetFullPath(inputPath);
    var csFiles = Directory.GetFiles(fullDirectoryPath, "*.cs", SearchOption.TopDirectoryOnly);
    if (csFiles.Length == 1)
    {
        return csFiles[0];
    }

    Console.Error.WriteLine(
        csFiles.Length == 0
            ? $"No .cs file found in directory: {fullDirectoryPath}"
            : $"Multiple .cs files found in directory: {fullDirectoryPath}. Specify the file explicitly."
    );
    Environment.Exit(1);
    return string.Empty;
}

sealed record WatcherOptions(string FilePath, string[] ForwardedArgs);

sealed partial class Worker(ILoggerFactory loggerFactory, WatcherOptions options)
    : BackgroundService
{
    private readonly SemaphoreSlim _restartLock = new(1, 1);
    private readonly ILogger _logger = loggerFactory.CreateLogger("Watcher");
    private readonly WatcherOptions _options = options;
    private Process? _process;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var watcher = new ProgramFileWatcher(_options.FilePath);
        watcher.ProgramChanged += args => HandleProgramChangedAsync(args.FilePath, stoppingToken);
        watcher.Start();

        await StartProcessAsync(stoppingToken);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleProgramChangedAsync(
        string filePath,
        CancellationToken cancellationToken
    )
    {
        try
        {
            Log_FileChanged(filePath);
            await RestartProcessAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ignore cancellation during shutdown.
        }
        catch (Exception)
        {
            // Ignore, not sure what to log.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopProcessAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private async Task StartProcessAsync(CancellationToken cancellationToken)
    {
        await _restartLock.WaitAsync(cancellationToken);
        try
        {
            StartProcessThreadUnsafe();
        }
        finally
        {
            _restartLock.Release();
        }
    }

    private void StartProcessThreadUnsafe()
    {
        if (_process is { HasExited: false })
        {
            Log_ProcessAlreadyRunning();
            return;
        }

        Log_ProcessStarting();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            WorkingDirectory =
                Path.GetDirectoryName(Path.GetFullPath(_options.FilePath))
                ?? Directory.GetCurrentDirectory(),
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(_options.FilePath);

        if (_options.ForwardedArgs.Length > 0)
        {
            startInfo.ArgumentList.Add("--");
            foreach (var arg in _options.ForwardedArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        try
        {
            _process = Process.Start(startInfo);
            if (_process is null)
            {
                Log_ProcessStartFailed(
                    new InvalidOperationException("Process.Start returned null.")
                );
            }
        }
        catch (Exception ex)
            when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log_ProcessStartFailed(ex);
            _process = null;
        }
    }

    private async Task RestartProcessAsync(CancellationToken cancellationToken)
    {
        await _restartLock.WaitAsync(cancellationToken);
        try
        {
            Log_ProcessRestarting();
            await StopProcessThreadUnsafeAsync(cancellationToken);
            StartProcessThreadUnsafe();
        }
        finally
        {
            _restartLock.Release();
        }
    }

    private async Task StopProcessAsync(CancellationToken cancellationToken)
    {
        await _restartLock.WaitAsync(cancellationToken);
        try
        {
            await StopProcessThreadUnsafeAsync(cancellationToken);
        }
        finally
        {
            _restartLock.Release();
        }
    }

    private async Task StopProcessThreadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (_process is null)
        {
            Log_ProcessNotRunning();
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                Log_ProcessStopping();
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(cancellationToken);
            }
            else
            {
                Log_ProcessAlreadyExited();
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "File changed: {FilePath}")]
    private partial void Log_FileChanged(string filePath);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Process starting")]
    private partial void Log_ProcessStarting();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Process restarting")]
    private partial void Log_ProcessRestarting();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Process stopping")]
    private partial void Log_ProcessStopping();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Process already running")]
    private partial void Log_ProcessAlreadyRunning();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Process not running")]
    private partial void Log_ProcessNotRunning();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Process already exited")]
    private partial void Log_ProcessAlreadyExited();

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to start dotnet cli")]
    private partial void Log_ProcessStartFailed(Exception exception);
}

sealed class ProgramFileWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly string _fullPath;
    private long _lastEventTicks = DateTime.MinValue.Ticks;
    private readonly long _debounceWindowTicks = TimeSpan.FromMilliseconds(250).Ticks;

    public ProgramFileWatcher(string filePath)
    {
        _fullPath = Path.GetFullPath(filePath);
        _watcher = new FileSystemWatcher(
            Path.GetDirectoryName(_fullPath)!,
            Path.GetFileName(_fullPath)
        )
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileChanged;
    }

    public event Func<ProgramFileChangedEventArgs, Task>? ProgramChanged;

    public void Start()
    {
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileChanged(object? sender, FileSystemEventArgs args)
    {
        if (
            !string.Equals(
                Path.GetFullPath(args.FullPath),
                _fullPath,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return;
        }

        var nowTicks = DateTime.UtcNow.Ticks;
        while (true)
        {
            var lastTicks = Interlocked.Read(ref _lastEventTicks);
            if (nowTicks - lastTicks < _debounceWindowTicks)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastEventTicks, nowTicks, lastTicks) == lastTicks)
            {
                break;
            }
        }

        _ = NotifyProgramChangedAsync(new ProgramFileChangedEventArgs(_fullPath));
    }

    private async Task NotifyProgramChangedAsync(ProgramFileChangedEventArgs args)
    {
        var handler = ProgramChanged;
        if (handler is null)
        {
            return;
        }

        var delegates = handler.GetInvocationList();
        foreach (var del in delegates)
        {
            await ((Func<ProgramFileChangedEventArgs, Task>)del)(args).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }
}

sealed class ProgramFileChangedEventArgs(string filePath) : EventArgs
{
    public string FilePath { get; } = filePath;
}
