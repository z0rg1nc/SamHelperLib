using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using BtmI2p.MiscUtil.IO;
using BtmI2p.MiscUtils;
using BtmI2p.ObjectStateLib;

namespace BtmI2p.SamHelper
{
    public enum InMessageStatus
    {
        HandshakeReceived,
        FirstBlockReceived,
        AllBlocksReceived
    }
    public class InMessageInfo
    {
        public InMessageInfo(
            uint messageSize,
            uint blockSize,
            uint replyToMessageId,
            byte messageKind,
            byte[] messageHash
        )
        {
            MessageKind = messageKind;
            uint blockCount = ReliableSamHelper.GetBlocksCount(
                messageSize, 
                blockSize
            );
            ReplyToMessageId = replyToMessageId;
            BlockSize = blockSize;
            BlockCount = blockCount;
            Status = InMessageStatus.HandshakeReceived;
            BlocksReceived = new bool[blockCount];
            ReceivedDataChunkSizes = new uint[blockCount];
            uint totalSize = messageSize;
            for (int i = 0; i < blockCount; i++)
            {
                if (totalSize >= blockSize)
                {
                    ReceivedDataChunkSizes[i] = blockSize;
                    totalSize -= blockSize;
                }
                else
                {
                    ReceivedDataChunkSizes[i] = totalSize;
                }
            }
            ReceivedData = Enumerable.Repeat<byte[]>(null, (int)blockCount).ToArray();
            MessageSize = messageSize;
            MessageHash = messageHash;
            EventFired = false;
        }
        public InMessageStatus Status = InMessageStatus.HandshakeReceived;
        public readonly DateTime CreatedTime = DateTime.UtcNow;
        public readonly byte[] MessageHash;
        public readonly byte[][] ReceivedData;
        public readonly uint[] ReceivedDataChunkSizes; 
        public readonly uint ReplyToMessageId;
        public readonly uint MessageSize;
        public readonly bool[] BlocksReceived;
        public readonly uint BlockSize;
        public readonly uint BlockCount;
        public readonly byte MessageKind;
        public uint BlocksReceivedCount = 0;
        public bool EventFired = false;
        private readonly SemaphoreSlim _lockSem = new SemaphoreSlim(1);

