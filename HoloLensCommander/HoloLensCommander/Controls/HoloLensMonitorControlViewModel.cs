﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Microsoft.Tools.WindowsDevicePortal;
using static Microsoft.Tools.WindowsDevicePortal.DevicePortal;

namespace HoloLensCommander
{
    /// <summary>
    /// The view model for the HoloLensMonitorControl object.
    /// </summary>
    partial class HoloLensMonitorControlViewModel : INotifyPropertyChanged, IDisposable
    {
        /// <summary>
        /// Text labels describing the power connection state.
        /// </summary>
        private static readonly string OnAcPowerLabel = "";     // EBB2 (battery with plug)
        private static readonly string OnBatteryLabel = "";     // EBA7 (battery)

        /// <summary>
        /// Message to display when the heartbeat is lost.
        /// </summary>
        private static readonly string HeartbeatLostMessage = "Lost Connection to Device";

        /// <summary>
        /// The HoloLensMonitor object responsible for communication with this HoloLens.
        /// </summary>
        private HoloLensMonitor holoLensMonitor;

        /// <summary>
        /// The HoloLensMonitorControl object to which this view model is registered.
        /// </summary>
        private HoloLensMonitorControl holoLensMonitorControl;

        /// <summary>
        /// Indicates whether or not the monitor control was selected prior to loss of heartbeat.
        /// </summary>
        private bool oldIsSelected = false;

        /// <summary>
        /// Event that is notified when a property value has changed.
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Initializes a new instance of the <see cref="HoloLensMonitorControlViewModel" /> class.
        /// </summary>
        /// <param name="control">The HoloLensMonitorControl to which this object is registered.</param>
        /// <param name="monitor">The HoloLensMonitor responsible for communication with this HoloLens.</param>
        public HoloLensMonitorControlViewModel(
            HoloLensMonitorControl control,
            HoloLensMonitor monitor)
        {
            this.holoLensMonitorControl = control;

            this.RegisterCommands();

            this.holoLensMonitor = monitor;
            this.holoLensMonitor.HeartbeatLost += HoloLens_HeartbeatLost;
            this.holoLensMonitor.HeartbeatReceived += HoloLens_HeartbeatReceived;
            this.holoLensMonitor.AppInstallStatus += HoloLensMonitor_AppInstallStatus;

            this.IsConnected = true;

            this.Address = holoLensMonitor.Address;
            this.IsSelected = true;
        }

        /// <summary>
        /// Finalizer so that we are assured we clean up all encapsulated resources.
        /// </summary>
        /// <remarks>Call Dispose on this object to avoid running the finalizer.</remarks>
        HoloLensMonitorControlViewModel()
        {
            Debug.WriteLine("[~HoloLensMonitorControlViewModel]");
            this.Dispose();
        }

        /// <summary>
        /// Cleans up objects managed by the HoloLensMonitorControlViewModel.
        /// </summary>
        /// <remarks>
        /// Failure to call this method will result in the object not being collected until
        /// finalization occurs.
        /// </remarks>
        public void Dispose()
        {
            Debug.WriteLine("[HoloLensMonitorControlViewModel.Dispose]");
            this.holoLensMonitor.HeartbeatLost -= HoloLens_HeartbeatLost;
            this.holoLensMonitor.HeartbeatReceived -= HoloLens_HeartbeatReceived;
            this.holoLensMonitor.AppInstallStatus -= HoloLensMonitor_AppInstallStatus;
            this.holoLensMonitor.Dispose();
            this.holoLensMonitor = null;

            GC.SuppressFinalize(this);
        }

        
        /// <summary>
        /// Closes all applications running on this HoloLens.
        /// </summary>
        /// <returns>Task object used for tracking method completion.</returns>
        internal async Task CloseAllAppsAsync()
        {
            if (this.IsConnected && this.IsSelected)
            {
                try
                {
                    await this.holoLensMonitor.TerminateAllApplicationsAsync();
                }
                catch(Exception e)
                {
                    this.StatusMessage = string.Format(
                        "Failed to close one or more applications - {0}",
                        e.Message);
                }
            }
        }

