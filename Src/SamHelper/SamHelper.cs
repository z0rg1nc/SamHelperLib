using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.MiscUtil.Conversion;
using BtmI2p.MiscUtil.IO;
using BtmI2p.MiscUtils;
using BtmI2p.ObjectStateLib;
using NLog;

namespace BtmI2p.SamHelper
{
    public class SamHost
    {
        public string HostName;
        public int Port;
    }
    public class I2PPrivateKey : IDisposable
    {
        public I2PDestination Destination;
        private const int PrivateKeyLength = 256;
        public byte[] PrivateKey;
        public byte[] SigningPrivateKey;

        private void LoadData(Stream inStream)
        {
            var converter = new BigEndianBitConverter();
            using (var endianReader = new EndianBinaryReader(converter, inStream))
            {
                Destination = new I2PDestination(endianReader);
                PrivateKey = endianReader.ReadBytesOrThrow(PrivateKeyLength);
                SigningPrivateKey = endianReader.ReadBytes(8192); //enough i think
            }
        }

        public I2PPrivateKey(string privateKeyString)
        {
            if(string.IsNullOrWhiteSpace(privateKeyString))
                throw new ArgumentNullException(
                    MyNameof.GetLocalVarName(() => privateKeyString));
            var data = I2PBase64Decode(privateKeyString);
            if(data.Length < 663)
                throw new ArgumentException("PrivateKey data too short");
            using (var stream = new MemoryStream(data,false))
            {
                LoadData(stream);
            }
        }
        // Copy from DataHelper class in I2P
        public static long ReadLong(Stream rawStream, int numBytes)
        {
            if (numBytes > 8)
                throw new ArgumentOutOfRangeException(
                    "readLong doesn't currently support reading numbers > 8 bytes [as thats bigger than java's long]");

            long rv = 0;
            for (int i = 0; i < numBytes; i++)
            {
                int cur = rawStream.ReadByte();
                // was DataFormatException
                if (cur == -1) throw new EndOfStreamException("EOF reading " + numBytes + " byte value");
                // we loop until we find a nonzero byte (or we reach the end)
                if (cur != 0)
                {
                    // ok, data found, now iterate through it to fill the rv
                    rv = cur & 0xff;
                    for (int j = i + 1; j < numBytes; j++)
                    {
                        rv <<= 8;
                        cur = rawStream.ReadByte();
                        // was DataFormatException
                        if (cur == -1)
                            throw new EndOfStreamException("EOF reading " + numBytes + " byte value");
                        rv |= cur & 0xff;
                    }
                    break;
                }
            }

            if (rv < 0)
                throw new InvalidOperationException("wtf, fromLong got a negative? " + rv + " numBytes=" + numBytes);
            return rv;
        }
        public static void WriteLong(Stream rawStream, int numBytes, long value) 
        {
            if (numBytes <= 0 || numBytes > 8)
                // probably got the args backwards
                throw new ArgumentOutOfRangeException("Bad byte count " + numBytes);
            if (value < 0)
                throw new ArgumentOutOfRangeException("Value is negative (" + value + ")");
            for (int i = (numBytes - 1) * 8; i >= 0; i -= 8) {
                byte cur = (byte) (value >> i);
                rawStream.WriteByte(cur);
            }
        }
        public static byte[] I2PBase64Decode(string b64String)
        {
            if(string.IsNullOrWhiteSpace(b64String))
                throw new ArgumentNullException(
                    MyNameof.GetLocalVarName(() => b64String));
            if(b64String.Length % 4 != 0)
                throw new ArgumentOutOfRangeException(
                    MyNameof.GetLocalVarName(() => b64String));
            return Convert.FromBase64String(
                b64String.Replace('~', '/').Replace('-', '+')
            );
        }

        public static string I2PBase64Encode(byte[] data)
        {
            if(data == null)
                throw new ArgumentNullException(
                    MyNameof.GetLocalVarName(() => data));
            return Convert.ToBase64String(data).Replace('/', '~').Replace('+', '-');
        }

        public string ToI2PBase64()
        {
            using (var ms = new MemoryStream())
            {
                var destinationBytes = I2PBase64Decode(Destination.ToI2PBase64());
                ms.Write(destinationBytes,0,destinationBytes.Length);
                ms.Write(PrivateKey,0,PrivateKeyLength);
                ms.Write(SigningPrivateKey,0,SigningPrivateKey.Length);
                return I2PBase64Encode(ms.ToArray());
            }
        }

