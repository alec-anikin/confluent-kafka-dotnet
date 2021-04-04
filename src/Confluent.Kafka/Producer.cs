// Copyright 2016-2018 Confluent Inc.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// Refer to LICENSE for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka.Impl;
using Confluent.Kafka.Internal;


namespace Confluent.Kafka
{
    /// <summary>
    ///     A high level producer with serialization capability.
    /// </summary>
    internal class Producer<TKey, TValue> : IProducer<TKey, TValue>, IClient
    {
        private readonly Producer internalProducer;

        private ISerializer<TKey> keySerializer;
        private ISerializer<TValue> valueSerializer;
        private IAsyncSerializer<TKey> asyncKeySerializer;
        private IAsyncSerializer<TValue> asyncValueSerializer;

        private static readonly Dictionary<Type, object> defaultSerializers = new Dictionary<Type, object>
        {
            { typeof(Null), Serializers.Null },
            { typeof(int), Serializers.Int32 },
            { typeof(long), Serializers.Int64 },
            { typeof(string), Serializers.Utf8 },
            { typeof(float), Serializers.Single },
            { typeof(double), Serializers.Double },
            { typeof(byte[]), Serializers.ByteArray }
        };

        private bool enableDeliveryReportKey = true;
        private bool enableDeliveryReportValue = true;

       

        /// <inheritdoc/>
        public int Poll(TimeSpan timeout)
        {
            return this.internalProducer.Poll(timeout);
        }


        /// <inheritdoc/>
        public int Flush(TimeSpan timeout)
        {
            return this.internalProducer.Flush(timeout);
        }


        /// <inheritdoc/>
        public void Flush(CancellationToken cancellationToken)
        {
            this.internalProducer.Flush(cancellationToken);
        }

        
        /// <inheritdoc/>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }


        /// <summary>
        ///     Releases the unmanaged resources used by the
        ///     <see cref="Producer{TKey,TValue}" />
        ///     and optionally disposes the managed resources.
        /// </summary>
        /// <param name="disposing">
        ///     true to release both managed and unmanaged resources;
        ///     false to release only unmanaged resources.
        /// </param>
        protected virtual void Dispose(bool disposing)
        {
            this.internalProducer.Dispose();
        }


        /// <inheritdoc/>
        public string Name
            => this.internalProducer.Name;


        /// <inheritdoc/>
        public int AddBrokers(string brokers)
            => this.internalProducer.AddBrokers(brokers);


        /// <inheritdoc/>
        public Handle Handle => this.internalProducer.Handle;

        private void InitializeSerializers(
            ISerializer<TKey> keySerializer,
            ISerializer<TValue> valueSerializer,
            IAsyncSerializer<TKey> asyncKeySerializer,
            IAsyncSerializer<TValue> asyncValueSerializer)
        {
            // setup key serializer.
            if (keySerializer == null && asyncKeySerializer == null)
            {
                if (!defaultSerializers.TryGetValue(typeof(TKey), out object serializer))
                {
                    throw new ArgumentNullException(
                        $"Key serializer not specified and there is no default serializer defined for type {typeof(TKey).Name}.");
                }
                this.keySerializer = (ISerializer<TKey>)serializer;
            }
            else if (keySerializer == null && asyncKeySerializer != null)
            {
                this.asyncKeySerializer = asyncKeySerializer;
            }
            else if (keySerializer != null && asyncKeySerializer == null)
            {
                this.keySerializer = keySerializer;
            }
            else
            {
                throw new InvalidOperationException("FATAL: Both async and sync key serializers were set.");
            }

            // setup value serializer.
            if (valueSerializer == null && asyncValueSerializer == null)
            {
                if (!defaultSerializers.TryGetValue(typeof(TValue), out object serializer))
                {
                    throw new ArgumentNullException(
                        $"Value serializer not specified and there is no default serializer defined for type {typeof(TValue).Name}.");
                }
                this.valueSerializer = (ISerializer<TValue>)serializer;
            }
            else if (valueSerializer == null && asyncValueSerializer != null)
            {
                this.asyncValueSerializer = asyncValueSerializer;
            }
            else if (valueSerializer != null && asyncValueSerializer == null)
            {
                this.valueSerializer = valueSerializer;
            }
            else
            {
                throw new InvalidOperationException("FATAL: Both async and sync value serializers were set.");
            }
        }

