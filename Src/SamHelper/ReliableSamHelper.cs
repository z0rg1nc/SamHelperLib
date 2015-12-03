using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.MiscUtil.Conversion;
using BtmI2p.MiscUtils;
using BtmI2p.ObjectStateLib;

namespace BtmI2p.SamHelper
{
    public class ReliableSamHelperSettings
    {
        public TimeSpan CleanInMessagesOlderThan = TimeSpan.FromMinutes(15.0d);
        public int MaxMesageLength = 500000;
        public uint MaxBlockSize = 9000;
        public int ProtocolVersion = 1;
        public int HandshakeTimeoutSeconds = 7;
        public int HandshakeAttemptCount = 4;
        public int WindowSize = 10;
        // blocks count controlled simultaneously
        public int ConfirmationTimeoutSeconds = 45;
        public int ConfirmationOneBlockTimeoutSeconds = 15;
    }
    public partial class ReliableSamHelper : IMyAsyncDisposable
    {
        private readonly Guid _reliableSamHelperGuid = Guid.NewGuid();
        private readonly ISamHelper _samHelper;
        private readonly ReliableSamHelperSettings _settings;
        public ReliableSamHelper(
            ISamHelper samHelper, 
            ReliableSamHelperSettings settings
        )
        {
            if(samHelper == null)
                throw new ArgumentNullException(
                    MyNameof.GetLocalVarName(() => samHelper));
            if(settings == null)
                throw new ArgumentNullException(
                    MyNameof.GetLocalVarName(() => settings));
            _samHelper = samHelper;
            _settings = settings;
            var rng = new Random(DateTime.UtcNow.Millisecond);
            _nextOutMessageId = (uint) rng.Next(int.MaxValue);
            RawDatagramReceived.ObserveOn(TaskPoolScheduler.Default).Subscribe(args => _log.Trace(
                "{5} recv raw datagram of {0} bytes " +
                    "from {1} id={2}, rid={3}, kind={6}, ansiS='{4}'",
                args.Data.Length,
                args.Destination.Substring(0, 20),
                args.MessageId,
                args.ReplyToMessageId,
                Encoding.ASCII.GetString(args.Data),
                _reliableSamHelperGuid.ToString().Substring(0,5),
                args.MessageKind
            ));
            ReliableMessageReceived.ObserveOn(TaskPoolScheduler.Default).Subscribe(args => 
                _log.Trace(
                    "{4} recv reliable message of {0} bytes " +
                        "from {1} id={2}, rid={3}, kind={5}",
                    args.Data.Length,
                    args.Destination.Substring(0, 20),
                    args.MessageId,
                    args.ReplyToMessageId,
                    _reliableSamHelperGuid.ToString().Substring(0, 5),
                    args.MessageKind
                )
            );
            _subscriptions.AddRange(
                new[]
                {
                    _samHelper.DatagramDataReceived.ObserveOn(TaskPoolScheduler.Default).Subscribe(
                        SamHelperOnDatagramDataReceived
                    ),
                    GetProtocolVersionReceived.ObserveOn(TaskPoolScheduler.Default).Subscribe(
                        OnGetProtocolVersionReceived
                    ),
                    HandshakeStartReceived.ObserveOn(TaskPoolScheduler.Default).Subscribe(
                        OnHandshakeStartReceived
                    ),
                    GetMessageStatusReceived.ObserveOn(TaskPoolScheduler.Default).Subscribe(
                        OnGetMessageStatusReceived
                    ),
                    BlockSendReceived.ObserveOn(TaskPoolScheduler.Default).Subscribe(
                        OnBlockSendReceived
                    ),
                    Observable.Interval(TimeSpan.FromSeconds(20.0d)).ObserveOn(TaskPoolScheduler.Default).Subscribe(
                        IntervalCleanupAction
                    )
                }
            );
            _stateHelper.SetInitializedState();
        }
        private readonly List<IDisposable> _subscriptions
            = new List<IDisposable>();
        private readonly SortedSet<string> _dropDestinations 
            = new SortedSet<string>(); 
        private readonly LittleEndianBitConverter _littleEndianConverter 
            = new LittleEndianBitConverter();
        
