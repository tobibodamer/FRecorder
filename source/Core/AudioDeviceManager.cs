using NAudio.CoreAudioApi.Interfaces;
using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using System.Collections.Immutable;
using Serilog;

namespace FRecorder2
{
  public sealed class AudioDeviceManager : IDisposable
  {
    private readonly MMDeviceEnumerator _deviceEnumerator;
    private readonly MMNotificationClient _notificationClient;

    // Notification handler subjects
    private readonly Subject<(string deviceId, DeviceState newState)> _deviceStateChangedSubject = new();
    private readonly Subject<(DataFlow flow, Role role, string deviceId)> _defaultDeviceChangedSubject = new();
    private readonly Subject<string> _deviceAddedSubject = new();
    private readonly Subject<string> _deviceRemovedSubject = new();
    private readonly Subject<string> _deviceChangedSubject = new();

    // Selected devices
    private readonly BehaviorSubject<string?> _selectedInputDeviceSubject;
    private readonly BehaviorSubject<string?> _selectedOutputDeviceSubject;


    public IObservable<IReadOnlyDictionary<string, (MMDeviceType deviceType, MMDevice device)>> ActiveDevicesO { get; }
    public IObservable<IReadOnlyList<MMDevice>> ActiveInputDevicesO { get; }
    public IObservable<IReadOnlyList<MMDevice>> ActiveOutputDevicesO { get; }

    public IObservable<string?> DefaultInputDeviceIdO { get; }
    public IObservable<string?> DefaultOutputDeviceIdO { get; }

    public IObservable<MMDevice?> DefaultInputDeviceO { get; }
    public IObservable<MMDevice?> DefaultOutputDeviceO { get; }

    public IObservable<(MMDeviceType deviceType, MMDevice device)> DeviceAddedO { get; }
    public IObservable<(MMDeviceType deviceType, string deviceId, bool wasDefault)> DeviceRemovedO { get; }

    public IObservable<string?> SelectedInputDeviceIdO { get; }
    public IObservable<string?> SelectedOutputDeviceIdO { get; }

    public IObservable<MMDevice?> ActualSelectedInputDeviceO { get; }
    public IObservable<MMDevice?> ActualSelectedOutputDeviceO { get; }

    /// <summary>
    /// Whether to switch the selected device automatically if it becomes unavailable.
    /// (Default is <see langword="true"/>).
    /// </summary>
    public bool SwitchDeviceAutomatically { get; set; } = true;

    public static string DefaultDeviceId { get; } = "Default";


    [Flags]
    public enum MMDeviceType
    {
      Input = 1,
      Output = 2,
    }

    private record ActiveDevicesState
    {
      public record struct Change(bool IsActive, string DeviceId, MMDeviceType DeviceType);

      public ImmutableDictionary<string, (MMDeviceType deviceType, MMDevice device)> Devices { get; init; }
        = ImmutableDictionary<string, (MMDeviceType deviceType, MMDevice device)>.Empty;

      public Change? LastChange { get; init; }
    }

    public AudioDeviceManager()
    {
      Log.Debug("Initializing audio device manager...");

      _notificationClient = new AudioDeviceManager.MMNotificationClient(this);

      _deviceEnumerator = new MMDeviceEnumerator();
      _deviceEnumerator.RegisterEndpointNotificationCallback(_notificationClient);

      Log.Debug("Enumerating audio endpoints...");

      var initialDevicesWithType = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active)
        .Select(GetDeviceWithType)
        .ToImmutableDictionary(deviceWithType => deviceWithType.device.ID, deviceWithType => deviceWithType);

      Log.Debug("{n} active audio endpoints found.", initialDevicesWithType.Count);

      Log.Debug("Getting default audio endpoints...");