        public void Dispose()
        {
            Destination = null;
            MiscFuncs.GetRandomBytes(PrivateKey);
            MiscFuncs.GetRandomBytes(SigningPrivateKey);
            PrivateKey = null;
            SigningPrivateKey = null;
        }
    }

    public class I2PDestination
    {
        public byte[] PublicKey;
        public byte[] SigningPublicKey;
        public int CertType;
        public int CertLength;
        public byte[] CertificatePayload;

        public string ToI2PBase64()
        {
            using (var outMs = new MemoryStream())
            {
                outMs.Write(PublicKey,0,PublicKeyLength);
                outMs.Write(SigningPublicKey,0,SigningPublicKeyLength);
                I2PPrivateKey.WriteLong(outMs,1,(long)CertType);
                I2PPrivateKey.WriteLong(outMs, 2, (long)CertLength);
                outMs.Write(CertificatePayload,0,CertLength);
                return I2PPrivateKey.I2PBase64Encode(outMs.ToArray());
            }
        }
        const int PublicKeyLength = 256;
        const int SigningPublicKeyLength = 128;
        private void LoadData(EndianBinaryReader inEndianReader)
        {
            PublicKey = inEndianReader.ReadBytesOrThrow(PublicKeyLength);
            SigningPublicKey = inEndianReader.ReadBytesOrThrow(SigningPublicKeyLength);
            CertType = (int)I2PPrivateKey.ReadLong(inEndianReader.BaseStream, 1);
            CertLength = (int)I2PPrivateKey.ReadLong(inEndianReader.BaseStream, 2);
            CertificatePayload = inEndianReader.ReadBytesOrThrow(CertLength);
        }

        public I2PDestination(
            EndianBinaryReader inEndianReader
        )
        {
            if (inEndianReader == null)
                throw new ArgumentNullException(
                    MyNameof.GetLocalVarName(() => inEndianReader));
            LoadData(inEndianReader);
        }

        public I2PDestination(
            byte[] data
        )
        {
            using (var ms = new MemoryStream(data))
            {
                var converter = new BigEndianBitConverter();
                using (var reader = new EndianBinaryReader(converter, ms))
                {
                    LoadData(reader);
                }
            }
        }
        public I2PDestination(
            string dest
        )
        {
            byte[] data = I2PPrivateKey.I2PBase64Decode(dest);
            using (var ms = new MemoryStream(data))
            {
                var converter = new BigEndianBitConverter();
                using (var reader = new EndianBinaryReader(converter, ms))
                {
                    LoadData(reader);
                }
            }
        }
    }

    public class SamHelperSettings : ICheckable 
    {
        public SamHelperSettings()
        {
        }

        public SamHelperSettings(SamHelperSettings copyFrom)
        {
            if(copyFrom == null)
                throw new ArgumentNullException(
                    MyNameof.GetLocalVarName(() => copyFrom));
            SamServerAddress = copyFrom.SamServerAddress;
            SamServerPort = copyFrom.SamServerPort;
            SessionPrivateKeys = copyFrom.SessionPrivateKeys;
            I2CpOptions = copyFrom.I2CpOptions;
            SessionId = copyFrom.SessionId;
        }

        public string SamServerAddress;
        public int SamServerPort;
        /**/
        public string SessionPrivateKeys = null;
        public string I2CpOptions = null;
        /**/
        public string SessionId = null;
        public void CheckMe()
        {
            if (SamServerAddress == null)
                throw new ArgumentNullException(
                    this.MyNameOfProperty(e => e.SamServerAddress));
            if (SamServerPort < 1 || SamServerPort > 65535)
                throw new ArgumentOutOfRangeException(
                    this.MyNameOfProperty(e => e.SamServerPort));
        }
    }

    public interface ISamHelper : IMyAsyncDisposable
    {
        SamHelper.SamSession Session { get; }
        Subject<DatagramDataReceivedArgs> DatagramDataReceived { get; }
        Task SendDatagram(
            string destination,
            byte[] data
        );
        bool HandleDatagrams { get; set; }
    }

