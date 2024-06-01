using NAudio.Wave;
using System;

namespace FRecorder2
{
  public class SampleChainBuilder
  {
    private ISampleProvider _current;

    public SampleChainBuilder(ISampleProvider current)
    {
      _current = current;
    }

    public void AddSampleProvider(Func<ISampleProvider, ISampleProvider> func)
    {
      _current = func(_current);
    }

    public ISampleProvider BuildSampleProvider()
    {
      return _current;
    }
  }
}
