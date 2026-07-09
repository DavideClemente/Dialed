using Dialed.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Dialed.Core.Views;

public sealed partial class OutputPage : Page
{
    public OutputViewModel ViewModel { get; }

    public OutputPage(OutputViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
}