        /// <summary>
        /// Queries the HoloLens for the names of all installed applications.
        /// </summary>
        /// <returns>List of application names.</returns>
        internal async Task<List<string>> GetInstalledAppNamesAsync()
        {
            List<string> appNames = new List<string>();

            // Whether or not a device is selected, we still need to know what apps it has installed
            if (this.IsConnected)
            {
                try
                {
                    AppPackages installedApps = await this.holoLensMonitor.GetInstalledApplicationsAsync();
                    appNames = Utilities.GetAppNamesFromPackageInfo(
                        installedApps.Packages,
                        true);
                }
                catch
                {
                }
            }
            
            return appNames;
        }

        /// <summary>
        /// Downloads mixed reality files from the HoloLens.
        /// </summary>
        /// <param name="parentFolder">The parent folder which will contain the HoloLens specific folder.</param>
        /// <param name="deleteAfterDownload">Value indicating whether or not files are to be deleted
        /// from the HoloLens after they have been downloaded.</param>
        /// <returns>The name of the folder in to which the files were downloaded.</returns>
        internal async Task<string> GetMixedRealityFilesAsync(
            StorageFolder parentFolder,
            bool deleteAfterDownload)
        {
            string folderName = null;

            if (this.IsConnected && this.IsSelected)
            {
                try
                {
                    MrcFileList fileList = await this.holoLensMonitor.GetMixedRealityFileListAsync();
                    
                    if (fileList.Files.Count != 0)
                    {
                        // Create the folder for this HoloLens' files.
                        StorageFolder folder = await parentFolder.CreateFolderAsync(
                            (string.IsNullOrWhiteSpace(this.Name) ? this.Address : this.Name),
                            CreationCollisionOption.OpenIfExists);
                        folderName = folder.Name;

                        foreach (MrcFileInformation fileInfo in fileList.Files)
                        {
                            try
                            {
                                byte[] fileData = await this.holoLensMonitor.GetMixedRealityFileAsync(fileInfo.FileName);

                                StorageFile file = await folder.CreateFileAsync(
                                    fileInfo.FileName,
                                    CreationCollisionOption.ReplaceExisting);

                                using (Stream stream = await file.OpenStreamForWriteAsync())
                                {
                                    await stream.WriteAsync(fileData, 0, fileData.Length);
                                    await stream.FlushAsync();
                                }

                                this.StatusMessage = string.Format(
                                    "{0} downloaded",
                                    fileInfo.FileName);

                                if (deleteAfterDownload)
                                {
                                    await this.holoLensMonitor.DeleteMixedRealityFile(fileInfo.FileName);
                                }
                            }
                            catch(Exception e)
                            {
                                this.StatusMessage = string.Format(
                                    "Failed to download {0} - {1}",
                                    fileInfo.FileName,
                                    e.Message);
                            }
                        }
                    }
                }
                catch(Exception e)
                {
                    this.StatusMessage = string.Format(
                        "Failed to get mixed reality files - {0}",
                        e.Message);
                }
            }
            
            return folderName;   
        }

        /// <summary>
        /// Installs an application on this HoloLens.
        /// </summary>
        /// <param name="appPackage">The fully qualified path to the application package.</param>
        /// <returns>Task object used for tracking method completion.</returns>
        internal async Task InstallAppAsync(string appPackage)
        {
            if (this.IsConnected && this.IsSelected)
            {
                try
                {
                    await this.holoLensMonitor.InstallApplicationAsync(appPackage);
                }
                catch(Exception e)
                {
                    this.StatusMessage = string.Format(
                        "Failed to install {0} - {1}",
                        appPackage,
                        e.Message);
                }
            }
        }

