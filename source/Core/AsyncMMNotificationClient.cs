using NAudio.CoreAudioApi.Interfaces;
using NAudio.CoreAudioApi;
using System.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace FRecorder2
{
  public interface IAsyncMMNotificationClient
  {
    event DefaultDeviceChangedHandler? DefaultDeviceChanged;
    event DeviceAddedHandler? DeviceAdded;
    event DeviceRemovedHandler? DeviceRemoved;
    event DeviceStateChangedHandler? DeviceStateChanged;
    event PropertyValueChangedHandler? PropertyValueChanged;
  }

  public delegate void DeviceStateChangedHandler(string deviceId, DeviceState newState);

  public delegate void DeviceAddedHandler(string deviceId);

  public delegate void DeviceRemovedHandler(string deviceId);

  public delegate void DefaultDeviceChangedHandler(DataFlow flow, Role role, string defaultDeviceId);

  public delegate void PropertyValueChangedHandler(string deviceId, PropertyKey key);

  public sealed class AsyncMMNotificationClient : IMMNotificationClient, IAsyncMMNotificationClient
  {
    private readonly SynchronizationContext _syncContext;


    public event DeviceStateChangedHandler? DeviceStateChanged;

    public event DeviceAddedHandler? DeviceAdded;

    public event DeviceRemovedHandler? DeviceRemoved;

    public event DefaultDeviceChangedHandler? DefaultDeviceChanged;

    public event PropertyValueChangedHandler? PropertyValueChanged;

    public AsyncMMNotificationClient()
    {
      _syncContext = SynchronizationContext.Current ?? new();
    }

    public AsyncMMNotificationClient(SynchronizationContext synchronizationContext)
    {
      _syncContext = synchronizationContext;
    }

    private void PostOnSyncContext<T>(Action<T> action, T state)
    {
      _syncContext.Post(state => action((T)state!), state);
    }

    private void PostOnSyncContext<T1, T2>(Action<T1, T2> action, T1 arg1, T2 arg2)
    {
      _syncContext.Post(state =>
      {
        var (a1, a2) = ((T1, T2))state!;
        action(a1, a2);
      }, (arg1, arg2));
    }

    private void RaiseOnSyncContext(MulticastDelegate action, params object?[] args)
    {
      _syncContext.Post(state =>
      {
        foreach (var d in action.GetInvocationList())
        {
          d.DynamicInvoke((object?[])state!);
        };
      }, args);
    }

    private void PostOnSyncContext<T1, T2, T3>(Action<T1, T2, T3> action, T1 arg1, T2 arg2, T3 arg3)
    {
      _syncContext.Post(state =>
      {
        var (a1, a2, a3) = ((T1, T2, T3))state!;
        action(a1, a2, a3);
      }, (arg1, arg2, arg3));
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
      if (DeviceStateChanged != null)
      {
        RaiseOnSyncContext(DeviceStateChanged, deviceId, newState);
      }
    }

    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId)
    {
      if (DeviceAdded != null)
      {
        RaiseOnSyncContext(DeviceAdded, pwstrDeviceId);
      }
    }

    void IMMNotificationClient.OnDeviceRemoved(string deviceId)
    {
      if (DeviceRemoved != null)
      {
        RaiseOnSyncContext(DeviceRemoved, deviceId);
      }
    }

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
      if (DefaultDeviceChanged != null)
      {
        RaiseOnSyncContext(DefaultDeviceChanged, flow, role, defaultDeviceId);
      }
    }

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
      if (PropertyValueChanged != null)
      {
        RaiseOnSyncContext(PropertyValueChanged, pwstrDeviceId, key);
      }
    }
  }
}