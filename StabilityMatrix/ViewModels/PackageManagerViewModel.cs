﻿using System.Collections.ObjectModel;
using System.Threading.Tasks;
using StabilityMatrix.Helper;
using StabilityMatrix.Models;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using EventManager = StabilityMatrix.Helper.EventManager;

namespace StabilityMatrix.ViewModels;

public partial class PackageManagerViewModel : ObservableObject
{
    private readonly ILogger<PackageManagerViewModel> logger;
    private readonly ISettingsManager settingsManager;
    private readonly IPackageFactory packageFactory;
    private readonly IDialogFactory dialogFactory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressBarVisibility))]
    private int progressValue;
    
    [ObservableProperty]
    private InstalledPackage selectedPackage;
    
    [ObservableProperty]
    private string progressText;
    
    [ObservableProperty]
    private bool isIndeterminate;

    [ObservableProperty]
    private string installButtonText;

    [ObservableProperty] 
    private bool installButtonEnabled;

    [ObservableProperty] 
    private Visibility installButtonVisibility;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPackage))]
    private bool updateAvailable;

    public PackageManagerViewModel(ILogger<PackageManagerViewModel> logger, ISettingsManager settingsManager,
        IPackageFactory packageFactory, IDialogFactory dialogFactory)
    {
        this.logger = logger;
        this.settingsManager = settingsManager;
        this.packageFactory = packageFactory;
        this.dialogFactory = dialogFactory;

        ProgressText = "shrug";
        InstallButtonText = "Install";
        installButtonEnabled = true;
        ProgressValue = 0;
        Packages = new ObservableCollection<InstalledPackage>(settingsManager.Settings.InstalledPackages);

        if (Packages.Any())
        {
            SelectedPackage = Packages[0];
            InstallButtonVisibility = Visibility.Visible;
        }
        else
        {
            SelectedPackage = new InstalledPackage
            {
                Name = "Click \"Add Package\" to install a package"
            };
            InstallButtonVisibility = Visibility.Collapsed;
        }
    }

    public async Task OnLoaded()
    {
        var installedPackages = settingsManager.Settings.InstalledPackages;
        if (installedPackages.Count == 0)
        {
            return;
        }
        
        Packages.Clear();
        
        foreach (var packageToUpdate in installedPackages)
        {
            var basePackage = packageFactory.FindPackageByName(packageToUpdate.PackageName);
            if (basePackage == null) continue;
            
            var hasUpdate = await basePackage.CheckForUpdates();
            packageToUpdate.UpdateAvailable = hasUpdate;
            Packages.Add(packageToUpdate);
        }

        SelectedPackage = Packages[0];
    }

    public ObservableCollection<InstalledPackage> Packages { get; }

    partial void OnSelectedPackageChanged(InstalledPackage? value)
    {
        if (value == null) return;
        
        UpdateAvailable = value.UpdateAvailable;
        InstallButtonText = value.UpdateAvailable ? "Update" : "Launch";
        InstallButtonVisibility = Visibility.Visible;
    }

    public Visibility ProgressBarVisibility => ProgressValue > 0 || IsIndeterminate ? Visibility.Visible : Visibility.Collapsed;

    [RelayCommand]
    private async Task Install()
    {
        switch (InstallButtonText.ToLower())
        {
            case "update":
                //await UpdateSelectedPackage();
                break;
            case "launch":
                EventManager.Instance.RequestPageChange(typeof(LaunchPage));
                break;
        }
    }

    [RelayCommand]
    private async Task ShowInstallWindow()
    {
        var installWindow = dialogFactory.CreateInstallerWindow();
        installWindow.ShowDialog();
        await OnLoaded();
    }

    private void SelectedPackageOnProgressChanged(object? sender, int progress)
    {
        if (progress == -1)
        {
            IsIndeterminate = true;
        }
        else
        {
            IsIndeterminate = false;
            ProgressValue = progress;
        }
        
        EventManager.Instance.OnGlobalProgressChanged(progress);
    }
}