        /// <summary>
        /// Launches an applicaiton on this HoloLens.
        /// </summary>
        /// <param name="appName">The name of the application to launch.</param>
        /// <returns>The process identifier of the running application.</returns>
        internal async Task<int> LaunchAppAsync(string appName)
        {
            int processId = 0;

            if (this.IsConnected && this.IsSelected)
            {
                try
                {
                    string appId = null;
                    string packageName = null;

                    AppPackages installedApps = await this.holoLensMonitor.GetInstalledApplicationsAsync();

                    foreach (PackageInfo packageInfo in installedApps.Packages)
                    {
                        if (appName == packageInfo.Name)
                        {
                            appId = packageInfo.AppId;
                            packageName = packageInfo.FullName;
                            break;    
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(appId) &&
                        !string.IsNullOrWhiteSpace(packageName))
                    {
                        processId = await this.holoLensMonitor.LaunchApplicationAsync(
                            appId, 
                            packageName);
                    }

                    Task t = new Task(
                        async () =>
                        {
                            await WatchProcess(processId,
                                appName,
                                2);
                        });
                    t.Start();
                }
                catch(Exception e)
                {
                    this.StatusMessage = string.Format(
                        "Failed to launch {0} - {1}",
                        appName,
                        e.Message);
                }
            }

            return processId;
        }

        /// <summary>
        /// Launches the default web browser and connects to the Windows Device Portal on this HoloLens.
        /// </summary>
        /// <returns>Task object used for tracking method completion.</returns>
        private async Task LaunchDevicePortalAsync()
        {
            await Launcher.LaunchUriAsync(this.holoLensMonitor.DevicePortalUri);
        }

        /// <summary>
        /// Displays the manage applications dialog.
        /// </summary>
        /// <returns>Task object used for tracking method completion.</returns>
        private async Task ManageAppsAsync()
        {
            ContentDialog dialog = new ManageAppsDialog(this.holoLensMonitor, this.holoLensMonitorControl);
            await dialog.ShowAsync();
        }
        
        /// <summary>
        /// Displays the mixed reality view dialog.
        /// </summary>
        /// <returns>Task object used for tracking method completion.</returns>
        private async Task MixedRealityViewAsync()
        {
            ContentDialog dialog = new MixedRealityViewDialog(this.holoLensMonitor);
            await dialog.ShowAsync();
        }

        /// <summary>
        /// Reboots this HoloLens.
        /// </summary>
        /// <returns>Task object used for tracking method completion.</returns>
        internal async Task RebootAsync()
        {
            if (this.IsConnected && this.IsSelected)
            {
                this.StatusMessage = "Rebooting";

                await this.holoLensMonitor.RebootAsync();

                this.IsConnected = false;
            }
        }

        /// <summary>
        /// Displays the set IPD dialog.
        /// </summary>
        /// <returns>Task object used for tracking method completion.</returns>
        internal async Task SetIpdAsync()
        {
            UserInformation userInfo = new UserInformation();
            float.TryParse(this.Ipd, out userInfo.Ipd);

            ContentDialog dialog = new SetIpdDialog(
                this.holoLensMonitor.Address, 
                userInfo);
            ContentDialogResult result = await dialog.ShowAsync().AsTask<ContentDialogResult>();;

            // Primary button == "Set"
            if (result == ContentDialogResult.Primary)
            {
                // Update the IPD on the HoloLens
                try
                {
                    await this.holoLensMonitor.SetIpd(userInfo.Ipd);
                }
                catch (Exception e)
                {
                    this.StatusMessage = string.Format(
                        "Unable to update the IPD - {0}",
                        e.Message);
                }
            }
        }

        /// <summary>
        /// Displays the device information dialog.
        /// </summary>
        /// <returns>Task object used for tracking method completion.</returns>
        internal async Task ShowDeviceInfoAsync()
        {
            ContentDialog dialog = new HoloLensInformationDialog(this.holoLensMonitor);
            await dialog.ShowAsync().AsTask<ContentDialogResult>();
        }

        /// <summary>
        /// Shuts down this HoloLens.
        /// </summary>
        /// <returns>Task object used for tracking method completion.</returns>
        internal async Task ShutdownAsync()
        {
            if (this.IsConnected && this.IsSelected)
            {
                this.StatusMessage = "Shutting down";

                await this.holoLensMonitor.ShutdownAsync();

                this.IsConnected = false;
            }
        }

        /// <summary>
        /// Starts recording a mixed reality video on this HoloLens.
        /// </summary>
        /// <returns>Task object used for tracking method completion.</returns>
        internal async Task StartMixedRealityRecordingAsync()
        {
            if (this.IsConnected && this.IsSelected)
            {
                this.StatusMessage = "Starting mixed reality recording";

                await this.holoLensMonitor.StartMixedRealityRecordingAsync();
            }
        }

        /// <summary>
        /// Stops the mixed reality recording on this HoloLens.
        /// </summary>
        /// <returns>Task object used for tracking method completion.</returns>
        internal async Task StopMixedRealityRecordingAsync()
        {
            if (this.IsConnected && this.IsSelected)
            {
                await this.holoLensMonitor.StopMixedRealityRecordingAsync();

                this.StatusMessage = "Mixed reality recording stopped";
            }
        }

        /// <summary>
        /// Displays the tag HoloLens dialog.
        /// </summary>
        /// <returns>Task object used for tracking method completion.</returns>
        private async Task TagHoloLensAsync()
        {
            TagInformation tagInfo = new TagInformation();
            tagInfo.Name = this.Name;

            ContentDialog dialog = new TagHoloLensDialog(
                this.holoLensMonitor.Address,
                tagInfo);
            ContentDialogResult result = await dialog.ShowAsync().AsTask<ContentDialogResult>();;

            // Primary button == "Ok"
            if (result == ContentDialogResult.Primary)
            {
                this.Name = tagInfo.Name;
                this.holoLensMonitorControl.NotifyTagChanged();
            }
        }

        /// <summary>
        /// Unintalls an application on this HoloLens.
        /// </summary>
        /// <returns>Task object used for tracking method completion.</returns>
        internal async Task UninstallAppAsync(string appName)
        {
            if (this.IsConnected && this.IsSelected)
            {
                try
                {
                    AppPackages installedApps = await this.holoLensMonitor.GetInstalledApplicationsAsync();

                    string packageName = Utilities.GetPackageNameFromAppName(
                        appName,
                        installedApps);
                
                    if (packageName == null)
                    {
                        throw new Exception(
                            string.Format("App ({0}) could not be found",
                            appName));
                    }

                    await this.holoLensMonitor.UninstallApplicationAsync(packageName);

                    this.StatusMessage = "Uninstall complete";
                }
                catch (Exception e)
                {
                    this.StatusMessage = string.Format(
                        "Failed to uninstall {0} - {1}",
                        appName,
                        e.Message);
                }

                this.holoLensMonitorControl.NotifyAppUninstall();
            }
        }

        /// <summary>
        /// Handles the ApplicationInstallStatus event.
        /// </summary>
        /// <param name="sender">The object which sent this event.</param>
        /// <param name="args">Event arguments.</param>
        private void HoloLensMonitor_AppInstallStatus(
            HoloLensMonitor sender, 
            ApplicationInstallStatusEventArgs args)
        {
            this.StatusMessage = args.Message;
        }

        /// <summary>
        /// Handles the HeartbeatLost event.
        /// </summary>
        /// <param name="sender">The object which sent this event.</param>
        private void HoloLens_HeartbeatLost(HoloLensMonitor sender)
        {
            this.IsConnected = false;

            this.StatusMessage = HeartbeatLostMessage;

            // Handle whether or not we were previously selected
            if (!this.oldIsSelected &&
                this.IsSelected)
            {
                this.IsSelected = false;
                this.oldIsSelected = true;
            }

            // Update the heartbeat based UI
            this.PowerIndicator = OnBatteryLabel;
            this.BatteryLevel = "--";
            this.ThermalStatus = Visibility.Collapsed;
            this.Ipd = "--";
        }

        /// <summary>
        /// Handles the HeartbeatReceived event.
        /// </summary>
        /// <param name="sender">The object which sent this event.</param>
        private void HoloLens_HeartbeatReceived(HoloLensMonitor sender)
        {
            this.IsConnected = true;

            // Did we recover from a heartbeat loss?
            if (this.StatusMessage == HeartbeatLostMessage)
            {
                this.StatusMessage = "";
            }

            // Handle whether or not we were previously selected
            if (this.oldIsSelected &&
                !this.IsSelected)
            {
                this.IsSelected = true;
                this.oldIsSelected = false;
            }

            // Update the heartbeat based UI
            this.PowerIndicator = sender.BatteryState.IsOnAcPower ? OnAcPowerLabel : OnBatteryLabel;
            this.BatteryLevel = string.Format("{0}%", sender.BatteryState.Level.ToString("#.00"));
            this.ThermalStatus = (sender.ThermalStage == ThermalStages.Normal) ? Visibility.Collapsed : Visibility.Visible;
            this.Ipd = sender.Ipd.ToString();
        }

        /// <summary>
        /// Method used by WatchProcess to ensure that the status message update is performed on the ui thread.
        /// </summary>
        /// <param name="message">The message to display to the user.</param>
        private void MarshalStatusMessageUpdate(string message)
        {
            CoreDispatcher dispatcher = this.holoLensMonitorControl.Dispatcher;
            if (!dispatcher.HasThreadAccess)
            {
                // Assigning the return value of RunAsync to a Task object to avoid 
                // warning 4014 (call is not awaited).
                Task t = dispatcher.RunAsync(
                    CoreDispatcherPriority.Normal,
                    () =>
                    {
                        this.MarshalStatusMessageUpdate(message);
                    }).AsTask();
                return;
            }

            this.StatusMessage = message;
        }

        /// <summary>
        /// Sends the PropertyChanged events to registered handlers.
        /// </summary>
        /// <param name="propertyName">The name of property that has changed.</param>
        private void NotifyPropertyChanged(string propertyName)
        {
            this.PropertyChanged?.Invoke(
                this, 
                new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Registers the commands supported by this object.
        /// </summary>
        private void RegisterCommands()
        {
            this.DisconnectCommand = new Command(
                (parameter) =>
                {
                    this.Disconnect();
                });

            this.SetIpdCommand = new Command(
                async (parameter) =>
                {
                    await this.SetIpdAsync();
                });

            this.SetTagCommand = new Command(
                async (parameter) =>
                {
                    await this.TagHoloLensAsync();
                });

            this.ShowContextMenuCommand = new Command(
                async (parameter) =>
                {
                    await this.ShowContextMenuAsync(parameter);
                });
        }

        /// <summary>
        /// Monitors running processes and when the specified id is no longer running, update's the status message ui.
        /// </summary>
        /// <param name="processId">The process identifier to watch.</param>
        ///  <param name="appName">The name of the application associated with the process identifier.</param>
        /// <param name="waitSeconds">Time, in seconds, to wait between checking on the processes.</param>
        /// <returns>Task object used for tracking method completion.</returns>
        private async Task WatchProcess(
            int processId,
            string appName,
            float waitSeconds)
        {
            ManualResetEvent resetEvent = new ManualResetEvent(false);
            int waitTime = (int)(waitSeconds * 1000.0f);

            Timer timer = new Timer(
                WatchProcessCallback,
                resetEvent,
                Timeout.Infinite,
                Timeout.Infinite);

            RunningProcesses runningProcesses = null;
            bool processIsRunning = false;

            try
            {
                do
                {
                    MarshalStatusMessageUpdate(string.Format(
                        "Waiting for {0} to exit",
                        appName));

                    resetEvent.Reset();
                    timer.Change(0, waitTime);
                    resetEvent.WaitOne(waitTime * 2);   // Wait no longer than twice the specified time.
                    runningProcesses = await this.holoLensMonitor.GetRunningProcessesAsync();
                    processIsRunning = runningProcesses.Contains(processId);
                }
                while(processIsRunning);

                    MarshalStatusMessageUpdate(string.Format(
                        "{0} has exited",
                        appName));
            }
            catch(Exception e)
            {
                MarshalStatusMessageUpdate(string.Format(
                    "Cannot determine the execution state of {0} - {1}",
                    appName,
                    e.Message));
            }

            timer.Change(
                Timeout.Infinite,
                Timeout.Infinite);
            timer.Dispose();
            timer = null;
        }

        /// <summary>
        /// Timer callback event used when watching a process.
        /// </summary>
        /// <param name="data">The manual reset event that is being waited upon.</param>
        private void WatchProcessCallback(object data)
        {
            ManualResetEvent resetEvent = data as ManualResetEvent;
            resetEvent.Set();
        }
    }
}
