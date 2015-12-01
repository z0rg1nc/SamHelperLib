using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.MiscUtils;
using Xunit;
using Xunit.Abstractions;

namespace BtmI2p.SamHelper.Tests
{
    public class UnitTestSimple
    {
        private readonly ITestOutputHelper _output;

        public UnitTestSimple(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task TestConnect2SameDestination()
        {
            const string address = "127.0.0.1";
            const int port = 7656;
            var samHelperSend = await SamHelper.CreateInstance(
                new SamHelperSettings()
                {
                    SamServerAddress = address,
                    SamServerPort = port
                },
                CancellationToken.None
            ).ConfigureAwait(false);
            try
            {
                var samHelperSend2 = await SamHelper.CreateInstance(
                    new SamHelperSettings()
                    {
                        SamServerAddress = address,
                        SamServerPort = port,
                        SessionPrivateKeys = samHelperSend.Session.PrivateKey
                    },
                    CancellationToken.None
                    ).ConfigureAwait(false);
                throw new Exception();
            }
            catch (EnumException<SamHelper.ECreateInstanceErrCodes> enumExc)
            {
                Assert.Equal(
                    SamHelper.ECreateInstanceErrCodes.CreateSessionError,
                    enumExc.ExceptionCode
                );
                var innerExc = (EnumException<SamHelper.ECreateSessionErrorCodes>)enumExc.InnerException;
                Assert.NotNull(innerExc);
                Assert.Equal(
                    SamHelper.ECreateSessionErrorCodes.DuplicatedDest,
                    innerExc.ExceptionCode
                );
            }
            finally
            {
                await samHelperSend.MyDisposeAsync().ConfigureAwait(false);
            }
        }
        /*
        [Fact]
        public async Task TestConnectAndDelay()
        {
            const string address = "127.0.0.1";
            const int port = 7656;
                var samHelperSend = await SamHelper.CreateInstance(
                    new SamHelperSettings()
                    {
                        SamServerAddress = address,
                        SamServerPort = port
                    },
                    CancellationToken.None
                ).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromDays(1));
            await samHelperSend.MyDisposeAsync().ConfigureAwait(false);
        }
        */
        [Fact]
        public async Task TestConnectDisconnectNTimes()
        {
            const string address = "127.0.0.1";
            const int port = 7656;
            int n = 20;
            string privateKeys = null;
            for (int i = 0; i < n; i++)
            {
                var samHelperSend = await SamHelper.CreateInstance(
                    new SamHelperSettings()
                    {
                        SamServerAddress = address,
                        SamServerPort = port,
                        SessionPrivateKeys = privateKeys
                    },
                    CancellationToken.None
                ).ConfigureAwait(false);
                privateKeys = samHelperSend.Session.PrivateKey;
                _output.WriteLine("{0}", i);
                await samHelperSend.MyDisposeAsync().ConfigureAwait(false);
            }
        }

        [Fact]
        public void TestExtractDestination()
        {
            var keyDestinations = new []
            {
                new[]
                {
                    "TefXQofupTzTYIVE4YQ5M39hmfPiQTzpIKezWlqE5-Td0xF-dR6ck~zOsZwKGm5me3MCoirZGYgHRBYbpvGfdg8wzx4MNrARBMivjlI6JHb694cqA53dFpBaoIwvPxOpXT08rJPY84UCVKKl-OYcqQBFiR9nKiXwfjmPGx22lxo6Okg1Tihpaz8amA4GBi2WH-2mGZj~iAOsl81Ll~G~tBGJHxR57KSIKPU8FuglmOUarrYJBh6YYX0EcAyEmaLyGiULhAVhczg9vucDuPyU5-Vfw3rVciAteMUuU9VVmQA8kEyxqcmqPwAKQStCApw9jEX4vVJXWJ5cbknt4J3~fE3POYk9kG9b-Xhuo6jJDe7oEYVsCLW34nopMlWydLONgetlRUiW4-LdSg9MEuupY4AZYzTSXRxZ7hlmWO6xmbmBAr6F7oQQxd8dcWaOYH~zrCbvrhEYaWAVmegd0s~Yv4Yb~GoJXHst~wdLzH3Jz-lXC4O5bEW5tJezP~Q8HJUCAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACL5w0IK~bao-xKw9RCS9EV~LP-lv9iQSgymJmDFCOQAeuCj20W2vltzfUA4PChCFD",
                    "TefXQofupTzTYIVE4YQ5M39hmfPiQTzpIKezWlqE5-Td0xF-dR6ck~zOsZwKGm5me3MCoirZGYgHRBYbpvGfdg8wzx4MNrARBMivjlI6JHb694cqA53dFpBaoIwvPxOpXT08rJPY84UCVKKl-OYcqQBFiR9nKiXwfjmPGx22lxo6Okg1Tihpaz8amA4GBi2WH-2mGZj~iAOsl81Ll~G~tBGJHxR57KSIKPU8FuglmOUarrYJBh6YYX0EcAyEmaLyGiULhAVhczg9vucDuPyU5-Vfw3rVciAteMUuU9VVmQA8kEyxqcmqPwAKQStCApw9jEX4vVJXWJ5cbknt4J3~fE3POYk9kG9b-Xhuo6jJDe7oEYVsCLW34nopMlWydLONgetlRUiW4-LdSg9MEuupY4AZYzTSXRxZ7hlmWO6xmbmBAr6F7oQQxd8dcWaOYH~zrCbvrhEYaWAVmegd0s~Yv4Yb~GoJXHst~wdLzH3Jz-lXC4O5bEW5tJezP~Q8HJUCAAAA"
                },
                new []
                {
                    "NK4USq1-mR1oK-J9~htlNTXgB5bjLS8i8aKXz~M14pwRp0qOryrXU9c6Pqip7VbJntjAtfNCFu6-SegpNIVWMcwNL-GyrxQPij9s8cYU8ra6wPZV~-pLnFui-7xG~t9A8wBAUEj94joGVCLMedQ7yP0BUaJn9lfvPALvgJxiioa-m-OaOPMtUTFke77ZL0GBDxRm-z9KMEuJWRBWZOJmbKZOpexllw~N7W68r2wapiaqx4eYIskyRwH9SdIaV9q8OOfKIRFzmyJ5-47er57K7UlCGtxtdXbrnoVybpp-5mYcYfolqzA~IEPMo7TOFxEpBNdSXKFfhWm70PTt4GQedS~njJZj4Z2aCRk4uSOTRF6pYwuK5-kncHr4jCGivCTgjVt1LQaKmfNKLLMmJsmkuE9Szb8DU4rbt4H3pMJcP3wssNleXiBUawfN642mgechWshwnO09D0SRgcV480rh3YrIt-my1CXCzB6ay2ZwW6OFhDvGjuLAL4s-fTT~XOzlAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAGqPkndWfsxSWhKOtHywH9KPdJyysKmD9YwIn5AOLgIoWKoUpiF1HwEoMAH-XesLD",
                    "NK4USq1-mR1oK-J9~htlNTXgB5bjLS8i8aKXz~M14pwRp0qOryrXU9c6Pqip7VbJntjAtfNCFu6-SegpNIVWMcwNL-GyrxQPij9s8cYU8ra6wPZV~-pLnFui-7xG~t9A8wBAUEj94joGVCLMedQ7yP0BUaJn9lfvPALvgJxiioa-m-OaOPMtUTFke77ZL0GBDxRm-z9KMEuJWRBWZOJmbKZOpexllw~N7W68r2wapiaqx4eYIskyRwH9SdIaV9q8OOfKIRFzmyJ5-47er57K7UlCGtxtdXbrnoVybpp-5mYcYfolqzA~IEPMo7TOFxEpBNdSXKFfhWm70PTt4GQedS~njJZj4Z2aCRk4uSOTRF6pYwuK5-kncHr4jCGivCTgjVt1LQaKmfNKLLMmJsmkuE9Szb8DU4rbt4H3pMJcP3wssNleXiBUawfN642mgechWshwnO09D0SRgcV480rh3YrIt-my1CXCzB6ay2ZwW6OFhDvGjuLAL4s-fTT~XOzlAAAA"
                }
            };
            foreach (var pair in keyDestinations)
            {
                var privateKeyString = pair[0];
                var privateKey = new I2PPrivateKey(privateKeyString);
                var destinationString = privateKey.Destination.ToI2PBase64();
                Assert.Equal(destinationString, pair[1]);
                Assert.Equal(privateKey.ToI2PBase64(), privateKeyString);
                _output.WriteLine("#####" + destinationString);
            }

        }

        [Fact]
        public void TestReadWriteI2PLong()
        {
            byte[] b1 = { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };
            _output.WriteLine(BitConverter.ToString(b1));
            long i1;
            int i2;
            short i3;
            sbyte i4;
            using (var ms = new MemoryStream(b1))
            {
                i1 = I2PPrivateKey.ReadLong(ms, 8);
            }
            using (var ms = new MemoryStream(b1))
            {
                i2 = (int)I2PPrivateKey.ReadLong(ms, 4);
            }
            using (var ms = new MemoryStream(b1))
            {
                i3 = (short)I2PPrivateKey.ReadLong(ms, 2);
            }
            using (var ms = new MemoryStream(b1))
            {
                i4 = (sbyte)I2PPrivateKey.ReadLong(ms, 1);
            }
            _output.WriteLine("{0} {1} {2} {3}", i1, i2, i3, i4);
            using (var ms = new MemoryStream())
            {
                I2PPrivateKey.WriteLong(ms,8,i1);
                _output.WriteLine(BitConverter.ToString(ms.ToArray()));
            }
            using (var ms = new MemoryStream())
            {
                I2PPrivateKey.WriteLong(ms, 4, i2);
                _output.WriteLine(BitConverter.ToString(ms.ToArray()));
            }
            using (var ms = new MemoryStream())
            {
                I2PPrivateKey.WriteLong(ms, 2, i3);
                _output.WriteLine(BitConverter.ToString(ms.ToArray()));
            }
            using (var ms = new MemoryStream())
            {
                I2PPrivateKey.WriteLong(ms, 1, i4);
                _output.WriteLine(BitConverter.ToString(ms.ToArray()));
            }
        }

        [Fact]
        public async Task TestGenNKeys()
        {
            const string address = "127.0.0.1";
            const int port = 7656;
            int n = 5;
            for (int i = 0; i < n; i++)
            {
                var samHelperSend = await SamHelper.CreateInstance(
                    new SamHelperSettings()
                    {
                        SamServerAddress = address,
                        SamServerPort = port,
                        SessionPrivateKeys = null
                    },
                    CancellationToken.None
                ).ConfigureAwait(false);
                _output.WriteLine("{0}", i);
                _output.WriteLine("Dest: {0}", samHelperSend.Session.Destination);
                _output.WriteLine("Key: {0}", samHelperSend.Session.PrivateKey);
                await samHelperSend.MyDisposeAsync().ConfigureAwait(false);
            }
        }

        [Fact]
        public async Task TestConnectDisconnectImpl()
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
            const string address = "127.0.0.1";
            const int port = 7656;
            var samHelperSend = await SamHelper.CreateInstance(
                new SamHelperSettings()
                {
                    SamServerAddress = address,
                    SamServerPort = port
                }, 
                new CancellationToken()
            ).ConfigureAwait(false);
            var samHelperReceive = await SamHelper.CreateInstance(
                new SamHelperSettings()
                {
                    SamServerAddress = address,
                    SamServerPort = port
                },
                new CancellationToken()
            ).ConfigureAwait(false);
            var sendDestination = samHelperSend.Session.Destination;
            _output.WriteLine("Send session created " + sendDestination.Substring(0, 20));
            var recvDestination = samHelperReceive.Session.Destination;
            _output.WriteLine("Recv session created " + recvDestination.Substring(0,20));
            await Task.Delay(2000).ConfigureAwait(false);
            _output.WriteLine("delay");
            var datagramRecvTimeout 
                = new CancellationTokenSource(
                    TimeSpan.FromSeconds(15)
                );
            samHelperReceive.DatagramDataReceived
                .ObserveOn(TaskPoolScheduler.Default)
                .Subscribe(
                    x => { _output.WriteLine("{0}", x.Data.Length); }
                );
            var datagramReceivedTask =
                samHelperReceive
                    .DatagramDataReceived
                    .FirstAsync()
                    .ToTask(datagramRecvTimeout.Token);
            
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    await samHelperSend.SendDatagram(
                        recvDestination,
                        Enumerable
                            .Repeat((byte) 100, 200)
                            .ToArray()
                    ).ConfigureAwait(false);
                }
                catch (Exception exc)
                {
                    _output.WriteLine("Send datagram error '{0}' '{1}'", exc.ToString(), i);
                    throw;
                }
            }
            
