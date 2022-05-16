﻿using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Elders.RedLock
{
    public class RedisLockManager : IRedisLockManager
    {
        private ILogger<RedisLockManager> log;

        private RedLockOptions options;

        private bool isDisposed = false;

        private ConnectionMultiplexer connection;

        public RedisLockManager(IOptionsMonitor<RedLockOptions> options, ILogger<RedisLockManager> logger)
        {
            this.options = options.CurrentValue;
            log = logger;
            var configurationOptions = ConfigurationOptions.Parse(options.CurrentValue.ConnectionString);
            connection = ConnectionMultiplexer.Connect(configurationOptions);
        }

        public Task<bool> LockAsync(string resource, TimeSpan ttl)
        {
            return RetryAsync(async () => await AcquireLock(resource, ttl), options.LockRetryCount, options.LockRetryDelay);
        }

        public Task UnlockAsync(string resource)
        {
            return UnlockInstance(resource);
        }

        public async Task<bool> IsLockedAsync(string resource)
        {
            if (connection.IsConnected == false)
            {
                log.LogWarning($"Unreachable endpoint '{connection.ClientName}'.");
                return false;
            }

            try
            {
                return await connection.GetDatabase().KeyExistsAsync(resource, CommandFlags.DemandMaster).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, $"Unreachable endpoint '{connection.ClientName}'.");
                return false;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (isDisposed) return;

            if (disposing)
            {
                if (connection != null)
                {
                    connection.Dispose();
                }

                isDisposed = true;
            }
        }

        private async Task<bool> AcquireLock(string resource, TimeSpan ttl)
        {
            var startTime = DateTime.Now;

            if (await LockInstance(resource, ttl).ConfigureAwait(false) == false)
            {
                return false;
            }

            var drift = Convert.ToInt32((ttl.TotalMilliseconds * options.ClockDriveFactor) + 2);
            var validityTime = ttl - (DateTime.Now - startTime) - new TimeSpan(0, 0, 0, 0, drift);

            if (validityTime.TotalMilliseconds > 0)
            {
                return true;
            }

            await UnlockInstance(resource).ConfigureAwait(false);

            return false;
        }

        private async Task<bool> LockInstance(string resource, TimeSpan ttl)
        {
            if (connection.IsConnected == false)
            {
                var message = $"Unreachable endpoint '{connection.ClientName}'. Unable to acquire lock on this node.";
                log.LogWarning(message);

                return false;
            }

            try
            {
                return await connection.GetDatabase().StringSetAsync(resource, Guid.NewGuid().ToByteArray(), ttl, When.NotExists, CommandFlags.DemandMaster).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var message = $"Unreachable endpoint '{connection.ClientName}'. Unable to acquire lock on this node.";
                log.LogWarning(ex, message);

                return false;
            }
        }

        private async Task UnlockInstance(string resource)
        {
            if (connection.IsConnected == false)
            {
                log.LogWarning($"Unreachable endpoint '{connection.ClientName}'. Unable to unlock resource '{resource}'.");

                return;
            }

            RedisKey[] key = { resource };

            try
            {
                await connection.GetDatabase().KeyDeleteAsync(key, CommandFlags.DemandMaster).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, $"Unreachable endpoint '{connection.ClientName}'. Unable to unlock resource '{resource}'.");
            }
        }

        private async Task<bool> RetryAsync(Func<Task<bool>> action, int retryCount, TimeSpan retryDelay)
        {
            var currentRetry = 0;
            var result = false;

            while (currentRetry++ < retryCount)
            {
                result = await action().ConfigureAwait(false);
                if (result) break;

                await Task.Delay(retryDelay).ConfigureAwait(false);
            }

            return result;
        }
    }
}
