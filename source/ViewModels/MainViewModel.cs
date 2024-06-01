using ByteSizeLib;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Media;
using System.Printing;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FRecorder2
{
  internal partial class MainViewModel : ObservableObject, IDisposable
  {
    #region Devices

    private readonly MMDeviceEnumerator _deviceEnumerator = new();
    private readonly AsyncMMNotificationClient _mmNotificationClient;
    private TaskCompletionSource<DataFlow> _defaultDeviceChangedTcs = new();

    [ObservableProperty]
    private ObservableCollection<DeviceViewModel> _inputDevices = [];

    [ObservableProperty]
    private ObservableCollection<DeviceViewModel> _outputDevices = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    private DeviceViewModel? _selectedInputDevice;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    private DeviceViewModel? _selectedOutputDevice;

    [ObservableProperty]
    private DeviceViewModel _defaultInputDevice = new(isDefault: true);

    [ObservableProperty]
    private DeviceViewModel _defaultOutputDevice = new(isDefault: true);

    public bool IsDefaultInputDeviceSelected => SelectedInputDevice == DefaultInputDevice;
    public bool IsDefaultSoundDeviceSelected => SelectedOutputDevice == DefaultOutputDevice;

    #endregion


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Duration))]
    private uint _durationInS = 20;

    public TimeSpan Duration => _recorder.BufferDuration;

    public long CurrentMicBufferLength => _recorder.CurrentMicBufferSize;
    public long CurrentPlaybackBufferLength => _recorder.CurrentPlaybackBufferSize;

    public long MicBufferLength => _recorder.MicrophoneBufferLength;
    public long SoundBufferLength => _recorder.PlaybackBufferLength;


    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopRecordingCommand), nameof(StartRecordingCommand), nameof(SaveCommand))]
    private bool _isRecording;


    [ObservableProperty]
    private float _playbackVolume;

    [ObservableProperty]
    private float _micVolume;

    [ObservableProperty]
    private bool _seperateTracks = false;


    [ObservableProperty]
    private SavedRecordingViewModel? _lastSavedFile;

    [ObservableProperty]
    private string _recordingFolder = "";

    [ObservableProperty]
    private string _fileNameTemplate = "";

    [ObservableProperty]
    private int _samplingRate = 32000;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    private bool _micRecordEnabled = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    private bool _soundRecordEnabled = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand), nameof(StopRecordingCommand))]
    private bool _isRunning;



    private TaskNotifier? _waitForDevice;

    /// <summary>
    /// The task that waits for an available device.
    /// </summary>
    public Task? WaitForDeviceTask
    {
      get => _waitForDevice;
      set => SetPropertyAndNotifyOnCompletion(ref _waitForDevice, value);
    }

    private TaskNotifier? _waitForDefaultDevice;

    /// <summary>
    /// The task that waits for the default device to change.
    /// </summary>
    public Task? WaitForDefaultDeviceTask
    {
      get => _waitForDefaultDevice;
      set => SetPropertyAndNotifyOnCompletion(ref _waitForDefaultDevice, value);
    }


    private readonly Recorder _recorder = new();

    public IAsyncRelayCommand SaveCommand { get; }

    public IRelayCommand StartRecordingCommand { get; }
    public IRelayCommand StopRecordingCommand { get; }

    public MainViewModel()
    {
      SaveCommand = new AsyncRelayCommand(Save, () => IsRecording);
      StartRecordingCommand = new RelayCommand(Start, () => CanStartRecording() && !IsRunning);
      StopRecordingCommand = new AsyncRelayCommand(Stop, () => IsRunning);

      _mmNotificationClient = new AsyncMMNotificationClient();
      _deviceEnumerator.RegisterEndpointNotificationCallback(_mmNotificationClient);

      _mmNotificationClient.DeviceStateChanged += OnDeviceStateChanged;
      _mmNotificationClient.DefaultDeviceChanged += OnDefaultDeviceChanged;
      _mmNotificationClient.DeviceAdded += OnDeviceAdded;
      _mmNotificationClient.DeviceRemoved += OnDeviceRemoved;

      _recorder.RecordingStarted += Recorder_RecordingStarted;
      _recorder.RecordingStopped += Recorder_RecordingStopped;
      _recorder.NewData += Recorder_NewData;
    }

    private void Recorder_NewData()
    {
      OnPropertyChanged(nameof(CurrentMicBufferLength));
      OnPropertyChanged(nameof(CurrentPlaybackBufferLength));
    }

    private void Recorder_RecordingStarted()
    {
      IsRecording = true;
    }

    private void Recorder_RecordingStopped()
    {
      IsRecording = false;
    }

    partial void OnMicVolumeChanged(float value)
    {
      _recorder.MicVolume = value;
    }

    partial void OnPlaybackVolumeChanged(float value)
    {
      _recorder.SoundVolume = value;
    }

    partial void OnSeperateTracksChanged(bool value)
    {
      _recorder.SeperateTracks = value;
      RestartRecording();
    }

    partial void OnSelectedInputDeviceChanged(DeviceViewModel? value)
    {
      if (value == null && _recorder.InputDevice == null)
      {
        return;
      }

      RestartRecording(false);
    }

    partial void OnSelectedOutputDeviceChanged(DeviceViewModel? value)
    {
      if (value == null && _recorder.OutputDevice == null)
      {
        return;
      }

      RestartRecording(false);
    }

    partial void OnDurationInSChanged(uint value)
    {
      _recorder.BufferDuration = TimeSpan.FromSeconds(value);
    }

    partial void OnMicRecordEnabledChanged(bool value)
    {
      if (!value && _recorder.InputDevice == null)
      {
        return;
      }

      RestartRecording(false);
    }

    partial void OnSoundRecordEnabledChanged(bool value)
    {
      if (!value && _recorder.OutputDevice == null)
      {
        return;
      }

      RestartRecording(false);
    }

    private bool CanStartRecording()
    {
      if (_recorder.IsRecording)
      {
        return false;
      }

      // At least one (enabled) device must be available
      return (MicRecordEnabled && SelectedInputDevice?.Device != null) ||
        (SoundRecordEnabled && SelectedOutputDevice?.Device != null);
    }

    private void Start()
    {
      _cts = new();

      _defaultDeviceChangedTcs = new();

      _recorder.StartRecording(
        MicRecordEnabled ? SelectedInputDevice?.Device : null,
        SoundRecordEnabled ? SelectedOutputDevice?.Device : null,
        SamplingRate,
        SelectedInputDevice?.NumChannels ?? -1,
        SelectedOutputDevice?.NumChannels ?? -1);

      IsRunning = true;
    }

    private async Task Stop()
    {
      Log.Debug("Stopping...");

      _cts.Cancel();

      if (WaitForDeviceTask != null)
      {
        await WaitForDeviceTask;
      }

      if (WaitForDefaultDeviceTask != null)
      {
        _defaultDeviceChangedTcs?.TrySetCanceled();
        await WaitForDefaultDeviceTask;
      }

      if (_recorder.IsRecording)
      {
        _recorder.StopRecording();
      }

      Log.Information("Stopped.");
      IsRunning = false;
    }

    public void RestartRecording(bool startIfNotStarted = false)
    {
      if (!IsRecording && !startIfNotStarted)
      {
        return;
      }

      if (_recorder.IsRecording)
      {
        _recorder.StopRecording();
      }

      Log.Debug("Trying to restart recording...");

      if (!CanStartRecording() && !IsRecording)
      {
        Log.Information("Cannot start recording: no device available. Waiting for device...");

        WaitForDeviceTask = Task.Run(() =>
        {
          while (!_cts.IsCancellationRequested)
          {
            Log.Verbose("Retrying again...");

            if (CanStartRecording())
            {
              Log.Verbose("CanStartRecording is true, terminating loop.");
              return;
            }
          }
        }, _cts.Token).ContinueWith(t =>
        {
          if (t.IsCanceled)
          {
            Log.Debug("Canceled waiting for device.");
          }
          else if (t.IsFaulted)
          {
            Log.Debug(t.Exception?.InnerException, "Error waiting for device");
          }
          else if (CanStartRecording() && !_cts.IsCancellationRequested)
          {
            Log.Information("Device available, trying to start...");
            Start();
          }
        }, _cts.Token, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());

        //_waitForDeviceAvailableTcs = new();

        //// Wait for MMNotification thread to receive available device. Then
        //// continue trying again (on the current synchronization context
        //// to prevent COM errors).
        //WaitForDeviceTask = _waitForDeviceAvailableTcs.Task.ContinueWith(t =>
        //{
        //  if (t.IsCanceled)
        //  {
        //    Log.Debug("Canceled waiting for device.");
        //  }
        //  else if (t.IsFaulted)
        //  {
        //    Log.Debug(t.Exception?.InnerException, "Error waiting for device");
        //    t.Exception?.Handle(_ => true);
        //  }
        //  else
        //  {
        //    Log.Debug("Received device available signal, trying again...");

        //    // Try again
        //    Start();
        //  }
        //}, _cts.Token, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());

        return;
      }

      Start();
    }


    #region Device handling

    private CancellationTokenSource _cts = new();

    private IEnumerable<ICollection<DeviceViewModel>> GetDeviceCollectionsFromFlow(DataFlow flow)
    {
      if (flow is DataFlow.Capture or DataFlow.All)
      {
        yield return InputDevices;
      }

      if (flow is DataFlow.Render or DataFlow.All)
      {
        yield return OutputDevices;
      }
    }
    private string GetDeviceTypeFromCol(ICollection<DeviceViewModel> col)
    {
      if (col == InputDevices)
      {
        return "input";
      }
      else
      {
        return "output";
      }
    }

    private bool RemoveDevice(string deviceId)
    {
      bool removed = false;
      bool selectedDeviceRemoved = false;

      // Find specific device to remove (not the default device since it should never be removed)
      var inputDeviceToRemove = InputDevices.FirstOrDefault(d => d.DeviceId == deviceId && !d.IsDefault);
      if (inputDeviceToRemove != null)
      {
        selectedDeviceRemoved |= SelectedInputDevice == inputDeviceToRemove;
        InputDevices.Remove(inputDeviceToRemove);
        Log.Information("Input device removed: {deviceName}", inputDeviceToRemove.DisplayName);
        removed = true;
      }

      var outputDeviceToRemove = OutputDevices.FirstOrDefault(d => d.DeviceId == deviceId && !d.IsDefault);
      if (outputDeviceToRemove != null)
      {
        selectedDeviceRemoved |= SelectedOutputDevice == outputDeviceToRemove;
        OutputDevices.Remove(outputDeviceToRemove);
        Log.Information("Output device removed: {deviceName}", outputDeviceToRemove.DisplayName);
        removed = true;
      }

      if (!selectedDeviceRemoved)
      {
        // Nothing to worry about, since it was not a selected device
        return removed;
      }

      bool restartRecording = false;

      if (IsRecording)
      {
        Log.Information("Removed device was in use, stopping recording");
        _recorder.StopRecording();
        restartRecording = true;
      }

      if (IsDefaultDeviceSelected(DataFlow.All))
      {
        Log.Verbose("Removed device was default, waiting for new default device before restarting recording...");

        WaitForDefaultDeviceTask = WaitForDefaultDeviceAsync(restartRecording: true, _cts.Token);
      }
      else
      {
        Log.Debug("Switching to another device...");
        EnsureDevicesSelected();

        bool restartRecordingAutomaticallyForNonDefault = true;
        if (restartRecording && restartRecordingAutomaticallyForNonDefault)
        {
          // Restart recording, if still stopped
          Start();
        }
        else
        {
          // Stop everything
          Task.Factory.StartNew(Stop, _cts.Token, TaskCreationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
        }
      }

      // not found
      return removed;
    }

    private Task WaitForDefaultDeviceAsync(bool restartRecording, CancellationToken cancellationToken = default)
    {
      return Task.Factory.StartNew(async () =>
      {
        // wait for default device change
        if (await Task.WhenAny(Task.Delay(1000, cancellationToken), _defaultDeviceChangedTcs.Task) == _defaultDeviceChangedTcs.Task)
        {
          Log.Debug("New default {dataFlow} device received, switching.", _defaultDeviceChangedTcs.Task.Result);
          _defaultDeviceChangedTcs = new();
        }
        else
        {
          Log.Warning("Timed out while waiting for default device to change, trying anyways.");
        }

        EnsureDevicesSelected();

        if (restartRecording)
        {
          // Restart recording, if still stopped
          Start();
        }

      }, cancellationToken, TaskCreationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void AddDeviceIfNotPresent(MMDevice newDevice)
    {
      bool deviceAdded = false;

      foreach (var col in GetDeviceCollectionsFromFlow(newDevice.DataFlow))
      {
        if (col.Any(d => d.DeviceId == newDevice.ID))
        {
          // Device already there
          continue;
        }

        try
        {
          Log.Information("Adding {deviceType} device '{deviceName}'",
            GetDeviceTypeFromCol(col), newDevice.FriendlyName);

          col.Add(new DeviceViewModel(newDevice, false));

          deviceAdded = true;
        }
        catch (Exception ex)
        {
          Log.Error(ex, "Error while adding device '{deviceId}'.", newDevice.ID);
        }
      }

      //if (deviceAdded && _waitForDeviceAvailableTcs != null)
      //{
      //  Log.Debug("Waiting for new default device before signaling restart...");

      //  WaitForDefaultDeviceTask = WaitForDefaultDeviceAsync(restartRecording: false, _cts.Token)
      //    .ContinueWith(t =>
      //    {
      //      if (t.IsCanceled)
      //      {
      //        _waitForDeviceAvailableTcs?.TrySetCanceled();
      //      }
      //      else if (t.IsFaulted)
      //      {
      //        _waitForDeviceAvailableTcs?.TrySetException(t.Exception?.InnerExceptions ?? Enumerable.Empty<Exception>());
      //      }
      //      else
      //      {
      //        _waitForDeviceAvailableTcs?.TrySetResult();
      //      }
      //    });
      //}
    }

    /// <summary>
    /// If no input or output device is selected, select the default device.
    /// </summary>
    private void EnsureDevicesSelected()
    {
      if (SelectedInputDevice == null)
      {
        SelectDefaultInputDevice();
      }

      if (SelectedOutputDevice == null)
      {
        SelectDefaultOutputDevice();
      }
    }

    private bool IsDefaultDeviceSelected(DataFlow flow)
    {
      return flow switch
      {
        DataFlow.Render => IsDefaultSoundDeviceSelected,
        DataFlow.Capture => IsDefaultInputDeviceSelected,
        DataFlow.All => IsDefaultInputDeviceSelected || IsDefaultSoundDeviceSelected,
        _ => false
      };
    }

    private void OnDeviceRemoved(string deviceId)
    {
      try
      {
        RemoveDevice(deviceId);
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error while removing device '{deviceId}'.", deviceId);
      }
    }

    private void OnDeviceAdded(string deviceId)
    {
      var newDevice = _deviceEnumerator.GetDevice(deviceId);

      AddDeviceIfNotPresent(newDevice);
    }

    private void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
      if (role is not Role.Console)
      {
        return;
      }

      Log.Verbose("Default device changed to '{defaultDeviceId}' for flow '{flow}'.",
              defaultDeviceId, flow, role);

      bool isDefaultDeviceSelected = IsDefaultDeviceSelected(flow);

      try
      {

        foreach (var devices in GetDeviceCollectionsFromFlow(flow))
        {
          var newDefaultDevice = devices.FirstOrDefault(x => x.DeviceId == defaultDeviceId && !x.IsDefault);
          if (devices == InputDevices)
          {
            DefaultInputDevice.Device = newDefaultDevice?.Device;

            Log.Information("Default input device changed to '{deviceName}'", newDefaultDevice?.DisplayName);
          }
          else
          {
            DefaultOutputDevice.Device = newDefaultDevice?.Device;

            Log.Information("Default output device changed to '{deviceName}'", newDefaultDevice?.DisplayName);
          }
        }

        if (IsRecording && isDefaultDeviceSelected)
        {
          // Restart recording
          RestartRecording();
        }

        // Notify waiting task
        _defaultDeviceChangedTcs.TrySetResult(flow);
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error while updating default device for '{deviceId}'", defaultDeviceId);
      }
    }

    private void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
      try
      {
        Log.Debug("Device state changed for '{deviceId}'. New state: {deviceState}",
          deviceId, newState);

        if (!newState.HasFlag(DeviceState.Active))
        {
          RemoveDevice(deviceId);
        }
        else
        {
          var device = _deviceEnumerator.GetDevice(deviceId);
          AddDeviceIfNotPresent(device);
        }
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error while handling device state change for device '{deviceId}'", deviceId);
      }
    }

    private MMDevice? GetDefaultDevice(DataFlow flow, Role role = Role.Console)
    {
      if (_deviceEnumerator.HasDefaultAudioEndpoint(flow, role))
      {
        return _deviceEnumerator.GetDefaultAudioEndpoint(flow, role);
      }
      else
      {
        return null;
      }
    }

    public void LoadDevices()
    {
      foreach (MMDevice device in _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
      {
        InputDevices.Add(new DeviceViewModel(device, false));
      }

      foreach (MMDevice device in _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
      {
        OutputDevices.Add(new DeviceViewModel(device, false));
      }

      var defaultInputDevice = GetDefaultDevice(DataFlow.Capture);
      var defaultOutputDevice = GetDefaultDevice(DataFlow.Render);

      DefaultInputDevice.Device = defaultInputDevice;
      DefaultOutputDevice.Device = defaultOutputDevice;

      InputDevices.Insert(0, DefaultInputDevice);
      OutputDevices.Insert(0, DefaultOutputDevice);

      EnsureDevicesSelected();
    }

    public void SelectDefaultInputDevice()
    {
      SelectedInputDevice = DefaultInputDevice;
    }

    public void SelectDefaultOutputDevice()
    {
      SelectedOutputDevice = DefaultOutputDevice;
    }

    #endregion

    #region Settings
    public void LoadFromSettings()
    {
      if (!App.Settings.AlwaysUseDefaultOutputDevice && App.Settings.SelectedOutputDeviceId != null)
      {
        var outputDeviceToSelect = OutputDevices.FirstOrDefault(d => d.DeviceId == App.Settings.SelectedOutputDeviceId);
        if (outputDeviceToSelect != null)
        {
          SelectedOutputDevice = outputDeviceToSelect;
        }
      }

      if (!App.Settings.AlwaysUseDefaultInputDevice && App.Settings.SelectedInputDeviceId != null)
      {
        var outputInputToSelect = InputDevices.FirstOrDefault(d => d.DeviceId == App.Settings.SelectedInputDeviceId);
        if (outputInputToSelect != null)
        {
          SelectedInputDevice = outputInputToSelect;
        }
      }

      if (App.Settings.RecordDurationInSeconds > 0)
      {
        DurationInS = App.Settings.RecordDurationInSeconds;
      }

      RecordingFolder = App.Settings.RecordingFolder;
      FileNameTemplate = App.Settings.FileNameTemplate;

      SeperateTracks = App.Settings.SeperateTracks;
      PlaybackVolume = App.Settings.SystemSoundsVolume;
      MicVolume = App.Settings.MicVolume;

      if (App.Settings.AutoStartRecording)
      {
        Log.Information("Auto start enabled, starting recording...");
        Start();
      }
    }

    public void SaveToSettings()
    {
      if (SelectedInputDevice != null)
      {
        App.Settings.SelectedInputDeviceId = SelectedInputDevice.DeviceId;
      }

      if (SelectedOutputDevice != null)
      {
        App.Settings.SelectedOutputDeviceId = SelectedOutputDevice.DeviceId;
      }

      App.Settings.AlwaysUseDefaultInputDevice = IsDefaultInputDeviceSelected;
      App.Settings.AlwaysUseDefaultOutputDevice = IsDefaultSoundDeviceSelected;

      App.Settings.RecordDurationInSeconds = DurationInS;
      App.Settings.RecordingFolder = RecordingFolder;
      App.Settings.FileNameTemplate = FileNameTemplate;

      App.Settings.SeperateTracks = SeperateTracks;
      App.Settings.SystemSoundsVolume = PlaybackVolume;
      App.Settings.MicVolume = MicVolume;
    }

    #endregion

    public async Task Save()
    {
      string timestamp = DateTime.Now.ToString("yy_MM_dd_HH_mm_ss");
      string fileName = FileNameTemplate.Replace("{Timestamp}", timestamp, StringComparison.CurrentCultureIgnoreCase) + ".wav";
      string fullFileName = Path.Combine(RecordingFolder, fileName);

      var savedDuration = await _recorder.Save(fullFileName);
      if (savedDuration == null)
      {
        Log.Warning("Could not save recording.");
        return;
      }

      LastSavedFile = new(new(fullFileName))
      {
        Duration = (int)savedDuration.Value.TotalSeconds
      };

      SystemSounds.Exclamation.Play();
    }

    public void Dispose()
    {
      _deviceEnumerator.UnregisterEndpointNotificationCallback(_mmNotificationClient);
      _deviceEnumerator.Dispose();

      _recorder.Dispose();
    }
  }
}
