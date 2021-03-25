namespace Ais.Net.EventHubExporter
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reactive.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Threading.Tasks.Dataflow;

    using Ais.Net.Models.Abstractions;
    using Ais.Net.Receiver.Configuration;
    using Ais.Net.Receiver.Receiver;

    using Azure.Messaging.EventHubs;
    using Azure.Messaging.EventHubs.Producer;

    using Endjin.Reactor.EventData.Ais;

    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

    internal class NmeaToEventHubTransferService : IHostedService
    {
        private readonly AisConfig aisConfig;
        private readonly EventHubConfig eventHubConfig;
        private readonly ILogger<NmeaToEventHubTransferService> logger;

        private readonly EventHubProducerClient producer;

        private CancellationTokenSource receiverHostCancellationSource;
        private Task receiverHostRun;
        private ActionBlock<IList<AisMessage>> sendBatchActionBlock;
        private IDisposable subscription;

        public NmeaToEventHubTransferService(
            ILogger<NmeaToEventHubTransferService> logger,
            IOptions<AisConfig> aisOptions,
            IOptions<EventHubConfig> eventHubOptions)
        {
            this.aisConfig = aisOptions.Value;
            this.eventHubConfig = eventHubOptions.Value;
            this.logger = logger;

            this.producer = new EventHubProducerClient(
                this.eventHubConfig.ConnectionString,
                this.eventHubConfig.EventHubName);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            this.logger.LogInformation("NMEA listener to Event Hub transfer service starting");

            this.logger.LogDebug($"Connecting to {this.aisConfig.Host}:{this.aisConfig.Port}");
            INmeaReceiver receiver = new NetworkStreamNmeaReceiver(
                this.aisConfig.Host,
                this.aisConfig.Port,
                this.aisConfig.RetryPeriodicity,
                this.aisConfig.RetryAttempts);

            var receiverHost = new ReceiverHost(receiver);

            this.sendBatchActionBlock = new ActionBlock<IList<AisMessage>>(this.SendAisBatchToEventHubAsync);

            IObservable<IList<AisMessage>> aisBatches = receiverHost.Messages
                .ConvertToDataModel()
                .Buffer(TimeSpan.FromSeconds(5), 75)
                .Where(batch => batch.Count > 0);
            this.subscription = aisBatches.Subscribe(this.sendBatchActionBlock.AsObserver());

            // ReceiverHost.StartAsync is slightly misnamed, because it doesn't actually complete until done
            this.logger.LogDebug("Starting receiver host");
            this.receiverHostCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            this.receiverHostRun = Task.Run(async () =>
            {
                try
                {
                    await receiverHost.StartAsync(this.receiverHostCancellationSource.Token);
                }
                catch (TaskCanceledException)
                {
                    // This is how we are normally shut down.
                }
                catch (Exception x)
                {
                    this.logger.LogError(x, "Receiver encountered error");

                    // TODO: either we should shut down, or we should restart

                    throw;
                }
            });
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            this.logger.LogInformation("NMEA listener to Event Hub transfer service stopping");

            if (this.receiverHostRun != null)
            {
                this.logger.LogDebug("Waiting for receiver host to complete");
                this.receiverHostCancellationSource.Cancel();
                try
                {
                    await this.receiverHostRun.ConfigureAwait(false);
                }
                catch (Exception x)
                {
                    this.logger.LogError(x, "Receiver did not shut down normally");
                }
            }

            if (this.subscription != null)
            {
                this.subscription.Dispose();
            }

            if (this.sendBatchActionBlock != null)
            {
                this.logger.LogDebug("Ensruing all remaining events have been flushed");
                this.sendBatchActionBlock.Complete();
                await this.sendBatchActionBlock.Completion.ConfigureAwait(false);

                this.subscription = null;
            }
            this.logger.LogInformation("NMEA listener to Event Hub transfer service stopped");
        }

        private async Task SendAisBatchToEventHubAsync(IList<AisMessage> batch)
        {
            using EventDataBatch eventBatch = await this.producer.CreateBatchAsync().ConfigureAwait(false);
            foreach (AisMessage message in batch)
            {
                byte[] messageUtf8 = JsonSerializer.SerializeToUtf8Bytes(message);
                var eventData = new EventData(messageUtf8);
                if (!eventBatch.TryAdd(eventData))
                {
                    this.logger.LogCritical("Event hub batch size exceeded");
                }
            }

            this.logger.LogDebug($"Sending batch with {batch.Count} items to Event Hub");
            await this.producer.SendAsync(eventBatch).ConfigureAwait(false);
        }
    }
}