using Serilog;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.IO.IsolatedStorage;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.ComponentModel;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using System.Security.Permissions;

namespace FRecorder2
{
  /// <summary>
  /// Interaction logic for App.xaml
  /// </summary>
  public partial class App : Application
  {
    public static readonly string SettingsFileName = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "FRecorder", "settings.json");

    public static Settings Settings { get; private set; } = new();

    public App()
    {

    }

    protected override async void OnExit(ExitEventArgs e)
    {
      base.OnExit(e);

      await Settings.SaveAsync();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
      var settings = await Settings.LoadFromFile();
      if (settings != null)
      {
        Settings = settings;
      }

      App.Current.MainWindow = new MainWindow();
      App.Current.MainWindow.Show();

      base.OnStartup(e);
    }
  }

  
}
