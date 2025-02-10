using CommunityToolkit.Mvvm.ComponentModel;

namespace AvaloniaApplication.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        [ObservableProperty]
        private string greeting = "Welcome to Avalonia!";
    }
}
