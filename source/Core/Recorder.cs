using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;

using ByteSizeLib;

using CommunityToolkit.Mvvm.ComponentModel;

using NAudio.CoreAudioApi;
using NAudio.Wave;

using Serilog;

namespace FRecorder2;

public class Recorder : ObservableObject, IDisposable
{
  private CircularBufferedWaveProvider? _soundBufferedWaveProvider;
  private CircularBufferedWaveProvider? _microphoneBufferedWaveProvider;
  private WasapiLoopbackCapture? _soundWaveSource;
  private WasapiCapture? _microphoneWaveSource;
  private MixingProvider32? _microphoneMixingProvider;
  private MixingProvider32? _soundMixingProvider;
  private IWaveProvider? _finalWaveProvider;
  private WasapiOut? _output;

  private bool _disposedValue;
  private readonly object _lockObj = new();
  private bool _isRecording;

  public MMDevice? InputDevice { get; private set; }
  public MMDevice? OutputDevice { get; private set; }


  private TimeSpan _bufferDuration = TimeSpan.FromSeconds(20);
  public TimeSpan BufferDuration
  {
    get => _bufferDuration; set
    {
      if (SetProperty(ref _bufferDuration, value) && IsRecording)
      {
        if (_microphoneBufferedWaveProvider != null)
        {
          _microphoneBufferedWaveProvider.BufferDuration = value;
        }

        else if (_soundBufferedWaveProvider != null)
        {
          _soundBufferedWaveProvider.BufferDuration = value;
        }
      }
    }
  }

  public bool SeperateTracks { get; set; } = false;
  public bool PlaySilence { get; set; } = true;

  public bool IsRecording
  {
    get => _isRecording;
    private set => SetProperty(ref _isRecording, value);
  }

  public long CurrentPlaybackBufferSize => IsRecording && OutputDevice != null
    ? (long)ByteSize.FromBytes(_soundBufferedWaveProvider.BufferedBytes).KiloBytes : 0;
  public long CurrentMicBufferSize => IsRecording && InputDevice != null
    ? (long)ByteSize.FromBytes(_microphoneBufferedWaveProvider.BufferedBytes).KiloBytes : 0;

  public long PlaybackBufferLength => IsRecording && OutputDevice != null
    ? (long)ByteSize.FromBytes(_soundBufferedWaveProvider.BufferLength).KiloBytes : 0;

  public long MicrophoneBufferLength => IsRecording && InputDevice != null
    ? (long)ByteSize.FromBytes(_microphoneBufferedWaveProvider.BufferLength).KiloBytes : 0;


  private float _micVolume = 1f;
  public float MicVolume
  {
    get => _micVolume;
    set
    {
      if (SetProperty(ref _micVolume, value))
      {
        _micVolume = value;

        if (IsRecording)
        {
          _microphoneMixingProvider!.Volume = value;
        }
      }
    }
  }

  private float _soundVolume = 1f;
  public float SoundVolume
  {
    get => _soundVolume;
    set
    {
      if (SetProperty(ref _soundVolume, value))
      {
        _soundVolume = value;

        if (IsRecording)
        {
          _soundMixingProvider!.Volume = value;
        }
      }
    }
  }


  public event Action? RecordingStarted;

  public event Action? RecordingStopped;

  public event Action? NewData;

  private int GetTargetChannels(int sourceChannels, int targetChannels, int maxChannels)
  {
    if (targetChannels > 0)
    {
      return targetChannels;
    }

    if (SeperateTracks)
    {
      return sourceChannels;
    }

    return maxChannels;
  }

