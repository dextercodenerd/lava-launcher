using System;
using System.Net.Http;
using System.Threading.Tasks;
using GenericLauncher.Misc;
using Microsoft.Extensions.Http;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Polly.Extensions.Http;

namespace GenericLauncher.Http;

public static class HttpRetry
{
    // No need to inject all the parameters everywhere...
    private static readonly string UserAgent = Product.Name + "/" + Product.Version;

    public static HttpClient CreateHttpClient(int retries) => CreateHttpClient(retries, TimeSpan.FromSeconds(2));

    public static HttpClient CreateHttpClient(int retries, TimeSpan initialDelay) =>
        CreateHttpClient(retries, initialDelay, TimeSpan.FromSeconds(15));

    public static HttpClient CreateHttpClient(int retries, TimeSpan initialDelay, TimeSpan timeout)
    {
        var delay =
            Backoff.DecorrelatedJitterBackoffV2(initialDelay, retries);

        var retryPolicy = HttpPolicyExtensions
            .HandleTransientHttpError()
            .Or<TaskCanceledException>() // .NET Core and .NET 5 and later only: The request failed due to timeout.
            .WaitAndRetryAsync(delay);

        return new HttpClient(new PolicyHttpMessageHandler(retryPolicy)
        {
            InnerHandler = new HttpClientHandler()
        })
        {
            DefaultRequestHeaders = { { "User-Agent", UserAgent } },
            Timeout = timeout
        };
    }
}
