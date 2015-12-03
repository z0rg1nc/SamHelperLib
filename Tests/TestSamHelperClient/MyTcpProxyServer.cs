using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.MiscUtils;
using BtmI2p.ObjectStateLib;
using NLog;

namespace BtmI2p.SamHelper.Tests
{
    public class MyTcpProxyServerSettings : ICheckable
    {
        public string DestinationHostname = string.Empty;
        public int DestinationPort = 0;
        public string ServerHostname = string.Empty;
        public int ServerPort = 0;
        public void CheckMe()
        {
            if(string.IsNullOrWhiteSpace(DestinationHostname))
                throw new ArgumentNullException(
                    this.MyNameOfProperty(e => e.DestinationHostname));
            if(string.IsNullOrWhiteSpace(ServerHostname))
                throw new ArgumentNullException(
                    this.MyNameOfProperty(e => e.ServerHostname));
            if(DestinationPort < 0 || DestinationPort > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(
                    this.MyNameOfProperty(e => e.DestinationPort));
            if(ServerPort < 0 ||  ServerPort > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(
                    this.MyNameOfProperty(e => e.ServerPort));
        }
    }
    public class MyTcpProxyServer : IMyAsyncDisposable
    {
        private MyTcpProxyServer()
        {
        }

        private readonly Logger _log = LogManager.GetCurrentClassLogger();

        private async void WaitClientConnectAction()
        {
            var curMethodName = this.MyNameOfMethod(e => e.WaitClientConnectAction());
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    var client = await _serverTcpListener.AcceptTcpClientAsync()
                        .ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                    try
                    {
                        var fromClientToDestinationTask = await Task.Factory.StartNew(
                            async () =>
                            {
                                try
                                {
                                    var buffer = new byte[4096];
                                    using (var stream = client.GetStream())
                                    using (var destinationStream = _clientToDestination.GetStream())
                                    {
                                        while (!_cts.IsCancellationRequested)
                                        {
                                            var i = await stream.ReadAsync(buffer, 0, buffer.Length)
                                                .ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                                            if (i == 0)
                                                return;
                                            await destinationStream.WriteAsync(
                                                buffer, 0, i
                                            ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                                        }
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                }
                            }
                        ).ConfigureAwait(false);
                        var fromDestinationToClientTask = await Task.Factory.StartNew(
                            async () =>
                            {
                                try
                                {
                                    var buffer = new byte[4096];
                                    using (var stream = client.GetStream())
                                    using (var destinationStream = _clientToDestination.GetStream())
                                    {
                                        while (!_cts.IsCancellationRequested)
                                        {
                                            var i = await destinationStream.ReadAsync(buffer, 0, buffer.Length)
                                                .ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                                            if (i == 0)
                                                return;
                                            await stream.WriteAsync(
                                                buffer, 0, i
                                            ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                                        }
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                }
                            }
                        ).ConfigureAwait(false);
                        await Task.WhenAny(
                            fromClientToDestinationTask,
                            fromDestinationToClientTask
                        ).ConfigureAwait(false);
                    }
                    finally
                    {
                        client.Close();
                    }
                }
            }
            catch (WrongDisposableObjectStateException)
            {
            }
            catch (Exception exc)
            {
                _log.Error(
                    "{0} unexpected error '{1}'", curMethodName, exc.ToString());
            }
        }

        public static async Task<MyTcpProxyServer> CreateInstance(
            MyTcpProxyServerSettings settings
        )
        {
            if(settings == null)
                throw new ArgumentNullException(
                    MyNameof.GetLocalVarName(() => settings));
            settings.CheckMe();
            var result = new MyTcpProxyServer();
            result._clientToDestination = new TcpClient();
            await result._clientToDestination.ConnectAsync(
                settings.DestinationHostname,
                settings.DestinationPort
            ).ConfigureAwait(false);
            try
            {
                result._serverTcpListener = new TcpListener(
                    IPAddress.Parse(settings.ServerHostname),
                    settings.ServerPort
                );
                result._serverTcpListener.Start();
                try
                {
                    result._stateHelper.SetInitializedState();
                    result.WaitClientConnectAction();
                    return result;
                }
                catch
                {
                    result._serverTcpListener.Stop();
                    throw;
                }
            }
            catch
            {
                result._clientToDestination.Close();
                throw;
            }
        }

        private TcpListener _serverTcpListener;
        private TcpClient _clientToDestination;
        private readonly CancellationTokenSource _cts 
            = new CancellationTokenSource();
        private readonly DisposableObjectStateHelper _stateHelper
            = new DisposableObjectStateHelper("MyTcpProxyServer");
        public async Task MyDisposeAsync()
        {
            _cts.Cancel();
            await _stateHelper.MyDisposeAsync().ConfigureAwait(false);
            _serverTcpListener.Stop();
            _clientToDestination.Close();
            _cts.Dispose();
        }
    }

}