        public async Task FlushBlocks()
        {
            using (await _lockSem.GetDisposable().ConfigureAwait(false))
            {
                if (Status != InMessageStatus.AllBlocksReceived)
                    throw new InvalidOperationException(
                        "Status != InMessageStatus.AllBlocksReceived");
                int l = ReceivedData.Length;
                for (int i = 0; i < l; i++)
                {
                    ReceivedData[i] = null;
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="blockId"></param>
        /// <param name="data"></param>
        /// <returns>True if data completely received, false otherwise</returns>
        public async Task<bool> ReceiveBlock(uint blockId, byte[] data)
        {
            using (await _lockSem.GetDisposable().ConfigureAwait(false))
            {
                if (Status == InMessageStatus.AllBlocksReceived)
                    return false;
                if (blockId >= BlockCount)
                    throw new ArgumentOutOfRangeException(
                        MyNameof.GetLocalVarName(() => blockId));
                if (data.Length != ReceivedDataChunkSizes[(int)blockId])
                    throw new ArgumentOutOfRangeException("data");
                if (BlocksReceived[(int)blockId])
                    return false;
                ReceivedData[(int)blockId] = new byte[ReceivedDataChunkSizes[(int)blockId]];
                Array.Copy(
                    data,
                    ReceivedData[(int)blockId],
                    data.Length
                );
                BlocksReceived[(int)blockId] = true;
                BlocksReceivedCount++;
                if (Status == InMessageStatus.HandshakeReceived)
                    Status = InMessageStatus.FirstBlockReceived;
                if (BlocksReceivedCount == BlockCount)
                {
                    Status = InMessageStatus.AllBlocksReceived;
                    return true;
                }
                return false;
            }
        }
    }
    public partial class ReliableSamHelper
    {
        public static byte[] ReadBytesToEnd(EndianBinaryReader reader)
        {
            const int bufferSize = 4096;
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[bufferSize];
                int count;
                while ((count = reader.Read(buffer, 0, buffer.Length)) != 0)
                    ms.Write(buffer, 0, count);
                return ms.ToArray();
            }
        }
        private async Task SendBlockConfirmation(
            string destination, 
            uint messageId, 
            uint blockId
        )
        {
            if (!SamHelper.IsDestinationStringValid(destination))
                throw new ArgumentOutOfRangeException(
                    MyNameof.GetLocalVarName(() => destination));
            using (
                var ms = new MemoryStream(
                    sizeof(byte) 
                    + sizeof(uint) 
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
                        (byte)ReliableDatagramCodes.BlockConfirmation
                    );
                    binaryWriter.Write(messageId);
                    binaryWriter.Write(blockId);
                    await _samHelper.SendDatagram(
                        destination,
                        ms.ToArray()
                    ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                }
            }
        }
        private async void OnBlockSendReceived(
            BlockSendArgs blockSendArgs
        )
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    string fromDestination = blockSendArgs.Destination;
                    if (_dropDestinations.Contains(fromDestination))
                        return;
                    if (!SamHelper.IsDestinationStringValid(fromDestination))
                        return;
                    DestinationInfo destinationInfo;
                    using (await _destinationDbLockSem.GetDisposable().ConfigureAwait(false))
                    {
                        if (
                            !_destinationDb.ContainsKey(
                                blockSendArgs.Destination
                                )
                            )
                            return;
                        destinationInfo
                            = _destinationDb[blockSendArgs.Destination];
                    }
                    InMessageInfo inMessageInfo;
                    using (await destinationInfo.InMessagesDbLockSem.GetDisposable().ConfigureAwait(false))
                    {
                        if (
                            !destinationInfo.InMessagesDb.ContainsKey(
                                blockSendArgs.MessageId
                            )
                        )
                            return;
                        inMessageInfo
                            = destinationInfo.InMessagesDb[
                                blockSendArgs.MessageId
                            ];
                    }
                    if (
                        inMessageInfo.Status
                        == InMessageStatus.AllBlocksReceived
                    )
                        return;
                    if (inMessageInfo.BlockCount > 1)
                    {
                        await SendBlockConfirmation(
                            fromDestination,
                            blockSendArgs.MessageId,
                            blockSendArgs.BlockId
                        ).ConfigureAwait(false);
                    }
                    if (
                        await inMessageInfo.ReceiveBlock(
                            blockSendArgs.BlockId,
                            blockSendArgs.BlockData
                        ).ConfigureAwait(false)
                    )
                    {
                        var allData = new List<byte>(
                            (int) inMessageInfo.MessageSize
                        );
                        foreach (
                            byte[] blockData 
                                in inMessageInfo.ReceivedData
                            )
                        {
                            allData.AddRange(blockData);
                        }
                        var dataArray = allData.ToArray();
                        if (
                            inMessageInfo.MessageHash.SequenceEqual(
                                GetMessageHash(
                                    blockSendArgs.MessageId,
                                    inMessageInfo.ReplyToMessageId,
                                    inMessageInfo.MessageKind,
                                    dataArray
                                    )
                                )
                            )
                        {
                            ReliableMessageReceived.OnNext(
                                new ReliableMessageReceivedArgs()
                                {
                                    Destination = fromDestination,
                                    Data = allData.ToArray(),
                                    MessageId = blockSendArgs.MessageId,
                                    ReplyToMessageId
                                        = inMessageInfo.ReplyToMessageId,
                                    MessageKind = inMessageInfo.MessageKind
                                }
                            );
                        }
                        await inMessageInfo.FlushBlocks().ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WrongDisposableObjectStateException)
            {
            }
            catch (Exception exc)
            {
                _log.Error(
                    "OnBlockSendReceived" +
                        " unexpected error '{0}'",
                    exc.ToString()
                );
            }
        }
        private async void OnGetMessageStatusReceived(
            GetMessageStatusArgs getMessageStatusArgs
        )
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    string fromDestination
                        = getMessageStatusArgs.Destination;
                    if (_dropDestinations.Contains(fromDestination))
                    {
                        return;
                    }
                    if (!SamHelper.IsDestinationStringValid(fromDestination))
                    {
                        return;
                    }
                    bool destinationDbContainsKey;
                    using (await _destinationDbLockSem.GetDisposable().ConfigureAwait(false))
                    {
                        destinationDbContainsKey 
                            = _destinationDb.ContainsKey(
                                fromDestination
                            );
                    }
                    if (
                        !destinationDbContainsKey
                        )
                    {
                        await SendMessageStatus(
                            fromDestination,
                            getMessageStatusArgs.MessageId,
                            MessageStatusArgs
                                .MessageStatusCode
                                .UnknownMessageId,
                            0
                        ).ConfigureAwait(false);
                        return;
                    }

                    DestinationInfo destinationInfo;
                    using (await _destinationDbLockSem.GetDisposable().ConfigureAwait(false))
                    {
                        destinationInfo =
                            _destinationDb[fromDestination];
                    }
                    bool inMessagesDbContainsKey;
                    using (await destinationInfo.InMessagesDbLockSem.GetDisposable().ConfigureAwait(false))
                    {
                        inMessagesDbContainsKey = 
                            destinationInfo.InMessagesDb.ContainsKey(
                                getMessageStatusArgs.MessageId
                            );
                    }
                    if (
                        !inMessagesDbContainsKey
                    )
                    {
                        await SendMessageStatus(
                            fromDestination,
                            getMessageStatusArgs.MessageId,
                            MessageStatusArgs
                                .MessageStatusCode
                                .UnknownMessageId,
                            0
                            ).ConfigureAwait(false);
                        return;
                    }
                    InMessageInfo inMessageInfo;
                    using (await destinationInfo.InMessagesDbLockSem.GetDisposable().ConfigureAwait(false))
                    {
                        inMessageInfo
                            = destinationInfo.InMessagesDb[
                                getMessageStatusArgs.MessageId
                            ];
                    }
                    int blocksReceivedCount
                        = inMessageInfo.BlocksReceived.Count(x => x);
                    if (
                        inMessageInfo.Status
                        == InMessageStatus.HandshakeReceived
                        )
                    {
                        await SendMessageStatus(
                            fromDestination,
                            getMessageStatusArgs.MessageId,
                            MessageStatusArgs
                                .MessageStatusCode
                                .HandshakeOk,
                            (uint) blocksReceivedCount
                            ).ConfigureAwait(false);
                        return;
                    }
                    if (
                        inMessageInfo.Status
                        == InMessageStatus.AllBlocksReceived
                        )
                    {
                        await SendMessageStatus(
                            fromDestination,
                            getMessageStatusArgs.MessageId,
                            MessageStatusArgs
                                .MessageStatusCode
                                .AllBlocksReceived,
                            (uint) inMessageInfo.BlocksReceived.Length
                            ).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WrongDisposableObjectStateException)
            {
            }
            catch (Exception exc)
            {
                _log.Error(
                    "OnGetMessageStatusReceived" +
                        " unexpected error '{0}'",
                    exc.ToString()
                );
            }
        }

