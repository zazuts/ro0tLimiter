using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using NetDivert.Core;

namespace NetDivert.UI
{
    public partial class ContactWindow : Window
    {
        public ContactWindow()
        {
            InitializeComponent();
            MailLink.Text = Brand.ContactEmail;
            GitHubLink.Text = Brand.RepositoryUrl;
            DonateImage.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/donate.png", UriKind.Absolute));
            QrImage.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/donate-qr.png", UriKind.Absolute));
        }

        private void Mail_Click(object sender, MouseButtonEventArgs e) =>
            Process.Start(new ProcessStartInfo($"mailto:{Brand.ContactEmail}") { UseShellExecute = true });

        private void GitHub_Click(object sender, MouseButtonEventArgs e) =>
            Process.Start(new ProcessStartInfo(Brand.RepositoryUrl) { UseShellExecute = true });

        private void Donate_Click(object sender, MouseButtonEventArgs e) =>
            Process.Start(new ProcessStartInfo(Brand.DonationUrl) { UseShellExecute = true });
    }
}