      var defaultInputDevice = _deviceEnumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console) ?
        _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console) : null;

      var defaultOutputDevice = _deviceEnumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console) ? 
        _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console) : null;


      #region Active Devices
      
      // Merged observable when device availability changed
      var deviceActiveChangedO = Observable.Merge<(string deviceId, MMDevice? device)>(
             _deviceStateChangedSubject
              // prevent double emit for other irrelevant state changes
              //.DistinctUntilChanged(change => change.newState.HasFlag(DeviceState.Active))
              .Select(change =>
              {
                var (deviceId, newState) = change;

                if (newState.HasFlag(DeviceState.Active))
                {
                  Debug.WriteLine("Thread id {0}", Environment.CurrentManagedThreadId);
                  return (deviceId, device: _deviceEnumerator.GetDevice(deviceId));
                }

                return (deviceId, device: (MMDevice?)null);
              }),
            _deviceAddedSubject.Select(deviceId => (deviceId, (MMDevice?)_deviceEnumerator.GetDevice(deviceId))),
            _deviceRemovedSubject.Select(deviceId => (deviceId, (MMDevice?)null))
          );

      // Small state machine that maintains active devices based on changes

      var initialActiveDevicesState = new ActiveDevicesState()
      {
        Devices = initialDevicesWithType
      };

      var activeDevicesStateO = deviceActiveChangedO
          .Scan(initialActiveDevicesState, (state, change) =>
          {
            var devices = state.Devices;
            var (deviceId, device) = change;

            if (device is null && devices.TryGetValue(deviceId, out var deviceToRemove))
            {
              return state with
              {
                Devices = devices.Remove(deviceId),
                LastChange = new(false, deviceId, deviceToRemove.deviceType)
              };
            }

            if (device is not null && !devices.ContainsKey(deviceId))
            {
              var deviceToAdd = GetDeviceWithType(device);

              return state with
              {
                Devices = devices.Add(deviceId, deviceToAdd),
                LastChange = new(true, deviceId, deviceToAdd.deviceType)
              };
            }

            return state;
          })
          .StartWith(initialActiveDevicesState)
          .Replay(1)
          .RefCount();

      ActiveDevicesO = activeDevicesStateO.Select(state => state.Devices.AsReadOnly());

      ActiveInputDevicesO = activeDevicesStateO.Select(state =>
        state.Devices.Where(kvp => kvp.Value.deviceType is MMDeviceType.Input)
                     .Select(kvp => kvp.Value.device)
                     .ToList()
                     .AsReadOnly());

      ActiveOutputDevicesO = activeDevicesStateO.Select(state =>
        state.Devices.Where(kvp => kvp.Value.deviceType is MMDeviceType.Output)
                     .Select(kvp => kvp.Value.device)
                     .ToList()
                     .AsReadOnly());

      #endregion

      #region Default devices

      // Default devices

      IObservable<(MMDeviceType deviceType, string? deviceId)> defaultDeviceIdO = _defaultDeviceChangedSubject
        .Where(change => change.role is Role.Console)
        .Select(change =>
        {
          var (dataFlow, role, defaultDeviceId) = change;

          MMDeviceType deviceType = dataFlow switch
          {
            DataFlow.Render => MMDeviceType.Output,
            DataFlow.Capture => MMDeviceType.Input,
            _ => MMDeviceType.Input,
          };

          return (deviceType, (string?)defaultDeviceId);
        })
        .StartWith(
          (MMDeviceType.Input, defaultInputDevice?.ID),
          (MMDeviceType.Output, defaultOutputDevice?.ID)
        )
        .Replay(1)
        .RefCount();

      IObservable<string?> defaultInputDeviceIdO = defaultDeviceIdO
        .Where(change => change.deviceType is MMDeviceType.Input)
        .Select(change => change.deviceId)
        .Replay(1)
        .RefCount();

      IObservable<string?> defaultOutputDeviceIdO = defaultDeviceIdO
        .Where(change => change.deviceType is MMDeviceType.Output)
        .Select(change => change.deviceId)
        .Replay(1)
        .RefCount();


      DefaultInputDeviceIdO = defaultInputDeviceIdO;
      DefaultOutputDeviceIdO = defaultOutputDeviceIdO;

      DefaultInputDeviceO = defaultInputDeviceIdO
          .WithLatestFrom(ActiveDevicesO, (deviceId, activeDevices) =>
          {
            if (deviceId is null)
            {
              return null;
            }

            if (!activeDevices.TryGetValue(deviceId, out var activeDeviceItem) ||
                activeDeviceItem.deviceType != MMDeviceType.Input)
            {
              return null;
            }

            return activeDeviceItem.device;
          })
          .Replay(1)
          .RefCount();

      DefaultOutputDeviceO = defaultOutputDeviceIdO
          .WithLatestFrom(ActiveDevicesO, (deviceId, activeDevices) =>
          {
            if (deviceId is null)
            {
              return null;
            }

            if (!activeDevices.TryGetValue(deviceId, out var activeDeviceItem) ||
                activeDeviceItem.deviceType != MMDeviceType.Output)
            {
              return null;
            }

            return activeDeviceItem.device;
          })
          .Replay(1)
          .RefCount();

      #endregion

      #region Add / Remove

      // Add / Remove

      DeviceAddedO = deviceActiveChangedO
        .Where(x => x.device != null)
        .Select(x => GetDeviceWithType(x.device!));

      var lastActiveChangeO = activeDevicesStateO.Skip(1)
        .Select(state => state.LastChange)
        .Where(change => change.HasValue)
        .Select(change => change!.Value);

      var deviceRemovedO = lastActiveChangeO
        .Where(change => !change.IsActive)
        .Select(change => (change.DeviceType, change.DeviceId));

      DeviceRemovedO = deviceRemovedO
        .SelectMany((removedDevice) =>
        {
          var relevantDefaultDeviceIdO = removedDevice.DeviceType is MMDeviceType.Input
            ? defaultInputDeviceIdO
            : defaultOutputDeviceIdO;

          return relevantDefaultDeviceIdO.Take(1).Select(defaultDeviceId => (
            deviceType: removedDevice.DeviceType,
            deviceId: removedDevice.DeviceId,
            wasDefault: removedDevice.DeviceId == defaultDeviceId
          ));
        });

      #endregion


      #region Selected Devices


      // Initialize selected devices (either default or first)
      _selectedInputDeviceSubject = new(DefaultDeviceId);

      _selectedOutputDeviceSubject = new(DefaultDeviceId);

      //defaultOutputDevice?.ID ??
      //initialDevicesWithType.Values.Where(d => d.deviceType is MMDeviceType.Output).Select(d => d.device.ID).FirstOrDefault()

      SelectedInputDeviceIdO = _selectedInputDeviceSubject.DistinctUntilChanged().AsObservable();
      SelectedOutputDeviceIdO = _selectedOutputDeviceSubject.DistinctUntilChanged().AsObservable();

      // Handle updating selected device on removal
      DeviceRemovedO.WithLatestFrom(Observable.CombineLatest(ActiveDevicesO, DefaultInputDeviceIdO, DefaultOutputDeviceIdO,
        (activeDevices, defaultInputDeviceId, defaultOutputDeviceId) => (activeDevices, defaultInputDeviceId, defaultOutputDeviceId)),
        (removedDevice, x) => (removedDevice, x.activeDevices, x.defaultInputDeviceId, x.defaultOutputDeviceId))
        .Subscribe(x =>
      {
        // We can get the latest value and guarantee that it never blocks, because these all replay once
        //var activeDevices = ActiveDevicesO.Latest().First();

        var (removedDevice, activeDevices, defaultInputDeviceId, defaultOutputDeviceId) = x;

        // Get the correct selected device subject
        var selectedDeviceSubject = removedDevice.deviceType is MMDeviceType.Input
          ? _selectedInputDeviceSubject : _selectedOutputDeviceSubject;

        var currentDefaultDevice = removedDevice.deviceType is MMDeviceType.Input
          ? defaultInputDeviceId : defaultOutputDeviceId;

        var userSelectedDeviceId = selectedDeviceSubject.Value;

        if (!SwitchDeviceAutomatically)
        {
          return;
        }

        if (userSelectedDeviceId == DefaultDeviceId)
        {
          // We dont need to change that, the real calculated device will just emit null.
          return;
        }

        if (userSelectedDeviceId != removedDevice.deviceId)
        {
          // We also dont need to handle this case, the selected device is still valid if another was removed
          return;
        }

        if (removedDevice.wasDefault)
        {
          // default device was selected specifically          
          // wait for default device to change and switch to new default
          defaultDeviceIdO
                    .Where(dd => dd.deviceType == removedDevice.deviceType)
                    .Skip(1) // skip replayed value
                    .Take(1) // subscribe once
                    .Subscribe(newDefault =>
                    {
                      // Check if still the removed id
                      if (selectedDeviceSubject.Value == removedDevice.deviceId)
                      {
                        selectedDeviceSubject.OnNext(newDefault.deviceId);
                      }
                    });
        }
        else
        {
          // Switch to the next best device, which could be the current default or any active device
          var nextBestDevice = activeDevices.Values.FirstOrDefault(x => x.deviceType == removedDevice.deviceType);

          selectedDeviceSubject.OnNext(currentDefaultDevice ?? nextBestDevice.device?.ID);
        }

        // else some other device was removed we dont care about
      });

      // Handle switching selected device when first becomes available
      DeviceAddedO.Subscribe(addedDevice =>
      {
        var selectedDeviceSubject = addedDevice.deviceType is MMDeviceType.Input
          ? _selectedInputDeviceSubject : _selectedOutputDeviceSubject;

        if (selectedDeviceSubject.Value == null && SwitchDeviceAutomatically)
        {
          selectedDeviceSubject.OnNext(addedDevice.device.ID);
        }
      });

      static (MMDevice? device, string? deviceId) getSelectedDevice(
        IReadOnlyDictionary<string, (MMDeviceType deviceType, MMDevice device)> activeDevices,
        string? currentDefault,
        string? userSelected
        )
      {
        if (userSelected == null)
        {
          // No device selected
          return (device: null, deviceId: null);
        }

        if (userSelected == DefaultDeviceId)
        {
          // If user selected DefaultDeviceId, choose the current default device from the active devices

          return currentDefault != null
          ? (activeDevices.GetValueOrDefault(currentDefault).device, deviceId: currentDefault)
            : (device: null, deviceId: null);

          //return defaultInputDeviceIdO.Select(defaultInputDeviceId => 
          //      defaultInputDeviceId != null
          //        ? (state.activeDevices.GetValueOrDefault(defaultInputDeviceId).device, (string?)state.userSelected)
          //        : (device: null, state.userSelected));
        }
        else
        {
          // If user selected a specific device, get it from the active devices
          return (activeDevices.GetValueOrDefault(userSelected).device, deviceId: userSelected);
        }
      }

      // Calculate real selected MMDevice
      ActualSelectedInputDeviceO = Observable.CombineLatest(
          ActiveDevicesO,
          defaultInputDeviceIdO,
          SelectedInputDeviceIdO,
          getSelectedDevice
      )
      .DistinctUntilChanged(x => x.deviceId)
      .Select(x => x.device);

      ActualSelectedOutputDeviceO = Observable.CombineLatest(
          ActiveDevicesO,
          defaultOutputDeviceIdO,
          SelectedOutputDeviceIdO,
          getSelectedDevice
      )
      .DistinctUntilChanged(x => x.deviceId)
      .Select(x => x.device);

      #endregion

      Log.Information("Audio device manager initialized.");
    }

    private static (MMDeviceType deviceType, MMDevice device) GetDeviceWithType(MMDevice device)
    {
      MMDeviceType deviceType = device.DataFlow switch
      {
        DataFlow.Render => MMDeviceType.Output,
        DataFlow.Capture => MMDeviceType.Input,
        _ => MMDeviceType.Input,
      };

      return (deviceType, device);
    }


    public void ChangeInputDevice(string? deviceId)
    {
      _selectedInputDeviceSubject.OnNext(deviceId);
    }

    public void ChangeOutputDevice(string? deviceId)
    {
      _selectedInputDeviceSubject.OnNext(deviceId);
    }

    public void SwitchToDefaultInputDevice()
    {
      _selectedInputDeviceSubject.OnNext(DefaultDeviceId);
    }


    private class MMNotificationClient : IMMNotificationClient
    {
      private readonly AudioDeviceManager _manager;

      public MMNotificationClient(AudioDeviceManager manager)
      {
        _manager = manager;
      }

      public void OnDeviceStateChanged(string deviceId, DeviceState newState)
      {
        Application.Current.Dispatcher.BeginInvoke(() => _manager.OnDeviceStateChanged(deviceId, newState));
      }

      public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
      {
        Application.Current.Dispatcher.BeginInvoke(() => _manager.OnDefaultDeviceChanged(flow, role, defaultDeviceId));
      }

      public void OnDeviceAdded(string deviceId)
      {
        Application.Current.Dispatcher.BeginInvoke(() => _manager.OnDeviceAdded(deviceId));
      }

      public void OnDeviceRemoved(string deviceId)
      {
        Application.Current.Dispatcher.BeginInvoke(() => _manager.OnDeviceRemoved(deviceId));
      }

      public void OnPropertyValueChanged(string deviceId, PropertyKey key)
      {

      }
    }

    private void OnDeviceAdded(string deviceId)
    {
      _deviceAddedSubject.OnNext(deviceId);
    }
    private void OnDeviceRemoved(string deviceId)
    {
      _deviceRemovedSubject.OnNext(deviceId);
    }
    private void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
      _deviceStateChangedSubject.OnNext((deviceId, newState));
    }
    private void OnDefaultDeviceChanged(DataFlow dataFlow, Role role, string defaultDeviceId)
    {
      _defaultDeviceChangedSubject.OnNext((dataFlow, role, defaultDeviceId));
    }

    public void Dispose()
    {
      _deviceEnumerator.UnregisterEndpointNotificationCallback(_notificationClient);
      _deviceEnumerator.Dispose();
    }
  }
}