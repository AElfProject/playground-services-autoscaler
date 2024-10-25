using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;
using System.Threading.Tasks;

namespace Common
{
    public interface IRedisCacheService
    {
        Task<T> GetCachedDataAsync<T>(string cacheKey);
        Task SetCacheDataAsync<T>(string cacheKey, T data, DistributedCacheEntryOptions options);
    }

    public class RedisCacheService : IRedisCacheService
    {
        private readonly IDistributedCache _cache;

        public RedisCacheService(IDistributedCache cache)
        {
            _cache = cache;
        }

        public async Task<T> GetCachedDataAsync<T>(string cacheKey)
        {
            var cachedData = await _cache.GetStringAsync(cacheKey);
            if (string.IsNullOrEmpty(cachedData))
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(cachedData);
        }

        public async Task SetCacheDataAsync<T>(string cacheKey, T data, DistributedCacheEntryOptions options)
        {
            var serializedData = JsonSerializer.Serialize(data);
            await _cache.SetStringAsync(cacheKey, serializedData, options);
        }
    }
}