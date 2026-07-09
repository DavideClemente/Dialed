using Dialed.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dialed.Core.Views;

public sealed partial class IdleScreenPage : Page
{
    public IdleScreenViewModel ViewModel { get; }

    public IdleScreenPage(IdleScreenViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private void OnSetActiveClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is IdleGifViewModel gif)
            ViewModel.SetActiveCommand.Execute(gif);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is IdleGifViewModel gif)
            ViewModel.DeleteGifCommand.Execute(gif);
    }
}
