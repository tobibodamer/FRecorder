using NAudio.Wave;
using System;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.ListView;
using System.Threading;

namespace FRecorder2
{
  public class CircularBufferedWaveProvider : IWaveProvider
  {
    private bool _isFull = false;
    private int _pos = 0;
    private byte[] _buffer = Array.Empty<byte>();

    private int _bufferLength = 0;

    //
    // Zusammenfassung:
    //     Buffer length in bytes
    public int BufferLength
    {
      get => _bufferLength;
      set
      {
        //var oldBytes = GetBytes();
        //var newBuffer = new byte[BufferLength];
        //var currentLength = oldBytes.Length;
        //var lengthToKeep = Math.Min(currentLength, BufferLength);
        //var start = currentLength - lengthToKeep;

        //Array.Copy(oldBytes, start, newBuffer, 0, lengthToKeep);

        _bufferLength = value;
        //_buffer = newBuffer;
        //_pos = lengthToKeep % BufferLength;
        //_isFull |= _pos == 0;
      }
    }

    public bool ReadFully { get; set; } = true;

    //
    // Zusammenfassung:
    //     Buffer duration
    public TimeSpan BufferDuration
    {
      get
      {
        return TimeSpan.FromSeconds((double)BufferLength / (double)WaveFormat.AverageBytesPerSecond);
      }
      set
      {
        BufferLength = (int)(value.TotalSeconds * (double)WaveFormat.AverageBytesPerSecond);
      }
    }

    //
    // Zusammenfassung:
    //     The number of buffered bytes
    public int BufferedBytes
    {
      get
      {
        return _isFull ? _buffer.Length : _pos;
      }
    }

    public WaveFormat WaveFormat { get; }

    public CircularBufferedWaveProvider(WaveFormat waveFormat)
    {
      WaveFormat = waveFormat;
    }

    public byte[] GetBytesToSave()
    {
      int length = _isFull ? _buffer.Length : _pos;
      var bytesToSave = new byte[length];
      int byteCountToEnd = _isFull ? (_buffer.Length - _pos) : 0;
      if (byteCountToEnd > 0)
      {
        // bytes from the current position to the end
        Array.Copy(_buffer, _pos, bytesToSave, 0, byteCountToEnd);
      }
      if (_pos > 0)
      {
        // bytes from the start to the current position
        Array.Copy(_buffer, 0, bytesToSave, byteCountToEnd, _pos);
      }
      return bytesToSave;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
      // Calculate the number of bytes available to read
      int availableBytes = _isFull ? _buffer.Length : _pos;
      int bytesToRead = Math.Min(count, availableBytes);

      if (bytesToRead == 0)
      {
        return 0; // No bytes available to read
      }

      // Calculate bytes to read from the current position to the end of the buffer
      int bytesToEnd = _isFull ? Math.Min(bytesToRead, _buffer.Length - _pos) : 0;

      if (bytesToEnd > 0)
      {
        // Copy bytes from the current position to the end of the buffer
        Array.Copy(_buffer, _pos, buffer, offset, bytesToEnd);
      }

      // Calculate remaining bytes to read after wrapping around
      int remainingBytes = bytesToRead - bytesToEnd;
      if (remainingBytes > 0)
      {
        Array.Copy(_buffer, 0, buffer, offset + bytesToEnd, remainingBytes);
      }

      return bytesToRead;
    }

    public byte[] GetBytes()
    {
      int length = _isFull ? _buffer.Length : _pos;
      var bytesToSave = new byte[length];
      int byteCountToEnd = _isFull ? (_buffer.Length - _pos) : 0;
      if (byteCountToEnd > 0)
      {
        // bytes from the current position to the end
        Array.Copy(_buffer, _pos, bytesToSave, 0, byteCountToEnd);
      }
      if (_pos > 0)
      {
        // bytes from the start to the current position
        Array.Copy(_buffer, 0, bytesToSave, byteCountToEnd, _pos);
      }
      return bytesToSave;
    }

    //
    // Zusammenfassung:
    //     Adds samples. Takes a copy of buffer, so that buffer can be reused if necessary
    public void AddSamples(byte[] buffer, int offset, int count)
    {
      if (_buffer.Length < BufferLength)
      {
        Array.Resize(ref _buffer, BufferLength);
      }

      for (int i = offset; i < count; ++i)
      {
        // save the data
        _buffer[_pos] = buffer[i];
        // move the current position (advances by 1 OR resets to zero if the length of the buffer was reached)
        _pos = (_pos + 1) % _buffer.Length;
        // flag if the buffer is full (will only set it from false to true the first time that it reaches the full length of the buffer)
        _isFull |= _pos == 0;
      }
    }

    public void ClearBuffer()
    {
      Array.Clear(_buffer);
      _pos = 0;
      _isFull = false;
    }
  }
}
