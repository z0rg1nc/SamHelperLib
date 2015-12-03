using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.MiscUtil.Conversion;
using BtmI2p.MiscUtil.IO;
using BtmI2p.MiscUtils;
using BtmI2p.ObjectStateLib;
using NLog;
using Xunit;

namespace BtmI2p.SamHelper
{
    public enum OutMessageStatus
    {
        Start,
        HandshakeOk,
        FirstBlockSent,
        AllBlocksSent,
        AllBlocksConfirmed
    }
    public class OutMessageProgressInfo
    {
        public OutMessageStatus CurrentStatus;
        public int HandshakeAttemptNo;
        public uint BlocksSent;
        public uint BlocksConfirmed;
        public uint TotalBlockCount;
        public override string ToString()
        {
            return string.Format(
                "Status {0} HandshakeAttemptNo {1} Blocks {2}/{3} of {4}",
                CurrentStatus,
                HandshakeAttemptNo,
                BlocksSent,
                BlocksConfirmed,
                TotalBlockCount
            );
        }
    }
    public class OutMessageInfo
    {
        private OutMessageProgressInfo GetProgressInfo()
        {
            return new OutMessageProgressInfo()
            {
                CurrentStatus = Status,
                HandshakeAttemptNo = HandshakeAttemptNo,
                BlocksSent = BlocksSentCount,
                BlocksConfirmed = BlocksConfirmedCount,
                TotalBlockCount = BlockCount
            };
        }
        private readonly IProgress<OutMessageProgressInfo> _progress;

        public OutMessageInfo(
            byte[] data,
            uint replyToMessageId,
            uint blockSize,
            IProgress<OutMessageProgressInfo> progress = null
        )
        {
            ReplyToMessageId = replyToMessageId;
            Data = ReliableSamHelper.SplitData(data, (int)blockSize);
            BlockSize = blockSize;
            BlockCount = ReliableSamHelper.GetBlocksCount(
                (uint)data.Length, 
                blockSize
            );
            BlockConfirmed = new bool[BlockCount];
            BlocksSent = new bool[BlockCount];
            Status = OutMessageStatus.Start;
            _progress = progress;
        }
        

        public OutMessageStatus Status
        {
            get { return _status; }
            set
            {
                if (_status != value)
                {
                    _status = value;
                    if (_progress != null)
                        _progress.Report(GetProgressInfo());
                }
            }
        }

        public List<byte[]> Data;
        public uint ReplyToMessageId;
        public uint BlockSize;
        public bool[] BlockConfirmed;
        private readonly SemaphoreSlim _blockConfirmedLockSem = new SemaphoreSlim(1);
        public bool[] BlocksSent;
        private readonly SemaphoreSlim _blocksSentLockSem = new SemaphoreSlim(1);
        public uint BlockCount = 0;

        public uint BlocksSentCount
        {
            get { return _blocksSentCount; }
            set
            {
                _blocksSentCount = value;
                if (_progress != null)
                    _progress.Report(GetProgressInfo());
            }
        }

        public uint BlocksConfirmedCount
        {
            get { return _blocksConfirmedCount; }
            set
            {
                _blocksConfirmedCount = value;
                if (_progress != null)
                    _progress.Report(GetProgressInfo());
            }
        }

        public int HandshakeAttemptNo
        {
            get { return _handshakeAttemptNo; }
            set
            {
                _handshakeAttemptNo = value;
                if (_progress != null)
                    _progress.Report(GetProgressInfo());
            }
        }

        private OutMessageStatus _status = OutMessageStatus.Start;
        private int _handshakeAttemptNo = 0;
        private uint _blocksConfirmedCount = 0;
        private uint _blocksSentCount;

