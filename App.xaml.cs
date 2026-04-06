using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Schmube;

public partial class App : Application
{
    private readonly string _logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Schmube");

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("DispatcherUnhandledException", e.Exception);
        ShowErrorDialog(e.Exception, canContinue: true);
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            LogException("UnhandledException", exception);
            ShowErrorDialog(exception, canContinue: false);
            return;
        }

        LogRaw("UnhandledException", e.ExceptionObject?.ToString() ?? "Unknown exception object");
        MessageBox.Show(
            "Schmube hit a fatal error. Details were written to the local log file.",
            "Schmube Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private void ShowErrorDialog(Exception exception, bool canContinue)
    {
        var message = new StringBuilder()
            .AppendLine("Schmube hit an unexpected error.")
            .AppendLine()
            .AppendLine($"Type: {exception.GetType().Name}")
            .AppendLine($"Message: {exception.Message}")
            .AppendLine()
            .AppendLine($"Log: {Path.Combine(_logDirectory, "errors.log")}")
            .AppendLine()
            .Append(canContinue
                ? "The app will try to continue running."
                : "The app may need to close.")
            .ToString();

        MessageBox.Show(
            message,
            "Schmube Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void LogException(string source, Exception exception)
    {
        var builder = new StringBuilder()
            .AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}")
            .AppendLine(exception.ToString())
            .AppendLine(new string('-', 80));

        LogRaw(source, builder.ToString());
    }

    private void LogRaw(string source, string text)
    {
        Directory.CreateDirectory(_logDirectory);
        File.AppendAllText(
            Path.Combine(_logDirectory, "errors.log"),
            text.EndsWith(Environment.NewLine, StringComparison.Ordinal) ? text : text + Environment.NewLine,
            Encoding.UTF8);
    }
}
