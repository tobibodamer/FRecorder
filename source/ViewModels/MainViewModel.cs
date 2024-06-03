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
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Media;
using System.Printing;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace FRecorder2
{
  internal partial class MainViewModel : ObservableObject, IDisposable
  {
    private readonly Recorder _recorder = new();
    private readonly AudioDeviceManager _audioDeviceManager;


    private MMDevice? _actualInputDevice = null;
    private MMDevice? _actualOutputDevice = null;

    private CancellationTokenSource _cts = new();
    private readonly CompositeDisposable _disposables = [];


    #region Devices

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
    [NotifyCanExecuteChangedFor(nameof(StopRecordingCommand), nameof(StartRecordingCommand), nameof(SaveRecordingCommand))]
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


    public IAsyncRelayCommand SaveRecordingCommand { get; }

    public IRelayCommand StartRecordingCommand { get; }
    public IRelayCommand StopRecordingCommand { get; }


    public MainViewModel()
    {
      SaveRecordingCommand = new AsyncRelayCommand(SaveRecording, () => IsRecording);
      StartRecordingCommand = new RelayCommand(Start, () => CanStartRecording() && !IsRunning);
      StopRecordingCommand = new AsyncRelayCommand(Stop, () => IsRunning);

      _recorder.RecordingStarted += Recorder_RecordingStarted;
      _recorder.RecordingStopped += Recorder_RecordingStopped;
      _recorder.NewData += Recorder_NewData;

      _audioDeviceManager = new();

      SetupAudioDeviceManagerSubscriptions();
    }


    #region UI Property changes

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
      _audioDeviceManager.ChangeInputDevice(value?.IsDefault == true ? AudioDeviceManager.DefaultDeviceId : value?.DeviceId);
    }

    partial void OnSelectedOutputDeviceChanged(DeviceViewModel? value)
    {
      _audioDeviceManager.ChangeOutputDevice(value?.IsDefault == true ? AudioDeviceManager.DefaultDeviceId : value?.DeviceId);
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

    #endregion


    #region Recording

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

    private bool CanStartRecording()
    {
      if (_recorder.IsRecording)
      {
        return false;
      }

      // At least one (enabled) device must be available
      return (MicRecordEnabled && _actualInputDevice != null) ||
        (SoundRecordEnabled && _actualOutputDevice != null);
    }

    private void Start()
    {
      _cts = new();

      _recorder.StartRecording(
        MicRecordEnabled ? _actualInputDevice : null,
        SoundRecordEnabled ? _actualOutputDevice : null,
        SamplingRate,
        SelectedInputDevice?.NumChannels ?? -1,
        SelectedOutputDevice?.NumChannels ?? -1);

      IsRunning = true;
    }

    private async Task Stop()
    {
      Log.Debug("Stopping...");

      _cts.Cancel();

      // Await tasks to ensure they are finished

      if (WaitForDeviceTask != null)
      {
        await WaitForDeviceTask;
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
        // recording is not started, dont start
        return;
      }

      if (WaitForDeviceTask != null && !WaitForDeviceTask.IsCompleted)
      {
        // Already in wait loop
        return;
      }

      if (_recorder.IsRecording)
      {
        _recorder.StopRecording();
      }

      Log.Debug("Trying to restart recording...");

      if (!CanStartRecording() && !IsRecording)
      {
        // Seems like no device is available, so wait
        Log.Information("Cannot start recording: no device available. Waiting for device...");

        if (WaitForDeviceTask != null && !WaitForDeviceTask.IsCompleted)
        {
          WaitForDeviceTask ??= Task.Run(() => WaitUntilCanStartRecording(_cts.Token));
        }

        return;
      }

      Start();
    }

    private async Task WaitUntilCanStartRecording(CancellationToken cancellationToken)
    {
      try
      {
        while (!cancellationToken.IsCancellationRequested)
        {
          Log.Verbose("Retrying again...");

          if (CanStartRecording())
          {
            Log.Verbose("CanStartRecording is true, terminating loop.");
            break;
          }

          await Task.Delay(2, cancellationToken);
        }

        if (cancellationToken.IsCancellationRequested)
        {
          Log.Debug("Canceled waiting for device.");
          return;
        }

        if (CanStartRecording())
        {
          Log.Information("Device available, trying to start...");
          await Application.Current.Dispatcher.InvokeAsync(Start);
          return;
        }

        Log.Information("Device unavailable, waiting more...");
        await WaitUntilCanStartRecording(cancellationToken);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        Log.Debug("Canceled waiting for device.");
      }
      catch (Exception ex)
      {
        Log.Debug(ex, "Error waiting for device");
      }
    }

    public async Task SaveRecording()
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

    #endregion


    #region Device handling

    private void SetupAudioDeviceManagerSubscriptions()
    {
      // Default devices
      _audioDeviceManager.DefaultInputDeviceIdO
        .Subscribe(id => Log.Debug("Default input device changed '{id}'", id))
        .DisposeWith(_disposables);

      _audioDeviceManager.DefaultOutputDeviceIdO
        .Subscribe(id => Log.Debug("Default output device changed '{id}'", id))
        .DisposeWith(_disposables);

      _audioDeviceManager.DefaultInputDeviceO?
       .Subscribe(OnDefaultInputDeviceChanged)
       .DisposeWith(_disposables);

      _audioDeviceManager.DefaultOutputDeviceO
        .Subscribe(OnDefaultOutputDeviceChanged)
        .DisposeWith(_disposables);


      // Add / Remove

      _audioDeviceManager.DeviceRemovedO
        .Select((x) => OnDeviceRemoved(x.deviceType, x.deviceId, x.wasDefault))
        .Subscribe()
        .DisposeWith(_disposables);

      _audioDeviceManager.DeviceAddedO
        .Select((x) => OnDeviceAdded(x.deviceType, x.device))
        .Subscribe()
        .DisposeWith(_disposables);


      // Selected Devices

      _audioDeviceManager.ActualSelectedInputDeviceO
        .Subscribe(OnActualSelectedInputDeviceChanged)
        .DisposeWith(_disposables);

      _audioDeviceManager.ActualSelectedOutputDeviceO
        .Subscribe(OnActualSelectedOutputDeviceChanged)
        .DisposeWith(_disposables);

      _audioDeviceManager.SelectedInputDeviceIdO.Subscribe((selectedDeviceId) =>
      {
        Log.Debug("User selected input device '{selectedDeviceId}'", selectedDeviceId);

        // Sync change back by selecting correct viewmodel
        if (selectedDeviceId == AudioDeviceManager.DefaultDeviceId)
        {
          SelectDefaultInputDevice();
        }
        else
        {
          SelectedInputDevice = InputDevices.FirstOrDefault(vm => vm.DeviceId == selectedDeviceId);
        }
      });

      _audioDeviceManager.SelectedOutputDeviceIdO.Subscribe((selectedDeviceId) =>
      {
        Log.Debug("User selected output device '{selectedDeviceId}'", selectedDeviceId);

        // Sync change back by selecting correct viewmodel
        if (selectedDeviceId == AudioDeviceManager.DefaultDeviceId)
        {
          SelectDefaultInputDevice();
        }
        else
        {
          SelectedOutputDevice = OutputDevices.FirstOrDefault(vm => vm.DeviceId == selectedDeviceId);
        }
      });
    }

    private void OnActualSelectedInputDeviceChanged(MMDevice? newInputDevice)
    {
      Log.Debug("Actual selected input device '{deviceName}'", newInputDevice?.FriendlyName);

      _actualInputDevice = newInputDevice;

      if (newInputDevice == null && _recorder.InputDevice == null)
      {
        return;
      }

      RestartRecording(false);
    }

    private void OnActualSelectedOutputDeviceChanged(MMDevice? newOutputDevice)
    {
      Log.Debug("Actual selected input device '{deviceName}'", newOutputDevice?.FriendlyName);

      _actualOutputDevice = newOutputDevice;

      if (newOutputDevice == null && _recorder.OutputDevice == null)
      {
        return;
      }

      RestartRecording(false);
    }

    private void OnDefaultInputDeviceChanged(MMDevice? newDefaultDevice)
    {
      try
      {
        Log.Information("Default input device changed to '{name}'.", newDefaultDevice?.FriendlyName);
        DefaultInputDevice.Device = newDefaultDevice;
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error while updating default device for '{deviceId}'", newDefaultDevice?.ID);
      }
    }

    private void OnDefaultOutputDeviceChanged(MMDevice? newDefaultDevice)
    {
      Log.Information("Default output device changed to '{name}'.", newDefaultDevice?.FriendlyName);

      try
      {
        DefaultOutputDevice.Device = newDefaultDevice;
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error while updating default device for '{deviceId}'", newDefaultDevice?.ID);
      }
    }

    private (bool removed, bool wasSelected) OnDeviceRemoved(
      AudioDeviceManager.MMDeviceType deviceType, string deviceId, bool wasDefault)
    {
      Log.Information("Device removed ({deviceType}): '{deviceId}' ({wasDefault}).",
        deviceType, deviceId, wasDefault);

      if (deviceType is AudioDeviceManager.MMDeviceType.Input)
      {
        var inputDeviceToRemove = InputDevices.FirstOrDefault(d => d.DeviceId == deviceId && !d.IsDefault);
        if (inputDeviceToRemove != null)
        {
          bool isSelected = SelectedInputDevice?.DeviceId == deviceId;

          if (wasDefault)
          {
            DefaultInputDevice.Device = null;
          }

          Log.Information("Input device removed: {deviceName}", inputDeviceToRemove.DisplayName);
          return (removed: InputDevices.Remove(inputDeviceToRemove), wasSelected: isSelected);
        }
      }
      else
      {
        var outputDeviceToRemove = OutputDevices.FirstOrDefault(d => d.DeviceId == deviceId && !d.IsDefault);
        if (outputDeviceToRemove != null)
        {
          bool isSelected = SelectedOutputDevice?.DeviceId == deviceId;

          Log.Information("Output device removed: {deviceName}", outputDeviceToRemove.DisplayName);
          return (removed: OutputDevices.Remove(outputDeviceToRemove), wasSelected: isSelected);
        }
      }

      return (removed: false, wasSelected: false);
    }

    private bool OnDeviceAdded(AudioDeviceManager.MMDeviceType deviceType, MMDevice newDevice)
    {
      Log.Information("Device added: {deviceName}", newDevice.FriendlyName);

      if (deviceType is AudioDeviceManager.MMDeviceType.Input &&
          !InputDevices.Any(vm => vm.DeviceId == newDevice.ID && !vm.IsDefault))
      {
        InputDevices.Add(new DeviceViewModel(newDevice, false));

        return true;
      }
      else if (!OutputDevices.Any(vm => vm.DeviceId == newDevice.ID && !vm.IsDefault))
      {
        OutputDevices.Add(new DeviceViewModel(newDevice, false));

        return true;
      }

      return false;
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

    public void SelectDefaultInputDevice()
    {
      SelectedInputDevice = DefaultInputDevice;
    }

    public void SelectDefaultOutputDevice()
    {
      SelectedOutputDevice = DefaultOutputDevice;
    }

    public async Task InitializeDevices()
    {
      try
      {
        Log.Debug("Initializing devices...");

        InputDevices.Add(DefaultInputDevice);
        OutputDevices.Add(DefaultOutputDevice);

        var activeInputDevices = await _audioDeviceManager.ActiveInputDevicesO.FirstValueFromAsync();
        foreach (var device in activeInputDevices)
        {
          InputDevices.Add(new DeviceViewModel(device, false));
        }

        var activeOutputDevices = await _audioDeviceManager.ActiveOutputDevicesO.FirstValueFromAsync();
        foreach (var device in activeOutputDevices)
        {
          OutputDevices.Add(new DeviceViewModel(device, false));
        }

        EnsureDevicesSelected();

        Log.Information("Devices initialized.");
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Error while initializing devices");
      }
    }

    #endregion


    #region Settings
    public void RestoreSettings()
    {
      Log.Debug("Restoring from settings...");

      if (!App.Settings.UseDefaultOutputDevice && App.Settings.SelectedOutputDeviceId != null)
      {
        Log.Debug("Restoring selected output device '{deviceId}'", App.Settings.SelectedOutputDeviceId);

        var outputDeviceToSelect = OutputDevices.FirstOrDefault(d => d.DeviceId == App.Settings.SelectedOutputDeviceId);
        if (outputDeviceToSelect != null)
        {
          SelectedOutputDevice = outputDeviceToSelect;
        }
        else
        {
          Log.Debug("Could not restore saved output device: Not found");
        }
      }

      if (!App.Settings.UseDefaultInputDevice && App.Settings.SelectedInputDeviceId != null)
      {
        Log.Debug("Restoring selected input device '{deviceId}'", App.Settings.SelectedInputDeviceId);

        var outputInputToSelect = InputDevices.FirstOrDefault(d => d.DeviceId == App.Settings.SelectedInputDeviceId);
        if (outputInputToSelect != null)
        {
          SelectedInputDevice = outputInputToSelect;
        }
        else
        {
          Log.Debug("Could not restore saved input device: Not found");
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

      App.Settings.UseDefaultInputDevice = IsDefaultInputDeviceSelected;
      App.Settings.UseDefaultOutputDevice = IsDefaultSoundDeviceSelected;

      App.Settings.RecordDurationInSeconds = DurationInS;
      App.Settings.RecordingFolder = RecordingFolder;
      App.Settings.FileNameTemplate = FileNameTemplate;

      App.Settings.SeperateTracks = SeperateTracks;
      App.Settings.SystemSoundsVolume = PlaybackVolume;
      App.Settings.MicVolume = MicVolume;
    }

    #endregion

    public void Dispose()
    {
      _recorder.Dispose();
      _audioDeviceManager.Dispose();
    }
  }
}