        public async Task ConfirmBlock(uint blockId)
        {
            if (blockId >= BlockCount)
                throw new ArgumentOutOfRangeException("blockId");
            using (await _blockConfirmedLockSem.GetDisposable().ConfigureAwait(false))
            {
                if (!BlockConfirmed[blockId])
                {
                    BlockConfirmed[blockId] = true;
                    BlocksConfirmedCount++;
                    if (BlocksConfirmedCount == BlockCount)
                    {
                        Status = OutMessageStatus.AllBlocksConfirmed;
                    }
                }
            }
        }

        public async Task SendBlock(uint blockId)
        {
            if (blockId >= BlockCount)
                throw new ArgumentOutOfRangeException("blockId");
            if (Status == OutMessageStatus.HandshakeOk)
            {
                Status = OutMessageStatus.FirstBlockSent;
            }
            using (await _blocksSentLockSem.GetDisposable().ConfigureAwait(false))
            {
                if (!BlocksSent[blockId])
                {
                    BlocksSentCount++;
                    BlocksSent[blockId] = true;
                    if (
                        Status == OutMessageStatus.FirstBlockSent &&
                        BlocksSentCount == BlockCount
                    )
                    {
                        Status = OutMessageStatus.AllBlocksSent;
                    }
                }
            }
        }
    }

    public partial class ReliableSamHelper
    {
        private byte[] GetMessageHash(
            uint messageId,
            uint replyToMessageId,
            byte messageKind,
            byte[] data
            )
        {
            using (
                var msBuffer = new MemoryStream(
                    + sizeof (uint)
                    + sizeof (uint)
                    + sizeof (byte)
                    + 32
                )
            )
            {
                using (
                    var writer
                        = new EndianBinaryWriter(
                            _littleEndianConverter,
                            msBuffer
                        )
                )
                {
                    writer.Write(messageId);
                    writer.Write(replyToMessageId);
                    writer.Write(messageKind);
                    using (var hashAlg = new SHA256Managed())
                    {
                        writer.Write(hashAlg.ComputeHash(data));
                    }
                    writer.Flush();
                    using (var hashAlg = new SHA256Managed())
                    {
                        return hashAlg.ComputeHash(msBuffer.ToArray());
                    }
                }
            }
        }

