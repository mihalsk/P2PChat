using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Shared;
using System.Collections.ObjectModel;
using System.Net;

namespace P2PChat;

public partial class MainPage : ContentPage
{
    private Client client = new Client();
    private List<ChatPage> chatPages = new List<ChatPage>();
    private ObservableCollection<ClientInfo> clients = new ObservableCollection<ClientInfo>();

    public MainPage()
    {
        InitializeComponent();
        lstClients.ItemsSource = clients;

        client.OnServerConnect += (s, e) => MainThread.BeginInvokeOnMainThread(() =>
        {
            btnConnect.Text = "Отключиться";
            chkUPnP.IsEnabled = false;
        });

        // Обработчик отключения от сервера
        client.OnServerDisconnect += (s, e) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            btnConnect.Text = "Подключиться";
            clients.Clear();
            chkUPnP.IsEnabled = true;
            // Закрыть все открытые страницы чата
            await Navigation.PopToRootAsync();
            chatPages.Clear();
        });

        client.OnResultsUpdate += (s, msg) => MainThread.BeginInvokeOnMainThread(() =>
        {
            txtResults.Text += msg + "\n";
            // Прокрутка вниз – Editor не имеет ScrollToEnd, но можно установить курсор в конец
            // и использовать ScrollView, в который обёрнут Editor.
        });

        client.OnClientAdded += (s, ci) => MainThread.BeginInvokeOnMainThread(() =>
        {
            clients.Add(ci);
        });

        client.OnClientUpdated += (s, ci) => MainThread.BeginInvokeOnMainThread(() =>
        {
            var existing = clients.FirstOrDefault(c => c.ID == ci.ID);
            if (existing != null)
            {
                existing.Update(ci);
                RefreshDetails();
            }
        });

        // Обработчик удаления клиента
        client.OnClientRemoved += (s, ci) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            var item = clients.FirstOrDefault(c => c.ID == ci.ID);
            if (item != null) clients.Remove(item);

            var page = chatPages.FirstOrDefault(p => p.id == ci.ID);
            if (page != null)
            {
                // Если страница всё ещё в стеке навигации – удаляем её
                if (Navigation.NavigationStack.Contains(page))
                    Navigation.RemovePage(page);
                chatPages.Remove(page);
            }
            RefreshDetails();
        });

        client.OnClientConnection += (s, ep) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            var ci = (ClientInfo)s;
            await GetOrCreateChatPage(ci, ep);
        });

        client.OnMessageReceived += (s, args) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            var ep = (IPEndPoint)s;
            var page = await GetOrCreateChatPage(args.clientInfo, args.EstablishedEP);
            page.ReceiveMessage(args.message);
        });
    }

    private void OnConnectClicked(object sender, EventArgs e) => client.ConnectOrDisconnect();

    private void OnUPnPChecked(object sender, CheckedChangedEventArgs e) => client.UPnPEnabled = e.Value;

    private void OnClearClicked(object sender, EventArgs e) => txtResults.Text = string.Empty;

    private void OnClientSelected(object sender, SelectedItemChangedEventArgs e)
    {
        RefreshDetails();
        btnConnectClient.IsEnabled = e.SelectedItem is ClientInfo ci && ci.ID != client.LocalClientInfo.ID;
    }

    private void OnConnectClientClicked(object sender, EventArgs e)
    {
        if (lstClients.SelectedItem is ClientInfo ci)
            client.ConnectToClient(ci);
    }

    private void RefreshDetails()
    {
        if (lstClients.SelectedItem is ClientInfo ci)
        {
            lblName.Text = $"Имя:{ci.Name}";
            lblUPnP.Text = $"UPnP:{ci.UPnPEnabled}";
            lblExtEP.Text = $"ExtEP:{ci.ExternalEndpoint?.ToString() ?? "Нет"}";
            lblIntEP.Text = $"IntEP:{ci.InternalEndpoint?.ToString() ?? "Нет"}";
            lblConType.Text = $"CT:{ci.ConnectionType}";
            lblIPs.Text = $"IPs:{string.Join(",", ci.InternalAddresses)}";
        }
        else
        {
            lblName.Text = "Имя:";
            lblUPnP.Text = "UPnP:";
            lblExtEP.Text = "ExtEP:";
            lblIntEP.Text = "IntEP:";
            lblConType.Text = "CT:";
            lblIPs.Text = "IPs:";
        }
    }

    private void OnUPnPChecked(object sender, TappedEventArgs e)
    {
        chkUPnP.IsChecked = !chkUPnP.IsChecked;
    }

    private async Task<ChatPage> GetOrCreateChatPage(ClientInfo ci, IPEndPoint ep)
    {
        // Ищем существующую страницу для этого клиента
        var existingPage = chatPages.FirstOrDefault(p => p.id == ci.ID);
        if (existingPage != null)
        {
            // Если страница уже есть, но не в стеке навигации – добавим её
            if (!Navigation.NavigationStack.Contains(existingPage))
            {
                // Удаляем старую страницу из навигации (если она где-то застряла)
                Navigation.RemovePage(existingPage);
                await Navigation.PushAsync(existingPage);
            }
            else
            {
                // Если она уже открыта, но не активна – переключимся на неё
                // (можно также использовать PopToRoot + Push, но это сложнее)
                // Просто вернём существующую, чтобы вызвать ReceiveMessage
            }
            return existingPage;
        }

        // Создаём новую страницу
        var chatPage = new ChatPage(client, ci.Name, ep, ci.ID);
        chatPages.Add(chatPage);
        chatPage.Disappearing += (o, args) => chatPages.Remove(chatPage);
        await Navigation.PushAsync(chatPage);
        return chatPage;
    }
}