        internal Producer(DependentProducerBuilder<TKey, TValue> builder)
        {
            this.internalProducer = new Producer(builder);

            InitializeSerializers(
                builder.KeySerializer, builder.ValueSerializer,
                builder.AsyncKeySerializer, builder.AsyncValueSerializer);
        }

        internal Producer(ProducerBuilder<TKey, TValue> builder)
        {
            var baseConfig = builder.ConstructBaseConfig(this);
           
            var deliveryReportEnabledFieldsObj = baseConfig.config.FirstOrDefault(prop => prop.Key == ConfigPropertyNames.Producer.DeliveryReportFields).Value;
            if (deliveryReportEnabledFieldsObj != null)
            {
                var fields = deliveryReportEnabledFieldsObj.Replace(" ", "");
                if (fields != "all")
                {
                    this.enableDeliveryReportKey = false;
                    this.enableDeliveryReportValue = false;
                    if (fields != "none")
                    {
                        var parts = fields.Split(',');
                        foreach (var part in parts)
                        {
                            switch (part)
                            {

                                //TODO : default 
                                case "key": this.enableDeliveryReportKey = true; break;
                                case "value": this.enableDeliveryReportValue = true; break;
                                default: throw new ArgumentException(
                                    $"Unknown delivery report field name '{part}' in config value '{ConfigPropertyNames.Producer.DeliveryReportFields}'.");
                            }
                        }
                    }
                }
            }

            this.internalProducer = new Producer(baseConfig);

            InitializeSerializers(
                builder.KeySerializer, builder.ValueSerializer,
                builder.AsyncKeySerializer, builder.AsyncValueSerializer);
        }