        private static readonly Logger _log
            = LogManager.GetCurrentClassLogger();
        public async Task<uint> SendRawDatagram(
            string destination, 
            byte[] data, 
            uint messageId = 0, 
            uint replyToMessageId = 0,
            byte messageKind = 0
        )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                if (messageId == 0)
                    messageId = await GetNextOutMessageId().ConfigureAwait(false);
                using (
                    var msBuffer = new MemoryStream(
                        sizeof (byte)
                        + sizeof (uint)
                        + sizeof (uint)
                        + sizeof (byte)
                        + data.Length
                        + 32
                    )
                )
                {
                    using (
                        var writer 
                            = new EndianBinaryWriter(
                                _littleEndianConverter, 
                                msBuffer
                            )
                    )
                    {
                        writer.Write((byte) ReliableDatagramCodes.RawDatagram);
                        writer.Write(messageId);
                        writer.Write(replyToMessageId);
                        writer.Write(messageKind);
                        writer.Write(GetMessageHash(messageId, replyToMessageId, messageKind, data));
                        writer.Write(data);
                        writer.Flush();
                        await _samHelper.SendDatagram(
                            destination,
                            msBuffer.ToArray()
                        ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                        _log.Trace(
                            string.Format(
                                "{5}, send raw datagram of {0} bytes " +
                                    "to {1} id={2},rid={3},kind={6},ansiS='{4}'",
                                data.Length,
                                destination.Substring(0, 20),
                                messageId,
                                replyToMessageId,
                                Encoding.ASCII.GetString(data),
                                _reliableSamHelperGuid.ToString().Substring(0, 5),
                                messageKind
                            )
                        );
                        return messageId;
                    }
                }
            }
        }
        private async Task SendHandshakeStart(
            string destination, 
            uint messageId, 
            uint replyToMessageId, 
            uint totalSize, 
            uint blockSize,
            byte[] messageHash,
            byte[] firstBlockData,
            byte messageKind = 0
        )
        {
            if(messageHash == null)
                throw new ArgumentNullException(
                    MyNameof.GetLocalVarName(() => messageHash));
            if(messageHash.Length != 32)
                throw new ArgumentOutOfRangeException(
                    MyNameof.GetLocalVarName(() => messageHash));
            using (
                var ms = new MemoryStream(
                    sizeof(byte) 
                    + sizeof(uint) 
                    + sizeof(uint) 
                    + sizeof(byte)
                    + sizeof(uint) 
                    + sizeof(uint)
                    + 32
                    + sizeof(uint)
                    + firstBlockData.Length
                )
            )
            {
                using (
                    var binaryWriter 
                        = new EndianBinaryWriter(
                            _littleEndianConverter, 
                            ms
                        )
                )
                {
                    binaryWriter.Write(
                        (byte)ReliableDatagramCodes.HandshakeStart
                    );
                    binaryWriter.Write(messageId);
                    binaryWriter.Write(replyToMessageId);
                    binaryWriter.Write(messageKind);
                    binaryWriter.Write(totalSize);
                    binaryWriter.Write(blockSize);
                    binaryWriter.Write(messageHash);
                    binaryWriter.Write((uint)firstBlockData.Length);
                    binaryWriter.Write(firstBlockData);
                    binaryWriter.Flush();
                    await _samHelper.SendDatagram(
                        destination,
                        ms.ToArray()
                    ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                }
            }
        }
        private async Task SendMessageStatus(
            string destination,
            uint messageId,
            MessageStatusArgs.MessageStatusCode statusCode,
            uint blocksReceived
            )
        {
            if (!SamHelper.IsDestinationStringValid(destination))
                throw new ArgumentOutOfRangeException("destination");
            using (
                var ms = new MemoryStream(
                    sizeof(byte) 
                    + sizeof(uint) 
                    + sizeof(byte) 
                    + sizeof(uint)
                )
            )
            {
                using (
                    var binaryWriter 
                        = new EndianBinaryWriter(
                            _littleEndianConverter, 
                            ms
                        )
                )
                {
                    binaryWriter.Write(
                        (byte)ReliableDatagramCodes.MessageStatus
                    );
                    binaryWriter.Write(messageId);
                    binaryWriter.Write((byte)statusCode);
                    binaryWriter.Write(blocksReceived);
                    await _samHelper.SendDatagram(
                        destination,
                        ms.ToArray()
                    ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                }
            }
        }
        private async Task SendBlock(
            string destination, 
            uint messageId, 
            uint blockId
        )
        {
            if (string.IsNullOrWhiteSpace(destination))
                throw new ArgumentNullException("destination");
            if (!SamHelper.IsDestinationStringValid(destination))
                throw new ArgumentOutOfRangeException("destination");
            DestinationInfo destinationInfo;
            using (await _destinationDbLockSem.GetDisposable().ConfigureAwait(false))
            {
                if (!_destinationDb.ContainsKey(destination))
                    throw new ArgumentException("destination");
                destinationInfo = _destinationDb[destination];
            }
            OutMessageInfo outMessageInfo;
            using (await destinationInfo.OutMessagesDbLockSem.GetDisposable().ConfigureAwait(false))
            {
                if (!destinationInfo.OutMessagesDb.ContainsKey(messageId))
                    throw new ArgumentOutOfRangeException("messageId");
                outMessageInfo = destinationInfo.OutMessagesDb[messageId];
            }
            if (blockId >= outMessageInfo.Data.Count)
                throw new ArgumentOutOfRangeException("blockId");
            var dataToSend = outMessageInfo.Data[(int)blockId];
            using (
                var ms = new MemoryStream(
                    sizeof(byte) 
                    + sizeof(uint)
                    + sizeof(uint) 
                    + dataToSend.Length
                )
            )
            {
                using (
                    var binaryWriter 
                        = new EndianBinaryWriter(
                            _littleEndianConverter, 
                            ms
                        )
                )
                {
                    binaryWriter.Write(
                        (byte)ReliableDatagramCodes.BlockSend
                    );
                    binaryWriter.Write(messageId);
                    binaryWriter.Write(blockId);
                    binaryWriter.Write(dataToSend);
                    await _samHelper.SendDatagram(
                        destination,
                        ms.ToArray()
                    ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                }
            }
        }
        public static uint GetBlocksCount(uint dataSize, uint blockSize)
        {
            uint blocksCount = dataSize / blockSize;
            if (dataSize % blockSize != 0)
                blocksCount++;
            return blocksCount;
        }
        public static List<byte[]> SplitData(
            byte[] data,
            int blockSize
        )
        {
            if (data == null || data.Length == 0)
                throw new ArgumentNullException();
            if (blockSize <= 0)
                throw new ArgumentOutOfRangeException();
            uint blockCount = GetBlocksCount((uint)data.Length, (uint)blockSize);
            var result = new List<byte[]>();
            var inStream = new MemoryStream(data);
            var littleEndianConverter = new LittleEndianBitConverter();
            var binaryReader = new EndianBinaryReader(
                littleEndianConverter, 
                inStream
            );
            for (int i = 0; i < blockCount; i++)
            {
                var elem = binaryReader.ReadBytes(blockSize);
                result.Add(elem);
            }
            return result;
        }
        public enum SendReliableMessageExcs
        {
            ExistMessageId,
            HandshakeError,
            HandshakeTimeout,
            SendBlocksTimeout,
            UnexpectedError
        }

        public async Task<uint> SendReliableMessage(
            string destination,
            byte[] data,
            IProgress<OutMessageProgressInfo> progress = null,
            uint messageId = 0,
            uint replyToMessageId = 0,
            byte messageKind = 0,
            CancellationToken token = default(CancellationToken)
        )
        {
            using (_stateHelper.GetFuncWrapper())
            {
                try
                {
                    Assert.True(SamHelper.IsDestinationStringValid(destination));
                    Assert.NotNull(data);
                    Assert.InRange(data.Length, 1, _settings.MaxMesageLength);
                    if (messageId == 0)
                        messageId = await GetNextOutMessageId().ConfigureAwait(false);
                    DestinationInfo destinationInfo;
                    using (await _destinationDbLockSem.GetDisposable(token: token).ConfigureAwait(false))
                    {
                        if (!_destinationDb.ContainsKey(destination))
                            _destinationDb.Add(destination, new DestinationInfo());
                        destinationInfo = _destinationDb[destination];
                    }
                    OutMessageInfo outMessageInfo;
                    using (await destinationInfo.OutMessagesDbLockSem.GetDisposable(token: token).ConfigureAwait(false))
                    {
                        if (
                            destinationInfo.OutMessagesDb.ContainsKey(messageId)
                        )
                            throw new EnumException<SendReliableMessageExcs>(
                                SendReliableMessageExcs.ExistMessageId
                            );
                        outMessageInfo = new OutMessageInfo(
                            data,
                            replyToMessageId,
                            _settings.MaxBlockSize,
                            progress
                        );
                        destinationInfo.OutMessagesDb.Add(messageId, outMessageInfo);
                    }
                    try
                    {
                        uint blocksCount = outMessageInfo.BlockCount;
                        /*
                         * Handshake
                         */
                        using (
                            var compositeTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                                _cts.Token,
                                token
                            )
                        )
                        {
                            try
                            {
                                var messageStatusTask =
                                    MessageStatusReceived
                                        .Where(
                                            x =>
                                                x.Destination == destination
                                                && x.MessageId == messageId
                                        )
                                        .FirstAsync()
                                        .ToTask(compositeTokenSource.Token);
                                byte[] messageHash = GetMessageHash(
                                    messageId,
                                    replyToMessageId,
                                    messageKind,
                                    data
                                );
                                for (
                                    outMessageInfo.HandshakeAttemptNo = 0;
                                    outMessageInfo.HandshakeAttemptNo
                                        <= _settings.HandshakeAttemptCount;
                                    outMessageInfo.HandshakeAttemptNo++
                                )
                                {
                                    if (
                                        outMessageInfo.HandshakeAttemptNo
                                        == _settings.HandshakeAttemptCount
                                    )
                                        throw new EnumException<SendReliableMessageExcs>(
                                            SendReliableMessageExcs.HandshakeTimeout
                                        );
                                    await SendHandshakeStart(
                                        destination,
                                        messageId,
                                        replyToMessageId,
                                        (uint)data.Length,
                                        outMessageInfo.BlockSize,
                                        messageHash,
                                        outMessageInfo.Data[0],
                                        messageKind
                                    ).ConfigureAwait(false);
                                    var waitTasks = await Task.WhenAny(
                                        messageStatusTask,
                                        Task.Delay(
                                            TimeSpan.FromSeconds(
                                                _settings.HandshakeTimeoutSeconds
                                            ),
                                            compositeTokenSource.Token
                                        )
                                    ).ConfigureAwait(false);
                                    if (waitTasks == messageStatusTask)
                                    {
                                        var messageStatus = await messageStatusTask.ConfigureAwait(false);
                                        if (
                                            !(
                                                messageStatus.StatusCode ==
                                                MessageStatusArgs.MessageStatusCode.HandshakeOk
                                                ||
                                                (
                                                    messageStatus.StatusCode ==
                                                    MessageStatusArgs.MessageStatusCode.AllBlocksReceived
                                                    && outMessageInfo.BlockCount == 1
                                                    )
                                                )
                                            )
                                        {
                                            throw new EnumException<SendReliableMessageExcs>(
                                                SendReliableMessageExcs.HandshakeError,
                                                messageStatus.WriteObjectToJson()
                                            );
                                        }
                                        break;
                                    }
                                }
                                outMessageInfo.Status = OutMessageStatus.HandshakeOk;
                                outMessageInfo.BlockConfirmed[0] = true;
                                if (outMessageInfo.BlockCount > 1)
                                {
                                    // Handshake established, start sending blocks
                                    DateTime lastBlockConfirmationTime = DateTime.UtcNow;
                                    var blocksInProgress = new SortedSet<uint>();
                                    var blocksInProgressLockSem = new SemaphoreSlim(1);
                                    var blockSentTimes = new DateTime[blocksCount];
                                    using (
                                        BlockConfirmationReceived
                                            .Where( _ =>
                                                _.Destination == destination
                                                && _.MessageId == messageId
                                            )
                                            .ObserveOn(TaskPoolScheduler.Default)
                                            .Subscribe(async x =>
                                                {
                                                    if (
                                                        x.BlockId < blocksCount
                                                        && !outMessageInfo.BlockConfirmed[x.BlockId]
                                                        )
                                                    {
                                                        await outMessageInfo.ConfirmBlock(x.BlockId).ConfigureAwait(false);
                                                        lastBlockConfirmationTime = DateTime.UtcNow;
                                                        using (await blocksInProgressLockSem.GetDisposable().ConfigureAwait(false))
                                                        {
                                                            blocksInProgress.Remove(x.BlockId);
                                                        }
                                                    }
                                                }
                                            )
                                    )
                                    {
                                        while (true)
                                        {
                                            compositeTokenSource.Token.ThrowIfCancellationRequested();
                                            IEnumerable<int> blocksToSend;
                                            using (
                                                await
                                                    blocksInProgressLockSem.GetDisposable(token: compositeTokenSource.Token)
                                                        .ConfigureAwait(false))
                                            {
                                                if (
                                                    blocksInProgress.Count == 0
                                                    && outMessageInfo.BlockConfirmed.All(x => x)
                                                    )
                                                    break;
                                                blocksInProgress.RemoveWhere(
                                                    i =>
                                                        DateTime.UtcNow >
                                                        (
                                                            blockSentTimes[i]
                                                            + TimeSpan.FromSeconds(
                                                                _settings
                                                                    .ConfirmationOneBlockTimeoutSeconds
                                                                )
                                                            )
                                                    );
                                                blocksToSend = Enumerable.Range(0, (int)blocksCount)
                                                    .Where(
                                                        i =>
                                                            !blocksInProgress.Contains((uint)i)
                                                            && !outMessageInfo.BlockConfirmed[i]
                                                    )
                                                    .Take(
                                                        Math.Max(
                                                            0,
                                                            _settings.WindowSize
                                                            - blocksInProgress.Count
                                                            )
                                                    );
                                            }
                                            foreach (int blockId in blocksToSend)
                                            {
                                                await SendBlock(
                                                    destination,
                                                    messageId,
                                                    (uint)blockId
                                                ).ConfigureAwait(false);
                                                await outMessageInfo.SendBlock(
                                                    (uint)blockId
                                                ).ConfigureAwait(false);
                                                using (
                                                    await
                                                        blocksInProgressLockSem.GetDisposable(token: compositeTokenSource.Token)
                                                            .ConfigureAwait(false))
                                                {
                                                    blocksInProgress.Add((uint)blockId);
                                                }
                                                blockSentTimes[blockId] = DateTime.UtcNow;
                                            }
                                            if (
                                                DateTime.UtcNow >
                                                lastBlockConfirmationTime +
                                                TimeSpan.FromSeconds(
                                                    _settings.ConfirmationTimeoutSeconds
                                                )
                                            )
                                                throw new EnumException<SendReliableMessageExcs>(
                                                    SendReliableMessageExcs.SendBlocksTimeout
                                                    );
                                            await Task.Delay(50, compositeTokenSource.Token).ConfigureAwait(false);
                                        }
                                    }
                                }
                                outMessageInfo.Status = OutMessageStatus.AllBlocksSent;
                                // all blocks sent and confirmed
                                _log.Trace(
                                    "{4}, send reliable message of {0} bytes " +
                                    "to {1} id={2},rid={3},kind={5}",
                                    data.Length,
                                    destination.Substring(0, 20),
                                    messageId,
                                    replyToMessageId,
                                    _reliableSamHelperGuid.ToString().Substring(0, 5),
                                    messageKind
                                    );
                                return messageId;
                            }
                            finally
                            {
                                compositeTokenSource.Cancel();
                            }
                        }
                    }
                    finally
                    {
                        TryRemoveOutMessageInfo(destinationInfo, messageId);
                    }
                }
                catch (EnumException<SendReliableMessageExcs>)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (TimeoutException)
                {
                    throw;
                }
                catch (Exception exc)
                {
                    throw new EnumException<SendReliableMessageExcs>(
                        SendReliableMessageExcs.UnexpectedError,
                        innerException: exc
                    );
                }
            }
        }

        private async void TryRemoveOutMessageInfo(DestinationInfo destInfo, uint messageId)
        {
            var curMthdName = this.MyNameOfMethod(e => e.TryRemoveOutMessageInfo(null, 0));
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    using (await destInfo.OutMessagesDbLockSem.GetDisposable().ConfigureAwait(false))
                    {
                        if (destInfo.OutMessagesDb.ContainsKey(messageId))
                            destInfo.OutMessagesDb.Remove(messageId);
                    }
                }
            }
            catch (WrongDisposableObjectStateException)
            {
            }
            catch (Exception exc)
            {
                _log.Error("{0} unexpected error '{1}'", curMthdName, exc.ToString());
            }
        }
    }
}
