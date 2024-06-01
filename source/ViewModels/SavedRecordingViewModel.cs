using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;

namespace FRecorder2
{
  internal partial class SavedRecordingViewModel : ObservableObject
  {
    [ObservableProperty]
    private string _fileName = "";

    public FileInfo FileInfo { get; }

    public SavedRecordingViewModel(FileInfo fileInfo)
    {
      FileInfo = fileInfo;
      _fileName = fileInfo.Name;
    }

    [ObservableProperty]
    private int _duration;
  }
}