        private async void OnHandshakeStartReceived(
            HandshakeStartArgs handshakeStartArgs
        )
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    string fromDestination
                        = handshakeStartArgs.Destination;
                    if (_dropDestinations.Contains(fromDestination))
                        return;
                    if (!SamHelper.IsDestinationStringValid(fromDestination))
                        return;
                    DestinationInfo destinationInfo;
                    using (await _destinationDbLockSem.GetDisposable().ConfigureAwait(false))
                    {
                        if (
                            !_destinationDb.ContainsKey(
                                handshakeStartArgs.Destination
                                )
                            )
                        {
                            destinationInfo = new DestinationInfo();
                            _destinationDb.Add(
                                handshakeStartArgs.Destination,
                                destinationInfo
                            );
                        }
                        else
                        {
                            destinationInfo =
                                _destinationDb[
                                    handshakeStartArgs.Destination
                                ];
                        }
                    }
                    using (await destinationInfo.InMessagesDbLockSem.GetDisposable().ConfigureAwait(false))
                    {
                        if (
                            destinationInfo.InMessagesDb.ContainsKey(
                                handshakeStartArgs.MessageId
                            )
                        )
                        {
                            var inMessageInfo
                                = destinationInfo.InMessagesDb[
                                    handshakeStartArgs.MessageId
                                ];
                            if (
                                inMessageInfo.Status
                                == InMessageStatus.HandshakeReceived
                            )
                            {
                                await
                                    SendMessageStatus(
                                        fromDestination,
                                        handshakeStartArgs.MessageId,
                                        MessageStatusArgs.MessageStatusCode.HandshakeOk,
                                        0
                                    ).ConfigureAwait(false);
                                return;
                            }
                            else if (
                                inMessageInfo.Status
                                == InMessageStatus.AllBlocksReceived
                            )
                            {
                                await SendMessageStatus(
                                    fromDestination,
                                    handshakeStartArgs.MessageId,
                                    MessageStatusArgs.MessageStatusCode.AllBlocksReceived,
                                    inMessageInfo.BlocksReceivedCount
                                ).ConfigureAwait(false);
                                return;
                            }
                            else
                            {
                                await SendMessageStatus(
                                    fromDestination,
                                    handshakeStartArgs.MessageId,
                                    MessageStatusArgs
                                        .MessageStatusCode
                                        .HandshakeErrorDuplicatedId,
                                    inMessageInfo.BlocksReceivedCount
                                ).ConfigureAwait(false);
                                return;
                            }
                        }
                        else
                        {
                            if (
                                handshakeStartArgs.MessageSize
                                > _settings.MaxMesageLength
                            )
                            {
                                await SendMessageStatus(
                                    fromDestination,
                                    handshakeStartArgs.MessageId,
                                    MessageStatusArgs.MessageStatusCode
                                        .HandshakeErrorWrongTotalSize,
                                    0
                                    ).ConfigureAwait(false);
                                return;
                            }
                            if (handshakeStartArgs.BlockSize > _settings.MaxBlockSize)
                            {
                                await SendMessageStatus(
                                    fromDestination,
                                    handshakeStartArgs.MessageId,
                                    MessageStatusArgs.MessageStatusCode
                                        .HandshakeErrorWrongBlockSize,
                                    0
                                    ).ConfigureAwait(false);
                                return;
                            }
                            var inMessageInfo = new InMessageInfo(
                                handshakeStartArgs.MessageSize,
                                handshakeStartArgs.BlockSize,
                                handshakeStartArgs.ReplyToMessageId,
                                handshakeStartArgs.MessageKind,
                                handshakeStartArgs.MessageHash
                            );
                            if (
                                inMessageInfo.ReceivedDataChunkSizes[0]
                                != handshakeStartArgs.FirstBlockData.Length
                            )
                            {
                                await SendMessageStatus(
                                    fromDestination,
                                    handshakeStartArgs.MessageId,
                                    MessageStatusArgs.MessageStatusCode
                                        .HandshakeErrorWrongBlockSize,
                                    0
                                ).ConfigureAwait(false);
                                return;
                            }
                            destinationInfo.InMessagesDb.Add(
                                handshakeStartArgs.MessageId,
                                inMessageInfo
                            );
                            await SendMessageStatus(
                                fromDestination,
                                handshakeStartArgs.MessageId,
                                MessageStatusArgs.MessageStatusCode.HandshakeOk,
                                0
                            ).ConfigureAwait(false);
                            OnBlockSendReceived(
                                new BlockSendArgs()
                                {
                                    BlockData = handshakeStartArgs.FirstBlockData,
                                    BlockId = 0,
                                    Destination = handshakeStartArgs.Destination,
                                    MessageId = handshakeStartArgs.MessageId
                                }
                            );
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WrongDisposableObjectStateException)
            {
            }
            catch (Exception exc)
            {
                _log.Error(
                    "OnHandshakeStartReceive" +
                        " unexpected error '{0}'",
                    exc.ToString()
                );
            }
        }

