﻿using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NLog;
using Python.Runtime;
using StabilityMatrix.Helper;
using ILogger = NLog.ILogger;

namespace StabilityMatrix;

public record struct PyVersionInfo(int Major, int Minor, int Micro, string ReleaseLevel, int Serial);

public class PyRunner : IPyRunner
{
    private readonly ILogger<PyRunner> logger;

    private const string RelativeDllPath = @"Assets\Python310\python310.dll";
    private const string RelativeExePath = @"Assets\Python310\python.exe";
    private const string RelativePipExePath = @"Assets\Python310\Scripts\pip.exe";
    private const string RelativeGetPipPath = @"Assets\Python310\get-pip.pyc";
    public static string DllPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RelativeDllPath);
    public static string ExePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RelativeExePath);
    public static string PipExePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RelativePipExePath);
    public static string GetPipPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RelativeGetPipPath);
    
    private static readonly SemaphoreSlim PyRunning = new(1, 1);
    
    private PyIOStream? StdOutStream;
    private PyIOStream? StdErrStream;

    public PyRunner(ILogger<PyRunner> logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// Initializes the Python runtime using the embedded dll.
    /// Can be called with no effect after initialization.
    /// </summary>
    /// <exception cref="FileNotFoundException">Thrown if Python DLL not found.</exception>
    public async Task Initialize()
    {
        if (PythonEngine.IsInitialized) return;

        logger.LogInformation("Initializing Python runtime with DLL: {DllPath}", DllPath);

        // Check PythonDLL exists
        if (!File.Exists(DllPath))
        {
            logger.LogError("Python DLL not found");
            throw new FileNotFoundException("Python DLL not found", DllPath);
        }
        Runtime.PythonDLL = DllPath;
        PythonEngine.Initialize();
        PythonEngine.BeginAllowThreads();

        // Redirect stdout and stderr
        StdOutStream = new PyIOStream();
        StdErrStream = new PyIOStream();
        await RunInThreadWithLock(() =>
        {
            dynamic sys = Py.Import("sys");
            sys.stdout = StdOutStream;
            sys.stderr = StdErrStream;
        });
    }

    /// <summary>
    /// One-time setup for get-pip
    /// </summary>
    public async Task SetupPip()
    {
        if (!File.Exists(GetPipPath))
        {
            throw new FileNotFoundException("get-pip not found", GetPipPath);
        }
        var p = ProcessRunner.StartProcess(ExePath, "-m get-pip");
        await ProcessRunner.WaitForExitConditionAsync(p);
    }

    /// <summary>
    /// Install a Python package with pip
    /// </summary>
    public async Task InstallPackage(string package)
    {
        if (!File.Exists(PipExePath))
        {
            throw new FileNotFoundException("pip not found", PipExePath);
        }
        var p = ProcessRunner.StartProcess(PipExePath, $"install {package}");
        await ProcessRunner.WaitForExitConditionAsync(p);
    }

    /// <summary>
    /// Run a Function with PyRunning lock as a Task with GIL.
    /// </summary>
    /// <param name="func">Function to run.</param>
    /// <param name="waitTimeout">Time limit for waiting on PyRunning lock.</param>
    /// <param name="cancelToken">Cancellation token.</param>
    /// <exception cref="OperationCanceledException">cancelToken was canceled, or waitTimeout expired.</exception>
    private async Task<T> RunInThreadWithLock<T>(Func<T> func, TimeSpan? waitTimeout = null, CancellationToken cancelToken = default)
    {
        // Wait to acquire PyRunning lock
        await PyRunning.WaitAsync(cancelToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using (Py.GIL())
                {
                    return func();
                }
            }, cancelToken);
        }
        finally
        {
            PyRunning.Release();
        }
    }

    /// <summary>
    /// Run an Action with PyRunning lock as a Task with GIL.
    /// </summary>
    /// <param name="action">Action to run.</param>
    /// <param name="waitTimeout">Time limit for waiting on PyRunning lock.</param>
    /// <param name="cancelToken">Cancellation token.</param>
    /// <exception cref="OperationCanceledException">cancelToken was canceled, or waitTimeout expired.</exception>
    private async Task RunInThreadWithLock(Action action, TimeSpan? waitTimeout = null, CancellationToken cancelToken = default)
    {
        // Wait to acquire PyRunning lock
        await PyRunning.WaitAsync(cancelToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using (Py.GIL())
                {
                    action();
                }
            }, cancelToken);
        }
        finally
        {
            PyRunning.Release();
        }
    }

    /// <summary>
    /// Evaluate Python expression and return its value as a string
    /// </summary>
    /// <param name="expression"></param>
    public async Task<string> Eval(string expression)
    {
        return await Eval<string>(expression);
    }

    /// <summary>
    /// Evaluate Python expression and return its value
    /// </summary>
    /// <param name="expression"></param>
    public Task<T> Eval<T>(string expression)
    {
        return RunInThreadWithLock(() =>
        {
            var result = PythonEngine.Eval(expression);
            return result.As<T>();
        });
    }

    /// <summary>
    /// Execute Python code without returning a value
    /// </summary>
    /// <param name="code"></param>
    public Task Exec(string code)
    {
        return RunInThreadWithLock(() =>
        {
            PythonEngine.Exec(code);
        });
    }

    /// <summary>
    /// Return the Python version as a PyVersionInfo struct
    /// </summary>
    public async Task<PyVersionInfo> GetVersionInfo()
    {
        var version = await RunInThreadWithLock(() =>
        {
            dynamic info = PythonEngine.Eval("tuple(__import__('sys').version_info)");
            return new PyVersionInfo(
                info[0].As<int>(),
                info[1].As<int>(),
                info[2].As<int>(),
                info[3].As<string>(),
                info[4].As<int>()
            );
        });
        return version;
    }
}
