using Microsoft.Maui.Controls;

namespace P2PChat;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        //MainPage = new MainPage(); // AppShell();
        try
        {
            MainPage = new AppShell();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            throw;
        }
    }
}