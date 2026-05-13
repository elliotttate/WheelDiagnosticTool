using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WheelDiagnosticTool.Services;

namespace WheelDiagnosticTool.Views;

public sealed partial class WelcomePage : Page
{
    public WelcomePage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (App.MainAppWindow is MainWindow mw)
        {
            mw.SetStepLabel($"Step 1 of {WizardFlow.Steps.Count}");
            mw.SetStatus("Ready");
        }
    }

    private void OnStartClicked(object sender, RoutedEventArgs e)
    {
        var next = WizardFlow.Next(typeof(WelcomePage), null);
        if (next != null && App.MainAppWindow is MainWindow mw)
            mw.NavigateTo(next.PageType);
    }
}
