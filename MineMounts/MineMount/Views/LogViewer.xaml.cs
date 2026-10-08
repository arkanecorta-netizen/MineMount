using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MineMount.Views;

/// <summary>
/// Visor de registros: launcher (minemount.log) o juego (logs/latest.log
/// de la serie). Solo lectura, con copiar y abrir carpeta.
/// </summary>
public partial class LogViewer : Window
{
    private readonly string _gameLogPath;
    private const int MaxChars = 200_000;

    public LogViewer(string? gameLogPath)
    {
        InitializeComponent();
        _gameLogPath = gameLogPath ?? string.Empty;

        SourceBox.Items.Add(Services.Loc.T("S.Log.Launcher"));
        SourceBox.Items.Add(Services.Loc.T("S.Log.Game"));
        SourceBox.SelectedIndex = string.IsNullOrWhiteSpace(_gameLogPath) ? 0 : 1;

        LoadLog();
    }

    private string CurrentPath()
    {
        if (SourceBox.SelectedIndex == 1 && !string.IsNullOrWhiteSpace(_gameLogPath))
            return _gameLogPath;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MineMount", "minemount.log");
    }

    private void LoadLog()
    {
        try
        {
            var path = CurrentPath();
            PathLabel.Text = path;

            if (!File.Exists(path))
            {
                LogBox.Text = string.Empty;
                return;
            }

            string text;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                text = reader.ReadToEnd();
            }

            if (text.Length > MaxChars)
                text = "…[recortado]\n" + text[^MaxChars..];

            LogBox.Text = text;
            LogScroller.ScrollToBottom();
        }
        catch (Exception ex)
        {
            LogBox.Text = ex.Message;
        }
    }

    private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadLog();

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadLog();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(LogBox.Text))
                Clipboard.SetText(LogBox.Text);
        }
        catch
        {
            // Portapapeles ocupado
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(CurrentPath());
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
        }
        catch
        {
            // Sin acceso a la carpeta
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
