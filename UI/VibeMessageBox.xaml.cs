using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace VibeDesk.UI
{
    public partial class VibeMessageBox : Window
    {
        private MessageBoxResult _result = MessageBoxResult.OK;

        public VibeMessageBox()
        {
            InitializeComponent();
            KeyDown += VibeMessageBox_KeyDown;
        }

        private void VibeMessageBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                _result = MessageBoxResult.Cancel;
                Close();
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            _result = MessageBoxResult.Cancel;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _result = MessageBoxResult.Cancel;
            Close();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            _result = MessageBoxResult.OK;
            Close();
        }

        public static MessageBoxResult Show(
            Window? owner,
            string message,
            string title = "VibeDesk",
            MessageBoxButton button = MessageBoxButton.OK,
            MessageBoxImage icon = MessageBoxImage.Information)
        {
            // Execute on UI Dispatcher if called from a background thread
            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
            {
                return Application.Current.Dispatcher.Invoke(() => Show(owner, message, title, button, icon));
            }

            var dlg = new VibeMessageBox();

            if (owner != null && owner.IsVisible)
            {
                dlg.Owner = owner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else if (Application.Current?.MainWindow != null && Application.Current.MainWindow.IsVisible)
            {
                dlg.Owner = Application.Current.MainWindow;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            dlg.TxtTitle.Text = string.IsNullOrWhiteSpace(title) ? "VibeDesk" : title;
            dlg.TxtMessage.Text = message;

            // Configure Buttons
            if (button == MessageBoxButton.OKCancel || button == MessageBoxButton.YesNo)
            {
                dlg.BtnCancel.Visibility = Visibility.Visible;
                dlg.BtnCancel.Content = button == MessageBoxButton.YesNo ? "Нет" : "Отмена";
                dlg.BtnOk.Content = button == MessageBoxButton.YesNo ? "Да" : "OK";
            }
            else
            {
                dlg.BtnCancel.Visibility = Visibility.Collapsed;
                dlg.BtnOk.Content = "Понятно";
            }

            // Configure Icon & Styling
            switch (icon)
            {
                case MessageBoxImage.Warning:
                    dlg.IconBadge.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xE5, 0xA9, 0x6A));
                    dlg.IconBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xE5, 0xA9, 0x6A));
                    dlg.IconPath.Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0xA9, 0x6A));
                    dlg.IconPath.Data = Geometry.Parse("M12 2L1 21h22L12 2zm1 14h-2v-2h2v2zm0-4h-2V7h2v5z");
                    break;

                case MessageBoxImage.Error:
                    dlg.IconBadge.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xD9, 0x53, 0x4F));
                    dlg.IconBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xD9, 0x53, 0x4F));
                    dlg.IconPath.Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x65, 0x62));
                    dlg.IconPath.Data = Geometry.Parse("M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm5 13.59L15.59 17 12 13.41 8.41 17 7 15.59 10.59 12 7 8.41 8.41 7 12 10.59 15.59 7 17 8.41 13.41 12 17 15.59z");
                    break;

                case MessageBoxImage.Information:
                default:
                    dlg.IconBadge.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xC2, 0xA8, 0x78));
                    dlg.IconBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xC2, 0xA8, 0x78));
                    dlg.IconPath.Fill = new SolidColorBrush(Color.FromRgb(0xC2, 0xA8, 0x78));
                    dlg.IconPath.Data = Geometry.Parse("M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-6h2v6zm0-8h-2V7h2v2z");
                    break;
            }

            // Play soft ambient notification chime
            VibeAudio.PlaySoftChime();

            dlg.ShowDialog();
            return dlg._result;
        }
    }
}