    public class SamHelper : ISamHelper
    {
        public readonly Subject<object> IoExceptionThrown 
            = new Subject<object>();
        public static bool IsDestinationStringValid(
            string destination
        )
        {
            if (string.IsNullOrWhiteSpace(destination))
                return false;
            try
            {
                var dest = new I2PDestination(destination);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private SamHelper()
        {
        }

        public enum SamHelperExcs
        {
            WrongConnectionStatus,
            NetStreamCannotWrite,
            NetStreamCannotRead
        }
        private SamReader _samReader;
        private NetworkStream _samStream;
        private TcpClient _samTcpClient;
        private SamSession _samSession;
        private readonly SemaphoreSlim _writeBufferLockSem = new SemaphoreSlim(1);
        private async Task WriteBuffer(byte[] writeBuffer)
        {
            using (await _writeBufferLockSem.GetDisposable().ConfigureAwait(false))
            {
                try
                {
                    await _samStream.WriteAsync(
                        writeBuffer,
                        0,
                        writeBuffer.Length
                    ).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    IoExceptionThrown.OnNext(null);
                    throw;
                }
            }
        }
        public enum ECreateInstanceErrCodes
        {
            Unknown,
            ConnectTcpTimeout,
            ConnectTcp,
            HelloReplyError,
            HelloReplyTimeoutExceed,
            CreateSessionError //EnumException<ECreateSessionErrorCodes>
        }
        //ECreateInstanceErrCodes
        public static async Task<SamHelper> CreateInstance(
            SamHelperSettings settings,
            CancellationToken cancellationToken
        )
        {
            if(settings == null)
                throw new ArgumentNullException(
                    MyNameof.GetLocalVarName(() => settings));
            if(cancellationToken == null)
                throw new ArgumentNullException(
                    MyNameof.GetLocalVarName(() => cancellationToken));
            settings.CheckMe();
            var result = new SamHelper();
            result._samTcpClient = new TcpClient();
            try
            {
                await result._samTcpClient.ConnectAsync(
                    settings.SamServerAddress,
                    settings.SamServerPort
                ).ThrowIfCancelled(cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException timeExc)
            {
                throw new EnumException<ECreateInstanceErrCodes>(
                    ECreateInstanceErrCodes.ConnectTcpTimeout,
                    innerException: timeExc
                );
            }
            catch (Exception exc)
            {
                throw new EnumException<ECreateInstanceErrCodes>(
                    ECreateInstanceErrCodes.ConnectTcp,
                    innerException: exc
                );
            }
            try
            {
                result._samStream = result._samTcpClient.GetStream();
                try
                {
                    if (!result._samStream.CanWrite)
                    {
                        throw new EnumException<SamHelperExcs>(
                            SamHelperExcs.NetStreamCannotWrite
                            );
                    }
                    if (!result._samStream.CanRead)
                    {
                        throw new EnumException<SamHelperExcs>(
                            SamHelperExcs.NetStreamCannotRead
                            );
                    }

                    result._samReader = new SamReader(result._samStream);
                    result._subscriptions.Add(
                        result._samReader.IoExceptionThrown.ObserveOn(TaskPoolScheduler.Default).Subscribe(
                            i => result.IoExceptionThrown.OnNext(null)
                        )
                    );
                    result._subscriptions.Add(
                        result._samReader.DatagramDataReceived.ObserveOn(TaskPoolScheduler.Default).Subscribe(
                            result.SamReaderOnDatagramDataReceived
                        )
                    );
                    /*******************/
                    HelloReplyReceivedArgs helloAnswer;
                    try
                    {
                        var writeBuffer = SamBridgeCommandBuilder.HelloReply();
                        var helloAnswerTask =
                            result._samReader.HelloReplyReceived
                                .FirstAsync()
                                .ToTask(cancellationToken);
                        await result.WriteBuffer(writeBuffer).ConfigureAwait(false);
                        helloAnswer = await helloAnswerTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        //timeout 
                        result._samTcpClient.Close();
                        throw new EnumException<ECreateInstanceErrCodes>(
                            ECreateInstanceErrCodes.HelloReplyTimeoutExceed
                        );
                    }
                    if (!helloAnswer.Ok)
                    {
                        result._samTcpClient.Close();
                        throw new EnumException<ECreateInstanceErrCodes>(
                            ECreateInstanceErrCodes.HelloReplyError,
                            tag: helloAnswer
                        );
                    }
                    result._stateHelper.SetInitializedState();
                    try
                    {
                        var sessionName = settings.SessionId ??
                                          Guid.NewGuid().ToString().Replace("-", "").Substring(0, 15);
                        result._samSession = await result.CreateSession(
                            sessionName,
                            cancellationToken,
                            settings.SessionPrivateKeys,
                            settings.I2CpOptions
                        ).ConfigureAwait(false);
                    }
                    catch (EnumException<ECreateSessionErrorCodes> createSessionExc)
                    {
                        throw new EnumException<ECreateInstanceErrCodes>(
                            ECreateInstanceErrCodes.CreateSessionError,
                            innerException: createSessionExc
                        );
                    }
                    return result;
                }
                catch
                {
                    result._samStream.Close();
                    throw;
                }
            }
            catch
            {
                result._samTcpClient.Close();
                throw;
            }
        }

        private readonly Subject<DatagramDataReceivedArgs> _datagramDataReceived
            = new Subject<DatagramDataReceivedArgs>();
        private void SamReaderOnDatagramDataReceived(
            DatagramDataReceivedArgs datagramDataReceivedArgs
        )
        {
            if(_handleDatagrams)
                _datagramDataReceived.OnNext(datagramDataReceivedArgs);
        }

        public const int DisconnectReadingTimeout = 10;

        public class SamSession
        {
            public string Destination => new I2PPrivateKey(PrivateKey).Destination.ToI2PBase64();
            public string PrivateKey = string.Empty;
            public SamBridgeCommandBuilder.SessionStyle SessionStyle 
                = SamBridgeCommandBuilder.SessionStyle.Stream;
            public string SessionId = string.Empty;
        }
        public enum ECreateSessionErrorCodes
        {
            DuplicatedId,
            DuplicatedDest,
            InvalidKey,
            I2PError,
            CreateSessionTimeout,
            UnknownError
        }

        private async Task<SamSession> CreateSession(
            string id, 
            CancellationToken cancellationToken,
            string destinationPrivateKey = null,
            string i2CpOptions = null
        )
        {
            try
            {
                if (id == null)
                    throw new ArgumentNullException("id");
                if (!_samStream.CanWrite)
                    throw new EnumException<SamHelperExcs>(
                        SamHelperExcs.NetStreamCannotWrite, 
                        tag: _samStream
                    );
                var sessionCreateTask 
                    = _samReader.SessionStatusReceived
                        .FirstAsync().ToTask(cancellationToken);
                var writeBuffer = SamBridgeCommandBuilder.SessionCreate(
                    SamBridgeCommandBuilder.SessionStyle.Datagram, 
                    id, 
                    destinationPrivateKey,
                    i2CpOptions
                );
                await WriteBuffer(writeBuffer).ConfigureAwait(false);
                SessionStatusReceivedArgs sessionStatusReply = null;
                try
                {
                    sessionStatusReply = await sessionCreateTask.ConfigureAwait(false);
                    if(sessionStatusReply == null)
                        throw new ArgumentNullException(
                            MyNameof.GetLocalVarName(() => sessionStatusReply));
                }
                catch (OperationCanceledException)
                {
                    throw new EnumException<ECreateSessionErrorCodes>(
                        ECreateSessionErrorCodes.CreateSessionTimeout
                    );
                }
                if (
                    sessionStatusReply.SessionStatusResult 
                        == SamBridgeMessages.SESSION_STATUS_OK
                )
                {
                    _log.Trace(
                        string.Format(
                            "Session created {0}", 
                            sessionStatusReply.DestinationWithPrivateKey
                                .Substring(0,20)
                        )
                    );
                    return new SamSession
                    {
                        PrivateKey 
                            = sessionStatusReply.DestinationWithPrivateKey,
                        SessionId = id,
                        SessionStyle = SamBridgeCommandBuilder.SessionStyle.Datagram
                    };
                }
                if (
                    sessionStatusReply.SessionStatusResult 
                    == SamBridgeMessages.SESSION_STATUS_DUPLICATE_ID
                )
                {
                    throw new EnumException<ECreateSessionErrorCodes>(
                        ECreateSessionErrorCodes.DuplicatedId,
                        tag: sessionStatusReply
                    );
                }
                if (
                    sessionStatusReply.SessionStatusResult 
                    == SamBridgeMessages.SESSION_STATUS_DUPLICATE_DEST
                )
                {
                    throw new EnumException<ECreateSessionErrorCodes>(
                        ECreateSessionErrorCodes.DuplicatedDest,
                        tag: sessionStatusReply
                    );
                }
                if (
                    sessionStatusReply.SessionStatusResult 
                    == SamBridgeMessages.SESSION_STATUS_INVALID_KEY
                )
                {
                    throw new EnumException<ECreateSessionErrorCodes>(
                        ECreateSessionErrorCodes.InvalidKey, 
                        tag: sessionStatusReply
                    );
                }
                if (
                    sessionStatusReply.SessionStatusResult
                    == SamBridgeMessages.SESSION_STATUS_I2P_ERROR
                    )
                {
                    throw new EnumException<ECreateSessionErrorCodes>(
                        ECreateSessionErrorCodes.I2PError,
                        message: sessionStatusReply.Message,
                        tag: sessionStatusReply
                        );
                }
                else
                {
                    _log.Error(
                        "CreateSession unknown error '{0}'",
                        sessionStatusReply.WriteObjectToJson()
                    );
                    throw new EnumException<ECreateSessionErrorCodes>(
                        ECreateSessionErrorCodes.UnknownError,
                        tag: sessionStatusReply
                    );
                }
            }
            catch (EnumException<ECreateSessionErrorCodes>)
            {
                throw;
            }
            catch (Exception exc)
            {
                throw new EnumException<ECreateSessionErrorCodes>(
                    ECreateSessionErrorCodes.UnknownError, 
                    innerException: exc
                );
            }
        }
        public enum SendDatagramExcs
        {
            UnknownError
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="destination"></param>
        /// <param name="data">max size 10000</param>
        public async Task SendDatagram(
            string destination, 
            byte[] data
        )
        {
            try
            {
                if (string.IsNullOrWhiteSpace(destination))
                    throw new ArgumentNullException(
                        MyNameof.GetLocalVarName(() => destination));
                if (data == null)
                    throw new ArgumentNullException(
                        MyNameof.GetLocalVarName(() => data));
                if (data.Length <= 0 || data.Length > 10000)
                    throw new ArgumentOutOfRangeException(
                        MyNameof.GetLocalVarName(() => data)
                            +"."+MyNameof.GetLocalVarName(() => data.Length), 
                        "data length should be 0 < length <= 10000");
                if (_samStream == null)
                    throw new InvalidOperationException(
                        this.MyNameOfProperty(e => e._samStream)
                    );
                if (!_samStream.CanWrite)
                    throw new InvalidOperationException(
                        "_samStream can't write"
                    );
                var writeBuffer = SamBridgeCommandBuilder.DatagramSend(
                    destination, 
                    data
                );
                await WriteBuffer(writeBuffer).ConfigureAwait(false);
            }
            catch (EnumException<SendDatagramExcs>)
            {
                throw;
            }
            catch (Exception exc)
            {
                throw new EnumException<SendDatagramExcs>(
                    SendDatagramExcs.UnknownError, 
                    innerException: exc
                );
            }
        }

        private bool _handleDatagrams = true;
        public bool HandleDatagrams
        {
            get { 
                using(_stateHelper.GetFuncWrapper())
                {
                    return _handleDatagrams;
                } 
            }
            set
            {
                using(_stateHelper.GetFuncWrapper())
                {
                    _handleDatagrams = value;
                }
            }
        }

        private static readonly Logger _log 
            = LogManager.GetCurrentClassLogger();
        private readonly DisposableObjectStateHelper _stateHelper 
            = new DisposableObjectStateHelper("SamHelper");
        private readonly List<IDisposable> _subscriptions 
            = new List<IDisposable>(); 
        public async Task MyDisposeAsync()
        {
            await _stateHelper.MyDisposeAsync().ConfigureAwait(false);
            foreach (IDisposable subscription in _subscriptions)
            {
                subscription.Dispose();
            }
            _subscriptions.Clear();
            await _samReader.MyDisposeAsync().ConfigureAwait(false);
            _samStream.Close();
            _samTcpClient.Close();
        }

        public SamSession Session
        {
            get
            {
                using(_stateHelper.GetFuncWrapper())
                    return _samSession;
            }
        }

        public Subject<DatagramDataReceivedArgs> DatagramDataReceived
        {
            get { 
                using(_stateHelper.GetFuncWrapper())
                    return _datagramDataReceived; 
            }
        }
    }
}