            try
            {
                var recvDatagram = await datagramReceivedTask.ConfigureAwait(false);
                _output.WriteLine(
                    "Datagram received message: " 
                    + Encoding.ASCII.GetString(
                        recvDatagram.Data
                    )
                );
            }
            catch (OperationCanceledException)
            {
                _output.WriteLine("Datagram receive timeout");
                throw;
            }
            catch (Exception exc)
            {
                _output.WriteLine("Datagram receive error '{0}'", exc.ToString());
                throw;
            }
            /**************************/
            await Task.Delay(500).ConfigureAwait(false);
            await samHelperSend.MyDisposeAsync().ConfigureAwait(false);
        }

        [Fact]
        public async Task TestAsyncWriteImpl()
        {
            using (var memStream = new MemoryStream())
            {
                var testChars = new[] {'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h'};
                const int bufferSize = 500;
                var dataArray = testChars
                    .Select(
                        c => 
                            Encoding.ASCII.GetBytes(
                                Enumerable
                                    .Repeat(c, bufferSize)
                                    .ToArray()
                            )
                    );
                foreach (var bytes in dataArray)
                {
                    await memStream.WriteAsync(bytes,0,bytes.Length).ConfigureAwait(false);
                }
                memStream.Seek(0, SeekOrigin.Begin);
                var reader = new StreamReader(memStream, Encoding.ASCII);
                _output.WriteLine(reader.ReadToEnd());
            }
        }

        public static class TestStaticTemplateClass<T1>
        {
            public static T1 TestValue;
            public static int TestLock;
        }

        [Fact]
        public void TestWorkWithTemplateClass()
        {
            TestStaticTemplateClass<int>.TestValue = 4;
            TestStaticTemplateClass<string>.TestValue = "Hello world";
            TestStaticTemplateClass<float>.TestValue = 5.6f;
            _output.WriteLine(
                "{0} {1} {2}",
                TestStaticTemplateClass<int>.TestValue,
                TestStaticTemplateClass<string>.TestValue,
                TestStaticTemplateClass<float>.TestValue
            );

            TestStaticTemplateClass<int>.TestLock = 1;
            TestStaticTemplateClass<string>.TestLock = 2;
            TestStaticTemplateClass<float>.TestLock = 3;
            _output.WriteLine(
                "{0} {1} {2}",
                TestStaticTemplateClass<int>.TestLock,
                TestStaticTemplateClass<string>.TestLock,
                TestStaticTemplateClass<float>.TestLock
            );
        }

    }
}
