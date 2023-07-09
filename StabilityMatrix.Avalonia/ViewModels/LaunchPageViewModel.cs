﻿using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.Logging;
using StabilityMatrix.Avalonia.Views;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Helper.Factory;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Packages;
using StabilityMatrix.Core.Processes;
using StabilityMatrix.Core.Python;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels;

[View(typeof(LaunchPageView))]
public partial class LaunchPageViewModel : PageViewModelBase
{
    private readonly ILogger<LaunchPageViewModel> logger;
    private readonly ISettingsManager settingsManager;
    private readonly IPackageFactory packageFactory;
    private readonly IPyRunner pyRunner;
    public override string Title => "Launch";
    public override Symbol Icon => Symbol.PlayFilled;

    [ObservableProperty]
    private TextDocument consoleDocument = new();
    
    [ObservableProperty] private bool launchButtonVisibility;
    [ObservableProperty] private bool stopButtonVisibility;
    [ObservableProperty] private bool isLaunchTeachingTipsOpen = false;
    [ObservableProperty] private bool showWebUiButton;
    
    [ObservableProperty] private InstalledPackage? selectedPackage;
    [ObservableProperty] private ObservableCollection<InstalledPackage> installedPackages = new();

    private BasePackage? runningPackage;
    private bool clearingPackages;
    private string webUiUrl = string.Empty;

    public LaunchPageViewModel(ILogger<LaunchPageViewModel> logger, ISettingsManager settingsManager, IPackageFactory packageFactory,
        IPyRunner pyRunner)
    {
        this.logger = logger;
        this.settingsManager = settingsManager;
        this.packageFactory = packageFactory;
        this.pyRunner = pyRunner;
    }

    public override void OnLoaded()
    {
        LoadPackages();
        lock (InstalledPackages)
        {
            // Skip if no packages
            if (!InstalledPackages.Any())
            {
                //logger.LogTrace($"No packages for {nameof(LaunchViewModel)}");
                return;
            }

            var activePackageId = settingsManager.Settings.ActiveInstalledPackage;
            if (activePackageId != null)
            {
                SelectedPackage = InstalledPackages.FirstOrDefault(
                    x => x.Id == activePackageId) ?? InstalledPackages[0];
            }
            InstalledPackages = new ObservableCollection<InstalledPackage>
            {
                new()
                {
                    DisplayName = "Dank Diffusion",
                    LibraryPath = "Packages\\dank-diffusion"
                }
            };
            SelectedPackage = InstalledPackages[0];
        }
    }

    public void OnLoaded()
    {
        if (Design.IsDesignMode) return;
        
        LoadPackages();
        lock (InstalledPackages)
        {
            // Skip if no packages
            if (!InstalledPackages.Any())
            {
                //logger.LogTrace($"No packages for {nameof(LaunchViewModel)}");
                return;
            }

            var activePackageId = settingsManager.Settings.ActiveInstalledPackage;
            if (activePackageId != null)
            {
                SelectedPackage = InstalledPackages.FirstOrDefault(
                    x => x.Id == activePackageId) ?? InstalledPackages[0];
            }
        }
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        var activeInstall = SelectedPackage;

        if (activeInstall == null)
        {
            // No selected package: error snackbar
            // snackbarService.ShowSnackbarAsync(
            //     "You must install and select a package before launching",
            //     "No package selected").SafeFireAndForget();
            return;
        }

        var activeInstallName = activeInstall.PackageName;
        var basePackage = string.IsNullOrWhiteSpace(activeInstallName)
            ? null
            : packageFactory.FindPackageByName(activeInstallName);

        if (basePackage == null)
        {
            logger.LogWarning(
                "During launch, package name '{PackageName}' did not match a definition",
                activeInstallName);
            // snackbarService.ShowSnackbarAsync(
            //     "Install package name did not match a definition. Please reinstall and let us know about this issue.",
            //     "Package name invalid").SafeFireAndForget();
            return;
        }

        // If this is the first launch (LaunchArgs is null),
        // load and save a launch options dialog in background
        // so that dynamic initial values are saved.
        // if (activeInstall.LaunchArgs == null)
        // {
        //     var definitions = basePackage.LaunchOptions;
        //     // Open a config page and save it
        //     var dialog = dialogFactory.CreateLaunchOptionsDialog(definitions, activeInstall);
        //     var args = dialog.AsLaunchArgs();
        //     settingsManager.SaveLaunchArgs(activeInstall.Id, args);
        // }

        // Clear console
        ConsoleDocument.Text = string.Empty;

        await pyRunner.Initialize();

        // Get path from package
        var packagePath = $"{settingsManager.LibraryDir}\\{activeInstall.LibraryPath!}";

        basePackage.ConsoleOutput += OnProcessOutputReceived;
        // basePackage.Exited += OnExit;
        // basePackage.StartupComplete += RunningPackageOnStartupComplete;

        // Update shared folder links (in case library paths changed)
        //sharedFolders.UpdateLinksForPackage(basePackage, packagePath);

        // Load user launch args from settings and convert to string
        var userArgs = settingsManager.GetLaunchArgs(activeInstall.Id);
        var userArgsString = string.Join(" ", userArgs.Select(opt => opt.ToArgString()));

        // Join with extras, if any
        userArgsString = string.Join(" ", userArgsString, basePackage.ExtraLaunchArguments);
        await basePackage.RunPackage(packagePath, userArgsString);
        runningPackage = basePackage;
        SetProcessRunning(true);
    }
    
    [RelayCommand]
    private void Stop()
    {
        if (runningPackage != null)
        {
            runningPackage.ConsoleOutput -= OnProcessOutputReceived;
            // runningPackage.StartupComplete -= RunningPackageOnStartupComplete;
            // runningPackage.Exited -= OnExit;
        }

        runningPackage?.Shutdown();
        runningPackage = null;
        SetProcessRunning(false);
        ConsoleDocument.Text += $"{Environment.NewLine}Stopped process at {DateTimeOffset.Now}";
        ShowWebUiButton = false;
    }

    // Callback for processes
    private void OnProcessOutputReceived(object? sender, ProcessOutput output)
    {
        var raw = output.RawText;
        // Replace \n and \r with literals
        raw = raw.Replace("\n", "\\n").Replace("\r", "\\r");
        Debug.WriteLine($"output raw: '{raw}', output: '{output.Text}', clear lines: {output.ClearLines}");
        Debug.Flush();
        Dispatcher.UIThread.Post(() =>
        {
            using var update = ConsoleDocument.RunUpdate();
            // Handle remove
            if (output.ClearLines > 0)
            {
                for (var i = 0; i < output.ClearLines; i++)
                {
                    var lastLineIndex = ConsoleDocument.LineCount - 1;
                    var line = ConsoleDocument.Lines[lastLineIndex];
                    ConsoleDocument.Remove(line.Offset, line.Length);
                }
            }
            // Add new line
            ConsoleDocument.Insert(ConsoleDocument.TextLength, output.Text);
        });
    }
    
    private void LoadPackages()
    {
        var packages = settingsManager.Settings.InstalledPackages;
        if (!packages?.Any() ?? true)
        {
            InstalledPackages.Clear();
            return;
        }

        clearingPackages = true;
        InstalledPackages.Clear();

        foreach (var package in packages)
        {
            InstalledPackages.Add(package);
        }

        clearingPackages = false;
    }
    
    private void SetProcessRunning(bool running)
    {
        LaunchButtonVisibility = running;
        StopButtonVisibility = running;
    }

    public override bool CanNavigateNext { get; protected set; }
    public override bool CanNavigatePrevious { get; protected set; }
}
