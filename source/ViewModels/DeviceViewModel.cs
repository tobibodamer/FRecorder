using CommunityToolkit.Mvvm.ComponentModel;
using NAudio.CoreAudioApi;
using System;
using System.Diagnostics.CodeAnalysis;

namespace FRecorder2
{
  public partial class DeviceViewModel : ObservableObject
  {
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDevice))]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private MMDevice? _device;

    [ObservableProperty]
    private int _numChannels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private bool _isDefault;

    [ObservableProperty]
    private string _deviceId;

    public string DisplayName
    {
      get
      {
        string name = Device?.FriendlyName ?? "None";
        return IsDefault ? $"Default ({name})" : name;
      }
    }

    public int MaxChannels => Device?.AudioEndpointVolume.Channels.Count ?? 0;

    partial void OnDeviceChanged(MMDevice? value)
    {
      DeviceId = value?.ID ?? "";
      NumChannels = Math.Min(NumChannels, MaxChannels);
    }

    [MemberNotNullWhen(true, nameof(Device))]
    public bool HasDevice => Device is not null;

    public DeviceViewModel(bool isDefault)
    {
      _deviceId = "";
      IsDefault = isDefault;
    }

    public DeviceViewModel(MMDevice device, bool isDefault)
    {
      _device = device;
      _deviceId = device.ID;

      IsDefault = isDefault;
      NumChannels = MaxChannels;
    }
  }
}
