using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace VolumeLock;

public sealed partial class MainPage : Page
{
    private readonly AudioVolumeService _audio = new();
    private readonly List<AudioEndpoint> _speakerDevices = new();
    private readonly List<AudioEndpoint> _microphoneDevices = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherTimer _watchdogTimer;

    private VolumeLockSession? _speakerLock;
    private VolumeLockSession? _microphoneLock;
    private bool _suppressSpeakerUiEvents;
    private bool _suppressMicrophoneUiEvents;
    private int _speakerRestoreCount;
    private int _microphoneRestoreCount;
    private int _speakerLockGeneration;
    private int _microphoneLockGeneration;
    private int _speakerWatchdogFailures;
    private int _microphoneWatchdogFailures;

    public MainPage()
    {
        InitializeComponent();

        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("当前线程没有可用的 DispatcherQueue。");

        _watchdogTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _watchdogTimer.Tick += WatchdogTimer_Tick;
    }

    private AudioEndpoint? SelectedSpeaker => SpeakerDeviceComboBox.SelectedItem as AudioEndpoint;

    private AudioEndpoint? SelectedMicrophone => MicrophoneDeviceComboBox.SelectedItem as AudioEndpoint;

    public void ToggleSpeakerLockFromTray()
    {
        SpeakerLockToggle.IsOn = !SpeakerLockToggle.IsOn;
    }

    public void ToggleMicrophoneLockFromTray()
    {
        MicrophoneLockToggle.IsOn = !MicrophoneLockToggle.IsOn;
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadDevicesAsync(initial: true);
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _watchdogTimer.Stop();
        StopSpeakerLock(updateStatus: false);
        StopMicrophoneLock(updateStatus: false);
    }

    public void DisposeResources()
    {
        _watchdogTimer.Stop();
        StopSpeakerLock(updateStatus: false);
        StopMicrophoneLock(updateStatus: false);
        _audio.Dispose();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadDevicesAsync(initial: false);
    }

