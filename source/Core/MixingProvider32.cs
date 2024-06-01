using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;

namespace FRecorder2
{
  public class SwitchingWaveProvider : IWaveProvider
  {
    private IWaveProvider? _source;
    private SilenceProvider _silenceProvider;

    private readonly object _lockObj = new();

    public WaveFormat WaveFormat { get; private set; }

    public IWaveProvider? CurrentProvider
    {
      get => _source;
      set {
        lock (_lockObj)
        {
          _source = value;

          if (value != null)
          {
            WaveFormat = value.WaveFormat;
            _silenceProvider = new(WaveFormat);
          }
        }
      }
    }

    public SwitchingWaveProvider(WaveFormat waveFormat)
    {
      WaveFormat = waveFormat;
      _silenceProvider = new(waveFormat);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
      if (CurrentProvider == null)
      {
        return _silenceProvider.Read(buffer, offset, count);
      }

      lock (_lockObj)
      {
        if (CurrentProvider == null)
        {
          return _silenceProvider.Read(buffer, offset, count);
        }
        else
        {
          return CurrentProvider.Read(buffer, offset, count);
        }
      }
    }
  }

  public class MixingProvider32 : IWaveProvider
  {
    public WaveFormat WaveFormat { get; }

    private readonly IWaveProvider _finalProvider;

    private readonly VolumeSampleProvider _volumeSampleProvider;
    private readonly MeteringSampleProvider _meteringSampleProvider;

    public float Volume
    {
      get => _volumeSampleProvider.Volume;
      set => _volumeSampleProvider.Volume = value;
    }

    public event EventHandler<StreamVolumeEventArgs>? StreamVolume
    {
      add
      {
        _meteringSampleProvider.StreamVolume += value;
      }
      remove
      {
        _meteringSampleProvider.StreamVolume -= value;
      }
    }

    public MixingProvider32(IWaveProvider source, int targetSamplingRate, int targetChannels)
    {
      WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(targetSamplingRate, targetChannels);

      var sampleStream = new WaveToSampleProvider(source);

      var chain = new SampleChainBuilder(source.ToSampleProvider());

      if (source.WaveFormat.Channels > 2 && source.WaveFormat.Channels > targetChannels)
      {
        // Multi-Channel to stereo
        chain.AddSampleProvider(x => new MultiplexingSampleProvider([x], Math.Max(targetChannels, 2)));
      }

      if (targetChannels == 1)
      {
        // Stereo to mono
        chain.AddSampleProvider(x => x.ToMono(0.5f, 0.5f));
      }

      // Downsample
      chain.AddSampleProvider(x => new WdlResamplingSampleProvider(x, WaveFormat.SampleRate));

      // Volume
      _volumeSampleProvider = new VolumeSampleProvider(chain.BuildSampleProvider());

      // Metering
      _meteringSampleProvider = new MeteringSampleProvider(_volumeSampleProvider);

      _finalProvider = _meteringSampleProvider.ToWaveProvider();
    }

    public int Read(byte[] destinationBuffer, int offset, int readingCount)
    {
      return _finalProvider.Read(destinationBuffer, offset, readingCount);
    }
  }
}