        /// <inheritdoc/>
        public async Task<DeliveryResult<TKey, TValue>> ProduceAsync(
            TopicPartition topicPartition,
            Message<TKey, TValue> message,
            CancellationToken cancellationToken)
        {
            Headers headers = message.Headers ?? new Headers();

            byte[] keyBytes;
            try
            {
                keyBytes = (keySerializer != null)
                    ? keySerializer.Serialize(message.Key, new SerializationContext(MessageComponentType.Key, topicPartition.Topic, headers))
                    : await asyncKeySerializer.SerializeAsync(message.Key, new SerializationContext(MessageComponentType.Key, topicPartition.Topic, headers)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new ProduceException<TKey, TValue>(
                    new Error(ErrorCode.Local_KeySerialization),
                    new DeliveryResult<TKey, TValue>
                    {
                        Message = message,
                        TopicPartitionOffset = new TopicPartitionOffset(topicPartition, Offset.Unset)
                    },
                    ex);
            }

            byte[] valBytes;
            try
            {
                valBytes = (valueSerializer != null)
                    ? valueSerializer.Serialize(message.Value, new SerializationContext(MessageComponentType.Value, topicPartition.Topic, headers))
                    : await asyncValueSerializer.SerializeAsync(message.Value, new SerializationContext(MessageComponentType.Value, topicPartition.Topic, headers)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new ProduceException<TKey, TValue>(
                    new Error(ErrorCode.Local_ValueSerialization),
                    new DeliveryResult<TKey, TValue>
                    {
                        Message = message,
                        TopicPartitionOffset = new TopicPartitionOffset(topicPartition, Offset.Unset)
                    },
                    ex);
            }

            try
            {
                if (enableDeliveryReports)
                {
                    var handler = new TypedTaskDeliveryHandlerShim(
                        topicPartition.Topic,
                        enableDeliveryReportKey ? message.Key : default(TKey),
                        enableDeliveryReportValue ? message.Value : default(TValue));

                    if (cancellationToken != null && cancellationToken.CanBeCanceled)
                    {
                        handler.CancellationTokenRegistration
                            = cancellationToken.Register(() => handler.TrySetCanceled());
                    }

                    this.internalProducer.ProduceImpl(
                        topicPartition.Topic,
                        valBytes,
                        keyBytes,
                        message.Timestamp, topicPartition.Partition, headers,
                        handler);

                    return await handler.Task.ConfigureAwait(false);
                }
                else
                {
                    this.internalProducer.ProduceImpl(
                        topicPartition.Topic, 
                        valBytes,
                        keyBytes,
                        message.Timestamp, topicPartition.Partition, headers, 
                        null);

                    var result = new DeliveryResult<TKey, TValue>
                    {
                        TopicPartitionOffset = new TopicPartitionOffset(topicPartition, Offset.Unset),
                        Message = message
                    };

                    return result;
                }
            }
            catch (KafkaException ex)
            {
                throw new ProduceException<TKey, TValue>(
                    ex.Error,
                    new DeliveryResult<TKey, TValue>
                    {
                        Message = message,
                        TopicPartitionOffset = new TopicPartitionOffset(topicPartition, Offset.Unset)
                    });
            }
        }


        /// <inheritdoc/>
        public Task<DeliveryResult<TKey, TValue>> ProduceAsync(
            string topic,
            Message<TKey, TValue> message,
            CancellationToken cancellationToken)
            => ProduceAsync(new TopicPartition(topic, Partition.Any), message, cancellationToken);


        /// <inheritdoc/>
        public void Produce(
            string topic,
            Message<TKey, TValue> message,
            Action<DeliveryReport<TKey, TValue>> deliveryHandler = null
        )
            => Produce(new TopicPartition(topic, Partition.Any), message, deliveryHandler);


        /// <inheritdoc/>
        public void Produce(
            TopicPartition topicPartition,
            Message<TKey, TValue> message,
            Action<DeliveryReport<TKey, TValue>> deliveryHandler = null)
        {
            if (deliveryHandler != null && !enableDeliveryReports)
            {
                throw new InvalidOperationException("A delivery handler was specified, but delivery reports are disabled.");
            }

            Headers headers = message.Headers ?? new Headers();

            byte[] keyBytes;
            try
            {
                keyBytes = (keySerializer != null)
                    ? keySerializer.Serialize(message.Key, new SerializationContext(MessageComponentType.Key, topicPartition.Topic, headers))
                    : throw new InvalidOperationException("Produce called with an IAsyncSerializer key serializer configured but an ISerializer is required.");
            }
            catch (Exception ex)
            {
                throw new ProduceException<TKey, TValue>(
                    new Error(ErrorCode.Local_KeySerialization, ex.ToString()),
                    new DeliveryResult<TKey, TValue>
                    {
                        Message = message,
                        TopicPartitionOffset = new TopicPartitionOffset(topicPartition, Offset.Unset),
                    },
                    ex);
            }

            byte[] valBytes;
            try
            {
                valBytes = (valueSerializer != null)
                    ? valueSerializer.Serialize(message.Value, new SerializationContext(MessageComponentType.Value, topicPartition.Topic, headers))
                    : throw new InvalidOperationException("Produce called with an IAsyncSerializer value serializer configured but an ISerializer is required.");
            }
            catch (Exception ex)
            {
                throw new ProduceException<TKey, TValue>(
                    new Error(ErrorCode.Local_ValueSerialization, ex.ToString()),
                    new DeliveryResult<TKey, TValue>
                    {
                        Message = message,
                        TopicPartitionOffset = new TopicPartitionOffset(topicPartition, Offset.Unset),
                    },
                    ex);
            }

            try
            {
                this.internalProducer.ProduceImpl(
                    topicPartition.Topic,
                    valBytes,
                    keyBytes,
                    message.Timestamp, topicPartition.Partition, 
                    headers,
                    new TypedDeliveryHandlerShim_Action(
                        topicPartition.Topic,
                        enableDeliveryReportKey ? message.Key : default(TKey),
                        enableDeliveryReportValue ? message.Value : default(TValue),
                        deliveryHandler));
            }
            catch (KafkaException ex)
            {
                throw new ProduceException<TKey, TValue>(
                    ex.Error,
                    new DeliveryReport<TKey, TValue>
                        {
                            Message = message,
                            TopicPartitionOffset = new TopicPartitionOffset(topicPartition, Offset.Unset)
                        });
            }
        }

        private class TypedTaskDeliveryHandlerShim : TaskCompletionSource<DeliveryResult<TKey, TValue>>, IDeliveryHandler
        {
            public TypedTaskDeliveryHandlerShim(string topic, TKey key, TValue val)
#if !NET45
                : base(TaskCreationOptions.RunContinuationsAsynchronously)
#endif
            {
                Topic = topic;
                Key = key;
                Value = val;
            }

            public CancellationTokenRegistration CancellationTokenRegistration;

            public string Topic;

            public TKey Key;

            public TValue Value;

            public void HandleDeliveryReport(DeliveryReport<Null, Null> deliveryReport)
            {
                if (CancellationTokenRegistration != null)
                {
                    CancellationTokenRegistration.Dispose();
                }

                if (deliveryReport == null)
                {
#if NET45
                    System.Threading.Tasks.Task.Run(() => TrySetResult(null));
#else
                    TrySetResult(null);
#endif
                    return;
                }

                var dr = new DeliveryResult<TKey, TValue>
                {
                    TopicPartitionOffset = deliveryReport.TopicPartitionOffset,
                    Status = deliveryReport.Status,
                    Message = new Message<TKey, TValue>
                    {
                        Key = Key,
                        Value = Value,
                        Timestamp = deliveryReport.Message.Timestamp,
                        Headers = deliveryReport.Message.Headers
                    }
                };
                // topic is cached in this object, not set in the deliveryReport to avoid the 
                // cost of marshalling it.
                dr.Topic = Topic;

#if NET45
                if (deliveryReport.Error.IsError)
                {
                    System.Threading.Tasks.Task.Run(() => SetException(new ProduceException<TKey, TValue>(deliveryReport.Error, dr)));
                }
                else
                {
                    System.Threading.Tasks.Task.Run(() => TrySetResult(dr));
                }
#else
                if (deliveryReport.Error.IsError)
                {
                    TrySetException(new ProduceException<TKey, TValue>(deliveryReport.Error, dr));
                }
                else
                {
                    TrySetResult(dr);
                }
#endif
            }
        }

        private class TypedDeliveryHandlerShim_Action : IDeliveryHandler
        {
            public TypedDeliveryHandlerShim_Action(string topic, TKey key, TValue val, Action<DeliveryReport<TKey, TValue>> handler)
            {
                Topic = topic;
                Key = key;
                Value = val;
                Handler = handler;
            }

            public string Topic;

            public TKey Key;

            public TValue Value;

            public Action<DeliveryReport<TKey, TValue>> Handler;

            public void HandleDeliveryReport(DeliveryReport<Null, Null> deliveryReport)
            {
                if (deliveryReport == null)
                {
                    return;
                }

                var dr = new DeliveryReport<TKey, TValue>
                {
                    TopicPartitionOffsetError = deliveryReport.TopicPartitionOffsetError,
                    Status = deliveryReport.Status,
                    Message = new Message<TKey, TValue> 
                    {
                        Key = Key,
                        Value = Value,
                        Timestamp = deliveryReport.Message == null 
                            ? new Timestamp(0, TimestampType.NotAvailable) 
                            : deliveryReport.Message.Timestamp,
                        Headers = deliveryReport.Message?.Headers
                    }
                };
                // topic is cached in this object, not set in the deliveryReport to avoid the 
                // cost of marshalling it.
                dr.Topic = Topic;

                if (Handler != null)
                {
                    Handler(dr);
                }
            }
        }

        /// <inheritdoc/>
        public void InitTransactions(TimeSpan timeout)
            => this.internalProducer.InitTransactions(timeout);

        /// <inheritdoc/>
        public void BeginTransaction()
            => this.internalProducer.BeginTransaction();

        /// <inheritdoc/>
        public void CommitTransaction(TimeSpan timeout)
            => this.internalProducer.CommitTransaction(timeout);
        
        /// <inheritdoc/>
        public void CommitTransaction()
            => this.internalProducer.CommitTransaction();

        /// <inheritdoc/>
        public void AbortTransaction(TimeSpan timeout)
            => this.internalProducer.AbortTransaction(timeout);

        /// <inheritdoc/>
        public void AbortTransaction()
            => this.internalProducer.AbortTransaction();

        /// <inheritdoc/>
        public void SendOffsetsToTransaction(IEnumerable<TopicPartitionOffset> offsets, IConsumerGroupMetadata groupMetadata, TimeSpan timeout)
            => this.internalProducer.SendOffsetsToTransaction(offsets, groupMetadata, timeout);
    }
}
