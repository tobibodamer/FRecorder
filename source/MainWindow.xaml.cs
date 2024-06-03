using Serilog;
using Serilog.Sinks.RichTextBox;
using Serilog.Sinks.RichTextBox.Themes;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace FRecorder2
{
  /// <summary>
  /// Interaction logic for MainWindow.xaml
  /// </summary>
  public partial class MainWindow : Window
  {
    public MainWindow()
    {
      InitializeComponent();

      // Initialize logging
      Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.RichTextBox(LogTraceTextBox, 
          outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}, {ThreadId}] {Message:lj}{NewLine}{Exception}", theme: RichTextBoxTheme.None)
        .WriteTo.File(App.LogFileName)
        .Enrich.WithThreadId()
        .CreateLogger();

      Log.Information("Logging initialized.");

      MainViewModel mainViewModel = new();

      DataContext = mainViewModel;

      _ = mainViewModel.InitializeDevices();
      mainViewModel.RestoreSettings();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
      ((MainViewModel)DataContext).SaveToSettings();

      base.OnClosing(e);
    }
  }
}
