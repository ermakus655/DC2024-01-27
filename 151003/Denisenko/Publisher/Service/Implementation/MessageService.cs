﻿using Confluent.Kafka;
using Microsoft.Extensions.Caching.Distributed;
using Newtonsoft.Json;
using Publisher.Entity.DTO.RequestTO;
using Publisher.Kafka.Model;
using Publisher.Service.Interface;

namespace Publisher.Service.Implementation
{
    public class MessageService(IProducer<string, string> producer, IDistributedCache cache) : IMessageService
    {
        private static readonly IConsumer<string, string> consumer;
        private const string SRC_TOPIC = "OutTopic";
        private const string DST_TOPIC = "InTopic";

        static MessageService()
        {
            var consumerConfig = new ConsumerConfig
            {
                BootstrapServers = "kafka:9092",
                GroupId = "publisher",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = true,
                EnableAutoOffsetStore = true
            };

            consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
            consumer.Subscribe(SRC_TOPIC);
        }

        public async Task<MessageKafkaResponse?> GetByID(int id)
        {
            var cacheResponse = await cache.GetStringAsync(GetRedisId(id));
            if (cacheResponse is null)
            {
                var json = await GetByIdFromBroker(id);
                await cache.SetStringAsync(GetRedisId(id), json);

                return JsonConvert.DeserializeObject<MessageKafkaResponse>(json);
            }

            return JsonConvert.DeserializeObject<MessageKafkaResponse>(cacheResponse);
        }

        private async Task<string> GetByIdFromBroker(int id)
        {
            var key = Guid.NewGuid().ToString();
            var value = new MessageKafkaRequest(new MessageRequestTO(id, default, string.Empty), HttpMethod.Get);
            await producer.ProduceAsync(DST_TOPIC,
                new Message<string, string> { Key = key, Value = JsonConvert.SerializeObject(value) });
            producer.Flush(TimeSpan.FromSeconds(1));

            return GetBrokerMessage(key);
        }

        public async Task<IList<MessageKafkaResponse>> GetAll()
        {
            var key = Guid.NewGuid().ToString();
            var value = new MessageKafkaRequest(new MessageRequestTO(default, default, string.Empty), HttpMethod.Get);
            await producer.ProduceAsync(DST_TOPIC,
                new Message<string, string> { Key = key, Value = JsonConvert.SerializeObject(value) });
            producer.Flush(TimeSpan.FromSeconds(1));

            var json = GetBrokerMessage(key);
            return JsonConvert.DeserializeObject<IList<MessageKafkaResponse>>(json) ?? [];
        }

        public async Task<MessageKafkaResponse?> Add(MessageRequestTO request)
        {
            var key = Guid.NewGuid().ToString();
            var value = new MessageKafkaRequest(request, HttpMethod.Post);
            await producer.ProduceAsync(DST_TOPIC,
                new Message<string, string> { Key = key, Value = JsonConvert.SerializeObject(value) });

            var json = GetBrokerMessage(key);
            var res = JsonConvert.DeserializeObject<MessageKafkaResponse>(json);
            if (res is not null)
            {
                await cache.SetStringAsync(GetRedisId(res.ResponseTO.Id), json);
            }

            return res;
        }

        public async Task<MessageKafkaResponse?> Update(MessageRequestTO request)
        {
            var key = Guid.NewGuid().ToString();
            var value = new MessageKafkaRequest(request, HttpMethod.Put);
            await producer.ProduceAsync(DST_TOPIC,
                new Message<string, string> { Key = key, Value = JsonConvert.SerializeObject(value) });

            var json = GetBrokerMessage(key);
            var res = JsonConvert.DeserializeObject<MessageKafkaResponse>(json);
            if (res is not null)
            {
                await cache.RemoveAsync(GetRedisId(res.ResponseTO.Id));
                await cache.SetStringAsync(GetRedisId(res.ResponseTO.Id), json);
            }

            return res;
        }

        public async Task<bool> Remove(int id)
        {
            var key = Guid.NewGuid().ToString();
            var value = new MessageKafkaRequest(new MessageRequestTO(id, default, string.Empty), HttpMethod.Delete);
            await producer.ProduceAsync(DST_TOPIC,
                new Message<string, string> { Key = key, Value = JsonConvert.SerializeObject(value) });

            var json = GetBrokerMessage(key);
            var response = JsonConvert.DeserializeObject<MessageKafkaResponse>(json);
            await cache.RemoveAsync(GetRedisId(id));

            return response is not null && response.State == State.Approve;
        }

        private static string GetBrokerMessage(string key)
        {
            ConsumeResult<string, string> res;
            do
            {
                res = consumer.Consume();
            } while (!res.Message.Key.Equals(key));

            return res.Message.Value;
        }

        private static string GetRedisId(int id) => $"Message:{id}";
    }
}
