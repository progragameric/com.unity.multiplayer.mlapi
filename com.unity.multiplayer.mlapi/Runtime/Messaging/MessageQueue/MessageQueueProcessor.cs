using System;
using System.Collections.Generic;
using Unity.Profiling;
using MLAPI.Configuration;
using MLAPI.Profiling;
using MLAPI.Logging;
using UnityEngine;

namespace MLAPI.Messaging
{
    /// <summary>
    /// MessageQueueProcessing
    /// Handles processing of MessageQueues
    /// Inbound to invocation
    /// Outbound to send
    /// </summary>
    internal class MessageQueueProcessor
    {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private static ProfilerMarker s_ProcessReceiveQueue = new ProfilerMarker($"{nameof(MessageQueueProcessor)}.{nameof(ProcessReceiveQueue)}");
        private static ProfilerMarker s_ProcessSendQueue = new ProfilerMarker($"{nameof(MessageQueueProcessor)}.{nameof(ProcessSendQueue)}");
#endif

        // Batcher object used to manage the RPC batching on the send side
        private readonly MessageBatcher m_MessageBatcher = new MessageBatcher();
        private const int k_BatchThreshold = 512;

        //The MessageQueueContainer that is associated with this MessageQueueProcessor
        private MessageQueueContainer m_MessageQueueContainer;

        private readonly NetworkManager m_NetworkManager;

        public void ProcessMessage(in MessageFrameItem item)
        {
            try
            {
                switch (item.MessageType)
                {
                    case MessageQueueContainer.MessageType.ClientRpc:
                    case MessageQueueContainer.MessageType.ServerRpc:
                        // Can rely on currentStage == the original updateStage in the buffer
                        // After all, that's the whole point of it being in the buffer.
                        m_NetworkManager.InvokeRpc(item, item.UpdateStage);
                        break;
                    case MessageQueueContainer.MessageType.ConnectionRequest:
                        if (m_NetworkManager.IsServer)
                        {
                            m_NetworkManager.MessageHandler.HandleConnectionRequest(item.NetworkId, item.NetworkBuffer);
                        }

                        break;
                    case MessageQueueContainer.MessageType.ConnectionApproved:
                        if (m_NetworkManager.IsClient)
                        {
                            m_NetworkManager.MessageHandler.HandleConnectionApproved(item.NetworkId, item.NetworkBuffer, item.Timestamp);
                        }

                        break;
                    case MessageQueueContainer.MessageType.CreateObject:
                        if (m_NetworkManager.IsClient)
                        {
                            m_NetworkManager.MessageHandler.HandleAddObject(item.NetworkId, item.NetworkBuffer);
                        }

                        break;
                    case MessageQueueContainer.MessageType.DestroyObject:
                        if (m_NetworkManager.IsClient)
                        {
                            m_NetworkManager.MessageHandler.HandleDestroyObject(item.NetworkId, item.NetworkBuffer);
                        }

                        break;
                    case MessageQueueContainer.MessageType.ChangeOwner:
                        if (m_NetworkManager.IsClient)
                        {
                            m_NetworkManager.MessageHandler.HandleChangeOwner(item.NetworkId, item.NetworkBuffer);
                        }

                        break;
                    case MessageQueueContainer.MessageType.TimeSync:
                        if (m_NetworkManager.IsClient)
                        {
                            m_NetworkManager.MessageHandler.HandleTimeSync(item.NetworkId, item.NetworkBuffer, item.Timestamp);
                        }

                        break;
                    default:
                        NetworkLog.LogWarning($"Received unknown message {((int)item.MessageType).ToString()}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);

                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal)
                {
                    NetworkLog.LogWarning($"A {item.MessageType} threw an exception while executing! Please check Unity logs for more information.");
                }
            }
        }

        /// <summary>
        /// ProcessReceiveQueue
        /// Public facing interface method to start processing all RPCs in the current inbound frame
        /// </summary>
        public void ProcessReceiveQueue(NetworkUpdateStage currentStage, bool isTesting)
        {
            bool advanceFrameHistory = false;
            if (!ReferenceEquals(m_MessageQueueContainer, null))
            {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                s_ProcessReceiveQueue.Begin();
#endif
                var currentFrame = m_MessageQueueContainer.GetQueueHistoryFrame(MessageQueueHistoryFrame.QueueFrameType.Inbound, currentStage);
                var nextFrame = m_MessageQueueContainer.GetQueueHistoryFrame(MessageQueueHistoryFrame.QueueFrameType.Inbound, currentStage, true);
                if (nextFrame.IsDirty && nextFrame.HasLoopbackData)
                {
                    advanceFrameHistory = true;
                }

                if (currentFrame != null && currentFrame.IsDirty)
                {
                    var currentQueueItem = currentFrame.GetFirstQueueItem();
                    while (currentQueueItem.MessageType != MessageQueueContainer.MessageType.None)
                    {
                        advanceFrameHistory = true;

                        if (!isTesting)
                        {
                            currentQueueItem.UpdateStage = currentStage;
                            ProcessMessage(currentQueueItem);
                        }

                        ProfilerStatManager.MessagesQueueProc.Record();
                        PerformanceDataManager.Increment(ProfilerConstants.MessageQueueProcessed);
                        currentQueueItem = currentFrame.GetNextQueueItem();
                    }

                    //We call this to dispose of the shared stream writer and stream
                    currentFrame.CloseQueue();
                }

                if (advanceFrameHistory)
                {
                    m_MessageQueueContainer.AdvanceFrameHistory(MessageQueueHistoryFrame.QueueFrameType.Inbound);
                }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
                s_ProcessReceiveQueue.End();
#endif
            }
        }

        /// <summary>
        /// ProcessSendQueue
        /// Called to send both performance RPC and internal messages and then flush the outbound queue
        /// </summary>
        internal void ProcessSendQueue(bool isListening)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_ProcessSendQueue.Begin();
#endif

            MessageQueueSendAndFlush(isListening);

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_ProcessSendQueue.End();
#endif
        }