        private async Task SendProtocolVersion(
            string destination, 
            int protocolId
        )
        {
            if (!SamHelper.IsDestinationStringValid(destination))
                throw new ArgumentOutOfRangeException(
                    "destination"
                );
            using (
                var ms = new MemoryStream(
                    sizeof(byte) + sizeof(int)
                )
            )
            {
                using (
                    var binaryWriter = new EndianBinaryWriter(
                        _littleEndianConverter, 
                        ms
                    )
                )
                {
                    binaryWriter.Write(
                        (byte)ReliableDatagramCodes.ProtocolVersion
                    );
                    binaryWriter.Write(protocolId);
                    await _samHelper.SendDatagram(
                        destination,
                        ms.ToArray()
                    ).ThrowIfCancelled(_cts.Token).ConfigureAwait(false);
                }
            }
        }
        private async void OnGetProtocolVersionReceived(
            GetProtocolVersionArgs getProtocolVersionArgs
        )
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    string fromDestination = getProtocolVersionArgs.Destination;
                    if (_dropDestinations.Contains(fromDestination))
                        return;
                    if (!SamHelper.IsDestinationStringValid(fromDestination))
                        return;
                    await SendProtocolVersion(
                        fromDestination,
                        _settings.ProtocolVersion
                    ).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WrongDisposableObjectStateException)
            {
            }
            catch (Exception exc)
            {
                _log.Error(
                    "OnGetProtocolVersionReceived" +
                        " unexpected error '{0}'",
                    exc.ToString()
                );
            }
        }
        #region Parse datagrams
        private async Task<bool> CheckExistOutMessage(
            string destination, 
            uint messageId
        )
        {
            DestinationInfo destinationInfo;
            using (await _destinationDbLockSem.GetDisposable().ConfigureAwait(false))
            {
                if (!_destinationDb.ContainsKey(destination))
                    return false;
                destinationInfo = _destinationDb[destination];
            }
            using (
                await destinationInfo.OutMessagesDbLockSem.GetDisposable()
                    .ConfigureAwait(false)
            )
            {
                if (
                    !destinationInfo
                        .OutMessagesDb.ContainsKey(messageId)
                    )
                    return false;
            }
            return true;
        }
        private async void SamHelperOnDatagramDataReceived(
            DatagramDataReceivedArgs datagramDataReceivedArgs
            )
        {
            try
            {
                using (_stateHelper.GetFuncWrapper())
                {
                    if (
                        datagramDataReceivedArgs.Data.Length
                        > (_settings.MaxBlockSize + 1000)
                        )
                        return;
                    if (
                        _dropDestinations.Contains(
                            datagramDataReceivedArgs.Destination
                            )
                        )
                        return;

                    string fromDestination = datagramDataReceivedArgs.Destination;
                    using (
                        var reader = new EndianBinaryReader(
                            _littleEndianConverter,
                            new MemoryStream(datagramDataReceivedArgs.Data)
                            )
                        )
                    {
                        var blockType = (ReliableDatagramCodes) reader.ReadByte();
                        switch (blockType)
                        {
                            case ReliableDatagramCodes.RawDatagram:
                            {
                                uint messageId = reader.ReadUInt32();
                                uint replyToMessageId = reader.ReadUInt32();
                                byte messageKind = reader.ReadByte();
                                var messageHash = reader.ReadBytesOrThrow(32);
                                var rawData = ReadBytesToEnd(reader);
                                if (
                                    messageHash.SequenceEqual(
                                        GetMessageHash(
                                            messageId,
                                            replyToMessageId,
                                            messageKind,
                                            rawData
                                            )
                                        )
                                    )
                                    RawDatagramReceived.OnNext(
                                        new ReliableMessageReceivedArgs()
                                        {
                                            Data = rawData,
                                            Destination = fromDestination,
                                            MessageId = messageId,
                                            ReplyToMessageId = replyToMessageId,
                                            MessageKind = messageKind
                                        }
                                        );
                                return;
                            }
                            case ReliableDatagramCodes.HandshakeStart:
                            {
                                uint messageId = reader.ReadUInt32();
                                uint replyToMessageId = reader.ReadUInt32();
                                byte messageKind = reader.ReadByte();
                                uint totalMessageSize = reader.ReadUInt32();
                                if (totalMessageSize > _settings.MaxMesageLength)
                                    return;
                                uint blockSize = reader.ReadUInt32();
                                if (blockSize > _settings.MaxBlockSize)
                                    return;
                                var messageHash = reader.ReadBytesOrThrow(32);
                                uint firstBlockSize = reader.ReadUInt32();
                                if (
                                    firstBlockSize == 0
                                    ||
                                    firstBlockSize > _settings.MaxBlockSize
                                    )
                                    return;
                                byte[] firstBlockData
                                    = reader.ReadBytesOrThrow((int) firstBlockSize);
                                HandshakeStartReceived.OnNext(
                                    new HandshakeStartArgs
                                    {
                                        Destination = fromDestination,
                                        BlockSize = blockSize,
                                        MessageId = messageId,
                                        ReplyToMessageId = replyToMessageId,
                                        MessageSize = totalMessageSize,
                                        MessageKind = messageKind,
                                        MessageHash = messageHash,
                                        FirstBlockData = firstBlockData
                                    }
                                    );
                                return;
                            }
                            case ReliableDatagramCodes.MessageStatus:
                            {
                                uint messageId = reader.ReadUInt32();
                                if (!await CheckExistOutMessage(fromDestination, messageId).ConfigureAwait(false))
                                {
                                    return;
                                }
                                var errorCode
                                    = (MessageStatusArgs.MessageStatusCode)
                                        reader.ReadByte();
                                uint blocksReceived = reader.ReadUInt32();
                                MessageStatusReceived.OnNext(
                                    new MessageStatusArgs()
                                    {
                                        Destination
                                            = datagramDataReceivedArgs
                                                .Destination,
                                        MessageId = messageId,
                                        StatusCode = errorCode,
                                        BlocksReceived = blocksReceived
                                    }
                                    );
                                return;
                            }
                            case ReliableDatagramCodes.BlockSend:
                            {
                                uint messageId = reader.ReadUInt32();
                                uint blockId = reader.ReadUInt32();
                                var blockData = ReadBytesToEnd(reader);
                                if (
                                    blockData.Length == 0
                                    || blockData.Length > _settings.MaxBlockSize
                                    )
                                    return;
                                BlockSendReceived.OnNext(new BlockSendArgs()
                                {
                                    Destination = fromDestination,
                                    MessageId = messageId,
                                    BlockId = blockId,
                                    BlockData = blockData
                                });
                                return;
                            }
                            case ReliableDatagramCodes.BlockConfirmation:
                            {
                                uint messageId = reader.ReadUInt32();
                                if (!await CheckExistOutMessage(fromDestination, messageId).ConfigureAwait(false))
                                    return;
                                uint blockId = reader.ReadUInt32();
                                BlockConfirmationReceived.OnNext(
                                    new BlockConfirmationArgs()
                                    {
                                        Destination = fromDestination,
                                        MessageId = messageId,
                                        BlockId = blockId
                                    }
                                    );
                                return;
                            }
                            case ReliableDatagramCodes.GetMessageStatus:
                            {
                                uint messageId = reader.ReadUInt32();
                                GetMessageStatusReceived.OnNext(
                                    new GetMessageStatusArgs()
                                    {
                                        Destination = fromDestination,
                                        MessageId = messageId
                                    }
                                    );
                                return;
                            }
                            case ReliableDatagramCodes.GetProtocolVersion:
                            {
                                GetProtocolVersionReceived.OnNext(
                                    new GetProtocolVersionArgs()
                                    {
                                        Destination = fromDestination
                                    }
                                    );
                                return;
                            }
                            case ReliableDatagramCodes.ProtocolVersion:
                            {
                                uint protocolVersion = reader.ReadUInt32();
                                ProtocolVersionReceived.OnNext(
                                    new ProtocolVersionArgs()
                                    {
                                        Destination = fromDestination,
                                        ProtocolVersion = protocolVersion
                                    }
                                    );
                                return;
                            }
                        }
                    }
                }
            }
            catch (EndOfStreamException exc)
            {
                _log.Error(
                    "SamHelperOnDatagramDataReceived" +
                    " EndOfStreamException '{0}'",
                    exc.ToString()
                );
            }
            catch (OperationCanceledException)
            {
            }
            catch (WrongDisposableObjectStateException)
            {
            }
            catch (Exception exc)
            {
                _log.Error(
                    "SamHelperOnDatagramDataReceived" +
                    " unexpected error '{0}'",
                    exc.ToString()
                    );
            }
        }
        /**************************/
        public Subject<ReliableMessageReceivedArgs> RawDatagramReceived =
            new Subject<ReliableMessageReceivedArgs>();
        
        /**************************/
        public class ReliableMessageReceivedArgs : DatagramDataReceivedArgs
        {
            public uint MessageId = 0;
            public uint ReplyToMessageId = 0;
            public byte MessageKind = 0;
        }

        public Subject<ReliableMessageReceivedArgs> ReliableMessageReceived =
            new Subject<ReliableMessageReceivedArgs>();
            
        /**************************/
        public class HandshakeStartArgs
        {
            public string Destination;
            public uint MessageId;
            public uint ReplyToMessageId;
            public uint MessageSize;
            public uint BlockSize;
            public byte MessageKind = 0;
            public byte[] MessageHash;
            public byte[] FirstBlockData;
        }
        public Subject<HandshakeStartArgs> HandshakeStartReceived 
            = new Subject<HandshakeStartArgs>();
        /**************************/
        public class MessageStatusArgs
        {
            public enum MessageStatusCode : byte
            {
                HandshakeErrorDuplicatedId = 0x00,
                HandshakeOk = 0x01,
                AllBlocksReceived = 0x02,
                UnknownMessageId = 0x03,
                HandshakeErrorWrongTotalSize = 0x04,
                HandshakeErrorWrongBlockSize = 0x05
            }
            public string Destination;
            public uint MessageId;
            public MessageStatusCode StatusCode;
            public uint BlocksReceived;
        }
        public Subject<MessageStatusArgs> MessageStatusReceived 
            = new Subject<MessageStatusArgs>();
        /**************************/
        public class BlockSendArgs
        {
            public string Destination;
            public uint MessageId;
            public uint BlockId;
            public byte[] BlockData;
        }
        public Subject<BlockSendArgs> BlockSendReceived 
            = new Subject<BlockSendArgs>();
        /**************************/
        public class BlockConfirmationArgs
        {
            public string Destination;
            public uint MessageId;
            public uint BlockId;
        }
        public Subject<BlockConfirmationArgs> BlockConfirmationReceived 
            = new Subject<BlockConfirmationArgs>();
        /**************************/
        public class GetMessageStatusArgs
        {
            public string Destination;
            public uint MessageId;
        }
        public Subject<GetMessageStatusArgs> GetMessageStatusReceived 
            = new Subject<GetMessageStatusArgs>();
        /**************************/
        public class GetProtocolVersionArgs
        {
            public string Destination;
        }
        public Subject<GetProtocolVersionArgs> GetProtocolVersionReceived 
            = new Subject<GetProtocolVersionArgs>();
        /**************************/
        public class ProtocolVersionArgs
        {
            public string Destination;
            public uint ProtocolVersion;
        }
        public Subject<ProtocolVersionArgs> ProtocolVersionReceived 
            = new Subject<ProtocolVersionArgs>();
        /**************************/
        #endregion
    }
}