  public void StartRecording(MMDevice? inputDevice, MMDevice? outputDevice, int samplingRate = 32000, int micChannels = -1, int soundChannels = -1)
  {
    if (inputDevice == null && outputDevice == null)
    {
      Log.Warning("Cannot start recording with no devices");
      return;
    }

    if (IsRecording)
    {
      Log.Warning("Cannot start recording, already in progress.");
      return;
    }

    List<MixingProvider32> mixedProviders = [];
    int micSourceChannels = 0;
    int soundSourceChanngels = 0;

    Log.Debug("Starting recording with input ({inputDeviceState}), output ({outputDeviceState}), {samplingRate} kHz...",
      inputDevice?.State, outputDevice?.State, samplingRate);

    try
    {
      lock (_lockObj)
      {
        if (inputDevice != null)
        {
          if (inputDevice.State is not DeviceState.Active)
          {
            Log.Warning("Input device is not active, wont start recording.");
            return;
          }

          _microphoneWaveSource = new(inputDevice);
          _microphoneBufferedWaveProvider = new(_microphoneWaveSource.WaveFormat)
          {
            BufferDuration = BufferDuration
          };

          _microphoneWaveSource.DataAvailable += MicrophoneWaveSource_DataAvailable;

          micSourceChannels = _microphoneWaveSource.WaveFormat.Channels;
        }

        if (outputDevice != null)
        {
          if (outputDevice.State is not DeviceState.Active)
          {
            Log.Warning("Ouput device is not active, wont start recording.");
            return;
          }

          _soundWaveSource = new(outputDevice);
          _soundBufferedWaveProvider = new(_soundWaveSource.WaveFormat)
          {
            BufferDuration = BufferDuration
          };

          _soundWaveSource.DataAvailable += SoundWaveSource_DataAvailable;

          soundSourceChanngels = _soundWaveSource.WaveFormat.Channels;
        }

        // Take the max number of channels from both providers as default target when combining tracks
        var maxChannels = Math.Max(micSourceChannels, soundSourceChanngels);

        if (_microphoneBufferedWaveProvider != null)
        {
          // Initialize the mixing chain for the mic, if we record it
          _microphoneMixingProvider = new MixingProvider32(
            _microphoneBufferedWaveProvider, // Use the buffer as input provider
            samplingRate, // Resample to the target sampling rate
            GetTargetChannels(micSourceChannels, micChannels, maxChannels))
          {
            Volume = MicVolume
          };

          mixedProviders.Add(_microphoneMixingProvider);
        }

        if (_soundBufferedWaveProvider != null)
        {
          // Initialize the mixing chain for the sound, if we record it
          _soundMixingProvider = new MixingProvider32(
            _soundBufferedWaveProvider, // Use the buffer as input provider
            samplingRate, // Resample to the target sampling rate
            GetTargetChannels(soundSourceChanngels, soundChannels, maxChannels))
          {
            Volume = SoundVolume
          };

          mixedProviders.Add(_soundMixingProvider);
        }

        if (mixedProviders.Count == 1)
        {
          Log.Information("Recording only single device");

          // Only mic or sound device is available, so set this provider as final without mixing tracks
          _finalWaveProvider = mixedProviders.Single();
        }
        else
        {
          if (SeperateTracks)
          {
            // Output a track for each channel
            _finalWaveProvider = new MultiplexingWaveProvider(mixedProviders,
              mixedProviders.Sum(x => x.WaveFormat.Channels));
          }
          else
          {
            // Mix down tracks by combining channels
            _finalWaveProvider = new MixingWaveProvider32(mixedProviders);
          }
        }

        if (outputDevice != null && PlaySilence)
        {
          // Play silence, if required, to keep WASAPI loopback capture data in sync
          var silence = new SilenceProvider(_soundWaveSource!.WaveFormat);

          _output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, false, 3);
          _output.Init(silence);
          _output.Play();
        }

        _microphoneWaveSource?.StartRecording();
        _soundWaveSource?.StartRecording();

        InputDevice = inputDevice;
        OutputDevice = outputDevice;
        IsRecording = true;
      }

      RecordingStarted?.Invoke();

      Log.Information("Recording started.");
    }
    catch (Exception ex)
    {
      Log.Error(ex, "Error while starting recording.");

      InputDevice = null;
      OutputDevice = null;

      // Try to stop recording to dispose of everything again
      StopRecording();
    }
  }

  private void SoundWaveSource_DataAvailable(object? sender, WaveInEventArgs e)
  {
    Log.Verbose("Mic wave source data available ({numBytes} bytes)", e.BytesRecorded);

    _soundBufferedWaveProvider?.AddSamples(e.Buffer, 0, e.BytesRecorded);

    NewData?.Invoke();
  }

  private void MicrophoneWaveSource_DataAvailable(object? sender, WaveInEventArgs e)
  {
    Log.Verbose("Mic wave source data available ({numBytes} byte)", e.BytesRecorded);

    _microphoneBufferedWaveProvider?.AddSamples(e.Buffer, 0, e.BytesRecorded);

    NewData?.Invoke();
  }

  public void StopRecording()
  {
    if (!IsRecording)
    {
      return;
    }

    Log.Debug("Stopping recording...");

    lock (_lockObj)
    {
      IsRecording = false;

      if (_microphoneWaveSource != null)
      {
        _microphoneWaveSource.StopRecording();
        _microphoneWaveSource.DataAvailable -= MicrophoneWaveSource_DataAvailable;
        _microphoneWaveSource.Dispose();
      }

      if (_soundWaveSource != null)
      {
        _soundWaveSource.StopRecording();
        _soundWaveSource.DataAvailable -= SoundWaveSource_DataAvailable;
        _soundWaveSource.Dispose();
      }

      _microphoneWaveSource = null;
      _microphoneBufferedWaveProvider = null;
      _microphoneMixingProvider = null;

      _soundMixingProvider = null;
      _soundWaveSource = null;
      _soundBufferedWaveProvider = null;

      _finalWaveProvider = null;

      if (_output != null)
      {
        _output.Stop();
        _output.PlaybackStopped += (s, e) =>
        {
          _output.Dispose();
          _output = null;
        };
      }

      InputDevice = null;
      OutputDevice = null;
    }

    RecordingStopped?.Invoke();

    Log.Information("Recording stopped.");
  }

  public async Task<TimeSpan?> Save(string fileName)
  {
    if (!IsRecording)
    {
      return null;
    }

    WaveFileWriter writer;
    byte[] buffer;
    int bytesRead;
    WaveFormat waveFormat;

    lock (_lockObj)
    {
      if (_finalWaveProvider == null)
      {
        return null;
      }

      waveFormat = _finalWaveProvider.WaveFormat;

      writer = new WaveFileWriter(fileName, waveFormat);

      int length = (int)Math.Ceiling(waveFormat.AverageBytesPerSecond * BufferDuration.TotalSeconds);

      buffer = new byte[length];
      bytesRead = _finalWaveProvider.Read(buffer, 0, length);

      _microphoneBufferedWaveProvider?.ClearBuffer();
      _soundBufferedWaveProvider?.ClearBuffer();
    }

    Log.Debug("Saving {bytes} kB of audio", ByteSize.FromBytes(bytesRead).KiloBytes);

    await writer.WriteAsync(buffer.AsMemory(0, bytesRead));
    await writer.FlushAsync();
    await writer.DisposeAsync();

    Log.Information("Saved {kilobytes} kB of audio to '{fileName}'.",
      ByteSize.FromBytes(bytesRead).KiloBytes, fileName);

    return TimeSpan.FromSeconds((double)bytesRead / waveFormat.AverageBytesPerSecond);
  }

  protected virtual void Dispose(bool disposing)
  {
    if (!_disposedValue)
    {
      if (disposing)
      {
        // TODO: dispose managed state (managed objects)
        StopRecording();
      }

      // TODO: free unmanaged resources (unmanaged objects) and override finalizer
      // TODO: set large fields to null
      _disposedValue = true;
    }
  }

  public void Dispose()
  {
    // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
    Dispose(disposing: true);
    GC.SuppressFinalize(this);
  }
}