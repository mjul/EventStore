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
using System.Security.Principal;
using EventStore.Common.Log;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.TransactionLog.Checkpoint;
using ReadStreamResult = EventStore.Core.Data.ReadStreamResult;

namespace EventStore.Core.Services.Storage
{
    public class StorageReaderWorker: IHandle<ClientMessage.ReadEvent>,
                                      IHandle<ClientMessage.ReadStreamEventsBackward>,
                                      IHandle<ClientMessage.ReadStreamEventsForward>,
                                      IHandle<ClientMessage.ReadAllEventsForward>,
                                      IHandle<ClientMessage.ReadAllEventsBackward>,
                                      IHandle<StorageMessage.CheckStreamAccess>
    {
        private static readonly ILogger Log = LogManager.GetLoggerFor<StorageReaderWorker>();

        private readonly IReadIndex _readIndex;
        private readonly ICheckpoint _writerCheckpoint;

        public StorageReaderWorker(IReadIndex readIndex, ICheckpoint writerCheckpoint)
        {
            Ensure.NotNull(readIndex, "readIndex");
            Ensure.NotNull(writerCheckpoint, "writerCheckpoint");

            _readIndex = readIndex;
            _writerCheckpoint = writerCheckpoint;
        }

        void IHandle<ClientMessage.ReadEvent>.Handle(ClientMessage.ReadEvent msg)
        {
            msg.Envelope.ReplyWith(ReadEvent(msg));
        }

        void IHandle<ClientMessage.ReadStreamEventsForward>.Handle(ClientMessage.ReadStreamEventsForward msg)
        {
            msg.Envelope.ReplyWith(ReadStreamEventsForward(msg));
        }

        void IHandle<ClientMessage.ReadStreamEventsBackward>.Handle(ClientMessage.ReadStreamEventsBackward msg)
        {
            msg.Envelope.ReplyWith(ReadStreamEventsBackward(msg));
        }

        void IHandle<ClientMessage.ReadAllEventsForward>.Handle(ClientMessage.ReadAllEventsForward msg)
        {
            msg.Envelope.ReplyWith(ReadAllEventsForward(msg));
        }

        void IHandle<ClientMessage.ReadAllEventsBackward>.Handle(ClientMessage.ReadAllEventsBackward msg)
        {
            msg.Envelope.ReplyWith(ReadAllEventsBackward(msg));
        }

        void IHandle<StorageMessage.CheckStreamAccess>.Handle(StorageMessage.CheckStreamAccess msg)
        {
            msg.Envelope.ReplyWith(CheckStreamAccess(msg));
        }

        private ClientMessage.ReadEventCompleted ReadEvent(ClientMessage.ReadEvent msg)
        {
            try
            {
                if (StreamAccessResult.Granted != _readIndex.CheckStreamAccess(msg.EventStreamId, StreamAccessType.Read, msg.User))
                    return NoData(msg, ReadEventResult.AccessDenied);

                var result = _readIndex.ReadEvent(msg.EventStreamId, msg.EventNumber);
                var record = result.Result == ReadEventResult.Success && msg.ResolveLinkTos
                                     ? ResolveLinkToEvent(result.Record, msg.User)
                                     : new ResolvedEvent(result.Record);
                if (record == null)
                    return NoData(msg, ReadEventResult.AccessDenied);

                return new ClientMessage.ReadEventCompleted(msg.CorrelationId, msg.EventStreamId,
                                                            result.Result, record.Value, result.Metadata, null);
            }
            catch (Exception exc)
            {
                Log.ErrorException(exc, "Error during processing ReadEvent request.");
                return NoData(msg, ReadEventResult.Error, exc.Message);
            }
        }

        private ClientMessage.ReadStreamEventsForwardCompleted ReadStreamEventsForward(ClientMessage.ReadStreamEventsForward msg)
        {
            var lastCommitPosition = _readIndex.LastCommitPosition;
            try
            {
                if (msg.ValidationStreamVersion.HasValue && _readIndex.GetLastStreamEventNumber(msg.EventStreamId) == msg.ValidationStreamVersion)
                    return NoData(msg, ReadStreamResult.NotModified, lastCommitPosition);
                if (StreamAccessResult.Granted != _readIndex.CheckStreamAccess(msg.EventStreamId, StreamAccessType.Read, msg.User))
                    return NoData(msg, ReadStreamResult.AccessDenied, lastCommitPosition);

                var result = _readIndex.ReadStreamEventsForward(msg.EventStreamId, msg.FromEventNumber, msg.MaxCount);
                CheckEventsOrder(msg, result);
                var resolvedPairs = ResolveLinkToEvents(result.Records, msg.ResolveLinkTos, msg.User);
                if (resolvedPairs == null)
                    return NoData(msg, ReadStreamResult.AccessDenied, lastCommitPosition);

                return new ClientMessage.ReadStreamEventsForwardCompleted(
                    msg.CorrelationId, msg.EventStreamId, msg.FromEventNumber, msg.MaxCount,
                    (ReadStreamResult) result.Result, resolvedPairs, result.Metadata, string.Empty,
                    result.NextEventNumber, result.LastEventNumber, result.IsEndOfStream, lastCommitPosition);
            }
            catch (Exception exc)
            {
                Log.ErrorException(exc, "Error during processing ReadStreamEventsForward request.");
                return NoData(msg, ReadStreamResult.Error, lastCommitPosition, exc.Message);
            }
        }

        private ClientMessage.ReadStreamEventsBackwardCompleted ReadStreamEventsBackward(ClientMessage.ReadStreamEventsBackward msg)
        {
            var lastCommitPosition = _readIndex.LastCommitPosition;
            try
            {
                if (msg.ValidationStreamVersion.HasValue && _readIndex.GetLastStreamEventNumber(msg.EventStreamId) == msg.ValidationStreamVersion)
                    return NoData(msg, ReadStreamResult.NotModified, lastCommitPosition);
                if (StreamAccessResult.Granted != _readIndex.CheckStreamAccess(msg.EventStreamId, StreamAccessType.Read, msg.User))
                    return NoData(msg, ReadStreamResult.AccessDenied, lastCommitPosition);

                var result = _readIndex.ReadStreamEventsBackward(msg.EventStreamId, msg.FromEventNumber, msg.MaxCount);
                CheckEventsOrder(msg, result);
                var resolvedPairs = ResolveLinkToEvents(result.Records, msg.ResolveLinkTos, msg.User);
                if (resolvedPairs == null)
                    return NoData(msg, ReadStreamResult.AccessDenied, lastCommitPosition);

                return new ClientMessage.ReadStreamEventsBackwardCompleted(
                    msg.CorrelationId, msg.EventStreamId, result.FromEventNumber, result.MaxCount,
                    (ReadStreamResult)result.Result, resolvedPairs, result.Metadata, string.Empty,
                    result.NextEventNumber, result.LastEventNumber, result.IsEndOfStream, lastCommitPosition);
            }
            catch (Exception exc)
            {
                Log.ErrorException(exc, "Error during processing ReadStreamEventsBackward request.");
                return NoData(msg, ReadStreamResult.Error, lastCommitPosition, exc.Message);
            }
        }

        private ClientMessage.ReadAllEventsForwardCompleted ReadAllEventsForward(ClientMessage.ReadAllEventsForward msg)
        {
            var pos = new TFPos(msg.CommitPosition, msg.PreparePosition);
            try
            {
                if (pos == TFPos.HeadOfTf)
                {
                    var checkpoint = _writerCheckpoint.Read();
                    pos = new TFPos(checkpoint, checkpoint);
                }
                if (pos.CommitPosition < 0 || pos.PreparePosition < 0)
                    return NoData(msg, ReadAllResult.Error, pos, "Invalid position.");
                if (msg.ValidationTfEofPosition.HasValue && _readIndex.LastCommitPosition == msg.ValidationTfEofPosition.Value)
                    return NoData(msg, ReadAllResult.NotModified, pos);
                if (StreamAccessResult.Granted != _readIndex.CheckStreamAccess(SystemStreams.AllStream, StreamAccessType.Read, msg.User))
                    return NoData(msg, ReadAllResult.AccessDenied, pos);

                var res = _readIndex.ReadAllEventsForward(pos, msg.MaxCount);
                var resolved = ResolveReadAllResult(res.Records, msg.ResolveLinkTos, msg.User);
                if (resolved == null)
                    return NoData(msg, ReadAllResult.AccessDenied, pos);

                return new ClientMessage.ReadAllEventsForwardCompleted(
                    msg.CorrelationId, ReadAllResult.Success, null, resolved, res.Metadata, msg.MaxCount,
                    res.CurrentPos, res.NextPos, res.PrevPos, res.TfEofPosition);
            }
            catch (Exception exc)
            {
                Log.ErrorException(exc, "Error during processing ReadAllEventsForward request.");
                return NoData(msg, ReadAllResult.Error, pos, exc.Message);
            }
        }

        private ClientMessage.ReadAllEventsBackwardCompleted ReadAllEventsBackward(ClientMessage.ReadAllEventsBackward msg)
        {
            var pos = new TFPos(msg.CommitPosition, msg.PreparePosition);
            try
            {
                if (pos == TFPos.HeadOfTf)
                {
                    var checkpoint = _writerCheckpoint.Read();
                    pos = new TFPos(checkpoint, checkpoint);
                }
                if (pos.CommitPosition < 0 || pos.PreparePosition < 0)
                    return NoData(msg, ReadAllResult.Error, pos, "Invalid position.");
                if (msg.ValidationTfEofPosition.HasValue && _readIndex.LastCommitPosition == msg.ValidationTfEofPosition.Value)
                    return NoData(msg, ReadAllResult.NotModified, pos);
                if (StreamAccessResult.Granted != _readIndex.CheckStreamAccess(SystemStreams.AllStream, StreamAccessType.Read, msg.User))
                    return NoData(msg, ReadAllResult.AccessDenied, pos);

                var res = _readIndex.ReadAllEventsBackward(pos, msg.MaxCount);
                var resolved = ResolveReadAllResult(res.Records, msg.ResolveLinkTos, msg.User);
                if (resolved == null)
                    return NoData(msg, ReadAllResult.AccessDenied, pos);

                return new ClientMessage.ReadAllEventsBackwardCompleted(
                    msg.CorrelationId, ReadAllResult.Success, null, resolved, res.Metadata, msg.MaxCount,
                    res.CurrentPos, res.NextPos, res.PrevPos, res.TfEofPosition);
            }
            catch (Exception exc)
            {
                Log.ErrorException(exc, "Error during processing ReadAllEventsBackward request.");
                return NoData(msg, ReadAllResult.Error, pos, exc.Message);
            }
        }

        private StorageMessage.CheckStreamAccessCompleted CheckStreamAccess(StorageMessage.CheckStreamAccess msg)
        {
            string streamId = msg.EventStreamId;
            try
            {
                if (msg.EventStreamId == null)
                {
                    if (msg.TransactionId == null) throw new Exception("No transaction ID specified.");
                    var transInfo = _readIndex.GetTransactionInfo(_writerCheckpoint.Read(), msg.TransactionId.Value);
                    if (transInfo.TransactionOffset < -1 || transInfo.EventStreamId.IsEmptyString())
                    {
                        throw new Exception(
                            string.Format("Invalid transaction info found for transaction ID {0}. "
                                            + "Possibly wrong transaction ID provided. TransactionOffset: {1}, EventStreamId: {2}",
                                            msg.TransactionId, transInfo.TransactionOffset,
                                            transInfo.EventStreamId.IsEmptyString() ? "<null>" : transInfo.EventStreamId));
                    }
                    streamId = transInfo.EventStreamId;
                }

                var result = _readIndex.CheckStreamAccess(streamId, msg.AccessType, msg.User);
                return new StorageMessage.CheckStreamAccessCompleted(msg.CorrelationId, streamId, msg.TransactionId, msg.AccessType, result);
            }
            catch (Exception exc)
            {
                Log.ErrorException(exc, "Error during processing CheckStreamAccess({0}, {1}) request.", msg.EventStreamId, msg.TransactionId);
                return new StorageMessage.CheckStreamAccessCompleted(msg.CorrelationId, streamId, msg.TransactionId, 
                                                                     msg.AccessType, StreamAccessResult.Denied);
            }
        }

        private static ClientMessage.ReadEventCompleted NoData(ClientMessage.ReadEvent msg, ReadEventResult result, string error = null)
        {
            return new ClientMessage.ReadEventCompleted(msg.CorrelationId, msg.EventStreamId, result, new ResolvedEvent(null), null, error);
        }

        private static ClientMessage.ReadStreamEventsForwardCompleted NoData(ClientMessage.ReadStreamEventsForward msg, ReadStreamResult result, long lastCommitPosition, string error = null)
        {
            return ClientMessage.ReadStreamEventsForwardCompleted.NoData(
                result, msg.CorrelationId, msg.EventStreamId, msg.FromEventNumber, msg.MaxCount, lastCommitPosition, error);
        }

        private static ClientMessage.ReadStreamEventsBackwardCompleted NoData(ClientMessage.ReadStreamEventsBackward msg, ReadStreamResult result, long lastCommitPosition, string error = null)
        {
            return ClientMessage.ReadStreamEventsBackwardCompleted.NoData(
                result, msg.CorrelationId, msg.EventStreamId, msg.FromEventNumber, msg.MaxCount, lastCommitPosition, error);
        }

        private ClientMessage.ReadAllEventsForwardCompleted NoData(ClientMessage.ReadAllEventsForward msg, ReadAllResult result, TFPos pos, string error = null)
        {
            return new ClientMessage.ReadAllEventsForwardCompleted(
                msg.CorrelationId, result, error, ResolvedEvent.EmptyArray, null,
                msg.MaxCount, pos, TFPos.Invalid, TFPos.Invalid, _writerCheckpoint.Read());
        }

        private ClientMessage.ReadAllEventsBackwardCompleted NoData(ClientMessage.ReadAllEventsBackward msg, ReadAllResult result, TFPos pos, string error = null)
        {
            return new ClientMessage.ReadAllEventsBackwardCompleted(
                msg.CorrelationId, result, error, ResolvedEvent.EmptyArray, null,
                msg.MaxCount, pos, TFPos.Invalid, TFPos.Invalid, _writerCheckpoint.Read());
        }

        private static void CheckEventsOrder(ClientMessage.ReadStreamEventsForward msg, IndexReadStreamResult result)
        {
            for (var index = 1; index < result.Records.Length; index++)
            {
                if (result.Records[index].EventNumber != result.Records[index - 1].EventNumber + 1)
                {
                    throw new Exception(
                            string.Format("Invalid order of events has been detected in read index for the event stream '{0}'. "
                                          + "The event {1} at position {2} goes after the event {3} at position {4}",
                                          msg.EventStreamId,
                                          result.Records[index].EventNumber,
                                          result.Records[index].LogPosition,
                                          result.Records[index - 1].EventNumber,
                                          result.Records[index - 1].LogPosition));
                }
            }
        }

        private static void CheckEventsOrder(ClientMessage.ReadStreamEventsBackward msg, IndexReadStreamResult result)
        {
            for (var index = 1; index < result.Records.Length; index++)
            {
                if (result.Records[index].EventNumber != result.Records[index - 1].EventNumber - 1)
                {
                    throw new Exception(string.Format("Invalid order of events has been detected in read index for the event stream '{0}'. "
                                                      + "The event {1} at position {2} goes after the event {3} at position {4}",
                                                      msg.EventStreamId,
                                                      result.Records[index].EventNumber,
                                                      result.Records[index].LogPosition,
                                                      result.Records[index - 1].EventNumber,
                                                      result.Records[index - 1].LogPosition));
                }
            }
        }

        private ResolvedEvent[] ResolveLinkToEvents(EventRecord[] records, bool resolveLinks, IPrincipal user)
        {
            var resolved = new ResolvedEvent[records.Length];
            if (resolveLinks)
            {
                for (int i = 0; i < records.Length; i++)
                {
                    var rec = ResolveLinkToEvent(records[i], user);
                    if (rec == null)
                        return null;
                    resolved[i] = rec.Value;
                }
            }
            else
            {
                for (int i = 0; i < records.Length; ++i)
                {
                    resolved[i] = new ResolvedEvent(records[i]);
                }
            }
            return resolved;
        }

        private ResolvedEvent? ResolveLinkToEvent(EventRecord eventRecord, IPrincipal user)
        {
            if (eventRecord.EventType == SystemEventTypes.LinkTo)
            {
                try
                {
                    string[] parts = Helper.UTF8NoBom.GetString(eventRecord.Data).Split('@');
                    int eventNumber = int.Parse(parts[0]);
                    string streamId = parts[1];

                    if (StreamAccessResult.Granted != _readIndex.CheckStreamAccess(streamId, StreamAccessType.Read, user))
                        return null;

                    var res = _readIndex.ReadEvent(streamId, eventNumber);
                    if (res.Result == ReadEventResult.Success)
                        return new ResolvedEvent(res.Record, eventRecord);
                }
                catch (Exception exc)
                {
                    Log.ErrorException(exc, "Error while resolving link for event record: {0}", eventRecord.ToString());
                }
            }
            return new ResolvedEvent(eventRecord);
        }

        private ResolvedEvent[] ResolveReadAllResult(IList<CommitEventRecord> records, bool resolveLinks, IPrincipal user)
        {
            var result = new ResolvedEvent[records.Count];
            if (resolveLinks)
            {
                for (int i = 0; i < result.Length; ++i)
                {
                    var record = records[i];
                    var resolvedPair = ResolveLinkToEvent(record.Event, user);
                    if (resolvedPair == null)
                        return null;
                    result[i] = new ResolvedEvent(resolvedPair.Value.Event, resolvedPair.Value.Link, record.CommitPosition);
                }
            }
            else
            {
                for (int i = 0; i < result.Length; ++i)
                {
                    result[i] = new ResolvedEvent(records[i].Event, null, records[i].CommitPosition);
                }
            }
            return result;
        }
    }
}