    private void SpeakerDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSpeakerUiEvents)
        {
            return;
        }

        StopSpeakerLock();
        SetSpeakerToggle(false);
        LoadSpeakerVolumeFromDevice();
        SetSpeakerStatus(LocalizationService.GetString("StatusDeviceSwitched"));
    }

    private void MicrophoneDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMicrophoneUiEvents)
        {
            return;
        }

        StopMicrophoneLock();
        SetMicrophoneToggle(false);
        LoadMicrophoneVolumeFromDevice();
        SetMicrophoneStatus(LocalizationService.GetString("StatusDeviceSwitched"));
    }

    private void SpeakerVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSpeakerUiEvents)
        {
            return;
        }

        var level = (float)(e.NewValue / 100.0);
        SpeakerVolumeText.Text = $"{Math.Round(e.NewValue)}%";

        if (SelectedSpeaker is not { } device)
        {
            return;
        }

        try
        {
            if (_speakerLock is not null)
            {
                _speakerLock.UpdateTarget(level);
            }
            else
            {
                _audio.SetVolumeLevel(device.Id, level);
            }

            UpdateTrayState();
        }
        catch (Exception ex)
        {
            ShowInfo(LocalizationService.GetString("ErrorSetSpeakerVolumeFailed"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private void MicrophoneVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressMicrophoneUiEvents)
        {
            return;
        }

        var level = (float)(e.NewValue / 100.0);
        MicrophoneVolumeText.Text = $"{Math.Round(e.NewValue)}%";

        if (SelectedMicrophone is not { } device)
        {
            return;
        }

        try
        {
            if (_microphoneLock is not null)
            {
                _microphoneLock.UpdateTarget(level);
            }
            else
            {
                _audio.SetVolumeLevel(device.Id, level);
            }

            UpdateTrayState();
        }
        catch (Exception ex)
        {
            ShowInfo(LocalizationService.GetString("ErrorSetMicrophoneVolumeFailed"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private void SpeakerLockToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSpeakerUiEvents)
        {
            return;
        }

        if (SpeakerLockToggle.IsOn)
        {
            StartSpeakerLock();
        }
        else
        {
            StopSpeakerLock();
        }
    }

    private void MicrophoneLockToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressMicrophoneUiEvents)
        {
            return;
        }

        if (MicrophoneLockToggle.IsOn)
        {
            StartMicrophoneLock();
        }
        else
        {
            StopMicrophoneLock();
        }
    }

    private void WatchdogTimer_Tick(object? sender, object e)
    {
        if (_speakerLock is not null)
        {
            try
            {
                _speakerLock.Refresh();
                _speakerWatchdogFailures = 0;
            }
            catch (Exception ex)
            {
                _speakerWatchdogFailures++;
                ShowInfo(LocalizationService.GetString("ErrorVolumeLockException"), ex.Message, InfoBarSeverity.Error);

                if (_speakerWatchdogFailures >= 3)
                {
                    StopSpeakerLock();
                }
            }
        }

        if (_microphoneLock is not null)
        {
            try
            {
                _microphoneLock.Refresh();
                _microphoneWatchdogFailures = 0;
            }
            catch (Exception ex)
            {
                _microphoneWatchdogFailures++;
                ShowInfo(LocalizationService.GetString("ErrorVolumeLockException"), ex.Message, InfoBarSeverity.Error);

                if (_microphoneWatchdogFailures >= 3)
                {
                    StopMicrophoneLock();
                }
            }
        }
    }

    private async Task LoadDevicesAsync(bool initial)
    {
        var previousSpeakerId = GetSelectedId(SpeakerDeviceComboBox);
        var previousMicrophoneId = GetSelectedId(MicrophoneDeviceComboBox);

        RefreshButton.IsEnabled = false;

        try
        {
            var speakers = await _audio.GetDevicesAsync(capture: false);
            var microphones = await _audio.GetDevicesAsync(capture: true);
            var defaultSpeakerId = _audio.GetDefaultDeviceId(capture: false);
            var defaultMicrophoneId = _audio.GetDefaultDeviceId(capture: true);

            _suppressSpeakerUiEvents = true;
            _suppressMicrophoneUiEvents = true;
            try
            {
                BindDevices(
                    SpeakerDeviceComboBox,
                    _speakerDevices,
                    speakers,
                    initial ? defaultSpeakerId : previousSpeakerId ?? defaultSpeakerId);

                BindDevices(
                    MicrophoneDeviceComboBox,
                    _microphoneDevices,
                    microphones,
                    initial ? defaultMicrophoneId : previousMicrophoneId ?? defaultMicrophoneId);
            }
            finally
            {
                _suppressSpeakerUiEvents = false;
                _suppressMicrophoneUiEvents = false;
            }

            if (initial || GetSelectedId(SpeakerDeviceComboBox) != previousSpeakerId)
            {
                LoadSpeakerVolumeFromDevice();

                if (!initial && _speakerLock is not null)
                {
                    StopSpeakerLock();
                    SetSpeakerToggle(false);
                }
            }

            if (initial || GetSelectedId(MicrophoneDeviceComboBox) != previousMicrophoneId)
            {
                LoadMicrophoneVolumeFromDevice();

                if (!initial && _microphoneLock is not null)
                {
                    StopMicrophoneLock();
                    SetMicrophoneToggle(false);
                }
            }

            if (speakers.Count == 0 && microphones.Count == 0)
            {
                ShowInfo(
                    LocalizationService.GetString("WarningNoAudioDevicesTitle"),
                    LocalizationService.GetString("WarningNoAudioDevicesMessage"),
                    InfoBarSeverity.Warning);
            }
            else if (!initial)
            {
                ShowInfo(
                    LocalizationService.GetString("InfoDevicesRefreshedTitle"),
                    LocalizationService.GetString("InfoDevicesRefreshedMessage"),
                    InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            ShowInfo(LocalizationService.GetString("ErrorLoadDevicesFailed"), ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void StartSpeakerLock()
    {
        if (SelectedSpeaker is not { } device)
        {
            ShowInfo(
                LocalizationService.GetString("ErrorCannotLockSpeaker"),
                LocalizationService.GetString("ErrorSelectOutputDevice"),
                InfoBarSeverity.Warning);
            SetSpeakerToggle(false);
            return;
        }

        try
        {
            var level = (float)(SpeakerVolumeSlider.Value / 100.0);
            _audio.SetVolumeLevel(device.Id, level);

            var previousLock = _speakerLock;
            _speakerLock = null;
            previousLock?.Dispose();

            var generation = ++_speakerLockGeneration;
            _speakerRestoreCount = 0;
            _speakerWatchdogFailures = 0;
            _speakerLock = _audio.CreateLock(
                device.Id,
                level,
                (target, observed, muted) => OnSpeakerExternalChange(generation, target, observed, muted));

            SetSpeakerStatus(LocalizationService.GetFormatted("StatusLockedWithCount", 0), locked: true);
            UpdateWatchdog();
        }
        catch (Exception ex)
        {
            _speakerLock = null;
            ShowInfo(LocalizationService.GetString("ErrorLockSpeakerFailed"), ex.Message, InfoBarSeverity.Error);
            SetSpeakerToggle(false);
            UpdateWatchdog();
        }
    }

    private void StartMicrophoneLock()
    {
        if (SelectedMicrophone is not { } device)
        {
            ShowInfo(
                LocalizationService.GetString("ErrorCannotLockMicrophone"),
                LocalizationService.GetString("ErrorSelectInputDevice"),
                InfoBarSeverity.Warning);
            SetMicrophoneToggle(false);
            return;
        }

        try
        {
            var level = (float)(MicrophoneVolumeSlider.Value / 100.0);
            _audio.SetVolumeLevel(device.Id, level);

            var previousLock = _microphoneLock;
            _microphoneLock = null;
            previousLock?.Dispose();

            var generation = ++_microphoneLockGeneration;
            _microphoneRestoreCount = 0;
            _microphoneWatchdogFailures = 0;
            _microphoneLock = _audio.CreateLock(
                device.Id,
                level,
                (target, observed, muted) => OnMicrophoneExternalChange(generation, target, observed, muted));

            SetMicrophoneStatus(LocalizationService.GetFormatted("StatusLockedWithCount", 0), locked: true);
            UpdateWatchdog();
        }
        catch (Exception ex)
        {
            _microphoneLock = null;
            ShowInfo(LocalizationService.GetString("ErrorLockMicrophoneFailed"), ex.Message, InfoBarSeverity.Error);
            SetMicrophoneToggle(false);
            UpdateWatchdog();
        }
    }

    private void StopSpeakerLock(bool updateStatus = true)
    {
        _speakerLockGeneration++;
        _speakerLock?.Dispose();
        _speakerLock = null;

        if (updateStatus)
        {
            SetSpeakerStatus(LocalizationService.GetString("StatusUnlocked"));
        }

        UpdateWatchdog();
    }

    private void StopMicrophoneLock(bool updateStatus = true)
    {
        _microphoneLockGeneration++;
        _microphoneLock?.Dispose();
        _microphoneLock = null;

        if (updateStatus)
        {
            SetMicrophoneStatus(LocalizationService.GetString("StatusUnlocked"));
        }

        UpdateWatchdog();
    }

    private void UpdateWatchdog()
    {
        if (_speakerLock is not null || _microphoneLock is not null)
        {
            _watchdogTimer.Start();
        }
        else
        {
            _watchdogTimer.Stop();
        }

        UpdateTrayState();
    }

    private void UpdateTrayState()
    {
        App.UpdateTrayLockState(
            _speakerLock is not null,
            _microphoneLock is not null,
            (int)Math.Round(SpeakerVolumeSlider.Value),
            (int)Math.Round(MicrophoneVolumeSlider.Value));
    }

    private void OnSpeakerExternalChange(int generation, float lockedLevel, float observedLevel, bool muted)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _speakerLockGeneration || _speakerLock is null)
            {
                return;
            }

            _speakerRestoreCount++;
            SpeakerVolumeText.Text = $"{Math.Round(lockedLevel * 100)}%";
            var action = LocalizationService.GetString("LockActionRestore");
            SetSpeakerStatus(LocalizationService.GetFormatted("StatusLockedWithAction", action, _speakerRestoreCount), locked: true);
        });
    }

    private void OnMicrophoneExternalChange(int generation, float lockedLevel, float observedLevel, bool muted)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _microphoneLockGeneration || _microphoneLock is null)
            {
                return;
            }

            _microphoneRestoreCount++;
            MicrophoneVolumeText.Text = $"{Math.Round(lockedLevel * 100)}%";
            var action = LocalizationService.GetString("LockActionRestore");
            SetMicrophoneStatus(LocalizationService.GetFormatted("StatusLockedWithAction", action, _microphoneRestoreCount), locked: true);
        });
    }

    private void LoadSpeakerVolumeFromDevice()
    {
        if (SelectedSpeaker is not { } device)
        {
            return;
        }

        try
        {
            SetSpeakerSlider(_audio.GetVolumeLevel(device.Id));
        }
        catch (Exception ex)
        {
            ShowInfo(LocalizationService.GetString("ErrorReadSpeakerVolumeFailed"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private void LoadMicrophoneVolumeFromDevice()
    {
        if (SelectedMicrophone is not { } device)
        {
            return;
        }

        try
        {
            SetMicrophoneSlider(_audio.GetVolumeLevel(device.Id));
        }
        catch (Exception ex)
        {
            ShowInfo(LocalizationService.GetString("ErrorReadMicrophoneVolumeFailed"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private void SetSpeakerSlider(float level)
    {
        level = Math.Clamp(level, 0f, 1f);

        _suppressSpeakerUiEvents = true;
        try
        {
            SpeakerVolumeSlider.Value = level * 100;
            SpeakerVolumeText.Text = $"{Math.Round(level * 100)}%";
        }
        finally
        {
            _suppressSpeakerUiEvents = false;
        }
    }

    private void SetMicrophoneSlider(float level)
    {
        level = Math.Clamp(level, 0f, 1f);

        _suppressMicrophoneUiEvents = true;
        try
        {
            MicrophoneVolumeSlider.Value = level * 100;
            MicrophoneVolumeText.Text = $"{Math.Round(level * 100)}%";
        }
        finally
        {
            _suppressMicrophoneUiEvents = false;
        }
    }

    private void SetSpeakerToggle(bool isOn)
    {
        _suppressSpeakerUiEvents = true;
        try
        {
            SpeakerLockToggle.IsOn = isOn;
        }
        finally
        {
            _suppressSpeakerUiEvents = false;
        }
    }

    private void SetMicrophoneToggle(bool isOn)
    {
        _suppressMicrophoneUiEvents = true;
        try
        {
            MicrophoneLockToggle.IsOn = isOn;
        }
        finally
        {
            _suppressMicrophoneUiEvents = false;
        }
    }

    private void SetSpeakerStatus(string text, bool locked = false)
    {
        SpeakerStatusText.Text = text;
    }

    private void SetMicrophoneStatus(string text, bool locked = false)
    {
        MicrophoneStatusText.Text = text;
    }

    private static string? GetSelectedId(ComboBox combo) => (combo.SelectedItem as AudioEndpoint)?.Id;

    private static void BindDevices(
        ComboBox combo,
        List<AudioEndpoint> target,
        IReadOnlyList<AudioEndpoint> source,
        string? preferredId)
    {
        target.Clear();
        target.AddRange(source);

        combo.ItemsSource = null;
        combo.ItemsSource = target;

        var selected = target.FirstOrDefault(device => device.Id == preferredId) ?? target.FirstOrDefault();
        combo.SelectedItem = selected;
        combo.IsEnabled = target.Count > 0;
    }

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}