        /// <summary>
        /// Sends all RPC queue items in the current outbound frame
        /// </summary>
        /// <param name="isListening">if flase it will just process through the queue items but attempt to send</param>
        private void MessageQueueSendAndFlush(bool isListening)
        {
            var advanceFrameHistory = false;
            if (!ReferenceEquals(m_MessageQueueContainer, null))
            {
                var currentFrame = m_MessageQueueContainer.GetCurrentFrame(MessageQueueHistoryFrame.QueueFrameType.Outbound, NetworkUpdateStage.PostLateUpdate);
                if (currentFrame != null)
                {
                    var currentQueueItem = currentFrame.GetFirstQueueItem();
                    while (currentQueueItem.MessageType != MessageQueueContainer.MessageType.None)
                    {
                        advanceFrameHistory = true;
                        if (m_MessageQueueContainer.IsUsingBatching())
                        {
                            m_MessageBatcher.QueueItem(currentQueueItem);

                            if (isListening)
                            {
                                m_MessageBatcher.SendItems(k_BatchThreshold, SendCallback);
                            }
                        }
                        else
                        {
                            if (isListening)
                            {
                                SendFrameQueueItem(currentQueueItem);
                            }
                        }

                        currentQueueItem = currentFrame.GetNextQueueItem();
                    }

                    //If the size is < m_BatchThreshold then just send the messages
                    if (advanceFrameHistory && m_MessageQueueContainer.IsUsingBatching())
                    {
                        m_MessageBatcher.SendItems(0, SendCallback);
                    }
                }

                //If we processed any RPCs, then advance to the next frame
                if (advanceFrameHistory)
                {
                    m_MessageQueueContainer.AdvanceFrameHistory(MessageQueueHistoryFrame.QueueFrameType.Outbound);
                }
            }
        }

        /// <summary>
        /// This is the callback from the batcher when it need to send a batch
        /// </summary>
        /// <param name="clientId"> clientId to send to</param>
        /// <param name="sendStream"> the stream to send</param>
        private void SendCallback(ulong clientId, MessageBatcher.SendStream sendStream)
        {
            var length = (int)sendStream.Buffer.Length;
            var bytes = sendStream.Buffer.GetBuffer();
            var sendBuffer = new ArraySegment<byte>(bytes, 0, length);

            m_MessageQueueContainer.NetworkManager.NetworkConfig.NetworkTransport.Send(clientId, sendBuffer, sendStream.NetworkChannel);
        }

        /// <summary>
        /// SendFrameQueueItem
        /// Sends the RPC Queue Item to the specified destination
        /// </summary>
        /// <param name="item">Information on what to send</param>
        private void SendFrameQueueItem(MessageFrameItem item)
        {
            switch (item.MessageType)
            {
                case MessageQueueContainer.MessageType.ServerRpc:
                    {
                        m_MessageQueueContainer.NetworkManager.NetworkConfig.NetworkTransport.Send(item.NetworkId, item.MessageData, item.NetworkChannel);

                        //For each packet sent, we want to record how much data we have sent

                        PerformanceDataManager.Increment(ProfilerConstants.ByteSent, (int)item.StreamSize);
                        PerformanceDataManager.Increment(ProfilerConstants.RpcSent);
                        ProfilerStatManager.BytesSent.Record((int)item.StreamSize);
                        ProfilerStatManager.MessagesSent.Record();
                        break;
                    }
                case MessageQueueContainer.MessageType.ClientRpc:
                    {
                        foreach (ulong clientid in item.ClientNetworkIds)
                        {
                            m_MessageQueueContainer.NetworkManager.NetworkConfig.NetworkTransport.Send(clientid, item.MessageData, item.NetworkChannel);

                            //For each packet sent, we want to record how much data we have sent
                            PerformanceDataManager.Increment(ProfilerConstants.ByteSent, (int)item.StreamSize);
                            ProfilerStatManager.BytesSent.Record((int)item.StreamSize);
                        }

                        //For each client we send to, we want to record how many RPCs we have sent
                        PerformanceDataManager.Increment(ProfilerConstants.RpcSent, item.ClientNetworkIds.Length);
                        ProfilerStatManager.MessagesSent.Record(item.ClientNetworkIds.Length);

                        break;
                    }
            }
        }

        internal MessageQueueProcessor(MessageQueueContainer messageQueueContainer, NetworkManager networkManager)
        {
            m_MessageQueueContainer = messageQueueContainer;
            m_NetworkManager = networkManager;
        }
    }
}