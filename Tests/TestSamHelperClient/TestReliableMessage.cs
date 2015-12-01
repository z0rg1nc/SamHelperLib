using System;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.MiscUtils;
using NLog;
using Xunit;

namespace BtmI2p.SamHelper.Tests
{
    public class TestReliableMessage
    {
        private static readonly Logger _logger
            = LogManager.GetCurrentClassLogger();
        [Fact]
        public async Task TestMemoryOverhead()
        {
            const string addressSend = "127.0.0.1";
            const int portSend = 7656;
            const string addressRecv = addressSend;
            const int portRecv = 7656;
            const string i2cpOptions = "";
            var samHelperSend = await SamHelper.CreateInstance(
                new SamHelperSettings()
                {
                    SamServerAddress = addressSend,
                    SamServerPort = portSend
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
                samHelperSend,
                new ReliableSamHelperSettings()
            );
            var reliableHelperRecv = new ReliableSamHelper(
                samHelperReceive,
                new ReliableSamHelperSettings()
            );
            /**/
            var data = new byte[400000];
            MiscFuncs.GetRandomBytes(data);
            _logger.Trace("Begin Total memory - {0} bytes", GC.GetTotalMemory(true));
            for (int i = 0; i < 10; i++)
            {
                var waitRecvMessage = reliableHelperRecv.ReliableMessageReceived
                    .Where(x => x.Destination == samHelperSend.Session.Destination)
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
                    _logger.Trace(
                        "Message received {0} bytes", 
                        receivedMessage.Data.Length
                    );
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
            /**/
            long memory = GC.GetTotalMemory(true);
            _logger.Trace("End Total memory - {0} bytes", memory);
            await reliableHelperSend.MyDisposeAsync().ConfigureAwait(false);
            _logger.Trace("reliableHelperSend disposed");
            await reliableHelperRecv.MyDisposeAsync().ConfigureAwait(false);
            _logger.Trace("reliableHelperRecv disposed");
            await samHelperSend.MyDisposeAsync().ConfigureAwait(false);
            _logger.Trace("samHelperSend disposed");
            await samHelperReceive.MyDisposeAsync().ConfigureAwait(false);
            _logger.Trace("samHelperReceive disposed");
        }

        [Fact]
        public async Task TestSendReceiveOneMessageImpl()
        {
            const string addressSend = "192.168.56.102";//"192.168.1.132";
            const int portSend = 7656;
            const string addressRecv = addressSend; //"127.0.0.1";
            const int portRecv = 7656;
            var samHelperSend = await SamHelper.CreateInstance(
                new SamHelperSettings()
                {
                    SamServerAddress = addressSend,
                    SamServerPort = portSend
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
                samHelperSend, 
                new ReliableSamHelperSettings()
            );
            var reliableHelperRecv = new ReliableSamHelper(
                samHelperReceive, 
                new ReliableSamHelperSettings()
            );
            var data = new byte[1];
            MiscFuncs.GetRandomBytes(data);
            var waitRecvMessage = reliableHelperRecv.ReliableMessageReceived
                .Where(x => x.Destination == samHelperSend.Session.Destination)
                .FirstAsync()
                .ToTask();
            var sendProgress = new Progress<OutMessageProgressInfo>(
                x => _logger.Trace(x.ToString())
            );
            try
            {
                var messageId = await reliableHelperSend.SendReliableMessage(
                    samHelperReceive.Session.Destination,
                    data,
                    sendProgress
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
                Assert.Equal(data,receivedMessage.Data);
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
            _logger.Trace("End Total memory - {0} bytes", GC.GetTotalMemory(true));
            await reliableHelperSend.MyDisposeAsync().ConfigureAwait(false);
            _logger.Trace("reliableHelperSend disposed");
            await reliableHelperRecv.MyDisposeAsync().ConfigureAwait(false);
            _logger.Trace("reliableHelperRecv disposed");
            await samHelperSend.MyDisposeAsync().ConfigureAwait(false);
            _logger.Trace("samHelperSend disposed");
            await samHelperReceive.MyDisposeAsync().ConfigureAwait(false);
            _logger.Trace("samHelperReceive disposed");
        }
    }
}
