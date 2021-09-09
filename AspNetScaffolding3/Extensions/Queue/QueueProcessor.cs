﻿using AspNetScaffolding.Extensions.Logger;
using AspNetScaffolding.Extensions.Queue.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetScaffolding.Extensions.Queue
{
    public class QueueProcessor : IQueueProcessor
    {
        private readonly List<Task> Threads = new List<Task>();

        public bool CanAddThread() => (this.Threads.Where(t => t.IsCompleted == false).Count() < this.QueueProcessorSettings.MaxThreads);

        private object LockThreads = new object();

        private object LockAckError = new object();

        public IQueueClient QueueClient { get; set; }

        public QueueSettings QueueProcessorSettings { get; set; }

        public QueueProcessor(IQueueClient queueClient, QueueSettings queueProcessorSettings)
        {
            this.QueueProcessorSettings = queueProcessorSettings;
            this.QueueClient = queueClient;
        }

        public bool ExecuteConsumer(Func<string, int, ulong, Task<bool>> func)
        {
            try
            {
                this.QueueClient.TryConnect();

                this.QueueClient.ReceiveMessage += (message, retryCount, deliveryTag) =>
                {
                    lock (this.LockThreads)
                    {
                        do
                        {
                            this.Threads.Where(t => t.IsCompleted).ToList()
                                        .ForEach(task => task.Dispose());
                            this.Threads.RemoveAll(t => t.IsCompleted);
                        }
                        while (this.CanAddThread() == false);
                    }

                    this.Threads.Add(HandleReceivedMessage(message, retryCount, deliveryTag, func));

                    return Task.CompletedTask;
                };

                SimpleLogger.Info(nameof(QueueProcessor), nameof(ExecuteConsumer), "Queue connected!");

                return true;
            }
            catch (Exception e)
            {
                SimpleLogger.Error(nameof(QueueProcessor), nameof(ExecuteConsumer), "An exception occurred while trying to connect with queue!", e);
                Thread.Sleep(2000);
                return false;
            }
        }

        public async Task HandleReceivedMessage(string message, int retryCount, ulong deliveryTag, Func<string, int, ulong, Task<bool>> func)
        {
            var success = await func.Invoke(message, retryCount, deliveryTag);

            try
            {
                if (success)
                {
                    if (this.QueueProcessorSettings.AutoAckOnSuccess)
                    {
                        this.HandleSuccededEvent(deliveryTag);
                    }
                }
                else
                {
                    this.HandleFailedEvent(message, retryCount, deliveryTag);
                }
            }
            catch (Exception)
            {
                lock (this.LockAckError)
                {
                    SimpleLogger.Error(nameof(QueueProcessor), nameof(HandleReceivedMessage), $"Error on handle received message {deliveryTag}", null);
                    this.QueueClient.TryConnect();
                }
            }
        }

        public void HandleSuccededEvent(ulong deliveryTag)
        {
            this.QueueClient.Ack(deliveryTag);
        }

        public void HandleFailedEvent(string message, int retryCount, ulong deliveryTag)
        {
            if (retryCount < this.QueueProcessorSettings.RetryCount)
            {
                this.QueueClient.AddRetryMessage(message, retryCount + 1);
                this.QueueClient.Ack(deliveryTag);
            }
            else
            {
                this.QueueClient.AddDeadMessage(message);
                this.QueueClient.Ack(deliveryTag);
            }
        }

        public void Dispose()
        {
            this.QueueClient.Dispose();
        }
    }
}