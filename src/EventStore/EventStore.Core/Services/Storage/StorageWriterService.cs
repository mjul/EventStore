﻿// Copyright (c) 2012, Event Store LLP
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
// 
// Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
// Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
// Neither the name of the Event Store LLP nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using EventStore.Common.Log;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Monitoring.Stats;
using EventStore.Core.Services.Storage.EpochManager;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.LogRecords;

namespace EventStore.Core.Services.Storage
{
    public class StorageWriterService : IHandle<SystemMessage.SystemInit>,
                                        IHandle<SystemMessage.StateChangeMessage>,
                                        IHandle<SystemMessage.WaitForChaserToCatchUp>,
                                        IHandle<StorageMessage.WritePrepares>,
                                        IHandle<StorageMessage.WriteDelete>,
                                        IHandle<StorageMessage.WriteTransactionStart>,
                                        IHandle<StorageMessage.WriteTransactionData>,
                                        IHandle<StorageMessage.WriteTransactionPrepare>,
                                        IHandle<StorageMessage.WriteCommit>,
                                        IHandle<MonitoringMessage.InternalStatsRequest>
    {
        private static readonly ILogger Log = LogManager.GetLoggerFor<StorageWriterService>();

        protected static readonly int TicksPerMs = (int)(Stopwatch.Frequency / 1000);
        private static readonly TimeSpan WaitForChaserSingleIterationTimeout = TimeSpan.FromMilliseconds(100);

        protected readonly TFChunkDb Db;
        protected readonly TFChunkWriter Writer;
        protected readonly IReadIndex ReadIndex;
        protected readonly IEpochManager EpochManager;

        protected readonly IPublisher Bus;
        private readonly ISubscriber _subscribeToBus;
        protected readonly QueuedHandler StorageWriterQueue;
        private readonly InMemoryBus _writerBus;

        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly int _minFlushDelay;
        private long _lastFlushDelay;
        private long _lastFlushTimestamp;

        protected int FlushMessagesInQueue;
        private VNodeState _vnodeState = VNodeState.Initializing;
        protected bool BlockWriter = false;

        private const int LastStatsCount = 1024;
        private readonly long[] _lastFlushDelays = new long[LastStatsCount];
        private readonly long[] _lastFlushSizes = new long[LastStatsCount];
        private int _statIndex;
        private int _statCount;
        private long _sumFlushDelay;
        private long _sumFlushSize;
        private long _lastFlushSize;
        private long _maxFlushSize;
        private long _maxFlushDelay;

        public StorageWriterService(IPublisher bus, 
                                    ISubscriber subscribeToBus,
                                    int minFlushDelayMs,
                                    TFChunkDb db,
                                    TFChunkWriter writer, 
                                    IReadIndex readIndex,
                                    IEpochManager epochManager)
        {
            Ensure.NotNull(bus, "bus");
            Ensure.NotNull(subscribeToBus, "subscribeToBus");
            Ensure.Nonnegative(minFlushDelayMs, "minFlushDelayMs");
            Ensure.NotNull(db, "db");
            Ensure.NotNull(writer, "writer");
            Ensure.NotNull(readIndex, "readIndex");
            Ensure.NotNull(epochManager, "epochManager");

            Bus = bus;
            _subscribeToBus = subscribeToBus;
            Db = db;
            ReadIndex = readIndex;
            EpochManager = epochManager;

            _minFlushDelay = minFlushDelayMs * TicksPerMs;
            _lastFlushDelay = 0;
            _lastFlushTimestamp = _watch.ElapsedTicks;

            Writer = writer;
            Writer.Open();

            _writerBus = new InMemoryBus("StorageWriterBus", watchSlowMsg: false);
            StorageWriterQueue = new QueuedHandler(new AdHocHandler<Message>(CommonHandle),
                                                   "StorageWriterQueue",
                                                   true,
                                                   TimeSpan.FromMilliseconds(500));
            StorageWriterQueue.Start();

            SubscribeToMessage<SystemMessage.SystemInit>();
            SubscribeToMessage<SystemMessage.StateChangeMessage>();
            SubscribeToMessage<SystemMessage.WaitForChaserToCatchUp>();
            SubscribeToMessage<StorageMessage.WritePrepares>();
            SubscribeToMessage<StorageMessage.WriteDelete>();
            SubscribeToMessage<StorageMessage.WriteTransactionStart>();
            SubscribeToMessage<StorageMessage.WriteTransactionData>();
            SubscribeToMessage<StorageMessage.WriteTransactionPrepare>();
            SubscribeToMessage<StorageMessage.WriteCommit>();
        }

        protected void SubscribeToMessage<T>() where T: Message
        {
            _writerBus.Subscribe((IHandle<T>)this);
            _subscribeToBus.Subscribe(new AdHocHandler<Message>(EnqueueMessage).WidenFrom<T, Message>());
        }

        private void EnqueueMessage(Message message)
        {
            if (message is StorageMessage.IFlushableMessage)
                Interlocked.Increment(ref FlushMessagesInQueue);

            StorageWriterQueue.Publish(message);

            if (message is SystemMessage.BecomeShuttingDown) // we need to handle this message on main thread to stop StorageWriterQueue
            {
                StorageWriterQueue.Stop();
                BlockWriter = true;
                Bus.Publish(new SystemMessage.ServiceShutdown("StorageWriterService"));
            }
        }

        private void CommonHandle(Message message)
        {
            if (BlockWriter && !(message is SystemMessage.StateChangeMessage))
            {
                Log.Trace("Blocking message {0} in StorageWriterService. Message:\n{1}", message.GetType().Name, message);
                return;
            }

            if (_vnodeState != VNodeState.Master && message is StorageMessage.IMasterWriteMessage)
            {
                var msg = string.Format("{0} appeared in StorageWriter during state {1}.", message.GetType().Name, _vnodeState);
                Log.Fatal(msg);
                Application.Exit(ExitCode.Error, msg);
                return;
            }

            try
            {
                _writerBus.Handle(message);
            }
            catch (Exception exc)
            {
                BlockWriter = true;
                Log.FatalException(exc, "Unexpected error in StorageWriterService. Terminating the process...");
                Application.Exit(ExitCode.Error, string.Format("Unexpected error in StorageWriterService: {0}", exc.Message));
            }
        }

        void IHandle<SystemMessage.SystemInit>.Handle(SystemMessage.SystemInit message)
        {
            // We rebuild index till the chaser position, because
            // everything else will be done by chaser as during replication
            // with no concurrency issues with writer, as writer before jumping 
            // into master-mode and accepting writes will wait till chaser caught up.
            var writerCheckpoint = Db.Config.WriterCheckpoint.Read();
            var chaserCheckpoint = Db.Config.ChaserCheckpoint.Read();
            ReadIndex.Init(writerCheckpoint, chaserCheckpoint);
            Bus.Publish(new SystemMessage.StorageWriterInitializationDone());
        }

        public virtual void Handle(SystemMessage.StateChangeMessage message)
        {
            _vnodeState = message.State;

            switch (message.State)
            {
                case VNodeState.Master:
                {
                    EpochManager.WriteNewEpoch(); // forces flush
                    break;
                }
                case VNodeState.ShuttingDown:
                {
                    Writer.Close();
                    break;
                }
            }
        }

        void IHandle<SystemMessage.WaitForChaserToCatchUp>.Handle(SystemMessage.WaitForChaserToCatchUp message)
        {
            // if we are in states, that doesn't need to wait for chaser, ignore
            if (_vnodeState != VNodeState.PreMaster && _vnodeState != VNodeState.PreReplica) 
                throw new Exception(string.Format("{0} appeared in {1} state.", message.GetType().Name, _vnodeState));

            Db.Config.WriterCheckpoint.Flush();

            var sw = Stopwatch.StartNew();
            while (Db.Config.ChaserCheckpoint.Read() < Db.Config.WriterCheckpoint.Read() && sw.Elapsed < WaitForChaserSingleIterationTimeout)
            {
                Thread.Sleep(1);
            }

            if (Db.Config.ChaserCheckpoint.Read() == Db.Config.WriterCheckpoint.Read())
            {
                Bus.Publish(new SystemMessage.ChaserCaughtUp(message.CorrelationId));
                return;
            }

            var totalTime = message.TotalTimeWasted + sw.Elapsed;
            if (totalTime < TimeSpan.FromSeconds(5) || (int)totalTime.TotalSeconds % 5 == 0) // too verbose otherwise
                Log.Debug("Still waiting for chaser to catch up already for {0}...", totalTime);
            Bus.Publish(new SystemMessage.WaitForChaserToCatchUp(message.CorrelationId, totalTime));
        }

        void IHandle<StorageMessage.WritePrepares>.Handle(StorageMessage.WritePrepares message)
        {
            Interlocked.Decrement(ref FlushMessagesInQueue);

            try
            {
                if (message.LiveUntil < DateTime.UtcNow)
                    return;

                var logPosition = Writer.Checkpoint.ReadNonFlushed();
                if (message.Events.Length > 0)
                {
                    var transactionPosition = logPosition;
                    for (int i = 0; i < message.Events.Length; ++i)
                    {
                        var evnt = message.Events[i];
                        var flags = PrepareFlags.Data;
                        if (i == 0)
                            flags |= PrepareFlags.TransactionBegin;
                        if (i == message.Events.Length - 1)
                            flags |= PrepareFlags.TransactionEnd;
                        if (evnt.IsJson)
                            flags |= PrepareFlags.IsJson;

                        var expectedVersion = i == 0 ? message.ExpectedVersion : ExpectedVersion.Any;
                        var res = WritePrepareWithRetry(LogRecord.Prepare(logPosition,
                                                                          message.CorrelationId,
                                                                          evnt.EventId,
                                                                          transactionPosition,
                                                                          i,
                                                                          message.EventStreamId,
                                                                          expectedVersion,
                                                                          flags,
                                                                          evnt.EventType,
                                                                          evnt.Data,
                                                                          evnt.Metadata));
                        logPosition = res.NewPos;
                        if (i == 0)
                            transactionPosition = res.WrittenPos; // transaction position could be changed due to switching to new chunk
                    }
                }
                else
                {
                    WritePrepareWithRetry(LogRecord.Prepare(logPosition,
                                                            message.CorrelationId,
                                                            Guid.NewGuid(),
                                                            logPosition,
                                                            -1,
                                                            message.EventStreamId,
                                                            message.ExpectedVersion,
                                                            PrepareFlags.TransactionBegin | PrepareFlags.TransactionEnd,
                                                            null,
                                                            Empty.ByteArray,
                                                            Empty.ByteArray));
                }
            }
            catch (Exception exc)
            {
                Log.ErrorException(exc, "Exception in writer.");
                throw;
            }
            finally
            {
                Flush();
            }
        }

        void IHandle<StorageMessage.WriteTransactionStart>.Handle(StorageMessage.WriteTransactionStart message)
        {
            Interlocked.Decrement(ref FlushMessagesInQueue);
            try
            {
                if (message.LiveUntil < DateTime.UtcNow)
                    return;

                var logPosition = Writer.Checkpoint.ReadNonFlushed();
                var record = LogRecord.TransactionBegin(logPosition, message.CorrelationId, message.EventStreamId, message.ExpectedVersion);
                var res = WritePrepareWithRetry(record);

                // we update cache to avoid non-cached look-up on next TransactionWrite
                ReadIndex.UpdateTransactionInfo(res.WrittenPos, new TransactionInfo(-1, message.EventStreamId)); 
            }
            catch (Exception exc)
            {
                Log.ErrorException(exc, "Exception in writer.");
                throw;
            }
            finally
            {
                Flush();
            }
        }

        void IHandle<StorageMessage.WriteTransactionData>.Handle(StorageMessage.WriteTransactionData message)
        {
            Interlocked.Decrement(ref FlushMessagesInQueue);
            try
            {
                var logPosition = Writer.Checkpoint.ReadNonFlushed();
                var transactionInfo = ReadIndex.GetTransactionInfo(Writer.Checkpoint.Read(), message.TransactionId);
                if (!CheckTransactionInfo(message.TransactionId, transactionInfo))
                    return;

                if (message.Events.Length > 0)
                {
                    for (int i = 0; i < message.Events.Length; ++i)
                    {
                        var evnt = message.Events[i];
                        var record = LogRecord.TransactionWrite(logPosition,
                                                                message.CorrelationId,
                                                                evnt.EventId,
                                                                message.TransactionId,
                                                                transactionInfo.TransactionOffset + i + 1,
                                                                transactionInfo.EventStreamId,
                                                                evnt.EventType,
                                                                evnt.Data,
                                                                evnt.Metadata,
                                                                evnt.IsJson);
                        var res = WritePrepareWithRetry(record);
                        logPosition = res.NewPos;
                    }
                    ReadIndex.UpdateTransactionInfo(message.TransactionId,
                                                    new TransactionInfo(transactionInfo.TransactionOffset + message.Events.Length,
                                                                        transactionInfo.EventStreamId));
                }
            }
            catch (Exception exc)
            {
                Log.ErrorException(exc, "Exception in writer.");
                throw;
            }
            finally
            {
                Flush();
            }
        }

        void IHandle<StorageMessage.WriteTransactionPrepare>.Handle(StorageMessage.WriteTransactionPrepare message)
        {
            Interlocked.Decrement(ref FlushMessagesInQueue);
            try
            {
                if (message.LiveUntil < DateTime.UtcNow)
                    return;

                var transactionInfo = ReadIndex.GetTransactionInfo(Writer.Checkpoint.Read(), message.TransactionId);
                if (!CheckTransactionInfo(message.TransactionId, transactionInfo))
                    return;

                var record = LogRecord.TransactionEnd(Writer.Checkpoint.ReadNonFlushed(),
                                                      message.CorrelationId,
                                                      Guid.NewGuid(),
                                                      message.TransactionId,
                                                      transactionInfo.EventStreamId);
                WritePrepareWithRetry(record);
            }
            catch (Exception exc)
            {
                Log.ErrorException(exc, "Exception in writer.");
                throw;
            }
            finally
            {
                Flush();
            }
        }

        private bool CheckTransactionInfo(long transactionId, TransactionInfo transactionInfo)
        {
            if (transactionInfo.TransactionOffset < -1 || transactionInfo.EventStreamId.IsEmptyString())
            {
                Log.Error(string.Format("Invalid transaction info found for transaction ID {0}. " 
                                        + "Possibly wrong transactionId provided. TransactionOffset: {1}, EventStreamId: {2}",
                                        transactionId,
                                        transactionInfo.TransactionOffset,
                                        transactionInfo.EventStreamId.IsEmptyString() ? "<null>" : transactionInfo.EventStreamId));
                return false;
            }
            return true;
        }

        void IHandle<StorageMessage.WriteCommit>.Handle(StorageMessage.WriteCommit message)
        {
            Interlocked.Decrement(ref FlushMessagesInQueue);
            try
            {
                var commitPos = Writer.Checkpoint.ReadNonFlushed();
                var result = ReadIndex.CheckCommitStartingAt(message.TransactionPosition, commitPos);
                switch (result.Decision)
                {
                    case CommitDecision.Ok:
                    {
                        var commit = WriteCommitWithRetry(LogRecord.Commit(commitPos,
                                                                           message.CorrelationId,
                                                                           message.TransactionPosition,
                                                                           result.CurrentVersion + 1));
                        ReadIndex.Commit(commit);
                        break;
                    }
                    case CommitDecision.WrongExpectedVersion:
                        message.Envelope.ReplyWith(new StorageMessage.WrongExpectedVersion(message.CorrelationId));
                        break;
                    case CommitDecision.Deleted:
                        message.Envelope.ReplyWith(new StorageMessage.StreamDeleted(message.CorrelationId));
                        break;
                    case CommitDecision.Idempotent:
                        message.Envelope.ReplyWith(new StorageMessage.AlreadyCommitted(message.CorrelationId,
                                                                                       result.EventStreamId,
                                                                                       result.StartEventNumber,
                                                                                       result.EndEventNumber));
                        break;
                    case CommitDecision.CorruptedIdempotency:
                        // in case of corrupted idempotency (part of transaction is ok, other is different)
                        // then we can say that the transaction is not idempotent, so WrongExpectedVersion is ok answer
                        message.Envelope.ReplyWith(new StorageMessage.WrongExpectedVersion(message.CorrelationId));
                        break;
                    case CommitDecision.InvalidTransaction:
                        message.Envelope.ReplyWith(new StorageMessage.InvalidTransaction(message.CorrelationId));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            catch (Exception exc)
            {
                Log.ErrorException(exc, "Exception in writer.");
                throw;
            }
            finally
            {
                Flush();
            }
        }

        void IHandle<StorageMessage.WriteDelete>.Handle(StorageMessage.WriteDelete message)
        {
            Interlocked.Decrement(ref FlushMessagesInQueue);
            try
            {
                if (message.LiveUntil < DateTime.UtcNow)
                    return;

                var record = LogRecord.DeleteTombstone(Writer.Checkpoint.ReadNonFlushed(),
                                                       message.CorrelationId,
                                                       message.EventStreamId,
                                                       message.ExpectedVersion);
                WritePrepareWithRetry(record);
            }
            catch (Exception exc)
            {
                Log.ErrorException(exc, "Exception in writer.");
                throw;
            }
            finally
            {
                Flush();
            }
        }

        private WriteResult WritePrepareWithRetry(PrepareLogRecord prepare)
        {
            long writtenPos = prepare.LogPosition;
            long newPos;
            if (!Writer.Write(prepare, out newPos))
            {
                var transactionPos = prepare.TransactionPosition == prepare.LogPosition ? newPos : prepare.TransactionPosition;
                var record = new PrepareLogRecord(newPos,
                                                  prepare.CorrelationId,
                                                  prepare.EventId,
                                                  transactionPos,
                                                  prepare.TransactionOffset,
                                                  prepare.EventStreamId,
                                                  prepare.ExpectedVersion,
                                                  prepare.TimeStamp,
                                                  prepare.Flags,
                                                  prepare.EventType,
                                                  prepare.Data,
                                                  prepare.Metadata);
                writtenPos = newPos;
                if (!Writer.Write(record, out newPos))
                {
                    throw new Exception(string.Format("Second write try failed when first writing prepare at {0}, then at {1}.",
                                                      prepare.LogPosition,
                                                      writtenPos));
                }
            }
            return new WriteResult(writtenPos, newPos);
        }

        private CommitLogRecord WriteCommitWithRetry(CommitLogRecord commit)
        {
            long newPos;
            if (!Writer.Write(commit, out newPos))
            {
                var transactionPos = commit.TransactionPosition == commit.LogPosition ? newPos : commit.TransactionPosition;
                var record = new CommitLogRecord(newPos,
                                                 commit.CorrelationId,
                                                 transactionPos,
                                                 commit.TimeStamp,
                                                 commit.FirstEventNumber);
                long writtenPos = newPos;
                if (!Writer.Write(record, out newPos))
                {
                    throw new Exception(string.Format("Second write try failed when first writing commit at {0}, then at {1}.",
                                                      commit.LogPosition,
                                                      writtenPos));
                }
                return record;
            }
            return commit;
        }

        protected bool Flush(bool force = false)
        {
            var start = _watch.ElapsedTicks;
            if (force || FlushMessagesInQueue == 0 || start - _lastFlushTimestamp >= _lastFlushDelay + _minFlushDelay)
            {
                var flushSize = Writer.Checkpoint.ReadNonFlushed() - Writer.Checkpoint.Read();

                Writer.Flush();

                var end = _watch.ElapsedTicks;
                var flushDelay = end - start;
                Interlocked.Exchange(ref _lastFlushDelay, flushDelay);
                Interlocked.Exchange(ref _lastFlushSize, flushSize);
                _lastFlushTimestamp = end;

                if (_statCount >= LastStatsCount)
                {
                    Interlocked.Add(ref _sumFlushSize, -_lastFlushSizes[_statIndex]);
                    Interlocked.Add(ref _sumFlushDelay, -_lastFlushDelays[_statIndex]);
                }
                else
                {
                    _statCount += 1;
                }
                _lastFlushSizes[_statIndex] = flushSize;
                _lastFlushDelays[_statIndex] = flushDelay;
                Interlocked.Add(ref _sumFlushSize, flushSize);
                Interlocked.Add(ref _sumFlushDelay, flushDelay);
                Interlocked.Exchange(ref _maxFlushSize, Math.Max(Interlocked.Read(ref _maxFlushSize), flushSize));
                Interlocked.Exchange(ref _maxFlushDelay, Math.Max(Interlocked.Read(ref _maxFlushDelay), flushDelay));
                _statIndex = (_statIndex + 1) & (LastStatsCount - 1);

                return true;
            }
            return false;
        }

        private struct WriteResult
        {
            public readonly long WrittenPos;
            public readonly long NewPos;

            public WriteResult(long writtenPos, long newPos)
            {
                WrittenPos = writtenPos;
                NewPos = newPos;
            }
        }

        public void Handle(MonitoringMessage.InternalStatsRequest message)
        {
            var lastFlushSize = Interlocked.Read(ref _lastFlushSize);
            var lastFlushDelayMs = Interlocked.Read(ref _lastFlushDelay) / (double)TicksPerMs;
            var statCount = _statCount;
            var meanFlushSize = statCount == 0 ? 0 : Interlocked.Read(ref _sumFlushSize) / statCount;
            var meanFlushDelayMs = statCount == 0 ? 0 : Interlocked.Read(ref _sumFlushDelay) / (double)TicksPerMs / statCount;
            var maxFlushSize = Interlocked.Read(ref _maxFlushSize);
            var maxFlushDelayMs = Interlocked.Read(ref _maxFlushDelay) / (double)TicksPerMs;
            var queuedFlushMessages = FlushMessagesInQueue;

            var stats = new Dictionary<string, object>
            {
                {"es-writer-lastFlushSize", new StatMetadata(lastFlushSize, "Writer Last Flush Size")},
                {"es-writer-lastFlushDelayMs", new StatMetadata(lastFlushDelayMs, "Writer Last Flush Delay, ms")},
                {"es-writer-meanFlushSize", new StatMetadata(meanFlushSize, "Writer Mean Flush Size")},
                {"es-writer-meanFlushDelayMs", new StatMetadata(meanFlushDelayMs, "Writer Mean Flush Delay, ms")},
                {"es-writer-maxFlushSize", new StatMetadata(maxFlushSize, "Writer Max Flush Size")},
                {"es-writer-maxFlushDelayMs", new StatMetadata(maxFlushDelayMs, "Writer Max Flush Delay, ms")},
                {"es-writer-queuedFlushMessages", new StatMetadata(queuedFlushMessages, "Writer Queued Flush Message")}
            };

            message.Envelope.ReplyWith(new MonitoringMessage.InternalStatsRequestResponse(stats));
        }
    }
}