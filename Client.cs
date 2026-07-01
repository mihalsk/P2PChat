// Written by Benjamin Watkins 2015
// watkins.ben@gmail.com

using Shared;
using SharpOpenNat;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace P2PChat
{
    public class Client
    {
        public IPEndPoint ServerEndpoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 50); //P2PServer ip

        private IPAddress InternetAccessAdapter;

        private TcpClient TCPClient = new TcpClient();
        private UdpClient UDPClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));

        //private UPnPNATClass UPnPNAT = new UPnPNATClass();
        //private IStaticPortMappingCollection UPnPMappings;
        private INatDevice _device;
        private Mapping _mapping;

        public ClientInfo LocalClientInfo = new ClientInfo();
        private List<ClientInfo> Clients = new List<ClientInfo>();
        private List<Ack> AckResponces = new List<Ack>();
        private List<int> UPnPPorts = new List<int>();

        private Thread ThreadTCPListen;
        private Thread ThreadUDPListen;

        public event EventHandler<string> OnResultsUpdate;
        public event EventHandler<ClientInfo> OnClientAdded;
        public event EventHandler<ClientInfo> OnClientUpdated;
        public event EventHandler<ClientInfo> OnClientRemoved;
        public event EventHandler OnServerConnect;
        public event EventHandler OnServerDisconnect;
        public event EventHandler<IPEndPoint> OnClientConnection;
        public event EventHandler<MessageReceivedEventArgs> OnMessageReceived;

        public bool UPnPEnabled { get; set; }

        private bool _TCPListen = false;
        public bool TCPListen
        {
            get { return _TCPListen; }
            set
            {
                _TCPListen = value;
                if (value)
                    ListenTCP();
            }
        }

        private bool _UDPListen = false;
        public bool UDPListen
        {
            get { return _UDPListen; }
            set
            {
                _UDPListen = value;
                if (value)
                    ListenUDP();
            }
        }

        public Client()
        {
            try
            {
                UDPClient.AllowNatTraversal(true);
            }
            catch
            {
                Debug.Print("**UDPClient.AllowNatTraversal(true)");
            }
            try
            {
                UDPClient.Client.SetIPProtectionLevel(IPProtectionLevel.Unrestricted);
            }
            catch
            {
                Debug.Print("**UDPClient.Client.SetIPProtectionLevel(IPProtectionLevel.Unrestricted)");
            }
            try
            {
                UDPClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            }
            catch
            {
                Debug.Print("**UDPClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);");
            }
            

            LocalClientInfo.Name = System.Environment.MachineName;
            LocalClientInfo.ConnectionType = ConnectionTypes.Unknown;
            LocalClientInfo.ID = DateTime.Now.Ticks;

            var IPs = Dns.GetHostEntry(Dns.GetHostName()).AddressList.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork);

            foreach (var IP in IPs)
                LocalClientInfo.InternalAddresses.Add(IP);
        }

        public async void ConnectOrDisconnect()
        {
            if (TCPClient.Connected)
            {
                TCPClient.Client.Disconnect(true);

                UDPListen = false;
                TCPListen = false;
                Clients.Clear();

                if (UPnPEnabled)
                    ClearUpUPnP();

                if (OnServerDisconnect != null)
                    OnServerDisconnect.Invoke(this, new EventArgs());

                if (OnResultsUpdate != null)
                    OnResultsUpdate.Invoke(this, "Disconnected.");
            }
            else
            {
                try
                {
                    InternetAccessAdapter = GetAdapterWithInternetAccess();

                    if (OnResultsUpdate != null)
                        OnResultsUpdate.Invoke(this, "Adapter with Internet Access: " + InternetAccessAdapter);

                    TCPClient = new TcpClient();
                    TCPClient.Client.Connect(ServerEndpoint);

                    UDPListen = true;
                    TCPListen = true;

                    SendMessageUDP(LocalClientInfo.Simplified(), ServerEndpoint);
                    LocalClientInfo.InternalEndpoint = (IPEndPoint)UDPClient.Client.LocalEndPoint;

                    if (UPnPEnabled)
                    {
                        //UPnPMappings = UPnPNAT.StaticPortMappingCollection;
                        ClearUpUPnP();

                        if (LocalClientInfo.InternalEndpoint != null)
                        {
                            if (OnResultsUpdate != null)
                                OnResultsUpdate.Invoke(this, "UDP Listening on Port " + LocalClientInfo.InternalEndpoint.Port);

                            if (await AttemptUPnP(LocalClientInfo.InternalEndpoint.Port))
                            {
                                if (OnResultsUpdate != null)
                                    OnResultsUpdate.Invoke(this, "UPnP Map Added");

                                LocalClientInfo.UPnPEnabled = true;
                            }
                            else
                            {
                                if (OnResultsUpdate != null)
                                    OnResultsUpdate.Invoke(this, "UPnP Mapping Not Possible");
                            }
                        }
                    }

                    Thread.Sleep(500);
                    await SendMessageTCPWithLength(LocalClientInfo);

                    Thread KeepAlive = new Thread(new ThreadStart(async delegate
                    {
                        while (TCPClient.Connected)
                        {
                            Thread.Sleep(5000);
                            await SendMessageTCPWithLength(new KeepAlive());
                        }
                    }));

                    KeepAlive.IsBackground = true;
                    KeepAlive.Start();

                    if (OnServerConnect != null)
                        OnServerConnect.Invoke(this, new EventArgs());

                }
                catch (Exception ex)
                {
                    if (OnResultsUpdate != null)
                        OnResultsUpdate.Invoke(this, "Error when connecting " + ex.Message);
                }
            }
        }

        private async Task<bool> AttemptUPnP(int Port)
        {
            try
            {
                // 1. Обнаруживаем UPnP устройство (роутер)
                INatDiscoverer discoverer = OpenNat.Discoverer;
                //var discoverer = new INatDiscoverer();
                // Используем CancellationTokenSource для ограничения времени поиска (например, 5 секунд)
                var cts = new CancellationTokenSource(5000);
                INatDevice _device = await discoverer.DiscoverDeviceAsync(PortMapper.Upnp, cts.Token);
                
                // 2. Создаем правило проброса порта
                // Протокол: UDP, Внутренний порт: Port, Внешний порт: Port, 
                // Описание: "P2P Chat"
                _mapping = new Mapping(Protocol.Udp, Port, Port, "P2P Chat");

                // 3. Применяем правило на устройстве
                await _device.CreatePortMapAsync(_mapping);
                
                return true;
            }
            catch (NatDeviceNotFoundException)
            {
                // Устройство не найдено (роутер не поддерживает UPnP)
                return false;
            }
            catch (Exception ex)
            {
                // Обработка других ошибок
                Console.WriteLine($"UPnP Error: {ex.Message}");
                return false;
            }
        }

        public async void ClearUpUPnP()
        {
            if (_mapping != null && _device != null)
            {
                await _device.DeletePortMapAsync(_mapping);
            }
        }

        //private IPAddress GetAdapterWithInternetAccess__()
        //{
        //    try
        //    {
        //        // 1. Пробуем через Dns (быстро, но может вернуть 127.0.0.1 на Android)
        //        var hostEntry = Dns.GetHostEntry(Dns.GetHostName());
        //        foreach (var ip in hostEntry.AddressList)
        //        {
        //            if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
        //                return ip;
        //        }
        //    }
        //    catch { }

        //    // 2. Пробуем через NetworkInterface (более точный)
        //    try
        //    {
        //        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        //        foreach (var ni in interfaces)
        //        {
        //            if (ni.OperationalStatus != OperationalStatus.Up) continue;
        //            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
        //                ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

        //            var properties = ni.GetIPProperties();
        //            // Не отсеиваем интерфейсы без шлюза – на мобильных устройствах они могут быть основными
        //            foreach (var ip in properties.UnicastAddresses)
        //            {
        //                if (ip.Address.AddressFamily == AddressFamily.InterNetwork &&
        //                    !IPAddress.IsLoopback(ip.Address) &&
        //                    !IsLinkLocal(ip.Address))
        //                {
        //                    return ip.Address;
        //                }
        //            }
        //        }
        //    }
        //    catch { }

        //    // 3. Запасной вариант – берём любой не-loopback IPv4
        //    try
        //    {
        //        var hostEntry = Dns.GetHostEntry(Dns.GetHostName());
        //        foreach (var ip in hostEntry.AddressList)
        //        {
        //            if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
        //                return ip;
        //        }
        //    }
        //    catch { }

        //    return IPAddress.Loopback;
        //}

        //private bool IsLinkLocal(IPAddress ip)
        //{
        //    byte[] bytes = ip.GetAddressBytes();
        //    return bytes[0] == 169 && bytes[1] == 254;
        //}

        private IPAddress GetAdapterWithInternetAccess()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var ni in interfaces)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    var props = ni.GetIPProperties();
                    // Предпочитаем интерфейсы со шлюзом (выход в интернет)
                    if (props.GatewayAddresses.Count == 0) continue;

                    foreach (var ip in props.UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(ip.Address))
                            return ip.Address;
                    }
                }

                // Запасной вариант – любой не-loopback IPv4
                foreach (var ni in interfaces)
                {
                    foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(ip.Address))
                            return ip.Address;
                    }
                }
            }
            catch { }
            return IPAddress.Loopback;
        }
        //private IPAddress GetAdapterWithInternetAccess()
        //{
        //    try
        //    {
        //        var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
        //        foreach (var ip in host.AddressList)
        //        {
        //            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
        //                !IPAddress.IsLoopback(ip))
        //            {
        //                return ip;
        //            }
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        System.Diagnostics.Debug.WriteLine($"GetAdapterWithInternetAccess: {ex.Message}");
        //    }
        //    return IPAddress.Loopback;
        //}
        //private IPAddress GetAdapterWithInternetAccess()
        //{
        //    try
        //    {
        //        var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
        //        foreach (var ni in interfaces)
        //        {
        //            // Пропускаем неактивные, loopback и туннели
        //            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
        //                continue;
        //            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback ||
        //                ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
        //                continue;

        //            var properties = ni.GetIPProperties();
        //            // Ищем интерфейс со шлюзом по умолчанию (признак выхода в интернет)
        //            if (properties.GatewayAddresses.Count == 0)
        //                continue;

        //            // Берём первый IPv4-адрес
        //            foreach (var ip in properties.UnicastAddresses)
        //            {
        //                if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        //                {
        //                    return ip.Address;
        //                }
        //            }
        //        }

        //        // Запасной вариант: любой не-loopback IPv4
        //        foreach (var ni in interfaces)
        //        {
        //            foreach (var ip in ni.GetIPProperties().UnicastAddresses)
        //            {
        //                if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
        //                    !IPAddress.IsLoopback(ip.Address))
        //                {
        //                    return ip.Address;
        //                }
        //            }
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        System.Diagnostics.Debug.WriteLine($"GetAdapterWithInternetAccess: {ex.Message}");
        //    }
        //    return IPAddress.Loopback;
        //}
        public async void SendMessageTCP(IP2PBase Item)
        {
            if (TCPClient.Connected)
            {
                byte[] Data = await Item.ToByteArray();

                try
                {
                    NetworkStream NetStream = TCPClient.GetStream();
                    NetStream.Write(Data, 0, Data.Length);
                }
                catch (Exception e)
                {
                    if (OnResultsUpdate != null)
                        OnResultsUpdate.Invoke(this, "Error on TCP Send: " + e.Message);
                }
            }
        }
        private async Task SendMessageTCPWithLength(IP2PBase Item)
        {
            if (!TCPClient.Connected) return;

            byte[] data = await Item.ToByteArray();
            // Заголовок: длина сообщения (4 байта, big-endian)
            byte[] lengthBytes = BitConverter.GetBytes(data.Length);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(lengthBytes);

            byte[] sendBuffer = new byte[4 + data.Length];
            Buffer.BlockCopy(lengthBytes, 0, sendBuffer, 0, 4);
            Buffer.BlockCopy(data, 0, sendBuffer, 4, data.Length);

            try
            {
                NetworkStream stream = TCPClient.GetStream();
                await stream.WriteAsync(sendBuffer, 0, sendBuffer.Length);
            }
            catch (Exception ex)
            {
                OnResultsUpdate?.Invoke(this, $"Error on TCP Send: {ex.Message}");
            }
        }
        public async void SendMessageUDP(IP2PBase Item, IPEndPoint EP)
        {
            Item.ID = LocalClientInfo.ID;

            byte[] data = await Item.ToByteArray();

            try
            {
                if (data != null)
                    UDPClient.Send(data, data.Length, EP);
            }
            catch (Exception e)
            {
                if (OnResultsUpdate != null)
                    OnResultsUpdate.Invoke(this, "Error on UDP Send: " + e.Message);
            }
        }

        private async void ListenUDP()
        {
            ThreadUDPListen = new Thread(new ThreadStart(async delegate
            {
                while (UDPListen)
                {
                    try
                    {
                        IPEndPoint EP = LocalClientInfo.InternalEndpoint;

                        if (EP != null)
                        {
                            byte[] ReceivedBytes = UDPClient.Receive(ref EP);
                            IP2PBase Item = await ReceivedBytes.ToP2PBase();
                            ProcessItem(Item, EP);
                        }
                    }
                    catch (Exception e)
                    {
                        if (OnResultsUpdate != null)
                            OnResultsUpdate.Invoke(this, "Error on UDP Receive: " + e.Message);
                    }
                }
            }));

            ThreadUDPListen.IsBackground = true;

            if (UDPListen)
                ThreadUDPListen.Start();
        }

        private async void ListenTCP()
        {
            ThreadTCPListen = new Thread(new ThreadStart(async delegate
            {
                while (TCPListen)
                {
                    try
                    {
                        NetworkStream stream = TCPClient.GetStream();

                        // 1. Читаем 4 байта длины
                        byte[] lengthBytes = new byte[4];
                        int bytesRead = 0;
                        while (bytesRead < 4)
                        {
                            int r = await stream.ReadAsync(lengthBytes, bytesRead, 4 - bytesRead);
                            if (r == 0) break;
                            bytesRead += r;
                        }
                        if (bytesRead < 4) break; // соединение закрыто

                        if (BitConverter.IsLittleEndian)
                            Array.Reverse(lengthBytes);
                        int length = BitConverter.ToInt32(lengthBytes, 0);
                        if (length <= 0) continue;

                        // 2. Читаем ровно length байт тела
                        byte[] data = new byte[length];
                        int totalRead = 0;
                        while (totalRead < length)
                        {
                            int r = await stream.ReadAsync(data, totalRead, length - totalRead);
                            if (r == 0) break;
                            totalRead += r;
                        }
                        if (totalRead < length) break; // неполное сообщение

                        // 3. Десериализуем
                        IP2PBase Item = await data.ToP2PBase();
                        ProcessItem(Item);
                    }
                    catch (Exception e)
                    {
                        OnResultsUpdate?.Invoke(this, $"Error on TCP Receive: {e.Message}");
                        break;
                    }
                }
            }));

            ThreadTCPListen.IsBackground = true;
            if (TCPListen)
                ThreadTCPListen.Start();
        }

        private void ProcessItem(IP2PBase Item, IPEndPoint EP = null)
        {
            if (Item.GetType() == typeof(Message))
            {
                Message m = (Message)Item;
                ClientInfo CI = Clients.FirstOrDefault(x => x.ID == Item.ID);

                if (m.ID == 0)
                    if (OnResultsUpdate != null)
                        OnResultsUpdate.Invoke(this, m.From + ": " + m.Content);

                if (m.ID != 0 & EP != null & CI != null)
                    if (OnMessageReceived != null)
                        OnMessageReceived.Invoke(EP, new MessageReceivedEventArgs(CI, m, EP));
            }
            else if (Item.GetType() == typeof(ClientInfo))
            {
                ClientInfo CI = Clients.FirstOrDefault(x => x.ID == Item.ID);

                if (CI == null)
                {
                    Clients.Add((ClientInfo)Item);

                    if (OnClientAdded != null)
                        OnClientAdded.Invoke(this, (ClientInfo)Item);
                }
                else
                {
                    CI.Update((ClientInfo)Item);

                    if (OnClientUpdated != null)
                        OnClientUpdated.Invoke(this, (ClientInfo)Item);
                }
            }
            else if (Item.GetType() == typeof(Notification))
            {
                Notification N = (Notification)Item;

                if (N.Type == NotificationsTypes.Disconnected)
                {
                    ClientInfo CI = Clients.FirstOrDefault(x => x.ID == long.Parse(N.Tag.ToString()));

                    if (CI != null)
                    {
                        if (OnClientRemoved != null)
                            OnClientRemoved.Invoke(this, CI);

                        Clients.Remove(CI);
                    }
                }
                else if(N.Type == NotificationsTypes.ServerShutdown)
                {
                    if (OnResultsUpdate != null)
                        OnResultsUpdate.Invoke(this, "Server shutting down.");

                    ConnectOrDisconnect();
                }
            }
            else if (Item.GetType() == typeof(Req))
            {
                Req R = (Req)Item;

                ClientInfo CI = Clients.FirstOrDefault(x => x.ID == R.ID);

                if (CI != null)
                {
                    if (OnResultsUpdate != null)
                        OnResultsUpdate.Invoke(this, "Received Connection Request from: " + CI.ToString());

                    IPEndPoint ResponsiveEP = FindReachableEndpoint(CI);

                    if (ResponsiveEP != null)
                    {
                        if (OnResultsUpdate != null)
                            OnResultsUpdate.Invoke(this, "Connection Successfull to: " + ResponsiveEP.ToString());

                        if (OnClientConnection != null)
                            OnClientConnection.Invoke(CI, ResponsiveEP);

                        if (OnClientUpdated != null)
                            OnClientUpdated.Invoke(this, CI);
                    }
                }
            }
            else if (Item.GetType() == typeof(Ack))
            {
                Ack A = (Ack)Item;

                if (A.Responce)
                    AckResponces.Add(A);
                else
                {
                    ClientInfo CI = Clients.FirstOrDefault(x => x.ID == A.ID);

                    if (CI.ExternalEndpoint.Address.Equals(EP.Address) & CI.ExternalEndpoint.Port != EP.Port)
                    {
                        if (OnResultsUpdate != null)
                            OnResultsUpdate.Invoke(this, "Received Ack on Different Port (" + EP.Port + "). Updating ...");

                        CI.ExternalEndpoint.Port = EP.Port;

                        if (OnClientUpdated != null)
                            OnClientUpdated.Invoke(this, CI);
                    }

                    List<string> IPs = new List<string>();
                    CI.InternalAddresses.ForEach(new Action<IPAddress>(delegate (IPAddress IP) { IPs.Add(IP.ToString()); }));

                    if (!CI.ExternalEndpoint.Address.Equals(EP.Address) & !IPs.Contains(EP.Address.ToString()))
                    {
                        if (OnResultsUpdate != null)
                            OnResultsUpdate.Invoke(this, "Received Ack on New Address (" + EP.Address + "). Updating ...");

                        CI.InternalAddresses.Add(EP.Address);
                    }

                    A.Responce = true;
                    A.RecipientID = LocalClientInfo.ID;
                    SendMessageUDP(A, EP);
                }
            }
        }

        public async Task ConnectToClient(ClientInfo CI)
        {
            Req R = new Req(LocalClientInfo.ID, CI.ID);

            await SendMessageTCPWithLength(R);

            if (OnResultsUpdate != null)
                OnResultsUpdate.Invoke(this, "Sent Connection Request To: " + CI.ToString());

            Thread Connect = new Thread(new ThreadStart(delegate
            {
                IPEndPoint ResponsiveEP = FindReachableEndpoint(CI);

                if (ResponsiveEP != null)
                {
                    if (OnResultsUpdate != null)
                        OnResultsUpdate.Invoke(this, "Connection Successfull to: " + ResponsiveEP.ToString());

                    if (OnClientConnection != null)
                        OnClientConnection.Invoke(CI, ResponsiveEP);
                }
            }));

            Connect.IsBackground = true;

            Connect.Start();
        }

        private IPEndPoint FindReachableEndpoint(ClientInfo CI)
        {
            if (OnResultsUpdate != null)
                OnResultsUpdate.Invoke(this, "Attempting to Connect via LAN");

            for (int ip = 0; ip < CI.InternalAddresses.Count; ip++) 
            {
                if (!TCPClient.Connected)
                    break;

                IPAddress IP = CI.InternalAddresses[ip];              

                IPEndPoint EP = new IPEndPoint(IP, CI.InternalEndpoint.Port);

                for (int i = 1; i < 4; i++)
                {
                    if (!TCPClient.Connected)
                        break;

                    if (OnResultsUpdate != null)
                        OnResultsUpdate.Invoke(this, "Sending Ack to " + EP.ToString() + ". Attempt " + i + " of 3");

                    SendMessageUDP(new Ack(LocalClientInfo.ID), EP);
                    Thread.Sleep(200);

                    Ack Responce = AckResponces.FirstOrDefault(a => a.RecipientID == CI.ID);

                    if (Responce != null)
                    {                        
                        if (OnResultsUpdate != null)
                            OnResultsUpdate.Invoke(this, "Received Ack Responce from " + EP.ToString());

                        CI.ConnectionType = ConnectionTypes.LAN;

                        AckResponces.Remove(Responce);

                        return EP;
                    }
                }
            }

            if (CI.ExternalEndpoint != null)
            {
                if (OnResultsUpdate != null)
                    OnResultsUpdate.Invoke(this, "Attempting to Connect via Internet");

                for (int i = 1; i < 5; i++)
                {
                    if (!TCPClient.Connected)
                        break;

                    if (OnResultsUpdate != null)
                        OnResultsUpdate.Invoke(this, "Sending Ack to " + CI.ExternalEndpoint + ". Attempt " + i + " of 99");

                    SendMessageUDP(new Ack(LocalClientInfo.ID), CI.ExternalEndpoint);
                    Thread.Sleep(300);

                    Ack Responce = AckResponces.FirstOrDefault(a => a.RecipientID == CI.ID);

                    if (Responce != null)
                    {
                        if (OnResultsUpdate != null)
                            OnResultsUpdate.Invoke(this, "Received Ack New from " + CI.ExternalEndpoint.ToString());

                        CI.ConnectionType = ConnectionTypes.WAN;

                        AckResponces.Remove(Responce);

                        return CI.ExternalEndpoint;
                    }
                }

                if (OnResultsUpdate != null)
                    OnResultsUpdate.Invoke(this, "Connection to " + CI.Name + " failed");
            }
            else
            {
                if (OnResultsUpdate != null)
                    OnResultsUpdate.Invoke(this, "Client's External EndPoint is Unknown");
            }

            return null;
        }
    }

    public class MessageReceivedEventArgs : EventArgs
    {
        public Message message { get; set; }
        public ClientInfo clientInfo { get; set; }
        public IPEndPoint EstablishedEP { get; set; }

        public MessageReceivedEventArgs(ClientInfo _clientInfo, Message _message, IPEndPoint _establishedEP)
        {
            clientInfo = _clientInfo;
            message = _message;
            EstablishedEP = _establishedEP;
        }
    }
}
