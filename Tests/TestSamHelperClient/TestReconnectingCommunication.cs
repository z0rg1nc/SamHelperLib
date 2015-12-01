using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.MiscUtils;
using NLog;
using Xunit;

namespace BtmI2p.SamHelper.Tests
{
    public class TestReconnectingCommunication
    {
        private static readonly Logger _logger
            = LogManager.GetCurrentClassLogger();

        private async Task testSendReceiveFunc(
            ReliableSamHelper reliableHelperRecv,
            ReconnectingSamHelper reconnectingSamHelperSend,
            ReliableSamHelper reliableHelperSend,
            byte[] data,
            SamHelper samHelperReceive
        )
        {
            var waitRecvMessage = reliableHelperRecv.ReliableMessageReceived
                    .Where(x => x.Destination == reconnectingSamHelperSend.Session.Destination)
                    .FirstAsync()
                    .ToTask();
            try
            {
                var messageId = await reliableHelperSend.SendReliableMessage(
                    samHelperReceive.Session.Destination,
                    data
                    ).ConfigureAwait(false);
                _logger.Trace("Message sent id {0}", messageId);
            }
            catch (EnumException<ReliableSamHelper.SendReliableMessageExcs> exc)
            {
                _logger.Trace("Send error {0}", exc.ExceptionCode);
                throw;
            }
            try
            {
                var receivedMessage = await waitRecvMessage.ConfigureAwait(false);
                _logger.Trace("Message received {0} bytes", receivedMessage.Data.Length);
                Assert.Equal(data, receivedMessage.Data);
            }
            catch (OperationCanceledException exc)
            {
                _logger.Trace("Recv timeout {0}", exc);
                throw;
            }
            catch (Exception exc)
            {
                _logger.Trace("Recv error {0}", exc);
                throw;
            }
        }

        [Fact]
        public async Task TestReconnectingReliable()
        {
            const string addressSend = "127.0.0.1";
            const int portSend = 7656;
            /**/
            var proxyHostname = "127.0.0.1";
            var proxyPort = 17000;
            var proxyServer = await MyTcpProxyServer.CreateInstance(
                new MyTcpProxyServerSettings()
                {
                    DestinationHostname = addressSend,
                    DestinationPort = portSend,
                    ServerHostname = proxyHostname,
                    ServerPort = proxyPort
                }
            ).ConfigureAwait(false);
            /**/
            const string addressRecv = addressSend;
            const int portRecv = portSend;
            string sendPrivKeys;
            /**/
            {
                var samHelperSend = await SamHelper.CreateInstance(
                    new SamHelperSettings()
                    {
                        SamServerAddress = addressSend,
                        SamServerPort = portSend
                    }, CancellationToken.None
                    ).ConfigureAwait(false);
                sendPrivKeys = samHelperSend.Session.PrivateKey;
                await samHelperSend.MyDisposeAsync().ConfigureAwait(false);
            }
            /**/
            var reconnectingSamHelperSend = await ReconnectingSamHelper.CreateInstance(
                new ReconnectingSamHelperSettings()
                {
                    ImplementationHelperSettings =
                        new SamHelperSettings()
                        {
                            SamServerAddress = proxyHostname,
                            SamServerPort = proxyPort,
                            SessionPrivateKeys = sendPrivKeys
                        }
                }, CancellationToken.None
                ).ConfigureAwait(false);
            var samHelperReceive = await SamHelper.CreateInstance(
                new SamHelperSettings()
                {
                    SamServerAddress = addressRecv,
                    SamServerPort = portRecv
                }, CancellationToken.None
                ).ConfigureAwait(false);
            var reliableHelperSend = new ReliableSamHelper(
                reconnectingSamHelperSend,
                new ReliableSamHelperSettings()
                );
            var reliableHelperRecv = new ReliableSamHelper(
                samHelperReceive,
                new ReliableSamHelperSettings()
                );
            var data = new byte[200000];
            MiscFuncs.GetRandomBytes(data);
            /**/
            await testSendReceiveFunc(
                reliableHelperRecv,
                reconnectingSamHelperSend,
                reliableHelperSend,
                data,
                samHelperReceive
            ).ConfigureAwait(false);
            await proxyServer.MyDisposeAsync().ConfigureAwait(false);
            _logger.Trace("###################################");
            await Assert.ThrowsAsync<TimeoutException>(
                async () => await testSendReceiveFunc(
                    reliableHelperRecv,
                    reconnectingSamHelperSend,
                    reliableHelperSend,
                    data,
                    samHelperReceive
                ).ConfigureAwait(false)
            ).ConfigureAwait(false);
            /**/
            _logger.Trace("###################################");
            proxyServer = await MyTcpProxyServer.CreateInstance(
                new MyTcpProxyServerSettings()
                {
                    DestinationHostname = addressSend,
                    DestinationPort = portSend,
                    ServerHostname = proxyHostname,
                    ServerPort = proxyPort
                }
            ).ConfigureAwait(false);
            reconnectingSamHelperSend.TryToReconnect(DateTime.UtcNow);
            await Task.Delay(TimeSpan.FromSeconds(15.0d)).ConfigureAwait(false);
            /**/
            await testSendReceiveFunc(
                reliableHelperRecv,
                reconnectingSamHelperSend,
                reliableHelperSend,
                data,
                samHelperReceive
            ).ConfigureAwait(false);
            /**/
            await reliableHelperSend.MyDisposeAsync().ConfigureAwait(false);
            _logger.Trace("reliableHelperSend disposed");
            await reliableHelperRecv.MyDisposeAsync().ConfigureAwait(false);
            _logger.Trace("reliableHelperRecv disposed");
            await reconnectingSamHelperSend.MyDisposeAsync().ConfigureAwait(false);
            _logger.Trace("samHelperSend disposed");
            await samHelperReceive.MyDisposeAsync().ConfigureAwait(false);
            _logger.Trace("samHelperReceive disposed");
            await proxyServer.MyDisposeAsync().ConfigureAwait(false);
            _logger.Trace("{0} disposed",MyNameof.GetLocalVarName(() => proxyServer));
        }
    }
}
