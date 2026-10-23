using System;
using System.ComponentModel;
using System.Windows;
using FaceTrackingApp.ViewModels;

namespace FaceTrackingApp
{
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _viewModel = new();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _viewModel;
            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await _viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Initialisierung fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnClosing(object? sender, CancelEventArgs e)
        {
            // Kamera und Threads sauber freigeben, damit die Kamera sofort für andere Apps frei ist
            _viewModel.Dispose();
        }
    }
}
