using System.ComponentModel;
using System.Windows;
using HapticBridge.UI.ViewModels;

namespace HapticBridge.UI.Views;

public partial class NativeTestWindow : Window
{
    private readonly NativeTestViewModel _model;
    private bool _closeAfterStop;

    public NativeTestWindow(NativeTestViewModel model, bool showController = true)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        if (!showController)
        {
            Title = TestHeading.Text = "VB-CABLE loopback test";
            TestDescription.Text = "The audio test runs for up to 60 seconds.";
            ControllerControls.Visibility = Visibility.Collapsed;
        }
        Loaded += RefreshOnLoad;
        Closing += StopBeforeClosing;
        model.PropertyChanged += CloseAfterStopping;
        Closed += (_, _) => model.PropertyChanged -= CloseAfterStopping;
    }

    private async void RefreshOnLoad(object sender, RoutedEventArgs args)
    {
        Loaded -= RefreshOnLoad;
        await _model.RefreshEndpointsAsync();
    }

    private void StopBeforeClosing(object? sender, CancelEventArgs args)
    {
        if (!_model.AudioRunning) return;
        args.Cancel = true;
        _closeAfterStop = true;
        _model.StopAudio();
    }

    private void CloseAfterStopping(object? sender, PropertyChangedEventArgs args)
    {
        if (_closeAfterStop && !_model.AudioRunning)
        {
            _closeAfterStop = false;
            Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private void Close_Click(object sender, RoutedEventArgs args) => Close();
}
