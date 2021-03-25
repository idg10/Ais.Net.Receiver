// <copyright file="ReceiverHost.cs" company="Endjin Limited">
// Copyright (c) Endjin Limited. All rights reserved.
// </copyright>

namespace Ais.Net.Receiver.Receiver
{
    using System;
    using System.Collections.Generic;
    using System.Reactive.Subjects;
    using System.Runtime.CompilerServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using Ais.Net.Models.Abstractions;
    using Ais.Net.Receiver.Parser;

    using Corvus.Retry;
    using Corvus.Retry.Policies;
    using Corvus.Retry.Strategies;

    public class ReceiverHost
    {
        private readonly INmeaReceiver receiver;
        private readonly Subject<string> sentences = new();
        private readonly Subject<IAisMessage> messages = new();

        public ReceiverHost(INmeaReceiver receiver)
        {
            this.receiver = receiver;
        }

        public IObservable<string> Sentences => this.sentences;

        public IObservable<IAisMessage> Messages => this.messages;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await Retriable.RetryAsync(() =>
                            this.StartAsyncInternal(cancellationToken),
                            cancellationToken,
                            new Backoff(maxTries: 100, deltaBackoff: TimeSpan.FromSeconds(5)),
                            new AnyExceptionPolicy(),
                            false);
            }
            catch (OperationCanceledException)
            {
                // We need this to enable flushing out of any messages that were being added to the next
                // batch but which hadn't gone out yet at shutdown. However, it does mean that once we're
                // done, our subject
                this.sentences.OnCompleted();
                this.messages.OnCompleted();
            }
        }

        private async Task StartAsyncInternal(CancellationToken cancellationToken = default)
        {
            var processor = new NmeaToAisMessageTypeProcessor();
            var adapter = new NmeaLineToAisStreamAdapter(processor);

            processor.Messages.Subscribe(this.messages);

            await foreach (string? message in this.GetAsync(cancellationToken).WithCancellation(cancellationToken))
            {
                static void ProcessLineNonAsync(string line, INmeaLineStreamProcessor lineStreamProcessor)
                {
                    byte[]? lineAsAscii = Encoding.ASCII.GetBytes(line);
                    lineStreamProcessor.OnNext(new NmeaLineParser(lineAsAscii), 0);
                }

                this.sentences.OnNext(message);

                if (this.messages.HasObservers)
                {
                    ProcessLineNonAsync(message, adapter);
                }
            }
        }

        private async IAsyncEnumerable<string> GetAsync([EnumeratorCancellation]CancellationToken cancellationToken = default)
        {
            await foreach (string? message in this.receiver.GetAsync(cancellationToken).WithCancellation(cancellationToken))
            {
                if (message.IsMissingNmeaBlockTags())
                {
                    yield return message.PrependNmeaBlockTags();
                }
                else
                {
                    yield return message;
                }
            }
        }
    }
}