        public enum ReliableDatagramCodes : byte
        {
            RawDatagram = 0x00,         // => 
            HandshakeStart = 0x01,      // => reply with MessageStatus
            MessageStatus = 0x02,       // <=
            BlockSend = 0x03,           // => reply with BlockConfirmation
            BlockConfirmation = 0x04,   // <=
            GetMessageStatus = 0x05,    // => reply with MessageStatus
            GetProtocolVersion = 0x06,  // => reply with ProtocolVersion
            ProtocolVersion = 0x07      // <=
        }
        public class DestinationInfo
        {
            public readonly SemaphoreSlim OutMessagesDbLockSem 
                = new SemaphoreSlim(1);
            public Dictionary<uint, OutMessageInfo> OutMessagesDb
                = new Dictionary<uint, OutMessageInfo>();
            public readonly SemaphoreSlim InMessagesDbLockSem
                = new SemaphoreSlim(1);
            public Dictionary<uint, InMessageInfo> InMessagesDb
                = new Dictionary<uint, InMessageInfo>();
        }
        private readonly SemaphoreSlim _destinationDbLockSem 
            = new SemaphoreSlim(1);
        private readonly SortedDictionary<string, DestinationInfo> 
            _destinationDb
                = new SortedDictionary<string, DestinationInfo>();
        private uint _nextOutMessageId;
        private readonly SemaphoreSlim _nextOutMessageIdLockSem 
            = new SemaphoreSlim(1);
        public async Task<uint> GetNextOutMessageId()
        {
            using (
                await _nextOutMessageIdLockSem.GetDisposable().ConfigureAwait(false)
            )
            {
                if (_nextOutMessageId == 0)
                    _nextOutMessageId++;
                return _nextOutMessageId++;
            }
        }
        private readonly DisposableObjectStateHelper _stateHelper
            = new DisposableObjectStateHelper("ReliableSamHelper");
        private  readonly CancellationTokenSource _cts = new CancellationTokenSource();
        public async Task MyDisposeAsync()
        {
            _cts.Cancel();
            await _stateHelper.MyDisposeAsync().ConfigureAwait(false);
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }
            _subscriptions.Clear();
            _cts.Dispose();
        }
        private readonly SemaphoreSlim _intervalCleanupActionLockSem = new SemaphoreSlim(1);
        private async void IntervalCleanupAction(long i)
        {
            var curMethodName = this.MyNameOfMethod(e => e.IntervalCleanupAction(0));
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    using (await _intervalCleanupActionLockSem.GetDisposable().ConfigureAwait(false))
                    {
                        var nowTime = DateTime.UtcNow;
                        using (await _destinationDbLockSem.GetDisposable().ConfigureAwait(false))
                        {
                            foreach (KeyValuePair<string, DestinationInfo> destinationInfo in _destinationDb)
                            {
                                using (await destinationInfo.Value.InMessagesDbLockSem.GetDisposable().ConfigureAwait(false))
                                {
                                    var inMessageIdsToRemove = 
                                        (
                                            from inMessageInfo 
                                            in destinationInfo.Value.InMessagesDb 
                                            where nowTime > inMessageInfo.Value.CreatedTime 
                                                + _settings.CleanInMessagesOlderThan 
                                            select inMessageInfo.Key
                                        ).ToList();
                                    foreach (uint messageId in inMessageIdsToRemove)
                                    {
                                        destinationInfo.Value.InMessagesDb.Remove(messageId);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (WrongDisposableObjectStateException)
            {
            }
            catch (Exception exc)
            {
                _log.Error("{0} unexpected error '{1}'", curMethodName, exc.ToString());
            }
        }
    }
}
