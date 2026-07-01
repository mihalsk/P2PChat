using Microsoft.Maui.Controls;
using System.Collections.ObjectModel;
using Shared;

namespace P2PChat;

public partial class ChatPage : ContentPage
{
    private Client client;
    private string remoteName;
    public System.Net.IPEndPoint remoteEP;
    public long id;

    public ObservableCollection<string> Messages { get; } = new ObservableCollection<string>();

    public ChatPage(Client client, string remoteName, System.Net.IPEndPoint remoteEP, long id)
    {
        InitializeComponent();
        BindingContext = this;
        this.client = client;
        this.remoteName = remoteName;
        this.remoteEP = remoteEP;
        this.id = id;
        Title = $"{remoteName} ({remoteEP})";
        cvConversation.ItemsSource = Messages;
    }

    public void ReceiveMessage(Message msg)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Messages.Add($"{msg.From}: {msg.Content}");
            // Прокрутка вниз – CollectionView не имеет встроенного ScrollToEnd,
            // можно использовать метод ScrollTo с последним элементом
            if (Messages.Count > 0)
                cvConversation.ScrollTo(Messages.LastOrDefault(), position: ScrollToPosition.End);
        });
    }

    private void SendMessage()
    {
        if (string.IsNullOrWhiteSpace(txtMessage.Text))
            return;

        var msg = new Message(client.LocalClientInfo.Name, remoteName, txtMessage.Text);
        client.SendMessageUDP(msg, remoteEP);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Messages.Add($"{client.LocalClientInfo.Name}: {txtMessage.Text}");
            cvConversation.ScrollTo(Messages.LastOrDefault(), ScrollToPosition.End);
            txtMessage.Text = string.Empty;
        });
    }

    private void OnMessageCompleted(object sender, EventArgs e) => SendMessage();
    private void OnSendClicked(object sender, EventArgs e) => SendMessage();

    // Закрытие (аналог Window.Closed) – можно использовать OnDisappearing
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // При необходимости отправляем уведомление о закрытии, но здесь оставляем как есть
    }
}