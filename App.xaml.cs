using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;

namespace NetDivert
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Bindings parse/format numbers with the user's culture (e.g. "0,5" MB/s in pt-BR).
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

            DispatcherUnhandledException += OnUnhandled;
            base.OnStartup(e);
        }

        private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(e.Exception.ToString(), "ro0tLimiter", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
